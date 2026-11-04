using System;
using System.Collections.Generic;
using System.Linq;
using CMS21Together.Shared.Economy;
using CMS21Together.Shared.World;
using NUnit.Framework;

namespace CMS21Together.Tests;

/// <summary>
/// Phase 8 (assessment follow-up): the same item taken at the same moment by two or three players, into the warehouse,
/// a bulk or single sale, or onto a car. The host accepts only the first removal of the UID, and that removal pays for
/// one thing: afterwards the item exists exactly once (in the inventory, the warehouse, on the car, or sold) and a sale
/// is paid at most once. The host rules below are those of ServerHandle.PartScriptPacket (mount),
/// WarehouseFeature.Resolve (warehouse) and BulkSellFeature.HandleSale (sale).
/// </summary>
[TestFixture]
public class ItemRaceTests
{
	private const long Uid = 7;
	private const string Kind = "spark_plug";
	private const long Price = 400;

	private enum Act
	{
		Mount,
		Warehouse,
		Sale
	}

	private sealed class Host
	{
		public long Now;
		public readonly InventoryAuthority<TestItem> Inventory = new(i => i.UID);
		public readonly RemovalArbiter<TestItem> Removals;
		public readonly EconomyServer Economy = new((_, _) => { }, () => new[] { 1, 2, 3 });
		public readonly Dictionary<int, int> Seq = new();
		public readonly HashSet<int> Mounted = new();
		public readonly HashSet<int> Warehouse = new();
		public readonly List<(int slot, long uid)> UndecidedWarehouse = new();
		public int Sold;

		public Host()
		{
			Removals = new RemovalArbiter<TestItem>(i => i.UID, i => i.ID, () => Now);
			Economy.Initialize(0, 0);
			Inventory.ApplyAsHost(InventoryOp.Add, Item());
		}

		public void Remove(int slot)
		{
			Seq[slot] = Seq.TryGetValue(slot, out var s) ? s + 1 : 1;
			var item = Item();
			Removals.Record(slot, InventoryOp.Remove, item, Inventory.Apply(slot, Seq[slot], InventoryOp.Remove, item));
		}

		public void MountWrite(int slot)
		{
			// A mount whose removal lost is undone: the writer gets the part back unmounted, no item is created.
			if (Removals.ClaimId(slot, Kind) != RemovalClaim.Lost) Mounted.Add(slot);
		}

		public void WarehouseAdd(int slot)
		{
			var claim = Removals.ClaimUid(slot, Uid);
			if (claim == RemovalClaim.Won) Warehouse.Add(slot);
			else if (claim == RemovalClaim.Unknown) UndecidedWarehouse.Add((slot, Uid));
		}

		public void Sale(int slot)
		{
			var credit = BulkSale.Settle(new[] { Uid }, Price, uid =>
			{
				if (!Inventory.TryGet(uid, out var item)) return false;
				Inventory.ApplyAsHost(InventoryOp.Remove, item);
				return true;
			}, out var sold);
			Sold += sold;
			if (credit > 0) Economy.Grant(slot, credit);
		}

		/// <summary>Undecided warehouse adds after their wait: kept only if the item left the inventory.</summary>
		public void Timeout()
		{
			foreach (var (slot, uid) in UndecidedWarehouse)
				if (!Inventory.Contains(uid))
					Warehouse.Add(slot);
			UndecidedWarehouse.Clear();
		}

		public int Copies => (Inventory.Contains(Uid) ? 1 : 0) + Warehouse.Count + Mounted.Count + Sold;
	}

	private static TestItem Item() => new() { UID = Uid, ID = Kind };

	/// <summary>Each player's messages in the order its game sends them (removal first, then what it was for).</summary>
	private static List<Action<Host>> Steps(int slot, Act act) => act switch
	{
		Act.Mount => new List<Action<Host>> { h => h.Remove(slot), h => h.MountWrite(slot) },
		Act.Warehouse => new List<Action<Host>> { h => h.Remove(slot), h => h.WarehouseAdd(slot) },
		_ => new List<Action<Host>> { h => h.Sale(slot) }
	};

	[Test]
	public void TheSameItemTakenAtOnceExistsExactlyOnce([Values(1, 2, 3, 4)] int seed)
	{
		var random = new Random(seed);
		var acts = (Act[])Enum.GetValues(typeof(Act));
		var outcomes = new Dictionary<string, int>();
		for (var run = 0; run < 1000; run++)
		{
			var host = new Host();
			var players = random.Next(2, 4);
			var queues = new List<Queue<Action<Host>>>();
			var picked = new List<Act>();
			for (var slot = 1; slot <= players; slot++)
			{
				var act = acts[random.Next(acts.Length)];
				picked.Add(act);
				queues.Add(new Queue<Action<Host>>(Steps(slot, act)));
			}
			// A random interleaving that keeps each player's own order (one ordered channel per player).
			while (queues.Any(q => q.Count > 0))
			{
				var live = queues.Where(q => q.Count > 0).ToList();
				host.Now += random.Next(0, 3000);
				live[random.Next(live.Count)].Dequeue()(host);
			}
			host.Timeout();

			var label = string.Join("+", picked);
			Assert.That(host.Copies, Is.EqualTo(1), $"run {run} ({label}): the item must exist exactly once");
			Assert.That(host.Sold, Is.LessThanOrEqualTo(1), $"run {run} ({label})");
			Assert.That(host.Economy.Ledger.Money, Is.EqualTo(host.Sold * Price), $"run {run} ({label}): paid at most once, only if sold");
			var winner = host.Mounted.Count > 0 ? "mounted" : host.Warehouse.Count > 0 ? "warehouse" : host.Sold > 0 ? "sold" : "inventory";
			outcomes[winner] = outcomes.TryGetValue(winner, out var n) ? n + 1 : 1;
		}
		TestContext.WriteLine(string.Join(", ", outcomes.Select(o => $"{o.Key} {o.Value}")));
		Assert.That(outcomes.Keys, Is.SupersetOf(new[] { "mounted", "warehouse", "sold" }), "every kind of winner happens");
	}

	[Test]
	public void AClaimIsSpentOnceAndAPutBackItemStartsOver()
	{
		long now = 0;
		var arbiter = new RemovalArbiter<TestItem>(i => i.UID, i => i.ID, () => now);
		arbiter.Record(1, InventoryOp.Remove, Item(), InventoryResult.Removed);
		arbiter.Record(2, InventoryOp.Remove, Item(), InventoryResult.NoChange);
		Assert.That(arbiter.ClaimUid(2, Uid), Is.EqualTo(RemovalClaim.Lost));
		Assert.That(arbiter.ClaimUid(1, Uid), Is.EqualTo(RemovalClaim.Won));
		Assert.That(arbiter.ClaimUid(1, Uid), Is.EqualTo(RemovalClaim.Unknown), "one removal pays for one thing");

		arbiter.Record(3, InventoryOp.Remove, Item(), InventoryResult.Removed);
		arbiter.Record(3, InventoryOp.Add, Item(), InventoryResult.Added);
		Assert.That(arbiter.ClaimId(3, Kind), Is.EqualTo(RemovalClaim.Unknown), "put back: that removal decides nothing");

		arbiter.Record(4, InventoryOp.Remove, Item(), InventoryResult.Removed);
		now += RemovalArbiter<TestItem>.KeepMs + 1;
		Assert.That(arbiter.ClaimUid(4, Uid), Is.EqualTo(RemovalClaim.Unknown), "forgotten after the mount window");
		arbiter.Record(5, InventoryOp.Remove, Item(), InventoryResult.Removed);
		arbiter.PlayerLeft(5);
		Assert.That(arbiter.ClaimUid(5, Uid), Is.EqualTo(RemovalClaim.Unknown));
		Assert.That(arbiter.ClaimId(6, null), Is.EqualTo(RemovalClaim.Unknown));
	}

	// Purchase before the wallet syncs (assessment follow-up 2).

	[TestCase(false, true, false, true)]
	[TestCase(false, true, true, false)]
	[TestCase(true, true, false, false)]
	[TestCase(false, false, false, false)]
	public void APlayerShowsNoMoneyUntilTheHostsWalletArrives(bool host, bool used, bool synced, bool hold) =>
		Assert.That(WalletRules.HoldAtZero(host, used, synced), Is.EqualTo(hold));

	[TestCase(true, false, false, false)]
	[TestCase(true, true, false, false)]
	[TestCase(true, true, true, true)]
	[TestCase(false, false, false, true)]
	public void MoneyIsSpentOnlyOnceTheWalletIsSyncedAndShown(bool used, bool synced, bool shown, bool can) =>
		Assert.That(WalletRules.CanSpend(used, synced, shown), Is.EqualTo(can));

	[Test]
	public void BeforeTheFirstBalanceNothingIsSpentOrSent()
	{
		var sent = 0;
		var client = new EconomyClient(_ => sent++);
		Assert.That(client.Synced, Is.False);
		Assert.That(client.Money, Is.Zero, "the game is shown 0, so it refuses every purchase");
		Assert.That(client.Record(-500, 0), Is.False);
		Assert.That(sent, Is.Zero);
		Assert.That(WalletRules.CanSpend(true, client.Synced, false), Is.False);

		using var balance = new CMS21Together.Shared.Packet((int)CMS21Together.Shared.PacketTypes.econBalance);
		EconomyWire.WriteBalance(balance, 5000, 0, 1, 0, TxnStatus.None);
		client.HandleBalance(new CMS21Together.Shared.Packet(balance.Id, balance.PayloadToArray()));
		Assert.That(client.Money, Is.EqualTo(5000));
		Assert.That(client.Record(-500, 0), Is.True);
		Assert.That(sent, Is.EqualTo(1));
		Assert.That(WalletRules.CanSpend(true, client.Synced, true), Is.True);
	}
}