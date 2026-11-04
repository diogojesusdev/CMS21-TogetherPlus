using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace CMS21Together.Shared.Net;

/// <summary>
/// Host settings in <c>UserData/TogetherPlus.json</c> (Phase 7, plan §3.6). Written with the defaults when missing;
/// a damaged or out-of-range value falls back to the default (logged), so a bad file never stops a session.
/// </summary>
public sealed class ServerConfig
{
	public const string FileName = "TogetherPlus.json";
	public const int DefaultMaxPlayers = 8;

	/// <summary>The lobby, the item UID ranges and the bandwidth estimate are sized for this many players.</summary>
	public const int MaxSupportedPlayers = 8;

	private static readonly Regex MaxPlayersField = new("\"MaxPlayers\"\\s*:\\s*(-?\\d+)", RegexOptions.CultureInvariant);

	public int MaxPlayers { get; private set; } = DefaultMaxPlayers;

	public static ServerConfig Parse(string json)
	{
		var config = new ServerConfig();
		if (string.IsNullOrEmpty(json)) return config;
		var match = MaxPlayersField.Match(json);
		if (!match.Success) return config;
		if (int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 2 && value <= MaxSupportedPlayers)
			config.MaxPlayers = value;
		else
			Log.Warn($"[Config] MaxPlayers {match.Groups[1].Value} is not between 2 and {MaxSupportedPlayers}; using {DefaultMaxPlayers}.");
		return config;
	}

	public string ToJson() => "{" + Environment.NewLine + $"  \"MaxPlayers\": {MaxPlayers.ToString(CultureInfo.InvariantCulture)}" + Environment.NewLine + "}" + Environment.NewLine;

	/// <summary>Reads the file in <paramref name="directory"/>, creating it with the defaults when it does not exist.</summary>
	public static ServerConfig LoadOrCreate(string directory)
	{
		try
		{
			var path = Path.Combine(directory, FileName);
			if (File.Exists(path)) return Parse(File.ReadAllText(path));
			var config = new ServerConfig();
			Directory.CreateDirectory(directory);
			File.WriteAllText(path, config.ToJson());
			return config;
		}
		catch (Exception ex)
		{
			Log.Warn($"[Config] Could not read {FileName}: {ex.Message}; using the defaults.");
			return new ServerConfig();
		}
	}
}

/// <summary>Who needs a player's movement (Phase 7 interest management).</summary>
public static class Interest
{
	/// <summary>Players in different scenes never see each other; an unknown scene still gets it (safe default).</summary>
	public static bool SameScene<TScene>(TScene mover, TScene viewer) where TScene : struct, Enum =>
		Convert.ToInt32(mover) == 0 || Convert.ToInt32(viewer) == 0 || mover.Equals(viewer);
}

/// <summary>Coalesce keys for the send queue: one pending movement frame per player and kind (Phase 7).</summary>
public static class CoalesceKeys
{
	/// <summary>Never 0. <paramref name="player"/> is the player the frame is about (0 for one's own on a player's link).</summary>
	public static long Movement(int player, int packetType) => ((long)(player + 1) << 32) | (uint)packetType;
}