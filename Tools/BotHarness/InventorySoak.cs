using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using CMS21Together.Shared;
using CMS21Together.Shared.Net;
using CMS21Together.Shared.World;

namespace CMS21Together.BotHarness;

[Serializable]
public sealed class SoakItem
{
	public long UID;
	public string ID;
}

public sealed class InventorySoakOptions
{
	/// <summary>Players including the host's own (loopback) player.</summary>
	public int Players = 8;

	public double Seconds = 120;
	public int LatencyMs;
	public int JitterMs;
	public double LossRate;
	public int TickMs = 10;

	/// <summary>Inventory changes per bot per second (real play: a few per minute).</summary>
	public double ActionsPerSecond = 4;

	/// <summary>Leave-and-rejoin events per remote bot per minute.</summary>
	public double RejoinsPerMinute = 0.5;

	/// <summary>Share of changes sent a second time, like a buggy retry.</summary>
	public double DuplicateRate = 0.03;

	public double CheckEverySeconds = 10;
	public double QuietEverySeconds = 20;
	public int Seed = 1;
	public Action<string> Progress;
}

public sealed class InventorySoakResult
{
	public readonly List<string> Errors = new();
	public long Changes, Adds, Removes, ReAdds, Rejoins, Checks, QuietChecks, Snapshots, Desyncs, LogErrors, DuplicatesSent;
	public int FinalCount;
	public double SimulatedSeconds;
	public TimeSpan WallTime;
	public bool Passed => Errors.Count == 0;

	internal void Fail(string message)
	{
		if (Errors.Count < 50) Errors.Add(message);
	}

	public override string ToString() =>
		$"{(Passed ? "PASS" : "FAIL")}: {SimulatedSeconds:0}s simulated in {WallTime.TotalSeconds:0.0}s, {Changes} changes " +
		$"({Adds} adds, {Removes} removes, {ReAdds} re-adds of a removed UID), {DuplicatesSent} sent twice, {Rejoins} rejoins, {Checks} checks, " +
		$"{Desyncs} desyncs, {Snapshots} snapshots, {QuietChecks} quiet checks, {FinalCount} items at the end, log errors {LogErrors}" +
		(Passed ? "" : Environment.NewLine + string.Join(Environment.NewLine, Errors.Select(e => "  - " + e)));
}

/// <summary>
/// Shared inventory soak (Phase 5): the real <see cref="InventoryHost{T}"/> and <see cref="InventoryClient{T}"/> over
/// the real net stack on simulated links. Bots add, remove and re-add items (also the same UIDs at the same time),
/// leave and rejoin, and keep a "game" inventory that is reconciled to the mirror's desired set, like the mod does.
/// In each quiet window and at the end every bot's game inventory equals the host's set, nothing is pending, and no
/// periodic check ever found a difference.
/// </summary>
public sealed class InventorySoak
{
	private const string Version = "inventory-soak-1";

	private readonly InventorySoakOptions options;
	private readonly InventorySoakResult result = new();
	private readonly Random random;
	private readonly List<Bot> bots = new();
	private readonly HashSet<(NetConnection connection, int seq)> applied = new();
	private readonly Stopwatch wall = new();
	private NetHost host;
	private InventoryHost<SoakItem> server;
	private long now;
	private long nextUid = 1;
	private bool quiet;

	public InventorySoak(InventorySoakOptions options)
	{
		this.options = options ?? new InventorySoakOptions();
		random = new Random(this.options.Seed);
	}

	public static InventorySoakResult Run(InventorySoakOptions options) => new InventorySoak(options).Run();

	private long Clock() => now;
	private long SettleMs => 1500 + 6 * (options.LatencyMs + options.JitterMs);

	public InventorySoakResult Run()
	{
		var previousSink = Log.Sink;
		var previousLevel = Log.MinLevel;
		Log.Sink = new CountingSink(result, previousSink);
		Log.MinLevel = LogLevel.Warn;
		wall.Start();
		try
		{
			host = new NetHost(options.Players, Version, Clock);
			server = new InventoryHost<SoakItem>(InventoryKind.Items, i => i.UID, (slot, p) => host.Send(slot, p.FrameBuffer, p.FrameLength),
				() => bots.Where(b => b.Validated).Select(b => b.Slot).ToList());
			host.FrameReceived += OnHostFrame;
			host.PeerDisconnected += (c, _) => server.PlayerLeft(c.SlotId);
			for (var i = 0; i < options.Players; i++) bots.Add(new Bot(this, i == 0));

			var end = (long)(options.Seconds * 1000);
			var nextQuiet = (long)(options.QuietEverySeconds * 1000);
			while (now < end)
			{
				Step(!quiet);
				if (now >= nextQuiet)
				{
					Quiet();
					nextQuiet = now + (long)(options.QuietEverySeconds * 1000);
				}
			}
			Quiet();
			result.FinalCount = server.Authority.Count;
			result.Snapshots = server.SnapshotsSent;
			result.Desyncs = server.Desyncs;
			if (server.Desyncs > 0) result.Fail($"{server.Desyncs} periodic checks found a player's set differing from the host's.");
		}
		finally
		{
			Log.Sink = previousSink;
			Log.MinLevel = previousLevel;
			wall.Stop();
			result.WallTime = wall.Elapsed;
			result.SimulatedSeconds = now / 1000.0;
		}
		return result;
	}

	private void Step(bool act)
	{
		now += options.TickMs;
		host.Tick();
		foreach (var bot in bots.ToList()) bot.Tick(act);
	}

	private void Quiet()
	{
		quiet = true;
		var until = now + SettleMs;
		while (now < until) Step(false);
		quiet = false;
		result.QuietChecks++;

		var expected = new HashSet<long>(server.Authority.Items.Select(i => i.UID));
		foreach (var bot in bots.Where(b => b.Validated))
		{
			var game = new HashSet<long>(bot.Game.Keys);
			if (!game.SetEquals(expected))
				result.Fail($"{now} ms: bot #{bot.Slot} holds {game.Count} items, the host {expected.Count} " +
				            $"(missing {string.Join(",", expected.Except(game).Take(5))}, extra {string.Join(",", game.Except(expected).Take(5))}).");
			if (bot.Inventory.Mirror.PendingCount != 0) result.Fail($"{now} ms: bot #{bot.Slot} still has {bot.Inventory.Mirror.PendingCount} pending changes.");
			if (bot.Inventory.Mirror.ConfirmedHash != server.Authority.Hash) result.Fail($"{now} ms: bot #{bot.Slot}'s confirmed set differs from the host's.");
		}
	}

	private void OnHostFrame(NetConnection connection, NetFrame frame)
	{
		var slot = connection.SlotId;
		using var packet = new Packet(frame);
		switch ((PacketTypes)frame.Type)
		{
			case PacketTypes.invPush:
				var push = InventoryWire.ReadPush<SoakItem>(packet);
				var outcome = server.HandlePush(slot, push.seq, push.op, push.item);
				if ((outcome == InventoryResult.Added || outcome == InventoryResult.Removed) && !applied.Add((connection, push.seq)))
					result.Fail($"{now} ms: change {push.seq} of #{slot} was applied twice.");
				break;
			case PacketTypes.invRequest:
				InventoryWire.ReadRequest(packet);
				var bot = bots.FirstOrDefault(b => b.Slot == slot);
				if (bot != null && !bot.Validated)
				{
					// The request doubles as the end of validation: from now on this player gets every change.
					bot.Validated = true;
					server.PlayerJoined(slot);
				}
				server.SendSnapshot(slot);
				break;
			case PacketTypes.invCheck:
				var check = InventoryWire.ReadCheck(packet);
				result.Checks++;
				server.HandleCheck(slot, check.version, check.hash);
				break;
		}
	}

	private sealed class Bot
	{
		private readonly InventorySoak soak;
		private readonly bool isHost;
		private readonly List<long> removed = new();
		private NetClient client;
		private SimLink link;
		private long nextCheck;

		public Bot(InventorySoak soak, bool isHost)
		{
			this.soak = soak;
			this.isHost = isHost;
			Connect();
		}

		public int Slot => client.SlotId;
		public bool Validated;
		public InventoryClient<SoakItem> Inventory { get; private set; }
		public Dictionary<long, SoakItem> Game { get; } = new();

		private void Connect()
		{
			var options = isHost
				? SimOptions.Perfect
				: new SimOptions { LatencyMs = soak.options.LatencyMs, JitterMs = soak.options.JitterMs, LossRate = soak.options.LossRate, Seed = soak.random.Next() };
			var (clientEnd, hostEnd) = SimLink.CreatePair(options, soak.Clock, isHost ? "loopback" : "bot");
			link = clientEnd;
			client = new NetClient(Version, soak.Clock);
			var recent = new List<byte[]>();
			var inventory = new InventoryClient<SoakItem>(InventoryKind.Items, i => i.UID, p =>
			{
				client.Send(p.FrameBuffer, p.FrameLength);
				if (p.Id != (int)PacketTypes.invPush) return;
				var copy = new byte[p.FrameLength];
				Buffer.BlockCopy(p.FrameBuffer, 0, copy, 0, copy.Length);
				recent.Add(copy);
				if (recent.Count > 8) recent.RemoveAt(0);
				if (soak.random.NextDouble() >= soak.options.DuplicateRate) return;
				// A late replay of one of the last changes (another player may have changed that item since).
				var old = recent[soak.random.Next(recent.Count)];
				client.Send(old, old.Length);
				soak.result.DuplicatesSent++;
			}, soak.Clock);
			Inventory = inventory;
			inventory.Changed += Reconcile;
			client.Connected += () => inventory.RequestSnapshot(true);
			client.FrameReceived += frame =>
			{
				using var packet = new Packet(frame);
				switch ((PacketTypes)frame.Type)
				{
					case PacketTypes.invOp:
						var op = InventoryWire.ReadOp<SoakItem>(packet);
						inventory.HandleOp(op.version, op.ack, op.op, op.item);
						break;
					case PacketTypes.invSnapshot:
						var snapshot = InventoryWire.ReadSnapshot<SoakItem>(packet);
						inventory.HandleSnapshot(snapshot.version, snapshot.ack, snapshot.items);
						break;
				}
			};
			Validated = false;
			Game.Clear();
			client.Connect(clientEnd, isHost ? soak.host.HostKey : null);
			soak.host.Accept(hostEnd);
			nextCheck = soak.now + (long)(soak.options.CheckEverySeconds * 1000 * (0.5 + soak.random.NextDouble()));
		}

		/// <summary>The mod's reconcile: the game inventory becomes the desired set (no echo).</summary>
		private void Reconcile()
		{
			var (add, remove) = InventoryDiff.Compute(Game, Inventory.Mirror.Desired());
			foreach (var uid in remove) Game.Remove(uid);
			foreach (var item in add) Game[item.UID] = item;
		}

		public void Tick(bool act)
		{
			client.Tick();
			if (client.State != NetClientState.Connected || !Inventory.Mirror.Synced) return;

			if (soak.now >= nextCheck)
			{
				nextCheck = soak.now + (long)(soak.options.CheckEverySeconds * 1000);
				Inventory.SendCheck();
			}
			if (!act) return;

			var rate = soak.options.ActionsPerSecond * soak.options.TickMs / 1000.0;
			if (soak.random.NextDouble() < rate) Act();

			if (!isHost && soak.random.NextDouble() < soak.options.RejoinsPerMinute * soak.options.TickMs / 60000.0)
			{
				client.Disconnect("Rejoining.");
				soak.result.Rejoins++;
				Connect();
			}
		}

		private void Act()
		{
			var roll = soak.random.NextDouble();
			SoakItem item;
			InventoryOp op;
			if (roll < 0.15 && removed.Count > 0)
			{
				// A23: the same UID comes back (another bot may re-add it at the same time).
				var uid = removed[soak.random.Next(removed.Count)];
				removed.Remove(uid);
				if (Game.ContainsKey(uid)) return;
				item = new SoakItem { UID = uid, ID = "readd" };
				op = InventoryOp.Add;
				soak.result.ReAdds++;
			}
			else if (roll < 0.55 || Game.Count == 0)
			{
				item = new SoakItem { UID = soak.nextUid++, ID = "part" };
				op = InventoryOp.Add;
				soak.result.Adds++;
			}
			else
			{
				var uids = Game.Keys.ToList();
				var uid = uids[soak.random.Next(uids.Count)];
				item = Game[uid];
				op = InventoryOp.Remove;
				soak.result.Removes++;
				foreach (var other in soak.bots) other.removed.Add(uid);
			}

			if (op == InventoryOp.Add) Game[item.UID] = item;
			else Game.Remove(item.UID);
			Inventory.Local(op, item);
			soak.result.Changes++;
		}
	}

	private sealed class CountingSink : ILogSink
	{
		private readonly InventorySoakResult result;
		private readonly ILogSink next;

		public CountingSink(InventorySoakResult result, ILogSink next)
		{
			this.result = result;
			this.next = next;
		}

		public void Write(LogLevel level, string message)
		{
			if (level >= LogLevel.Error)
			{
				result.LogErrors++;
				result.Fail("Logged error: " + message);
			}
			next?.Write(level, message);
		}
	}
}