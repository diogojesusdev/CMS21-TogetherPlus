using CMS21Together.BotHarness;
using CMS21Together.Shared.Economy;
using NUnit.Framework;

namespace CMS21Together.Tests;

/// <summary>Phase 6 exit test: a refused purchase is rolled back on the buyer and never reaches anyone else.</summary>
[TestFixture]
public class PurchaseRollbackTests
{
	[TestCase(2, 60, 40, 20, 0.0, 1)]
	[TestCase(8, 120, 80, 40, 0.05, 2)]
	[TestCase(4, 90, 250, 150, 0.2, 3)]
	// Phase 8 assessment: a revert of a refused paint job reached the painter after its next, paid job, so it kept the
	// old colour (first seen at 422 s); also past the 1,024 decisions the host remembers per player.
	[TestCase(8, 600, 100, 20, 0.05, 1)]
	public void BotsRacingForTheLastMoneyNeverOwnAnythingUncharged(int players, int seconds, int latency, int jitter, double loss, int seed)
	{
		var result = PurchaseSoak.Run(new PurchaseSoakOptions
		{
			Players = players,
			Seconds = seconds,
			LatencyMs = latency,
			JitterMs = jitter,
			LossRate = loss,
			Seed = seed
		});
		Assert.That(result.Passed, Is.True, result.ToString());
		Assert.That(result.ItemsRefused + result.PaintsRefused + result.CarsRolledBack, Is.GreaterThan(0), result.ToString());
	}

	[Test]
	public void LinkerTiesAFrameToItsDebitAndUndoesOnlyRefusedOnes()
	{
		var linker = new PurchaseLinker();
		var undone = 0;
		linker.Debit(10, 4);
		Assert.That(linker.TxnFor(10), Is.EqualTo(4));
		Assert.That(linker.TxnFor(11), Is.Zero, "the next frame is not paid by it");
		linker.OnRefused(4, () => undone++);
		linker.OnRefused(5, () => undone += 10);
		linker.Refused(4);
		linker.Refused(4);
		Assert.That(undone, Is.EqualTo(1), "undone once");
		linker.Processed(5);
		linker.Refused(5);
		Assert.That(undone, Is.EqualTo(1), "an applied debit keeps its purchase");
	}
}