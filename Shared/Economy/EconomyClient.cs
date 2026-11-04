using System;
using System.Collections.Generic;

namespace CMS21Together.Shared.Economy;

/// <summary>
/// Client side of the shared wallet. The game spends and earns locally right away; each change is sent to the host
/// as a numbered transaction and kept as pending until the host has processed it. The balance to show is always the
/// host's absolute balance plus this player's pending changes, so nothing is counted twice and every player converges
/// on the host's value.
/// </summary>
public sealed class EconomyClient
{
	/// <summary>Backstop against a runaway caller; real play sends a few transactions per minute.</summary>
	public const int MaxPending = 512;

	private readonly Action<Packet> send;
	private readonly List<(int number, long money, long scrap)> pending = new();
	private int nextNumber;

	public EconomyClient(Action<Packet> send) => this.send = send ?? throw new ArgumentNullException(nameof(send));

	/// <summary>Raised after every balance from the host.</summary>
	public event Action Changed;

	/// <summary>One of this player's transactions was refused; its local effect is undone by the next balance.</summary>
	public event Action<TxnStatus> Rejected;

	/// <summary>Which transaction was refused (purchase rollback, Phase 6).</summary>
	public event Action<int, TxnStatus> RejectedTxn;

	/// <summary>The host's balance arrived at least once; local changes are only sent from then on.</summary>
	public bool Synced { get; private set; }

	public long HostMoney { get; private set; }
	public long HostScrap { get; private set; }
	public long Seq { get; private set; }
	public int Processed { get; private set; }
	public int PendingCount => pending.Count;

	/// <summary>Number of the last transaction sent on this connection (0 before the first).</summary>
	public int LastNumber => nextNumber;

	/// <summary>The balance to show: the host's plus what is still on its way.</summary>
	public long Money
	{
		get
		{
			var total = HostMoney;
			foreach (var txn in pending) total += txn.money;
			return Clamp(total);
		}
	}

	public long Scrap
	{
		get
		{
			var total = HostScrap;
			foreach (var txn in pending) total += txn.scrap;
			return Clamp(total);
		}
	}

	/// <summary>The game changed the wallet locally. Returns false when the change is not sent (it is then overwritten by the host's balance).</summary>
	public bool Record(long money, long scrap)
	{
		if (!Synced || (money == 0 && scrap == 0)) return false;
		if (pending.Count >= MaxPending)
		{
			Log.Warn($"[Economy] {pending.Count} transactions are waiting for the host; dropping a change of {money} money, {scrap} scrap.");
			return false;
		}
		var number = ++nextNumber;
		pending.Add((number, money, scrap));
		using var packet = new Packet((int)PacketTypes.econTxn);
		EconomyWire.WriteTxn(packet, number, money, scrap);
		send(packet);
		return true;
	}

	public void HandleBalance(Packet packet)
	{
		var (money, scrap, seq, processed, status) = EconomyWire.ReadBalance(packet);
		HostMoney = money;
		HostScrap = scrap;
		Seq = seq;
		if (processed > Processed) Processed = processed;
		pending.RemoveAll(t => t.number <= Processed);
		Synced = true;
		if (status is TxnStatus.Insufficient or TxnStatus.Invalid or TxnStatus.NotReady)
		{
			// The refused transaction is the one the host just processed.
			RejectedTxn?.Invoke(processed, status);
			Rejected?.Invoke(status);
		}
		Changed?.Invoke();
	}

	private static long Clamp(long value) => value < 0 ? 0 : value > EconomyLedger.MaxBalance ? EconomyLedger.MaxBalance : value;
}