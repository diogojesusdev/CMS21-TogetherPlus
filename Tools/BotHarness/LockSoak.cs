using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using CMS21Together.Shared;
using CMS21Together.Shared.Locks;
using CMS21Together.Shared.Net;

namespace CMS21Together.BotHarness;

public sealed class LockSoakOptions
{
	/// <summary>Players including the host's own (loopback) player.</summary>
	public int Players = 8;

	public double Seconds = 120;
	public int LatencyMs;
	public int JitterMs;
	public double LossRate;
	public int TickMs = 10;

	/// <summary>Average new actions per bot per second while idle.</summary>
	public double ActionsPerSecond = 0.7;

	/// <summary>Share of actions that ignore the local lock table, like a player acting before a foreign grant arrived.</summary>
	public double RaceRate = 0.15;

	/// <summary>Games that freeze for good (crash, power loss) per remote bot per minute.</summary>
	public double KillsPerMinute = 0.3;

	/// <summary>Scene changes (leaving the garage) per bot per minute.</summary>
	public double SceneChangesPerMinute = 0.5;

	public int Seed = 1;
	public Action<string> Progress;
}

public sealed class LockSoakResult
{
	public readonly List<string> Errors = new();
	public long Requests, Grants, Denials, LocalBlocks;
	public long WritesAccepted, WritesRejected, RaceWrites;
	public long Kills, SceneChanges, Joins;
	public long MaxKillFreeMs;
	public int MaxConcurrentGrants;
	public long LogErrors;
	public double SimulatedSeconds;
	public TimeSpan WallTime;

	public bool Passed => Errors.Count == 0;

	internal void Fail(string message)
	{
		if (Errors.Count < 50) Errors.Add(message);
		else if (Errors.Count == 50) Errors.Add("... more errors omitted");
	}

	public override string ToString() =>
		$"{(Passed ? "PASS" : "FAIL")}: {SimulatedSeconds:0}s simulated in {WallTime.TotalSeconds:0.0}s, " +
		$"requests {Requests}, grants {Grants}, denials {Denials}, local blocks {LocalBlocks}, " +
		$"writes {WritesAccepted} accepted / {WritesRejected} rejected ({RaceWrites} racing), " +
		$"kills {Kills} (locks freed within {MaxKillFreeMs} ms), scene changes {SceneChanges}, joins {Joins}, " +
		$"max concurrent grants {MaxConcurrentGrants}, log errors {LogErrors}" +
		(Passed ? "" : Environment.NewLine + string.Join(Environment.NewLine, Errors.Select(e => "  - " + e)));
}

/// <summary>
/// Lock contention soak: the real <see cref="LockServer"/> and <see cref="LockClient"/> over the real net stack on
/// simulated links. Bots hammer a few hot keys (a whole car, its parts, a panel, a lifter, an engine stand), write
/// fenced state, change scenes and sometimes freeze for good. Checked on every tick:
/// the host never grants conflicting keys; no two players both believe they hold conflicting keys; a player that
/// believes it holds a key really holds it on the host; the holder's own writes are never rejected; a frozen player's
/// locks are free within one lease. At the end every lock must have been released (no leaks).
/// </summary>
public sealed class LockSoak
{
	private const string Version = "lock-soak-1";

	private static readonly string[] HotKeys =
	{
		LockKeys.Car(0),
		LockKeys.Part(0, 0, 1, -1),
		LockKeys.Part(0, 0, 2, -1),
		LockKeys.Part(0, 3, 7, 0),
		LockKeys.Body(0, 4),
		LockKeys.Part(1, 0, 1, -1),
		LockKeys.Lifter(0),
		LockKeys.Stand(1),
		LockKeys.StandPart(1, 2)
	};

	private readonly LockSoakOptions options;
	private readonly LockSoakResult result = new();
	private readonly Random random;
	private readonly Stopwatch wall = new();
	private readonly List<Bot> bots = new();
	private readonly List<Kill> kills = new();
	private NetHost host;
	private LockServer server;
	private long now;
	private int nextIncarnation;
	private bool quiescing;
	private bool stopping;

	public LockSoak(LockSoakOptions options)
	{
		this.options = options ?? new LockSoakOptions();
		random = new Random(this.options.Seed);
	}

	public static LockSoakResult Run(LockSoakOptions options) => new LockSoak(options).Run();

	private long Clock() => now;

	/// <summary>Longest time a frozen player's locks may survive: one lease, the expiry sweep and a request still in flight.</summary>
	private long KillFreeLimitMs =>
		ResourceLockManager.LeaseMs + LockServer.ExpireEveryMs + 3 * (options.LatencyMs + options.JitterMs) + 4 * options.TickMs + 50;

	public LockSoakResult Run()
	{
		var previousSink = Log.Sink;
		var previousLevel = Log.MinLevel;
		Log.Sink = new FailOnError(result, previousSink);
		Log.MinLevel = LogLevel.Warn;
		wall.Start();
		try
		{
			StartHost();
			var endMs = (long)(options.Seconds * 1000);
			var progressEvery = Math.Max(1000, (long)(options.Seconds * 100));
			var nextProgress = progressEvery;
			while (now < endMs)
			{
				Step();
				if (now < nextProgress) continue;
				nextProgress += progressEvery;
				options.Progress?.Invoke($"{now / 1000}s: {server.Manager.Count} grants, {result.Grants} granted, {result.Denials} denied, errors {result.Errors.Count}");
			}

			// Everybody stops working: idle timers release every lock, then frozen players time out.
			quiescing = true;
			var settleMs = now + ClientLockTable.IdleMs + NetProtocol.TimeoutMs + 2000 + 10 * (options.LatencyMs + options.JitterMs);
			while (now < settleMs) Step();
			if (server.Manager.Count != 0)
				result.Fail($"Lock leak: {server.Manager.Count} grant(s) left after everybody stopped: " +
				            string.Join(", ", server.Manager.Snapshot().Select(p => $"{p.Key}=#{p.Value}")));
			foreach (var bot in bots.Where(b => b.Live))
			{
				var stale = HotKeys.Where(k => bot.Locks.Table.HolderOf(k) != 0).ToList();
				if (stale.Count > 0) result.Fail($"Bot {bot.Incarnation} still sees holders of {string.Join(", ", stale)} after everybody stopped.");
			}
			if (result.Grants == 0) result.Fail("No lock was ever granted.");
			if (options.Players > 1 && result.Denials + result.LocalBlocks == 0) result.Fail("No contention happened.");
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
				host?.Stop("Lock soak finished.");
				foreach (var bot in bots) bot.Client.Tick();
			}
			catch (Exception ex)
			{
				result.Fail("Shutdown failed: " + ex.Message);
			}
			wall.Stop();
			result.WallTime = wall.Elapsed;
			result.SimulatedSeconds = now / 1000.0;
			Log.Sink = previousSink;
			Log.MinLevel = previousLevel;
		}
		return result;
	}

	private void StartHost()
	{
		host = new NetHost(options.Players, Version, Clock);
		server = new LockServer(Clock,
			(slot, p) => host.Send(slot, p.FrameBuffer, p.FrameLength),
			p =>
			{
				foreach (var peer in host.Peers.ToArray()) peer.Send(p.FrameBuffer, p.FrameLength);
			});
		server.Manager.Changed += (_, holder) =>
		{
			if (holder != 0) result.Grants++;
		};
		host.PeerConnected += c => server.SendSnapshot(c.SlotId);
		host.PeerDisconnected += (c, _) => server.PlayerLeft(c.SlotId);
		host.FrameReceived += OnHostFrame;

		var (serverEnd, clientEnd) = SimLink.CreatePair(null, Clock, "loopback");
		host.Accept(serverEnd);
		AddBot(clientEnd, host.HostKey, true);
		for (var i = 1; i < options.Players; i++) AddRemoteBot();
	}

	private void AddRemoteBot()
	{
		var sim = new SimOptions
		{
			LatencyMs = options.LatencyMs,
			JitterMs = options.JitterMs,
			LossRate = options.LossRate,
			Seed = random.Next(1, int.MaxValue / 8000)
		};
		var (serverEnd, clientEnd) = SimLink.CreatePair(sim, Clock, "bot");
		serverEnd.HeartbeatRequired = clientEnd.HeartbeatRequired = true;
		host.Accept(serverEnd);
		AddBot(clientEnd, null, false);
	}

	private void AddBot(SimLink link, byte[] hostKey, bool isHost)
	{
		var bot = new Bot(this, ++nextIncarnation, isHost, link);
		bots.Add(bot);
		result.Joins++;
		bot.Client.Connect(link, hostKey);
	}

	private void OnHostFrame(NetConnection connection, NetFrame frame)
	{
		var slot = connection.SlotId;
		using var packet = new Packet(frame);
		switch ((PacketTypes)frame.Type)
		{
			case PacketTypes.lockRequest:
				server.HandleRequest(slot, packet);
				break;
			case PacketTypes.lockRenew:
				server.HandleRenew(slot, packet);
				break;
			case PacketTypes.lockRelease:
				server.HandleRelease(slot, packet);
				break;
			case PacketTypes.sceneChange:
				server.PlayerChangedScene(slot);
				break;
			case PacketTypes.partScript:
				var key = packet.Read<string>();
				var token = packet.ReadInt();
				var held = packet.Read<bool>();
				var incarnation = packet.ReadInt();
				if (server.AcceptWrite(slot, key, token))
					result.WritesAccepted++;
				else
				{
					result.WritesRejected++;
					if (held) result.Fail($"{now} ms: bot {incarnation} (#{slot}) held {key} but its write (token {token}) was rejected.");
				}
				break;
			default:
				result.Fail($"Host: unexpected packet {frame.Type} from #{slot}.");
				break;
		}
	}

	private void Step()
	{
		now += options.TickMs;
		host.Tick();
		server.Tick();
		foreach (var bot in bots.ToArray())
		{
			if (bot.Frozen) continue;
			bot.Client.Tick();
			if (!quiescing) bot.Act(now);
			bot.Locks.Pump();
		}
		bots.RemoveAll(b => b.Gone && !b.Frozen);

		CheckInvariants();
		CheckKills();
		if (!quiescing && options.KillsPerMinute > 0) MaybeKill();
		// A frozen player keeps its slot until the host times it out; replace bots once a slot is free again.
		while (!quiescing && bots.Count(b => !b.Frozen) < options.Players &&
		       host.PeerCount + bots.Count(b => !b.Connected && !b.Gone && !b.Frozen) < options.Players)
			AddRemoteBot();
		bots.RemoveAll(b => b.Frozen && now - b.FrozenAt > NetProtocol.TimeoutMs + 5000);
	}

	private void CheckInvariants()
	{
		var grants = server.Manager.Snapshot();
		if (grants.Count > result.MaxConcurrentGrants) result.MaxConcurrentGrants = grants.Count;
		for (var i = 0; i < grants.Count; i++)
		for (var j = i + 1; j < grants.Count; j++)
			if (grants[i].Value != grants[j].Value && LockKeys.Conflicts(grants[i].Key, grants[j].Key))
				result.Fail($"{now} ms: the host granted {grants[i].Key} to #{grants[i].Value} and {grants[j].Key} to #{grants[j].Value}.");

		var held = new List<(Bot bot, string key)>();
		foreach (var bot in bots)
		{
			if (!bot.Live) continue;
			foreach (var key in HotKeys)
			{
				if (bot.Locks.Table.StateOf(key) != ClientLockTable.State.Held) continue;
				held.Add((bot, key));
				var holder = server.Manager.HolderOf(key);
				if (holder != bot.Slot)
					result.Fail($"{now} ms: bot {bot.Incarnation} (#{bot.Slot}) believes it holds {key}, the host says #{holder}.");
			}
		}
		for (var i = 0; i < held.Count; i++)
		for (var j = i + 1; j < held.Count; j++)
			if (held[i].bot != held[j].bot && LockKeys.Conflicts(held[i].key, held[j].key))
				result.Fail($"{now} ms: bots {held[i].bot.Incarnation} and {held[j].bot.Incarnation} both hold {held[i].key} / {held[j].key}.");
	}

	private void MaybeKill()
	{
		var chance = options.KillsPerMinute * options.TickMs / 60000.0;
		foreach (var bot in bots.ToArray())
		{
			if (bot.IsHost || !bot.Live || random.NextDouble() >= chance) continue;
			bot.Freeze(now);
			kills.Add(new Kill(bot.Slot, bot.Incarnation, now));
			result.Kills++;
		}
	}

	private void CheckKills()
	{
		for (var i = kills.Count - 1; i >= 0; i--)
		{
			var kill = kills[i];
			var owned = server.Manager.Snapshot().Where(p => p.Value == kill.Slot).Select(p => p.Key).ToList();
			if (owned.Count == 0)
			{
				result.MaxKillFreeMs = Math.Max(result.MaxKillFreeMs, now - kill.At);
				kills.RemoveAt(i);
				continue;
			}
			if (now - kill.At <= KillFreeLimitMs) continue;
			result.Fail($"{now} ms: frozen bot {kill.Incarnation} (#{kill.Slot}) still holds {string.Join(", ", owned)} {now - kill.At} ms after it froze.");
			kills.RemoveAt(i);
		}
	}

	private readonly struct Kill
	{
		public readonly int Slot;
		public readonly int Incarnation;
		public readonly long At;

		public Kill(int slot, int incarnation, long at)
		{
			Slot = slot;
			Incarnation = incarnation;
			At = at;
		}
	}

	private sealed class Bot
	{
		private readonly LockSoak soak;
		private readonly Random random;
		private readonly SimLink link;
		private string workKey;
		private long workEnd, nextWrite;
		private bool racing;

		public Bot(LockSoak soak, int incarnation, bool isHost, SimLink link)
		{
			this.soak = soak;
			this.link = link;
			Incarnation = incarnation;
			IsHost = isHost;
			random = new Random(soak.options.Seed * 31 + incarnation);
			Client = new NetClient(Version, soak.Clock);
			Locks = new LockClient(0, soak.Clock, p =>
			{
				if (p.Id == (int)PacketTypes.lockRequest) soak.result.Requests++;
				Client.Send(p.FrameBuffer, p.FrameLength);
			});
			Locks.Denied += _ => soak.result.Denials++;
			Client.Connected += () =>
			{
				Locks.Table.Self = Client.SlotId;
				Slot = Client.SlotId;
				Connected = true;
			};
			Client.FrameReceived += OnFrame;
			Client.Disconnected += reason =>
			{
				Gone = true;
				if (!Frozen && !soak.stopping) soak.result.Fail($"Bot {Incarnation} (#{Slot}) was disconnected: {reason}");
			};
		}

		public readonly NetClient Client;
		public readonly LockClient Locks;
		public readonly int Incarnation;
		public readonly bool IsHost;
		public int Slot;
		public bool Connected;
		public bool Gone;
		public bool Frozen;
		public long FrozenAt;

		public bool Live => Connected && !Gone && !Frozen;

		public void Freeze(long time)
		{
			Frozen = true;
			FrozenAt = time;
			link.Frozen = true;
		}

		public void Act(long time)
		{
			if (!Live) return;
			var options = soak.options;

			if (workKey != null)
			{
				if (time >= workEnd)
				{
					// Walks away: the idle timer releases the lock, like in the game.
					workKey = null;
					return;
				}
				if (time >= nextWrite)
				{
					nextWrite = time + 150 + random.Next(300);
					Write(workKey);
				}
				return;
			}

			if (options.SceneChangesPerMinute > 0 && random.NextDouble() < options.SceneChangesPerMinute * options.TickMs / 60000.0)
			{
				// Same order as the game: forget own locks, then tell the host (which frees them).
				Locks.Table.DropOwn();
				using var packet = new Packet((int)PacketTypes.sceneChange);
				Client.Send(packet.FrameBuffer, packet.FrameLength);
				soak.result.SceneChanges++;
				return;
			}

			if (random.NextDouble() >= options.ActionsPerSecond * options.TickMs / 1000.0) return;
			var key = HotKeys[random.Next(HotKeys.Length)];
			racing = random.NextDouble() < options.RaceRate;
			if (!racing && Locks.Table.BlockedBy(key) != 0)
			{
				soak.result.LocalBlocks++;
				return;
			}
			Locks.Begin(key);
			workKey = key;
			workEnd = time + 300 + random.Next(4000);
			// The first write follows the action right away (before any reply), like a bolt turned on click.
			nextWrite = time;
		}

		private void Write(string key)
		{
			var held = Locks.Table.StateOf(key) == ClientLockTable.State.Held;
			var token = Locks.Stamp(key);
			if (racing && !held) soak.result.RaceWrites++;
			using var packet = new Packet((int)PacketTypes.partScript);
			packet.Write(key);
			packet.Write(token);
			packet.Write(held);
			packet.Write(Incarnation);
			Client.Send(packet.FrameBuffer, packet.FrameLength);
		}

		private void OnFrame(NetFrame frame)
		{
			try
			{
				using var packet = new Packet(frame);
				switch ((PacketTypes)frame.Type)
				{
					case PacketTypes.lockReply:
						Locks.HandleReply(packet);
						break;
					case PacketTypes.lockState:
						Locks.HandleState(packet);
						break;
					case PacketTypes.lockSnapshot:
						Locks.HandleSnapshot(packet);
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
	}

	private sealed class FailOnError : ILogSink
	{
		private readonly LockSoakResult result;
		private readonly ILogSink inner;

		public FailOnError(LockSoakResult result, ILogSink inner)
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
			inner?.Write(level, message);
		}
	}
}
