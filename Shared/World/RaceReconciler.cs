using System;
using System.Collections.Generic;

namespace CMS21Together.Shared.World;

/// <summary>
/// Phase 2 follow-up: when the host drops a player's part write because another player won that part, the inventory
/// change that came with the write is undone on the host, so a lock race can neither duplicate nor lose an item.
/// Matched by item id and time: an unmount puts the part's new item into the inventory moments before its write; a
/// mount takes the item out when the player picks it, up to the whole bolting time before the write.
/// </summary>
public sealed class RaceReconciler<T> where T : class
{
	public const long UnmountWindowMs = 5_000;
	public const long MountWindowMs = 120_000;
	public const int MaxRecords = 512;

	private readonly Func<T, long> uidOf;
	private readonly Func<T, string> idOf;
	private readonly Func<long> clock;
	private readonly LinkedList<Entry> entries = new();

	public RaceReconciler(Func<T, long> uidOf, Func<T, string> idOf, Func<long> clockMs)
	{
		this.uidOf = uidOf ?? throw new ArgumentNullException(nameof(uidOf));
		this.idOf = idOf ?? throw new ArgumentNullException(nameof(idOf));
		clock = clockMs ?? throw new ArgumentNullException(nameof(clockMs));
	}

	public int Undone { get; private set; }

	/// <summary>Every inventory change the host processed (applied or not).</summary>
	public void Record(int slot, InventoryOp op, T item, InventoryResult result)
	{
		// Updates (paint, plates) never decide a race over a part.
		if (item == null || result == InventoryResult.Replayed || op == InventoryOp.Update) return;
		var applied = result == InventoryResult.Added || result == InventoryResult.Removed;
		entries.AddLast(new Entry(slot, op, item, uidOf(item), idOf(item), applied, clock()));
		while (entries.Count > MaxRecords) entries.RemoveFirst();
	}

	/// <summary>
	/// The host dropped <paramref name="slot"/>'s write of a part with item id <paramref name="itemId"/>.
	/// <paramref name="unmount"/>: the write said the part came off. Returns the inventory change that undoes the
	/// player's side of it, or null when there is nothing (or nothing safe) to undo.
	/// </summary>
	public (InventoryOp op, T item)? Rejected(int slot, string itemId, bool unmount)
	{
		if (string.IsNullOrEmpty(itemId)) return null;
		var now = clock();
		while (entries.First != null && now - entries.First.Value.AtMs > MountWindowMs) entries.RemoveFirst();

		var wanted = unmount ? InventoryOp.Add : InventoryOp.Remove;
		var window = unmount ? UnmountWindowMs : MountWindowMs;
		for (var node = entries.Last; node != null; node = node.Previous)
		{
			var entry = node.Value;
			if (now - entry.AtMs > window) break;
			if (entry.Slot != slot || entry.Op != wanted || entry.Used || !entry.Applied || entry.Id != itemId) continue;
			entry.Used = true;
			if (!unmount && TakenByOther(entry))
			{
				// Someone else took the same item too: they mounted it, so it stays out of the inventory.
				return null;
			}
			Undone++;
			return (unmount ? InventoryOp.Remove : InventoryOp.Add, entry.Item);
		}
		return null;
	}

	/// <summary>
	/// Like <see cref="Rejected(int,string,bool)"/>, for a write that names the exact item it moved (body panels: the
	/// player's game linked the panel to the item it created or used in the same frame).
	/// </summary>
	public (InventoryOp op, T item)? RejectedExact(int slot, long uid, bool unmount)
	{
		if (uid == 0) return null;
		var now = clock();
		var wanted = unmount ? InventoryOp.Add : InventoryOp.Remove;
		var window = unmount ? UnmountWindowMs : MountWindowMs;
		for (var node = entries.Last; node != null; node = node.Previous)
		{
			var entry = node.Value;
			if (now - entry.AtMs > window) break;
			if (entry.Slot != slot || entry.Uid != uid || entry.Op != wanted || entry.Used || !entry.Applied) continue;
			entry.Used = true;
			if (!unmount && TakenByOther(entry)) return null;
			Undone++;
			return (unmount ? InventoryOp.Remove : InventoryOp.Add, entry.Item);
		}
		return null;
	}

	public void PlayerLeft(int slot)
	{
		for (var node = entries.First; node != null; node = node.Next)
			if (node.Value.Slot == slot)
				node.Value.Used = true;
	}

	private bool TakenByOther(Entry removal)
	{
		foreach (var entry in entries)
			if (entry.Slot != removal.Slot && entry.Op == InventoryOp.Remove && entry.Uid == removal.Uid &&
			    Math.Abs(entry.AtMs - removal.AtMs) <= MountWindowMs)
				return true;
		return false;
	}

	private sealed class Entry
	{
		public Entry(int slot, InventoryOp op, T item, long uid, string id, bool applied, long atMs)
		{
			Slot = slot;
			Op = op;
			Item = item;
			Uid = uid;
			Id = id;
			Applied = applied;
			AtMs = atMs;
		}

		public int Slot { get; }
		public InventoryOp Op { get; }
		public T Item { get; }
		public long Uid { get; }
		public string Id { get; }
		public bool Applied { get; }
		public long AtMs { get; }
		public bool Used { get; set; }
	}
}