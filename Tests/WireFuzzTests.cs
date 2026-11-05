using System;
using System.Collections.Generic;
using System.Linq;
using CMS21Together.Shared;
using CMS21Together.Shared.Economy;
using CMS21Together.Shared.Features;
using CMS21Together.Shared.Locks;
using CMS21Together.Shared.Net;
using CMS21Together.Shared.Persistence;
using CMS21Together.Shared.World;
using NUnit.Framework;

namespace CMS21Together.Tests;

/// <summary>
/// Every wire decoder is reachable by any peer before it is trusted, so it has one job: parse what it was given or
/// reject it as <see cref="NetFormatException"/> (what PacketRouter expects and counts). Another exception type
/// (an index out of range, a cast, a null) still counts against the peer towards the kick limit, and tells the log
/// something useless. The payloads here are empty, constant and random; the point is that nothing else can escape.
/// </summary>
[TestFixture]
public class WireFuzzTests
{
	private sealed class Item
	{
		public long Uid = 1;
		public string Id = "part";
		public int Count;
	}

	private static Action<byte[]> PacketDecoder<T>(PacketTypes type, Func<Packet, T> read) => payload =>
	{
		using var packet = new Packet((int)type, payload);
		read(packet);
	};

	private static Action<byte[]> FrameDecoder<T>(Func<NetFrame, T> read) => payload =>
	{
		var frame = FrameCodec.Encode(NetFrameTypes.Hello, FrameFlags.None, 0, payload);
		read(FrameCodec.Decode(frame, 0, frame.Length));
	};

	private static IEnumerable<TestCaseData> Decoders()
	{
		yield return Case("NetProtocol.DecodeHello", FrameDecoder(NetProtocol.DecodeHello));
		yield return Case("NetProtocol.DecodeWelcome", FrameDecoder(NetProtocol.DecodeWelcome));
		yield return Case("NetProtocol.DecodeReason", FrameDecoder(NetProtocol.DecodeReason));
		yield return Case("NetProtocol.DecodeTimestamp", FrameDecoder(frame => NetProtocol.DecodeTimestamp(frame)));

		yield return Case("ReplicaWire.ReadBegin", PacketDecoder(PacketTypes.replicaBegin, ReplicaWire.ReadBegin));
		yield return Case("ReplicaWire.ReadChunk", PacketDecoder(PacketTypes.replicaChunk, packet => ReplicaWire.ReadChunk(packet)));
		yield return Case("ReplicaWire.ReadAck", PacketDecoder(PacketTypes.replicaAck, packet => ReplicaWire.ReadAck(packet)));
		yield return Case("ReplicaWire.ReadCancel", PacketDecoder(PacketTypes.replicaCancel, packet => ReplicaWire.ReadCancel(packet)));
		yield return Case("ReplicaWire.ReadOffer", PacketDecoder(PacketTypes.replicaOffer, ReplicaWire.ReadOffer));
		yield return Case("ReplicaWire.ReadRequest", PacketDecoder(PacketTypes.replicaRequest, packet => ReplicaWire.ReadRequest(packet)));

		yield return Case("LockWire.ReadRequest", PacketDecoder(PacketTypes.lockRequest, packet => LockWire.ReadRequest(packet)));
		yield return Case("LockWire.ReadToken", PacketDecoder(PacketTypes.lockRenew, LockWire.ReadToken));
		yield return Case("LockWire.ReadReply", PacketDecoder(PacketTypes.lockReply, packet => LockWire.ReadReply(packet)));
		yield return Case("LockWire.ReadState", PacketDecoder(PacketTypes.lockState, packet => LockWire.ReadState(packet)));
		yield return Case("LockWire.ReadSnapshot", PacketDecoder(PacketTypes.lockSnapshot, LockWire.ReadSnapshot));

		yield return Case("EconomyWire.ReadTxn", PacketDecoder(PacketTypes.econTxn, packet => EconomyWire.ReadTxn(packet)));
		yield return Case("EconomyWire.ReadBalance", PacketDecoder(PacketTypes.econBalance, packet => EconomyWire.ReadBalance(packet)));

		yield return Case("PurchaseWire.ReadAsk", PacketDecoder(PacketTypes.purchaseAsk, packet => PurchaseWire.ReadAsk(packet)));
		yield return Case("PurchaseWire.ReadAnswer", PacketDecoder(PacketTypes.purchaseAnswer, packet => PurchaseWire.ReadAnswer(packet)));

		yield return Case("InventoryWire.ReadPush", PacketDecoder(PacketTypes.invPush, packet => InventoryWire.ReadPush<Item>(packet)));
		yield return Case("InventoryWire.ReadOp", PacketDecoder(PacketTypes.invOp, packet => InventoryWire.ReadOp<Item>(packet)));
		yield return Case("InventoryWire.ReadSnapshot", PacketDecoder(PacketTypes.invSnapshot, packet => InventoryWire.ReadSnapshot<Item>(packet)));
		yield return Case("InventoryWire.ReadRequest", PacketDecoder(PacketTypes.invRequest, InventoryWire.ReadRequest));
		yield return Case("InventoryWire.ReadCheck", PacketDecoder(PacketTypes.invCheck, packet => InventoryWire.ReadCheck(packet)));

		yield return Case("FeatureMessage.Read", PacketDecoder(PacketTypes.feature, FeatureMessage.Read));
	}

	private static TestCaseData Case(string name, Action<byte[]> decode) => new TestCaseData(decode).SetName("Fuzzed_" + name);

	[TestCaseSource(nameof(Decoders))]
	public void GarbageIsAlwaysRejectedAsAFormatError(Action<byte[]> decode)
	{
		var payloads = new List<byte[]>
		{
			Array.Empty<byte>(),
			new byte[1],
			new byte[3],
			new byte[8],
			new byte[64],
			Enumerable.Repeat((byte)0xFF, 64).ToArray(),
			Enumerable.Repeat((byte)0x7F, 64).ToArray(),
			Enumerable.Repeat((byte)0x00, 64).ToArray()
		};

		var random = new Random(20261105);
		for (var i = 0; i < 128; i++)
		{
			var payload = new byte[random.Next(0, 129)];
			random.NextBytes(payload);
			payloads.Add(payload);
		}

		foreach (var payload in payloads)
		{
			try
			{
				decode(payload);
			}
			catch (NetFormatException)
			{
				// The decoder rejected it the way the router expects.
			}
			catch (Exception ex)
			{
				Assert.Fail($"{TestContext.CurrentContext.Test.Name} threw {ex.GetType().Name} on a {payload.Length}-byte payload instead of NetFormatException: {ex.Message}");
			}
		}
	}

	[Test]
	public void EveryDecoderRejectsAnEmptyPayload()
	{
		// An empty payload is the shortest thing a peer can send for a type; none of these types is optional, so it
		// must be a rejection rather than a silently empty value.
		foreach (var data in Decoders())
		{
			var decode = (Action<byte[]>)data.Arguments[0];
			Assert.Throws<NetFormatException>(() => decode(Array.Empty<byte>()), data.TestName + " accepted an empty payload");
		}
	}
}

