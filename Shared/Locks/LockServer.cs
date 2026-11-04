using System;
using System.Collections.Generic;

namespace CMS21Together.Shared.Locks;

/// <summary>
/// Host side of the lock protocol: answers requests, broadcasts every holder change, expires leases and fences state
/// writes. Transport-agnostic (the game and the bot harness pass their own send functions); main thread only.
/// </summary>
public sealed class LockServer
{
	public const int ExpireEveryMs = 250;
	public const int ResyncEveryMs = 1000;
	public const int MaxResyncEntries = 1024;

	private readonly Func<long> clock;
	private readonly Action<int, Packet> send;
	private readonly Action<Packet> broadcast;
	private readonly Dictionary<(int slot, string key), long> lastResync = new();
	private long nextExpire;

	public LockServer(Func<long> clock, Action<int, Packet> send, Action<Packet> broadcast)
	{
		this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
		this.send = send ?? throw new ArgumentNullException(nameof(send));
		this.broadcast = broadcast ?? throw new ArgumentNullException(nameof(broadcast));
		Manager = new ResourceLockManager(clock);
		Manager.Changed += OnChanged;
	}

	public ResourceLockManager Manager { get; }

	/// <summary>Writes rejected by fencing since the session started (diagnostics and tests).</summary>
	public int RejectedWrites { get; private set; }

	public void HandleRequest(int slot, Packet packet)
	{
		var (token, keys) = LockWire.ReadRequest(packet);
		var outcome = Manager.Acquire(slot, token, keys);
		if (outcome.Status == LockStatus.Invalid)
			Log.Debug($"[Locks] Invalid request {token} from #{slot}: {string.Join(", ", keys)}");
		Reply(slot, token, outcome.Status, outcome.Holder);
	}

	public void HandleRenew(int slot, Packet packet)
	{
		var token = LockWire.ReadToken(packet);
		if (!Manager.Renew(slot, token)) Reply(slot, token, LockStatus.Lost, 0);
	}

	public void HandleRelease(int slot, Packet packet) => Manager.Release(slot, LockWire.ReadToken(packet));

	/// <summary>Current holders, for a player that just joined.</summary>
	public void SendSnapshot(int slot)
	{
		var holders = Manager.Snapshot();
		if (holders.Count > LockWire.MaxSnapshot) holders.RemoveRange(LockWire.MaxSnapshot, holders.Count - LockWire.MaxSnapshot);
		using var packet = new Packet((int)PacketTypes.lockSnapshot);
		LockWire.WriteSnapshot(packet, holders);
		send(slot, packet);
	}

	public void Tick()
	{
		var now = clock();
		if (now < nextExpire) return;
		nextExpire = now + ExpireEveryMs;
		var expired = Manager.Expire();
		if (expired > 0) Log.Debug($"[Locks] {expired} lease(s) expired.");
	}

	/// <summary>The slot is gone for good: free its locks and let a new connection in the slot start its tokens over.</summary>
	public void PlayerLeft(int slot)
	{
		Manager.ReleaseAll(slot, true);
		ForgetResyncs(slot);
	}

	/// <summary>A player loading another scene cannot be working on anything here any more.</summary>
	public void PlayerChangedScene(int slot) => Manager.ReleaseAll(slot, false);

	/// <summary>Fencing for a state write from <paramref name="slot"/>; false means: do not store or relay it.</summary>
	public bool AcceptWrite(int slot, string key, int token)
	{
		var verdict = Manager.CheckWrite(slot, key, token, out var holder);
		if (verdict == WriteVerdict.Accept) return true;
		RejectedWrites++;
		Log.Debug($"[Locks] Rejected write of {key} from #{slot} (token {token}): {verdict}{(holder != 0 ? $", held by #{holder}" : "")}.");
		return false;
	}

	/// <summary>Rate limit for sending the stored state back to a player whose write was rejected (one per key per second).</summary>
	public bool ShouldResync(int slot, string key)
	{
		var now = clock();
		var id = (slot, key);
		if (lastResync.TryGetValue(id, out var last) && now - last < ResyncEveryMs) return false;
		if (lastResync.Count >= MaxResyncEntries) Prune(now);
		if (lastResync.Count >= MaxResyncEntries) return false;
		lastResync[id] = now;
		return true;
	}

	public void Clear()
	{
		Manager.Clear();
		lastResync.Clear();
	}

	private void Reply(int slot, int token, LockStatus status, int holder)
	{
		using var packet = new Packet((int)PacketTypes.lockReply);
		LockWire.WriteReply(packet, token, status, holder);
		send(slot, packet);
	}

	private void OnChanged(string key, int holder)
	{
		using var packet = new Packet((int)PacketTypes.lockState);
		LockWire.WriteState(packet, key, holder);
		broadcast(packet);
	}

	private void ForgetResyncs(int slot)
	{
		List<(int, string)> stale = null;
		foreach (var id in lastResync.Keys)
			if (id.slot == slot) (stale ??= new List<(int, string)>()).Add(id);
		if (stale != null) foreach (var id in stale) lastResync.Remove(id);
	}

	private void Prune(long now)
	{
		List<(int, string)> stale = null;
		foreach (var pair in lastResync)
			if (now - pair.Value >= ResyncEveryMs) (stale ??= new List<(int, string)>()).Add(pair.Key);
		if (stale != null) foreach (var id in stale) lastResync.Remove(id);
	}
}
