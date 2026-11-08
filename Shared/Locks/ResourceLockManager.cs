using System;
using System.Collections.Generic;
using System.Linq;

namespace CMS21Together.Shared.Locks;

public enum LockStatus : byte
{
	Granted = 0,
	/// <summary>Another player holds the key, its parent or one of its children.</summary>
	Denied = 1,
	/// <summary>A renewal arrived after the lease had already expired.</summary>
	Lost = 2,
	/// <summary>Malformed request: bad key, replayed token, too many keys or grants.</summary>
	Invalid = 3
}

public enum WriteVerdict
{
	Accept,
	/// <summary>Someone else holds the resource right now.</summary>
	RejectHeld,
	/// <summary>The writer's lock was denied, or someone else wrote since the writer's lease ended.</summary>
	RejectStale
}

public readonly struct LockOutcome
{
	public readonly LockStatus Status;
	public readonly int Holder;
	public readonly long Epoch;

	public LockOutcome(LockStatus status, int holder, long epoch)
	{
		Status = status;
		Holder = holder;
		Epoch = epoch;
	}

	public bool Granted => Status == LockStatus.Granted;
}

/// <summary>
/// Server-side exclusive leases with fencing (plan section 3.2). Runs on the server main thread only.
/// A request names the keys it needs plus a client-chosen token that must increase per owner; writes carry that token
/// and <see cref="CheckWrite"/> decides whether they may be applied.
/// </summary>
public sealed class ResourceLockManager
{
	public const int LeaseMs = 10_000;
	public const int MaxKeysPerRequest = 8;
	public const int MaxGrantsPerOwner = 32;
	public const int MaxWriteRecords = 8192;

	private sealed class Grant
	{
		public string Key;
		public int Owner;
		public int Token;
		public long Epoch;
		public long Expires;
	}

	private readonly Func<long> clock;
	private readonly Dictionary<string, Grant> grants = new(StringComparer.Ordinal);
	private readonly Dictionary<int, int> lastToken = new();
	private readonly Dictionary<string, (int owner, int token)> lastWriter = new(StringComparer.Ordinal);
	private readonly Queue<string> writerOrder = new();
	private long epoch;

	public ResourceLockManager(Func<long> clock)
	{
		this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
	}

	/// <summary>Raised for every key whose holder changes; holder 0 means free.</summary>
	public event Action<string, int> Changed;

	public int Count => grants.Count;

	/// <summary>Grants every key or none. Taking a component releases the owner's other components (one at a time).</summary>
	public LockOutcome Acquire(int owner, int token, IList<string> keys)
	{
		if (owner <= 0 || token <= 0 || keys == null || keys.Count == 0 || keys.Count > MaxKeysPerRequest)
			return new LockOutcome(LockStatus.Invalid, 0, 0);
		if (lastToken.TryGetValue(owner, out var previous) && token <= previous)
			return new LockOutcome(LockStatus.Invalid, 0, 0);
		foreach (var key in keys)
			if (!LockKeys.IsValid(key))
				return new LockOutcome(LockStatus.Invalid, 0, 0);
		lastToken[owner] = token;

		var wanted = keys.Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal).ToList();
		foreach (var key in wanted)
		{
			var holder = Conflict(owner, key);
			if (holder != 0) return new LockOutcome(LockStatus.Denied, holder, 0);
		}

		var takesComponent = wanted.Any(LockKeys.IsComponent);
		var released = takesComponent
			? grants.Values.Where(g => g.Owner == owner && LockKeys.IsComponent(g.Key) && !wanted.Contains(g.Key, StringComparer.Ordinal)).ToList()
			: new List<Grant>();
		var kept = grants.Values.Count(g => g.Owner == owner) - released.Count;
		var added = wanted.Count(k => !grants.ContainsKey(k));
		if (kept + added > MaxGrantsPerOwner) return new LockOutcome(LockStatus.Invalid, 0, 0);

		foreach (var grant in released) Remove(grant);

		var now = clock();
		epoch++;
		foreach (var key in wanted)
		{
			// The grantee is now the rightful writer of the key: a write of theirs that lands after their own lease
			// ended is late, not stale, even when another player worked on that part earlier (see CheckWrite).
			Record(key, owner, token);
			if (grants.TryGetValue(key, out var mine))
			{
				mine.Token = token;
				mine.Epoch = epoch;
				mine.Expires = now + LeaseMs;
				continue;
			}
			grants[key] = new Grant { Key = key, Owner = owner, Token = token, Epoch = epoch, Expires = now + LeaseMs };
			Changed?.Invoke(key, owner);
		}
		return new LockOutcome(LockStatus.Granted, owner, epoch);
	}

	/// <summary>Extends every grant of this token. False when the lease already expired (the client must treat it as lost).</summary>
	public bool Renew(int owner, int token)
	{
		var expires = clock() + LeaseMs;
		var found = false;
		foreach (var grant in grants.Values)
			if (grant.Owner == owner && grant.Token == token)
			{
				grant.Expires = expires;
				found = true;
			}
		return found;
	}

	public void Release(int owner, int token)
	{
		foreach (var grant in grants.Values.Where(g => g.Owner == owner && g.Token == token).ToList())
			Remove(grant);
	}

	/// <summary>Frees everything the owner holds (disconnect, scene change). <paramref name="forgetTokens"/> lets a new connection in the slot start over.</summary>
	public void ReleaseAll(int owner, bool forgetTokens)
	{
		foreach (var grant in grants.Values.Where(g => g.Owner == owner).ToList())
			Remove(grant);
		if (forgetTokens) lastToken.Remove(owner);
	}

	/// <summary>Drops expired leases; returns how many were dropped.</summary>
	public int Expire()
	{
		if (grants.Count == 0) return 0;
		var now = clock();
		var expired = grants.Values.Where(g => g.Expires <= now).ToList();
		foreach (var grant in expired) Remove(grant);
		return expired.Count;
	}

	/// <summary>The other player blocking <paramref name="owner"/> from <paramref name="key"/> (same key, parent or child), or 0.</summary>
	public int Conflict(int owner, string key)
	{
		if (grants.TryGetValue(key, out var exact) && exact.Owner != owner) return exact.Owner;
		var parent = LockKeys.Parent(key);
		if (parent != null)
			return grants.TryGetValue(parent, out var above) && above.Owner != owner ? above.Owner : 0;
		foreach (var grant in grants.Values)
			if (grant.Owner != owner && string.Equals(LockKeys.Parent(grant.Key), key, StringComparison.Ordinal))
				return grant.Owner;
		return 0;
	}

	public int HolderOf(string key) => grants.TryGetValue(key, out var grant) ? grant.Owner : 0;

	/// <summary>
	/// Fencing for a state write. A write is rejected while someone else holds the resource, when the writer's own request was
	/// denied (token &lt; 0), or when its lease is gone and another player has held or written the key since. Accepted writes
	/// renew the writer's lease. A grant records its owner as the key's writer, so a write that lands just after the writer's
	/// own lease ended is still theirs (a mount sent when the last bolt goes in, seconds after the claim).
	/// </summary>
	public WriteVerdict CheckWrite(int owner, string key, int token, out int holder)
	{
		holder = 0;
		if (!LockKeys.IsValid(key)) return WriteVerdict.RejectStale;
		holder = Conflict(owner, key);
		if (holder != 0) return WriteVerdict.RejectHeld;
		if (token < 0) return WriteVerdict.RejectStale;

		var now = clock();
		if (grants.TryGetValue(key, out var mine))
		{
			mine.Expires = now + LeaseMs;
			Record(key, owner, token);
			return WriteVerdict.Accept;
		}
		var parent = LockKeys.Parent(key);
		if (parent != null && grants.ContainsKey(parent))
		{
			Record(key, owner, token);
			return WriteVerdict.Accept;
		}
		if (token != 0 && lastWriter.TryGetValue(key, out var last) && last.owner != owner)
			return WriteVerdict.RejectStale;
		Record(key, owner, token);
		return WriteVerdict.Accept;
	}

	public WriteVerdict CheckWrite(int owner, string key, int token) => CheckWrite(owner, key, token, out _);

	public List<KeyValuePair<string, int>> Snapshot() =>
		grants.Values.Select(g => new KeyValuePair<string, int>(g.Key, g.Owner)).ToList();

	public void Clear()
	{
		foreach (var grant in grants.Values.ToList()) Remove(grant);
		lastToken.Clear();
		lastWriter.Clear();
		writerOrder.Clear();
	}

	private void Remove(Grant grant)
	{
		grants.Remove(grant.Key);
		Changed?.Invoke(grant.Key, 0);
	}

	private void Record(string key, int owner, int token)
	{
		if (!lastWriter.ContainsKey(key))
		{
			writerOrder.Enqueue(key);
			while (writerOrder.Count > MaxWriteRecords)
				lastWriter.Remove(writerOrder.Dequeue());
		}
		lastWriter[key] = (owner, token);
	}
}
