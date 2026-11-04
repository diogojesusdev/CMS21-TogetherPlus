namespace CMS21Together.Shared.World;

/// <summary>Car loader ids from object names, the fallback when a loader is not in the garage's list (audit A12).</summary>
public static class LoaderNames
{
	/// <summary>"CarLoader_3" (or "CarLoader_12") → 2; -1 when the name has no number at its end.</summary>
	public static int IdFromName(string name)
	{
		if (string.IsNullOrEmpty(name)) return -1;
		var end = name.Length;
		var start = end;
		while (start > 0 && char.IsDigit(name[start - 1])) start--;
		if (start == end || end - start > 6) return -1;
		return int.Parse(name.Substring(start, end - start)) - 1;
	}
}