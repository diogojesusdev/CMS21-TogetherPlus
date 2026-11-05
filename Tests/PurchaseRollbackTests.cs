using CMS21Together.BotHarness;
using System;
using System.Collections.Generic;
using CMS21Together.Shared;
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

	[Test]
	public void AResetForgetsAPendingDebitAndItsUndo()
	{
		var linker = new PurchaseLinker();
		var undone = 0;
		linker.Debit(4, 9);
		linker.OnRefused(9, () => undone++);

		linker.Reset();

		Assert.That(linker.TxnFor(4), Is.Zero, "a session that starts over has no debit from the last one");
		linker.Refused(9);
		Assert.That(undone, Is.Zero);
		Assert.That(linker.RolledBack, Is.Zero);
	}

	[Test]
	public void OnlyRealDebitsWithAnActionAreTracked()
	{
		var linker = new PurchaseLinker();
		linker.OnRefused(0, () => Assert.Fail("transaction 0 is not a purchase"));
		linker.OnRefused(-3, () => Assert.Fail("a negative transaction is not a purchase"));
		linker.OnRefused(4, null);

		linker.Refused(0);
		linker.Refused(4);

		Assert.That(linker.RolledBack, Is.Zero);
	}

	[Test]
	public void EveryUndoOfARefusedDebitRunsOnce()
	{
		var linker = new PurchaseLinker();
		var steps = new List<string>();
		linker.OnRefused(4, () => steps.Add("car"));
		linker.OnRefused(4, () => steps.Add("item"));

		linker.Refused(4);
		linker.Refused(4);

		Assert.That(steps, Is.EqualTo(new[] { "car", "item" }));
		Assert.That(linker.RolledBack, Is.EqualTo(2));
	}

	[Test]
	public void AFailingUndoDoesNotStopTheNextOneAndIsNotCounted()
	{
		var linker = new PurchaseLinker();
		var after = false;
		var logged = new List<string>();
		var previousSink = Log.Sink;
		var previousLevel = Log.MinLevel;
		try
		{
			Log.Sink = new Sink(logged);
			Log.MinLevel = LogLevel.Error;

			linker.OnRefused(4, () => throw new InvalidOperationException("the game refused the undo"));
			linker.OnRefused(4, () => after = true);

			Assert.DoesNotThrow(() => linker.Refused(4));
			Assert.That(after, Is.True, "the remaining undos still run");
			Assert.That(linker.RolledBack, Is.EqualTo(1), "only the undo that ran counts");
			Assert.That(logged, Has.Count.EqualTo(1));
		}
		finally
		{
			Log.Sink = previousSink;
			Log.MinLevel = previousLevel;
		}
	}

	private sealed class Sink : ILogSink
	{
		private readonly List<string> messages;

		public Sink(List<string> messages) => this.messages = messages;

		public void Write(LogLevel level, string message) => messages.Add(message);
	}
}

