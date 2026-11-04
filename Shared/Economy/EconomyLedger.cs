using System;
using System.Collections.Generic;
using System.Linq;

namespace CMS21Together.Shared.Economy;

public enum TxnStatus
{
	None,
	Applied,
	Insufficient,
	Invalid,
	NotReady
}

/// <summary>One processed transaction, kept for diagnostics (applied or not).</summary>
public readonly struct LedgerEntry
{
	public LedgerEntry(long seq, int slot, long money, long scrap, TxnStatus status, long moneyAfter, long scrapAfter)
	{
		Seq = seq;
		Slot = slot;
		Money = money;
		Scrap = scrap;
		Status = status;
		MoneyAfter = moneyAfter;
		ScrapAfter = scrapAfter;
	}

	public long Seq { get; }
	public int Slot { get; }
	public long Money { get; }
	public long Scrap { get; }
	public TxnStatus Status { get; }
	public long MoneyAfter { get; }
	public long ScrapAfter { get; }

	public override string ToString() => $"#{Seq} by {Slot}: {Money:+#;-#;0} money, {Scrap:+#;-#;0} scrap -> {Status} ({MoneyAfter}/{ScrapAfter})";
}

/// <summary>A ledger entry as stored in the host's save (field names are part of the save format).</summary>
[Serializable]
public sealed class LedgerRecord
{
	public long seq;
	public int slot;
	public long money;
	public long scrap;
	public int status;
	public long moneyAfter;
	public long scrapAfter;

	public static LedgerRecord From(LedgerEntry entry) => new()
	{
		seq = entry.Seq,
		slot = entry.Slot,
		money = entry.Money,
		scrap = entry.Scrap,
		status = (int)entry.Status,
		moneyAfter = entry.MoneyAfter,
		scrapAfter = entry.ScrapAfter
	};

	/// <summary>False for a record that cannot come from a ledger (damaged or edited save).</summary>
	public bool TryToEntry(out LedgerEntry entry)
	{
		entry = default;
		if (seq < 0 || status < 0 || status > (int)TxnStatus.NotReady || moneyAfter < 0 || scrapAfter < 0) return false;
		entry = new LedgerEntry(seq, slot, money, scrap, (TxnStatus)status, moneyAfter, scrapAfter);
		return true;
	}
}

/// <summary>
/// The shared wallet (money and scrap) on the host. Every change is a transaction that is applied whole or not at all;
/// balances never go negative and never leave the range the game can store.
/// </summary>
public sealed class EconomyLedger
{
	/// <summary>The game keeps both balances in an int.</summary>
	public const long MaxBalance = int.MaxValue;
	public const long MaxMoneyDelta = 100_000_000;
	public const long MaxScrapDelta = 10_000_000;
	public const int HistorySize = 200;

	private readonly Queue<LedgerEntry> history = new();

	public bool Initialized { get; private set; }
	public long Money { get; private set; }
	public long Scrap { get; private set; }

	/// <summary>Number of applied transactions; every client converges on the same value.</summary>
	public long Seq { get; private set; }

	public IEnumerable<LedgerEntry> History => history;

	/// <summary>Starting balances from the host's loaded save. Only the first call counts.</summary>
	public bool Initialize(long money, long scrap)
	{
		if (Initialized) return false;
		Money = Clamp(money);
		Scrap = Clamp(scrap);
		Initialized = true;
		return true;
	}

	/// <summary>
	/// Puts the history saved with the host's save back, before the wallet starts (Phase 3 follow-up). Sequence numbers
	/// continue from the last applied entry. Returns false once the wallet has started.
	/// </summary>
	public bool Restore(IEnumerable<LedgerEntry> entries)
	{
		if (Initialized || Seq != 0 || entries == null) return false;
		var restored = entries.ToList();
		var later = history.ToList();
		history.Clear();
		foreach (var entry in restored) Remember(entry);
		foreach (var entry in later) Remember(entry);
		Seq = restored.Where(e => e.Status == TxnStatus.Applied).Select(e => e.Seq).DefaultIfEmpty(0).Max();
		return true;
	}

	/// <summary>Balances after the last applied entry, or null when there is none (to spot money changed outside the ledger).</summary>
	public (long money, long scrap)? LastAppliedBalance()
	{
		LedgerEntry? last = null;
		foreach (var entry in history)
			if (entry.Status == TxnStatus.Applied)
				last = entry;
		return last.HasValue ? (last.Value.MoneyAfter, last.Value.ScrapAfter) : null;
	}

	public TxnStatus Apply(int slot, long money, long scrap)
	{
		var status = Check(money, scrap);
		if (status == TxnStatus.Applied)
		{
			Money += money;
			Scrap += scrap;
			Seq++;
		}
		Remember(new LedgerEntry(Seq, slot, money, scrap, status, Money, Scrap));
		return status;
	}

	private TxnStatus Check(long money, long scrap)
	{
		if (!Initialized) return TxnStatus.NotReady;
		if (money == 0 && scrap == 0) return TxnStatus.Invalid;
		if (money > MaxMoneyDelta || money < -MaxMoneyDelta || scrap > MaxScrapDelta || scrap < -MaxScrapDelta) return TxnStatus.Invalid;
		var newMoney = Money + money;
		var newScrap = Scrap + scrap;
		if (newMoney < 0 || newScrap < 0) return TxnStatus.Insufficient;
		if (newMoney > MaxBalance || newScrap > MaxBalance) return TxnStatus.Invalid;
		return TxnStatus.Applied;
	}

	private void Remember(LedgerEntry entry)
	{
		history.Enqueue(entry);
		while (history.Count > HistorySize) history.Dequeue();
	}

	private static long Clamp(long value) => value < 0 ? 0 : value > MaxBalance ? MaxBalance : value;
}