using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CMS21Together.Shared.Persistence;

public sealed class BundleInfo
{
	public string Path;
	public BundleMeta Meta;
	public long Length;

	public override string ToString() => $"{Meta} [{System.IO.Path.GetFileName(Path)}]";
}

/// <summary>
/// A ring of save bundles per save id, one folder each: <c>{root}\{save id}\gen-{N}-{hash}.tpb</c>. Writes are atomic,
/// identical bundles are stored once, and only the newest <see cref="Keep"/> generations stay. Thread-safe.
/// </summary>
public sealed class BundleStore
{
	public const string Extension = ".tpb";
	private readonly object sync = new();

	/// <param name="keep">Bundles kept per save id.</param>
	/// <param name="maxSaves">Save ids kept (0 = no limit); the least recently written ones go first.</param>
	public BundleStore(string root, int keep, int maxSaves = 0)
	{
		if (string.IsNullOrEmpty(root)) throw new ArgumentException("Root is empty.", nameof(root));
		if (keep < 1) throw new ArgumentOutOfRangeException(nameof(keep));
		Root = root;
		Keep = keep;
		MaxSaves = maxSaves;
	}

	public string Root { get; }
	public int Keep { get; }
	public int MaxSaves { get; }

	public string FolderOf(string saveId)
	{
		if (!SaveIds.IsValid(saveId)) throw new ArgumentException("Invalid save id.", nameof(saveId));
		return Path.Combine(Root, saveId);
	}

	/// <summary>Stores a complete, verified bundle and returns its path. A bundle already in the store is not written again.</summary>
	public string Put(byte[] bundle)
	{
		if (!SaveBundle.TryReadMeta(bundle, true, out var meta, out var error)) throw new ArgumentException(error, nameof(bundle));
		var name = $"gen-{meta.Generation:D10}-{SaveBundle.StoredHash(bundle).Substring(0, 12)}{Extension}";
		lock (sync)
		{
			var folder = FolderOf(meta.SaveId);
			var path = Path.Combine(folder, name);
			if (!File.Exists(path)) AtomicFile.WriteAllBytes(path, bundle);
			Prune(meta.SaveId, path);
			CapSaves(meta.SaveId);
			return path;
		}
	}

	/// <summary>
	/// True when exactly this bundle (generation, length and SHA-256 of all its bytes) is already stored (review
	/// fix #4). Only files of the same generation and length are hashed.
	/// </summary>
	public bool Holds(string saveId, long generation, long length, string bundleSha256)
	{
		if (string.IsNullOrEmpty(bundleSha256)) return false;
		foreach (var info in List(saveId))
		{
			if (info.Meta.Generation != generation || info.Length != length) continue;
			try
			{
				if (string.Equals(Sha256Hex.Of(File.ReadAllBytes(info.Path)), bundleSha256, StringComparison.OrdinalIgnoreCase)) return true;
			}
			catch (IOException)
			{
			}
			catch (UnauthorizedAccessException)
			{
			}
		}
		return false;
	}

	/// <summary>Bundles of one save, newest first (by generation, then time). Headers only: not verified.</summary>
	public List<BundleInfo> List(string saveId)
	{
		lock (sync)
		{
			var folder = FolderOf(saveId);
			var result = new List<BundleInfo>();
			if (!Directory.Exists(folder)) return result;
			foreach (var file in Directory.GetFiles(folder, "*" + Extension))
			{
				var info = ReadInfo(file);
				if (info != null && info.Meta.SaveId == saveId) result.Add(info);
			}
			return Sort(result);
		}
	}

	/// <summary>Bundles of every save id, newest first.</summary>
	public List<BundleInfo> ListAll()
	{
		var result = new List<BundleInfo>();
		foreach (var saveId in SaveIdsOnDisk()) result.AddRange(List(saveId));
		return Sort(result);
	}

	public IEnumerable<string> SaveIdsOnDisk()
	{
		if (!Directory.Exists(Root)) return Array.Empty<string>();
		return Directory.GetDirectories(Root).Select(Path.GetFileName).Where(SaveIds.IsValid).ToList();
	}

	/// <summary>Highest generation stored for a save (0 when none).</summary>
	public long NewestGeneration(string saveId)
	{
		var list = List(saveId);
		return list.Count == 0 ? 0 : list.Max(b => b.Meta.Generation);
	}

	/// <summary>The newest bundle that passes full verification, or null.</summary>
	public SaveBundle NewestValid(string saveId, out BundleInfo info, out byte[] bytes)
	{
		foreach (var candidate in List(saveId))
			if (TryLoad(candidate.Path, out var bundle, out bytes, out var error))
			{
				info = candidate;
				return bundle;
			}
			else
				Log.Warn($"[Saves] Skipping damaged backup {candidate.Path}: {error}");
		info = null;
		bytes = null;
		return null;
	}

	/// <summary>Moves every bundle newer than <paramref name="generation"/> into <paramref name="target"/> (after a restore).</summary>
	public int MoveNewerThan(string saveId, long generation, BundleStore target)
	{
		var moved = 0;
		foreach (var info in List(saveId).Where(b => b.Meta.Generation > generation))
		{
			try
			{
				var bytes = File.ReadAllBytes(info.Path);
				if (SaveBundle.TryReadMeta(bytes, true, out _, out _)) target.Put(bytes);
				lock (sync) File.Delete(info.Path);
				moved++;
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
			{
				Log.Warn($"[Saves] Could not move {info.Path} aside: {ex.Message}");
			}
		}
		return moved;
	}

	public static bool TryLoad(string path, out SaveBundle bundle, out byte[] bytes, out string error)
	{
		bundle = null;
		bytes = null;
		try
		{
			var length = new FileInfo(path).Length;
			if (length > SaveBundle.MaxBundleBytes)
			{
				error = "The file is too large to be a save bundle.";
				return false;
			}
			bytes = File.ReadAllBytes(path);
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
		{
			error = ex.Message;
			return false;
		}
		if (SaveBundle.TryDecode(bytes, out bundle, out error)) return true;
		bytes = null;
		return false;
	}

	private static BundleInfo ReadInfo(string file)
	{
		try
		{
			using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
			var prefix = new byte[(int)Math.Min(stream.Length, SaveBundle.HeaderPrefixBytes)];
			var count = 0;
			while (count < prefix.Length)
			{
				var read = stream.Read(prefix, count, prefix.Length - count);
				if (read == 0) break;
				count += read;
			}
			if (!SaveBundle.TryReadHeader(prefix, count, out var meta, out _)) return null;
			return new BundleInfo { Path = file, Meta = meta, Length = stream.Length };
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
		{
			return null;
		}
	}

	private static List<BundleInfo> Sort(List<BundleInfo> list) => list
		.OrderByDescending(b => b.Meta.Generation)
		.ThenByDescending(b => b.Meta.CreatedUtc)
		.ThenBy(b => b.Path, StringComparer.Ordinal)
		.ToList();

	private void Prune(string saveId, string keepPath)
	{
		var folder = FolderOf(saveId);
		foreach (var temp in Directory.GetFiles(folder, "*" + Extension + ".tmp")) TryDelete(temp);
		var all = List(saveId);
		var kept = 0;
		foreach (var info in all)
		{
			if (kept < Keep || string.Equals(info.Path, keepPath, StringComparison.OrdinalIgnoreCase))
			{
				kept++;
				continue;
			}
			TryDelete(info.Path);
		}
	}

	private void CapSaves(string current)
	{
		if (MaxSaves <= 0) return;
		var folders = SaveIdsOnDisk()
			.Where(id => id != current)
			.Select(id => (id, written: LastWrite(FolderOf(id))))
			.OrderByDescending(f => f.written)
			.ToList();
		foreach (var (id, _) in folders.Skip(Math.Max(0, MaxSaves - 1)))
		{
			try
			{
				Directory.Delete(FolderOf(id), true);
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
			{
				Log.Warn($"[Saves] Could not remove old copies of save {id}: {ex.Message}");
			}
		}
	}

	private static DateTime LastWrite(string folder)
	{
		var files = Directory.GetFiles(folder, "*" + Extension);
		return files.Length == 0 ? Directory.GetLastWriteTimeUtc(folder) : files.Max(File.GetLastWriteTimeUtc);
	}

	private static void TryDelete(string path)
	{
		try
		{
			File.Delete(path);
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
		{
			Log.Warn($"[Saves] Could not delete {path}: {ex.Message}");
		}
	}
}