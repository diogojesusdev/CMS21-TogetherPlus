using System;
using System.Collections.Generic;
using System.IO;

namespace CMS21Together.Shared.Updates;

/// <summary>
/// The files the mod and the updater share. The installed version sits next to the mod's DLL (so the updater knows what
/// is installed without loading the DLL it is about to replace), and the update state lives under
/// <see cref="PlusPaths.Root"/> so it survives reinstalling or verifying the game.
/// </summary>
public static class UpdateFiles
{
	public const string ModFileName = "CMS21-Together.dll";
	public const string VersionFileName = "CMS21-Together.version";

	public static string ModsDirectory(string gameDirectory) => Path.Combine(gameDirectory, "Mods");

	public static string InstalledVersionPath(string gameDirectory) =>
		Path.Combine(ModsDirectory(gameDirectory), VersionFileName);

	public static string StateDirectory(string plusRoot) => Path.Combine(plusRoot, "Updates");

	/// <summary>Version the updater installed but the game has not started successfully with (yet).</summary>
	public static string PendingPath(string plusRoot) => Path.Combine(StateDirectory(plusRoot), "pending.txt");

	/// <summary>Version the player must switch to (the host runs it): the updater installs exactly this one next time.</summary>
	public static string RequestPath(string plusRoot) => Path.Combine(StateDirectory(plusRoot), "request.txt");

	/// <summary>Version the mod last started with. Written after a full, successful mod init.</summary>
	public static string StartedPath(string plusRoot) => Path.Combine(StateDirectory(plusRoot), "started.txt");

	/// <summary>Versions that were installed and then failed to start, one per line (never retried automatically).</summary>
	public static string FailedPath(string plusRoot) => Path.Combine(StateDirectory(plusRoot), "failed.txt");

	public static string ReadInstalledVersion(string gameDirectory) => ReadVersion(InstalledVersionPath(gameDirectory));

	public static void WriteInstalledVersion(string gameDirectory, string version) =>
		WriteVersion(InstalledVersionPath(gameDirectory), version);

	public static string ReadPending(string plusRoot) => ReadVersion(PendingPath(plusRoot));

	public static void WritePending(string plusRoot, string version) => WriteVersion(PendingPath(plusRoot), version);

	public static void ClearPending(string plusRoot) => Delete(PendingPath(plusRoot));

	public static string ReadRequest(string plusRoot) => ReadVersion(RequestPath(plusRoot));

	public static void WriteRequest(string plusRoot, string version) => WriteVersion(RequestPath(plusRoot), version);

	public static void ClearRequest(string plusRoot) => Delete(RequestPath(plusRoot));

	/// <summary>Version the player pinned in the multiplayer menu; overrides requests and "newest" until cleared.</summary>
	public static string PinnedPath(string plusRoot) => Path.Combine(StateDirectory(plusRoot), "pinned.txt");

	public static string ReadPinned(string plusRoot) => ReadVersion(PinnedPath(plusRoot));

	public static void WritePinned(string plusRoot, string version) => WriteVersion(PinnedPath(plusRoot), version);

	public static void ClearPinned(string plusRoot) => Delete(PinnedPath(plusRoot));

	public static string ReadStarted(string plusRoot) => ReadVersion(StartedPath(plusRoot));

	public static void WriteStarted(string plusRoot, string version) => WriteVersion(StartedPath(plusRoot), version);

	public static bool WasStarted(string plusRoot, string version)
	{
		var started = ReadStarted(plusRoot);
		return started != null && VersionText.AreEqual(started, version);
	}

	public static IReadOnlyList<string> ReadFailed(string plusRoot)
	{
		var result = new List<string>();
		try
		{
			if (!File.Exists(FailedPath(plusRoot))) return result;
			foreach (var line in File.ReadAllLines(FailedPath(plusRoot)))
			{
				var version = VersionText.FromTag(line);
				if (version != null && !result.Contains(version)) result.Add(version);
			}
		}
		catch (Exception)
		{
			// A missing or unreadable list only means "nothing is known to have failed".
		}

		return result;
	}

	public static void MarkFailed(string plusRoot, string version)
	{
		if (string.IsNullOrEmpty(version)) return;
		var failed = new List<string>(ReadFailed(plusRoot));
		if (!failed.Contains(version)) failed.Add(version);
		try
		{
			Directory.CreateDirectory(StateDirectory(plusRoot));
			File.WriteAllLines(FailedPath(plusRoot), failed);
		}
		catch (Exception)
		{
			// Losing this list only risks one more retry of a broken version.
		}
	}

	public static bool HasFailed(string plusRoot, string version)
	{
		foreach (var failed in ReadFailed(plusRoot))
			if (VersionText.AreEqual(failed, version)) return true;
		return false;
	}

	/// <summary>A version that started successfully is not a failed one any more (it may have failed once for an
	/// unrelated reason: a second game instance, a missing file).</summary>
	public static void ClearFailed(string plusRoot, string version)
	{
		if (string.IsNullOrEmpty(version)) return;
		var failed = new List<string>(ReadFailed(plusRoot));
		if (failed.RemoveAll(entry => VersionText.AreEqual(entry, version)) == 0) return;
		try
		{
			Directory.CreateDirectory(StateDirectory(plusRoot));
			File.WriteAllLines(FailedPath(plusRoot), failed);
		}
		catch (Exception)
		{
			// The list is advisory: keeping a stale entry only means the update is offered again.
		}
	}

	/// <summary>Reads a version file; null when it is absent or does not hold a version.</summary>
	public static string ReadVersion(string path)
	{
		try
		{
			if (!File.Exists(path)) return null;
			return VersionText.FromTag(File.ReadAllText(path));
		}
		catch (Exception)
		{
			return null;
		}
	}

	public static void WriteVersion(string path, string version)
	{
		if (string.IsNullOrEmpty(version)) throw new ArgumentNullException(nameof(version));
		Directory.CreateDirectory(Path.GetDirectoryName(path));
		// A marker is deleted and recreated by the writer only; a torn write must never look like a valid version.
		File.WriteAllText(path, version + Environment.NewLine);
	}

	private static void Delete(string path)
	{
		try
		{
			if (File.Exists(path)) File.Delete(path);
		}
		catch (Exception)
		{
			// A marker that survives is handled by its reader (it will be seen as failed, or cleared next time).
		}
	}
}
