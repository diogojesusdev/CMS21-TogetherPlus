// Based on InventoryBulkSellSync.cs from FixForTogether by TogetherFixer
// (https://github.com/Stevii001100/Fix-For-CMS21-Together), custom non-commercial licence.
// Modified for Together+: same patch target; the sale is settled by the host (items removed from the shared
// inventory once, the credit paid into the shared wallet once) instead of each game crediting itself.
using System;
using CMS21Together.ClientSide.Data.Player;
using CMS21Together.ServerSide;
using CMS21Together.Shared;
using CMS21Together.Shared.Data.Vanilla;
using CMS21Together.Shared.Economy;
using CMS21Together.Shared.Features;
using CMS21Together.Shared.Data;
using CMS21Together.Shared.Locks;
using CMS21Together.Shared.World;
using PlayerInventory = CMS21Together.ClientSide.Data.Player.Inventory;

namespace CMS21Together.ClientSide.Features.Inventory;

/// <summary>Bulk sell from the inventory, paid once by the host (like a car sale).</summary>
public sealed class BulkSellFeature : GameFeature
{
	private static int nextRequest;
	private static bool selling;
	private static int depth;

	public override string Id => FeatureCatalog.BulkSell;

	protected override void Patch()
	{
		var sell = Target(typeof(global::Inventory), nameof(global::Inventory.SellPerCondition), typeof(float));
		Prefix(sell, nameof(Before));
		Finalizer(sell, nameof(After));
		// Phase 8: selling one item is settled by the host the same way, so an item another player took at the same
		// moment (onto a car, into the warehouse) is never paid. The game's private SellItem(Item|GroupItem, bool, bool).
		var single = 0;
		foreach (var type in SafeTypes(typeof(global::Inventory).Assembly))
		foreach (var method in type.GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static |
		                                       System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
		                                       System.Reflection.BindingFlags.DeclaredOnly))
		{
			if (method.Name != "SellItem") continue;
			var args = method.GetParameters();
			if (args.Length != 3 || args[0].ParameterType != typeof(Item) && args[0].ParameterType != typeof(GroupItem)) continue;
			Prefix(method, nameof(Before));
			Finalizer(method, nameof(After));
			single++;
		}
		Log.Info($"[BulkSell] Single-item sales settled by the host: {single} entry point(s).");
	}

	private static System.Collections.Generic.IEnumerable<Type> SafeTypes(System.Reflection.Assembly assembly)
	{
		try
		{
			return assembly.GetTypes();
		}
		catch (System.Reflection.ReflectionTypeLoadException ex)
		{
			return System.Linq.Enumerable.Where(ex.Types, t => t != null);
		}
	}

	protected override void Stopped()
	{
		depth = 0;
		if (!selling) return;
		selling = false;
		PlayerInventory.EndCapture();
		Wallet.EndHold();
	}

	private static void Before()
	{
		// A bulk sale may sell item by item: only the outermost call starts and ends the capture.
		if (depth++ > 0 || !LocalAction || selling) return;
		selling = true;
		PlayerInventory.BeginCapture();
		Wallet.BeginHold();
	}

	private static void After()
	{
		if (--depth > 0) return;
		depth = 0;
		if (!selling) return;
		selling = false;
		var uids = PlayerInventory.EndCapture();
		var money = Wallet.EndHold();
		if (uids.Count == 0)
		{
			// Nothing left the shared inventory (e.g. a sale from the warehouse): the money is this player's as before.
			if (money != 0) Wallet.OnLocalChange(money, 0);
			return;
		}
		using var packet = new Packet((int)PacketTypes.bulkSell);
		BulkSale.Write(packet, ++nextRequest, Math.Max(0, Math.Min(money, EconomyLedger.MaxMoneyDelta)), uids);
		Client.Instance.SendData(packet, true);
		Log.Info($"[BulkSell] Sold {uids.Count} items for {money}; the host settles the sale.");
	}

	/// <summary>Host: removes the items still in the shared inventory and pays their share once.</summary>
	public static void HandleSale(int fromClient, Packet packet)
	{
		var (request, total, uids) = BulkSale.Read(packet);
		var server = Server.Instance;
		if (server?.Inventory == null || !Features.InSession(FeatureCatalog.BulkSell)) return;
		var credit = BulkSale.Settle(uids, total, uid =>
		{
			if (!server.Inventory.Authority.TryGet(uid, out var item)) return false;
			if (server.Locks != null && server.Locks.Manager.Conflict(fromClient, LockKeys.Item(uid)) != 0) return false;
			server.Inventory.Correct(InventoryOp.Remove, item);
			return true;
		}, out var sold);
		var paid = Data.ClientData.Instance != null && Data.ClientData.Instance.gamemode != Gamemode.Sandbox;
		var status = credit > 0 && paid ? server.Economy?.Grant(fromClient, credit) ?? TxnStatus.NotReady : TxnStatus.Applied;
		Log.Info($"[BulkSell] #{fromClient} sold {sold} of {uids.Count} items: {credit} ({status}).");
		using var answer = new Packet((int)PacketTypes.purchaseAnswer);
		PurchaseWire.WriteAnswer(answer, -request, status);
		server.Send(fromClient, answer, true);
	}

	/// <summary>Player: the host settled a sale; items it kept come back, and the balance is the host's.</summary>
	public static void Settled()
	{
		PlayerInventory.ReconcileSoon();
		Wallet.OnBalance();
	}
}