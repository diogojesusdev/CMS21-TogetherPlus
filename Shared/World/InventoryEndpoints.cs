using System;
using System.Collections.Generic;
using System.Linq;

namespace CMS21Together.Shared.World;

/// <summary>Host side of one shared inventory: applies players' changes and sends versioned changes to everyone.</summary>
public sealed class InventoryHost<T> where T : class
{
	private readonly InventoryKind kind;
	private readonly Action<int, Packet> send;
	private readonly Func<IEnumerable<int>> players;

	/// <param name="send">Reliable send to one slot.</param>
	/// <param name="players">Slots in the session (validated).</param>
	public InventoryHost(InventoryKind kind, Func<T, long> uidOf, Action<int, Packet> send, Func<IEnumerable<int>> players)
	{
		this.kind = kind;
		this.send = send ?? throw new ArgumentNullException(nameof(send));
		this.players = players ?? throw new ArgumentNullException(nameof(players));
		Authority = new InventoryAuthority<T>(uidOf);
	}

	public InventoryAuthority<T> Authority { get; }

	/// <summary>Players whose set did not match the host's (diagnostics and tests).</summary>
	public int Desyncs { get; private set; }

	public int SnapshotsSent { get; private set; }

	public void PlayerJoined(int slot) => Authority.PlayerJoined(slot);
	public void PlayerLeft(int slot) => Authority.PlayerLeft(slot);

	/// <summary>Whether a player's debit was charged (Phase 6). Null: purchases are not checked.</summary>
	public Func<int, int, bool> Charged { get; set; }

	/// <summary>Purchased items dropped because their debit was refused.</summary>
	public int RefusedPurchases { get; private set; }

	/// <param name="txn">The debit that paid for this item (0 = not a purchase).</param>
	public InventoryResult HandlePush(int slot, int seq, InventoryOp op, T item, int txn = 0)
	{
		var refused = op == InventoryOp.Add && txn > 0 && Charged != null && !Charged(slot, txn);
		var result = refused ? Authority.Skip(slot, seq) : Authority.Apply(slot, seq, op, item);
		if (result == InventoryResult.Refused) RefusedPurchases++;
		Processed?.Invoke(slot, op, item, result);
		if (result == InventoryResult.Replayed) return result;
		if (result == InventoryResult.NoChange || result == InventoryResult.Refused)
		{
			// Nothing changed: the sender only learns that its change was processed (an already-applied version);
			// a refused purchase therefore disappears from its game, and nobody else ever sees it.
			using var ack = new Packet((int)PacketTypes.invOp);
			InventoryWire.WriteOp(ack, kind, Authority.Version, Authority.ProcessedUpTo(slot), op, item);
			send(slot, ack);
			return result;
		}
		Broadcast(op, item);
		return result;
	}

	/// <summary>Every change a player sent, with what the host made of it (race reconciliation).</summary>
	public event Action<int, InventoryOp, T, InventoryResult> Processed;

	/// <summary>A correction made by the host itself; everyone gets it like any other change.</summary>
	public InventoryResult Correct(InventoryOp op, T item)
	{
		var result = Authority.ApplyAsHost(op, item);
		if (result == InventoryResult.Added || result == InventoryResult.Removed || result == InventoryResult.Updated) Broadcast(op, item);
		return result;
	}

	private void Broadcast(InventoryOp op, T item)
	{
		foreach (var player in players().ToList())
		{
			using var packet = new Packet((int)PacketTypes.invOp);
			InventoryWire.WriteOp(packet, kind, Authority.Version, Authority.ProcessedUpTo(player), op, item);
			send(player, packet);
		}
	}

	public void SendSnapshot(int slot)
	{
		using var packet = new Packet((int)PacketTypes.invSnapshot);
		InventoryWire.WriteSnapshot(packet, kind, Authority.Version, Authority.ProcessedUpTo(slot), Authority.Items.ToList());
		send(slot, packet);
		SnapshotsSent++;
	}

	/// <summary>A player's periodic check (plan §3.4, desync detector). A mismatch gets the host's set back.</summary>
	public void HandleCheck(int slot, long version, string hash)
	{
		if (Authority.Matches(version, hash) != false) return;
		Desyncs++;
		Log.Warn($"[Inventory] Player #{slot}'s {kind} differ from the host's at version {version}; sending the host's.");
		SendSnapshot(slot);
	}
}

/// <summary>A player's side of one shared inventory.</summary>
public sealed class InventoryClient<T> where T : class
{
	/// <summary>Snapshot requests are not repeated faster than this while one is outstanding.</summary>
	public const long RequestEveryMs = 2000;

	private readonly InventoryKind kind;
	private readonly Action<Packet> send;
	private readonly Func<long> clock;
	private long lastRequestMs = long.MinValue / 2;

	public InventoryClient(InventoryKind kind, Func<T, long> uidOf, Action<Packet> send, Func<long> clockMs)
	{
		this.kind = kind;
		this.send = send ?? throw new ArgumentNullException(nameof(send));
		clock = clockMs ?? throw new ArgumentNullException(nameof(clockMs));
		Mirror = new InventoryMirror<T>(uidOf);
		Mirror.SnapshotNeeded += () => RequestSnapshot(true);
	}

	public InventoryMirror<T> Mirror { get; }

	private readonly List<(int seq, InventoryOp op, T item)> outbox = new();

	/// <summary>
	/// Send each change at once (default). Off: changes are recorded at once but sent by <see cref="Flush"/>, so a
	/// purchase made in the same frame as its debit can carry the debit's number whichever came first (Phase 6).
	/// </summary>
	public bool AutoFlush { get; set; } = true;

	/// <summary>Sends the changes recorded since the last flush; additions carry <paramref name="purchaseTxn"/>.</summary>
	public void Flush(int purchaseTxn = 0)
	{
		if (outbox.Count == 0) return;
		foreach (var (seq, op, item) in outbox) Send(seq, op, item, op == InventoryOp.Add ? purchaseTxn : 0);
		outbox.Clear();
	}

	private void Send(int seq, InventoryOp op, T item, int txn)
	{
		using var packet = new Packet((int)PacketTypes.invPush);
		InventoryWire.WritePush(packet, kind, seq, op, item, txn);
		send(packet);
	}

	/// <summary>The set the game should hold changed (apply <see cref="InventoryMirror{T}.Desired"/>).</summary>
	public event Action Changed;

	public void Local(InventoryOp op, T item, int txn = 0)
	{
		var seq = Mirror.Local(op, item);
		if (AutoFlush) Send(seq, op, item, txn);
		else outbox.Add((seq, op, item));
	}

	public void HandleOp(long version, int ack, InventoryOp op, T item)
	{
		Mirror.Remote(version, ack, op, item);
		Changed?.Invoke();
	}

	public void HandleSnapshot(long version, int ack, List<T> items)
	{
		Mirror.Snapshot(version, ack, items);
		Changed?.Invoke();
	}

	/// <param name="force">Ask even if a request went out recently (a change was just missed).</param>
	public void RequestSnapshot(bool force = false)
	{
		var now = clock();
		if (!force && now - lastRequestMs < RequestEveryMs) return;
		lastRequestMs = now;
		using var packet = new Packet((int)PacketTypes.invRequest);
		InventoryWire.WriteRequest(packet, kind);
		send(packet);
	}

	/// <summary>Periodic desync check: the host compares this set with its own at the same version.</summary>
	public void SendCheck()
	{
		if (!Mirror.Synced)
		{
			RequestSnapshot();
			return;
		}
		using var packet = new Packet((int)PacketTypes.invCheck);
		InventoryWire.WriteCheck(packet, kind, Mirror.Version, Mirror.ConfirmedHash);
		send(packet);
	}
}