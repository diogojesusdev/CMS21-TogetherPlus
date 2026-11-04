using System.Collections.Generic;
using CMS21Together.Shared.World;
using NUnit.Framework;

namespace CMS21Together.Tests;

/// <summary>Review fix #3: a late joiner loads a parking lot with empty slots.</summary>
[TestFixture]
public class ParkSlotsTests
{
	[Test]
	public void GapsInTheParkingLotFillTheRightSlots()
	{
		var slots = new string[4];
		var placed = ParkSlots.Place(new Dictionary<int, string> { [0] = "a", [2] = "c" }, slots.Length, (i, car) => slots[i] = car);
		Assert.That(placed, Is.EqualTo(2));
		Assert.That(slots, Is.EqualTo(new[] { "a", null, "c", null }));
	}

	[Test]
	public void SlotsOutsideTheLotAreSkipped()
	{
		var slots = new string[2];
		var placed = ParkSlots.Place(new Dictionary<int, string> { [-1] = "x", [1] = "b", [5] = "y" }, slots.Length, (i, car) => slots[i] = car);
		Assert.That(placed, Is.EqualTo(1));
		Assert.That(slots, Is.EqualTo(new[] { null, "b" }));
		Assert.That(ParkSlots.Place<string>(null, 2, (_, _) => { }), Is.Zero);
	}
}