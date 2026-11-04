using System;
using System.IO;

namespace CMS21Together.Shared.Persistence;

/// <summary>Puts a save bundle back on disk. Nothing is overwritten without a copy of it being kept first.</summary>
public static class SaveRestore
{
	/// <summary>
	/// Keeps the current files in <paramref name="safety"/> (note "before restore"), then writes the bundle's profile
	/// and sidecar atomically. <paramref name="sidecar"/> replaces the bundle's sidecar (e.g. for another slot);
	/// <paramref name="currentProfilePath"/> is the profile in use now when it has another name (other format).
	/// </summary>
	public static void Apply(SaveBundle bundle, string profilePath, string sidecarPath, BundleStore safety,
		long currentGeneration, byte[] sidecar = null, string currentProfilePath = null)
	{
		if (bundle == null) throw new ArgumentNullException(nameof(bundle));
		var kept = currentProfilePath ?? profilePath;
		KeepCopy(bundle.Meta.SaveId, bundle.Meta.Name, kept, sidecarPath, safety, currentGeneration, "before restore", FormatOf(kept));
		AtomicFile.WriteAllBytes(profilePath, bundle.Profile);
		AtomicFile.WriteAllBytes(sidecarPath, sidecar ?? bundle.Sidecar);
		Log.Info($"[Saves] Restored {bundle.Meta}.");
	}

	/// <summary>Stores the files as they are now (missing ones as empty). Returns the copy's path, or null when there was nothing.</summary>
	/// <summary>The game's profile format of a file: <c>.cms21</c> = 0 (legacy), <c>.cms21b</c> = 1.</summary>
	public static byte FormatOf(string profilePath) =>
		profilePath != null && profilePath.EndsWith(".cms21", StringComparison.OrdinalIgnoreCase) ? (byte)0 : (byte)1;

	public static string KeepCopy(string saveId, string name, string profilePath, string sidecarPath, BundleStore safety,
		long generation, string note, byte profileFormat = 1)
	{
		if (safety == null || !SaveIds.IsValid(saveId)) return null;
		var profile = File.Exists(profilePath) ? File.ReadAllBytes(profilePath) : null;
		var sidecar = File.Exists(sidecarPath) ? File.ReadAllBytes(sidecarPath) : null;
		if (profile == null && sidecar == null) return null;
		if ((profile?.Length ?? 0) > SaveBundle.MaxProfileBytes || (sidecar?.Length ?? 0) > SaveBundle.MaxSidecarBytes)
			throw new IOException("The current files are too large to keep a copy; not overwriting them.");
		name ??= string.Empty;
		var meta = new BundleMeta
		{
			SaveId = saveId,
			Generation = Math.Max(0, generation),
			CreatedUtc = DateTime.UtcNow,
			Name = name.Length > SaveBundle.MaxTextLength ? name.Substring(0, SaveBundle.MaxTextLength) : name,
			ProfileFormat = profileFormat,
			Schema = SlotValidator.CurrentSchema,
			Note = note
		};
		return safety.Put(SaveBundle.Encode(meta, profile, sidecar));
	}
}