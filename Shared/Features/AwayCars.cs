using System;
using System.Collections.Generic;
using System.Linq;
using CMS21Together.Shared.Economy;

namespace CMS21Together.Shared.Features;

/// <summary>
/// Cars bought away from the garage (junkyard, auto salon, barn; Phase 6b, SceneReturnSync). The buyer's game writes
/// the car into its own garage or parking list, which may be stale; instead the car goes to the host, which places it
/// in a free parking space for everyone once it has charged the buyer's debit (review #2 follow-up: never uncharged).
/// </summary>
public static class AwayCars
{
	/// <summary>Indices of <paramref name="after"/> holding a car that was not in <paramref name="before"/> (a move is not a purchase).</summary>
	public static List<int> Added(IReadOnlyList<(string uid, string name)> before, IReadOnlyList<(string uid, string name)> after)
	{
		var result = new List<int>();
		if (after == null) return result;
		before ??= Array.Empty<(string, string)>();
		var known = new HashSet<string>(before.Where(c => !string.IsNullOrEmpty(c.name) && !string.IsNullOrEmpty(c.uid)).Select(c => c.uid));
		for (var i = 0; i < after.Count; i++)
		{
			var car = after[i];
			if (string.IsNullOrEmpty(car.name)) continue;
			if (i < before.Count && before[i].name == car.name && (before[i].uid ?? string.Empty) == (car.uid ?? string.Empty)) continue;
			if (!string.IsNullOrEmpty(car.uid) && known.Contains(car.uid)) continue;
			result.Add(i);
		}
		return result;
	}
}

/// <summary>What became of a car bought away.</summary>
public enum AwayCarOutcome
{
	Parked,
	Refunded,
	Refused,
	TimedOut
}

/// <summary>A car bought away, waiting on the host.</summary>
public sealed class AwayCar<T>
{
	internal AwayCar(int from, int txn, string uid, T car, double since)
	{
		From = from;
		Txn = txn;
		Uid = uid;
		Car = car;
		Since = since;
	}

	public int From { get; }
	public int Txn { get; }
	public string Uid { get; }
	public T Car { get; }
	public double Since { get; }

	/// <summary>
	/// What the debit charged, taken when it was applied (Phase 8 assessment: refunding by transaction number could hit
	/// another debit after the buyer reconnected, as numbers restart per connection).
	/// </summary>
	public long Charge { get; internal set; }

	/// <summary>The debit was applied (or there was none): the car may be placed.</summary>
	public bool Charged { get; internal set; }
}

/// <summary>Host side: away cars wait until their debit is decided; refused ones are dropped, charged ones placed once.</summary>
public sealed class AwayCarQueue<T>
{
	/// <summary>A debit still undecided after this long is not trusted (the buyer left, or the debit was lost).</summary>
	public const double GiveUpSeconds = 60;

	private readonly List<AwayCar<T>> pending = new();
	private readonly HashSet<string> seen = new();

	public int Count => pending.Count;

	/// <summary>Queues a car; false when the same car (by its UId) was already queued or placed.</summary>
	public bool Add(int from, int txn, string uid, T car, double now)
	{
		if (!string.IsNullOrEmpty(uid) && !seen.Add(uid)) return false;
		pending.Add(new AwayCar<T>(from, txn, uid, car, now) { Charged = txn <= 0 });
		return true;
	}

	/// <summary>
	/// Updates every car from its debit's status; refused and timed-out cars are removed and reported. Returns the
	/// charged cars, oldest first; each stays queued until <see cref="Done"/>.
	/// </summary>
	public List<AwayCar<T>> Step(Func<int, int, TxnStatus> status, double now, Action<AwayCar<T>, AwayCarOutcome> dropped, Func<int, int, long> charged = null)
	{
		foreach (var car in pending.ToList())
		{
			if (car.Charged) continue;
			var decided = status(car.From, car.Txn);
			if (decided == TxnStatus.Applied)
			{
				car.Charged = true;
				car.Charge = Math.Max(0, -(charged?.Invoke(car.From, car.Txn) ?? 0));
				continue;
			}
			AwayCarOutcome? outcome = decided switch
			{
				TxnStatus.Insufficient or TxnStatus.Invalid => AwayCarOutcome.Refused,
				_ when now - car.Since >= GiveUpSeconds => AwayCarOutcome.TimedOut,
				_ => null
			};
			if (outcome == null) continue;
			pending.Remove(car);
			dropped?.Invoke(car, outcome.Value);
		}
		return pending.Where(c => c.Charged).ToList();
	}

	public void Done(AwayCar<T> car) => pending.Remove(car);

	public void Clear()
	{
		pending.Clear();
		seen.Clear();
	}
}