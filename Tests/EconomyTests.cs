using System.Collections.Generic;
using System.Linq;
using CMS21Together.Shared;
using CMS21Together.Shared.Economy;
using CMS21Together.Shared.Net;
using NUnit.Framework;

namespace CMS21Together.Tests;

[TestFixture]
public class EconomyLedgerTests
{
	[Test]
	public void Refuses_everything_until_initialized()
	{
		var ledger = new EconomyLedger();
		Assert.That(ledger.Apply(1, 100, 0), Is.EqualTo(TxnStatus.NotReady));
		Assert.That(ledger.Money, Is.Zero);
		Assert.That(ledger.Seq, Is.Zero);
	}

	[Test]
	public void Only_the_first_initialization_counts_and_it_is_clamped()
	{
		var ledger = new EconomyLedger();
		Assert.That(ledger.Initialize(-5, long.MaxValue), Is.True);
		Assert.That(ledger.Money, Is.Zero);
		Assert.That(ledger.Scrap, Is.EqualTo(EconomyLedger.MaxBalance));
		Assert.That(ledger.Initialize(1000, 10), Is.False);
		Assert.That(ledger.Money, Is.Zero);
	}

	[Test]
	public void Applies_credits_and_debits_and_counts_them()
	{
		var ledger = Ready(1000, 50);
		Assert.That(ledger.Apply(1, -300, 0), Is.EqualTo(TxnStatus.Applied));
		Assert.That(ledger.Apply(2, 500, -50), Is.EqualTo(TxnStatus.Applied));
		Assert.That((ledger.Money, ledger.Scrap, ledger.Seq), Is.EqualTo((1200L, 0L, 2L)));
	}

	[Test]
	public void Never_goes_negative_and_applies_all_or_nothing()
	{
		var ledger = Ready(100, 5);
		Assert.That(ledger.Apply(1, -101, 0), Is.EqualTo(TxnStatus.Insufficient));
		Assert.That(ledger.Apply(1, -50, -6), Is.EqualTo(TxnStatus.Insufficient));
		Assert.That((ledger.Money, ledger.Scrap, ledger.Seq), Is.EqualTo((100L, 5L, 0L)));
		Assert.That(ledger.Apply(1, -100, -5), Is.EqualTo(TxnStatus.Applied));
		Assert.That((ledger.Money, ledger.Scrap), Is.EqualTo((0L, 0L)));
	}

	[TestCase(0L, 0L)]
	[TestCase(EconomyLedger.MaxMoneyDelta + 1, 0L)]
	[TestCase(-EconomyLedger.MaxMoneyDelta - 1, 0L)]
	[TestCase(0L, EconomyLedger.MaxScrapDelta + 1)]
	[TestCase(0L, -EconomyLedger.MaxScrapDelta - 1)]
	public void Refuses_empty_and_out_of_range_changes(long money, long scrap)
	{
		var ledger = Ready(EconomyLedger.MaxMoneyDelta * 2, EconomyLedger.MaxScrapDelta * 2);
		Assert.That(ledger.Apply(1, money, scrap), Is.EqualTo(TxnStatus.Invalid));
		Assert.That(ledger.Seq, Is.Zero);
	}

	[Test]
	public void Refuses_a_balance_the_game_cannot_store()
	{
		var ledger = Ready(EconomyLedger.MaxBalance - 10, 0);
		Assert.That(ledger.Apply(1, 11, 0), Is.EqualTo(TxnStatus.Invalid));
		Assert.That(ledger.Apply(1, 10, 0), Is.EqualTo(TxnStatus.Applied));
		Assert.That(ledger.Money, Is.EqualTo(EconomyLedger.MaxBalance));
	}

	[Test]
	public void History_keeps_the_latest_entries_including_refusals()
	{
		var ledger = Ready(0, 0);
		ledger.Apply(3, -1, 0);
		Assert.That(ledger.History.Single().Status, Is.EqualTo(TxnStatus.Insufficient));
		for (var i = 0; i < EconomyLedger.HistorySize + 20; i++) ledger.Apply(1, 1, 0);
		Assert.That(ledger.History.Count(), Is.EqualTo(EconomyLedger.HistorySize));
		Assert.That(ledger.History.Last().MoneyAfter, Is.EqualTo(EconomyLedger.HistorySize + 20));
		Assert.That(ledger.History.All(e => e.Status == TxnStatus.Applied), Is.True);
	}

	private static EconomyLedger Ready(long money, long scrap)
	{
		var ledger = new EconomyLedger();
		ledger.Initialize(money, scrap);
		return ledger;
	}
}

[TestFixture]
public class EconomyWireTests
{
	[Test]
	public void Round_trips_transactions_and_balances()
	{
		using var txn = new Packet((int)PacketTypes.econTxn);
		EconomyWire.WriteTxn(txn, 7, -123456789012, 42);
		Assert.That(EconomyWire.ReadTxn(Reread(txn)), Is.EqualTo((7, -123456789012L, 42L)));

		using var balance = new Packet((int)PacketTypes.econBalance);
		EconomyWire.WriteBalance(balance, 5000, 12, 99, 3, TxnStatus.Insufficient);
		Assert.That(EconomyWire.ReadBalance(Reread(balance)), Is.EqualTo((5000L, 12L, 99L, 3, TxnStatus.Insufficient)));
	}

	[Test]
	public void Rejects_malformed_payloads()
	{
		using var txn = new Packet((int)PacketTypes.econTxn);
		EconomyWire.WriteTxn(txn, 0, 1, 0);
		Assert.Throws<NetFormatException>(() => EconomyWire.ReadTxn(Reread(txn)));

		using var negative = new Packet((int)PacketTypes.econBalance);
		EconomyWire.WriteBalance(negative, -1, 0, 0, 0, TxnStatus.None);
		Assert.Throws<NetFormatException>(() => EconomyWire.ReadBalance(Reread(negative)));

		using var status = new Packet((int)PacketTypes.econBalance);
		EconomyWire.WriteBalance(status, 1, 0, 0, 0, (TxnStatus)99);
		Assert.Throws<NetFormatException>(() => EconomyWire.ReadBalance(Reread(status)));
	}

	private static Packet Reread(Packet p) => new(p.Id, p.PayloadToArray());
}

/// <summary><see cref="EconomyServer"/> and <see cref="EconomyClient"/> talking through in-memory queues (packets really serialized).</summary>
[TestFixture]
public class EconomyProtocolTests
{
	private EconomyServer server;
	private Dictionary<int, EconomyClient> clients;
	private Queue<(int slot, Packet packet)> toServer;
	private Queue<(int slot, Packet packet)> toClients;
	private List<(int slot, TxnStatus status)> rejections;

	[SetUp]
	public void SetUp()
	{
		clients = new Dictionary<int, EconomyClient>();
		toServer = new Queue<(int, Packet)>();
		toClients = new Queue<(int, Packet)>();
		rejections = new List<(int, TxnStatus)>();
		server = new EconomyServer((slot, p) => toClients.Enqueue((slot, Copy(p))), () => clients.Keys.ToList());
		Join(1);
		Join(2);
		Join(3);
	}

	private static Packet Copy(Packet p) => new(p.Id, p.PayloadToArray());

	private EconomyClient Join(int slot)
	{
		var client = new EconomyClient(p => toServer.Enqueue((slot, Copy(p))));
		client.Rejected += status => rejections.Add((slot, status));
		clients[slot] = client;
		server.PlayerJoined(slot);
		return client;
	}

	private void ToServer()
	{
		while (toServer.Count > 0)
		{
			var (slot, p) = toServer.Dequeue();
			Assert.That(p.Id, Is.EqualTo((int)PacketTypes.econTxn));
			server.HandleTxn(slot, p);
		}
	}

	private void ToClients()
	{
		while (toClients.Count > 0)
		{
			var (slot, p) = toClients.Dequeue();
			Assert.That(p.Id, Is.EqualTo((int)PacketTypes.econBalance));
			if (clients.TryGetValue(slot, out var client)) client.HandleBalance(p);
		}
	}

	private void Deliver()
	{
		while (toServer.Count > 0 || toClients.Count > 0)
		{
			ToServer();
			ToClients();
		}
	}

	private void Start(long money = 1000, long scrap = 10)
	{
		server.Initialize(money, scrap);
		Deliver();
	}

	[Test]
	public void Nothing_is_sent_before_the_first_balance()
	{
		Assert.That(clients[1].Record(-100, 0), Is.False);
		Assert.That(toServer, Is.Empty);
		Start();
		Assert.That(clients.Values.All(c => c.Synced && c.Money == 1000 && c.Scrap == 10), Is.True);
	}

	[Test]
	public void A_late_joiner_gets_the_current_balance()
	{
		Start();
		clients[1].Record(-400, 0);
		Deliver();
		var late = Join(4);
		Deliver();
		Assert.That((late.Synced, late.Money, late.Seq), Is.EqualTo((true, 600L, 1L)));
	}

	[Test]
	public void Local_changes_show_at_once_and_everyone_converges()
	{
		Start();
		clients[1].Record(-300, 0);
		clients[2].Record(250, 5);
		Assert.That(clients[1].Money, Is.EqualTo(700), "own purchase shows before the host answers");
		Assert.That(clients[3].Money, Is.EqualTo(1000));
		Deliver();
		Assert.That(clients.Values.Select(c => (c.Money, c.Scrap, c.Seq, c.PendingCount)).Distinct().Single(), Is.EqualTo((950L, 15L, 2L, 0)));
		Assert.That(server.Ledger.Money, Is.EqualTo(950));
	}

	[Test]
	public void Concurrent_changes_are_not_counted_twice()
	{
		Start();
		// Both players act before either hears from the host; each balance carries only its receiver's progress.
		clients[1].Record(-100, 0);
		clients[2].Record(-200, 0);
		clients[1].Record(50, 0);
		ToServer();
		Assert.That(clients[1].Money, Is.EqualTo(950));
		var first = toClients.Dequeue();
		clients[first.slot].HandleBalance(first.packet);
		ToClients();
		Assert.That(clients.Values.All(c => c.Money == 750 && c.PendingCount == 0), Is.True);
	}

	[Test]
	public void Pending_changes_stay_until_the_host_processed_them()
	{
		Start();
		clients[1].Record(-100, 0);
		ToServer();
		clients[1].Record(-200, 0); // in flight while the first balance comes back
		ToClients();
		Assert.That((clients[1].HostMoney, clients[1].PendingCount, clients[1].Money), Is.EqualTo((900L, 1, 700L)));
		Deliver();
		Assert.That((clients[1].Money, clients[1].PendingCount, clients[2].Money), Is.EqualTo((700L, 0, 700L)));
	}

	[Test]
	public void The_loser_of_a_race_for_the_last_money_is_told_and_corrected()
	{
		Start(100, 0);
		clients[1].Record(-80, 0);
		clients[2].Record(-80, 0);
		Deliver();
		Assert.That(server.Ledger.Money, Is.EqualTo(20));
		Assert.That(rejections, Is.EqualTo(new[] { (2, TxnStatus.Insufficient) }));
		Assert.That(clients.Values.All(c => c.Money == 20 && c.PendingCount == 0), Is.True);
	}

	[Test]
	public void Replayed_transactions_are_ignored()
	{
		Start();
		using (var replay = new Packet((int)PacketTypes.econTxn))
		{
			EconomyWire.WriteTxn(replay, 1, -100, 0);
			server.HandleTxn(2, Copy(replay));
			server.HandleTxn(2, Copy(replay));
		}
		using (var old = new Packet((int)PacketTypes.econTxn))
		{
			EconomyWire.WriteTxn(old, 1, -100, 0);
			server.HandleTxn(2, Copy(old));
		}
		Deliver();
		Assert.That(server.Ledger.Money, Is.EqualTo(900));
		Assert.That(server.Duplicates, Is.EqualTo(2));
	}

	[Test]
	public void A_new_connection_in_the_slot_starts_numbering_over()
	{
		Start();
		clients[2].Record(-100, 0);
		clients[2].Record(-100, 0);
		Deliver();
		clients.Remove(2);
		server.PlayerLeft(2);
		var again = Join(2);
		Deliver();
		Assert.That(again.Record(-100, 0), Is.True);
		Deliver();
		Assert.That((server.Ledger.Money, again.Money, server.Duplicates), Is.EqualTo((700L, 700L, 0)));
	}

	[Test]
	public void Changes_lost_with_a_dropped_connection_are_not_applied_later()
	{
		Start();
		clients[2].Record(-100, 0);
		toServer.Clear(); // the connection died with the transaction in flight
		clients.Remove(2);
		server.PlayerLeft(2);
		var again = Join(2);
		Deliver();
		Assert.That((again.Money, server.Ledger.Money), Is.EqualTo((1000L, 1000L)));
	}

	[Test]
	public void A_runaway_caller_is_capped()
	{
		Start(0, 0);
		for (var i = 0; i < EconomyClient.MaxPending; i++) Assert.That(clients[1].Record(1, 0), Is.True);
		Assert.That(clients[1].Record(1, 0), Is.False);
		Deliver();
		Assert.That((server.Ledger.Money, clients[3].Money), Is.EqualTo(((long)EconomyClient.MaxPending, (long)EconomyClient.MaxPending)));
	}

	[Test]
	public void The_shown_balance_stays_in_the_games_range()
	{
		Start(EconomyLedger.MaxBalance - 5, 0);
		clients[1].Record(100, 0);
		Assert.That(clients[1].Money, Is.EqualTo(EconomyLedger.MaxBalance));
		Deliver();
		Assert.That(rejections, Is.EqualTo(new[] { (1, TxnStatus.Invalid) }));
		Assert.That(clients[1].Money, Is.EqualTo(EconomyLedger.MaxBalance - 5));
	}
}
