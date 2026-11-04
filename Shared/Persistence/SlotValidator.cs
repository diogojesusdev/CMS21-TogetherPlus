using System;

namespace CMS21Together.Shared.Persistence;

public enum SlotState
{
	/// <summary>Never played (or never saved): nothing to check.</summary>
	New,

	/// <summary>The profile matches the hash stored in the sidecar.</summary>
	Ok,

	/// <summary>No hash to compare with (the last save did not finish its second step) but the profile reads fine.</summary>
	Unverified,

	Missing,
	Corrupt,

	/// <summary>A backup is newer than these files (they were replaced by an older copy).</summary>
	Older,

	/// <summary>Written by a newer Together+: opened read-only, never overwritten.</summary>
	NewerSchema
}

/// <summary>What is known about a slot's files when the host picks it.</summary>
public sealed class SlotInput
{
	public bool AlreadyLoaded;
	public int Schema;
	public long Generation;
	public string ProfileSha256;

	/// <summary>Profile file contents; null when the file does not exist.</summary>
	public byte[] Profile;

	/// <summary>Whether the game can read a profile (the real parser in game; a format check in tests).</summary>
	public Func<byte[], bool> Readable;

	/// <summary>Newest backup of this save, or null.</summary>
	public BundleMeta NewestBackup;
}

public readonly struct SlotCheck
{
	public SlotCheck(SlotState state, string profileSha256, BundleMeta backup)
	{
		State = state;
		ProfileSha256 = profileSha256;
		Backup = backup;
	}

	public SlotState State { get; }

	/// <summary>Hash of the profile on disk (null when missing).</summary>
	public string ProfileSha256 { get; }

	/// <summary>Backup the player can restore, or null.</summary>
	public BundleMeta Backup { get; }

	/// <summary>Safe to host without asking.</summary>
	public bool CanHost => State == SlotState.New || State == SlotState.Ok || State == SlotState.Unverified;

	/// <summary>The sidecar should get the profile's hash now (it had none, or vanilla saved after the mod).</summary>
	public bool NeedsStamp => State == SlotState.Unverified;

	public string Describe() => State switch
	{
		SlotState.New => "New save.",
		SlotState.Ok => "The save is intact.",
		SlotState.Unverified => "The save could not be verified, but it reads fine.",
		SlotState.Missing => "The game save file of this slot is missing.",
		SlotState.Corrupt => "The game save file of this slot is damaged.",
		SlotState.Older => "These files are older than your newest backup.",
		SlotState.NewerSchema => "This save was written by a newer version of Together+. Update the mod to play it.",
		_ => State.ToString()
	};
}

/// <summary>Decides, before a session loads, whether a slot's files can be trusted (plan §3.5, load validation).</summary>
public static class SlotValidator
{
	/// <summary>Sidecar schema written by this version. 1 = Together hf3 (no generation or hash).</summary>
	public const int CurrentSchema = 2;

	public static SlotCheck Check(SlotInput input)
	{
		if (input == null) throw new ArgumentNullException(nameof(input));
		var schema = input.Schema <= 0 ? 1 : input.Schema;
		var backup = input.NewestBackup;
		if (schema > CurrentSchema) return new SlotCheck(SlotState.NewerSchema, null, backup);
		if (!input.AlreadyLoaded) return new SlotCheck(SlotState.New, null, backup);

		if (input.Profile == null || input.Profile.Length == 0)
		{
			// Started but never saved by Together+: there was nothing to lose.
			if (schema >= 2 && input.Generation == 0 && backup == null) return new SlotCheck(SlotState.New, null, null);
			return new SlotCheck(SlotState.Missing, null, backup);
		}

		var sha = Sha256Hex.Of(input.Profile);
		var newerBackup = backup != null && backup.Generation > input.Generation;
		if (sha == input.ProfileSha256) return new SlotCheck(newerBackup ? SlotState.Older : SlotState.Ok, sha, backup);

		var readable = input.Readable == null || SafeReadable(input.Readable, input.Profile);
		if (!readable) return new SlotCheck(SlotState.Corrupt, sha, backup);
		if (newerBackup) return new SlotCheck(SlotState.Older, sha, backup);
		// Either the second save step never ran, or vanilla saved after the mod's hook (exit, quit): the file reads fine.
		return new SlotCheck(SlotState.Unverified, sha, backup);
	}

	private static bool SafeReadable(Func<byte[], bool> readable, byte[] profile)
	{
		try
		{
			return readable(profile);
		}
		catch (Exception ex)
		{
			Log.Warn($"[Saves] Reading the profile failed: {ex.Message}");
			return false;
		}
	}
}