using System;
using System.Collections.Generic;
using CMS21Together.Shared;
using CMS21Together.Shared.Net;
using NUnit.Framework;

namespace CMS21Together.Tests;

public class PacketRouterTests
{
	private static NetFrame Frame(PacketTypes type, int value)
	{
		var packet = new Packet((int)type);
		packet.Write(value);
		// Copy: the router owns the frame after the packet is gone.
		var bytes = new byte[packet.FrameLength];
		Buffer.BlockCopy(packet.FrameBuffer, 0, bytes, 0, bytes.Length);
		packet.Dispose();
		return new NetFrame((ushort)type, FrameFlags.None, 0, bytes, FrameCodec.HeaderSize, bytes.Length - FrameCodec.HeaderSize);
	}

	[Test]
	public void Dispatch_PassesTheSenderSlotAndPayload()
	{
		var router = new PacketRouter("test");
		var seen = new List<(int, int)>();
		router.Register((int)PacketTypes.exp, (slot, packet) => seen.Add((slot, packet.ReadInt())));

		Assert.That(router.Dispatch(3, Frame(PacketTypes.exp, 99)), Is.True);
		Assert.That(seen, Is.EqualTo(new[] { (3, 99) }));
	}

	[Test]
	public void UnknownTypes_AreIgnored()
	{
		var router = new PacketRouter("test");
		Assert.That(router.Dispatch(2, Frame(PacketTypes.exp, 1)), Is.False);
		Assert.That(router.ErrorCount(2), Is.Zero);
	}

	[Test]
	public void AFailingHandler_DoesNotStopTheNextPackets()
	{
		var router = new PacketRouter("test");
		var handled = 0;
		router.Register((int)PacketTypes.exp, packet =>
		{
			if (packet.ReadInt() < 0) throw new InvalidOperationException("broken");
			handled++;
		});

		Assert.That(router.Dispatch(2, Frame(PacketTypes.exp, -1)), Is.False);
		Assert.That(router.Dispatch(2, Frame(PacketTypes.exp, 1)), Is.True);
		Assert.That(handled, Is.EqualTo(1));
		Assert.That(router.ErrorCount(2), Is.EqualTo(1));
	}

	[Test]
	public void TruncatedPayload_CountsAsAnErrorInsteadOfCrashing()
	{
		var router = new PacketRouter("test");
		router.Register((int)PacketTypes.exp, packet => packet.Read<string>());
		Assert.That(router.Dispatch(4, Frame(PacketTypes.exp, 1000)), Is.False);
		Assert.That(router.ErrorCount(4), Is.EqualTo(1));
	}

	[Test]
	public void PeerMisbehaved_IsRaisedOnceAtTheLimit()
	{
		var router = new PacketRouter("test");
		router.Register((int)PacketTypes.exp, _ => throw new InvalidOperationException());
		var reports = new List<int>();
		router.PeerMisbehaved += (slot, _) => reports.Add(slot);

		for (var i = 0; i < PacketRouter.MaxHandlerErrors + 10; i++) router.Dispatch(5, Frame(PacketTypes.exp, i));

		Assert.That(reports, Is.EqualTo(new[] { 5 }));
		router.ResetPeer(5);
		Assert.That(router.ErrorCount(5), Is.Zero);
	}
}