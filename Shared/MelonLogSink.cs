using MelonLoader;

namespace CMS21Together.Shared;

/// <summary>Routes <see cref="Log"/> to the MelonLoader console and Latest.log.</summary>
public sealed class MelonLogSink : ILogSink
{
	private const string Category = "TogetherPlus";
	private const string LevelEntry = "LogLevel";

	private static MelonPreferences_Entry<string> levelPreference;

	public void Write(LogLevel level, string message)
	{
		switch (level)
		{
			case LogLevel.Error:
				MelonLogger.Error(message);
				break;
			case LogLevel.Warn:
				MelonLogger.Warning(message);
				break;
			case LogLevel.Info:
				MelonLogger.Msg(message);
				break;
			default:
				MelonLogger.Msg($"[{level}] {message}");
				break;
		}
	}

	/// <summary>Installs the sink and reads the level from UserData\MelonPreferences.cfg.</summary>
	public static void Initialize()
	{
		Log.Sink = new MelonLogSink();

		var category = MelonPreferences.CreateCategory(Category, "Together+");
		levelPreference = category.CreateEntry(LevelEntry, LogLevel.Info.ToString(), "Log level",
			"Trace, Debug, Info, Warn, Error or Off. Debug and Trace are verbose (per-packet logs).",
			false, false, null);
		ApplyLevel();
	}

	public static void ApplyLevel()
	{
		if (levelPreference == null) return;

		if (!Log.TryParseLevel(levelPreference.Value, out var level))
			MelonLogger.Warning($"[Log] Unknown {Category}.{LevelEntry} '{levelPreference.Value}', using {level}.");
		Log.MinLevel = level;
	}
}
