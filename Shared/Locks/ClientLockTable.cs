using System;
using System.Collections.Generic;
using System.Linq;

namespace CMS21Together.Shared.Locks;

public enum LockCommandKind : byte
{
	Renew,
	Release
}

public readonly struct LockCommand
{
	public readonly LockCommandKind Kind;
	public readonly int Token;

	public LockCommand(LockCommandKind kind, int token)
	{
		Kind = kind;
		Token = token;
	}
}

/// <summary>
/// Client half of the lock protocol: mirrors who holds what (for "in use by" checks), tracks this player's own requests,
/// stamps writes with their token and decides when to renew or release. Main thread only; the clock is injected.
/// </summary>
public sealed class ClientLockTable
{
	/// <summary>Renewal cadence while the player keeps touching the resource (server lease is 10 s).</summary>
	public const int RenewMs = 3000;

	/// <summary>A lock nobody touched for this long is released.</summary>
	public const int IdleMs = 6000;

	/// <summary>How long a finished, denied or lost request still stamps late writes with its token.</summary>
	public const int RetainMs = 6000;

	public enum State
	{
		Pending,
		Held,
		Denied,
		Lost,
		Released
	}

	private sealed class Mine
	{
		public int Token;
		public string[] Keys;
		public State State;
		public long LastTouch;
		public long LastRenew;
		public long Since;
		public int DeniedBy;
	}

	private readonly Func<long> clock;
	private readonly Dictionary<string, int> holders = new(StringComparer.Ordinal);
	private readonly Dictionary<int, Mine> mine = new();
	private readonly Dictionary<string, Mine> byKey = new(StringComparer.Ordinal);
	private readonly List<int> queuedReleases = new();
	private int nextToken;

	public ClientLockTable(int self, Func<long> clock)
	{
		Self = self;
		this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
	}

	public int Self { get; set; }

	/// <summary>Number of own requests still tracked (for tests and diagnostics).</summary>
	public int OwnCount => mine.Count;

	/// <summary>True when the server reports another player holding anything (lets the game gates skip resolving keys).</summary>
	public bool AnyForeign
	{
		get
		{
			foreach (var holder in holders.Values)
				if (holder != Self) return true;
			return false;
		}
	}

	/// <summary>Another player holding this key, its parent or one of its children, according to the server's broadcasts; 0 if none.</summary>
	public int BlockedBy(string key)
	{
		if (key == null) return 0;
		if (holders.TryGetValue(key, out var exact) && exact != Self) return exact;
		foreach (var pair in holders)
			if (pair.Value != Self && LockKeys.Conflicts(pair.Key, key))
				return pair.Value;
		return 0;
	}

	/// <summary>Every resource another player (or this one) holds, as the host last reported it (diagnostics).</summary>
	public IEnumerable<KeyValuePair<string, int>> Holders => holders;

	public int HolderOf(string key) => key != null && holders.TryGetValue(key, out var holder) ? holder : 0;

	/// <summary>
	/// Starts using <paramref name="keys"/>. Returns the token of a new request to send, or 0 when this player already
	/// has (or is waiting for) exactly these keys - then only the idle timer is refreshed.
	/// </summary>
	public int Begin(params string[] keys)
	{
		if (keys == null || keys.Length == 0 || keys.Length > ResourceLockManager.MaxKeysPerRequest) return 0;
		var now = clock();
		if (byKey.TryGetValue(keys[0], out var current) && current.State is State.Pending or State.Held &&
		    keys.All(k => byKey.TryGetValue(k, out var m) && ReferenceEquals(m, current)))
		{
			current.LastTouch = now;
			return 0;
		}

		var request = new Mine
		{
			Token = ++nextToken,
			Keys = keys.Distinct(StringComparer.Ordinal).ToArray(),
			State = State.Pending,
			LastTouch = now,
			LastRenew = now,
			Since = now
		};

		// Mirrors the server: taking a component lets go of this player's other components.
		if (request.Keys.Any(LockKeys.IsComponent))
			foreach (var other in mine.Values)
				if (other.State is State.Pending or State.Held && other.Keys.All(LockKeys.IsComponent) &&
				    !other.Keys.Any(k => request.Keys.Contains(k, StringComparer.Ordinal)))
				{
					Finish(other, State.Released, now);
					// The server only lets go of them if the new request is granted; say it explicitly.
					queuedReleases.Add(other.Token);
				}

		mine[request.Token] = request;
		foreach (var key in request.Keys) byKey[key] = request;
		return request.Token;
	}

	/// <summary>
	/// Token to stamp on a write: the request's token, or 0 when there is none or it was denied. A denied write is then
	/// only rejected while someone really holds the resource, never after the holder let go (the client saw their changes).
	/// </summary>
	public int TokenFor(string key)
	{
		if (key == null || !byKey.TryGetValue(key, out var m)) return 0;
		return m.State == State.Denied ? 0 : m.Token;
	}

	public State? StateOf(string key) => key != null && byKey.TryGetValue(key, out var m) ? m.State : null;

	/// <summary>Keeps an active request alive (the player is still working on it).</summary>
	public void Touch(string key)
	{
		if (key != null && byKey.TryGetValue(key, out var m) && m.State is State.Pending or State.Held)
			m.LastTouch = clock();
	}

	/// <summary>Applies the server's answer. Returns the holder to name in a "denied" message, or 0.</summary>
	public int OnReply(int token, LockStatus status, int holder)
	{
		if (!mine.TryGetValue(token, out var m)) return 0;
		var now = clock();
		switch (status)
		{
			case LockStatus.Granted:
				if (m.State == State.Pending)
				{
					m.State = State.Held;
					m.LastRenew = now;
				}
				return 0;
			case LockStatus.Denied:
				if (m.State is State.Pending or State.Held)
				{
					m.DeniedBy = holder;
					Finish(m, State.Denied, now);
					return holder;
				}
				return 0;
			case LockStatus.Lost:
				if (m.State is State.Pending or State.Held) Finish(m, State.Lost, now);
				return 0;
			default:
				if (m.State is State.Pending or State.Held) Finish(m, State.Released, now);
				return 0;
		}
	}

	/// <summary>Server broadcast: <paramref name="holder"/> 0 frees the key.</summary>
	public void OnState(string key, int holder)
	{
		if (key == null) return;
		if (holder == 0) holders.Remove(key);
		else holders[key] = holder;
	}

	/// <summary>Full holder list sent when joining.</summary>
	public void OnSnapshot(IEnumerable<KeyValuePair<string, int>> snapshot)
	{
		holders.Clear();
		foreach (var pair in snapshot)
			if (pair.Key != null && pair.Value != 0)
				holders[pair.Key] = pair.Value;
	}

	/// <summary>Renewals and releases due now, appended to <paramref name="output"/>.</summary>
	public void Pump(List<LockCommand> output)
	{
		foreach (var token in queuedReleases) output.Add(new LockCommand(LockCommandKind.Release, token));
		queuedReleases.Clear();
		if (mine.Count == 0) return;
		var now = clock();
		List<int> forget = null;
		foreach (var m in mine.Values)
			switch (m.State)
			{
				case State.Pending:
				case State.Held:
					if (now - m.LastTouch >= IdleMs)
					{
						output.Add(new LockCommand(LockCommandKind.Release, m.Token));
						Finish(m, State.Released, now);
					}
					else if (m.State == State.Held && now - m.LastRenew >= RenewMs)
					{
						output.Add(new LockCommand(LockCommandKind.Renew, m.Token));
						m.LastRenew = now;
					}
					break;
				default:
					if (now - m.Since >= RetainMs) (forget ??= new List<int>()).Add(m.Token);
					break;
			}

		if (forget == null) return;
		foreach (var token in forget)
		{
			var m = mine[token];
			mine.Remove(token);
			foreach (var key in m.Keys)
				if (byKey.TryGetValue(key, out var current) && ReferenceEquals(current, m))
					byKey.Remove(key);
		}
	}

	/// <summary>Forgets this player's own requests without telling the server (it already released them, e.g. on a scene change).</summary>
	public void DropOwn()
	{
		queuedReleases.Clear();
		mine.Clear();
		byKey.Clear();
	}

	public void Clear()
	{
		DropOwn();
		holders.Clear();
	}

	private static void Finish(Mine m, State state, long now)
	{
		m.State = state;
		m.Since = now;
	}
}
