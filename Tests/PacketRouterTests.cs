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

	/// <summary>
	/// The per-frame streams, as documented in AGENTS.md ("never log in per-frame paths"). Anything else that is
	/// received gets one breadcrumb, which is what makes a crash log readable.
	/// </summary>
	private static readonly PacketTypes[] PerFrameStreams =
	{
		PacketTypes.position, PacketTypes.rotation, PacketTypes.carPosition, PacketTypes.toolMove,
		PacketTypes.engineStandAngle, PacketTypes.carEngineSound, PacketTypes.playerInCar,
		PacketTypes.replicaChunk, PacketTypes.replicaAck, PacketTypes.lockRenew, PacketTypes.econBalance,
		PacketTypes.invCheck, PacketTypes.carCheck
	};

	[Test]
	public void OnlyThePerFrameStreamsAreQuiet()
	{
		foreach (PacketTypes type in Enum.GetValues(typeof(PacketTypes)))
		{
			var expected = Array.IndexOf(PerFrameStreams, type) >= 0;
			Assert.That(PacketRouter.IsQuiet(type), Is.EqualTo(expected),
				expected
					? $"{type} sends every frame and must stay quiet (one log line per frame costs the frame rate)"
					: $"{type} is not a per-frame stream: it must be logged, or a crash log loses it");
		}

		Assert.That(PacketRouter.IsQuiet(-1), Is.False);
		Assert.That(PacketRouter.IsQuiet(Enum.GetValues(typeof(PacketTypes)).Length + 50), Is.False, "an unknown type is not quiet");
	}

	private sealed class RecordingSink : ILogSink
	{
		public readonly List<string> Info = new();

		public void Write(LogLevel level, string message)
		{
			if (level == LogLevel.Info) Info.Add(message);
		}
	}

	[Test]
	public void ARouteIsOnlyBreadcrumbedWhenItIsNotAPerFrameStream()
	{
		var previousSink = Log.Sink;
		var previousLevel = Log.MinLevel;
		var sink = new RecordingSink();
		try
		{
			Log.Sink = sink;
			Log.MinLevel = LogLevel.Info;

			var router = new PacketRouter("test");
			router.Register((int)PacketTypes.item, _ => { });
			router.Register((int)PacketTypes.position, _ => { });

			Assert.That(router.Dispatch(1, Frame(PacketTypes.item, 1)), Is.True);
			Assert.That(router.Dispatch(1, Frame(PacketTypes.position, 2)), Is.True);

			Assert.That(sink.Info, Has.Count.EqualTo(1), "one breadcrumb for the logged packet, none for the per-frame stream");
			Assert.That(sink.Info[0], Does.Contain("<- item"));
		}
		finally
		{
			Log.Sink = previousSink;
			Log.MinLevel = previousLevel;
		}
	}
}