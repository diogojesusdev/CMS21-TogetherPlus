using CMS21Together.Shared.Net;

namespace CMS21Together.Shared.Persistence;

public enum ReplicaStatus
{
	/// <summary>Accepted; send more (the ack carries the next chunk index expected).</summary>
	Continue,
	Done,
	AlreadyHave,
	Refused,
	Failed
}

public sealed class ReplicaBegin
{
	public int TransferId;
	public string SaveId;
	public long Generation;
	public int Length;

	/// <summary>SHA-256 of the complete bundle bytes.</summary>
	public string BundleSha256;

	public int ChunkSize;

	public int ChunkCount => (int)(((long)Length + ChunkSize - 1) / ChunkSize);
}

/// <summary>Payload layouts of the save replica packets (plan §3.5), shared by the game and the headless tests.</summary>
public static class ReplicaWire
{
	public const int MinChunkSize = 1024;
	public const int MaxChunkSize = 60 * 1024;

	public static void WriteBegin(Packet packet, ReplicaBegin begin)
	{
		packet.Write(begin.TransferId);
		packet.Write(begin.SaveId);
		packet.Write(begin.Generation);
		packet.Write(begin.Length);
		packet.Write(begin.BundleSha256);
		packet.Write(begin.ChunkSize);
	}

	public static ReplicaBegin ReadBegin(Packet packet)
	{
		var begin = new ReplicaBegin
		{
			TransferId = packet.ReadInt(),
			SaveId = packet.Read<string>(),
			Generation = packet.Read<long>(),
			Length = packet.ReadInt(),
			BundleSha256 = packet.Read<string>(),
			ChunkSize = packet.ReadInt()
		};
		if (begin.TransferId <= 0) throw new NetFormatException($"Replica transfer id {begin.TransferId}.");
		if (!SaveIds.IsValid(begin.SaveId)) throw new NetFormatException("Invalid replica save id.");
		if (begin.Generation < 0) throw new NetFormatException($"Replica generation {begin.Generation}.");
		if (begin.Length <= 0 || begin.Length > SaveBundle.MaxBundleBytes) throw new NetFormatException($"Replica size {begin.Length}.");
		if (!Sha256Hex.IsValid(begin.BundleSha256)) throw new NetFormatException("Invalid replica hash.");
		if (begin.ChunkSize < MinChunkSize || begin.ChunkSize > MaxChunkSize) throw new NetFormatException($"Replica chunk size {begin.ChunkSize}.");
		return begin;
	}

	public static void WriteChunk(Packet packet, int transferId, int index, byte[] data)
	{
		packet.Write(transferId);
		packet.Write(index);
		packet.Write(data.Length);
		packet.Write(data);
	}

	public static (int transferId, int index, byte[] data) ReadChunk(Packet packet)
	{
		var transferId = packet.ReadInt();
		var index = packet.ReadInt();
		var length = packet.ReadInt();
		if (index < 0) throw new NetFormatException($"Replica chunk index {index}.");
		if (length <= 0 || length > MaxChunkSize || length > packet.UnreadLength()) throw new NetFormatException($"Replica chunk size {length}.");
		return (transferId, index, packet.ReadBytes(length));
	}

	public static void WriteAck(Packet packet, int transferId, int next, ReplicaStatus status)
	{
		packet.Write(transferId);
		packet.Write(next);
		packet.Write((int)status);
	}

	public static (int transferId, int next, ReplicaStatus status) ReadAck(Packet packet)
	{
		var transferId = packet.ReadInt();
		var next = packet.ReadInt();
		var status = packet.ReadInt();
		if (next < 0) throw new NetFormatException($"Replica ack {next}.");
		if (status < 0 || status > (int)ReplicaStatus.Failed) throw new NetFormatException($"Replica status {status}.");
		return (transferId, next, (ReplicaStatus)status);
	}

	public static void WriteCancel(Packet packet, int transferId) => packet.Write(transferId);

	public static int ReadCancel(Packet packet) => packet.ReadInt();

	public static void WriteOffer(Packet packet, ReplicaOffer offer)
	{
		packet.Write(offer.SaveId);
		packet.Write(offer.Generation);
		packet.Write(offer.BundleSha256);
		packet.Write(offer.CreatedUtcTicks);
	}

	public static ReplicaOffer ReadOffer(Packet packet)
	{
		var offer = new ReplicaOffer
		{
			SaveId = packet.Read<string>(),
			Generation = packet.Read<long>(),
			BundleSha256 = packet.Read<string>(),
			CreatedUtcTicks = packet.Read<long>()
		};
		if (!SaveIds.IsValid(offer.SaveId) || offer.Generation <= 0 || !Sha256Hex.IsValid(offer.BundleSha256))
			throw new NetFormatException("Invalid replica offer.");
		return offer;
	}

	public static void WriteRequest(Packet packet, string saveId, long generation, string bundleSha256)
	{
		packet.Write(saveId);
		packet.Write(generation);
		packet.Write(bundleSha256);
	}

	public static (string saveId, long generation, string bundleSha256) ReadRequest(Packet packet)
	{
		var saveId = packet.Read<string>();
		var generation = packet.Read<long>();
		var sha = packet.Read<string>();
		if (!SaveIds.IsValid(saveId) || generation <= 0 || !Sha256Hex.IsValid(sha)) throw new NetFormatException("Invalid replica request.");
		return (saveId, generation, sha);
	}
}