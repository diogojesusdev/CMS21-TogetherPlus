using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CMS21Together.BotHarness;
using CMS21Together.Shared.Net;
using CMS21Together.Shared.World;
using NUnit.Framework;

namespace CMS21Together.Tests;

/// <summary>Phase 7: 8 players, movement coalescing, bounded movement lag, host tick budget, config, UID ranges.</summary>
[TestFixture]
public class ScaleTests
{
	private static byte[] Frame(byte tag, int size = 20)
	{
		var frame = new byte[size];
		frame[size - 1] = tag;
		return frame;
	}

	private static List<byte[]> Drain(SendQueue queue)
	{
		var frames = new List<byte[]>();
		while (queue.TryDequeue(out var frame, out _)) frames.Add(frame);
		return frames;
	}

	[Test]
	public void KeyedMovementReplacesThePendingFrameInPlace()
	{
		var queue = new SendQueue();
		var posA = CoalesceKeys.Movement(2, 1);
		var posB = CoalesceKeys.Movement(3, 1);
		queue.Enqueue(Frame(1), SendPriority.Normal, true, posA);
		queue.Enqueue(Frame(2));
		queue.Enqueue(Frame(3), SendPriority.Normal, true, posB);
		queue.Enqueue(Frame(4, 30), SendPriority.Normal, true, posA);
		queue.Enqueue(Frame(5), SendPriority.Normal, true, posA);

		Assert.That(queue.Count, Is.EqualTo(3), "at most one pending frame per key");
		Assert.That(queue.Coalesced, Is.EqualTo(2));
		Assert.That(queue.QueuedBytes, Is.EqualTo(60));
		Assert.That(Drain(queue).Select(f => f[f.Length - 1]), Is.EqualTo(new byte[] { 5, 2, 3 }), "latest wins, in the first one's place");
		Assert.That(queue.QueuedBytes, Is.Zero);

		queue.Enqueue(Frame(6), SendPriority.Normal, true, posA);
		Assert.That(queue.Count, Is.EqualTo(1), "a sent frame is not replaced: the next one queues anew");
	}

	[Test]
	public void OnlyUnreliableNormalSingleFramesCoalesce()
	{
		var queue = new SendQueue();
		const long key = 42;
		queue.Enqueue(Frame(1), SendPriority.Normal, false, key);
		queue.Enqueue(Frame(2), SendPriority.Normal, false, key);
		queue.Enqueue(Frame(3), SendPriority.Bulk, true, key);
		queue.Enqueue(Frame(4), SendPriority.Bulk, true, key);
		queue.Enqueue(new[] { Frame(5), Frame(6) }, SendPriority.Normal, true, key);
		queue.Enqueue(new[] { Frame(7), Frame(8) }, SendPriority.Normal, true, key);
		Assert.That(queue.Coalesced, Is.Zero, "reliable, bulk and multi-frame messages are never replaced");
		Assert.That(queue.Count, Is.EqualTo(8));
	}

	[Test]
	public void MovementIsNeverDroppedForBacklogButStaysBounded()
	{
		var queue = new SendQueue(unreliableDropThreshold: 100);
		queue.Enqueue(Frame(0, 200));
		Assert.That(queue.Enqueue(Frame(1), SendPriority.Normal, true), Is.EqualTo(EnqueueResult.Dropped), "plain unreliable frames drop on a backlog");
		for (var i = 0; i < 1000; i++)
			Assert.That(queue.Enqueue(Frame((byte)i), SendPriority.Normal, true, CoalesceKeys.Movement(i % 8, 1)), Is.EqualTo(EnqueueResult.Queued));
		Assert.That(queue.Count, Is.EqualTo(9), "one per player on top of the backlog");
		queue.Clear();
		Assert.That(queue.Enqueue(Frame(9), SendPriority.Normal, true, CoalesceKeys.Movement(1, 1)), Is.EqualTo(EnqueueResult.Queued));
		Assert.That(queue.Count, Is.EqualTo(1), "Clear forgets the pending keys");
	}

	[Test]
	public void CoalesceKeysAreNonZeroAndDistinct()
	{
		var keys = new HashSet<long>();
		for (var player = 0; player <= ServerConfig.MaxSupportedPlayers; player++)
		foreach (var type in new[] { 0, 1, 2, 65535 })
		{
			var key = CoalesceKeys.Movement(player, type);
			Assert.That(key, Is.Not.Zero);
			Assert.That(keys.Add(key), Is.True);
		}
	}

	[Test]
	public void MovementLagStaysBoundedUnderTheClumsyProfile()
	{
		var result = MovementSoak.Run(new MovementSoakOptions { Seconds = 90, Seed = 3 });
		TestContext.WriteLine(result);
		Assert.That(result.Passed, Is.True, result.ToString());
		Assert.That(result.StaleP50, Is.LessThanOrEqualTo(result.IntervalMs + result.RttMs + 100), "about one send interval plus the RTT");
		Assert.That(result.Coalesced, Is.GreaterThan(0));

		var old = MovementSoak.Run(new MovementSoakOptions { Seconds = 90, Seed = 3, Coalesce = false });
		TestContext.WriteLine(old);
		Assert.That(old.LastQuarterP99, Is.GreaterThan(old.FirstQuarterP99 * 2), "the old reliable path builds a backlog: the soak sees it");
	}

	[Test]
	public void EightPlayersFitAndANinthIsTurnedAway()
	{
		long now = 0;
		var host = new NetHost(8, "scale-1", () => now);
		var clients = new List<NetClient>();
		var rejected = new List<string>();
		for (var i = 0; i < 9; i++)
		{
			var (a, b) = SimLink.CreatePair(null, () => now);
			var client = new NetClient("scale-1", () => now);
			client.Disconnected += reason => rejected.Add(reason);
			client.Connect(a, i == 0 ? host.HostKey : null);
			host.Accept(b);
			clients.Add(client);
			for (var round = 0; round < 4; round++)
			{
				host.Tick();
				foreach (var c in clients) c.Tick();
			}
		}
		Assert.That(clients.Take(8).Select(c => c.SlotId), Is.EqualTo(Enumerable.Range(1, 8)));
		Assert.That(clients[7].MaxPlayers, Is.EqualTo(8));
		Assert.That(host.PeerCount, Is.EqualTo(8));
		Assert.That(rejected.Single(), Does.Contain("full"));
	}

	[Test]
	public void TheHostTickStopsAtItsBudgetAndKeepsOrder()
	{
		long now = 0;
		long fakeTicks = 0;
		var host = new NetHost(2, "scale-1", () => now)
		{
			BudgetMs = 4,
			// Every read of the budget clock is 1 ms later: the budget allows MinEventsPerTick, then a few.
			BudgetClock = () => fakeTicks += System.Diagnostics.Stopwatch.Frequency / 1000
		};
		var seen = new List<int>();
		host.FrameReceived += (_, frame) => seen.Add(new CMS21Together.Shared.Packet(frame.Type, frame.CopyPayload()).ReadInt());
		var (a, b) = SimLink.CreatePair(null, () => now);
		var client = new NetClient("scale-1", () => now);
		client.Connect(a, host.HostKey);
		host.Accept(b);
		for (var round = 0; round < 4; round++)
		{
			host.Tick();
			client.Tick();
		}
		for (var i = 0; i < 500; i++)
		{
			using var packet = new CMS21Together.Shared.Packet(100);
			packet.Write(i);
			client.Send(packet.FrameBuffer, packet.FrameLength);
		}
		host.Tick();
		Assert.That(seen.Count, Is.GreaterThanOrEqualTo(NetHost.MinEventsPerTick).And.LessThan(500), "the rest waits for the next tick");
		Assert.That(host.BudgetHits, Is.EqualTo(1));
		Assert.That(host.Backlog, Is.EqualTo(500 - seen.Count));
		for (var round = 0; round < 20 && seen.Count < 500; round++) host.Tick();
		Assert.That(seen, Is.EqualTo(Enumerable.Range(0, 500)), "every frame, once, in order");
		Assert.That(host.MaxTickMs, Is.GreaterThan(0));

		host.BudgetMs = 0;
		for (var i = 0; i < 500; i++)
		{
			using var packet = new CMS21Together.Shared.Packet(100);
			packet.Write(1000 + i);
			client.Send(packet.FrameBuffer, packet.FrameLength);
		}
		host.Tick();
		Assert.That(seen.Count, Is.EqualTo(1000), "no budget: everything in one tick");
	}

	[TestCase(null, 8)]
	[TestCase("", 8)]
	[TestCase("{\"MaxPlayers\": 4}", 4)]
	[TestCase("{ \"MaxPlayers\" : 8 }", 8)]
	[TestCase("{\"MaxPlayers\": 2}", 2)]
	[TestCase("{\"MaxPlayers\": 1}", 8)]
	[TestCase("{\"MaxPlayers\": 9}", 8)]
	[TestCase("{\"MaxPlayers\": -3}", 8)]
	[TestCase("{\"MaxPlayers\": \"six\"}", 8)]
	[TestCase("not json", 8)]
	public void ConfigReadsMaxPlayersOrFallsBack(string json, int expected) =>
		Assert.That(ServerConfig.Parse(json).MaxPlayers, Is.EqualTo(expected));

	[Test]
	public void ConfigIsCreatedWithTheDefaultsAndReadBack()
	{
		var dir = Path.Combine(Path.GetTempPath(), "tp-config-" + Guid.NewGuid().ToString("N"));
		try
		{
			Assert.That(ServerConfig.LoadOrCreate(dir).MaxPlayers, Is.EqualTo(8));
			var path = Path.Combine(dir, ServerConfig.FileName);
			Assert.That(File.Exists(path), Is.True);
			File.WriteAllText(path, "{\"MaxPlayers\": 6}");
			Assert.That(ServerConfig.LoadOrCreate(dir).MaxPlayers, Is.EqualTo(6));
		}
		finally
		{
			if (Directory.Exists(dir)) Directory.Delete(dir, true);
		}
	}

	[Test]
	public void UidRangesGrowForEightPlayersWithoutOverlap()
	{
		Assert.That(UidRanges.Initial(8), Is.EqualTo(new long[] { 1000, 10_000_000, 20_000_000, 30_000_000, 40_000_000, 50_000_000, 60_000_000, 70_000_000 }));
		Assert.That(UidRanges.Initial(4), Is.EqualTo(new long[] { 1000, 10_000_000, 20_000_000, 30_000_000 }), "same as hf3");

		var hf3 = new long[] { 5_400, 10_000_321, 20_000_000, 30_007_000 };
		var grown = UidRanges.Ensure(hf3, 8);
		Assert.That(grown.Take(4), Is.EqualTo(hf3), "existing counters are kept");
		Assert.That(grown.Skip(4), Is.EqualTo(new long[] { 40_000_000, 50_000_000, 60_000_000, 70_000_000 }));
		Assert.That(UidRanges.Ensure(grown, 8), Is.SameAs(grown), "nothing to grow");
		Assert.That(UidRanges.Ensure(grown, 4), Is.SameAs(grown), "never shrinks");

		var overflowed = new long[] { 1_000, 10_000_000, 20_000_000, 45_000_000 };
		var safe = UidRanges.Ensure(overflowed, 8);
		Assert.That(safe.Skip(4), Is.EqualTo(new long[] { 50_000_000, 60_000_000, 70_000_000, 80_000_000 }), "past every counter in use");
		Assert.That(safe.Distinct().Count(), Is.EqualTo(8));
		Assert.That(UidRanges.Ensure(null, 2), Is.EqualTo(new long[] { 1000, 10_000_000 }));
	}

	[Test]
	public void TheCounterFollowsEveryUidASlotUses()
	{
		// Phase 8 assessment: group items (wheels, engines) and their parts never moved the counter, so after a
		// rejoin a player could be handed a UID already in the shared inventory.
		var counter = 20_000_000L;
		counter = UidRanges.Advance(counter, 20_000_000);
		Assert.That(counter, Is.EqualTo(20_000_001));
		counter = UidRanges.Advance(counter, 20_000_050);
		Assert.That(counter, Is.EqualTo(20_000_051), "a group's parts use UIDs too");
		Assert.That(UidRanges.Advance(counter, 20_000_010), Is.EqualTo(counter), "never back");
		Assert.That(UidRanges.Advance(counter, 1_500), Is.EqualTo(counter), "an item from another slot's range");
		Assert.That(UidRanges.Advance(counter, 60_000_000), Is.EqualTo(counter), "never into a higher slot's range");
	}

	public enum Scene
	{
		Unknown,
		Garage,
		Junkyard
	}

	[TestCase(Scene.Garage, Scene.Garage, true)]
	[TestCase(Scene.Garage, Scene.Junkyard, false)]
	[TestCase(Scene.Unknown, Scene.Junkyard, true)]
	[TestCase(Scene.Garage, Scene.Unknown, true)]
	public void MovementGoesOnlyToPlayersInTheSameScene(Scene mover, Scene viewer, bool sent) =>
		Assert.That(Interest.SameScene(mover, viewer), Is.EqualTo(sent));
}