// Warehouse sync for Together+ (Phase 6b, plan §3.7 "warehouse bulk move"). Together Fixes by TogetherFixer
// (https://github.com/Stevii001100/Fix-For-CMS21-Together) only carries the UI texts of its warehouse bulk move;
// this implementation is new. Custom non-commercial licence of the combined work applies.
using System.Collections.Generic;
using System.Linq;
using CMS21Together.ClientSide.Data;
using CMS21Together.ServerSide;
using CMS21Together.Shared;
using CMS21Together.Shared.Data.Vanilla;
using CMS21Together.Shared.Features;
using CMS21Together.Shared.World;
using UnityEngine;

namespace CMS21Together.ClientSide.Features.Inventory;

/// <summary>
/// The warehouse is the host's (it is saved with the host's save): players send their moves into and out of it, single
/// or bulk, and the host publishes each warehouse's full contents, so every player sees the host's warehouse and
/// nothing a player puts there is lost. hf3 kept a separate, never-saved warehouse on each player's game.
/// </summary>
public sealed class WarehouseFeature : GameFeature
{
	private const string MoveKey = "move";
	private const int AddItem = 1, AddGroup = 2, DeleteItem = 3, DeleteGroup = 4;
	private const float PublishDelay = 0.5f;

	private static WarehouseFeature instance;
	private readonly HashSet<int> dirty = new();

	/// <summary>An undecided add waits this long for the mover's inventory removal (Phase 8).</summary>
	private const float ClaimWaitSeconds = 5f;

	/// <summary>Adds waiting for the host to know whether the mover's removal of the item won (Phase 8).</summary>
	private readonly List<PendingAdd> pending = new();
	private float publishAt;
	private bool publishedAll;

	public override string Id => FeatureCatalog.WarehouseBulkMove;

	private static bool IsHost => Server.Instance != null && Server.Instance.isRunning;
	private static Warehouse Game => GameManager.Instance != null ? GameManager.Instance.Warehouse : null;

	protected override void Patch()
	{
		instance = this;
		Postfix(Target(typeof(Warehouse), nameof(Warehouse.Add), typeof(Item)), nameof(ItemAdded));
		Postfix(Target(typeof(Warehouse), nameof(Warehouse.Add), typeof(GroupItem)), nameof(GroupAdded));
		Postfix(Target(typeof(Warehouse), nameof(Warehouse.Delete), typeof(Item)), nameof(ItemDeleted));
		Postfix(Target(typeof(Warehouse), nameof(Warehouse.Delete), typeof(GroupItem)), nameof(GroupDeleted));
	}

	protected override void Started()
	{
		dirty.Clear();
		pending.Clear();
		publishedAll = false;
	}

	private static void Moved(int op, System.Action<Packet> write)
	{
		if (!LocalAction || instance == null) return;
		var index = Game?.SelectedOption ?? 0;
		if (IsHost)
		{
			instance.Dirty(index);
			return;
		}
		// Phase 8: sent after this frame's inventory changes, so the host knows whether this player's removal of the
		// item won before it puts the item in the warehouse.
		Data.Player.Purchases.Defer(_ => instance.Send(FeatureMessageKind.Event, MoveKey, p =>
		{
			p.Write(op);
			p.Write(index);
			write(p);
		}));
	}

	private static void ItemAdded(Item item)
	{
		if (item == null) return;
		var data = new ModItem(item);
		if (IsHost && LocalAction) instance?.pending.Add(new PendingAdd(1, false, Game?.SelectedOption ?? 0, data.UID, data, null, true));
		Moved(AddItem, p => p.Write(data));
	}

	private static void GroupAdded(GroupItem item)
	{
		if (item == null) return;
		var data = new ModGroupItem(item);
		if (IsHost && LocalAction) instance?.pending.Add(new PendingAdd(1, true, Game?.SelectedOption ?? 0, data.UID, null, data, true));
		Moved(AddGroup, p => p.Write(data));
	}

	private static void ItemDeleted(Item item)
	{
		if (item != null) Moved(DeleteItem, p => p.Write(item.UID));
	}

	private static void GroupDeleted(GroupItem group)
	{
		if (group != null) Moved(DeleteGroup, p => p.Write(group.UID));
	}

	private void Dirty(int index)
	{
		dirty.Add(index);
		publishAt = Time.unscaledTime + PublishDelay;
	}

	protected internal override void Tick()
	{
		if (!IsHost) return;
		var game = Game;
		if (game?.warehouseList == null) return;
		Resolve(game);
		if (!publishedAll)
		{
			publishedAll = true;
			for (var i = 0; i < game.warehouseList.Count; i++) dirty.Add(i);
			publishAt = Time.unscaledTime;
		}
		if (dirty.Count == 0 || Time.unscaledTime < publishAt) return;
		foreach (var index in dirty.ToList()) Publish(game, index);
		dirty.Clear();
	}

	private void Publish(Warehouse game, int index)
	{
		if (index < 0 || index >= game.warehouseList.Count) return;
		var items = new List<ModItem>();
		foreach (var item in game.warehouseList[index])
			if (item != null)
				items.Add(new ModItem(item));
		var groups = new List<ModGroupItem>();
		if (game.warehouseGroupList != null && index < game.warehouseGroupList.Count)
			foreach (var group in game.warehouseGroupList[index])
				if (group != null)
					groups.Add(new ModGroupItem(group));
		Send(FeatureMessageKind.State, "wh/" + index, p =>
		{
			p.Write(index);
			p.Write(items);
			p.Write(groups);
		});
	}

	protected override void Receive(FeatureMessage message)
	{
		var game = Game;
		if (game?.warehouseList == null) return;
		using var p = Payload(message);
		if (message.Key == MoveKey)
		{
			if (!IsHost) return;
			var op = p.ReadInt();
			var index = p.ReadInt();
			if (index < 0 || index >= game.warehouseList.Count) return;
			if (op == AddItem || op == AddGroup)
			{
				var group = op == AddGroup;
				var item = group ? null : p.Read<ModItem>();
				var groupItem = group ? p.Read<ModGroupItem>() : null;
				var uid = group ? groupItem?.UID ?? 0 : item?.UID ?? 0;
				if (uid == 0) return;
				pending.Add(new PendingAdd(message.From, group, index, uid, item, groupItem, false));
				Resolve(game);
				return;
			}
			Remote(() => ApplyMove(game, op, index, p));
			Dirty(index);
			return;
		}
		if (IsHost) return;
		var target = p.ReadInt();
		var items = p.Read<List<ModItem>>() ?? new List<ModItem>();
		var groups = p.Read<List<ModGroupItem>>() ?? new List<ModGroupItem>();
		if (target < 0 || target >= game.warehouseList.Count) return;
		Remote(() =>
		{
			var list = game.warehouseList[target];
			list.Clear();
			foreach (var item in items)
			{
				var gameItem = item.ToGame();
				if (gameItem != null) list.Add(gameItem);
			}
			if (game.warehouseGroupList == null || target >= game.warehouseGroupList.Count) return;
			var groupList = game.warehouseGroupList[target];
			groupList.Clear();
			foreach (var group in groups)
			{
				var gameGroup = group.ToGame();
				if (gameGroup != null) groupList.Add(gameGroup);
			}
		});
	}

	/// <summary>
	/// Host: warehouse adds whose mover's removal of the item is decided (Phase 8). Won: the add stands. Lost (another
	/// player took the item first, e.g. onto a car): the add is dropped (or taken back out of the host's warehouse). Not
	/// decided within 5 s: it stands only if the item is no longer in the shared inventory, so it is never a copy.
	/// </summary>
	private void Resolve(Warehouse game)
	{
		var server = Server.Instance;
		if (pending.Count == 0 || server == null) return;
		var now = Time.unscaledTime;
		foreach (var add in pending.ToArray())
		{
			var claim = add.Group
				? server.GroupRemovals?.ClaimUid(add.From, add.Uid) ?? RemovalClaim.Won
				: server.Removals?.ClaimUid(add.From, add.Uid) ?? RemovalClaim.Won;
			if (claim == RemovalClaim.Unknown)
			{
				if (now - add.Since < ClaimWaitSeconds) continue;
				var inInventory = add.Group ? server.GroupInventory?.Authority.Contains(add.Uid) == true : server.Inventory?.Authority.Contains(add.Uid) == true;
				claim = inInventory ? RemovalClaim.Lost : RemovalClaim.Won;
			}
			pending.Remove(add);
			if (add.Index < 0 || add.Index >= game.warehouseList.Count) continue;
			if (claim == RemovalClaim.Lost)
			{
				Log.Info($"[Warehouse] #{add.From}'s move of item {add.Uid} lost to another player taking it first: not stored.");
				if (add.Own) Remote(() => RemoveFromWarehouse(game, add.Index, add.Uid, add.Group));
			}
			else if (!add.Own)
			{
				var target = add.Index;
				Remote(() => AddToWarehouse(game, target, add.Item, add.GroupItem));
			}
			Dirty(add.Index);
		}
	}

	private static void AddToWarehouse(Warehouse game, int index, ModItem item, ModGroupItem group)
	{
		if (item != null)
		{
			foreach (var existing in game.warehouseList[index])
				if (existing != null && existing.UID == item.UID)
					return;
			var gameItem = item.ToGame();
			if (gameItem != null) game.warehouseList[index].Add(gameItem);
			return;
		}
		if (group == null || game.warehouseGroupList == null || index >= game.warehouseGroupList.Count) return;
		var groups = game.warehouseGroupList[index];
		foreach (var existing in groups)
			if (existing != null && existing.UID == group.UID)
				return;
		var gameGroup = group.ToGame();
		if (gameGroup != null) groups.Add(gameGroup);
	}

	private static void RemoveFromWarehouse(Warehouse game, int index, long uid, bool group)
	{
		if (!group)
		{
			var items = game.warehouseList[index];
			for (var i = items.Count - 1; i >= 0; i--)
				if (items[i] != null && items[i].UID == uid)
					items.RemoveAt(i);
			return;
		}
		if (game.warehouseGroupList == null || index >= game.warehouseGroupList.Count) return;
		var groups = game.warehouseGroupList[index];
		for (var i = groups.Count - 1; i >= 0; i--)
			if (groups[i] != null && groups[i].UID == uid)
				groups.RemoveAt(i);
	}

	private sealed class PendingAdd
	{
		public PendingAdd(int from, bool group, int index, long uid, ModItem item, ModGroupItem groupItem, bool own)
		{
			From = from;
			Group = group;
			Index = index;
			Uid = uid;
			Item = item;
			GroupItem = groupItem;
			Own = own;
			Since = Time.unscaledTime;
		}

		public int From { get; }
		public bool Group { get; }
		public int Index { get; }
		public long Uid { get; }
		public ModItem Item { get; }
		public ModGroupItem GroupItem { get; }

		/// <summary>The host's own move: its game already holds the item in the warehouse.</summary>
		public bool Own { get; }

		public float Since { get; }
	}

	private static void ApplyMove(Warehouse game, int op, int index, Packet p)
	{
		var items = game.warehouseList[index];
		var groups = game.warehouseGroupList != null && index < game.warehouseGroupList.Count ? game.warehouseGroupList[index] : null;
		switch (op)
		{
			case AddItem:
			{
				var item = p.Read<ModItem>();
				if (item == null) return;
				foreach (var existing in items)
					if (existing != null && existing.UID == item.UID)
						return;
				var gameItem = item.ToGame();
				if (gameItem != null) items.Add(gameItem);
				break;
			}
			case AddGroup:
			{
				var group = p.Read<ModGroupItem>();
				if (group == null || groups == null) return;
				foreach (var existing in groups)
					if (existing != null && existing.UID == group.UID)
						return;
				var gameGroup = group.ToGame();
				if (gameGroup != null) groups.Add(gameGroup);
				break;
			}
			case DeleteItem:
			{
				var uid = p.Read<long>();
				for (var i = items.Count - 1; i >= 0; i--)
					if (items[i] != null && items[i].UID == uid)
						items.RemoveAt(i);
				break;
			}
			case DeleteGroup:
			{
				var uid = p.Read<long>();
				if (groups == null) return;
				for (var i = groups.Count - 1; i >= 0; i--)
					if (groups[i] != null && groups[i].UID == uid)
						groups.RemoveAt(i);
				break;
			}
		}
	}
}