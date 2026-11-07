using System;
using System.Collections.Generic;
using System.Linq;
using CMS21Together.Shared;
using CMS21Together.Shared.Economy;
using CMS21Together.Shared.Net;
using NUnit.Framework;

namespace CMS21Together.Tests;

/// <summary>Phase 6: accepting and handing over an order and selling a car happen once, and nobody is paid twice.</summary>
[TestFixture]
public class PayoutsTests
{
	private long now;
	private EconomyServer economy;
	private Payouts payouts;
	private readonly List<int> slots = new() { 1, 2, 3, 4, 5, 6, 7, 8 };

	[SetUp]
	public void SetUp()
	{
		now = 1000;
		economy = new EconomyServer((_, _) => { }, () => slots);
		economy.Initialize(500, 0);
		payouts = new Payouts(economy, () => now);
	}

	[TestCase(2, 1)]
	[TestCase(8, 2)]
	[TestCase(5, 3)]
	public void RacingHandOversPayTheOrderOnce(int players, int seed)
	{
		var random = new Random(seed);
		for (var job = 1; job <= 200; job++)
		{
			var order = Enumerable.Range(1, players).OrderBy(_ => random.Next()).ToList();
			var winner = order.FirstOrDefault(slot => payouts.Decide(slot, PurchaseKind.JobEnd, job.ToString(), 1000, true) == TxnStatus.Applied);
			Assert.That(winner, Is.GreaterThan(0), $"order {job} was approved");
			Assert.That(payouts.Done(PurchaseKind.JobEnd, job.ToString()), Is.False, "the payout waits for the hand-over");
			Assert.That(payouts.CompleteEnd(winner, job.ToString(), true, out _), Is.EqualTo(TxnStatus.Applied));
			foreach (var other in order.Where(slot => slot != winner))
				Assert.That(payouts.CompleteEnd(other, job.ToString(), true, out _), Is.EqualTo(TxnStatus.Invalid),
					$"order {job} was only approved for its winner");
			Assert.That(payouts.Done(PurchaseKind.JobEnd, job.ToString()), Is.True);
		}
		Assert.That(economy.Ledger.Money, Is.EqualTo(500 + 200 * 1000 * Payouts.OrderPayoutMultiplier), "every order paid exactly once");
	}

	[Test]
	public void OnlyOnePlayerAcceptsAnOrderUntilTheWinnerGivesItBack()
	{
		Assert.That(payouts.Decide(2, PurchaseKind.JobAccept, "7", 0, true), Is.EqualTo(TxnStatus.Applied));
		Assert.That(payouts.Decide(3, PurchaseKind.JobAccept, "7", 0, true), Is.EqualTo(TxnStatus.Invalid));
		Assert.That(payouts.Decide(3, PurchaseKind.AcceptCancel, "7", 0, true), Is.EqualTo(TxnStatus.Invalid), "only the winner gives it back");
		Assert.That(payouts.Decide(2, PurchaseKind.AcceptCancel, "7", 0, true), Is.EqualTo(TxnStatus.Applied));
		Assert.That(payouts.Decide(3, PurchaseKind.JobAccept, "7", 0, true), Is.EqualTo(TxnStatus.Applied));
		Assert.That(payouts.Decide(3, PurchaseKind.JobAccept, "7", 5, true), Is.EqualTo(TxnStatus.Invalid), "accepting pays nothing");
		Assert.That(economy.Ledger.Money, Is.EqualTo(500));
	}

	[Test]
	public void ACarSlotIsSoldOnceThenFreesForTheNextCar()
	{
		Assert.That(payouts.Decide(1, PurchaseKind.CarSell, "3", 2500, true), Is.EqualTo(TxnStatus.Applied));
		Assert.That(payouts.Decide(2, PurchaseKind.CarSell, "3", 2500, true), Is.EqualTo(TxnStatus.Invalid));
		now += Payouts.SaleHoldMs - 1;
		Assert.That(payouts.Decide(2, PurchaseKind.CarSell, "3", 2500, true), Is.EqualTo(TxnStatus.Invalid));
		now += 2;
		Assert.That(payouts.Decide(2, PurchaseKind.CarSell, "3", 2500, true), Is.EqualTo(TxnStatus.Applied), "a later car in the slot sells");
		Assert.That(economy.Ledger.Money, Is.EqualTo(5500));
	}

	[Test]
	public void SandboxDecidesWithoutMoneyAndBadAsksAreRefused()
	{
		Assert.That(payouts.Decide(1, PurchaseKind.JobEnd, "9", 1000, false), Is.EqualTo(TxnStatus.Applied));
		Assert.That(economy.Ledger.Money, Is.EqualTo(500), "the payout is granted when the hand-over is applied");
		Assert.That(payouts.CompleteEnd(1, "9", false, out _), Is.EqualTo(TxnStatus.Applied));
		Assert.That(economy.Ledger.Money, Is.EqualTo(500), "sandbox pays nothing");
		Assert.That(payouts.Decide(1, PurchaseKind.JobEnd, "10", -1, true), Is.EqualTo(TxnStatus.Invalid));
		Assert.That(payouts.Decide(1, PurchaseKind.Upgrade, "11", 0, true), Is.EqualTo(TxnStatus.Invalid));
		Assert.That(payouts.Decide(1, PurchaseKind.JobEnd, "", 0, true), Is.EqualTo(TxnStatus.Invalid));
		Assert.That(payouts.Done(PurchaseKind.JobEnd, "10"), Is.False, "a refused hand-over can be retried");
	}

	[Test]
	public void AnOverflowingPayoutIsRefusedAndTheOrderStaysOpen()
	{
		Assert.That(payouts.Decide(1, PurchaseKind.JobEnd, "1", EconomyLedger.MaxMoneyDelta + 1, true), Is.EqualTo(TxnStatus.Invalid));
		Assert.That(payouts.Done(PurchaseKind.JobEnd, "1"), Is.False);
		Assert.That(payouts.Decide(2, PurchaseKind.JobEnd, "1", 10, true), Is.EqualTo(TxnStatus.Applied));
		Assert.That(payouts.Done(PurchaseKind.JobEnd, "1"), Is.False, "the payout is paid on completion");
		Assert.That(payouts.CompleteEnd(2, "1", true, out _), Is.EqualTo(TxnStatus.Applied));
		Assert.That(payouts.Done(PurchaseKind.JobEnd, "1"), Is.True);
		var noWallet = new Payouts(null, () => now);
		Assert.That(noWallet.Decide(1, PurchaseKind.JobEnd, "2", 10, true), Is.EqualTo(TxnStatus.Applied));
		Assert.That(noWallet.CompleteEnd(1, "2", true, out _), Is.EqualTo(TxnStatus.NotReady), "no wallet yet: retry later");
	}

	[Test]
	public void TheHandOverPaysTheAmountTheHostApproved()
	{
		Assert.That(payouts.Decide(2, PurchaseKind.JobEnd, "20", 2106, true), Is.EqualTo(TxnStatus.Applied));
		Assert.That(payouts.CompleteEnd(2, "20", true, out var paid), Is.EqualTo(TxnStatus.Applied));
		Assert.That(paid, Is.EqualTo(2106 * Payouts.OrderPayoutMultiplier));
		Assert.That(economy.Ledger.Money, Is.EqualTo(500 + 2106 * Payouts.OrderPayoutMultiplier), "the order's payout reached the shared wallet");
	}

	[Test]
	public void TheMultipliedPayoutIsWhatGetsRangeChecked()
	{
		var limit = EconomyLedger.MaxMoneyDelta / Payouts.OrderPayoutMultiplier;
		Assert.That(payouts.Decide(1, PurchaseKind.JobEnd, "30", limit + 1, true), Is.EqualTo(TxnStatus.Invalid));
		Assert.That(payouts.Decide(1, PurchaseKind.JobEnd, "30", limit, true), Is.EqualTo(TxnStatus.Applied));
	}

	[Test]
	public void AHandOverApprovalExpiresAndCanBeRetried()
	{
		Assert.That(payouts.Decide(2, PurchaseKind.JobEnd, "12", 10, true), Is.EqualTo(TxnStatus.Applied));
		Assert.That(payouts.Decide(3, PurchaseKind.JobEnd, "12", 10, true), Is.EqualTo(TxnStatus.Invalid), "one hand-over at a time");
		now += Payouts.JobEndApprovalMs + 1;
		Assert.That(payouts.CompleteEnd(2, "12", true, out _), Is.EqualTo(TxnStatus.Invalid), "the old approval expired");
		Assert.That(payouts.Decide(3, PurchaseKind.JobEnd, "12", 10, true), Is.EqualTo(TxnStatus.Applied));
		Assert.That(payouts.CompleteEnd(3, "12", true, out _), Is.EqualTo(TxnStatus.Applied));
		Assert.That(payouts.Done(PurchaseKind.JobEnd, "12"), Is.True);
	}

	[Test]
	public void GrantOnlyCredits()
	{
		Assert.That(economy.Grant(1, -5), Is.EqualTo(TxnStatus.Invalid));
		Assert.That(economy.Grant(1, 5), Is.EqualTo(TxnStatus.Applied));
		Assert.That(economy.Ledger.Money, Is.EqualTo(505));
	}

	[TestCase(PurchaseKind.JobAccept)]
	[TestCase(PurchaseKind.JobEnd)]
	[TestCase(PurchaseKind.CarSell)]
	[TestCase(PurchaseKind.AcceptCancel)]
	[TestCase(PurchaseKind.ParkIn)]
	[TestCase(PurchaseKind.ParkOut)]
	public void NewAskKindsRoundTrip(PurchaseKind kind)
	{
		using var packet = new Packet((int)PacketTypes.purchaseAsk);
		PurchaseWire.WriteAsk(packet, 4, kind, "12", 3, 900);
		using var read = new Packet(packet.Id, packet.PayloadToArray());
		Assert.That(PurchaseWire.ReadAsk(read), Is.EqualTo((4, kind, "12", 3, 900L)));
	}

	[Test]
	public void UnknownAskKindIsAFormatError()
	{
		using var packet = new Packet((int)PacketTypes.purchaseAsk);
		PurchaseWire.WriteAsk(packet, 4, (PurchaseKind)8, "12", 3, 900);
		using var read = new Packet(packet.Id, packet.PayloadToArray());
		Assert.Throws<NetFormatException>(() => PurchaseWire.ReadAsk(read));
	}

	[Test]
	public void OnePlayerParksAtATimeAndASpaceIsEmptiedOnce()
	{
		Assert.That(payouts.Decide(2, PurchaseKind.ParkIn, "park", 0, true), Is.EqualTo(TxnStatus.Applied));
		Assert.That(payouts.Decide(3, PurchaseKind.ParkIn, "park", 0, true), Is.EqualTo(TxnStatus.Invalid), "another player is parking");
		now += Payouts.ParkHoldMs;
		Assert.That(payouts.Decide(3, PurchaseKind.ParkIn, "park", 0, true), Is.EqualTo(TxnStatus.Applied));

		Assert.That(payouts.Decide(2, PurchaseKind.ParkOut, "4", 0, true), Is.EqualTo(TxnStatus.Applied));
		Assert.That(payouts.Decide(3, PurchaseKind.ParkOut, "4", 0, true), Is.EqualTo(TxnStatus.Invalid), "the car was already taken out");
		Assert.That(payouts.Decide(3, PurchaseKind.ParkOut, "5", 0, true), Is.EqualTo(TxnStatus.Applied), "another space");
		Assert.That(payouts.Decide(3, PurchaseKind.ParkOut, "6", 10, true), Is.EqualTo(TxnStatus.Invalid), "parking moves no money");
		Assert.That(economy.Ledger.Money, Is.EqualTo(500));
	}
}