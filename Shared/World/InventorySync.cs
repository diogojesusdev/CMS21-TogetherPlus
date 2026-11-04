using System;
using System.Collections.Generic;
using System.Linq;
using CMS21Together.Shared.Net;

namespace CMS21Together.Shared.World;

public enum InventoryOp
{
	Add,
	Remove,

	/// <summary>The item changed in place (paint, licence plate, quality...; Phase 6b): same UID, new data.</summary>
	Update
}

/// <summary>The two inventories of the shared garage: single items and item groups (wheels, engines, ...).</summary>
public enum InventoryKind
{
	Items,
	Groups
}

public enum InventoryResult
{
	Added,
	Removed,
	Updated,

	/// <summary>Already there (add) or already gone (remove): nothing changed.</summary>
	NoChange,

	/// <summary>A replayed or out-of-order operation number from that player: ignored.</summary>
	Replayed,

	/// <summary>A purchased item whose debit the host did not charge (Phase 6): never added.</summary>
	Refused
}

/// <summary>Order-independent fingerprint of a set of UIDs: count plus XOR of a mixed hash per UID.</summary>
public struct UidSetHash
{
	private ulong xor;

	public int Count { get; private set; }

	public void Toggle(long uid, bool added)
	{
		xor ^= Mix((ulong)uid);
		Count += added ? 1 : -1;
	}

	public override string ToString() => $"{Count}:{xor:x16}";

	public static string Of(IEnumerable<long> uids)
	{
		var hash = new UidSetHash();
		foreach (var uid in uids) hash.Toggle(uid, true);
		return hash.ToString();
	}

	private static ulong Mix(ulong z)
	{
		z += 0x9E3779B97F4A7C15UL;
		z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
		z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
		return z ^ (z >> 31);
	}
}

/// <summary>
/// The host's copy of one shared inventory (plan §3.4, audit A15/A23). Players send numbered operations; each is
/// applied at most once, in order, keyed by item UID, and every change gets a version number. A player that
/// applies every versioned change in order holds the same set as the host; one that misses any asks for a snapshot.
/// </summary>
public sealed class InventoryAuthority<T> where T : class
{
	public const int HashHistory = 1024;

	private readonly Func<T, long> uidOf;
	private readonly Dictionary<long, T> items = new();
	private readonly Dictionary<int, int> processed = new();
	private readonly Dictionary<long, string> hashes = new();
	private readonly Queue<long> hashOrder = new();
	private UidSetHash hash;

	public InventoryAuthority(Func<T, long> uidOf)
	{
		this.uidOf = uidOf ?? throw new ArgumentNullException(nameof(uidOf));
		Remember();
	}

	public long Version { get; private set; }
	public int Count => items.Count;
	public IEnumerable<T> Items => items.Values;
	public string Hash => hash.ToString();
	public bool Contains(long uid) => items.ContainsKey(uid);

	/// <summary>The stored item, for in-place updates that keep its UID (repairs).</summary>
	public bool TryGet(long uid, out T item) => items.TryGetValue(uid, out item);

	public int ProcessedUpTo(int slot) => processed.TryGetValue(slot, out var seq) ? seq : 0;

	/// <summary>A new connection in the slot: its operation numbers start over.</summary>
	public void PlayerJoined(int slot) => processed[slot] = 0;

	public void PlayerLeft(int slot) => processed.Remove(slot);

	public InventoryResult Apply(int slot, int seq, InventoryOp op, T item)
	{
		if (item == null) throw new ArgumentNullException(nameof(item));
		if (seq <= ProcessedUpTo(slot)) return InventoryResult.Replayed;
		processed[slot] = seq;
		return Change(op, item);
	}

	/// <summary>Consumes a player's operation number without changing anything (a refused purchase).</summary>
	public InventoryResult Skip(int slot, int seq)
	{
		if (seq <= ProcessedUpTo(slot)) return InventoryResult.Replayed;
		processed[slot] = seq;
		return InventoryResult.Refused;
	}

	/// <summary>A change made by the host itself (undoing a lost race), outside every player's numbering.</summary>
	public InventoryResult ApplyAsHost(InventoryOp op, T item)
	{
		if (item == null) throw new ArgumentNullException(nameof(item));
		return Change(op, item);
	}

	private InventoryResult Change(InventoryOp op, T item)
	{
		var uid = uidOf(item);
		if (op == InventoryOp.Add)
		{
			if (items.ContainsKey(uid)) return InventoryResult.NoChange;
			items[uid] = item;
			hash.Toggle(uid, true);
			Changed();
			return InventoryResult.Added;
		}
		if (op == InventoryOp.Update)
		{
			// The set of UIDs is unchanged (same hash); the version moves so every player applies it in order.
			if (!items.ContainsKey(uid)) return InventoryResult.NoChange;
			items[uid] = item;
			Changed();
			return InventoryResult.Updated;
		}
		if (!items.Remove(uid)) return InventoryResult.NoChange;
		hash.Toggle(uid, false);
		Changed();
		return InventoryResult.Removed;
	}

	/// <summary>
	/// Whether a player's set at <paramref name="version"/> matches the host's set at that version. Null when the
	/// version is unknown (too old, or not reached yet): nothing can be said.
	/// </summary>
	public bool? Matches(long version, string playerHash) =>
		hashes.TryGetValue(version, out var expected) ? expected == playerHash : null;

	private void Changed()
	{
		Version++;
		Remember();
	}

	private void Remember()
	{
		hashes[Version] = hash.ToString();
		hashOrder.Enqueue(Version);
		while (hashOrder.Count > HashHistory) hashes.Remove(hashOrder.Dequeue());
	}
}

/// <summary>
/// A player's view of one shared inventory: the host's set as of the last version applied, plus this player's own
/// operations the host has not processed yet (predictive, like the shared wallet). <see cref="Desired"/> is what the
/// game's inventory should hold. Main thread only.
/// </summary>
public sealed class InventoryMirror<T> where T : class
{
	public const int MaxPending = 4096;

	private readonly Func<T, long> uidOf;
	private readonly Dictionary<long, T> confirmed = new();
	private readonly List<(int seq, InventoryOp op, T item)> pending = new();
	private UidSetHash hash;
	private int nextSeq;

	public InventoryMirror(Func<T, long> uidOf)
	{
		this.uidOf = uidOf ?? throw new ArgumentNullException(nameof(uidOf));
	}

	/// <summary>A snapshot was applied; versioned changes are tracked from there.</summary>
	public bool Synced { get; private set; }

	public long Version { get; private set; } = -1;
	public int PendingCount => pending.Count;
	public string ConfirmedHash => hash.ToString();
	public int ConfirmedCount => confirmed.Count;

	/// <summary>A change was missed: ask the host for a snapshot.</summary>
	public event Action SnapshotNeeded;

	/// <summary>Records this player's own change; returns the operation number to send.</summary>
	public int Local(InventoryOp op, T item)
	{
		if (item == null) throw new ArgumentNullException(nameof(item));
		if (pending.Count >= MaxPending) pending.RemoveAt(0);
		pending.Add((++nextSeq, op, item));
		return nextSeq;
	}

	/// <summary>A versioned change from the host. <paramref name="ack"/>: this player's operations it has processed.</summary>
	public void Remote(long version, int ack, InventoryOp op, T item)
	{
		Acknowledge(ack);
		if (!Synced || version <= Version) return;
		if (version != Version + 1)
		{
			Synced = false;
			SnapshotNeeded?.Invoke();
			return;
		}
		Version = version;
		var uid = uidOf(item);
		if (op == InventoryOp.Add)
		{
			if (confirmed.ContainsKey(uid)) return;
			confirmed[uid] = item;
			hash.Toggle(uid, true);
		}
		else if (op == InventoryOp.Update)
		{
			if (confirmed.ContainsKey(uid)) confirmed[uid] = item;
		}
		else if (confirmed.Remove(uid))
			hash.Toggle(uid, false);
	}

	public void Snapshot(long version, int ack, IEnumerable<T> items)
	{
		confirmed.Clear();
		hash = new UidSetHash();
		foreach (var item in items)
		{
			var uid = uidOf(item);
			if (confirmed.ContainsKey(uid)) continue;
			confirmed[uid] = item;
			hash.Toggle(uid, true);
		}
		Version = version;
		Synced = true;
		Acknowledge(ack);
	}

	/// <summary>The host's set with this player's unprocessed changes on top, in order.</summary>
	public Dictionary<long, T> Desired()
	{
		var result = new Dictionary<long, T>(confirmed);
		foreach (var (_, op, item) in pending)
		{
			var uid = uidOf(item);
			if (op == InventoryOp.Add) result[uid] = item;
			else if (op == InventoryOp.Update)
			{
				if (result.ContainsKey(uid)) result[uid] = item;
			}
			else result.Remove(uid);
		}
		return result;
	}

	public void Reset()
	{
		confirmed.Clear();
		pending.Clear();
		hash = new UidSetHash();
		Synced = false;
		Version = -1;
		nextSeq = 0;
	}

	private void Acknowledge(int ack) => pending.RemoveAll(p => p.seq <= ack);
}

public static class InventoryDiff
{
	/// <summary>What to add to and remove from <paramref name="current"/> so it holds exactly <paramref name="desired"/>.</summary>
	public static (List<T> add, List<long> remove) Compute<T>(IDictionary<long, T> current, IDictionary<long, T> desired)
	{
		var add = desired.Where(d => !current.ContainsKey(d.Key)).Select(d => d.Value).ToList();
		var remove = current.Keys.Where(uid => !desired.ContainsKey(uid)).ToList();
		return (add, remove);
	}
}

/// <summary>Payload layouts of the inventory packets.</summary>
public static class InventoryWire
{
	public const int MaxSnapshotItems = 200_000;

	/// <summary>Player to host: one change (<c>invPush</c>); <paramref name="txn"/> is the debit that paid for it (0 = none).</summary>
	public static void WritePush<T>(Packet packet, InventoryKind kind, int seq, InventoryOp op, T item, int txn = 0)
	{
		packet.Write((int)kind);
		packet.Write(seq);
		packet.Write((int)op);
		packet.Write(txn);
		packet.Write(item);
	}

	public static (InventoryKind kind, int seq, InventoryOp op, T item, int txn) ReadPush<T>(Packet packet) where T : class
	{
		var kind = ReadKind(packet);
		var seq = packet.ReadInt();
		var op = ReadOp(packet);
		var txn = packet.ReadInt();
		if (seq <= 0 || txn < 0) throw new NetFormatException($"Inventory operation {seq} (transaction {txn}).");
		return (kind, seq, op, packet.Read<T>() ?? throw new NetFormatException("Inventory operation without an item."), txn);
	}

	/// <summary>Host to player: one versioned change (<c>invOp</c>).</summary>
	public static void WriteOp<T>(Packet packet, InventoryKind kind, long version, int ack, InventoryOp op, T item)
	{
		packet.Write((int)kind);
		packet.Write(version);
		packet.Write(ack);
		packet.Write((int)op);
		packet.Write(item);
	}

	public static (InventoryKind kind, long version, int ack, InventoryOp op, T item) ReadOp<T>(Packet packet) where T : class
	{
		var kind = ReadKind(packet);
		var version = packet.Read<long>();
		var ack = packet.ReadInt();
		var op = ReadOp(packet);
		if (version < 0 || ack < 0) throw new NetFormatException($"Inventory change {version}/{ack}.");
		return (kind, version, ack, op, packet.Read<T>() ?? throw new NetFormatException("Inventory change without an item."));
	}

	public static void WriteSnapshot<T>(Packet packet, InventoryKind kind, long version, int ack, List<T> items)
	{
		packet.Write((int)kind);
		packet.Write(version);
		packet.Write(ack);
		packet.Write(items);
	}

	public static (InventoryKind kind, long version, int ack, List<T> items) ReadSnapshot<T>(Packet packet) where T : class
	{
		var kind = ReadKind(packet);
		var version = packet.Read<long>();
		var ack = packet.ReadInt();
		if (version < 0 || ack < 0) throw new NetFormatException($"Inventory snapshot {version}/{ack}.");
		var items = packet.Read<List<T>>() ?? new List<T>();
		if (items.Count > MaxSnapshotItems || items.Any(i => i == null)) throw new NetFormatException("Invalid inventory snapshot.");
		return (kind, version, ack, items);
	}

	public static void WriteRequest(Packet packet, InventoryKind kind) => packet.Write((int)kind);

	public static InventoryKind ReadRequest(Packet packet) => ReadKind(packet);

	public static void WriteCheck(Packet packet, InventoryKind kind, long version, string hash)
	{
		packet.Write((int)kind);
		packet.Write(version);
		packet.Write(hash ?? string.Empty);
	}

	public static (InventoryKind kind, long version, string hash) ReadCheck(Packet packet)
	{
		var kind = ReadKind(packet);
		var version = packet.Read<long>();
		var hash = packet.Read<string>();
		if (version < 0 || hash == null || hash.Length > 64) throw new NetFormatException("Invalid inventory check.");
		return (kind, version, hash);
	}

	private static InventoryKind ReadKind(Packet packet)
	{
		var kind = packet.ReadInt();
		if (kind < 0 || kind > (int)InventoryKind.Groups) throw new NetFormatException($"Unknown inventory {kind}.");
		return (InventoryKind)kind;
	}

	private static InventoryOp ReadOp(Packet packet)
	{
		var op = packet.ReadInt();
		if (op < 0 || op > (int)InventoryOp.Update) throw new NetFormatException($"Unknown inventory operation {op}.");
		return (InventoryOp)op;
	}
}