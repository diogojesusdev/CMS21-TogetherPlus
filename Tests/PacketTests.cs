using System;
using System.Collections.Generic;
using CMS21Together.Shared;
using CMS21Together.Shared.Net;
using NUnit.Framework;

namespace CMS21Together.Tests;

public class PacketTests
{
	internal static NetFrame ToFrame(Packet packet) =>
		new((ushort)packet.Id, FrameFlags.None, 0, packet.FrameBuffer, FrameCodec.HeaderSize, packet.FrameLength - FrameCodec.HeaderSize);

	[Test]
	public void Frame_StartsWithTheHeaderAndTheId()
	{
		using var packet = new Packet((int)PacketTypes.exp);
		packet.Write(42);

		Assert.That(packet.FrameLength, Is.EqualTo(FrameCodec.HeaderSize + 4));
		Assert.That(FrameCodec.PeekType(packet.FrameBuffer), Is.EqualTo((ushort)PacketTypes.exp));
		Assert.That(packet.PayloadToArray(), Is.EqualTo(BitConverter.GetBytes(42)));
	}

	[Test]
	public void Values_RoundTrip()
	{
		using var written = new Packet((int)PacketTypes.item);
		written.Write(7);
		written.Write("garage");
		written.Write(3.5f);
		written.Write(new[] { 1, 2, 3 });
		written.Write(new Dictionary<string, bool> { ["dlc"] = true });

		using var read = new Packet(ToFrame(written));

		Assert.That(read.Id, Is.EqualTo((int)PacketTypes.item));
		Assert.That(read.ReadInt(), Is.EqualTo(7));
		Assert.That(read.Read<string>(), Is.EqualTo("garage"));
		Assert.That(read.Read<float>(), Is.EqualTo(3.5f));
		Assert.That(read.Read<int[]>(), Is.EqualTo(new[] { 1, 2, 3 }));
		Assert.That(read.Read<Dictionary<string, bool>>()["dlc"], Is.True);
		Assert.That(read.UnreadLength(), Is.Zero);
	}

	[Test]
	public void ReadingPastTheEnd_Throws()
	{
		using var written = new Packet((int)PacketTypes.item);
		written.Write(7);
		using var read = new Packet(ToFrame(written));
		read.ReadInt();

		Assert.That(() => read.ReadInt(), Throws.Exception);
	}

	[Test]
	public void Dispose_IsIdempotent()
	{
		var packet = new Packet((int)PacketTypes.item);
		packet.Dispose();
		Assert.That(() => packet.Dispose(), Throws.Nothing);
	}

	[Test]
	public void GameIds_StayBelowTransportFrames()
	{
		foreach (int id in Enum.GetValues(typeof(PacketTypes)))
			Assert.That(NetFrameTypes.IsTransport((ushort)id), Is.False, $"Packet id {id}");
		Assert.That(() => new Packet(NetFrameTypes.Chunk), Throws.InstanceOf<ArgumentOutOfRangeException>());
	}

	[Test]
	public void PlayerLeft_FollowsConnect()
	{
		Assert.That((int)PacketTypes.playerLeft, Is.EqualTo((int)PacketTypes.connect + 1));
	}
}