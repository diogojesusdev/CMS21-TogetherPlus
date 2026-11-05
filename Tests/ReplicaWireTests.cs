using System;
using CMS21Together.Shared;
using CMS21Together.Shared.Net;
using CMS21Together.Shared.Persistence;
using NUnit.Framework;

namespace CMS21Together.Tests;

/// <summary>
/// The replica packets carry another player's save folder, so every field names a file or a bound: a decoder that
/// accepts a bad save id or a size out of range is a path or a memory bug waiting for one bad router.
/// </summary>
[TestFixture]
public class ReplicaWireTests
{
	private static readonly string SaveId = SaveIds.New();
	private static readonly string Hash = Sha256Hex.Of(new byte[] { 1, 2, 3 });

	private static Packet Reader(Action<Packet> write, PacketTypes type)
	{
		using var packet = new Packet((int)type);
		write(packet);
		return new Packet((int)type, packet.PayloadToArray());
	}

	private static Packet BeginPacket(Action<ReplicaBegin> mutate = null, PacketTypes type = PacketTypes.replicaBegin)
	{
		var begin = new ReplicaBegin
		{
			TransferId = 7,
			SaveId = SaveId,
			Generation = 2,
			Length = 4096,
			BundleSha256 = Hash,
			ChunkSize = 4096
		};
		mutate?.Invoke(begin);
		return Reader(packet => ReplicaWire.WriteBegin(packet, begin), type);
	}

	[Test]
	public void ABeginKeepsEveryField()
	{
		var begin = ReplicaWire.ReadBegin(BeginPacket());

		Assert.That(begin.TransferId, Is.EqualTo(7));
		Assert.That(begin.SaveId, Is.EqualTo(SaveId));
		Assert.That(begin.Generation, Is.EqualTo(2));
		Assert.That(begin.Length, Is.EqualTo(4096));
		Assert.That(begin.BundleSha256, Is.EqualTo(Hash));
		Assert.That(begin.ChunkSize, Is.EqualTo(4096));
	}

	[TestCase(4096, 4096, 1)]
	[TestCase(4097, 4096, 2)]
	[TestCase(8192, 4096, 2)]
	[TestCase(1, 4096, 1)]
	public void TheChunkCountRoundsUp(int length, int chunkSize, int expected)
	{
		var begin = ReplicaWire.ReadBegin(BeginPacket(b => { b.Length = length; b.ChunkSize = chunkSize; }));
		Assert.That(begin.ChunkCount, Is.EqualTo(expected));
	}

	[TestCase(0)]
	[TestCase(-1)]
	public void ABeginWithoutATransferIdIsRejected(int transferId)
	{
		Assert.Throws<NetFormatException>(() => ReplicaWire.ReadBegin(BeginPacket(b => b.TransferId = transferId)));
	}

	[TestCase(null)]
	[TestCase("")]
	[TestCase("0123456789abcdef0123456789ABCDEF")]
	[TestCase("..\\..\\save")]
	[TestCase("0123456789abcdef0123456789abcde")]
	public void ABeginWithASaveIdThatIsNotOneIsRejected(string saveId)
	{
		Assert.Throws<NetFormatException>(() => ReplicaWire.ReadBegin(BeginPacket(b => b.SaveId = saveId)));
	}

	[Test]
	public void ANegativeGenerationIsRejected()
	{
		Assert.Throws<NetFormatException>(() => ReplicaWire.ReadBegin(BeginPacket(b => b.Generation = -1)));
	}

	[TestCase(0)]
	[TestCase(-1)]
	[TestCase(SaveBundle.MaxBundleBytes + 1)]
	public void AnImpossibleLengthIsRejected(int length)
	{
		Assert.Throws<NetFormatException>(() => ReplicaWire.ReadBegin(BeginPacket(b => b.Length = length)));
	}

	[Test]
	public void AHashThatIsNotSha256IsRejected()
	{
		Assert.Throws<NetFormatException>(() => ReplicaWire.ReadBegin(BeginPacket(b => b.BundleSha256 = Hash.ToUpperInvariant())));
	}

	[TestCase(ReplicaWire.MinChunkSize - 1)]
	[TestCase(ReplicaWire.MaxChunkSize + 1)]
	[TestCase(0)]
	public void AChunkSizeOutsideTheBoundsIsRejected(int chunkSize)
	{
		Assert.Throws<NetFormatException>(() => ReplicaWire.ReadBegin(BeginPacket(b => b.ChunkSize = chunkSize)));
	}

	[TestCase(ReplicaWire.MinChunkSize)]
	[TestCase(ReplicaWire.MaxChunkSize)]
	public void BothChunkBoundsAreAccepted(int chunkSize)
	{
		var begin = ReplicaWire.ReadBegin(BeginPacket(b => b.ChunkSize = chunkSize));
		Assert.That(begin.ChunkSize, Is.EqualTo(chunkSize));
	}

	[Test]
	public void AChunkKeepsItsIndexAndBytes()
	{
		var data = new byte[] { 7, 8, 9 };
		var reader = Reader(packet => ReplicaWire.WriteChunk(packet, 5, 2, data), PacketTypes.replicaChunk);

		var (transferId, index, bytes) = ReplicaWire.ReadChunk(reader);

		Assert.That((transferId, index), Is.EqualTo((5, 2)));
		Assert.That(bytes, Is.EqualTo(data));
	}

	[Test]
	public void ANegativeChunkIndexIsRejected()
	{
		var reader = Reader(packet => ReplicaWire.WriteChunk(packet, 5, -1, new byte[] { 1 }), PacketTypes.replicaChunk);
		Assert.Throws<NetFormatException>(() => ReplicaWire.ReadChunk(reader));
	}

	[TestCase(0)]
	[TestCase(-1)]
	[TestCase(ReplicaWire.MaxChunkSize + 1)]
	public void AChunkLengthOutsideTheBoundsIsRejected(int length)
	{
		var reader = Reader(packet =>
		{
			packet.Write(5);
			packet.Write(0);
			packet.Write(length);
			packet.Write(new byte[3]);
		}, PacketTypes.replicaChunk);

		Assert.Throws<NetFormatException>(() => ReplicaWire.ReadChunk(reader));
	}

	[Test]
	public void AChunkThatPromisesMoreBytesThanItCarriesIsRejected()
	{
		var reader = Reader(packet =>
		{
			packet.Write(5);
			packet.Write(0);
			packet.Write(100);
			packet.Write(new byte[10]);
		}, PacketTypes.replicaChunk);

		Assert.Throws<NetFormatException>(() => ReplicaWire.ReadChunk(reader));
	}

	[TestCase(ReplicaStatus.Continue)]
	[TestCase(ReplicaStatus.Done)]
	[TestCase(ReplicaStatus.AlreadyHave)]
	[TestCase(ReplicaStatus.Refused)]
	[TestCase(ReplicaStatus.Failed)]
	public void AnAckKeepsItsNextChunkAndStatus(ReplicaStatus status)
	{
		var reader = Reader(packet => ReplicaWire.WriteAck(packet, 4, 3, status), PacketTypes.replicaAck);

		var (transferId, next, read) = ReplicaWire.ReadAck(reader);

		Assert.That((transferId, next, read), Is.EqualTo((4, 3, status)));
	}

	[Test]
	public void AnAckWithANegativePositionOrUnknownStatusIsRejected()
	{
		Assert.Throws<NetFormatException>(() => ReplicaWire.ReadAck(Reader(
			packet => ReplicaWire.WriteAck(packet, 1, -1, ReplicaStatus.Continue), PacketTypes.replicaAck)));

		Assert.Throws<NetFormatException>(() => ReplicaWire.ReadAck(Reader(packet =>
		{
			packet.Write(1);
			packet.Write(0);
			packet.Write((int)ReplicaStatus.Failed + 1);
		}, PacketTypes.replicaAck)));
	}

	[Test]
	public void ACancelNamesItsTransfer()
	{
		var reader = Reader(packet => ReplicaWire.WriteCancel(packet, 11), PacketTypes.replicaCancel);
		Assert.That(ReplicaWire.ReadCancel(reader), Is.EqualTo(11));
	}

	private static Packet OfferPacket(Action<ReplicaOffer> mutate)
	{
		var offer = new ReplicaOffer { SaveId = SaveId, Generation = 2, BundleSha256 = Hash, CreatedUtcTicks = 638_000_000_000_000_000L };
		mutate?.Invoke(offer);
		return Reader(packet => ReplicaWire.WriteOffer(packet, offer), PacketTypes.replicaOffer);
	}

	[Test]
	public void AnOfferKeepsEveryField()
	{
		var offer = ReplicaWire.ReadOffer(OfferPacket(null));

		Assert.That(offer.SaveId, Is.EqualTo(SaveId));
		Assert.That(offer.Generation, Is.EqualTo(2));
		Assert.That(offer.BundleSha256, Is.EqualTo(Hash));
		Assert.That(offer.CreatedUtcTicks, Is.EqualTo(638_000_000_000_000_000L));
	}

	[TestCase(null)]
	[TestCase("nope")]
	[TestCase("0123456789abcdef0123456789ABCDEF")]
	public void AnOfferWithABadSaveIdIsRejected(string saveId)
	{
		Assert.Throws<NetFormatException>(() => ReplicaWire.ReadOffer(OfferPacket(o => o.SaveId = saveId)));
	}

	[TestCase(0)]
	[TestCase(-5)]
	public void AnOfferWithNoGenerationIsRejected(long generation)
	{
		Assert.Throws<NetFormatException>(() => ReplicaWire.ReadOffer(OfferPacket(o => o.Generation = generation)));
	}

	[Test]
	public void AnOfferWithABadHashIsRejected()
	{
		Assert.Throws<NetFormatException>(() => ReplicaWire.ReadOffer(OfferPacket(o => o.BundleSha256 = "abc")));
	}

	[Test]
	public void ARequestNamesWhatThePlayerWants()
	{
		var reader = Reader(packet => ReplicaWire.WriteRequest(packet, SaveId, 2, Hash), PacketTypes.replicaRequest);
		Assert.That(ReplicaWire.ReadRequest(reader), Is.EqualTo((SaveId, 2L, Hash)));
	}

	[TestCase(false, 2L)]
	[TestCase(true, 0L)]
	[TestCase(true, -1L)]
	public void ARequestThatCannotBeAnsweredIsRejected(bool withSaveId, long generation)
	{
		var saveId = withSaveId ? SaveId : null;
		var reader = Reader(packet => ReplicaWire.WriteRequest(packet, saveId, generation, Hash), PacketTypes.replicaRequest);
		Assert.Throws<NetFormatException>(() => ReplicaWire.ReadRequest(reader));
	}

	[Test]
	public void ARequestWithABadHashIsRejected()
	{
		var reader = Reader(packet => ReplicaWire.WriteRequest(packet, SaveId, 2, "not-a-hash"), PacketTypes.replicaRequest);
		Assert.Throws<NetFormatException>(() => ReplicaWire.ReadRequest(reader));
	}
}
