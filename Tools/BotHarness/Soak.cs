using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using CMS21Together.Shared;
using CMS21Together.Shared.Net;

namespace CMS21Together.BotHarness;

public enum SoakTransport
{
	/// <summary>In-memory links on a virtual clock: an hour of play runs in minutes, fully deterministic.</summary>
	Sim,

	/// <summary>Real loopback TCP sockets on the wall clock.</summary>
	Tcp
}

public sealed class SoakOptions
{
	/// <summary>Players including the host's own (loopback) player.</summary>
	public int Players = 8;

	public double Seconds = 60;
	public SoakTransport Transport = SoakTransport.Sim;
	public int LatencyMs;
	public int JitterMs;
	public double LossRate;
	public long BytesPerSecond;
	public int TickMs = 10;

	/// <summary>Average leave-and-rejoin events per remote bot per minute.</summary>
	public double ChurnPerMinute;

	public int PositionHz = 20;
	public int ReliablePerSecond = 5;
	public int ReliableMaxBytes = 2048;
	public double LargeMessageEverySeconds = 30;
	public int LargeMessageBytes = 300_000;
	public int Seed = 1;
	public Action<string> Progress;
}

public sealed class SoakResult
{
	public readonly List<string> Errors = new();
	public long ReliableSent, ReliableRelayed, ReliableReceived;
	public long UnreliableSent, UnreliableReceived;
	public long LargeMessages;
	public long Joins, Leaves, Kicks;
	public long LogErrors, LogWarnings;
	public int MaxRttMs;
	public double SimulatedSeconds;
	public TimeSpan WallTime;

	public bool Passed => Errors.Count == 0;

	internal void Fail(string message)
	{
		// Keep the report readable: the first failures are the interesting ones.
		if (Errors.Count < 50) Errors.Add(message);
		else if (Errors.Count == 50) Errors.Add("... more errors omitted");
	}

	public override string ToString() =>
		$"{(Passed ? "PASS" : "FAIL")}: {SimulatedSeconds:0}s simulated in {WallTime.TotalSeconds:0.0}s, " +
		$"reliable {ReliableSent} sent / {ReliableRelayed} relayed / {ReliableReceived} received, " +
		$"unreliable {UnreliableSent} sent / {UnreliableReceived} received, large {LargeMessages}, " +
		$"joins {Joins}, leaves {Leaves}, kicks {Kicks}, max RTT {MaxRttMs} ms, log errors {LogErrors}, warnings {LogWarnings}" +
		(Passed ? "" : Environment.NewLine + string.Join(Environment.NewLine, Errors.Select(e => "  - " + e)));
}

/// <summary>
/// Soak test for the network layer: a host with its own loopback player plus remote bots that join, validate like
/// the real client, stream positions (unreliable) and item-like messages (reliable, sometimes chunked), and
/// optionally leave and rejoin. The host relays everything like the game server does. Every bot checks that
/// reliable messages from every sender arrive complete, in order, once and uncorrupted; at the end the traffic is
/// drained and the last sequence numbers must match exactly.
/// </summary>
public sealed class Soak
{
	public const string Version = "soak-1";
	private const string SaveId = "soak-save";

	private readonly SoakOptions options;
	private readonly SoakResult result = new();
	private readonly Random random;
	private readonly Stopwatch wall = new();
	private readonly List<Bot> bots = new();
	private readonly HashSet<int> validated = new();
	private readonly Dictionary<int, int> serverReliableSeq = new();
	private readonly Dictionary<int, int> serverUnreliableSeq = new();
	private readonly HashSet<int> expectedLeaves = new();
	private readonly Dictionary<int, int> slotIncarnation = new();
	private readonly Dictionary<int, int> hostLastReliable = new();
	private readonly Dictionary<(int receiver, int sender), int> lastRelayed = new();
	private NetHost host;
	private PacketRouter router;
	private TcpAcceptor acceptor;
	private long now;
	private int nextIncarnation;
	private bool quiescing;
	private bool stopping;

	public Soak(SoakOptions options)
	{
		this.options = options ?? new SoakOptions();
		random = new Random(this.options.Seed);
	}

	private long Clock() => options.Transport == SoakTransport.Sim ? now : wall.ElapsedMilliseconds;

	public static SoakResult Run(SoakOptions options) => new Soak(options).Run();

	public SoakResult Run()
	{
		var previousSink = Log.Sink;
		var previousLevel = Log.MinLevel;
		Log.Sink = new CountingSink(result, previousSink);
		Log.MinLevel = LogLevel.Warn;
		wall.Start();
		try
		{
			StartHost();
			var endMs = Clock() + (long)(options.Seconds * 1000);
			var progressEvery = Math.Max(1000, (long)(options.Seconds * 100));
			var nextProgress = Clock() + progressEvery;
			while (Clock() < endMs)
			{
				Step();
				if (Clock() < nextProgress) continue;
				nextProgress += progressEvery;
				options.Progress?.Invoke($"{Clock() / 1000}s: {validated.Count} players, reliable {result.ReliableReceived}, errors {result.Errors.Count}");
			}

			// Stop sending, let everything in flight arrive, then compare what was sent with what was received.
			quiescing = true;
			var settle = Clock() + Math.Max(1000, 10 * (options.LatencyMs + options.JitterMs));
			var drainEnd = Clock() + 120000;
			while (Clock() < settle || Clock() < drainEnd && !CaughtUp()) Step();
			Verify();
		}
		catch (Exception ex)
		{
			result.Fail("Harness crashed: " + ex);
		}
		finally
		{
			stopping = true;
			try
			{
				host?.Stop("Soak finished.");
				foreach (var bot in bots) bot.Client.Tick();
			}
			catch (Exception ex)
			{
				result.Fail("Shutdown failed: " + ex.Message);
			}
			acceptor?.Dispose();
			wall.Stop();
			result.WallTime = wall.Elapsed;
			result.SimulatedSeconds = Clock() / 1000.0;
			Log.Sink = previousSink;
			Log.MinLevel = previousLevel;
		}
		return result;
	}

	private void StartHost()
	{
		host = new NetHost(options.Players, Version, Clock);
		router = new PacketRouter("Soak");
		router.PeerMisbehaved += (slot, reason) =>
		{
			result.Fail($"Host: player #{slot} misbehaved: {reason}");
			Kick(slot, reason);
		};
		router.Register((int)PacketTypes.connect, OnConnect);
		router.Register((int)PacketTypes.position, (slot, p) => OnRelay(slot, p, true));
		router.Register((int)PacketTypes.item, (slot, p) => OnRelay(slot, p, false));
		host.PeerConnected += c =>
		{
			using var packet = new Packet((int)PacketTypes.identityRequest);
			packet.Write(SaveId);
			host.Send(c.SlotId, packet.FrameBuffer, packet.FrameLength);
		};
		host.FrameReceived += (c, frame) =>
		{
			if (frame.Type != (int)PacketTypes.connect && !validated.Contains(c.SlotId))
			{
				result.Fail($"Host: packet {frame.Type} from #{c.SlotId} before validation.");
				return;
			}
			router.Dispatch(c.SlotId, frame);
		};
		host.PeerDisconnected += (c, reason) =>
		{
			var slot = c.SlotId;
			validated.Remove(slot);
			slotIncarnation.Remove(slot);
			serverReliableSeq.Remove(slot);
			serverUnreliableSeq.Remove(slot);
			router.ResetPeer(slot);
			if (!expectedLeaves.Remove(slot) && !stopping)
				result.Fail($"Host: player #{slot} disconnected unexpectedly: {reason}");
		};

		if (options.Transport == SoakTransport.Tcp)
			acceptor = new TcpAcceptor(0, link => host.Accept(link), System.Net.IPAddress.Loopback);

		var (serverEnd, clientEnd) = SimLink.CreatePair(null, Clock, "loopback");
		host.Accept(serverEnd);
		AddBot(clientEnd, host.HostKey, true);
		for (var i = 1; i < options.Players; i++) AddRemoteBot();
	}

	private void AddRemoteBot()
	{
		NetLink link;
		if (options.Transport == SoakTransport.Tcp)
			link = new TcpLink("127.0.0.1", acceptor.Port);
		else
		{
			var sim = new SimOptions
			{
				LatencyMs = options.LatencyMs,
				JitterMs = options.JitterMs,
				LossRate = options.LossRate,
				BytesPerSecond = options.BytesPerSecond,
				Seed = random.Next(1, int.MaxValue / 8000)
			};
			var (serverEnd, clientEnd) = SimLink.CreatePair(sim, Clock, "bot");
			// Like Steam and TCP: pings measure RTT and detect dead peers.
			serverEnd.HeartbeatRequired = clientEnd.HeartbeatRequired = true;
			host.Accept(serverEnd);
			link = clientEnd;
		}
		AddBot(link, null, false);
	}

	private void AddBot(NetLink link, byte[] hostKey, bool isHost)
	{
		var bot = new Bot(this, ++nextIncarnation, isHost);
		bots.Add(bot);
		result.Joins++;
		bot.Client.Connect(link, hostKey);
	}

	private void Step()
	{
		if (options.Transport == SoakTransport.Sim) now += options.TickMs;
		else Thread.Sleep(options.TickMs);

		host.Tick();
		foreach (var bot in bots.ToArray())
		{
			bot.Client.Tick();
			if (!quiescing) bot.Act(Clock());
			var rtt = bot.Client.RttMs;
			if (rtt > result.MaxRttMs) result.MaxRttMs = rtt;
		}
		bots.RemoveAll(b => b.Gone);

		if (!quiescing && options.ChurnPerMinute > 0) Churn();
		// Replace bots whose departure the host has processed (also while draining, so the roster ends full).
		while (bots.Count < options.Players && host.PeerCount < options.Players && expectedLeaves.Count == 0) AddRemoteBot();
	}

	private void Churn()
	{
		var chance = options.ChurnPerMinute * options.TickMs / 60000.0;
		foreach (var bot in bots.ToArray())
		{
			if (bot.IsHost || !bot.Validated || bot.Leaving || random.NextDouble() >= chance) continue;
			bot.Leave();
		}
	}

	private void Kick(int slot, string reason)
	{
		result.Kicks++;
		expectedLeaves.Add(slot);
		host.Kick(slot, reason);
	}

	private void OnConnect(int slot, Packet packet)
	{
		var clientIdCheck = packet.ReadInt();
		var name = packet.Read<string>();
		packet.Read<ReadOnlyDictionary<string, bool>>();
		packet.Read<string>(); // game version
		packet.Read<string>(); // mod version
		packet.Read<string>(); // legacy GUID
		var saveId = packet.Read<string>();
		var token = packet.Read<string>();
		if (clientIdCheck != slot || saveId != SaveId || slot != 1 && !IdentityService.IsValidToken(token)
		    || !name.StartsWith("bot", StringComparison.Ordinal) || !int.TryParse(name.Substring(3), out var incarnation))
		{
			result.Fail($"Host: player #{slot} sent an invalid validation.");
			Kick(slot, "Invalid validation.");
			return;
		}
		if (!validated.Add(slot))
		{
			result.Fail($"Host: player #{slot} validated twice.");
			return;
		}
		slotIncarnation[slot] = incarnation;
		serverReliableSeq[slot] = 0;
		serverUnreliableSeq[slot] = -1;
		using var ack = new Packet((int)PacketTypes.userData);
		ack.Write(slot);
		host.Send(slot, ack.FrameBuffer, ack.FrameLength);
	}

	private void OnRelay(int slot, Packet packet, bool unreliable)
	{
		var message = Message.Read(packet);
		if (!message.Intact) result.Fail($"Host: corrupt {(unreliable ? "unreliable" : "reliable")} message from #{slot}.");
		if (unreliable)
		{
			if (message.Seq <= serverUnreliableSeq[slot]) result.Fail($"Host: unreliable message from #{slot} out of order.");
			serverUnreliableSeq[slot] = message.Seq;
		}
		else
		{
			var expected = serverReliableSeq[slot];
			if (message.Seq != expected) result.Fail($"Host: reliable message {message.Seq} from #{slot}, expected {expected}.");
			serverReliableSeq[slot] = message.Seq + 1;
			hostLastReliable[message.Incarnation] = message.Seq;
			result.ReliableRelayed++;
		}
		if (message.Incarnation != slotIncarnation[slot]) result.Fail($"Host: player #{slot} sent a message as bot {message.Incarnation}.");

		using var relay = new Packet(unreliable ? (int)PacketTypes.position : (int)PacketTypes.item);
		message.Write(relay);
		foreach (var peer in host.Peers.ToArray())
		{
			if (peer.SlotId == slot || !validated.Contains(peer.SlotId)) continue;
			peer.Send(relay.FrameBuffer, relay.FrameLength, SendPriority.Normal, unreliable);
			if (!unreliable) lastRelayed[(slotIncarnation[peer.SlotId], message.Incarnation)] = message.Seq;
		}
	}

	/// <summary>
	/// Live senders whose whole stream reached the host, and live receivers that got exactly the last message the
	/// host relayed to them from each live sender (a player who joined after a sender went quiet is owed nothing).
	/// </summary>
	private IEnumerable<string> Gaps()
	{
		var live = bots.Where(b => b.Validated && !b.Gone).ToList();
		foreach (var sender in live)
		{
			if (sender.ReliableSeq == 0) continue;
			var atHost = hostLastReliable.TryGetValue(sender.Incarnation, out var h) ? h : -1;
			if (atHost != sender.ReliableSeq - 1)
				yield return $"The host got up to {atHost} from bot {sender.Incarnation}, which sent up to {sender.ReliableSeq - 1}.";
		}
		foreach (var receiver in live)
		foreach (var sender in live)
		{
			if (receiver == sender || !lastRelayed.TryGetValue((receiver.Incarnation, sender.Incarnation), out var relayed)) continue;
			var got = receiver.LastReliable.TryGetValue(sender.Incarnation, out var last) ? last : -1;
			if (got != relayed)
				yield return $"Bot {receiver.Incarnation} got up to {got} from bot {sender.Incarnation}; the host relayed up to {relayed}.";
		}
	}

	private bool CaughtUp() => bots.Count == options.Players && bots.All(b => b.Validated) && !Gaps().Any();

	private void Verify()
	{
		var live = bots.Where(b => b.Validated && !b.Gone).ToList();
		if (live.Count < options.Players) result.Fail($"Only {live.Count} of {options.Players} players were connected at the end.");
		foreach (var gap in Gaps()) result.Fail(gap);
		var pairs = live.Sum(r => live.Count(s => r != s && lastRelayed.ContainsKey((r.Incarnation, s.Incarnation))));
		if (live.Count > 1 && result.ReliableSent > 0 && pairs == 0) result.Fail("No reliable message was relayed between live players.");
		if (options.LossRate == 0 && result.UnreliableSent > 0 && result.UnreliableReceived == 0)
			result.Fail("No unreliable message arrived.");
	}

	private readonly struct Message
	{
		public readonly int Incarnation;
		public readonly int Seq;
		public readonly byte[] Data;
		public readonly uint Crc;

		public Message(int incarnation, int seq, byte[] data)
		{
			Incarnation = incarnation;
			Seq = seq;
			Data = data;
			Crc = Crc32.Compute(data);
		}

		private Message(int incarnation, int seq, byte[] data, uint crc)
		{
			Incarnation = incarnation;
			Seq = seq;
			Data = data;
			Crc = crc;
		}

		public bool Intact => Crc32.Compute(Data) == Crc;

		public void Write(Packet packet)
		{
			packet.Write(Incarnation);
			packet.Write(Seq);
			packet.Write<byte[]>(Data);
			packet.Write<uint>(Crc);
		}

		public static Message Read(Packet packet) =>
			new(packet.ReadInt(), packet.ReadInt(), packet.Read<byte[]>(), packet.Read<uint>());
	}

	private sealed class Bot
	{
		private readonly Soak soak;
		private readonly Random random;
		private readonly Dictionary<int, int> lastUnreliable = new();
		private long nextPosition, nextReliable, nextLarge;
		private int unreliableSeq;

		public Bot(Soak soak, int incarnation, bool isHost)
		{
			this.soak = soak;
			Incarnation = incarnation;
			IsHost = isHost;
			random = new Random(soak.options.Seed * 31 + incarnation);
			Client = new NetClient(Version, soak.Clock);
			Client.FrameReceived += OnFrame;
			Client.Disconnected += OnDisconnected;
		}

		public readonly NetClient Client;
		public readonly int Incarnation;
		public readonly bool IsHost;
		public readonly Dictionary<int, int> LastReliable = new();
		public bool Validated;
		public bool Leaving;
		public bool Gone;
		public int ReliableSeq;

		public void Act(long time)
		{
			if (!Validated || Leaving) return;
			var options = soak.options;
			if (time >= nextPosition)
			{
				nextPosition = time + 1000 / Math.Max(1, options.PositionHz);
				Send(new Message(Incarnation, unreliableSeq++, Bytes(12)), true);
				soak.result.UnreliableSent++;
			}
			if (time >= nextReliable)
			{
				nextReliable = time + random.Next(1, 2 * 1000 / Math.Max(1, options.ReliablePerSecond));
				Send(new Message(Incarnation, ReliableSeq++, Bytes(random.Next(options.ReliableMaxBytes + 1))), false);
				soak.result.ReliableSent++;
			}
			if (options.LargeMessageEverySeconds > 0 && time >= nextLarge)
			{
				var first = nextLarge == 0;
				nextLarge = time + (long)(options.LargeMessageEverySeconds * 1000 * (0.5 + random.NextDouble()));
				if (first) return;
				Send(new Message(Incarnation, ReliableSeq++, Bytes(options.LargeMessageBytes)), false);
				soak.result.ReliableSent++;
				soak.result.LargeMessages++;
			}
		}

		public void Leave()
		{
			Leaving = true;
			Gone = true;
			soak.result.Leaves++;
			soak.expectedLeaves.Add(Client.SlotId);
			Client.Disconnect("Bot leaving.");
		}

		private byte[] Bytes(int count)
		{
			var data = new byte[count];
			random.NextBytes(data);
			return data;
		}

		private void Send(Message message, bool unreliable)
		{
			using var packet = new Packet(unreliable ? (int)PacketTypes.position : (int)PacketTypes.item);
			message.Write(packet);
			var queued = Client.Send(packet.FrameBuffer, packet.FrameLength, SendPriority.Normal, unreliable);
			if (!unreliable && queued != EnqueueResult.Queued)
				soak.result.Fail($"Bot {Incarnation}: reliable send was not queued ({queued}).");
		}

		private void OnFrame(NetFrame frame)
		{
			try
			{
				using var packet = new Packet(frame);
				switch ((PacketTypes)frame.Type)
				{
					case PacketTypes.identityRequest:
						Validate(packet.Read<string>());
						break;
					case PacketTypes.userData:
						if (packet.ReadInt() != Client.SlotId) soak.result.Fail($"Bot {Incarnation}: acknowledged with the wrong slot.");
						Validated = true;
						break;
					case PacketTypes.position:
						Receive(Message.Read(packet), true);
						break;
					case PacketTypes.item:
						Receive(Message.Read(packet), false);
						break;
					default:
						soak.result.Fail($"Bot {Incarnation}: unexpected packet {frame.Type}.");
						break;
				}
			}
			catch (Exception ex)
			{
				soak.result.Fail($"Bot {Incarnation}: handling packet {frame.Type} threw {ex.GetType().Name}: {ex.Message}");
			}
		}

		private void Validate(string saveId)
		{
			using var packet = new Packet((int)PacketTypes.connect);
			packet.Write(Client.SlotId);
			packet.Write($"bot{Incarnation}");
			packet.Write(new ReadOnlyDictionary<string, bool>(new Dictionary<string, bool> { ["dlc"] = true }));
			packet.Write("game-1");
			packet.Write(Version);
			packet.Write(Guid.NewGuid().ToString());
			packet.Write(saveId);
			packet.Write(IdentityService.NewToken());
			Client.Send(packet.FrameBuffer, packet.FrameLength);
		}

		private void Receive(Message message, bool unreliable)
		{
			var result = soak.result;
			if (!message.Intact) result.Fail($"Bot {Incarnation}: corrupt message from bot {message.Incarnation}.");
			if (message.Incarnation == Incarnation) result.Fail($"Bot {Incarnation}: received its own message back.");
			if (unreliable)
			{
				if (lastUnreliable.TryGetValue(message.Incarnation, out var previous) && message.Seq <= previous)
					result.Fail($"Bot {Incarnation}: unreliable {message.Seq} from bot {message.Incarnation} after {previous}.");
				lastUnreliable[message.Incarnation] = message.Seq;
				result.UnreliableReceived++;
				return;
			}

			// The first message seen from a sender can be anywhere in its stream (it may have joined earlier).
			if (LastReliable.TryGetValue(message.Incarnation, out var last) && message.Seq != last + 1)
				result.Fail($"Bot {Incarnation}: reliable {message.Seq} from bot {message.Incarnation}, expected {last + 1}.");
			LastReliable[message.Incarnation] = message.Seq;
			result.ReliableReceived++;
		}

		private void OnDisconnected(string reason)
		{
			Gone = true;
			if (!Leaving && !soak.stopping) soak.result.Fail($"Bot {Incarnation} (slot {Client.SlotId}) was disconnected: {reason}");
		}
	}

	private sealed class CountingSink : ILogSink
	{
		private readonly SoakResult result;
		private readonly ILogSink inner;

		public CountingSink(SoakResult result, ILogSink inner)
		{
			this.result = result;
			this.inner = inner;
		}

		public void Write(LogLevel level, string message)
		{
			if (level >= LogLevel.Error)
			{
				result.LogErrors++;
				result.Fail("Logged error: " + message);
			}
			else if (level == LogLevel.Warn) result.LogWarnings++;
			inner?.Write(level, message);
		}
	}
}