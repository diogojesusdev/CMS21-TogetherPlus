using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CMS21Together.Shared.Safety;

/// <summary>
/// Turns a failed mod action into a diagnosable event: a report file, a clipboard-friendly text and a breadcrumb of
/// what was happening. Native crashes cannot be caught in-process; this covers the managed failures and the
/// invalid-state paths that precede most of the mod's crashes.
/// </summary>
public static class CrashReport
{
	private const int MaxNotes = 40;
	private static readonly List<string> notes = new();
	private static readonly object gate = new();

	/// <summary>Tests point this at a temp folder; the mod leaves it null (TogetherPlus/diagnostics).</summary>
	public static string DirectoryOverride;

	/// <summary>Set by MainMod and CrashUI; off in headless tests unless they opt in.</summary>
	public static bool Enabled;

	public static string Version = "unknown";
	public static Func<string> Context;
	public static string LastReport { get; private set; }
	public static bool PreviousSessionCrashed { get; private set; }

	/// <summary>Raised with the report text after a capture (the client copies it and shows a warning).</summary>
	public static event Action<string> Captured;

	public static string DirectoryPath => DirectoryOverride ?? Path.Combine(PlusPaths.Root, "diagnostics");
	private static string Marker => Path.Combine(DirectoryPath, "session.running");

	/// <summary>Records what was being attempted; the report lists the last few.</summary>
	public static void Note(string action)
	{
		if (string.IsNullOrEmpty(action)) return;
		lock (gate)
		{
			notes.Add($"{DateTime.UtcNow:HH:mm:ss.fff} {action}");
			if (notes.Count > MaxNotes) notes.RemoveAt(0);
		}
	}

	/// <summary>Call once at startup, before anything else writes the marker.</summary>
	public static void BeginSession()
	{
		try
		{
			PreviousSessionCrashed = File.Exists(Marker);
			System.IO.Directory.CreateDirectory(DirectoryPath);
			File.WriteAllText(Marker, DateTime.UtcNow.ToString("o"));
		}
		catch (Exception ex)
		{
			Log.Warn($"[Guard] Could not write the session marker: {ex.Message}");
		}
	}

	/// <summary>Call on a clean shutdown: an existing marker at the next start means the game crashed.</summary>
	public static void EndSession()
	{
		try
		{
			if (File.Exists(Marker)) File.Delete(Marker);
		}
		catch (Exception ex)
		{
			Log.Warn($"[Guard] Could not clear the session marker: {ex.Message}");
		}
	}

	public static string LatestReportPath
	{
		get
		{
			try
			{
				if (!System.IO.Directory.Exists(DirectoryPath)) return null;
				return System.IO.Directory.GetFiles(DirectoryPath, "crash-*.txt")
					.OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
			}
			catch
			{
				return null;
			}
		}
	}

	public static string LatestReport
	{
		get
		{
			var path = LatestReportPath;
			if (path == null) return null;
			try { return File.ReadAllText(path); }
			catch { return null; }
		}
	}

	/// <summary>Builds, stores and raises the report for one failed action.</summary>
	public static string Capture(string action, Exception exception, string context = null)
	{
		var text = Build(action, exception, context);
		LastReport = text;
		var path = Enabled ? Write(text, action) : null;
		Log.Error($"[Guard] {action} failed; the action was cancelled. {(exception != null ? exception.Message : "no exception")}" +
		          (path != null ? $" (report: {path})" : ""));
		if (Enabled)
		{
			try { Captured?.Invoke(text); }
			catch (Exception ex) { Log.Warn($"[Guard] The error handler failed: {ex.Message}"); }
		}
		return text;
	}

	public static string Build(string action, Exception exception, string context = null)
	{
		var builder = new System.Text.StringBuilder();
		builder.AppendLine("Together+ error report");
		builder.AppendLine("Version: " + Version);
		builder.AppendLine("Time (UTC): " + DateTime.UtcNow.ToString("o"));
		try
		{
			var extra = context ?? Context?.Invoke();
			if (!string.IsNullOrEmpty(extra)) builder.AppendLine("Context: " + extra);
		}
		catch
		{
			// The context is best effort.
		}
		builder.AppendLine("Action: " + action);
		builder.AppendLine();
		builder.AppendLine(exception != null ? exception.ToString() : "(no exception)");
		builder.AppendLine();
		builder.AppendLine("Last actions:");
		lock (gate)
			foreach (var note in notes)
				builder.AppendLine("  " + note);
		builder.AppendLine();
		builder.AppendLine("Log: MelonLoader\\Logs\\Latest.log");
		return builder.ToString();
	}

	private static string Write(string text, string action)
	{
		try
		{
			System.IO.Directory.CreateDirectory(DirectoryPath);
			var name = "crash-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Sanitize(action) + ".txt";
			var path = Path.Combine(DirectoryPath, name);
			File.WriteAllText(path, text);
			return path;
		}
		catch (Exception ex)
		{
			Log.Warn($"[Guard] Could not write the report: {ex.Message}");
			return null;
		}
	}

	private static string Sanitize(string action)
	{
		if (string.IsNullOrEmpty(action)) return "error";
		var chars = action.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
		var name = new string(chars).Trim('-');
		return name.Length > 40 ? name.Substring(0, 40) : name.Length == 0 ? "error" : name;
	}
}