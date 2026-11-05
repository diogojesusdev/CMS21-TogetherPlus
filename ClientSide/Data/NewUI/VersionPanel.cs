using System;
using System.Collections;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using CMS21Together.ClientSide.Data.Player;
using CMS21Together.Shared;
using CMS21Together.Shared.Updates;
using MelonLoader;
using UnityEngine;
using Il2CppList = Il2CppSystem.Collections.Generic.List<string>;

namespace CMS21Together.ClientSide.Data.NewUI;

/// <summary>Multiplayer -> Version: pins a release, or returns to "latest" automatic updates.</summary>
public static class VersionPanel
{
	private static readonly List<ReleaseInfo> releases = new();
	private static bool loading;

	public static void Open()
	{
		if (loading)
		{
			Purchases.Toast("Still loading the version list...");
			return;
		}
		loading = true;
		Purchases.Toast("Loading available versions...");
		MelonCoroutines.Start(Load());
	}

	private static IEnumerator Load()
	{
		Task<List<ReleaseInfo>> task;
		try
		{
			task = Fetch();
		}
		catch (Exception ex)
		{
			loading = false;
			UICustomPanel.CreateInfoPanel("Could not load the version list: " + ex.Message);
			yield break;
		}

		for (var waited = 0f; !task.IsCompleted && waited < 10f; waited += 0.25f)
			yield return new WaitForSeconds(0.25f);
		loading = false;

		if (!task.IsCompleted)
		{
			UICustomPanel.CreateInfoPanel("The version list took too long to load. Check your internet connection and try again.");
			yield break;
		}
		if (task.IsFaulted)
		{
			UICustomPanel.CreateInfoPanel("Could not load the version list: " + (task.Exception?.GetBaseException().Message ?? "unknown error"));
			yield break;
		}
		if (task.Result == null || task.Result.Count == 0)
		{
			UICustomPanel.CreateInfoPanel("No published Together+ versions were found.");
			yield break;
		}

		releases.Clear();
		releases.AddRange(task.Result);
		Show();
	}

	private static Task<List<ReleaseInfo>> Fetch() => Task.Run(async () =>
	{
		using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
		http.DefaultRequestHeaders.UserAgent.ParseAdd("TogetherPlus-VersionUI");
		http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
		var json = await http.GetStringAsync(ReleaseCatalog.Endpoint);
		return ReleaseCatalog.Parse(json);
	});

	private static void Show()
	{
		var pin = UpdateFiles.ReadPinned(PlusPaths.Root);
		var options = new Il2CppList();
		options.Add("Latest (auto-update)");
		var selected = 0;
		var pinFound = string.IsNullOrEmpty(pin);

		foreach (var release in releases)
		{
			var label = release.Version;
			if (release.PreRelease) label += " (pre-release)";
			if (VersionText.AreEqual(release.Version, MainMod.PLUS_VERSION)) label += " (installed)";
			if (UpdateFiles.HasFailed(PlusPaths.Root, release.Version)) label += " (failed to start)";
			if (!string.IsNullOrEmpty(pin) && VersionText.AreEqual(release.Version, pin))
			{
				label += " (pinned)";
				selected = options.Count;
				pinFound = true;
			}
			options.Add(label);
		}

		if (!pinFound)
			Purchases.Toast($"Pinned version {pin} is not in the published list; pick another.");
		UICustomPanel.CreateVersionPanel(options, selected, Apply);
	}

	private static void Apply(int index)
	{
		if (index <= 0)
		{
			UpdateFiles.ClearPinned(PlusPaths.Root);
			UpdateFiles.ClearRequest(PlusPaths.Root);
			UIMenu.RefreshVersionLabel();
			var newest = releases.Count > 0 ? releases[0].Version : null;
			if (newest != null && VersionText.IsNewer(newest, MainMod.PLUS_VERSION))
				UICustomPanel.CreateChoicePanel("Restart required",
					$"Automatic updates are back. The newest release is {newest}; restart the game to update to it.",
					("OK", null));
			else
				Purchases.Toast("Automatic updates restored.");
			return;
		}

		var chosen = releases[index - 1].Version;
		UpdateFiles.WritePinned(PlusPaths.Root, chosen);
		UpdateFiles.ClearRequest(PlusPaths.Root);
		UIMenu.RefreshVersionLabel();

		if (VersionText.AreEqual(chosen, MainMod.PLUS_VERSION))
		{
			Purchases.Toast($"Pinned to {chosen} (already installed).");
			return;
		}
		UICustomPanel.CreateChoicePanel("Restart required",
			$"Together+ will switch to {chosen} on the next launch. Restart the game to apply it.",
			("OK", null),
			("Undo pin", () =>
			{
				UpdateFiles.ClearPinned(PlusPaths.Root);
				UIMenu.RefreshVersionLabel();
				Purchases.Toast("Pin cleared.");
			}));
	}
}