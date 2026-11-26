namespace CMS21Together.Shared.Economy;

/// <summary>Compares two players' progress for the session-wide level sync.</summary>
public static class LevelSync
{
	/// <summary>True when <paramref name="level"/> / <paramref name="exp"/> is further than the other pair.</summary>
	public static bool IsAhead(int level, int exp, int otherLevel, int otherExp) =>
		level > otherLevel || (level == otherLevel && exp > otherExp);
}
