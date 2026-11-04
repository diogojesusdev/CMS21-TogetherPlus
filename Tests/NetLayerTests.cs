using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using CMS21Together.Shared.Net;
using NUnit.Framework;

namespace CMS21Together.Tests;

[TestFixture]
public class NetLayerTests
{
	private const string ModVersion = "test-1";
	private long now;

	private long Clock() => now;

	private static byte[] Logical(ushort type, byte[] payload) => FrameCodec.Encode(type, FrameFlags.None, 0, payload);

	private static byte[] RandomBytes(int count, int seed)
	{
		var data = new byte[count];
		new Random(seed).NextBytes(data);
		return data;
	}

	private sealed class TestPeer
	{
		public NetClient Client;
		public SimLink ClientLink;
		public SimLink HostLink;
		public readonly List<string> Disconnects = new();
		public readonly List<NetFrame> Frames = new();
		public int ConnectedCount;
	}

	private sealed class HostLog
	{
		public readonly List<int> Connected = new();
		public readonly List<(int slot, string reason)> Disconnected = new();
		public readonly List<(int slot, NetFrame frame)> Frames = new();

		public HostLog(NetHost host)
		{
			host.PeerConnected += c => Connected.Add(c.SlotId);
			host.PeerDisconnected += (c, r) => Disconnected.Add((c.SlotId, r));
			host.FrameReceived += (c, f) => Frames.Add((c.SlotId, f));
		}
	}

	private NetHost NewHost(int maxPlayers = 4) => new(maxPlayers, ModVersion, Clock);

	private TestPeer Join(NetHost host, byte[] hostKey = null, string version = ModVersion, SimOptions options = null)
	{
		var (a, b) = SimLink.CreatePair(options, Clock);
		var peer = new TestPeer { Client = new NetClient(version, Clock), ClientLink = a, HostLink = b };
		peer.Client.Connected += () => peer.ConnectedCount++;
		peer.Client.Disconnected += r => peer.Disconnects.Add(r);
		peer.Client.FrameReceived += f => peer.Frames.Add(f);
		peer.Client.Connect(a, hostKey);
		host.Accept(b);
		return peer;
	}

	private void Pump(NetHost host, IEnumerable<TestPeer> peers, int rounds = 6, int stepMs = 0)
	{
		var list = peers.ToList();
		for (var i = 0; i < rounds; i++)
		{
			now += stepMs;
			host.Tick();
			foreach (var p in list) p.Client.Tick();
		}
	}

	[Test]
	public void HostKeyGetsSlotOneAndOthersGetLowestFreeSlot()
	{
		var host = NewHost();
		var log = new HostLog(host);
		var owner = Join(host, host.HostKey);
		var p2 = Join(host);
		var p3 = Join(host);
		Pump(host, new[] { owner, p2, p3 });

		Assert.That(owner.Client.SlotId, Is.EqualTo(1));
		Assert.That(p2.Client.SlotId, Is.EqualTo(2));
		Assert.That(p3.Client.SlotId, Is.EqualTo(3));
		Assert.That(p3.Client.MaxPlayers, Is.EqualTo(4));
		Assert.That(log.Connected, Is.EqualTo(new[] { 1, 2, 3 }));
		Assert.That(new[] { owner, p2, p3 }.Select(p => p.ConnectedCount), Is.All.EqualTo(1));
		Assert.That(host.PeerCount, Is.EqualTo(3));
	}

	[Test]
	public void ClientWithoutTheKeyNeverGetsSlotOne()
	{
		var host = NewHost();
		var p = Join(host, new byte[NetProtocol.HostKeyLength]);
		Pump(host, new[] { p });
		Assert.That(p.Client.SlotId, Is.EqualTo(2));
		Assert.That(host.GetPeer(1), Is.Null);
	}

	[Test]
	public void SecondHostKeyIsRejected()
	{
		var host = NewHost();
		var owner = Join(host, host.HostKey);
		Pump(host, new[] { owner });
		var impostor = Join(host, host.HostKey);
		Pump(host, new[] { owner, impostor });

		Assert.That(impostor.Client.State, Is.EqualTo(NetClientState.Closed));
		Assert.That(impostor.Disconnects, Is.EqualTo(new[] { "The host is already connected." }));
		Assert.That(owner.Client.State, Is.EqualTo(NetClientState.Connected));
	}

	[Test]
	public void FullServerRejectsWithAReadableReason()
	{
		var host = NewHost(2);
		var log = new HostLog(host);
		var owner = Join(host, host.HostKey);
		var p2 = Join(host);
		Pump(host, new[] { owner, p2 });
		var p3 = Join(host);
		Pump(host, new[] { owner, p2, p3 });

		Assert.That(p3.ConnectedCount, Is.Zero);
		Assert.That(p3.Disconnects, Has.Count.EqualTo(1));
		Assert.That(p3.Disconnects[0], Does.Contain("full"));
		Assert.That(log.Connected, Is.EqualTo(new[] { 1, 2 }));
		Assert.That(log.Disconnected, Is.Empty);
	}

	[Test]
	public void ModVersionMismatchIsRejected()
	{
		var host = NewHost();
		var p = Join(host, version: "other");
		Pump(host, new[] { p });
		Assert.That(p.Disconnects.Single(), Does.Contain("Mod version mismatch").And.Contain(ModVersion).And.Contain("other"));
		Assert.That(host.PeerCount, Is.Zero);
	}

	[Test]
	public void ProtocolMismatchIsRejectedWithoutParsingTheRest()
	{
		var host = NewHost();
		var (a, b) = SimLink.CreatePair(null, Clock);
		var conn = new List<NetFrame>();
		a.FrameReceived += (_, f) => conn.Add(f);
		host.Accept(b);
		// A future client: protocol 99 followed by bytes this version cannot parse.
		var hello = new NetWriter();
		hello.WriteUInt32(NetProtocol.Magic);
		hello.WriteUInt16(99);
		hello.WriteBytes(new byte[] { 0xFF, 0xFF, 0xFF });
		a.Send(new[] { Logical(NetFrameTypes.Hello, hello.ToArray()) }, SendPriority.Normal, false);
		for (var i = 0; i < 4; i++)
		{
			host.Tick();
			a.Poll();
		}
		var reject = conn.Single(f => f.Type == NetFrameTypes.Reject);
		Assert.That(NetProtocol.DecodeReason(reject), Does.Contain("Incompatible Together+ version"));
		Assert.That(host.PeerCount, Is.Zero);
	}

	[Test]
	public void BadMagicDropsTheConnectionSilently()
	{
		var host = NewHost();
		var log = new HostLog(host);
		var (a, b) = SimLink.CreatePair(null, Clock);
		host.Accept(b);
		a.Send(new[] { Logical(NetFrameTypes.Hello, new byte[] { 1, 2, 3, 4, 5, 6 }) }, SendPriority.Normal, false);
		for (var i = 0; i < 4; i++) host.Tick();
		Assert.That(b.IsClosed, Is.True);
		Assert.That(log.Connected, Is.Empty);
		Assert.That(log.Disconnected, Is.Empty);
	}

	[Test]
	public void GameFrameBeforeHandshakeIsRejected()
	{
		var host = NewHost();
		var log = new HostLog(host);
		var (a, b) = SimLink.CreatePair(null, Clock);
		host.Accept(b);
		a.Send(new[] { Logical(5, new byte[] { 1 }) }, SendPriority.Normal, false);
		for (var i = 0; i < 4; i++) host.Tick();
		Assert.That(b.IsClosing, Is.True);
		Assert.That(log.Frames, Is.Empty);
	}

	[Test]
	public void SilentConnectionTimesOutDuringHandshake()
	{
		var host = NewHost();
		var log = new HostLog(host);
		var (_, b) = SimLink.CreatePair(null, Clock);
		host.Accept(b);
		host.Tick();
		now += NetProtocol.HandshakeTimeoutMs - 1;
		host.Tick();
		Assert.That(b.IsClosing, Is.False);
		now += 2;
		host.Tick();
		Assert.That(b.IsClosed, Is.True);
		Assert.That(b.CloseReason, Does.Contain("Handshake timed out"));
		Assert.That(log.Connected, Is.Empty);
		Assert.That(log.Disconnected, Is.Empty);
	}

	[Test]
	public void ClientGivesUpWhenTheHostNeverAnswers()
	{
		var (a, _) = SimLink.CreatePair(null, Clock);
		var client = new NetClient(ModVersion, Clock);
		var reasons = new List<string>();
		client.Disconnected += reasons.Add;
		client.Connect(a);
		now += NetProtocol.ClientConnectTimeoutMs + 1;
		client.Tick();
		client.Tick();
		Assert.That(reasons, Has.Count.EqualTo(1));
		Assert.That(reasons[0], Does.Contain("did not answer"));
		Assert.That(client.State, Is.EqualTo(NetClientState.Closed));
	}

	[Test]
	public void HeartbeatKeepsAnIdleConnectionAliveAndMeasuresRtt()
	{
		var host = NewHost();
		var log = new HostLog(host);
		var p = Join(host, options: new SimOptions { LatencyMs = 40 });
		p.ClientLink.HeartbeatRequired = true;
		p.HostLink.HeartbeatRequired = true;
		Pump(host, new[] { p }, rounds: 600, stepMs: 100);

		Assert.That(p.Client.State, Is.EqualTo(NetClientState.Connected));
		Assert.That(log.Disconnected, Is.Empty);
		Assert.That(p.Client.RttMs, Is.InRange(80, 300));
		Assert.That(host.GetPeer(2).RttMs, Is.InRange(80, 300));
	}

	[Test]
	public void EveryoneStallingAtOnceDoesNotDisconnect()
	{
		var host = NewHost();
		var log = new HostLog(host);
		var p = Join(host, options: new SimOptions { LatencyMs = 40 });
		p.ClientLink.HeartbeatRequired = true;
		p.HostLink.HeartbeatRequired = true;
		Pump(host, new[] { p }, rounds: 20, stepMs: 100);

		// Host and client both load the garage: no tick, no ping, for longer than the timeout.
		now += 3 * NetProtocol.TimeoutMs;
		Pump(host, new[] { p }, rounds: 30, stepMs: 100);

		Assert.That(log.Disconnected, Is.Empty);
		Assert.That(p.Disconnects, Is.Empty);
		Assert.That(p.Client.State, Is.EqualTo(NetClientState.Connected));
	}

	[Test]
	public void DeadPeerStillTimesOutAfterALocalStall()
	{
		var host = NewHost();
		var log = new HostLog(host);
		var p = Join(host);
		p.ClientLink.HeartbeatRequired = true;
		p.HostLink.HeartbeatRequired = true;
		Pump(host, new[] { p });
		p.ClientLink.Frozen = true;

		now += 2 * NetProtocol.TimeoutMs;
		host.Tick();
		Assert.That(log.Disconnected, Is.Empty, "a stall alone must not time anyone out");
		for (var t = 0; t <= NetProtocol.TimeoutMs + 1000; t += 500)
		{
			now += 500;
			host.Tick();
		}
		Assert.That(log.Disconnected.Select(d => d.reason), Is.EqualTo(new[] { "Connection timed out." }));
	}

	[Test]
	public void DeadPeerTimesOutOnBothSides()
	{
		var host = NewHost();
		var log = new HostLog(host);
		var p = Join(host);
		p.ClientLink.HeartbeatRequired = true;
		p.HostLink.HeartbeatRequired = true;
		Pump(host, new[] { p });
		p.ClientLink.Frozen = true;

		Pump(host, new[] { p }, rounds: (NetProtocol.TimeoutMs / 500) - 1, stepMs: 500);
		Assert.That(log.Disconnected, Is.Empty);
		Pump(host, new[] { p }, rounds: 4, stepMs: 500);

		Assert.That(log.Disconnected, Has.Count.EqualTo(1));
		Assert.That(log.Disconnected[0].reason, Does.Contain("timed out"));
		Assert.That(p.Disconnects, Has.Count.EqualTo(1));
		Assert.That(p.Disconnects[0], Does.Contain("timed out"));
	}

	[Test]
	public void MalformedPeerIsDroppedWithoutAffectingOthers()
	{
		var host = NewHost();
		var log = new HostLog(host);
		var owner = Join(host, host.HostKey);
		var bad = Join(host);
		var good = Join(host);
		Pump(host, new[] { owner, bad, good });

		// Valid length prefix, then a header claiming an impossible length.
		bad.HostLink.InjectInbound(new byte[] { 3, 0, 0, 0, 1, 2, 3 });
		Pump(host, new[] { owner, bad, good });

		Assert.That(log.Disconnected.Select(d => d.slot), Is.EqualTo(new[] { 2 }));
		Assert.That(log.Disconnected[0].reason, Does.Contain("Protocol error"));
		Assert.That(bad.Disconnects, Has.Count.EqualTo(1));

		var payload = new byte[] { 42 };
		good.Client.Send(Logical(7, payload), FrameCodec.HeaderSize + 1);
		owner.Client.Send(Logical(8, payload), FrameCodec.HeaderSize + 1);
		Pump(host, new[] { owner, bad, good });
		Assert.That(log.Frames.Select(f => (f.slot, (int)f.frame.Type)), Is.EquivalentTo(new[] { (3, 7), (1, 8) }));
		Assert.That(owner.Client.State, Is.EqualTo(NetClientState.Connected));
		Assert.That(good.Client.State, Is.EqualTo(NetClientState.Connected));
	}

	[Test]
	public void CorruptChunkDropsOnlyThatPeer()
	{
		var host = NewHost();
		var log = new HostLog(host);
		var bad = Join(host);
		var good = Join(host);
		Pump(host, new[] { bad, good });

		// A chunk that does not start at offset 0.
		var chunk = new NetWriter();
		chunk.WriteUInt32(1);
		chunk.WriteUInt32(200000);
		chunk.WriteUInt32(5000);
		chunk.WriteUInt32(0);
		chunk.WriteBytes(new byte[16]);
		bad.HostLink.InjectInbound(FrameCodec.Encode(NetFrameTypes.Chunk, FrameFlags.Chunk, 1, chunk.ToArray()));
		Pump(host, new[] { bad, good });

		Assert.That(log.Disconnected.Select(d => d.slot), Is.EqualTo(new[] { 2 }));
		Assert.That(good.Client.State, Is.EqualTo(NetClientState.Connected));
	}

	[Test]
	public void ControlFrameAfterHandshakeDisconnectsThePeer()
	{
		var host = NewHost();
		var log = new HostLog(host);
		var p = Join(host);
		Pump(host, new[] { p });
		p.ClientLink.Send(new[] { Logical(NetFrameTypes.Welcome, new byte[8]) }, SendPriority.Normal, false);
		Pump(host, new[] { p });
		Assert.That(log.Disconnected, Has.Count.EqualTo(1));
		Assert.That(p.Disconnects.Single(), Does.Contain("Unexpected control frame"));
	}

	[Test]
	public void KickDeliversTheReasonAndRaisesDisconnectOnce()
	{
		var host = NewHost();
		var log = new HostLog(host);
		var p = Join(host);
		Pump(host, new[] { p });

		host.Kick(2, "Kicked by the host.");
		Pump(host, new[] { p });

		Assert.That(p.Disconnects, Is.EqualTo(new[] { "Kicked by the host." }));
		Assert.That(log.Disconnected, Is.EqualTo(new[] { (2, "Kicked by the host.") }));
		Assert.That(host.GetPeer(2), Is.Null);
	}

	[Test]
	public void FramesSentBeforeAKickStillArrive()
	{
		var host = NewHost();
		var p = Join(host);
		Pump(host, new[] { p });
		var payload = RandomBytes(300000, 3);
		host.Send(2, Logical(9, payload), FrameCodec.HeaderSize + payload.Length);
		host.Kick(2, "Bye now.");
		Pump(host, new[] { p });

		Assert.That(p.Frames.Single().CopyPayload().SequenceEqual(payload));
		Assert.That(p.Disconnects, Is.EqualTo(new[] { "Bye now." }));
	}

	[Test]
	public void ClientLeavingTellsTheHostWhyAndDoesNotRaiseItsOwnDisconnect()
	{
		var host = NewHost();
		var log = new HostLog(host);
		var p = Join(host);
		Pump(host, new[] { p });

		p.Client.Disconnect("Left the session.");
		Pump(host, new[] { p });

		Assert.That(log.Disconnected, Is.EqualTo(new[] { (2, "Left the session.") }));
		Assert.That(p.Disconnects, Is.Empty);
		Assert.That(p.Client.State, Is.EqualTo(NetClientState.Closed));
	}

	[Test]
	public void SlotIsReusedOnlyAfterItsDisconnectWasHandled()
	{
		var host = NewHost(3);
		var log = new HostLog(host);
		var owner = Join(host, host.HostKey);
		var p2 = Join(host);
		var p3 = Join(host);
		Pump(host, new[] { owner, p2, p3 });
		p2.Client.Disconnect();
		Pump(host, new[] { owner, p2, p3 });
		var p4 = Join(host);
		Pump(host, new[] { owner, p4 });

		Assert.That(p4.Client.SlotId, Is.EqualTo(2));
		Assert.That(log.Connected, Is.EqualTo(new[] { 1, 2, 3, 2 }));
		Assert.That(log.Disconnected.Select(d => d.slot), Is.EqualTo(new[] { 2 }));
	}

	[Test]
	public void StopDisconnectsEveryoneExactlyOnce()
	{
		var host = NewHost();
		var log = new HostLog(host);
		var owner = Join(host, host.HostKey);
		var p2 = Join(host);
		Pump(host, new[] { owner, p2 });

		host.Stop("The host closed the session.");
		Assert.That(log.Disconnected.Select(d => d.slot), Is.EquivalentTo(new[] { 1, 2 }));
		Pump(host, new[] { owner, p2 });

		Assert.That(log.Disconnected, Has.Count.EqualTo(2));
		Assert.That(p2.Disconnects, Is.EqualTo(new[] { "The host closed the session." }));
		Assert.That(host.IsRunning, Is.False);

		var late = Join(host);
		Pump(host, new[] { late });
		Assert.That(late.ConnectedCount, Is.Zero);
		Assert.That(late.Disconnects, Has.Count.EqualTo(1));
	}

	[Test]
	public void DisconnectHandlerExceptionStillFreesTheSlot()
	{
		var host = NewHost(2);
		host.PeerDisconnected += (_, _) => throw new InvalidOperationException("boom");
		var p = Join(host);
		Pump(host, new[] { p });
		p.Client.Disconnect();
		Pump(host, new[] { p });
		Assert.That(host.GetPeer(2), Is.Null);
		var again = Join(host);
		Pump(host, new[] { again });
		Assert.That(again.Client.SlotId, Is.EqualTo(2));
	}

	[Test]
	public void LargeMessagesSurviveLatencyJitterAndLossInOrder()
	{
		var host = NewHost();
		var log = new HostLog(host);
		var p = Join(host, options: new SimOptions { LatencyMs = 80, JitterMs = 40, LossRate = 0.2, BytesPerSecond = 4 * 1024 * 1024, Seed = 7 });
		Pump(host, new[] { p }, rounds: 20, stepMs: 50);
		Assert.That(p.Client.State, Is.EqualTo(NetClientState.Connected));

		var sent = new List<byte[]>();
		for (var i = 0; i < 12; i++)
		{
			var payload = RandomBytes(i % 3 == 0 ? 1_500_000 : 100 + i, i);
			sent.Add(payload);
			p.Client.Send(Logical((ushort)(100 + i), payload), FrameCodec.HeaderSize + payload.Length);
		}
		Pump(host, new[] { p }, rounds: 400, stepMs: 25);

		Assert.That(log.Frames.Select(f => (int)f.frame.Type), Is.EqualTo(Enumerable.Range(100, 12)));
		for (var i = 0; i < sent.Count; i++) Assert.That(log.Frames[i].frame.CopyPayload().SequenceEqual(sent[i]), $"message {i}");
	}

	[Test]
	public void UnreliableFramesMayBeLostButNeverCorrupted()
	{
		var host = NewHost();
		var log = new HostLog(host);
		var p = Join(host, options: new SimOptions { LatencyMs = 20, LossRate = 0.3, Seed = 3 });
		Pump(host, new[] { p }, rounds: 20, stepMs: 20);
		for (var i = 0; i < 200; i++)
			p.Client.Send(Logical(50, BitConverter.GetBytes(i)), FrameCodec.HeaderSize + 4, unreliable: true);
		Pump(host, new[] { p }, rounds: 50, stepMs: 20);

		var received = log.Frames.Select(f => BitConverter.ToInt32(f.frame.CopyPayload(), 0)).ToList();
		Assert.That(received.Count, Is.InRange(80, 190));
		Assert.That(received, Is.Ordered.And.Unique);
		Assert.That(log.Frames.Select(f => f.frame.Flags & FrameFlags.Unreliable), Is.All.EqualTo(FrameFlags.Unreliable));
	}

	[Test]
	[Timeout(30000)]
	public void APlayerLoadingASceneKeepsAnsweringPings()
	{
		// Phase 5, load-aware heartbeat: a player's main thread can freeze while a scene loads. Pings are answered on
		// the link's receive thread, so the host keeps hearing from it and never times it out for loading.
		var host = new NetHost(4, ModVersion);
		var log = new HostLog(host);
		using var acceptor = new TcpAcceptor(0, host.Accept, IPAddress.Loopback);
		var client = new NetClient(ModVersion);
		client.Connect(new TcpLink("127.0.0.1", acceptor.Port));
		var watch = Stopwatch.StartNew();
		while (!(client.State == NetClientState.Connected && log.Connected.Count == 1))
		{
			if (watch.ElapsedMilliseconds > 15000) Assert.Fail("Timed out waiting for the handshake");
			host.Tick();
			client.Tick();
			Thread.Sleep(2);
		}

		var peer = host.GetPeer(2);
		var frozenAt = peer.LastReceiveMs;
		// The player's game freezes for 3 s: no Tick at all. The host carries on.
		watch.Restart();
		while (watch.ElapsedMilliseconds < 3000)
		{
			host.Tick();
			Thread.Sleep(20);
		}

		Assert.That(peer.LastReceiveMs - frozenAt, Is.GreaterThanOrEqualTo(2000), "pongs kept arriving from the frozen player");
		Assert.That(log.Disconnected, Is.Empty);
		Assert.That(host.GetPeer(2), Is.Not.Null);
	}

	[Test]
	[Timeout(30000)]
	public void TcpLoopbackCarriesLargeMessagesAndGoodbyes()
	{
		var host = new NetHost(4, ModVersion);
		var log = new HostLog(host);
		using var acceptor = new TcpAcceptor(0, host.Accept, IPAddress.Loopback);

		var client = new NetClient(ModVersion);
		var clientFrames = new List<NetFrame>();
		var clientDisconnects = new List<string>();
		client.FrameReceived += clientFrames.Add;
		client.Disconnected += clientDisconnects.Add;
		client.Connect(new TcpLink("127.0.0.1", acceptor.Port));

		void PumpUntil(Func<bool> condition, string what)
		{
			var watch = Stopwatch.StartNew();
			while (!condition())
			{
				if (watch.ElapsedMilliseconds > 15000) Assert.Fail("Timed out waiting for " + what);
				host.Tick();
				client.Tick();
				Thread.Sleep(2);
			}
		}

		PumpUntil(() => client.State == NetClientState.Connected && log.Connected.Count == 1, "the handshake");
		Assert.That(client.SlotId, Is.EqualTo(2));

		var up = RandomBytes(3_000_000, 1);
		var down = RandomBytes(2_000_000, 2);
		client.Send(Logical(11, up), FrameCodec.HeaderSize + up.Length);
		host.Send(2, Logical(12, down), FrameCodec.HeaderSize + down.Length);
		PumpUntil(() => log.Frames.Count == 1 && clientFrames.Count == 1, "the large messages");
		Assert.That(log.Frames[0].frame.CopyPayload().SequenceEqual(up));
		Assert.That(clientFrames[0].CopyPayload().SequenceEqual(down));

		host.Kick(2, "Kicked over TCP.");
		PumpUntil(() => clientDisconnects.Count == 1 && log.Disconnected.Count == 1, "the kick");
		Assert.That(clientDisconnects[0], Is.EqualTo("Kicked over TCP."));
		Assert.That(log.Disconnected[0].reason, Is.EqualTo("Kicked over TCP."));

		var second = new NetClient(ModVersion);
		second.Connect(new TcpLink("127.0.0.1", acceptor.Port));
		PumpUntil(() =>
		{
			second.Tick();
			return second.State == NetClientState.Connected;
		}, "the second client");
		Assert.That(second.SlotId, Is.EqualTo(2));
		second.Disconnect("Closing the game.");
		PumpUntil(() =>
		{
			second.Tick();
			return log.Disconnected.Count == 2;
		}, "the client goodbye");
		Assert.That(log.Disconnected[1].reason, Is.EqualTo("Closing the game."));
	}

	[Test]
	[Timeout(20000)]
	public void TcpConnectFailureIsReported()
	{
		int port;
		using (var probe = new TcpAcceptor(0, _ => { }, IPAddress.Loopback)) port = probe.Port;
		var client = new NetClient(ModVersion);
		var reasons = new List<string>();
		client.Disconnected += reasons.Add;
		client.Connect(new TcpLink("127.0.0.1", port, 3000));
		var watch = Stopwatch.StartNew();
		while (reasons.Count == 0 && watch.ElapsedMilliseconds < 15000)
		{
			client.Tick();
			Thread.Sleep(5);
		}
		Assert.That(reasons, Has.Count.EqualTo(1));
		Assert.That(reasons[0], Does.Contain("Could not connect"));
	}
}
