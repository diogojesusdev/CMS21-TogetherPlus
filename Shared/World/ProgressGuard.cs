namespace CMS21Together.Shared.World;

/// <summary>
/// Level and XP never go down in the game, so a client report that lowers them is a stale profile (for example a
/// rejoin that loaded before the player's stats arrived) and must not overwrite the stored progress. Skill points do
/// go down when spent and are not checked here.
/// </summary>
public static class ProgressGuard
{
	/// <summary>True when (<paramref name="level"/>, <paramref name="exp"/>) is below the stored values.</summary>
	public static bool Lowers(int storedLevel, int storedExp, int level, int exp) =>
		level < storedLevel || level == storedLevel && exp < storedExp;
}