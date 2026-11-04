using CMS21Together.Shared.Net;

namespace CMS21Together.Shared.Economy;

public enum PurchaseKind
{
	Upgrade = 1,

	/// <summary>Accepting an order (id = order id, cost 0).</summary>
	JobAccept = 2,

	/// <summary>Handing an order over (id = order id, level = car slot, cost = payout credited to the shared wallet).</summary>
	JobEnd = 3,

	/// <summary>Selling a car (id = level = car slot, cost = price credited to the shared wallet).</summary>
	CarSell = 4,

	/// <summary>The winner of <see cref="JobAccept"/> could not accept after all: the order is free again.</summary>
	AcceptCancel = 5,

	/// <summary>Parking a car (id = "park"): one player parks at a time, so two cars never race for one space.</summary>
	ParkIn = 6,

	/// <summary>Taking a car out of a parking space (id = space): only once.</summary>
	ParkOut = 7
}

/// <summary>
/// Pre-authorized purchases and once-only actions (Phase 6): the player asks, the host charges (or credits) and
/// decides, then the game applies it.
/// </summary>
public static class PurchaseWire
{
	public const int MaxIdLength = 128;

	public static void WriteAsk(Packet packet, int request, PurchaseKind kind, string id, int level, long cost)
	{
		packet.Write(request);
		packet.Write((int)kind);
		packet.Write(id ?? string.Empty);
		packet.Write(level);
		packet.Write(cost);
	}

	public static (int request, PurchaseKind kind, string id, int level, long cost) ReadAsk(Packet packet)
	{
		var request = packet.ReadInt();
		var kind = packet.ReadInt();
		var id = packet.Read<string>();
		var level = packet.ReadInt();
		var cost = packet.Read<long>();
		if (request <= 0 || kind < (int)PurchaseKind.Upgrade || kind > (int)PurchaseKind.ParkOut) throw new NetFormatException($"Purchase request {request} of kind {kind}.");
		if (string.IsNullOrEmpty(id) || id.Length > MaxIdLength) throw new NetFormatException("Purchase without a valid id.");
		if (cost < 0 || cost > EconomyLedger.MaxMoneyDelta || level < 0) throw new NetFormatException($"Purchase cost {cost}, level {level}.");
		return (request, (PurchaseKind)kind, id, level, cost);
	}

	public static void WriteAnswer(Packet packet, int request, TxnStatus status)
	{
		packet.Write(request);
		packet.Write((int)status);
	}

	public static (int request, TxnStatus status) ReadAnswer(Packet packet)
	{
		var request = packet.ReadInt();
		var status = packet.ReadInt();
		if (status < 0 || status > (int)TxnStatus.NotReady) throw new NetFormatException($"Purchase answer {status}.");
		return (request, (TxnStatus)status);
	}
}