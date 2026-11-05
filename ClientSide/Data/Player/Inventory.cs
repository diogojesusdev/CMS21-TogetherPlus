using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CMS.Helpers;
using CMS21Together.ClientSide.Data.Handle;
using CMS21Together.ServerSide;
using CMS21Together.Shared.Data.Vanilla;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using CMS21Together.ClientSide.Data.Garage;
using CMS21Together.Shared;
using CMS21Together.Shared.World;

namespace CMS21Together.ClientSide.Data.Player;

[HarmonyPatch]
public static class Inventory
{
	/// <summary>What the game's inventory holds, as far as the mod knows (by UID).</summary>
	private static readonly Dictionary<long, ModItem> gameItems = new();
	private static readonly Dictionary<long, ModGroupItem> gameGroups = new();

	private static InventoryClient<ModItem> items;
	private static InventoryClient<ModGroupItem> groups;
	private static bool loadSkip;
	private static bool populated;
	private static bool reconcileDue;
	private static int applying;
	private static bool skippedLoad;
	private static int loadingFrame = -10;
	private static float nextCheck;

	public const float CheckEverySeconds = 10f;

	/// <summary>A new connection: the shared inventory is followed from the host's snapshot on (plan §3.4).</summary>
	public static void Reset()
	{
		gameItems.Clear();
		gameGroups.Clear();
		loadSkip = false;
		populated = false;
		reconcileDue = false;
		applying = 0;
		nextCheck = 0;
		var clock = System.Diagnostics.Stopwatch.StartNew();
		items = new InventoryClient<ModItem>(InventoryKind.Items, i => i.UID, p => Client.Instance.SendData(p, true), () => clock.ElapsedMilliseconds);
		groups = new InventoryClient<ModGroupItem>(InventoryKind.Groups, g => g.UID, p => Client.Instance.SendData(p, true), () => clock.ElapsedMilliseconds);
		items.Changed += () => reconcileDue = true;
		groups.Changed += () => reconcileDue = true;
		// Sent at the end of the frame, so items bought in the same frame as their debit carry it (Phase 6).
		items.AutoFlush = false;
		groups.AutoFlush = false;
	}

	/// <summary>End of the frame: sends this frame's changes; additions carry the frame's debit, if any.</summary>
	public static void Flush(int purchaseTxn)
	{
		items?.Flush(purchaseTxn);
		groups?.Flush(purchaseTxn);
	}

	public static bool TryGetTracked(long uid, out ModItem item) => gameItems.TryGetValue(uid, out item);

	public static bool TryGetTrackedGroup(long uid, out ModGroupItem group) => gameGroups.TryGetValue(uid, out group);

	/// <summary>This player's recent item changes by frame, to link a body panel to the item it became (Phase 6).</summary>
	private static readonly List<(int frame, InventoryOp op, long uid)> journal = new();

	private static void Journal(InventoryOp op, long uid)
	{
		journal.Add((Time.frameCount, op, uid));
		if (journal.Count > 32) journal.RemoveAt(0);
	}

	/// <summary>The one item this player's game added (or removed) during <paramref name="frame"/>, or 0.</summary>
	public static long LinkedItem(int frame, InventoryOp op)
	{
		long found = 0;
		foreach (var entry in journal)
		{
			if (entry.frame != frame || entry.op != op) continue;
			if (found != 0) return 0;
			found = entry.uid;
		}
		return found;
	}

	/// <summary>Sent right behind the validation (same ordered channel): the host answers once this player is in.</summary>
	public static void RequestSnapshots()
	{
		items?.RequestSnapshot(true);
		groups?.RequestSnapshot(true);
	}

	public static void HandleOp(Packet packet)
	{
		var kind = (InventoryKind)PeekKind(packet);
		if (kind == InventoryKind.Items)
		{
			var op = InventoryWire.ReadOp<ModItem>(packet);
			items?.HandleOp(op.version, op.ack, op.op, op.item);
			if (op.op == InventoryOp.Update) ApplyUpdate(op.item);
		}
		else
		{
			var op = InventoryWire.ReadOp<ModGroupItem>(packet);
			groups?.HandleOp(op.version, op.ack, op.op, op.item);
			if (op.op == InventoryOp.Update) ApplyGroupUpdate(op.item);
		}
	}

	public static void HandleSnapshot(Packet packet)
	{
		var kind = (InventoryKind)PeekKind(packet);
		if (kind == InventoryKind.Items)
		{
			var snapshot = InventoryWire.ReadSnapshot<ModItem>(packet);
			items?.HandleSnapshot(snapshot.version, snapshot.ack, snapshot.items);
		}
		else
		{
			var snapshot = InventoryWire.ReadSnapshot<ModGroupItem>(packet);
			groups?.HandleSnapshot(snapshot.version, snapshot.ack, snapshot.items);
		}
	}

	/// <summary>The packets start with the inventory kind; reading it twice is cheaper than two packet types.</summary>
	private static int PeekKind(Packet packet)
	{
		var payload = packet.PayloadToArray();
		return payload.Length >= 4 ? BitConverter.ToInt32(payload, 0) : -1;
	}

	/// <summary>Main thread, every frame: brings the game's inventory to the shared one, and checks it now and then.</summary>
	public static void Pump()
	{
		if (items == null || !GameLoadHook.IsGameReady() || GameData.Instance?.localInventory == null) return;
		if (reconcileDue && items.Mirror.Synced && groups.Mirror.Synced)
		{
			reconcileDue = false;
			Reconcile(GameData.Instance.localInventory);
		}
		var now = Time.unscaledTime;
		if (now < nextCheck) return;
		nextCheck = now + CheckEverySeconds;
		items.SendCheck();
		groups.SendCheck();
	}

	/// <summary>Adds and removes in the game whatever differs from the shared inventory. Never sent back.</summary>
	private static void Reconcile(global::Inventory inventory)
	{
		applying++;
		try
		{
			var (addGroups, removeGroups) = InventoryDiff.Compute(gameGroups, groups.Mirror.Desired());
			foreach (var uid in removeGroups)
			{
				gameGroups.Remove(uid);
				if (inventory.GetGroup(uid) != null) inventory.DeleteGroup(uid);
			}
			foreach (var group in addGroups)
			{
				var gameGroup = group.ToGame();
				if (gameGroup == null)
				{
					Log.Warn($"[Inventory] Could not create group item {group.UID}.");
					continue;
				}
				gameGroups[group.UID] = group;
				inventory.AddGroup(gameGroup);
			}

			var (addItems, removeItems) = InventoryDiff.Compute(gameItems, items.Mirror.Desired());
			foreach (var uid in removeItems)
			{
				gameItems.Remove(uid);
				var gameItem = inventory.GetItem(uid);
				if (gameItem != null) inventory.Delete(gameItem);
			}
			foreach (var item in addItems)
			{
				var gameItem = item.ToGame();
				if (gameItem == null)
				{
					Log.Warn($"[Inventory] Could not create item {item.UID}.");
					continue;
				}
				gameItems[item.UID] = item;
				inventory.Add(gameItem, false);
			}
			if (addItems.Count + removeItems.Count + addGroups.Count + removeGroups.Count > 0)
				Log.Debug($"[Inventory] Applied the shared inventory: +{addItems.Count}/-{removeItems.Count} items, +{addGroups.Count}/-{removeGroups.Count} groups.");
		}
		catch (Exception ex)
		{
			Log.Error("[Inventory] Applying the shared inventory failed", ex);
		}
		finally
		{
			applying--;
		}
	}

	private static int frozen;
	private static List<long> captured;

	/// <summary>
	/// Starts capturing this player's item deletes instead of sending them (bulk sell, Phase 6b): the host decides which
	/// of them leave the shared inventory.
	/// </summary>
	public static void BeginCapture() => captured = new List<long>();

	/// <summary>The items deleted since <see cref="BeginCapture"/>; capturing stops.</summary>
	public static List<long> EndCapture()
	{
		var result = captured ?? new List<long>();
		captured = null;
		return result;
	}

	/// <summary>
	/// This player changed an item in place (paint, licence plate, quality; Phase 6b): the shared inventory gets the new
	/// data under the same UID.
	/// </summary>
	public static void Update(Item item)
	{
		if (Ignore || item == null || !gameItems.ContainsKey(item.UID)) return;
		var modItem = new ModItem(item);
		gameItems[item.UID] = modItem;
		items.Local(InventoryOp.Update, modItem);
	}

	/// <summary>This player changed a group item in place (wheel balanced, tyre changed; Phase 6b).</summary>
	public static void UpdateGroup(GroupItem group)
	{
		if (Ignore || group == null || !gameGroups.ContainsKey(group.UID)) return;
		var modGroup = new ModGroupItem(group);
		gameGroups[group.UID] = modGroup;
		groups.Local(InventoryOp.Update, modGroup);
	}

	private static void ApplyGroupUpdate(ModGroupItem update)
	{
		if (update?.ItemList == null || GameData.Instance?.localInventory == null || !GameLoadHook.IsGameReady()) return;
		var group = GameData.Instance.localInventory.GetGroup(update.UID);
		if (group?.ItemList == null) return;
		applying++;
		try
		{
			foreach (var item in group.ItemList)
			{
				if (item == null) continue;
				var match = update.ItemList.Find(i => i != null && i.UID == item.UID);
				if (match != null) CopyVisuals(match.ToGame(), item);
			}
			gameGroups[update.UID] = update;
		}
		catch (Exception ex)
		{
			Log.Warn($"[Inventory] Could not apply the change of group {update.UID}: {ex.Message}");
		}
		finally
		{
			applying--;
		}
	}

	private static void CopyVisuals(Item from, Item to)
	{
		if (from == null || to == null) return;
		to.Color = from.Color;
		to.IsPainted = from.IsPainted;
		to.PaintType = from.PaintType;
		to.PaintData = from.PaintData;
		to.IsTinted = from.IsTinted;
		to.TintColor = from.TintColor;
		to.LPData = from.LPData;
		to.Quality = from.Quality;
		to.IsExamined = from.IsExamined;
		to.WheelData = from.WheelData;
	}

	/// <summary>Another player's in-place change: the same data on this game's item (never sent back).</summary>
	private static void ApplyUpdate(ModItem update)
	{
		if (update == null || GameData.Instance?.localInventory == null || !GameLoadHook.IsGameReady()) return;
		var item = GameData.Instance.localInventory.GetItem(update.UID);
		if (item == null) return;
		var fresh = update.ToGame();
		if (fresh == null) return;
		applying++;
		try
		{
			CopyVisuals(fresh, item);
			gameItems[update.UID] = update;
		}
		catch (Exception ex)
		{
			Log.Warn($"[Inventory] Could not apply the change of item {update.UID}: {ex.Message}");
		}
		finally
		{
			applying--;
		}
	}

	/// <summary>Brings the game's inventory back to the shared one on the next frame (after a host decision).</summary>
	public static void ReconcileSoon() => reconcileDue = true;

	/// <summary>
	/// Applies another player's action (Phase 6b features) without touching this game's inventory: the game's own
	/// adds and deletes are skipped, because the inventory follows the host (the acting player's change arrives from
	/// the host by UID). Synchronous only; never across a coroutine yield.
	/// </summary>
	public static void Frozen(Action action)
	{
		frozen++;
		try
		{
			using (ActionGate.Apply()) action();
		}
		finally
		{
			frozen--;
		}
	}

	[HarmonyPatch(typeof(global::Inventory), "Add", typeof(Item), typeof(bool))]
	[HarmonyPrefix]
	[HarmonyPriority(Priority.First)]
	public static bool FreezeAdd() => frozen == 0;

	[HarmonyPatch(typeof(global::Inventory), "AddGroup")]
	[HarmonyPrefix]
	[HarmonyPriority(Priority.First)]
	public static bool FreezeAddGroup() => frozen == 0;

	[HarmonyPatch(typeof(global::Inventory), "Delete")]
	[HarmonyPrefix]
	[HarmonyPriority(Priority.First)]
	public static bool FreezeDelete() => frozen == 0;

	[HarmonyPatch(typeof(global::Inventory), "DeleteGroup")]
	[HarmonyPrefix]
	[HarmonyPriority(Priority.First)]
	public static bool FreezeDeleteGroup() => frozen == 0;

	/// <summary>Changes made by the game itself while applying the host's state are not this player's.</summary>
	private static bool Ignore => !Client.Instance.isConnected || items == null || applying > 0 || ActionGate.ApplyingServerState ||
	                              Time.frameCount == loadingFrame;

	[HarmonyPatch(typeof(UIHelper), nameof(UIHelper.GetItemsForID))]
	[HarmonyPrefix]
	public static bool GetItemsForIDFix(Il2CppSystem.Collections.Generic.List<Item> items,
		string id, ref Il2CppSystem.Collections.Generic.List<BaseItem> __result)
	{
		if (!Client.Instance.isConnected)
			return true;

		// `ToArray()` throws when the game list has no backing array (the rc12 finding), and a ConcurrentBag made the
		// result order random: copy defensively and keep the list order.
		var copy = SafeCopy(items);
		var needle = id ?? string.Empty;
		var newRes = new Il2CppSystem.Collections.Generic.List<BaseItem>();
		foreach (var item in copy)
			if (item?.ID != null && item.ID.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
				newRes.Add(item);

		__result = newRes;
		return false;
	}

	[HarmonyPatch(typeof(UIHelper), nameof(UIHelper.GetBaseItemsForIDExact))]
	[HarmonyPrefix]
	public static bool GetBaseItemsForIDExactFix(Il2CppSystem.Collections.Generic.List<Item> items,
		string id, ref Il2CppSystem.Collections.Generic.List<BaseItem> __result)
	{
		if (!Client.Instance.isConnected) {return true;}

		var copy = SafeCopy(items);
		var newRes = new Il2CppSystem.Collections.Generic.List<BaseItem>();
		foreach (var item in copy)
			if (item != null && string.Equals(item.ID, id, StringComparison.Ordinal))
				newRes.Add(item);

		__result = newRes;
		return false;
	}

	/// <summary>Copies a game list without calling ToArray (null backing array) or indexing past its real storage.</summary>
	private static List<Item> SafeCopy(Il2CppSystem.Collections.Generic.List<Item> items)
	{
		var copy = new List<Item>();
		if (items == null) return copy;
		try
		{
			var count = items.Count;
			for (var i = 0; i < count; i++) copy.Add(items[i]);
		}
		catch (Exception ex)
		{
			Log.Warn($"[Inventory] Could not read the game's item list: {ex.Message}");
		}
		return copy;
	}


	[HarmonyPatch(typeof(global::Inventory), "Add", typeof(Item), typeof(bool))]
	[HarmonyPrefix]
	public static void AddItemHook(Item item, bool showPopup = false)
	{
		if (Ignore || item == null || gameItems.ContainsKey(item.UID)) return;
		var newItem = new ModItem(item);
		gameItems[item.UID] = newItem;
		items.Local(InventoryOp.Add, newItem);
		Journal(InventoryOp.Add, item.UID);
	}

	[HarmonyPatch(typeof(global::Inventory), "AddGroup")]
	[HarmonyPrefix]
	public static void AddGroupItemHook(GroupItem group)
	{
		if (Ignore || group == null || gameGroups.ContainsKey(group.UID)) return;
		var newItem = new ModGroupItem(group);
		gameGroups[group.UID] = newItem;
		groups.Local(InventoryOp.Add, newItem);
	}

	[HarmonyPatch(typeof(global::Inventory), "Delete")]
	[HarmonyPrefix]
	public static void RemoveItemHook(Item item, global::Inventory __instance)
	{
		if (Ignore || item == null) return;
		// Audit A23: tracked by UID, never by object.
		if (gameItems.TryGetValue(item.UID, out var tracked))
		{
			gameItems.Remove(item.UID);
			if (captured != null)
			{
				captured.Add(item.UID);
				return;
			}
			items.Local(InventoryOp.Remove, tracked);
			Journal(InventoryOp.Remove, item.UID);
		}
	}

	[HarmonyPatch(typeof(global::Inventory), "DeleteGroup")]
	[HarmonyPrefix]
	public static void RemoveGroupItemHook(long UId)
	{
		if (Ignore) return;
		if (gameGroups.TryGetValue(UId, out var tracked))
		{
			gameGroups.Remove(UId);
			// Bulk sell captures group removals too; without this their money was client-claimed and the host never
			// checked the removal.
			if (captured != null)
			{
				captured.Add(UId);
				return;
			}
			groups.Local(InventoryOp.Remove, tracked);
		}
	}

	[HarmonyPatch(typeof(global::Inventory), "Load")]
	[HarmonyPrefix]
	public static bool LoadHook(global::Inventory __instance)
	{
		skippedLoad = false;
		if (!Client.Instance.isConnected || items == null) return true;

		if (!Server.Instance.isRunning && !loadSkip)
		{
			// hf3 behaviour: a player's first load is the empty session profile.
			loadSkip = true;
			skippedLoad = true;
			return false;
		}
		// What the game loads is read back afterwards; nothing it does while loading is a player's change.
		loadingFrame = Time.frameCount;
		return true;
	}

	[HarmonyPatch(typeof(global::Inventory), "Load")]
	[HarmonyPostfix]
	public static void LoadPostfix(global::Inventory __instance)
	{
		loadingFrame = -10;
		if (skippedLoad || !Client.Instance.isConnected || items == null) return;
		try
		{
			gameItems.Clear();
			gameGroups.Clear();
			foreach (var group in __instance.GetGroups())
				if (group != null)
					gameGroups[group.UID] = new ModGroupItem(group);
			foreach (var item in __instance.GetItems())
				if (item != null)
					gameItems[item.UID] = new ModItem(item);
		}
		catch (Exception ex)
		{
			Log.Error("[Inventory] Reading the loaded inventory failed", ex);
		}

		if (Server.Instance.isRunning && !populated)
		{
			// The host's own save is the shared inventory at the start of the session.
			populated = true;
			foreach (var group in gameGroups.Values) groups.Local(InventoryOp.Add, group);
			foreach (var item in gameItems.Values) items.Local(InventoryOp.Add, item);
			Log.Info($"[Inventory] Shared inventory starts with {gameItems.Count} items and {gameGroups.Count} groups.");
			return;
		}
		// Every later load: the game shows the shared inventory, not the copy in its profile.
		reconcileDue = true;
	}
}
[Serializable]
public enum InventoryAction
{
	add,
	remove,
	resync
}