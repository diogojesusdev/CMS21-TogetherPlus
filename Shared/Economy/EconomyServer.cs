using System;
using System.Collections.Generic;

namespace CMS21Together.Shared.Economy;

/// <summary>
/// Host side of the shared wallet (audit A7). Clients send numbered transactions; each one is applied at most once,
/// in order, and every player gets the absolute balance plus how far their own transactions got. Transport-agnostic;
/// main thread only.
/// </summary>
public sealed class EconomyServer
{
	private readonly Action<int, Packet> send;
	private readonly Func<IEnumerable<int>> players;
	private readonly Dictionary<int, int> processed = new();
	private readonly Dictionary<int, Dictionary<int, TxnStatus>> decisions = new();
	private readonly Dictionary<int, Queue<int>> decisionOrder = new();
	private readonly Dictionary<int, Dictionary<int, long>> debits = new();

	/// <summary>Decisions remembered per player (purchases are checked against their debit moments later).</summary>
	public const int RememberedDecisions = 1024;

	/// <param name="send">Reliable send to one slot.</param>
	/// <param name="players">Slots that are in the session (validated).</param>
	public EconomyServer(Action<int, Packet> send, Func<IEnumerable<int>> players)
	{
		this.send = send ?? throw new ArgumentNullException(nameof(send));
		this.players = players ?? throw new ArgumentNullException(nameof(players));
	}

	public EconomyLedger Ledger { get; } = new();

	/// <summary>Replayed or out-of-order transaction numbers that were ignored (diagnostics and tests).</summary>
	public int Duplicates { get; private set; }

	/// <summary>Starting balances from the host's loaded save; everybody already in the session gets them.</summary>
	/// <param name="history">Ledger history saved with the host's save (restored before the wallet starts).</param>
	public void Initialize(long money, long scrap, IEnumerable<LedgerRecord> history = null)
	{
		if (Ledger.Initialized) return;
		if (history != null)
		{
			var entries = new List<LedgerEntry>();
			var damaged = 0;
			foreach (var record in history)
				if (record != null && record.TryToEntry(out var entry)) entries.Add(entry);
				else damaged++;
			if (damaged > 0) Log.Warn($"[Economy] Ignored {damaged} damaged ledger entries in the save.");
			if (Ledger.Restore(entries) && entries.Count > 0)
			{
				var last = Ledger.LastAppliedBalance();
				if (last.HasValue && (last.Value.money != money || last.Value.scrap != scrap))
					Log.Info($"[Economy] The save holds {money}/{scrap}; the ledger history ended at {last.Value.money}/{last.Value.scrap} (changed outside the ledger).");
			}
		}
		if (!Ledger.Initialize(money, scrap)) return;
		Log.Info($"[Economy] Shared wallet: {Ledger.Money} money, {Ledger.Scrap} scrap (ledger at #{Ledger.Seq}).");
		foreach (var slot in players()) SendBalance(slot, TxnStatus.None);
	}

	/// <summary>A new connection in the slot: its transaction numbers start over.</summary>
	public void PlayerJoined(int slot)
	{
		processed[slot] = 0;
		decisions[slot] = new Dictionary<int, TxnStatus>();
		decisionOrder[slot] = new Queue<int>();
		debits[slot] = new Dictionary<int, long>();
		if (Ledger.Initialized) SendBalance(slot, TxnStatus.None);
	}

	public void PlayerLeft(int slot)
	{
		processed.Remove(slot);
		decisions.Remove(slot);
		decisionOrder.Remove(slot);
		debits.Remove(slot);
	}

	/// <summary>What became of a player's transaction on this connection (None while unknown).</summary>
	public TxnStatus StatusOf(int slot, int number) =>
		decisions.TryGetValue(slot, out var map) && map.TryGetValue(number, out var status) ? status : TxnStatus.None;

	/// <summary>A purchase linked to transaction <paramref name="number"/> may happen only if that debit was applied (Phase 6).</summary>
	public bool Charged(int slot, int number) => StatusOf(slot, number) == TxnStatus.Applied;

	/// <summary>
	/// A pre-authorized purchase (Phase 6): the host charges the player before the game does anything, and everyone
	/// gets the new balance. The player's own game then applies the purchase without recording a debit.
	/// </summary>
	public TxnStatus Charge(int slot, long money, long scrap = 0)
	{
		if (money > 0 || scrap > 0) return TxnStatus.Invalid;
		var status = Ledger.Apply(slot, money, scrap);
		if (status == TxnStatus.Applied)
			foreach (var player in players()) SendBalance(player, TxnStatus.None);
		return status;
	}

	/// <summary>
	/// Gives back an applied debit whose purchase could not be delivered (Phase 6b: a car bought away with no free
	/// parking space). Only the amount the ledger charged, and only once; returns the amount refunded (0 when none).
	/// </summary>
	/// <summary>The money an applied debit charged (negative), or 0 when unknown or not a debit.</summary>
	public long DebitOf(int slot, int number) => debits.TryGetValue(slot, out var map) && map.TryGetValue(number, out var money) ? money : 0;

	public long Refund(int slot, int number)
	{
		if (!debits.TryGetValue(slot, out var map) || !map.TryGetValue(number, out var money)) return 0;
		map.Remove(number);
		return Grant(slot, -money) == TxnStatus.Applied ? -money : 0;
	}

	/// <summary>A credit decided by the host itself (an order handed over, a car sold; Phase 6), shown to everyone.</summary>
	public TxnStatus Grant(int slot, long money, long scrap = 0)
	{
		if (money < 0 || scrap < 0) return TxnStatus.Invalid;
		var status = Ledger.Apply(slot, money, scrap);
		if (status == TxnStatus.Applied)
			foreach (var player in players()) SendBalance(player, TxnStatus.None);
		return status;
	}

	private void Decide(int slot, int number, TxnStatus status)
	{
		if (!decisions.TryGetValue(slot, out var map))
		{
			map = decisions[slot] = new Dictionary<int, TxnStatus>();
			decisionOrder[slot] = new Queue<int>();
		}
		if (!debits.ContainsKey(slot)) debits[slot] = new Dictionary<int, long>();
		map[number] = status;
		var order = decisionOrder[slot];
		order.Enqueue(number);
		while (order.Count > RememberedDecisions)
		{
			var old = order.Dequeue();
			map.Remove(old);
			debits[slot].Remove(old);
		}
	}

	public void HandleTxn(int slot, Packet packet)
	{
		var (number, money, scrap) = EconomyWire.ReadTxn(packet);
		processed.TryGetValue(slot, out var last);
		if (number <= last)
		{
			Duplicates++;
			Log.Debug($"[Economy] Ignored replayed transaction {number} from #{slot} (already at {last}).");
			return;
		}
		processed[slot] = number;

		var status = Ledger.Apply(slot, money, scrap);
		Decide(slot, number, status);
		if (status == TxnStatus.Applied && money < 0 && scrap == 0) debits[slot][number] = money;
		if (status != TxnStatus.Applied)
		{
			Log.Debug($"[Economy] Transaction {number} from #{slot} ({money} money, {scrap} scrap): {status}.");
			SendBalance(slot, status);
			return;
		}
		foreach (var player in players()) SendBalance(player, player == slot ? status : TxnStatus.None);
	}

	private void SendBalance(int slot, TxnStatus status)
	{
		processed.TryGetValue(slot, out var upTo);
		using var packet = new Packet((int)PacketTypes.econBalance);
		EconomyWire.WriteBalance(packet, Ledger.Money, Ledger.Scrap, Ledger.Seq, upTo, status);
		send(slot, packet);
	}
}