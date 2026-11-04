using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using CMS21Together.Shared;
using CMS21Together.Shared.Net;
using CMS21Together.Shared.World;

namespace CMS21Together.BotHarness;

public sealed class CarSoakOptions
{
	public int Players = 4;
	public double Seconds = 180;
	public int LatencyMs;
	public int JitterMs;
	public double LossRate;
	public int TickMs = 10;
	public int Cars = 3;
	public int PartsPerCar = 40;

	/// <summary>Part changes per bot per second (each bot works on its own parts, as the locks ensure in game).</summary>
	public double ChangesPerSecond = 2;

	public double DivergeEverySeconds = 25;
	public int Seed = 1;
	public Action<string> Trace;
}

public sealed class CarSoakResult
{
	public readonly List<string> Errors = new();
	public long Changes, Divergences, Converged, Pushes, Checks;
	public long SlowestMs;
	public double SimulatedSeconds;
	public TimeSpan WallTime;
	public bool Passed => Errors.Count == 0;

	internal void Fail(string message)
	{
		if (Errors.Count < 50) Errors.Add(message);
	}

	public override string ToString() =>
		$"{(Passed ? "PASS" : "FAIL")}: {SimulatedSeconds:0}s simulated in {WallTime.TotalSeconds:0.0}s, {Changes} part changes, " +
		$"{Divergences} cars diverged, {Converged} converged (slowest {SlowestMs} ms), {Pushes} pushes after {Checks} checks" +
		(Passed ? "" : Environment.NewLine + string.Join(Environment.NewLine, Errors.Select(e => "  - " + e)));
}

/// <summary>
/// Phase 6 exit test (per-car desync detector): bots change car parts over lossy, laggy links, relayed by the host
/// like the mod does; the host's own game is the reference. Every so often one player's car is silently diverged. It
/// must match the host's again within one detector cycle, and the host never pushes a car that was not diverged.
/// </summary>
public sealed class CarSoak
{
	private const string Version = "car-soak-1";

	private readonly CarSoakOptions options;
	private readonly CarSoakResult result = new();
	private readonly Random random;
	private readonly List<Bot> bots = new();
	private readonly Stopwatch wall = new();
	private readonly Dictionary<(int slot, int car), long> diverged = new();
	private NetHost host;
	private CarDesyncDetector detector;
	private long now;
	private long quietCarUntil;

	public CarSoak(CarSoakOptions options)
	{
		this.options = options ?? new CarSoakOptions();
		random = new Random(this.options.Seed);
	}

	public static CarSoakResult Run(CarSoakOptions options) => new CarSoak(options).Run();

	private long Clock() => now;

	/// <summary>One cycle: the report interval, the idle time before a car is reported, and the round trips.</summary>
	private long CycleMs => CarCheckClient.DefaultEveryMs + CarDesyncDetector.IdleMs + 4 * (options.LatencyMs + options.JitterMs) + 500;

	public CarSoakResult Run()
	{
		var previousSink = Log.Sink;
		var previousLevel = Log.MinLevel;
		Log.MinLevel = LogLevel.Warn;
		wall.Start();
		try
		{
			host = new NetHost(options.Players, Version, Clock);
			detector = new CarDesyncDetector(Clock);
			host.FrameReceived += OnHostFrame;
			for (var i = 0; i < options.Players; i++) bots.Add(new Bot(this, i));

			var end = (long)(options.Seconds * 1000);
			var nextDiverge = (long)(options.DivergeEverySeconds * 1000);
			while (now < end)
			{
				Step();
				if (now >= nextDiverge - 4000 && now < nextDiverge) quietCarUntil = nextDiverge + CycleMs;
				if (now >= nextDiverge && now + CycleMs < end)
				{
					Diverge();
					nextDiverge = now + (long)(options.DivergeEverySeconds * 1000);
				}
				TrackConvergence();
			}
			foreach (var pending in diverged) result.Fail($"Bot #{pending.Key.slot}'s car {pending.Key.car}, diverged at {pending.Value} ms, never converged.");
			result.Pushes = detector.Pushes;
			result.Checks = detector.Checks;
			if (result.Converged == 0) result.Fail("No divergence was ever repaired.");
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

	private Bot HostBot => bots[0];

	private void Step()
	{
		now += options.TickMs;
		host.Tick();
		foreach (var bot in bots) bot.Tick();
	}

	private void Diverge()
	{
		var players = bots.Skip(1).Where(b => b.Ready).ToList();
		if (players.Count == 0) return;
		var bot = players[random.Next(players.Count)];
		var key = random.Next(options.PartsPerCar);
		var part = bot.Cars[0][key];
		// Silently wrong: a missed packet, a game quirk. Nothing is sent.
		bot.Cars[0][key] = (!part.unmounted, part.condition);
		diverged[(bot.Slot, 0)] = now;
		result.Divergences++;
		options.Trace?.Invoke($"{now}: diverged #{bot.Slot} car 0 part {key}");
	}

	private void TrackConvergence()
	{
		foreach (var key in diverged.Keys.ToList())
		{
			var bot = bots.First(b => b.Slot == key.slot);
			if (bot.Hash(key.car) != HostBot.Hash(key.car))
			{
				if (now - diverged[key] > CycleMs)
				{
					result.Fail($"Bot #{key.slot}'s car {key.car} was still diverged {now - diverged[key]} ms later (one cycle is {CycleMs} ms).");
					diverged.Remove(key);
				}
				continue;
			}
			options.Trace?.Invoke($"{now}: #{key.slot} car {key.car} converged");
			result.SlowestMs = Math.Max(result.SlowestMs, now - diverged[key]);
			result.Converged++;
			diverged.Remove(key);
		}
	}

	private void OnHostFrame(NetConnection connection, NetFrame frame)
	{
		var slot = connection.SlotId;
		using var packet = new Packet(frame);
		switch ((PacketTypes)frame.Type)
		{
			case PacketTypes.partScript:
				var car = packet.ReadInt();
				var key = packet.ReadInt();
				var unmounted = packet.Read<bool>();
				var condition = packet.Read<float>();
				detector.Touched(car);
				// Relayed to everyone else, the host's own game included (that is the host's live state).
				foreach (var bot in bots.Where(b => b.Ready && b.Slot != slot)) SendPart(bot.Slot, car, key, unmounted, condition);
				break;
			case PacketTypes.carCheck:
				foreach (var (carId, hash) in CarCheckWire.Read(packet))
				{
					var hostHash = HostBot.Hash(carId);
					if (hash != hostHash)
						options.Trace?.Invoke($"{now}: #{slot} reports car {carId} different (host idle {detector.Idle(carId)}): {DiffOf(slot, carId)}");
					if (!detector.ShouldPush(slot, carId, hash, hostHash, false)) continue;
					if (!diverged.ContainsKey((slot, carId))) result.Fail($"{now} ms: the host pushed car {carId} to #{slot}, which was not diverged.");
					// The host's full car, read from its live game.
					foreach (var part in HostBot.Cars[carId]) SendPart(slot, carId, part.Key, part.Value.unmounted, part.Value.condition);
				}
				break;
		}
	}

	private string DiffOf(int slot, int car)
	{
		var bot = bots.First(b => b.Slot == slot);
		return string.Join(", ", bot.Cars[car].Where(p => !p.Value.Equals(HostBot.Cars[car][p.Key]))
			.Select(p => $"part {p.Key} {p.Value} vs host {HostBot.Cars[car][p.Key]}"));
	}

	private void SendPart(int slot, int car, int key, bool unmounted, float condition)
	{
		using var packet = new Packet((int)PacketTypes.partScript);
		packet.Write(car);
		packet.Write(key);
		packet.Write(unmounted);
		packet.Write(condition);
		host.Send(slot, packet.FrameBuffer, packet.FrameLength);
	}

	private sealed class Bot
	{
		private readonly CarSoak soak;
		private readonly int index;
		private readonly NetClient client;
		private readonly CarCheckClient checks;

		public Bot(CarSoak soak, int index)
		{
			this.soak = soak;
			this.index = index;
			var isHost = index == 0;
			var options = isHost
				? SimOptions.Perfect
				: new SimOptions { LatencyMs = soak.options.LatencyMs, JitterMs = soak.options.JitterMs, LossRate = soak.options.LossRate, Seed = soak.random.Next() };
			var (clientEnd, hostEnd) = SimLink.CreatePair(options, soak.Clock, isHost ? "loopback" : "bot");
			client = new NetClient(Version, soak.Clock);
			checks = new CarCheckClient(soak.Clock);
			for (var car = 0; car < soak.options.Cars; car++)
			{
				var parts = new Dictionary<int, (bool unmounted, float condition)>();
				for (var key = 0; key < soak.options.PartsPerCar; key++) parts[key] = (false, 1f);
				Cars[car] = parts;
			}
			client.FrameReceived += frame =>
			{
				using var packet = new Packet(frame);
				if ((PacketTypes)frame.Type != PacketTypes.partScript) return;
				var car = packet.ReadInt();
				var key = packet.ReadInt();
				Cars[car][key] = (packet.Read<bool>(), packet.Read<float>());
				checks.Touched(car);
			};
			client.Connect(clientEnd, isHost ? soak.host.HostKey : null);
			soak.host.Accept(hostEnd);
		}

		public int Slot => client.SlotId;
		public bool Ready => client.State == NetClientState.Connected;
		public Dictionary<int, Dictionary<int, (bool unmounted, float condition)>> Cars { get; } = new();

		public string Hash(int car) =>
			CarHash.Of(Cars[car].Select(p => new PartState(p.Key.ToString(), p.Value.unmounted, p.Value.condition, 1, null, false)));

		public void Tick()
		{
			client.Tick();
			if (!Ready) return;
			// Changes start once everybody is in (a player who joins later gets the cars from the join snapshot in game).
			if (soak.bots.All(b => b.Ready) && soak.random.NextDouble() < soak.options.ChangesPerSecond * soak.options.TickMs / 1000.0) Change();
			if (index == 0) return;
			var due = checks.Due(Cars.Keys);
			if (due.Count == 0) return;
			using var packet = new Packet((int)PacketTypes.carCheck);
			CarCheckWire.Write(packet, due.Select(car => (car, Hash(car))).ToList());
			client.Send(packet.FrameBuffer, packet.FrameLength);
		}

		private void Change()
		{
			var car = soak.random.Next(soak.options.Cars);
			if (car == 0 && soak.now < soak.quietCarUntil) car = 1 + soak.random.Next(soak.options.Cars - 1);
			// Each player works on its own parts (the part locks in game).
			var keys = Enumerable.Range(0, soak.options.PartsPerCar).Where(k => k % soak.bots.Count == index).ToList();
			var key = keys[soak.random.Next(keys.Count)];
			var state = (soak.random.Next(2) == 0, (float)Math.Round(soak.random.NextDouble(), 3));
			Cars[car][key] = state;
			checks.Touched(car);
			soak.result.Changes++;
			using var packet = new Packet((int)PacketTypes.partScript);
			packet.Write(car);
			packet.Write(key);
			packet.Write(state.Item1);
			packet.Write(state.Item2);
			client.Send(packet.FrameBuffer, packet.FrameLength);
		}
	}
}