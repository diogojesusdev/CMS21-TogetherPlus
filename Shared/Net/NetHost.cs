using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;

namespace CMS21Together.Shared.Net;

/// <summary>
/// Server side of the transport. Links are accepted from any thread; everything visible to the game happens in
/// <see cref="Tick"/> on the main thread: handshakes, slot assignment, game frames, heartbeats and exactly one
/// <see cref="PeerDisconnected"/> per connected peer. A slot is only reused after its disconnect handlers ran.
/// </summary>
public sealed class NetHost
{
	private readonly ConcurrentQueue<NetEvent> inbox = new();
	private readonly List<NetConnection> live = new();
	private readonly List<NetConnection> scratch = new();
	private readonly NetConnection[] slots;
	private readonly Func<long> clock;
	private readonly string modVersion;

	public NetHost(int maxPlayers, string modVersion, Func<long> clock = null)
	{
		if (maxPlayers < 1 || maxPlayers > 64) throw new ArgumentOutOfRangeException(nameof(maxPlayers));
		MaxPlayers = maxPlayers;
		this.modVersion = modVersion ?? "";
		if (clock == null)
		{
			var stopwatch = Stopwatch.StartNew();
			clock = () => stopwatch.ElapsedMilliseconds;
		}
		this.clock = clock;
		slots = new NetConnection[maxPlayers + 1];
		HostKey = new byte[NetProtocol.HostKeyLength];
		using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(HostKey);
		IsRunning = true;
	}

	public int MaxPlayers { get; }

	/// <summary>Default per-tick dispatch budget on the host (Phase 7, plan §3.6).</summary>
	public const double DefaultBudgetMs = 4;

	/// <summary>Events always dispatched per tick, whatever the budget, so a busy host still makes progress.</summary>
	public const int MinEventsPerTick = 64;

	/// <summary>
	/// Time a <see cref="Tick"/> may spend dispatching (0 = unlimited). What is left waits for the next tick, in order;
	/// game frames stay smooth while 8 players send at once.
	/// </summary>
	public double BudgetMs { get; set; } = DefaultBudgetMs;

	/// <summary>Real-time clock for the budget, in ticks of <see cref="Stopwatch.Frequency"/> (replaceable in tests).</summary>
	public Func<long> BudgetClock { get; set; } = Stopwatch.GetTimestamp;

	/// <summary>Dispatch time of the last tick, and the longest seen, in ms.</summary>
	public double LastTickMs { get; private set; }

	public double MaxTickMs { get; private set; }

	/// <summary>Ticks that stopped at the budget with events still waiting.</summary>
	public long BudgetHits { get; private set; }

	/// <summary>Events waiting for the main thread.</summary>
	public int Backlog => inbox.Count;

	/// <summary>Clears <see cref="MaxTickMs"/> and <see cref="BudgetHits"/>.</summary>
	public void ResetMetrics()
	{
		MaxTickMs = 0;
		BudgetHits = 0;
	}

	/// <summary>In-memory secret: only the host's own loopback client can claim slot 1.</summary>
	public byte[] HostKey { get; }

	public bool IsRunning { get; private set; }

	public event Action<NetConnection> PeerConnected;
	public event Action<NetConnection, string> PeerDisconnected;
	public event Action<NetConnection, NetFrame> FrameReceived;

	public int PeerCount
	{
		get
		{
			var count = 0;
			for (var i = 1; i < slots.Length; i++)
				if (slots[i] != null)
					count++;
			return count;
		}
	}

	/// <summary>Takes ownership of a new link. Thread-safe.</summary>
	public void Accept(NetLink link)
	{
		if (!IsRunning)
		{
			link.Close("Server is not running.");
			return;
		}
		var connection = new NetConnection(link, clock, inbox.Enqueue);
		inbox.Enqueue(NetEvent.Accepted(connection));
		link.Start();
	}

	/// <summary>Connected peer in <paramref name="slot"/>, or null.</summary>
	public NetConnection GetPeer(int slot) => slot >= 1 && slot < slots.Length ? slots[slot] : null;

	public IEnumerable<NetConnection> Peers
	{
		get
		{
			for (var i = 1; i < slots.Length; i++)
				if (slots[i] != null)
					yield return slots[i];
		}
	}

	public EnqueueResult Send(int slot, byte[] frame, int length, SendPriority priority = SendPriority.Normal, bool unreliable = false, long coalesceKey = 0)
	{
		var peer = GetPeer(slot);
		return peer == null || peer.LocalClose ? EnqueueResult.Dropped : peer.Send(frame, length, priority, unreliable, coalesceKey);
	}

	/// <summary>Sends the reason to the peer, flushes and disconnects it. <see cref="PeerDisconnected"/> follows in a later Tick.</summary>
	public void Kick(int slot, string reason)
	{
		var peer = GetPeer(slot);
		if (peer == null) return;
		Log.Info($"[Net] Kicking {peer}: {reason}");
		peer.Disconnect(reason);
	}

	/// <summary>Disconnects everybody with <paramref name="reason"/> and raises <see cref="PeerDisconnected"/> for each connected peer now.</summary>
	public void Stop(string reason)
	{
		if (!IsRunning) return;
		IsRunning = false;
		scratch.Clear();
		scratch.AddRange(live);
		foreach (var connection in scratch) connection.Disconnect(reason);
		for (var i = 1; i < slots.Length; i++)
			if (slots[i] != null)
				Drop(slots[i], reason);
		live.Clear();
		// Links accepted but not handled yet are closed too (Phase 8 assessment: their sockets stayed open).
		while (inbox.TryDequeue(out var e))
			if (e.Kind == NetEventKind.Accepted)
				e.Connection.Link.Close(reason);
	}

	private NetProtocol.StallClock stall;

	public void Tick()
	{
		scratch.Clear();
		scratch.AddRange(live);
		foreach (var connection in scratch)
		{
			try
			{
				connection.Link.Poll();
			}
			catch (Exception ex)
			{
				Log.Error($"[Net] Poll of {connection} failed", ex);
				connection.Link.Close("Connection error: " + ex.Message);
			}
		}

		var now = clock();
		var started = BudgetClock();
		var limit = BudgetMs > 0 ? (long)(BudgetMs * Stopwatch.Frequency / 1000.0) : long.MaxValue;
		var dispatched = 0;
		while (inbox.TryDequeue(out var e))
		{
			Dispatch(e, now);
			if (++dispatched < MinEventsPerTick || BudgetClock() - started < limit) continue;
			if (!inbox.IsEmpty) BudgetHits++;
			break;
		}
		LastTickMs = (BudgetClock() - started) * 1000.0 / Stopwatch.Frequency;
		if (LastTickMs > MaxTickMs) MaxTickMs = LastTickMs;
		Heartbeat(now);
	}

	private void Dispatch(NetEvent e, long now)
	{
		var connection = e.Connection;
		switch (e.Kind)
		{
			case NetEventKind.Accepted:
				if (!IsRunning)
				{
					connection.Link.Close("Server is not running.");
					return;
				}
				connection.State = NetConnectionState.Handshaking;
				connection.HandshakeDeadline = now + NetProtocol.HandshakeTimeoutMs;
				connection.NextPingMs = now;
				live.Add(connection);
				Log.Debug($"[Net] Incoming connection from {connection.Link.Description}.");
				return;

			case NetEventKind.Frame:
				if (connection.State == NetConnectionState.Closed || connection.LocalClose) return;
				if (connection.State == NetConnectionState.Handshaking)
				{
					if (e.Frame.Type == NetFrameTypes.Hello) HandleHello(connection, e.Frame);
					else connection.Disconnect("Handshake expected.", NetFrameTypes.Reject);
					return;
				}
				if (NetFrameTypes.IsTransport(e.Frame.Type))
				{
					connection.Disconnect("Unexpected control frame.");
					return;
				}
				try
				{
					FrameReceived?.Invoke(connection, e.Frame);
				}
				catch (Exception ex)
				{
					Log.Error($"[Net] Frame handler for {connection} failed", ex);
				}
				return;

			case NetEventKind.Closed:
				live.Remove(connection);
				if (connection.State == NetConnectionState.Connected && slots[connection.SlotId] == connection)
					Drop(connection, e.Reason);
				else
					connection.State = NetConnectionState.Closed;
				return;
		}
	}

	private void Drop(NetConnection connection, string reason)
	{
		connection.State = NetConnectionState.Closed;
		Log.Info($"[Net] Player {connection} disconnected: {reason}");
		try
		{
			PeerDisconnected?.Invoke(connection, reason);
		}
		catch (Exception ex)
		{
			Log.Error($"[Net] Disconnect handler for {connection} failed", ex);
		}
		finally
		{
			slots[connection.SlotId] = null;
		}
	}

	private void HandleHello(NetConnection connection, NetFrame frame)
	{
		NetProtocol.Hello hello;
		try
		{
			hello = NetProtocol.DecodeHello(frame);
		}
		catch (NetFormatException ex)
		{
			Log.Warn($"[Net] Bad handshake from {connection}: {ex.Message}");
			connection.LocalClose = true;
			connection.Link.Close("Bad handshake.");
			return;
		}

		if (hello.Protocol != NetProtocol.Version)
		{
			Reject(connection, $"Incompatible Together+ version (host protocol {NetProtocol.Version}, yours {hello.Protocol}). Install the same mod version as the host.");
			return;
		}
		if (!string.Equals(hello.ModVersion, modVersion, StringComparison.Ordinal))
		{
			Reject(connection, $"Mod version mismatch: host has {modVersion}, you have {hello.ModVersion}.");
			return;
		}

		int slot;
		if (NetProtocol.KeyEquals(hello.HostKey, HostKey))
		{
			if (slots[1] != null)
			{
				Reject(connection, "The host is already connected.");
				return;
			}
			slot = 1;
		}
		else
		{
			slot = 0;
			for (var i = 2; i < slots.Length && slot == 0; i++)
				if (slots[i] == null)
					slot = i;
			if (slot == 0)
			{
				Reject(connection, $"Server is full ({MaxPlayers} players).");
				return;
			}
		}

		connection.SlotId = slot;
		connection.State = NetConnectionState.Connected;
		slots[slot] = connection;
		connection.SendControl(NetFrameTypes.Welcome, NetProtocol.EncodeWelcome(slot, MaxPlayers));
		Log.Info($"[Net] Player {connection} joined.");
		try
		{
			PeerConnected?.Invoke(connection);
		}
		catch (Exception ex)
		{
			Log.Error($"[Net] Connect handler for {connection} failed", ex);
		}
	}

	private static void Reject(NetConnection connection, string reason)
	{
		Log.Info($"[Net] Rejecting {connection}: {reason}");
		connection.Disconnect(reason, NetFrameTypes.Reject);
	}

	private void Heartbeat(long now)
	{
		stall.Tick(now);
		scratch.Clear();
		scratch.AddRange(live);
		foreach (var connection in scratch)
		{
			if (connection.Link.IsClosing) continue;
			if (connection.State == NetConnectionState.Handshaking && now > connection.HandshakeDeadline)
			{
				connection.LocalClose = true;
				connection.Link.Close("Handshake timed out.");
				continue;
			}
			if (!connection.Link.NeedsHeartbeat) continue;
			if (now >= connection.NextPingMs)
			{
				connection.SendPing(now);
				connection.NextPingMs = now + NetProtocol.PingIntervalMs;
			}
			if (stall.TimedOut(now, connection.LastReceiveMs))
			{
				connection.LocalClose = true;
				connection.Link.Close("Connection timed out.");
			}
		}
	}
}
