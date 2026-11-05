using System.Collections.Generic;
using CMS21Together.Shared.Data.Vanilla.Cars;
using NUnit.Framework;

namespace CMS21Together.Tests;

/// <summary>
/// A car received from another player is built into game objects before the game walks it. A payload that would make
/// the game walk nonsense must be refused first: a native access violation after the fact cannot be caught, so
/// "recover" means "do not apply it and ask for the car again".
/// </summary>
[TestFixture]
public class CarPayloadRulesTests
{
	[Test]
	public void AUsableCarIsAccepted()
	{
		Assert.That(CarPayloadRules.Problem("car_katagiritamago", 4, 121, true, 4, 8), Is.Null);
	}

	[Test]
	public void ACarWithNoNameIsRefused()
	{
		Assert.That(CarPayloadRules.NameIsUsable(null), Is.False);
		Assert.That(CarPayloadRules.NameIsUsable(""), Is.False);
		Assert.That(CarPayloadRules.NameIsUsable("   "), Is.False);
		Assert.That(CarPayloadRules.Problem(null, 0, 0, true, 0, 0), Does.Contain("car name"));

		var tooLong = new string('c', CarPayloadRules.MaxNameLength + 1);
		Assert.That(CarPayloadRules.NameIsUsable(tooLong), Is.False);
	}

	[Test]
	public void AbsurdCountsAreRefused()
	{
		Assert.That(CarPayloadRules.CountIsSane(-1, 10), Is.False);
		Assert.That(CarPayloadRules.CountIsSane(11, 10), Is.False);
		Assert.That(CarPayloadRules.CountIsSane(10, 10), Is.True);

		Assert.That(CarPayloadRules.Problem("car_x", CarPayloadRules.MaxBodyParts + 1, 10, true, 0, 0), Does.Contain("body parts"));
		Assert.That(CarPayloadRules.Problem("car_x", 1, CarPayloadRules.MaxParts + 1, true, 0, 0), Does.Contain("parts"));
		Assert.That(CarPayloadRules.Problem("car_x", 1, 1, true, CarPayloadRules.MaxWheelSizes + 1, 0), Does.Contain("wheel"));
		Assert.That(CarPayloadRules.Problem("car_x", 1, 1, true, 0, CarPayloadRules.MaxGearRatios + 1), Does.Contain("gear"));
	}

	[Test]
	public void ANullEntryIsRefused()
	{
		var withHole = new List<string> { "a", null, "c" };
		Assert.That(CarPayloadRules.AllPresent(withHole), Is.False);
		Assert.That(CarPayloadRules.AllPresent(new List<string> { "a", "b" }), Is.True);
		Assert.That(CarPayloadRules.AllPresent<string>(null), Is.True, "an optional list may be missing");
		Assert.That(CarPayloadRules.Problem("car_x", 1, 1, false, 0, 0), Does.Contain("part entries"));
	}

	[Test]
	public void OptionalArraysMayBeMissingButNotRunaway()
	{
		Assert.That(CarPayloadRules.SizesAreSane<int>(null, 4), Is.True);
		Assert.That(CarPayloadRules.SizesAreSane(new List<int> { 1, 2, 3 }, 4), Is.True);
		Assert.That(CarPayloadRules.SizesAreSane(new List<int> { 1, 2, 3, 4, 5 }, 4), Is.False);
	}
}
