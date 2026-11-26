using CMS21Together.Shared.Economy;
using NUnit.Framework;

namespace CMS21Together.Tests;

/// <summary>Skill points per order: at least one, more for more complex orders.</summary>
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
}
