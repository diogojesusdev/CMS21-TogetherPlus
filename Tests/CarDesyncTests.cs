using System.Collections.Generic;
using System.Linq;
using CMS21Together.BotHarness;
using CMS21Together.Shared;
using CMS21Together.Shared.World;
using NUnit.Framework;

namespace CMS21Together.Tests;

/// <summary>Phase 6: the per-car desync detector (built from the live game on every side).</summary>
[TestFixture]
public class CarDesyncTests
{
	[TestCase(4, 180, 0, 0, 0.0, 1)]
	[TestCase(8, 240, 80, 40, 0.05, 2)]
	[TestCase(4, 180, 250, 150, 0.2, 3)]
	public void ADivergedCarConvergesWithinOneCycleAndNothingElseIsPushed(int players, int seconds, int latency, int jitter, double loss, int seed)
	{
		var result = CarSoak.Run(new CarSoakOptions
		{
			Players = players,
			Seconds = seconds,
			LatencyMs = latency,
			JitterMs = jitter,
			LossRate = loss,
			Seed = seed
		});
		Assert.That(result.Passed, Is.True, result.ToString());
		Assert.That(result.Converged, Is.GreaterThanOrEqualTo(5), result.ToString());
	}

	[Test]
	public void HashIgnoresOrderAndNoiseBelowOnePercent()
	{
		var a = new[] { new PartState("a", false, 0.501f, 2, "x", true), new PartState("b", true, 1f, 1, null, false) };
		var b = new[] { new PartState("b", true, 1.001f, 1, "", false), new PartState("a", false, 0.499f, 2, "x", true) };
		Assert.That(CarHash.Of(a), Is.EqualTo(CarHash.Of(b)));
		Assert.That(CarHash.Of(a), Is.Not.EqualTo(CarHash.Of(a.Take(1))));
		Assert.That(CarHash.Of(new[] { new PartState("a", true, 0.5f, 2, "x", true) }),
			Is.Not.EqualTo(CarHash.Of(new[] { new PartState("a", false, 0.5f, 2, "x", true) })));
	}

	[Test]
	public void TheHostWaitsUntilTheCarIsIdleAndUnlocked()
	{
		long now = 0;
		var detector = new CarDesyncDetector(() => now);
		detector.Touched(3);
		Assert.That(detector.ShouldPush(2, 3, "a", "b", false), Is.False, "just changed");
		now += CarDesyncDetector.IdleMs;
		Assert.That(detector.ShouldPush(2, 3, "a", "b", true), Is.False, "someone works on it");
		Assert.That(detector.ShouldPush(2, 3, "a", "a", false), Is.False, "same");
		Assert.That(detector.ShouldPush(2, 3, "a", null, false), Is.False, "the host cannot read it");
		Assert.That(detector.ShouldPush(2, 3, "a", "b", false), Is.True);
	}

	[Test]
	public void ADifferenceAPushDidNotRepairIsNotPushedAgain()
	{
		long now = CarDesyncDetector.IdleMs;
		var detector = new CarDesyncDetector(() => now);
		Assert.That(detector.ShouldPush(2, 3, "a", "b", false), Is.True);
		Assert.That(detector.ShouldPush(2, 3, "a", "b", false), Is.False, "the same pair again");
		Assert.That(detector.Unrepaired, Is.EqualTo(1));
		Assert.That(detector.ShouldPush(2, 3, "c", "b", false), Is.True, "a new difference");
		Assert.That(detector.ShouldPush(4, 3, "a", "b", false), Is.True, "another player");
	}

	[Test]
	public void APlayerReportsOnlyCarsItHasNotChangedLately()
	{
		long now = 0;
		var checks = new CarCheckClient(() => now);
		Assert.That(checks.Due(new[] { 0, 1 }), Is.Empty, "not yet");
		now = CarCheckClient.DefaultEveryMs - 1000;
		checks.Touched(1);
		now = CarCheckClient.DefaultEveryMs;
		Assert.That(checks.Due(new[] { 0, 1 }), Is.EqualTo(new[] { 0 }));
	}

	[Test]
	public void ForcedResyncReportsIdleCarsAtOnce()
	{
		long now = 0;
		var checks = new CarCheckClient(() => now);
		Assert.That(checks.Due(new[] { 1, 2 }), Is.Empty, "not yet due");
		checks.Touched(2);
		now = CarDesyncDetector.IdleMs;
		checks.ForceNow();
		Assert.That(checks.Due(new[] { 1, 2 }), Is.EquivalentTo(new[] { 1, 2 }));
		Assert.That(checks.Due(new[] { 1, 2 }), Is.Empty, "then back to the normal cycle");
	}

	[Test]
	public void GhostAndMissingCarsAreActedOnOnlyAfterTwoReports()
	{
		var roster = new CarRoster();
		var first = roster.Compare(2, new[] { 1, 5 }, new[] { 1, 7 });
		Assert.That(first.ghosts, Is.Empty);
		Assert.That(first.missing, Is.Empty, "could be a car being spawned or deleted right now");
		var second = roster.Compare(2, new[] { 1, 5 }, new[] { 1, 7 });
		Assert.That(second.ghosts, Is.EqualTo(new[] { 5 }));
		Assert.That(second.missing, Is.EqualTo(new[] { 7 }));
		Assert.That(roster.Compare(2, new[] { 1, 5 }, new[] { 1, 7 }).ghosts, Is.Empty, "then two more reports before acting again");
	}

	[Test]
	public void ADifferenceThatGoesAwayIsForgotten()
	{
		var roster = new CarRoster();
		roster.Compare(3, new[] { 4 }, new int[0]);
		roster.Compare(3, new int[0], new int[0]);
		Assert.That(roster.Compare(3, new[] { 4 }, new int[0]).ghosts, Is.Empty, "not two reports in a row");
		roster.Compare(4, new[] { 9 }, new int[0]);
		roster.PlayerLeft(4);
		Assert.That(roster.Compare(4, new[] { 9 }, new int[0]).ghosts, Is.Empty);
	}

	[Test]
	public void ReportsCarryTheLoadedCarsAndOldReportsStillRead()
	{
		using var packet = new Packet((int)PacketTypes.carCheck);
		CarCheckWire.Write(packet, new List<(int, string)> { (1, "h") }, new[] { 1, 3 });
		using var read = new Packet(packet.Id, packet.PayloadToArray());
		Assert.That(CarCheckWire.Read(read).Count, Is.EqualTo(1));
		Assert.That(CarCheckWire.ReadLoaded(read), Is.EqualTo(new[] { 1, 3 }));

		using var old = new Packet((int)PacketTypes.carCheck);
		CarCheckWire.Write(old, new List<(int, string)>());
		using var oldRead = new Packet(old.Id, old.PayloadToArray());
		CarCheckWire.Read(oldRead);
		Assert.That(CarCheckWire.ReadLoaded(oldRead), Is.Null);
	}
}