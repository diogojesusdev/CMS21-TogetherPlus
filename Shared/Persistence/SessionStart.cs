using System;

namespace CMS21Together.Shared.Persistence;

/// <summary>
/// Before a save is played, its files as they are now get a stamp and a backup when they lack one: after a crash the
/// newest complete generation may exist only on disk, and the session's first save rewrites the profile in place.
/// </summary>
public static class SessionStart
{
	/// <summary>Returns the generation the files are now known as (0 when there is nothing to back up).</summary>
	public static long Prepare(ISaveSlot slot, SlotCheck check, BundleStore backups, string name, string note = "before session")
	{
		if (slot == null || backups == null || check.ProfileSha256 == null || !SaveIds.IsValid(slot.SaveId)) return 0;
		if (check.State != SlotState.Ok && check.State != SlotState.Unverified) return 0;

		var generation = slot.Generation > 0 ? slot.Generation : backups.NewestGeneration(slot.SaveId) + 1;
		byte[] sidecar = null;
		if (check.State == SlotState.Unverified || slot.Generation <= 0)
		{
			sidecar = slot.WriteSidecar(generation, check.ProfileSha256);
			if (sidecar == null) return 0;
		}

		foreach (var info in backups.List(slot.SaveId))
			if (info.Meta.Generation == generation && info.Meta.ProfileSha256 == check.ProfileSha256)
				return generation;

		var profile = slot.ReadProfile(out var format);
		if (profile == null || Sha256Hex.Of(profile) != check.ProfileSha256)
		{
			Log.Warn("[Saves] The profile changed while it was being checked; no backup was taken.");
			return generation;
		}
		sidecar ??= slot.WriteSidecar(generation, check.ProfileSha256);
		if (sidecar == null) return generation;

		name ??= string.Empty;
		var meta = new BundleMeta
		{
			SaveId = slot.SaveId,
			Generation = generation,
			CreatedUtc = DateTime.UtcNow,
			Name = name.Length > SaveBundle.MaxTextLength ? name.Substring(0, SaveBundle.MaxTextLength) : name,
			ProfileFormat = format,
			Schema = SlotValidator.CurrentSchema,
			Note = note
		};
		backups.Put(SaveBundle.Encode(meta, profile, sidecar));
		Log.Info($"[Saves] Backed up generation {generation} {note}.");
		return generation;
	}
}