using System;
using System.Text.RegularExpressions;

namespace CMS21Together.Shared.Updates;

/// <summary>
/// Together+ version strings as they appear in release tags ("v0.5.0-rc12"), in the installed-version sidecar and in
/// the transport's handshake rejection ("Mod version mismatch: host has Together+ 0.5.0-rc12 (...)").
/// </summary>
public static class VersionText
{
	public const string Prefix = "Together+ ";

	private static readonly Regex TagPattern = new(@"^\s*[vV]?(?<version>[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z][0-9A-Za-z.\-]*)?)\s*$",
		RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private static readonly Regex HostPattern = new(@"host has\s+(?:Together(?:\+|Plus)?[\s-]+)?(?<version>[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z][0-9A-Za-z.\-]*)?)",
		RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

	/// <summary>The version inside a release tag ("v0.5.0-rc12" -> "0.5.0-rc12"), or null.</summary>
	public static string FromTag(string tag) => TryFromTag(tag, out var version) ? version : null;

	public static bool TryFromTag(string tag, out string version)
	{
		version = null;
		if (string.IsNullOrWhiteSpace(tag)) return false;
		var match = TagPattern.Match(tag);
		if (!match.Success) return false;
		version = match.Groups["version"].Value;
		return true;
	}

	/// <summary>
	/// The host's version out of a handshake rejection, or null when the reason is not a version mismatch. The host
	/// sends <c>Mod version mismatch: host has TogetherPlus-0.5.0-rc16, you have ...</c>; the old wording
	/// (<c>Together+ 0.5.0-rc16</c>) and a bare version are accepted too.
	/// </summary>
	public static string HostVersionIn(string reason)
	{
		if (string.IsNullOrEmpty(reason)) return null;
		var match = HostPattern.Match(reason);
		return match.Success ? match.Groups["version"].Value : null;
	}

	/// <summary>
	/// Orders two versions: negative when <paramref name="a"/> is older, 0 when equal, positive when newer. A full
	/// release sorts after every pre-release of the same numbers (0.5.1 &gt; 0.5.1-rc3), and RC numbers count
	/// numerically (rc9 &lt; rc10 &lt; rc12).
	/// </summary>
	public static int Compare(string a, string b)
	{
		if (string.Equals(a, b, StringComparison.Ordinal)) return 0;
		if (!TryParse(a, out var left)) return -1;
		if (!TryParse(b, out var right)) return 1;

		var result = left.Major.CompareTo(right.Major);
		if (result == 0) result = left.Minor.CompareTo(right.Minor);
		if (result == 0) result = left.Patch.CompareTo(right.Patch);
		if (result != 0) return result;

		// Same numbers: a release without a pre-release tag is the newest.
		if (left.PreTag == null && right.PreTag == null) return 0;
		if (left.PreTag == null) return 1;
		if (right.PreTag == null) return -1;

		result = string.Compare(left.PreTag, right.PreTag, StringComparison.OrdinalIgnoreCase);
		if (result == 0) result = left.PreNumber.CompareTo(right.PreNumber);
		return result;
	}

	public static bool IsNewer(string candidate, string current) => Compare(candidate, current) > 0;

	public static bool AreEqual(string a, string b) => Compare(a, b) == 0;

	private static bool TryParse(string text, out ParsedVersion version)
	{
		version = default;
		if (!TryFromTag(text, out var value)) return false;

		var split = value.Split(new[] { '-' }, 2);
		var numbers = split[0].Split('.');
		version = new ParsedVersion
		{
			Major = int.Parse(numbers[0]),
			Minor = int.Parse(numbers[1]),
			Patch = int.Parse(numbers[2])
		};
		if (split.Length == 1) return true;

		var pre = split[1];
		// "rc12": letters first, then the number. "1rc2" (rare) keeps the whole string as the tag.
		var letters = 0;
		while (letters < pre.Length && char.IsLetter(pre[letters])) letters++;
		if (letters == 0 || letters == pre.Length)
		{
			version.PreTag = pre;
			return true;
		}

		version.PreTag = pre.Substring(0, letters);
		var number = pre.Substring(letters);
		version.PreNumber = number.Length == 0 ? 0 : int.Parse(number);
		return true;
	}

	private struct ParsedVersion
	{
		public int Major;
		public int Minor;
		public int Patch;
		public string PreTag;
		public int PreNumber;
	}
}
