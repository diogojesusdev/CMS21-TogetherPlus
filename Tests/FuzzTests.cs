using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CMS21Together.Shared;
using CMS21Together.Shared.Net;
using NUnit.Framework;

namespace CMS21Together.Tests;

/// <summary>
/// Hostile input against the full host stack (links, framing, chunking, handshake, router, serializer): garbage
/// before and after the handshake, random payloads for real packet types and bit-flipped frames. The host must
/// never throw, well-behaved players must not notice, and an offender is disconnected or kicked.
/// </summary>
[TestFixture]
public class FuzzTests
{
	private const string Version = "fuzz-1";
	private const int Probe = 1000;
	private long now;
	private NetHost host;
	private PacketRouter router;
	private readonly List<(int slot, string reason)> kicked = new();
	private readonly Dictionary<int, int> probes = new();
	private ILogSink previousSink;

	private long Clock() => now;

	[Serializable]
	public class FuzzDto
	{
		public int Id;
		public string Name;
		public float[] Values;
		public List<FuzzDto> Children;
		public Dictionary<string, int> Map;
		public PacketTypes Kind;
		public long? Optional;
	}

	private sealed class Peer
	{
		public NetClient Client;
		public SimLink ClientLink;
		public SimLink HostLink;
		public readonly List<string> Disconnects = new();
	}

	[SetUp]
	public void SetUp()
	{
		previousSink = Log.Sink;
		Log.Sink = null;
		now = 0;
		kicked.Clear();
		probes.Clear();
		host = new NetHost(8, Version, Clock);
		router = new PacketRouter("Fuzz");
		router.PeerMisbehaved += (slot, reason) =>
		{
			kicked.Add((slot, reason));
			host.Kick(slot, reason);
		};
		// Shapes like the real handlers: validation, DTO payloads, collections of DTOs.
		router.Register((int)PacketTypes.connect, (_, p) =>
		{
			p.ReadInt();
			p.Read<string>();
			p.Read<ReadOnlyDictionary<string, bool>>();
			p.Read<string>();
			p.Read<string>();
			p.Read<string>();
			p.Read<string>();
			p.Read<string>();
		});
		router.Register((int)PacketTypes.item, (_, p) => p.Read<FuzzDto>());
		router.Register((int)PacketTypes.position, (_, p) =>
		{
			p.Read<List<FuzzDto>>();
			p.ReadInt();
		});
		router.Register(Probe, (slot, p) =>
		{
			Assert.That(p.ReadInt(), Is.EqualTo(slot));
			probes[slot] = probes.TryGetValue(slot, out var n) ? n + 1 : 1;
		});
		host.FrameReceived += (c, f) => router.Dispatch(c.SlotId, f);
	}

	[TearDown]
	public void TearDown() => Log.Sink = previousSink;

	private Peer Join(byte[] hostKey = null)
	{
		var (a, b) = SimLink.CreatePair(null, Clock, "fuzz");
		var peer = new Peer { Client = new NetClient(Version, Clock), ClientLink = a, HostLink = b };
		peer.Client.Disconnected += r => peer.Disconnects.Add(r);
		peer.Client.Connect(a, hostKey);
		host.Accept(b);
		return peer;
	}

	private void Pump(IEnumerable<Peer> peers, int rounds = 6, int stepMs = 0)
	{
		var list = peers.ToList();
		for (var i = 0; i < rounds; i++)
		{
			now += stepMs;
			host.Tick();
			foreach (var p in list) p.Client.Tick();
		}
	}

	private static Packet ProbePacket(int slot)
	{
		var packet = new Packet(Probe);
		packet.Write(slot);
		return packet;
	}

	private static void SendProbe(Peer peer)
	{
		using var packet = ProbePacket(peer.Client.SlotId);
		Assert.That(peer.Client.Send(packet.FrameBuffer, packet.FrameLength), Is.EqualTo(EnqueueResult.Queued));
	}

	private static byte[] Wire(Packet packet) => packet.FrameBuffer.Take(packet.FrameLength).ToArray();

	private static FuzzDto SampleDto(Random random, int depth = 0) => new()
	{
		Id = random.Next(),
		Name = new string('x', random.Next(20)),
		Values = Enumerable.Range(0, random.Next(5)).Select(i => (float)random.NextDouble()).ToArray(),
		Children = depth < 2 ? Enumerable.Range(0, random.Next(3)).Select(_ => SampleDto(random, depth + 1)).ToList() : null,
		Map = new Dictionary<string, int> { ["a"] = random.Next(), ["b"] = 2 },
		Kind = PacketTypes.item,
		Optional = random.Next(2) == 0 ? null : random.Next()
	};

	/// <summary>A well-formed packet of one of the registered types.</summary>
	private static Packet ValidPacket(Random random)
	{
		switch (random.Next(3))
		{
			case 0:
			{
				var p = new Packet((int)PacketTypes.connect);
				p.Write(2);
				p.Write("name");
				p.Write(new ReadOnlyDictionary<string, bool>(new Dictionary<string, bool> { ["dlc"] = true }));
				p.Write("game");
				p.Write(Version);
				p.Write(Guid.NewGuid().ToString());
				p.Write("save");
				p.Write(IdentityService.NewToken());
				return p;
			}
			case 1:
			{
				var p = new Packet((int)PacketTypes.item);
				p.Write(SampleDto(random));
				return p;
			}
			default:
			{
				var p = new Packet((int)PacketTypes.position);
				p.Write(new List<FuzzDto> { SampleDto(random), SampleDto(random) });
				p.Write(7);
				return p;
			}
		}
	}

	private (Peer owner, Peer good) StartSession()
	{
		var owner = Join(host.HostKey);
		var good = Join();
		Pump(new[] { owner, good });
		Assert.That(owner.Client.State, Is.EqualTo(NetClientState.Connected));
		Assert.That(good.Client.State, Is.EqualTo(NetClientState.Connected));
		return (owner, good);
	}

	private void AssertBystandersFine(params Peer[] bystanders)
	{
		var before = bystanders.ToDictionary(b => b.Client.SlotId, b => probes.TryGetValue(b.Client.SlotId, out var n) ? n : 0);
		foreach (var b in bystanders) SendProbe(b);
		Pump(bystanders);
		foreach (var b in bystanders)
		{
			Assert.That(b.Client.State, Is.EqualTo(NetClientState.Connected), $"player #{b.Client.SlotId} was dropped");
			Assert.That(b.Disconnects, Is.Empty);
			Assert.That(probes[b.Client.SlotId], Is.EqualTo(before[b.Client.SlotId] + 1), $"player #{b.Client.SlotId} lost a packet");
			Assert.That(router.ErrorCount(b.Client.SlotId), Is.Zero);
		}
		Assert.That(kicked.Select(k => k.slot), Has.None.AnyOf(bystanders.Select(b => b.Client.SlotId).ToArray()));
	}

	[Test]
	public void GarbageBeforeTheHandshakeNeverReachesTheGame()
	{
		var (owner, good) = StartSession();
		var random = new Random(5);
		var dispatchedBefore = probes.Values.Sum();
		var attackers = new List<SimLink>();
		for (var i = 0; i < 300; i++)
		{
			var (_, hostEnd) = SimLink.CreatePair(null, Clock, "raw");
			host.Accept(hostEnd);
			attackers.Add(hostEnd);
			// Either pure noise or a well-formed game packet sent without a Hello.
			byte[] wire;
			if (random.Next(2) == 0)
			{
				wire = new byte[random.Next(1, 400)];
				random.NextBytes(wire);
			}
			else
			{
				using var packet = random.Next(2) == 0 ? ValidPacket(random) : ProbePacket(2);
				wire = Wire(packet);
			}
			hostEnd.InjectInbound(wire);
			Pump(new[] { owner, good }, 2);
		}

		// Nothing an unauthenticated connection sent may reach the router.
		Assert.That(probes.Values.Sum(), Is.EqualTo(dispatchedBefore));
		Assert.That(router.ErrorCount(2) + router.ErrorCount(3), Is.Zero);
		AssertBystandersFine(owner, good);

		// Connections that never completed the handshake are cleaned up and free no-one else's slot.
		Pump(new[] { owner, good }, 40, 500);
		Assert.That(host.PeerCount, Is.EqualTo(2));
		AssertBystandersFine(owner, good);
	}

	[Test]
	public void RandomPayloadsForRealPacketTypesGetOnlyTheSenderKicked()
	{
		var (owner, good) = StartSession();
		var bad = Join();
		Pump(new[] { owner, good, bad });
		var badSlot = bad.Client.SlotId;
		var random = new Random(17);

		for (var i = 0; i < 400 && bad.Client.State == NetClientState.Connected; i++)
		{
			using var valid = ValidPacket(random);
			var payload = valid.PayloadToArray();
			byte[] fuzzed;
			switch (random.Next(3))
			{
				case 0: // Pure noise.
					fuzzed = new byte[random.Next(0, 300)];
					random.NextBytes(fuzzed);
					break;
				case 1: // Truncated.
					fuzzed = payload.Take(random.Next(payload.Length)).ToArray();
					break;
				default: // Mutated bytes, often in a length or count field.
					fuzzed = (byte[])payload.Clone();
					for (var m = random.Next(1, 4); m > 0 && fuzzed.Length > 0; m--)
						fuzzed[random.Next(fuzzed.Length)] = (byte)random.Next(256);
					break;
			}
			using var packet = new Packet(random.Next(3) switch { 0 => (int)PacketTypes.connect, 1 => (int)PacketTypes.item, _ => (int)PacketTypes.position });
			packet.Write(fuzzed);
			bad.Client.Send(packet.FrameBuffer, packet.FrameLength);
			if (i % 10 == 0)
			{
				SendProbe(good);
				SendProbe(owner);
			}
			Pump(new[] { owner, good, bad }, 1);
		}
		Pump(new[] { owner, good, bad });

		Assert.That(kicked.Select(k => k.slot), Is.EqualTo(new[] { badSlot }));
		Assert.That(bad.Disconnects.Single(), Does.Contain("Too many invalid packets"));
		Assert.That(host.GetPeer(badSlot), Is.Null);
		AssertBystandersFine(owner, good);
	}

	[Test]
	public void BitFlippedFramesNeverCrashTheHost()
	{
		var (owner, good) = StartSession();
		var random = new Random(23);
		for (var round = 0; round < 150; round++)
		{
			var bad = Join();
			Pump(new[] { owner, good, bad });
			Assert.That(bad.Client.State, Is.EqualTo(NetClientState.Connected));
			for (var f = 0; f < 5; f++)
			{
				using var packet = ValidPacket(random);
				var wire = Wire(packet);
				for (var flips = random.Next(1, 4); flips > 0; flips--)
					wire[random.Next(wire.Length)] ^= (byte)(1 << random.Next(8));
				bad.HostLink.InjectInbound(wire);
			}
			Pump(new[] { owner, good, bad });

			// A flipped length can leave the stream waiting for bytes that never come: the session must end it.
			bad.Client.Disconnect("Fuzz round over.");
			Pump(new[] { owner, good, bad }, 40, 500);
			Assert.That(host.GetPeer(bad.Client.SlotId), Is.Null, $"round {round}: the fuzzed connection was never cleaned up");
			if (round % 25 == 0) AssertBystandersFine(owner, good);
		}
		AssertBystandersFine(owner, good);
		Assert.That(kicked.Select(k => k.slot), Has.None.EqualTo(owner.Client.SlotId).And.None.EqualTo(good.Client.SlotId));
	}
}