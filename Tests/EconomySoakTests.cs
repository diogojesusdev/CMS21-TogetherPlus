using CMS21Together.BotHarness;
using NUnit.Framework;

namespace CMS21Together.Tests;

/// <summary>The shared wallet over the real net stack (see <see cref="EconomySoak"/> for the invariants checked).</summary>
[TestFixture]
public class EconomySoakTests
{
	[Test]
	public void EightPlayersSpendAndEarnOnABadNetwork()
	{
		var result = EconomySoak.Run(new EconomySoakOptions
		{
			Players = 8,
			Seconds = 180,
			LatencyMs = 80,
			JitterMs = 40,
			LossRate = 0.05,
			KillsPerMinute = 0.3,
			Seed = 3
		});
		TestContext.WriteLine(result);
		Assert.That(result.Errors, Is.Empty);
		Assert.That(result.Recorded, Is.GreaterThanOrEqualTo(10_000), "not enough transactions");
		Assert.That(result.Refused, Is.GreaterThan(0), "no race for the last money happened");
		Assert.That(result.DuplicatesIgnored, Is.GreaterThan(0), "no replayed transaction reached the host");
		Assert.That(result.Kills, Is.GreaterThan(0), "no player froze");
		Assert.That(result.QuietChecks, Is.GreaterThanOrEqualTo(5));
	}

	[Test]
	public void TwoPlayersOnAGoodNetwork()
	{
		var result = EconomySoak.Run(new EconomySoakOptions
		{
			Players = 2,
			Seconds = 60,
			LatencyMs = 20,
			KillsPerMinute = 0,
			Seed = 7
		});
		TestContext.WriteLine(result);
		Assert.That(result.Errors, Is.Empty);
		Assert.That(result.Applied, Is.GreaterThan(500));
	}

	[Test]
	public void HighLatencyAndHeavyLoss()
	{
		var result = EconomySoak.Run(new EconomySoakOptions
		{
			Players = 4,
			Seconds = 90,
			LatencyMs = 250,
			JitterMs = 150,
			LossRate = 0.2,
			KillsPerMinute = 0.5,
			StartMoney = 500,
			Seed = 11
		});
		TestContext.WriteLine(result);
		Assert.That(result.Errors, Is.Empty);
		Assert.That(result.Refused, Is.GreaterThan(0));
	}
}