using System;
using System.Collections.Generic;
using System.Linq;
using CMS21Together.Shared;
using CMS21Together.Shared.Economy;
using CMS21Together.Shared.Features;
using CMS21Together.Shared.Net;
using NUnit.Framework;

namespace CMS21Together.Tests;

/// <summary>Phase 6b: feature switches, isolation of broken features, host-decided config and the feature channel.</summary>
[TestFixture]
public class FeatureTests
{
	private sealed class FakeModule : IFeatureModule
	{
		public FakeModule(string id, bool fails = false)
		{
			Id = id;
			Fails = fails;
		}

		public string Id { get; }
		public bool Fails;
		public int Patches;
		public int Installs;

		public void Install()
		{
			Installs++;
			Patches++;
			if (Fails) throw new MissingMethodException("target gone");
			Patches++;
		}

		public void Uninstall() => Patches = 0;
	}

	[Test]
	public void DefaultsAreOnExceptSkipMinigames()
	{
		var defaults = FeatureCatalog.Defaults();
		Assert.That(defaults.Where(p => !p.Value).Select(p => p.Key), Is.EqualTo(new[] { FeatureCatalog.SkipMinigames }));
		Assert.That(FeatureCatalog.All.Select(f => f.Id).Distinct().Count(), Is.EqualTo(FeatureCatalog.All.Count));
	}

	[Test]
	public void AFeatureSwitchedOffHasNoPatchInEffect()
	{
		var board = new FeatureSwitchboard();
		var tint = new FakeModule(FeatureCatalog.WindowTint);
		board.Register(tint);
		board.Apply(new Dictionary<string, bool> { [FeatureCatalog.WindowTint] = true });
		Assert.That(board.IsOn(FeatureCatalog.WindowTint) && tint.Patches > 0, Is.True);
		board.Apply(FeatureConfig.AllOff());
		Assert.That(tint.Patches, Is.Zero);
		Assert.That(board.StateOf(FeatureCatalog.WindowTint), Is.EqualTo(FeatureState.Off));
		board.Apply(new Dictionary<string, bool>());
		Assert.That(tint.Installs, Is.EqualTo(1), "missing switch = off, nothing installed again");
	}

	[Test]
	public void ABrokenFeatureIsUndoneAndNeverTakesOthersDown()
	{
		var board = new FeatureSwitchboard();
		var dyno = new FakeModule(FeatureCatalog.Dyno, fails: true);
		var lathe = new FakeModule(FeatureCatalog.BrakeLathe);
		board.Register(dyno);
		board.Register(lathe);
		board.Apply(FeatureCatalog.Defaults());
		Assert.That(board.StateOf(FeatureCatalog.Dyno), Is.EqualTo(FeatureState.Broken));
		Assert.That(dyno.Patches, Is.Zero, "the half-installed patch was removed");
		Assert.That(board.IsOn(FeatureCatalog.BrakeLathe), Is.True);
		board.Apply(FeatureCatalog.Defaults());
		Assert.That(dyno.Installs, Is.EqualTo(1), "a broken feature is not retried every time");
	}

	[Test]
	public void PlayersFollowTheHostsSwitches()
	{
		var host = FeatureConfig.FromPreferences(new Dictionary<string, bool> { [FeatureCatalog.Parking] = false, ["NotAFeature"] = true });
		Assert.That(host[FeatureCatalog.Parking], Is.False);
		Assert.That(host.ContainsKey("NotAFeature"), Is.False);
		Assert.That(host[FeatureCatalog.Dyno], Is.True, "missing preference = default");

		using var packet = new Packet((int)PacketTypes.features);
		FeatureConfig.Write(packet, host);
		using var read = new Packet(packet.Id, packet.PayloadToArray());
		var player = FeatureConfig.Read(read);
		Assert.That(player, Is.EquivalentTo(host), "a player's own preferences don't matter");
		Assert.That(FeatureConfig.FollowHost(null).Values.Any(v => v), Is.False);
	}

	private static FeatureMessage Msg(string feature, FeatureMessageKind kind, string key, params byte[] data) =>
		new() { Feature = FeatureCatalog.Find(feature).Index, Kind = kind, Key = key, Data = data };

	[Test]
	public void FeatureMessagesRoundTripAndRejectGarbage()
	{
		using var packet = new Packet((int)PacketTypes.feature);
		Msg(FeatureCatalog.BatteryCharger, FeatureMessageKind.State, "car/3", 1, 2, 3).Write(packet);
		using var read = new Packet(packet.Id, packet.PayloadToArray());
		var message = FeatureMessage.Read(read);
		Assert.That(message.Feature, Is.EqualTo(FeatureCatalog.Find(FeatureCatalog.BatteryCharger).Index));
		Assert.That((message.Kind, message.Key, message.Data), Is.EqualTo((FeatureMessageKind.State, "car/3", new byte[] { 1, 2, 3 })));

		using var bad = new Packet((int)PacketTypes.feature);
		bad.Write(999);
		bad.Write(0);
		bad.Write("x");
		bad.Write(0);
		bad.Write(0);
		using var badRead = new Packet(bad.Id, bad.PayloadToArray());
		Assert.Throws<NetFormatException>(() => FeatureMessage.Read(badRead));

		using var lying = new Packet((int)PacketTypes.feature);
		lying.Write(0);
		lying.Write(0);
		lying.Write("x");
		lying.Write(0);
		lying.Write(1000);
		using var lyingRead = new Packet(lying.Id, lying.PayloadToArray());
		Assert.Throws<NetFormatException>(() => FeatureMessage.Read(lyingRead), "a length beyond the packet");
	}

	[Test]
	public void TheRelayForwardsOnlyFeaturesThatAreOnAndKeepsStatesForLateJoiners()
	{
		var on = new HashSet<string> { FeatureCatalog.BatteryCharger, FeatureCatalog.WindowTint };
		var broadcast = new List<(int except, FeatureMessage m)>();
		var direct = new List<(int to, FeatureMessage m)>();
		var relay = new FeatureRelay(on.Contains, (s, m) => broadcast.Add((s, m)), (s, m) => direct.Add((s, m)));

		Assert.That(relay.Handle(2, Msg(FeatureCatalog.Dyno, FeatureMessageKind.Event, "")), Is.False, "dyno is off");
		Assert.That(relay.Handle(2, Msg(FeatureCatalog.BatteryCharger, FeatureMessageKind.State, "car/1", 1)), Is.True);
		relay.Handle(3, Msg(FeatureCatalog.BatteryCharger, FeatureMessageKind.State, "car/1", 2));
		relay.Handle(3, Msg(FeatureCatalog.WindowTint, FeatureMessageKind.State, "car/2", 9));
		relay.Handle(3, Msg(FeatureCatalog.WindowTint, FeatureMessageKind.Event, "car/2", 7));
		Assert.That(broadcast.Select(b => b.except), Is.EqualTo(new[] { 2, 3, 3, 3 }), "never echoed to the sender");
		Assert.That(broadcast[0].m.From, Is.EqualTo(2));

		Assert.That(relay.SendStates(4), Is.EqualTo(2));
		Assert.That(direct.Select(d => d.m.Data[0]), Is.EqualTo(new byte[] { 2, 9 }), "the last state per key, events not kept");

		relay.Handle(2, Msg(FeatureCatalog.BatteryCharger, FeatureMessageKind.Clear, "car/1"));
		on.Remove(FeatureCatalog.WindowTint);
		direct.Clear();
		Assert.That(relay.SendStates(5), Is.Zero, "cleared, and states of a feature switched off are not replayed");
	}

	[Test]
	public void TheRelayKeepsABoundedNumberOfStates()
	{
		var relay = new FeatureRelay(_ => true, (_, _) => { }, (_, _) => { });
		for (var i = 0; i < FeatureRelay.MaxStates + 50; i++) relay.Handle(2, Msg(FeatureCatalog.ItemPaint, FeatureMessageKind.State, "item/" + i));
		Assert.That(relay.StateCount, Is.EqualTo(FeatureRelay.MaxStates));
	}

	[TestCase("+connect CMS21Together:ABC123", "ABC123")]
	[TestCase("game.exe -x +connect CMS21Together:XYZ -y", "XYZ")]
	[TestCase("\"CMS21Together:Q1\"", "Q1")]
	[TestCase("+connect 127.0.0.1", null)]
	[TestCase("CMS21Together:", null)]
	[TestCase(null, null)]
	public void SteamConnectStringsGiveTheSessionCode(string connect, string code)
	{
		Assert.That(SteamConnect.Parse(connect), Is.EqualTo(code));
		if (code != null) Assert.That(SteamConnect.Parse(SteamConnect.Of(code)), Is.EqualTo(code));
	}

	[Test]
	public void OnlyWorkshopCarsEveryPlayerHasAreAllowed()
	{
		var left = new WorkshopCars();
		left.Report(1, new[] { "a", "b" });
		left.Report(2, new[] { "a" });
		Assert.That(left.KeepOnly(new[] { 1 }), Is.True, "player 2 left");
		Assert.That(left.Allowed(), Is.EqualTo(new[] { "a", "b" }), "its missing car is offered again");
		Assert.That(left.KeepOnly(new[] { 1 }), Is.False);
		var cars = new WorkshopCars();
		Assert.That(cars.Allowed(), Is.Null);
		cars.Report(1, new[] { "a", "b", "c" });
		cars.Report(2, new[] { "b", "c", "d" });
		cars.Report(3, new[] { "c", "b" });
		Assert.That(cars.Allowed(), Is.EqualTo(new[] { "b", "c" }));
		cars.Report(4, new string[0]);
		Assert.That(cars.Allowed(), Is.Empty, "a player without workshop cars allows none");
		cars.Forget(4);
		Assert.That(cars.Allowed(), Is.EqualTo(new[] { "b", "c" }));

		using var packet = new Packet((int)PacketTypes.feature);
		WorkshopCars.Write(packet, new[] { "x", "y" });
		using var read = new Packet(packet.Id, packet.PayloadToArray());
		Assert.That(WorkshopCars.Read(read), Is.EqualTo(new[] { "x", "y" }));
	}

	[TestCase("car/3", 3)]
	[TestCase("car/12/ecu", 12)]
	[TestCase("car/0/part/1/2/3", 0)]
	[TestCase("car/", -1)]
	[TestCase("car/x/ecu", -1)]
	[TestCase("car/-1", -1)]
	[TestCase("wh/2", -1)]
	[TestCase("rows", -1)]
	[TestCase(null, -1)]
	public void KeysTellWhichCarTheyBelongTo(string key, int car) => Assert.That(FeatureRelay.CarOf(key), Is.EqualTo(car));

	[Test]
	public void ACarSentAgainGetsItsKeptStatesAndADeletedCarLosesThem()
	{
		// Phase 8 assessment (high): tuning, alignment and dyno results were lost when a car was sent again.
		var on = new HashSet<string> { FeatureCatalog.Tuning, FeatureCatalog.Dyno, FeatureCatalog.WarehouseBulkMove };
		var direct = new List<(int to, FeatureMessage m)>();
		var relay = new FeatureRelay(on.Contains, (_, _) => { }, (s, m) => direct.Add((s, m)));
		relay.Handle(2, Msg(FeatureCatalog.Tuning, FeatureMessageKind.State, "car/1/ecu", 1));
		relay.Handle(2, Msg(FeatureCatalog.Dyno, FeatureMessageKind.State, "car/1", 2));
		relay.Handle(2, Msg(FeatureCatalog.Dyno, FeatureMessageKind.State, "car/10", 3));
		relay.Handle(1, Msg(FeatureCatalog.WarehouseBulkMove, FeatureMessageKind.State, "wh/0", 4));

		Assert.That(relay.SendCarStates(5, 1), Is.EqualTo(2));
		Assert.That(direct.Select(d => (d.to, d.m.Data[0])), Is.EqualTo(new[] { (5, (byte)1), (5, (byte)2) }), "only car 1's, not car 10's");
		on.Remove(FeatureCatalog.Tuning);
		Assert.That(relay.SendCarStates(5, 1), Is.EqualTo(1), "a feature switched off sends nothing");

		Assert.That(relay.ForgetCar(1), Is.EqualTo(2));
		Assert.That(relay.SendCarStates(5, 1), Is.Zero, "the next car in loader 1 starts clean");
		Assert.That(relay.StateCount, Is.EqualTo(2));
	}

	[Test]
	public void SessionStatesOutliveManyCarStates()
	{
		// Phase 8 assessment: the warehouse and parking rows were the first evicted once 2,048 states were kept.
		var on = new HashSet<string> { FeatureCatalog.ExaminedSync, FeatureCatalog.WarehouseBulkMove, FeatureCatalog.Parking };
		var direct = new List<FeatureMessage>();
		var relay = new FeatureRelay(on.Contains, (_, _) => { }, (_, m) => direct.Add(m));
		relay.Handle(1, Msg(FeatureCatalog.Parking, FeatureMessageKind.State, "rows", 3));
		relay.Handle(1, Msg(FeatureCatalog.WarehouseBulkMove, FeatureMessageKind.State, "wh/0", 1));
		for (var i = 0; i < FeatureRelay.MaxStates * 2; i++)
			relay.Handle(2, Msg(FeatureCatalog.ExaminedSync, FeatureMessageKind.State, $"car/{i % 20}/part/0/{i}/0", 1));
		Assert.That(relay.StateCount, Is.EqualTo(FeatureRelay.MaxStates));
		relay.SendStates(9);
		Assert.That(direct.Select(m => m.Key), Does.Contain("rows").And.Contain("wh/0"));

		relay.Handle(1, Msg(FeatureCatalog.WarehouseBulkMove, FeatureMessageKind.State, "wh/0", 2));
		direct.Clear();
		relay.SendStates(9);
		Assert.That(direct.Last().Key, Is.EqualTo("wh/0"), "an overwritten state is the newest");
		Assert.That(direct.Last().Data[0], Is.EqualTo(2));
	}

	[Test]
	public void ACampaignCarWithNoDebitIsRefusedInsteadOfDelivered()
	{
		var queue = new AwayCarQueue<string>();
		var dropped = new List<(string, AwayCarOutcome)>();
		Assert.That(queue.Add(2, 0, "u9", "free?", 0, requiresDebit: true), Is.True);
		Assert.That(queue.Step((_, _) => TxnStatus.None, 1, (car, outcome) => dropped.Add((car.Car, outcome))), Is.Empty,
			"the game may take the money a moment later");
		Assert.That(queue.Step((_, _) => TxnStatus.None, AwayCarQueue<string>.NoDebitSeconds + 0.1,
			(car, outcome) => dropped.Add((car.Car, outcome))), Is.Empty);
		Assert.That(dropped, Is.EqualTo(new[] { ("free?", AwayCarOutcome.Refused) }), "no debit: never delivered");
	}

	[Test]
	public void OnlyNewCarsCountAsBoughtAway()
	{
		var before = new List<(string, string)> { ("u1", "car_a"), (null, null), ("u2", "car_b"), (null, null) };
		Assert.That(AwayCars.Added(before, before), Is.Empty, "nothing changed");
		var bought = new List<(string, string)> { ("u1", "car_a"), ("u3", "car_c"), ("u2", "car_b"), (null, null) };
		Assert.That(AwayCars.Added(before, bought), Is.EqualTo(new[] { 1 }));
		var moved = new List<(string, string)> { (null, null), ("u1", "car_a"), ("u2", "car_b"), (null, null) };
		Assert.That(AwayCars.Added(before, moved), Is.Empty, "a car moved to another slot was not bought");
		var sold = new List<(string, string)> { (null, null), (null, null), ("u2", "car_b"), (null, null) };
		Assert.That(AwayCars.Added(before, sold), Is.Empty);
		var replaced = new List<(string, string)> { ("u9", "car_a"), (null, null), ("u2", "car_b"), ("", "car_d") };
		Assert.That(AwayCars.Added(before, replaced), Is.EqualTo(new[] { 0, 3 }), "same model, new car; a car without UId");
		Assert.That(AwayCars.Added(null, bought), Is.EqualTo(new[] { 0, 1, 2 }));
	}

	[Test]
	public void AwayCarsArePlacedOnlyOnceAndOnlyWhenCharged()
	{
		var queue = new AwayCarQueue<string>();
		var status = new Dictionary<(int, int), TxnStatus>();
		TxnStatus Status(int slot, int txn) => status.TryGetValue((slot, txn), out var s) ? s : TxnStatus.None;
		var dropped = new List<(string, AwayCarOutcome)>();
		void Drop(AwayCar<string> car, AwayCarOutcome outcome) => dropped.Add((car.Car, outcome));

		Assert.That(queue.Add(2, 10, "u1", "paid", 0), Is.True);
		Assert.That(queue.Add(2, 10, "u1", "paid again", 0), Is.False, "the same car is handed over once");
		Assert.That(queue.Add(3, 7, "u2", "refused", 0), Is.True);
		Assert.That(queue.Add(4, 3, "u3", "lost", 0), Is.True);
		Assert.That(queue.Add(5, 0, "u4", "sandbox", 0, requiresDebit: false), Is.True);

		var ready = queue.Step(Status, 1, Drop);
		Assert.That(ready.Select(c => c.Car), Is.EqualTo(new[] { "sandbox" }), "no debit, nothing to wait for");
		Assert.That(dropped, Is.Empty, "undecided debits wait");

		status[(2, 10)] = TxnStatus.Applied;
		status[(3, 7)] = TxnStatus.Insufficient;
		var debits = new Dictionary<(int, int), long> { [(2, 10)] = -5_000 };
		ready = queue.Step(Status, 2, Drop, (slot, txn) => debits.TryGetValue((slot, txn), out var m) ? m : 0);
		Assert.That(ready[0].Charge, Is.EqualTo(5_000), "the charge is kept when the debit is applied");
		debits.Clear();
		Assert.That(queue.Step(Status, 3, Drop, (_, _) => 0)[0].Charge, Is.EqualTo(5_000), "a reconnect (numbers restart) cannot change it");
		Assert.That(ready.Select(c => c.Car), Is.EqualTo(new[] { "paid", "sandbox" }));
		Assert.That(dropped, Is.EqualTo(new[] { ("refused", AwayCarOutcome.Refused) }), "a refused debit never ships a car");

		queue.Done(ready[0]);
		ready = queue.Step(Status, AwayCarQueue<string>.GiveUpSeconds + 1, Drop);
		Assert.That(ready.Select(c => c.Car), Is.EqualTo(new[] { "sandbox" }), "a placed car is not placed again; charged cars never time out");
		Assert.That(dropped.Last(), Is.EqualTo(("lost", AwayCarOutcome.TimedOut)));
		Assert.That(queue.Count, Is.EqualTo(1));
		Assert.That(queue.Add(2, 10, "u1", "paid", 0), Is.False, "a placed car's UId stays known");
	}

	[Test]
	public void AnUndeliveredAwayCarRefundsExactlyItsDebitOnce()
	{
		var economy = new EconomyServer((_, _) => { }, () => new[] { 1, 2 });
		economy.Initialize(10_000, 0);
		economy.PlayerJoined(2);
		TxnStatus Txn(int number, long money)
		{
			using var packet = new Packet((int)PacketTypes.econTxn);
			EconomyWire.WriteTxn(packet, number, money, 0);
			using var read = new Packet(packet.Id, packet.PayloadToArray());
			economy.HandleTxn(2, read);
			return economy.StatusOf(2, number);
		}
		Assert.That(Txn(1, -4_000), Is.EqualTo(TxnStatus.Applied));
		Assert.That(Txn(2, -9_000), Is.EqualTo(TxnStatus.Insufficient));
		Assert.That(Txn(3, 500), Is.EqualTo(TxnStatus.Applied));
		Assert.That(economy.Ledger.Money, Is.EqualTo(6_500));

		Assert.That(economy.Refund(2, 2), Is.Zero, "a refused debit charged nothing");
		Assert.That(economy.Refund(2, 3), Is.Zero, "a credit is not a debit");
		Assert.That(economy.Refund(1, 1), Is.Zero, "another player's number");
		Assert.That(economy.Refund(2, 1), Is.EqualTo(4_000));
		Assert.That(economy.Refund(2, 1), Is.Zero, "only once");
		Assert.That(economy.Ledger.Money, Is.EqualTo(10_500));
		economy.PlayerJoined(2);
		Assert.That(Txn(1, -100), Is.EqualTo(TxnStatus.Applied));
		economy.PlayerLeft(2);
		Assert.That(economy.Refund(2, 1), Is.Zero, "forgotten with the connection");
	}

	[TestCase(-10, true)]
	[TestCase(-11, true)]
	[TestCase(-999, true)]
	[TestCase(-9, false)]
	[TestCase(-1000, false)]
	[TestCase(-1, false)]
	[TestCase(-2, false)]
	[TestCase(0, false)]
	[TestCase(3, false)]
	public void OnlyQolExtraParkingPlacesAreExtra(int placeNo, bool extra) =>
		Assert.That(QolParking.IsExtraPlace(placeNo), Is.EqualTo(extra), "engine stands (-1, -2) and garage places stay synced");
}