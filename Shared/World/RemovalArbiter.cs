using System;
using System.Collections.Generic;

namespace CMS21Together.Shared.World;

/// <summary>What became of a player's removal of an item that something else (a mount, a warehouse move) relies on.</summary>
public enum RemovalClaim
{
	/// <summary>This player's removal was the first of that item: what it was for may happen.</summary>
	Won,

	/// <summary>Another player removed the item first: what it was for must be undone.</summary>
	Lost,

	/// <summary>No removal of the item by this player is known (yet).</summary>
	Unknown
}

/// <summary>
/// Phase 8 (assessment follow-up): the host accepts only the first removal of an item's UID, and that removal pays for
/// exactly one thing. Two players taking the same item at the same moment, one into the warehouse or a bulk sale and
/// one onto a car, used to keep both (a duplicate). The inventory already refuses the second removal; this records
/// who won each removal, so the mount, warehouse move or sale that relied on a lost removal is undone. A removal can be
/// claimed once; a later re-add of the UID starts over. Transport-agnostic; main thread only.
/// </summary>
public sealed class RemovalArbiter<T> where T : class
{
	/// <summary>Removals are kept this long: a mount is written when its bolts are done, up to two minutes later.</summary>
	public const long KeepMs = RaceReconciler<T>.MountWindowMs;

	public const int MaxEntries = 4096;

	private readonly Func<T, long> uidOf;
	private readonly Func<T, string> idOf;
	private readonly Func<long> clock;
	private readonly LinkedList<Entry> entries = new();

	public RemovalArbiter(Func<T, long> uidOf, Func<T, string> idOf, Func<long> clockMs)
	{
		this.uidOf = uidOf ?? throw new ArgumentNullException(nameof(uidOf));
		this.idOf = idOf ?? throw new ArgumentNullException(nameof(idOf));
		clock = clockMs ?? throw new ArgumentNullException(nameof(clockMs));
	}

	/// <summary>Claims that came back <see cref="RemovalClaim.Lost"/> (diagnostics and tests).</summary>
	public int Lost { get; private set; }

	/// <summary>Every inventory change the host processed (hook it to the inventory's Processed event).</summary>
	public void Record(int slot, InventoryOp op, T item, InventoryResult result)
	{
		if (item == null || result == InventoryResult.Replayed || op == InventoryOp.Update) return;
		Prune();
		var uid = uidOf(item);
		if (op == InventoryOp.Add)
		{
			// The item is back: earlier removals of it were spent (put back) and decide nothing any more.
			if (result == InventoryResult.Added)
				foreach (var entry in entries)
					if (entry.Uid == uid)
						entry.Used = true;
			return;
		}
		if (op != InventoryOp.Remove) return;
		entries.AddLast(new Entry(slot, uid, idOf(item), result == InventoryResult.Removed, clock()));
		while (entries.Count > MaxEntries) entries.RemoveFirst();
	}

	/// <summary>A change that names the exact item (a warehouse move, a body panel linked to its item).</summary>
	public RemovalClaim ClaimUid(int slot, long uid) => Claim(slot, e => e.Uid == uid);

	/// <summary>A part mount, which names only the item's kind (the newest matching removal; a won one first).</summary>
	public RemovalClaim ClaimId(int slot, string id) => string.IsNullOrEmpty(id) ? RemovalClaim.Unknown : Claim(slot, e => e.Id == id);

	public void PlayerLeft(int slot)
	{
		foreach (var entry in entries)
			if (entry.Slot == slot)
				entry.Used = true;
	}

	private RemovalClaim Claim(int slot, Func<Entry, bool> match)
	{
		Prune();
		Entry lost = null;
		for (var node = entries.Last; node != null; node = node.Previous)
		{
			var entry = node.Value;
			if (entry.Slot != slot || entry.Used || !match(entry)) continue;
			if (entry.Won)
			{
				entry.Used = true;
				return RemovalClaim.Won;
			}
			lost ??= entry;
		}
		if (lost == null) return RemovalClaim.Unknown;
		lost.Used = true;
		Lost++;
		return RemovalClaim.Lost;
	}

	private void Prune()
	{
		var now = clock();
		while (entries.First != null && now - entries.First.Value.AtMs > KeepMs) entries.RemoveFirst();
	}

	private sealed class Entry
	{
		public Entry(int slot, long uid, string id, bool won, long atMs)
		{
			Slot = slot;
			Uid = uid;
			Id = id;
			Won = won;
			AtMs = atMs;
		}

		public int Slot { get; }
		public long Uid { get; }
		public string Id { get; }
		public bool Won { get; }
		public long AtMs { get; }
		public bool Used { get; set; }
	}
}