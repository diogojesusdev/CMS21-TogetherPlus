using System;
using System.Collections.Generic;
using System.Linq;
using CMS21Together.Shared;
using CMS21Together.Shared.Net;

namespace CMS21Together.BotHarness;

public sealed class MovementSoakOptions
{
	public int Players = 8;
	public double Seconds = 120;
	public int LatencyMs = 100;
	public int JitterMs = 20;
	public double LossRate = 0.05;

	/// <summary>Per direction, per link. The default is below what 8 players' movement needs at 20 Hz.</summary>
	public long BytesPerSecond = 8_000;

	public int Hz = 20;

	/// <summary>Phase 7 path: unreliable, one pending frame per player and kind. Off = the old path (reliable, queued).</summary>
	public bool Coalesce = true;

	/// <summary>Other traffic (items, parts) per player, reliable.</summary>
	public int ReliablePerSecond = 1;

	public int ReliableBytes = 150;
	public int TickMs = 5;
	public int Seed = 1;
	public Action<string> Progress;
}

public sealed class MovementSoakResult
{
	public readonly List<string> Errors = new();
	public long Sent, Received, Coalesced;
	public double IntervalMs, RttMs;

	/// <summary>How old the newest position a player has of another player is, sampled every tick (ms).</summary>
	public double StaleP50, StaleP99, StaleMax;

	/// <summary>p99 staleness over the first and the last quarter of the run (growth = a backlog).</summary>
	public double FirstQuarterP99, LastQuarterP99;

	public bool Passed => Errors.Count == 0;

	public override string ToString() =>
		$"{(Passed ? "PASS" : "FAIL")}: movement staleness p50 {StaleP50:0} ms, p99 {StaleP99:0} ms, max {StaleMax:0} ms " +
		$"(first quarter p99 {FirstQuarterP99:0}, last quarter p99 {LastQuarterP99:0}; send interval {IntervalMs:0} + RTT {RttMs:0}); " +
		$"{Sent} sent, {Received} received, {Coalesced} coalesced" +
		(Passed ? "" : Environment.NewLine + string.Join(Environment.NewLine, Errors.Select(e => "  - " + e)));
}

/// <summary>
/// Phase 7 bounded-lag soak: a host and 7 players stream position and rotation at 20 Hz over links with the clumsy
/// profile (latency, jitter, loss) and too little bandwidth, plus some reliable traffic. The host relays movement like
/// the game (<c>Server.SendMovement</c>). Each player tracks how old the newest position of every other player is.
/// With coalescing that age stays bounded near one send interval plus the RTT (plus a little queueing); without it
/// a backlog builds and the age grows for as long as the run lasts.
/// </summary>
public sealed class MovementSoak
{
	private const string Version = "movement-soak-1";
	private const int Position = (int)PacketTypes.position;
	private const int Rotation = (int)PacketTypes.rotation;
	private const int Item = (int)PacketTypes.item;

	private readonly MovementSoakOptions options;
	private readonly MovementSoakResult result = new();
	private readonly List<Bot> bots = new();
	private readonly List<SimLink> links = new();
	private readonly List<(long at, double age)> samples = new();
	private NetHost host;
	private long now;

	public MovementSoak(MovementSoakOptions options) => this.options = options ?? new MovementSoakOptions();

	public static MovementSoakResult Run(MovementSoakOptions options) => new MovementSoak(options).Run();

	private long Clock() => now;

	public MovementSoakResult Run()
	{
		var previousLevel = Log.MinLevel;
		Log.MinLevel = LogLevel.Error;
		try
		{
			host = new NetHost(options.Players, Version, Clock) { BudgetMs = 0 };
			host.FrameReceived += Relay;
			var (serverEnd, clientEnd) = SimLink.CreatePair(null, Clock, "loopback");
			host.Accept(serverEnd);
			bots.Add(new Bot(this, clientEnd, host.HostKey));
			var random = new Random(options.Seed);
			for (var i = 1; i < options.Players; i++)
			{
				var sim = new SimOptions
				{
					LatencyMs = options.LatencyMs,
					JitterMs = options.JitterMs,
					LossRate = options.LossRate,
					BytesPerSecond = options.BytesPerSecond,
					Queued = true,
					Seed = random.Next(1, int.MaxValue / 8000)
				};
				var (hostEnd, botEnd) = SimLink.CreatePair(sim, Clock, "bot" + i);
				links.Add(hostEnd);
				links.Add(botEnd);
				host.Accept(hostEnd);
				bots.Add(new Bot(this, botEnd, null));
			}

			var end = (long)(options.Seconds * 1000);
			var warmup = Math.Min(5000, end / 10);
			var nextProgress = 10_000L;
			while (now < end)
			{
				now += options.TickMs;
				host.Tick();
				foreach (var bot in bots) bot.Step(now);
				if (now >= warmup) Sample();
				if (now < nextProgress) continue;
				nextProgress += 10_000;
				options.Progress?.Invoke($"{now / 1000}s: {result.Received} received, staleness now {(samples.Count > 0 ? samples[samples.Count - 1].age : 0):0} ms");
			}
			Summarize(warmup, end);
		}
		catch (Exception ex)
		{
			result.Errors.Add("Harness crashed: " + ex);
		}
		finally
		{
			try
			{
				host?.Stop("Soak finished.");
			}
			catch (Exception)
			{
				// The verdict is already made.
			}
			Log.MinLevel = previousLevel;
		}
		return result;
	}

	private void Relay(NetConnection from, NetFrame frame)
	{
		var kind = frame.Type;
		using var packet = new Packet(kind);
		packet.Write(frame.CopyPayload());
		var movement = kind == Position || kind == Rotation;
		foreach (var peer in host.Peers.ToArray())
		{
			if (peer.SlotId == from.SlotId) continue;
			if (movement && options.Coalesce)
				peer.Send(packet.FrameBuffer, packet.FrameLength, SendPriority.Normal, true, CoalesceKeys.Movement(from.SlotId, kind));
			else
				peer.Send(packet.FrameBuffer, packet.FrameLength);
		}
	}

	private void Sample()
	{
		foreach (var receiver in bots)
		{
			if (receiver.Client.State != NetClientState.Connected) continue;
			foreach (var sender in bots)
			{
				if (sender == receiver || sender.Client.SlotId == 0) continue;
				if (!receiver.Newest.TryGetValue(sender.Client.SlotId, out var sentAt)) continue;
				samples.Add((now, now - sentAt));
			}
		}
	}

	private static double Percentile(List<double> sorted, double p) =>
		sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(p * sorted.Count) - 1)];

	private void Summarize(long warmup, long end)
	{
		foreach (var link in links) result.Coalesced += link.Outbound?.Coalesced ?? 0;
		result.IntervalMs = 1000.0 / Math.Max(1, options.Hz);
		result.RttMs = 2.0 * options.LatencyMs;
		var all = samples.Select(s => s.age).OrderBy(a => a).ToList();
		if (all.Count == 0)
		{
			result.Errors.Add("No movement arrived.");
			return;
		}
		result.StaleP50 = Percentile(all, 0.50);
		result.StaleP99 = Percentile(all, 0.99);
		result.StaleMax = all[all.Count - 1];
		var quarter = (end - warmup) / 4;
		result.FirstQuarterP99 = Percentile(samples.Where(s => s.at < warmup + quarter).Select(s => s.age).OrderBy(a => a).ToList(), 0.99);
		result.LastQuarterP99 = Percentile(samples.Where(s => s.at >= end - quarter).Select(s => s.age).OrderBy(a => a).ToList(), 0.99);
		if (!options.Coalesce) return;

		// Bounded: about one send interval plus the RTT, with room for jitter, queueing and retransmitted reliable
		// frames ahead in the stream (a lost reliable frame costs an extra round trip).
		var bound = result.IntervalMs + result.RttMs + 2 * options.JitterMs + 2 * result.RttMs;
		if (result.StaleP99 > bound) result.Errors.Add($"p99 staleness {result.StaleP99:0} ms is over the bound {bound:0} ms.");
		if (result.LastQuarterP99 > result.FirstQuarterP99 * 1.5 + 50)
			result.Errors.Add($"Staleness grows: p99 {result.FirstQuarterP99:0} ms in the first quarter, {result.LastQuarterP99:0} ms in the last.");
	}

	private sealed class Bot
	{
		private readonly MovementSoak soak;
		private readonly Random random;
		private long nextMove, nextReliable;
		private int seq;

		public Bot(MovementSoak soak, NetLink link, byte[] hostKey)
		{
			this.soak = soak;
			random = new Random(soak.options.Seed * 31 + soak.bots.Count);
			Client = new NetClient(Version, soak.Clock);
			Client.FrameReceived += OnFrame;
			Client.Connect(link, hostKey);
		}

		public readonly NetClient Client;

		/// <summary>Per other player: when the newest position this player has of them was sent.</summary>
		public readonly Dictionary<int, long> Newest = new();

		public void Step(long time)
		{
			Client.Tick();
			if (Client.State != NetClientState.Connected) return;
			var options = soak.options;
			if (time >= nextMove)
			{
				nextMove = time + 1000 / Math.Max(1, options.Hz);
				Send(Position, time, 12);
				Send(Rotation, time, 16);
			}
			if (options.ReliablePerSecond > 0 && time >= nextReliable)
			{
				nextReliable = time + random.Next(1, 2 * 1000 / options.ReliablePerSecond);
				using var packet = new Packet(Item);
				packet.Write(Client.SlotId);
				packet.Write(time);
				packet.Write(seq++);
				packet.Write(new byte[options.ReliableBytes]);
				Client.Send(packet.FrameBuffer, packet.FrameLength);
			}
		}

		private void Send(int kind, long time, int filler)
		{
			using var packet = new Packet(kind);
			packet.Write(Client.SlotId);
			packet.Write(time);
			packet.Write(new byte[filler]);
			if (soak.options.Coalesce)
				Client.Send(packet.FrameBuffer, packet.FrameLength, SendPriority.Normal, true, CoalesceKeys.Movement(0, kind));
			else
				Client.Send(packet.FrameBuffer, packet.FrameLength);
			if (kind == Position) soak.result.Sent++;
		}

		private void OnFrame(NetFrame frame)
		{
			if (frame.Type != Position) return;
			using var packet = new Packet(frame.Type, frame.CopyPayload());
			var sender = packet.ReadInt();
			var sentAt = packet.Read<long>();
			soak.result.Received++;
			if (!Newest.TryGetValue(sender, out var newest) || sentAt > newest) Newest[sender] = sentAt;
		}
	}
}