using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using CMS21Together.Shared;
using CMS21Together.Shared.Economy;
using CMS21Together.Shared.Net;

namespace CMS21Together.BotHarness;

public sealed class EconomySoakOptions
{
	/// <summary>Players including the host's own (loopback) player.</summary>
	public int Players = 8;

	public double Seconds = 120;
	public int LatencyMs;
	public int JitterMs;
	public double LossRate;
	public int TickMs = 10;

	/// <summary>Average wallet changes per bot per second (real play: a few per minute).</summary>
	public double ActionsPerSecond = 10;

	/// <summary>Share of changes that earn (job payouts, selling); the rest spend.</summary>
	public double CreditShare = 0.47;

	/// <summary>Share of transactions sent a second time by the application, like a buggy retry.</summary>
	public double DuplicateRate = 0.03;

	/// <summary>Games that freeze for good (crash, power loss) per remote bot per minute.</summary>
	public double KillsPerMinute = 0.3;

	public long StartMoney = 3000;
	public long StartScrap = 40;

	/// <summary>Every so often everybody stops for a moment and all wallets must agree with the host's.</summary>
	public double QuietEverySeconds = 20;

	public int Seed = 1;
	public Action<string> Progress;
}

public sealed class EconomySoakResult
{
	public readonly List<string> Errors = new();
	public long Recorded, Applied, Refused, DuplicatesSent, DuplicatesIgnored, LostInFlight;
	public long Kills, Joins, QuietChecks, BalancesChecked;
	public int MaxPending;
	public long FinalMoney, FinalScrap, MinMoney = long.MaxValue;
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
		$"transactions {Recorded} sent, {Applied} applied, {Refused} refused, {LostInFlight} lost with a frozen game, " +
		$"duplicates {DuplicatesSent} sent / {DuplicatesIgnored} ignored, balances checked {BalancesChecked}, " +
		$"quiet checks {QuietChecks}, kills {Kills}, joins {Joins}, max pending {MaxPending}, " +
		$"final {FinalMoney} money / {FinalScrap} scrap (lowest {MinMoney}), log errors {LogErrors}" +
		(Passed ? "" : Environment.NewLine + string.Join(Environment.NewLine, Errors.Select(e => "  - " + e)));
}

/// <summary>
/// Shared wallet soak: the real <see cref="EconomyServer"/> and <see cref="EconomyClient"/> over the real net stack on
/// simulated links. Bots play a game that spends only what it shows and shows the wallet like the mod does (local change
/// at once, the shared balance reapplied when it arrives). Checked continuously: every transaction is applied at most
/// once, in order, and only through the ledger (the harness keeps its own sum); the ledger never goes negative; every
/// balance a bot receives is the host's plus exactly that bot's unprocessed transactions. In each quiet window and at
/// the end every live wallet, game value and transaction counter equals the host's, with nothing pending.
/// </summary>
public sealed class EconomySoak
{
	private const string Version = "economy-soak-1";
	private const long InitializeAtMs = 1000;
	private const long PumpEveryMs = 1000;

	private readonly EconomySoakOptions options;
	private readonly EconomySoakResult result = new();
	private readonly Random random;
	private readonly Stopwatch wall = new();
	private readonly List<Bot> bots = new();
	private readonly Dictionary<int, Connection> connections = new();
	private readonly HashSet<(int connection, int number)> seen = new();
	private NetHost host;
	private EconomyServer server;
	private long now;
	private long expectedMoney, expectedScrap;
	private int nextIncarnation, nextConnection;
	private bool quiet, quiescing, stopping;
	private long quietUntil, nextQuiet;

	public EconomySoak(EconomySoakOptions options)
	{
		this.options = options ?? new EconomySoakOptions();
		random = new Random(this.options.Seed);
	}

	public static EconomySoakResult Run(EconomySoakOptions options) => new EconomySoak(options).Run();

	private long Clock() => now;

	private long SettleMs => 1500 + 6 * (options.LatencyMs + options.JitterMs) + 2 * PumpEveryMs;

	public EconomySoakResult Run()
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
			nextQuiet = options.QuietEverySeconds > 0 ? (long)(options.QuietEverySeconds * 1000) : long.MaxValue;
			var progressEvery = Math.Max(1000, (long)(options.Seconds * 100));
			var nextProgress = progressEvery;
			while (now < endMs)
			{
				Step();
				if (now < nextProgress) continue;
				nextProgress += progressEvery;
				options.Progress?.Invoke($"{now / 1000}s: wallet {server.Ledger.Money}/{server.Ledger.Scrap}, {result.Applied} applied, {result.Refused} refused, errors {result.Errors.Count}");
			}

			quiescing = true;
			var settleUntil = now + SettleMs + NetProtocol.TimeoutMs;
			while (now < settleUntil) Step();
			CheckConverged("at the end");
			result.FinalMoney = server.Ledger.Money;
			result.FinalScrap = server.Ledger.Scrap;
			if (result.Applied == 0) result.Fail("No transaction was ever applied.");
			if (expectedMoney != server.Ledger.Money || expectedScrap != server.Ledger.Scrap)
				result.Fail($"The ledger holds {server.Ledger.Money}/{server.Ledger.Scrap}, the transactions add up to {expectedMoney}/{expectedScrap}.");
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
				host?.Stop("Economy soak finished.");
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
		server = new EconomyServer(SendBalance, () => connections.Keys.ToList());
		host.PeerConnected += c =>
		{
			connections[c.SlotId] = new Connection(++nextConnection);
			server.PlayerJoined(c.SlotId);
		};
		host.PeerDisconnected += (c, _) =>
		{
			connections.Remove(c.SlotId);
			server.PlayerLeft(c.SlotId);
		};
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

	private void SendBalance(int slot, Packet packet)
	{
		// What the bot in that slot must end up showing once this balance arrives (checked by the bot on receipt).
		if (connections.TryGetValue(slot, out var connection))
			connection.Expected.Enqueue((server.Ledger.Money, server.Ledger.Scrap, connection.LastNumber));
		host.Send(slot, packet.FrameBuffer, packet.FrameLength);
	}

	private void OnHostFrame(NetConnection peer, NetFrame frame)
	{
		var slot = peer.SlotId;
		if (frame.Type != (int)PacketTypes.econTxn)
		{
			result.Fail($"Host: unexpected packet {frame.Type} from #{slot}.");
			return;
		}
		if (!connections.TryGetValue(slot, out var connection))
		{
			result.Fail($"Host: transaction from #{slot}, which never joined.");
			return;
		}

		using var peek = new Packet(frame);
		var (number, money, scrap) = EconomyWire.ReadTxn(peek);
		var duplicate = !seen.Add((connection.Id, number));
		if (!duplicate && number != connection.LastNumber + 1)
			result.Fail($"{now} ms: #{slot} sent transaction {number} after {connection.LastNumber} (gap or reorder).");
		var before = (server.Ledger.Money, server.Ledger.Scrap, server.Ledger.Seq);
		var ignoredBefore = server.Duplicates;
		if (!duplicate) connection.LastNumber = number;

		using (var packet = new Packet(frame)) server.HandleTxn(slot, packet);

		var after = (server.Ledger.Money, server.Ledger.Scrap, server.Ledger.Seq);
		if (duplicate)
		{
			if (after != before) result.Fail($"{now} ms: replayed transaction {number} from #{slot} changed the wallet {before} -> {after}.");
			if (server.Duplicates != ignoredBefore + 1) result.Fail($"{now} ms: replayed transaction {number} from #{slot} was not counted as ignored.");
			result.DuplicatesIgnored++;
			return;
		}
		if (after.Seq == before.Seq + 1)
		{
			result.Applied++;
			expectedMoney += money;
			expectedScrap += scrap;
		}
		else if (after.Seq == before.Seq)
			result.Refused++;
		else
			result.Fail($"{now} ms: one transaction moved the counter from {before.Seq} to {after.Seq}.");
		if (server.Ledger.Money != expectedMoney || server.Ledger.Scrap != expectedScrap)
			result.Fail($"{now} ms: the ledger holds {server.Ledger.Money}/{server.Ledger.Scrap}, the applied transactions add up to {expectedMoney}/{expectedScrap}.");
		if (server.Ledger.Money < 0 || server.Ledger.Scrap < 0)
			result.Fail($"{now} ms: the wallet went negative ({server.Ledger.Money}/{server.Ledger.Scrap}).");
		result.MinMoney = Math.Min(result.MinMoney, server.Ledger.Money);
	}

	private void Step()
	{
		now += options.TickMs;
		host.Tick();
		if (!server.Ledger.Initialized && now >= InitializeAtMs && bots.Any(b => b.IsHost && b.Connected))
		{
			server.Initialize(options.StartMoney, options.StartScrap);
			expectedMoney = server.Ledger.Money;
			expectedScrap = server.Ledger.Scrap;
		}

		if (!quiescing && !quiet && now >= nextQuiet)
		{
			quiet = true;
			quietUntil = now + SettleMs;
		}
		var acting = !quiescing && !quiet;
		foreach (var bot in bots.ToArray())
		{
			if (bot.Frozen) continue;
			bot.Client.Tick();
			if (acting) bot.Act();
			bot.Pump();
			result.MaxPending = Math.Max(result.MaxPending, bot.Economy.PendingCount);
		}
		bots.RemoveAll(b => b.Gone && !b.Frozen);

		if (quiet && now >= quietUntil)
		{
			CheckConverged($"in the quiet window at {now / 1000}s");
			result.QuietChecks++;
			quiet = false;
			nextQuiet = now + (long)(options.QuietEverySeconds * 1000);
		}

		if (acting && options.KillsPerMinute > 0) MaybeKill();
		while (!quiescing && bots.Count(b => !b.Frozen) < options.Players &&
		       host.PeerCount + bots.Count(b => !b.Connected && !b.Gone && !b.Frozen) < options.Players)
			AddRemoteBot();
		bots.RemoveAll(b => b.Frozen && now - b.FrozenAt > NetProtocol.TimeoutMs + 5000);
	}

	private void CheckConverged(string when)
	{
		var ledger = server.Ledger;
		foreach (var bot in bots.Where(b => b.Live))
		{
			var economy = bot.Economy;
			if (!economy.Synced)
			{
				if (ledger.Initialized && now - bot.ConnectedAt > SettleMs) result.Fail($"Bot {bot.Incarnation} (#{bot.Slot}) never got the wallet ({when}).");
				continue;
			}
			if (economy.PendingCount != 0 || economy.Money != ledger.Money || economy.Scrap != ledger.Scrap || economy.Seq != ledger.Seq || bot.Game != (ledger.Money, ledger.Scrap))
				result.Fail($"Bot {bot.Incarnation} (#{bot.Slot}) disagrees {when}: shows {economy.Money}/{economy.Scrap} (game {bot.Game.money}/{bot.Game.scrap}, " +
				            $"counter {economy.Seq}, {economy.PendingCount} pending), host {ledger.Money}/{ledger.Scrap} (counter {ledger.Seq}).");
		}
	}

	private void MaybeKill()
	{
		var chance = options.KillsPerMinute * options.TickMs / 60000.0;
		foreach (var bot in bots.ToArray())
		{
			if (bot.IsHost || !bot.Live || random.NextDouble() >= chance) continue;
			bot.Freeze(now);
			result.Kills++;
			result.LostInFlight += bot.Economy.PendingCount;
		}
	}

	private sealed class Connection
	{
		public readonly int Id;
		public readonly Queue<(long money, long scrap, int processed)> Expected = new();
		public int LastNumber;

		public Connection(int id) => Id = id;
	}

	private sealed class Bot
	{
		private readonly EconomySoak soak;
		private readonly Random random;
		private readonly SimLink link;
		private readonly List<(int number, long money, long scrap)> sent = new();
		private Connection connection;
		private bool applied, dirty;
		private long nextPump;

		public Bot(EconomySoak soak, int incarnation, bool isHost, SimLink link)
		{
			this.soak = soak;
			this.link = link;
			Incarnation = incarnation;
			IsHost = isHost;
			random = new Random(soak.options.Seed * 31 + incarnation);
			Client = new NetClient(Version, soak.Clock);
			Economy = new EconomyClient(p => Client.Send(p.FrameBuffer, p.FrameLength));
			Economy.Changed += () => dirty = true;
			Client.Connected += () =>
			{
				Slot = Client.SlotId;
				Connected = true;
				ConnectedAt = soak.now;
				if (!soak.connections.TryGetValue(Slot, out connection))
					soak.result.Fail($"Bot {Incarnation} connected as #{Slot}, which the host does not know.");
			};
			Client.FrameReceived += OnFrame;
			Client.Disconnected += reason =>
			{
				Gone = true;
				if (!Frozen && !soak.stopping) soak.result.Fail($"Bot {Incarnation} (#{Slot}) was disconnected: {reason}");
			};
		}

		public readonly NetClient Client;
		public readonly EconomyClient Economy;
		public readonly int Incarnation;
		public readonly bool IsHost;
		public int Slot;
		public bool Connected;
		public long ConnectedAt;
		public bool Gone;
		public bool Frozen;
		public long FrozenAt;

		/// <summary>The money and scrap the game shows.</summary>
		public (long money, long scrap) Game;

		public bool Live => Connected && !Gone && !Frozen;

		public void Freeze(long time)
		{
			Frozen = true;
			FrozenAt = time;
			link.Frozen = true;
		}

		public void Act()
		{
			if (!Live) return;
			var options = soak.options;
			if (random.NextDouble() >= options.ActionsPerSecond * options.TickMs / 1000.0) return;

			long money = 0, scrap = 0;
			var earn = random.NextDouble() < options.CreditShare;
			if (random.NextDouble() < 0.8)
			{
				// The game only lets a player buy what the shown balance covers.
				if (earn) money = 1 + random.Next(1200);
				else if (Game.money > 0) money = -(1 + (long)(random.NextDouble() * Math.Min(Game.money, 1500)));
			}
			else
			{
				if (earn) scrap = 1 + random.Next(12);
				else if (Game.scrap > 0) scrap = -(1 + random.Next((int)Math.Min(Game.scrap, 15)));
			}
			if (money == 0 && scrap == 0) return;

			Game = (Game.money + money, Game.scrap + scrap);
			// Like Wallet.OnLocalChange: before the shared balance is shown, a change is overwritten instead of sent.
			if (!applied || !Economy.Record(money, scrap))
			{
				dirty = true;
				return;
			}
			soak.result.Recorded++;
			sent.Add((Economy.LastNumber, money, scrap));
			if (random.NextDouble() >= options.DuplicateRate) return;
			var (number, m, s) = sent[sent.Count - 1];
			using var again = new Packet((int)PacketTypes.econTxn);
			EconomyWire.WriteTxn(again, number, m, s);
			Client.Send(again.FrameBuffer, again.FrameLength);
			soak.result.DuplicatesSent++;
		}

		/// <summary>Like Wallet.Pump: shows the shared balance when it changed, and at least once a second.</summary>
		public void Pump()
		{
			if (!Live || !Economy.Synced) return;
			if (!dirty && soak.now < nextPump) return;
			nextPump = soak.now + PumpEveryMs;
			dirty = false;
			Game = (Economy.Money, Economy.Scrap);
			applied = true;
		}

		private void OnFrame(NetFrame frame)
		{
			try
			{
				if (frame.Type != (int)PacketTypes.econBalance)
				{
					soak.result.Fail($"Bot {Incarnation}: unexpected packet {frame.Type}.");
					return;
				}
				using (var packet = new Packet(frame)) Economy.HandleBalance(packet);
				Check();
			}
			catch (Exception ex)
			{
				soak.result.Fail($"Bot {Incarnation}: handling packet {frame.Type} threw {ex.GetType().Name}: {ex.Message}");
			}
		}

		/// <summary>The shown balance is the host's plus exactly this player's transactions the host had not processed.</summary>
		private void Check()
		{
			if (connection == null) return;
			if (connection.Expected.Count == 0)
			{
				soak.result.Fail($"Bot {Incarnation} (#{Slot}) got a balance the host never sent it.");
				return;
			}
			var (money, scrap, processed) = connection.Expected.Dequeue();
			sent.RemoveAll(t => t.number <= processed);
			var expectMoney = Clamp(money + sent.Sum(t => t.money));
			var expectScrap = Clamp(scrap + sent.Sum(t => t.scrap));
			soak.result.BalancesChecked++;
			if (Economy.HostMoney != money || Economy.HostScrap != scrap || Economy.Processed != processed || Economy.PendingCount != sent.Count ||
			    Economy.Money != expectMoney || Economy.Scrap != expectScrap)
				soak.result.Fail($"{soak.now} ms: bot {Incarnation} (#{Slot}) shows {Economy.Money}/{Economy.Scrap} with {Economy.PendingCount} pending " +
				                 $"(host {Economy.HostMoney}/{Economy.HostScrap}, processed {Economy.Processed}); expected {expectMoney}/{expectScrap} " +
				                 $"with {sent.Count} pending (host {money}/{scrap}, processed {processed}).");
		}

		private static long Clamp(long value) => value < 0 ? 0 : value > EconomyLedger.MaxBalance ? EconomyLedger.MaxBalance : value;
	}

	private sealed class FailOnError : ILogSink
	{
		private readonly EconomySoakResult result;
		private readonly ILogSink inner;

		public FailOnError(EconomySoakResult result, ILogSink inner)
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
