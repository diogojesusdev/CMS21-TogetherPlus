using System;
using System.Collections.Generic;

namespace CMS21Together.Shared.Economy;

/// <summary>
/// Host side of the things that may happen only once (Phase 6, plan §3.8): accepting an order, handing it over and
/// selling a car. A player asks first; the first ask wins, pays its credit into the shared wallet, and every later ask
/// for the same order or car is refused, so two players clicking at once never spawn a car twice or get paid twice.
/// Transport-agnostic; main thread only.
/// </summary>
public sealed class Payouts
{
	/// <summary>Remembered decisions (oldest forgotten first).</summary>
	public const int Remembered = 4096;

	/// <summary>A car slot gets new cars later: a sale only blocks the same slot for this long.</summary>
	public const long SaleHoldMs = 10_000;

	/// <summary>Parking is one player at a time for this long (the parked car reaches everyone well before).</summary>
	public const long ParkHoldMs = 5_000;

	/// <summary>A hand-over approval is only good this long: the payout is granted when the car is actually handed over.</summary>
	public const long JobEndApprovalMs = 120_000;

	private readonly EconomyServer economy;
	private readonly Func<long> clock;
	private readonly Dictionary<string, long> done = new();
	private readonly Dictionary<(int slot, string id), long> approvals = new();
	private readonly Queue<string> order = new();

	public Payouts(EconomyServer economy, Func<long> clock)
	{
		this.economy = economy;
		this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
	}

	public static bool Handles(PurchaseKind kind) =>
		kind is PurchaseKind.JobAccept or PurchaseKind.JobEnd or PurchaseKind.CarSell or PurchaseKind.AcceptCancel or PurchaseKind.ParkIn or PurchaseKind.ParkOut;

	private static string KeyOf(PurchaseKind kind, string id) => kind switch
	{
		PurchaseKind.JobAccept or PurchaseKind.AcceptCancel => "accept/" + id,
		PurchaseKind.JobEnd => "end/" + id,
		PurchaseKind.ParkIn => "park",
		PurchaseKind.ParkOut => "out/" + id,
		_ => "sell/" + id
	};

	/// <summary>True once the order or car was approved (and has not expired).</summary>
	public bool Done(PurchaseKind kind, string id) => done.TryGetValue(KeyOf(kind, id), out var until) && until > clock();

	/// <summary>
	/// Decides a player's ask. <paramref name="amount"/> is the credit (order payout or sale price); it goes into the
	/// shared wallet only when <paramref name="paid"/> (campaign; the sandbox has no money).
	/// </summary>
	public TxnStatus Decide(int slot, PurchaseKind kind, string id, long amount, bool paid)
	{
		if (!Handles(kind) || string.IsNullOrEmpty(id) || amount < 0) return TxnStatus.Invalid;
		var key = KeyOf(kind, id);
		if (kind == PurchaseKind.AcceptCancel)
		{
			// The winner could not accept after all (its order window closed): the order is free again.
			if (amount != 0) return TxnStatus.Invalid;
			if (!done.TryGetValue(key + "#by", out var by) || by != slot) return TxnStatus.Invalid;
			done.Remove(key);
			done.Remove(key + "#by");
			return TxnStatus.Applied;
		}
		if (kind is PurchaseKind.JobAccept or PurchaseKind.ParkIn or PurchaseKind.ParkOut && amount != 0) return TxnStatus.Invalid;

		var now = clock();
		if (done.TryGetValue(key, out var until) && until > now) return TxnStatus.Invalid;
		if (kind == PurchaseKind.JobEnd)
		{
			// Approve only: the money is granted by CompleteEnd when EndJobPacket proves the car was handed over.
			// This used to pay on the ask and store MaxValue, so a lost answer paid once and blocked the order forever.
			if (amount > EconomyLedger.MaxMoneyDelta) return TxnStatus.Invalid;
			PurgeApprovals(now);
			if (ApprovalHeld(id, now)) return TxnStatus.Invalid;
			approvals[(slot, id)] = now + JobEndApprovalMs;
			return TxnStatus.Applied;
		}
		var status = !paid || amount == 0 ? TxnStatus.Applied : economy?.Grant(slot, amount) ?? TxnStatus.NotReady;
		if (status != TxnStatus.Applied) return status;
		Remember(key, kind switch
		{
			PurchaseKind.CarSell or PurchaseKind.ParkOut => now + SaleHoldMs,
			PurchaseKind.ParkIn => now + ParkHoldMs,
			_ => long.MaxValue
		});
		if (kind == PurchaseKind.JobAccept) Remember(key + "#by", slot);
		return status;
	}

	/// <summary>Grants the approved hand-over's payout once, when the hand-over is actually applied.</summary>
	public TxnStatus CompleteEnd(int slot, string id, long amount, bool paid)
	{
		if (string.IsNullOrEmpty(id) || amount < 0 || amount > EconomyLedger.MaxMoneyDelta) return TxnStatus.Invalid;
		var now = clock();
		PurgeApprovals(now);
		if (!approvals.TryGetValue((slot, id), out var until) || until < now) return TxnStatus.Invalid;
		var status = !paid || amount == 0 ? TxnStatus.Applied : economy?.Grant(slot, amount) ?? TxnStatus.NotReady;
		if (status != TxnStatus.Applied)
		{
			// No wallet yet: keep the approval so the hand-over can be retried.
			approvals[(slot, id)] = now + JobEndApprovalMs;
			return status;
		}
		approvals.Remove((slot, id));
		Remember(KeyOf(PurchaseKind.JobEnd, id), long.MaxValue);
		return status;
	}

	private bool ApprovalHeld(string id, long now)
	{
		foreach (var pair in approvals)
			if (pair.Key.id == id && pair.Value > now) return true;
		return false;
	}

	private void PurgeApprovals(long now)
	{
		if (approvals.Count == 0) return;
		var expired = new System.Collections.Generic.List<(int, string)>();
		foreach (var pair in approvals)
			if (pair.Value < now) expired.Add(pair.Key);
		foreach (var key in expired) approvals.Remove(key);
	}

	private void Remember(string key, long value)
	{
		if (!done.ContainsKey(key)) order.Enqueue(key);
		done[key] = value;
		while (order.Count > Remembered) done.Remove(order.Dequeue());
	}
}
