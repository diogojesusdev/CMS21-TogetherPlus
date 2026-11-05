using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using CMS21Together.Shared.Updates;
using MelonLoader;

[assembly: MelonInfo(typeof(TogetherPlus.Updater.UpdaterPlugin), "TogetherPlus-Updater", "1.0.0", "TogetherPlus")]

namespace TogetherPlus.Updater;

/// <summary>
/// Updates Together+ between launches. MelonLoader cannot replace a mod while the game runs, but plugins load before
/// the mods, so this one can check GitHub, download the release, verify its SHA-256 and put the new DLL in place before
/// MelonLoader loads it: the game then starts on the new version with no extra restart.
///
/// Safety: downloads come only from this repository over HTTPS, must match the SHA-256 published next to the release,
/// and the previous files are kept. An update that fails to start is rolled back on the next launch (the mod records
/// the version it started with). The updater itself and the GC plugin are already loaded when this runs, so they are
/// only replaced by the installer.
/// </summary>
public class UpdaterPlugin : MelonPlugin
{
	private const string Repo = "diogojesusdev/CMS21-TogetherPlus";
	private const string AssetPrefix = "TogetherPlus-";
	private const string UpdaterFileName = "TogetherPlus.Updater.dll";
	private const string GcFixFileName = "TogetherPlus.GcFix.dll";
	private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(15);
	private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

	private static readonly HttpClient Http = new HttpClient { Timeout = DownloadTimeout };

	private MelonPreferences_Entry<string> channel;
	private MelonPreferences_Entry<bool> checkOnStartup;

	public override void OnEarlyInitializeMelon()
	{
		try
		{
			var category = MelonPreferences.CreateCategory("TogetherPlusUpdater", "Together+ updater");
			channel = category.CreateEntry("Channel", "prerelease", "Channel",
				"prerelease = newest build including RC builds, stable = full releases only, off = never check automatically");
			checkOnStartup = category.CreateEntry("CheckOnStartup", true, "Check for updates on startup",
				"Checks GitHub before the mod loads, so an update applies on this launch.");
		}
		catch (Exception ex)
		{
			LoggerInstance.Warning("[Update] Could not read the updater settings: " + ex.Message);
		}
	}

	public override void OnPreModsLoaded()
	{
		try
		{
			Run();
		}
		catch (Exception ex)
		{
			LoggerInstance.Warning("[Update] The update check failed: " + ex.Message);
		}
	}

	private void Run()
	{
		ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

		var gameDirectory = MelonUtils.GameDirectory ?? MelonUtils.BaseDirectory;
		var plusRoot = CMS21Together.Shared.PlusPaths.Root;
		var selection = (channel?.Value ?? "prerelease").Trim().ToLowerInvariant();

		RecoverFailedUpdate(gameDirectory, plusRoot);

		// Read after the recovery: a rollback changes the installed version.
		var installed = UpdateFiles.ReadInstalledVersion(gameDirectory);
		if (installed == null)
		{
			LoggerInstance.Msg("[Update] The installed version is unknown (the mod has not run yet); skipping the check.");
			return;
		}

		// A pin is an explicit player choice: it wins over "off", the mismatch request and the newest release, and it
		// survives until the player picks Latest in the multiplayer menu.
		var pinned = UpdateFiles.ReadPinned(plusRoot);
		if (!string.IsNullOrEmpty(pinned))
		{
			if (VersionText.AreEqual(pinned, installed))
			{
				LoggerInstance.Msg($"[Update] Together+ {installed} is pinned; staying on it.");
				return;
			}
			var pinnedRelease = FindRelease("v" + pinned);
			var pinnedVersion = pinnedRelease != null ? VersionText.FromTag(pinnedRelease.TagName) : null;
			if (pinnedRelease == null || pinnedVersion == null || !VersionText.AreEqual(pinnedVersion, pinned))
			{
				LoggerInstance.Warning($"[Update] The pinned release v{pinned} was not found; staying on {installed}. Pick another version in Multiplayer -> Version.");
				return;
			}
			if (UpdateFiles.HasFailed(plusRoot, pinnedVersion))
			{
				LoggerInstance.Warning($"[Update] Pinned Together+ {pinnedVersion} failed to start before, so it is not installed again. Pick another version in Multiplayer -> Version.");
				return;
			}
			Install(pinnedRelease, pinnedVersion, installed, true, gameDirectory, plusRoot, false);
			return;
		}

		if (selection == "off")
		{
			LoggerInstance.Msg("[Update] Automatic updates are off (TogetherPlusUpdater.Channel).");
			return;
		}

		var request = UpdateFiles.ReadRequest(plusRoot);
		if (request != null && VersionText.AreEqual(request, installed))
		{
			UpdateFiles.ClearRequest(plusRoot);
			request = null;
		}

		var release = request != null ? FindRelease("v" + request) : FindNewest(selection);
		if (release == null)
		{
			LoggerInstance.Msg(request != null
				? $"[Update] Release v{request} was not found; staying on {installed}."
				: "[Update] No Together+ release was found; staying on " + installed + ".");
			return;
		}

		var version = VersionText.FromTag(release.TagName);
		if (version == null)
		{
			LoggerInstance.Warning($"[Update] Release tag '{release.TagName}' is not a version; ignoring it.");
			return;
		}

		var forced = request != null && VersionText.AreEqual(version, request);
		if (!forced && !VersionText.IsNewer(version, installed))
		{
			if (request != null) UpdateFiles.ClearRequest(plusRoot);
			LoggerInstance.Msg($"[Update] Together+ {installed} is up to date.");
			return;
		}
		if (UpdateFiles.HasFailed(plusRoot, version))
		{
			LoggerInstance.Warning($"[Update] Together+ {version} failed to start before, so it is not installed automatically. Run the installer to try it again.");
			return;
		}

		Install(release, version, installed, forced, gameDirectory, plusRoot);
	}

	/// <summary>
	/// Puts the previous files back when the version installed last time never finished starting. A version is only
	/// blamed when another version had already started successfully on this machine: a game that quits before the mod
	/// loads (a second instance, a missing dependency) says nothing about the update, and rolling back would fight the
	/// next successful start.
	/// </summary>
	private void RecoverFailedUpdate(string gameDirectory, string plusRoot)
	{
		var pending = UpdateFiles.ReadPending(plusRoot);
		if (pending == null) return;

		if (UpdateFiles.WasStarted(plusRoot, pending))
		{
			UpdateFiles.ClearPending(plusRoot);
			LoggerInstance.Msg($"[Update] Together+ {pending} started successfully.");
			return;
		}

		if (UpdateFiles.ReadStarted(plusRoot) == null)
		{
			// No version has started here yet, so there is nothing to compare against: keep the update and let the
			// next successful start clear it. The installer repairs an installation that never starts.
			LoggerInstance.Msg($"[Update] Together+ {pending} has not started yet; keeping it until it does.");
			return;
		}

		var backup = Path.Combine(UpdateFiles.StateDirectory(plusRoot), "backup");
		var previous = UpdateFiles.ReadVersion(Path.Combine(backup, "version.txt"));
		if (previous == null)
		{
			UpdateFiles.MarkFailed(plusRoot, pending);
			UpdateFiles.ClearPending(plusRoot);
			LoggerInstance.Warning($"[Update] Together+ {pending} did not start and there is no backup. Run install.ps1 to repair the installation.");
			return;
		}

		var restored = 0;
		foreach (var relative in BackedUpFiles(includePlugins: false))
		{
			var source = Path.Combine(backup, relative);
			if (!File.Exists(source)) continue;
			try
			{
				var target = Path.Combine(gameDirectory, relative);
				Directory.CreateDirectory(Path.GetDirectoryName(target));
				File.Copy(source, target, true);
				restored++;
			}
			catch (Exception ex)
			{
				LoggerInstance.Warning($"[Update] Could not restore {relative}: {ex.Message}");
			}
		}

		UpdateFiles.WriteInstalledVersion(gameDirectory, previous);
		UpdateFiles.MarkFailed(plusRoot, pending);
		UpdateFiles.ClearPending(plusRoot);
		LoggerInstance.Msg($"[Update] Together+ {pending} did not start; rolled back to {previous} ({restored} file(s)). " +
		                   "The installer can retry that version.");
	}

	private void Install(ReleaseJson release, string version, string installed, bool forced, string gameDirectory, string plusRoot, bool clearRequest = true)
	{
		var zipName = AssetPrefix + version + ".zip";
		var asset = release.Assets?.FirstOrDefault(a => a != null && a.Name == zipName);
		var hashAsset = release.Assets?.FirstOrDefault(a => a != null && a.Name == zipName + ".sha256");
		if (asset == null)
		{
			LoggerInstance.Warning($"[Update] Release {release.TagName} has no {zipName} asset; nothing was installed.");
			return;
		}
		if (hashAsset == null)
		{
			// Without a published checksum a damaged or swapped download could not be detected.
			LoggerInstance.Warning($"[Update] Release {release.TagName} has no {zipName}.sha256; refusing to install it.");
			return;
		}

		var work = Path.Combine(Path.GetTempPath(), "TogetherPlus-update-" + Guid.NewGuid().ToString("N"));
		try
		{
			Directory.CreateDirectory(work);
			var zipPath = Path.Combine(work, zipName);
			LoggerInstance.Msg($"[Update] Downloading Together+ {version} (you have {installed})...");
			DownloadToFile(asset.Url, zipPath);

			var expected = FirstWord(DownloadText(hashAsset.Url, ApiTimeout));
			var actual = Sha256(zipPath);
			if (expected == null || !string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
			{
				LoggerInstance.Warning($"[Update] The checksum of {zipName} does not match the release ({actual} vs {expected ?? "none"}); nothing was installed.");
				return;
			}

			var package = Path.Combine(work, "package");
			ZipFile.ExtractToDirectory(zipPath, package);
			var newDll = Path.Combine(package, "Mods", UpdateFiles.ModFileName);
			if (!File.Exists(newDll))
			{
				LoggerInstance.Warning($"[Update] {zipName} is not a Together+ package ({newDll} is missing); nothing was installed.");
				return;
			}

			Backup(gameDirectory, plusRoot, installed, package);
			File.Copy(newDll, Path.Combine(gameDirectory, "Mods", UpdateFiles.ModFileName), true);
			CopyUserLibs(package, gameDirectory);
			CopyWithRetry(Path.Combine(package, "Mods", "TogetherPlus-README.md"), Path.Combine(gameDirectory, "Mods", "TogetherPlus-README.md"));
			CopyWithRetry(Path.Combine(package, "Mods", "TogetherPlus-NOTICE.md"), Path.Combine(gameDirectory, "Mods", "TogetherPlus-NOTICE.md"));

			UpdateFiles.WriteInstalledVersion(gameDirectory, version);
			UpdateFiles.WritePending(plusRoot, version);
			if (forced && clearRequest) UpdateFiles.ClearRequest(plusRoot);
			LoggerInstance.Msg($"[Update] Together+ {version} installed (was {installed}); the game starts on it now. " +
			                   $"Plugins\\{GcFixFileName} and {UpdaterFileName} are replaced by the installer when it changes.");
		}
		catch (Exception ex)
		{
			LoggerInstance.Warning($"[Update] The update to {version} failed and was not applied: {ex.Message}");
		}
		finally
		{
			try
			{
				if (Directory.Exists(work)) Directory.Delete(work, true);
			}
			catch (Exception)
			{
				// Leftover temporary files are harmless.
			}
		}
	}

	private static void Backup(string gameDirectory, string plusRoot, string installed, string package)
	{
		var backup = Path.Combine(UpdateFiles.StateDirectory(plusRoot), "backup");
		if (Directory.Exists(backup)) Directory.Delete(backup, true);

		foreach (var relative in BackedUpFiles(includePlugins: true))
		{
			var source = Path.Combine(gameDirectory, relative);
			if (!File.Exists(source)) continue;
			var target = Path.Combine(backup, relative);
			Directory.CreateDirectory(Path.GetDirectoryName(target));
			File.Copy(source, target, true);
		}

		UpdateFiles.WriteVersion(Path.Combine(backup, "version.txt"), installed);
	}

	private static IEnumerable<string> BackedUpFiles(bool includePlugins)
	{
		yield return Path.Combine("Mods", UpdateFiles.ModFileName);
		yield return Path.Combine("Mods", "TogetherPlus-README.md");
		yield return Path.Combine("Mods", "TogetherPlus-NOTICE.md");
		// Plugins are loaded before this runs, so they cannot be copied back while the game runs.
		if (includePlugins) yield return Path.Combine("Plugins", GcFixFileName);
		yield return Path.Combine("UserLibs", "Facepunch.Steamworks.Win64.dll");
		yield return Path.Combine("UserLibs", "steam_api64.dll");
	}

	private static void CopyUserLibs(string package, string gameDirectory)
	{
		var source = Path.Combine(package, "UserLibs");
		if (!Directory.Exists(source)) return;
		foreach (var file in Directory.GetFiles(source, "*.dll"))
			CopyWithRetry(file, Path.Combine(gameDirectory, "UserLibs", Path.GetFileName(file)));
	}

	/// <summary>A loaded DLL (or an antivirus scan) can briefly hold a file: retry instead of giving up.</summary>
	private static void CopyWithRetry(string source, string target)
	{
		if (!File.Exists(source)) return;
		for (var attempt = 1; ; attempt++)
		{
			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(target));
				File.Copy(source, target, true);
				return;
			}
			catch (IOException) when (attempt < 3)
			{
				Thread.Sleep(150 * attempt);
			}
			catch (UnauthorizedAccessException) when (attempt < 3)
			{
				Thread.Sleep(150 * attempt);
			}
		}
	}

	private ReleaseJson FindNewest(string selection)
	{
		var releases = Get<List<ReleaseJson>>($"https://api.github.com/repos/{Repo}/releases?per_page=20");
		if (releases == null) return null;

		ReleaseJson best = null;
		string bestVersion = null;
		foreach (var release in releases)
		{
			if (release == null || release.Draft) continue;
			if (selection != "prerelease" && release.PreRelease) continue;
			var version = VersionText.FromTag(release.TagName);
			if (version == null) continue;
			if (bestVersion == null || VersionText.IsNewer(version, bestVersion))
			{
				best = release;
				bestVersion = version;
			}
		}

		return best;
	}

	private ReleaseJson FindRelease(string tag)
	{
		try
		{
			var release = Get<ReleaseJson>($"https://api.github.com/repos/{Repo}/releases/tags/{tag}");
			return release == null || release.Draft ? null : release;
		}
		catch (Exception ex)
		{
			LoggerInstance.Warning($"[Update] Could not read release {tag}: {ex.Message}");
			return null;
		}
	}

	private T Get<T>(string url) where T : class
	{
		var json = DownloadText(url, ApiTimeout);
		if (json == null) return null;
		using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
			return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
	}

	private string DownloadText(string url, TimeSpan timeout)
	{
		using (var cts = new CancellationTokenSource(timeout))
		using (var request = NewRequest(url))
		using (var response = Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cts.Token).GetAwaiter().GetResult())
		{
			if (!response.IsSuccessStatusCode)
			{
				LoggerInstance.Warning($"[Update] GitHub answered {(int)response.StatusCode} for {url}.");
				return null;
			}

			return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
		}
	}

	private void DownloadToFile(string url, string path)
	{
		using (var cts = new CancellationTokenSource(DownloadTimeout))
		using (var request = NewRequest(url))
		using (var response = Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).GetAwaiter().GetResult())
		{
			response.EnsureSuccessStatusCode();
			using (var source = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
			using (var target = File.Create(path))
				source.CopyTo(target);
		}
	}

	private static HttpRequestMessage NewRequest(string url)
	{
		var request = new HttpRequestMessage(HttpMethod.Get, url);
		request.Headers.UserAgent.ParseAdd("TogetherPlus-Updater");
		request.Headers.Accept.ParseAdd("application/vnd.github+json");
		return request;
	}

	private static string Sha256(string path)
	{
		using (var stream = File.OpenRead(path))
		using (var sha = SHA256.Create())
			return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
	}

	/// <summary>The hash out of "&lt;sha256&gt;  TogetherPlus-x.zip" (also accepts a bare hash).</summary>
	private static string FirstWord(string text)
	{
		if (string.IsNullOrWhiteSpace(text)) return null;
		var trimmed = text.Trim();
		var end = trimmed.IndexOfAny(new[] { ' ', '\t', '\r', '\n' });
		return end < 0 ? trimmed : trimmed.Substring(0, end);
	}

#pragma warning disable 0649 // Assigned by the JSON serializer.
	[DataContract]
	private class ReleaseJson
	{
		[DataMember(Name = "tag_name")] public string TagName;
		[DataMember(Name = "prerelease")] public bool PreRelease;
		[DataMember(Name = "draft")] public bool Draft;
		[DataMember(Name = "assets")] public AssetJson[] Assets;
	}

	[DataContract]
	private class AssetJson
	{
		[DataMember(Name = "name")] public string Name;
		[DataMember(Name = "browser_download_url")] public string Url;
	}
#pragma warning restore 0649
}
