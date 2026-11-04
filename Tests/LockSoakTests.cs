using CMS21Together.BotHarness;
using CMS21Together.Shared.Locks;
using NUnit.Framework;

namespace CMS21Together.Tests;

/// <summary>Lock contention over the real net stack (see <see cref="LockSoak"/> for the invariants checked every tick).</summary>
[TestFixture]
public class LockSoakTests
{
	[Test]
	public void EightPlayersFightOverHotKeysOnABadNetwork()
	{
		var result = LockSoak.Run(new LockSoakOptions
		{
			Players = 8,
			Seconds = 180,
			LatencyMs = 80,
			JitterMs = 40,
			LossRate = 0.05,
			KillsPerMinute = 0.3,
			Seed = 5
		});
		TestContext.WriteLine(result);
		Assert.That(result.Errors, Is.Empty);
		Assert.That(result.Denials, Is.GreaterThan(0), "no request was ever denied");
		Assert.That(result.WritesRejected, Is.GreaterThan(0), "fencing never rejected a racing write");
		Assert.That(result.Kills, Is.GreaterThan(0), "no player froze");
		Assert.That(result.MaxKillFreeMs, Is.LessThanOrEqualTo(ResourceLockManager.LeaseMs + 1000));
	}

	[Test]
	public void TwoPlayersOnAGoodNetwork()
	{
		var result = LockSoak.Run(new LockSoakOptions
		{
			Players = 2,
			Seconds = 120,
			LatencyMs = 20,
			KillsPerMinute = 0,
			Seed = 9
		});
		TestContext.WriteLine(result);
		Assert.That(result.Errors, Is.Empty);
		Assert.That(result.Grants, Is.GreaterThan(10));
	}

	[Test]
	public void HighLatencyAndHeavyLoss()
	{
		var result = LockSoak.Run(new LockSoakOptions
		{
			Players = 4,
			Seconds = 120,
			LatencyMs = 250,
			JitterMs = 150,
			LossRate = 0.2,
			RaceRate = 0.4,
			KillsPerMinute = 0.5,
			Seed = 13
		});
		TestContext.WriteLine(result);
		Assert.That(result.Errors, Is.Empty);
		Assert.That(result.WritesRejected, Is.GreaterThan(0));
	}
}