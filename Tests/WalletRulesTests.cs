using CMS21Together.Shared.Economy;
using NUnit.Framework;

namespace CMS21Together.Tests;

/// <summary>Review fix #2: money spent or earned outside the garage is recorded, so it is never free.</summary>
[TestFixture]
public class WalletRulesTests
{
	[Test]
	public void AwayScenesRecordWithoutTheGarageBeingReady()
	{
		Assert.That(WalletRules.Record(false, true, WalletPlace.Away, false), Is.True, "a junkyard/salon/barn purchase is a transaction");
		Assert.That(WalletRules.Live(WalletPlace.Away, false), Is.True, "and the shared balance is shown there");
	}

	[Test]
	public void GarageRecordsOnlyOnceReady()
	{
		Assert.That(WalletRules.Record(false, true, WalletPlace.Garage, false), Is.False, "the garage's own load");
		Assert.That(WalletRules.Record(false, true, WalletPlace.Garage, true), Is.True);
	}

	[TestCase(true, true, WalletPlace.Away, true)]
	[TestCase(false, false, WalletPlace.Away, true)]
	[TestCase(false, true, WalletPlace.None, true)]
	public void HostStateLoadsAndUnsyncedWalletsAreNotRecorded(bool applying, bool shown, WalletPlace place, bool ready)
	{
		Assert.That(WalletRules.Record(applying, shown, place, ready), Is.False);
	}
}