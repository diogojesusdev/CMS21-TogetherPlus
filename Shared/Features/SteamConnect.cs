using System;

namespace CMS21Together.Shared.Features;

/// <summary>The Steam "connect" string of a Together+ session (Steam invites, Phase 6b).</summary>
public static class SteamConnect
{
	public const string Prefix = "CMS21Together:";

	public static string Of(string sessionCode) => "+connect " + Prefix + sessionCode;

	/// <summary>The session code in a connect string or command line, or null.</summary>
	public static string Parse(string connect)
	{
		if (string.IsNullOrEmpty(connect)) return null;
		var at = connect.IndexOf(Prefix, StringComparison.Ordinal);
		if (at < 0) return null;
		var code = connect.Substring(at + Prefix.Length).Trim();
		var end = code.IndexOfAny(new[] { ' ', '"', '\t' });
		if (end >= 0) code = code.Substring(0, end);
		return code.Length is > 0 and <= 64 ? code : null;
	}
}