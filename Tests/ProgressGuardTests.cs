using CMS21Together.Shared.World;
using NUnit.Framework;

namespace CMS21Together.Tests;

/// <summary>Review fix #1 follow-up: a stale profile never lowers a player's stored level or XP on the host.</summary>
[TestFixture]
public class ProgressGuardTests
{
	[Test]
	public void AStaleLevel1ProfileDoesNotOverwriteAStoredLevel5()
	{
		Assert.That(ProgressGuard.Lowers(5, 1200, 1, 0), Is.True);
	}

	[TestCase(5, 1200, 5, 1100, true)]
	[TestCase(5, 1200, 5, 1200, false)]
	[TestCase(5, 1200, 5, 1300, false)]
	[TestCase(5, 1200, 6, 0, false)]
	[TestCase(1, 0, 1, 0, false)]
	public void OnlyProgressGoesThrough(int storedLevel, int storedExp, int level, int exp, bool lowers)
	{
		Assert.That(ProgressGuard.Lowers(storedLevel, storedExp, level, exp), Is.EqualTo(lowers));
	}
}