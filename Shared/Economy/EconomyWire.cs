using CMS21Together.Shared.Net;

namespace CMS21Together.Shared.Economy;

/// <summary>Payload layouts of the economy packets, shared by the game and the headless harness.</summary>
public static class EconomyWire
{
	public static void WriteTxn(Packet packet, int number, long money, long scrap)
	{
		packet.Write(number);
		packet.Write(money);
		packet.Write(scrap);
	}

	public static (int number, long money, long scrap) ReadTxn(Packet packet)
	{
		var number = packet.ReadInt();
		if (number <= 0) throw new NetFormatException($"Transaction number {number}.");
		return (number, packet.Read<long>(), packet.Read<long>());
	}

	public static void WriteBalance(Packet packet, long money, long scrap, long seq, int processed, TxnStatus status)
	{
		packet.Write(money);
		packet.Write(scrap);
		packet.Write(seq);
		packet.Write(processed);
		packet.Write((int)status);
	}

	public static (long money, long scrap, long seq, int processed, TxnStatus status) ReadBalance(Packet packet)
	{
		var money = packet.Read<long>();
		var scrap = packet.Read<long>();
		var seq = packet.Read<long>();
		var processed = packet.ReadInt();
		var status = packet.ReadInt();
		if (money < 0 || scrap < 0 || processed < 0) throw new NetFormatException($"Invalid balance {money}/{scrap} (processed {processed}).");
		if (status < 0 || status > (int)TxnStatus.NotReady) throw new NetFormatException($"Unknown transaction status {status}.");
		return (money, scrap, seq, processed, (TxnStatus)status);
	}
}