using System;
using CMS21Together.Shared.Economy;
using NUnit.Framework;

namespace CMS21Together.Tests;

/// <summary>XP and skill points per order: at least one point, and XP sized in levels rather than raw points.</summary>
[TestFixture]
public class JobRewardsTests
{
	[TestCase(0, 0, false, 1)]
	[TestCase(1, 0, false, 1)]
	[TestCase(1, 3, false, 1, TestName = "ThreePartsStillOnePoint")]
	[TestCase(1, 4, false, 2)]
	[TestCase(2, 2, false, 2)]
	[TestCase(2, 4, false, 3)]
	[TestCase(3, 19, true, 9)]
	[TestCase(3, 22, true, 10)]
	[TestCase(6, 40, true, 10, TestName = "AVeryComplexOrderIsCapped")]
	public void SkillPointsGrowWithTheOrder(int tasks, int parts, bool mission, int expected)
	{
		Assert.That(JobRewards.SkillPoints(tasks, parts, mission), Is.EqualTo(expected));
	}

	[TestCase(-1, -5, false, 1)]
	[TestCase(2, -5, false, 2)]
	public void BadCountsStillGiveAtLeastOnePoint(int tasks, int parts, bool mission, int expected)
	{
		Assert.That(JobRewards.SkillPoints(tasks, parts, mission), Is.EqualTo(expected));
	}

	[TestCase(0, false, 0.5)]
	[TestCase(20, false, 0.5)]
	[TestCase(34, false, 0.5)]
	[TestCase(35, false, 0.75)]
	[TestCase(99, false, 0.75)]
	[TestCase(100, false, 1.0)]
	[TestCase(0, true, 1.0)]
	[TestCase(202, true, 1.0)]
	public void ExperienceIsAShareOfTheLevelBar(int xp, bool mission, double expected)
	{
		Assert.That(JobRewards.ExperienceFraction(xp, mission), Is.EqualTo(expected));
	}

	[Test]
	public void TwoSimpleOrdersFillOneLevel()
	{
		var bar = 375; // level 2 -> 3 on the game's curve
		var each = (int)Math.Ceiling(bar * JobRewards.ExperienceFraction(20, false));
		Assert.That(each * 2, Is.GreaterThanOrEqualTo(bar));
	}

	[Test]
	public void OneHardOrderFillsOneLevel()
	{
		var bar = 375;
		var xp = (int)Math.Ceiling(bar * JobRewards.ExperienceFraction(202, true));
		Assert.That(xp, Is.GreaterThanOrEqualTo(bar));
	}
}
