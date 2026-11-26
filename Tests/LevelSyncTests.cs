using CMS21Together.Shared.Economy;
using NUnit.Framework;

namespace CMS21Together.Tests;

/// <summary>The session-wide level sync: whoever is furthest raises the rest.</summary>
[TestFixture]
public class LevelSyncTests
{
	[TestCase(5, 0, 4, 999, true)]
	[TestCase(5, 100, 5, 99, true)]
	[TestCase(5, 99, 5, 100, false)]
	[TestCase(4, 500, 5, 0, false)]
	[TestCase(5, 100, 5, 100, false)]
	[TestCase(1, 0, 0, 0, true)]
	public void IsAheadComparesLevelThenExperience(int level, int exp, int otherLevel, int otherExp, bool expected)
	{
		Assert.That(LevelSync.IsAhead(level, exp, otherLevel, otherExp), Is.EqualTo(expected));
	}
}
