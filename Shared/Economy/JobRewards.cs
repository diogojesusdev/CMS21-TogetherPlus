using System;

namespace CMS21Together.Shared.Economy;

/// <summary>
/// What an order is worth besides money: skill points that grow with its size, and a share of the player's current
/// level bar as XP. Pure so the tests can cover both curves; the client grants them on hand-over.
/// </summary>
public static class JobRewards
{
	/// <summary>A very complex order still fits in one hand-over.</summary>
	public const int MaxSkillPointsPerJob = 10;

	/// <summary>An order worth this much XP (or a story mission) is hard: it gives a whole level.</summary>
	public const int HardJobXp = 100;

	/// <summary>An ordinary order worth this much XP is big: three quarters of a level.</summary>
	public const int BigJobXp = 35;

	/// <summary>
	/// One for the order, one per extra task, one per four parts, and two more for a story mission.
	/// </summary>
	public static int SkillPoints(int tasks, int parts, bool mission)
	{
		var points = 1 + Math.Max(0, tasks - 1) + Math.Max(0, parts) / 4 + (mission ? 2 : 0);
		return Math.Min(points, MaxSkillPointsPerJob);
	}

	/// <summary>
	/// Share of the current level bar an order is worth: a hard order gives a whole level, two ordinary ones do too.
	/// </summary>
	public static double ExperienceFraction(int xp, bool mission)
	{
		if (mission || xp >= HardJobXp) return 1.0;
		if (xp >= BigJobXp) return 0.75;
		return 0.5;
	}

	/// <summary>What the order summary should show as the total payout: what the host actually credits.</summary>
	public static long DisplayedPayout(long totalPayout) =>
		totalPayout * Payouts.OrderPayoutMultiplier + Payouts.OrderPayoutFlatBonus;

	/// <summary>The task bonus as paid (the whole order payout is multiplied).</summary>
	public static long DisplayedTaskBonus(long taskBonus) => taskBonus * Payouts.OrderPayoutMultiplier;

	/// <summary>The job bonus as paid.</summary>
	public static long DisplayedJobBonus(long jobBonus) => jobBonus * Payouts.OrderPayoutMultiplier;
}
