using CMS21Together.Shared.Data.Vanilla.Cars;
using NUnit.Framework;

namespace CMS21Together.Tests;

/// <summary>
/// Loading a car writes every one of its fluid levels through the same game method a player's pour does. A player
/// walking back into the garage used to broadcast the car's load-time levels, which reverted the draining and
/// filling of whoever was working on that car.
/// </summary>
[TestFixture]
public class FluidSendRulesTests
{
	/// <summary>A pour by this player on a car that is loaded and settled here.</summary>
	private static bool Send(bool connected = true, bool applying = false, bool suppressed = false,
		bool gameReady = true, bool inGarage = true, bool tracked = true, bool ready = true, bool needResync = false) =>
		FluidSendRules.ShouldSend(connected, applying, suppressed, gameReady, inGarage, tracked, ready, needResync);

	[Test]
	public void APourOnASettledCarIsSent()
	{
		Assert.That(Send(), Is.True);
	}

	[Test]
	public void ACarStillLoadingHereIsSilent()
	{
		Assert.That(Send(ready: false), Is.False);
	}

	[Test]
	public void TheCopyLeftOverFromBeforeASceneChangeIsSilent()
	{
		// The garage reloads its cars from the local save when the player comes back; those levels are stale.
		Assert.That(Send(needResync: true), Is.False);
	}

	[Test]
	public void AnUntrackedCarIsSilent()
	{
		Assert.That(Send(tracked: false), Is.False);
	}

	[Test]
	public void TheGarageStillLoadingIsSilent()
	{
		Assert.That(Send(gameReady: false), Is.False);
	}

	[Test]
	public void TheHostsOwnChangeIsNotSentBack()
	{
		Assert.That(Send(applying: true), Is.False);
		Assert.That(Send(suppressed: true), Is.False);
	}

	[Test]
	public void NothingIsSentOutsideTheGarageOrWithoutASession()
	{
		Assert.That(Send(inGarage: false), Is.False);
		Assert.That(Send(connected: false), Is.False);
	}
}
