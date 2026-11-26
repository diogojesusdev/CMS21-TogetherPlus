using System;

namespace CMS21Together.Shared.Economy;

/// <summary>
/// Skill points an order is worth. Separate from the money payout: every order gives at least one point, and bigger,
/// more complex orders give more. Pure so the tests can cover the curve; the client grants the result on hand-over.
/// </summary>
public static class JobRewards
{
	/// <summary>A very complex order still fits in one hand-over.</summary>
	public const int MaxSkillPointsPerJob = 10;

	/// <summary>
	/// One for the order, one per extra task, one per four parts, and two more for a story mission.
	/// </summary>
	public static int SkillPoints(int tasks, int parts, bool mission)
	{
		var points = 1 + Math.Max(0, tasks - 1) + Math.Max(0, parts) / 4 + (mission ? 2 : 0);
		return Math.Min(points, MaxSkillPointsPerJob);
	}
}
