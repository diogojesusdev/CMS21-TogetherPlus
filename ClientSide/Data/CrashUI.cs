using System;
using CMS21Together.ClientSide.Data.NewUI;
using CMS21Together.ClientSide.Data.Player;
using CMS21Together.Shared;
using CMS21Together.Shared.Safety;
using UnityEngine;
using CrashReport = CMS21Together.Shared.Safety.CrashReport;

namespace CMS21Together.ClientSide.Data;

/// <summary>Shows a failed mod action to the player: copyable report, a short warning, and the crash-session notice.</summary>
public static class CrashUI
{
	private static bool initialized;
	private static bool previousShown;
	private static float lastShown = -100f;

	public static void Initialize()
	{
		if (initialized) return;
		initialized = true;
		CrashReport.Enabled = true;
		CrashReport.Version = MainMod.PLUS_VERSION;
		CrashReport.Context = () => SceneManager.CurrentScene().ToString();
		CrashReport.Captured += OnCaptured;
	}

	private static void OnCaptured(string text)
	{
		try
		{
			GUIUtility.systemCopyBuffer = text;
		}
		catch (Exception ex)
		{
			Log.Warn($"[Guard] Could not copy the report to the clipboard: {ex.Message}");
		}
		if (Time.unscaledTime - lastShown < 5f) return;
		lastShown = Time.unscaledTime;
		try
		{
			UICustomPanel.CreateInfoPanel(
				"Together+ stopped an action before it could crash the game. The details were copied to the clipboard " +
				"and saved to TogetherPlus\\diagnostics.");
		}
		catch (Exception ex)
		{
			Log.Warn($"[Guard] Could not show the error panel: {ex.Message}");
		}
	}

	/// <summary>Ctrl+Shift+E: copy the last report (e.g. for a bug report).</summary>
	public static void CopyLast()
	{
		if (string.IsNullOrEmpty(CrashReport.LastReport))
		{
			Purchases.Toast("No error has been recorded yet.");
			return;
		}
		try
		{
			GUIUtility.systemCopyBuffer = CrashReport.LastReport;
			Purchases.Toast("Last error copied to the clipboard.");
		}
		catch (Exception ex)
		{
			Log.Warn($"[Guard] Could not copy the last error: {ex.Message}");
		}
	}

	/// <summary>First menu after an unclean shutdown: tell the player and make the report easy to send.</summary>
	public static void ShowPreviousSessionNotice()
	{
		if (previousShown || !CrashReport.PreviousSessionCrashed) return;
		previousShown = true;
		var report = CrashReport.LatestReport;
		if (!string.IsNullOrEmpty(report))
		{
			try { GUIUtility.systemCopyBuffer = report; }
			catch { }
		}
		var path = CrashReport.LatestReportPath;
		var message = "The previous session ended unexpectedly.";
		message += string.IsNullOrEmpty(path)
			? " No report file was found; check MelonLoader\\Logs\\Latest.log."
			: " The last report was copied to the clipboard and saved to " + path + ".";
		try
		{
			UICustomPanel.CreateInfoPanel(message);
		}
		catch (Exception ex)
		{
			Log.Warn($"[Guard] Could not show the crash notice: {ex.Message}");
		}
	}
}