using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using CMS21Together.Shared;
using CMS21Together.Shared.Persistence;

namespace CMS21Together.BotHarness;

/// <summary>
/// Phase 4 exit test: a writer process saves in a loop exactly like the game does (mod hook, then a non-atomic profile
/// write, then the commit's second step) and gets killed at random moments. After every kill the files must load as
/// a valid generation, directly or by restoring the newest backup, and never older than the newest backup.
/// </summary>
public static class SaveKill
{
	public const string SaveId = "5a7e5a7e5a7e5a7e5a7e5a7e5a7e5a7e";

	public sealed class Result
	{
		public readonly List<string> Errors = new();
		public readonly Dictionary<SlotState, int> States = new();
		public int Kills;
		public int Restores;
		public long LastGeneration;
		public bool Passed => Errors.Count == 0;

		public override string ToString() =>
			$"{(Passed ? "PASSED" : "FAILED")}: {Kills} kills, {Restores} restores, reached generation {LastGeneration}; states: " +
			string.Join(", ", States.OrderBy(s => s.Key).Select(s => $"{s.Key} {s.Value}")) +
			(Passed ? "" : Environment.NewLine + string.Join(Environment.NewLine, Errors.Take(20)));
	}

	/// <summary>The writer process: saves forever.</summary>
	public static int RunWriter(string dir)
	{
		var clock = Stopwatch.StartNew();
		var committer = new SaveCommitter(new BundleStore(Path.Combine(dir, "Backups"), 10), () => clock.ElapsedMilliseconds, inline: true);
		var slot = DiskSlot.Open(dir);
		var random = new Random(Environment.TickCount ^ Process.GetCurrentProcess().Id);
		while (true)
			if (!SaveOnce(slot, committer, random))
				return 3;
	}

	/// <summary>One save as the game does it. False when the commit could not start.</summary>
	public static bool SaveOnce(DiskSlot slot, SaveCommitter committer, Random random)
	{
		slot.Progress = "xp=" + (slot.Generation + 1) * 10;
		var generation = committer.Begin(slot);
		if (generation == 0) return false;
		var bytes = Profile(generation, random.Next(20_000, 200_000), random);
		using (var stream = new FileStream(slot.ProfilePath, FileMode.Create, FileAccess.Write, FileShare.Read))
			for (var offset = 0; offset < bytes.Length; offset += 4096)
			{
				stream.Write(bytes, offset, Math.Min(4096, bytes.Length - offset));
				stream.Flush(true);
				committer.Pump(true);
			}
		while (!committer.Idle)
		{
			Thread.Sleep(5);
			committer.Pump(false);
		}
		return true;
	}

	/// <summary>Kills the writer <paramref name="kills"/> times and checks the files after each kill.</summary>
	public static Result Run(string harnessExe, string dir, int kills, int seed, Action<string> progress = null)
	{
		var result = new Result();
		if (Directory.Exists(dir)) Directory.Delete(dir, true);
		Directory.CreateDirectory(dir);

		// A first complete save, so there is always something to keep.
		var warmup = new SaveCommitter(new BundleStore(Path.Combine(dir, "Backups"), 10), () => Environment.TickCount, inline: true);
		SaveOnce(DiskSlot.Open(dir), warmup, new Random(seed));

		var random = new Random(seed);
		for (var i = 1; i <= kills; i++)
		{
			using (var process = Process.Start(new ProcessStartInfo(harnessExe, $"--save-writer \"{dir}\"")
			       {
				       UseShellExecute = false,
				       CreateNoWindow = true,
				       RedirectStandardError = true
			       }))
			{
				Thread.Sleep(random.Next(30, 1800));
				if (process.HasExited)
				{
					result.Errors.Add($"Kill {i}: the writer stopped by itself (exit code {process.ExitCode}): {process.StandardError.ReadToEnd()}");
					break;
				}
				process.Kill();
				process.WaitForExit();
			}
			result.Kills++;
			Check(dir, i, result);
			if (i % 25 == 0) progress?.Invoke($"{i} kills: generation {result.LastGeneration}, {result.Restores} restores, {result.Errors.Count} errors");
		}
		return result;
	}

	private static void Check(string dir, int kill, Result result)
	{
		var backups = new BundleStore(Path.Combine(dir, "Backups"), 10);
		var slot = DiskSlot.Open(dir);
		var newest = backups.NewestValid(SaveId, out _, out _);
		var input = slot.Input(backups);
		var check = SlotValidator.Check(input);
		result.States[check.State] = result.States.TryGetValue(check.State, out var n) ? n + 1 : 1;

		switch (check.State)
		{
			case SlotState.Ok:
			case SlotState.Unverified:
				// What the game does when the save is hosted again.
				SessionStart.Prepare(slot, check, backups, slot.Name);
				break;
			case SlotState.Missing:
			case SlotState.Corrupt:
			case SlotState.Older:
				if (newest == null)
				{
					result.Errors.Add($"Kill {kill}: {check.State} and no valid backup to restore.");
					return;
				}
				SaveRestore.Apply(newest, slot.ProfilePath, slot.SidecarPath, new BundleStore(Path.Combine(dir, "Safety"), 5), slot.Generation);
				result.Restores++;
				slot = DiskSlot.Open(dir);
				var again = SlotValidator.Check(slot.Input(backups)).State;
				if (again != SlotState.Ok)
				{
					result.Errors.Add($"Kill {kill}: after restoring generation {newest.Meta.Generation} the slot is {again}.");
					return;
				}
				break;
			default:
				result.Errors.Add($"Kill {kill}: unexpected state {check.State}.");
				return;
		}

		var profile = File.ReadAllBytes(slot.ProfilePath);
		if (!Readable(profile))
		{
			result.Errors.Add($"Kill {kill}: the profile that would load is not readable.");
			return;
		}
		var generation = ProfileGeneration(profile);
		if (newest != null && generation < newest.Meta.Generation)
			result.Errors.Add($"Kill {kill}: loads generation {generation}, older than backup {newest.Meta.Generation}.");
		if (generation < result.LastGeneration)
			result.Errors.Add($"Kill {kill}: went back from generation {result.LastGeneration} to {generation}.");
		result.LastGeneration = Math.Max(result.LastGeneration, generation);
	}

	/// <summary>A stand-in for the game's profile: "PROFILE|{generation}|{length}|{payload}|END".</summary>
	public static byte[] Profile(long generation, int size, Random random)
	{
		var payload = new char[size];
		for (var i = 0; i < size; i++) payload[i] = (char)('a' + random.Next(26));
		return Encoding.ASCII.GetBytes($"PROFILE|{generation}|{size}|{new string(payload)}|END");
	}

	public static bool Readable(byte[] profile)
	{
		var text = Encoding.ASCII.GetString(profile);
		var parts = text.Split('|');
		return parts.Length == 5 && parts[0] == "PROFILE" && long.TryParse(parts[1], out _) && int.TryParse(parts[2], out var size) &&
		       parts[3].Length == size && parts[4] == "END";
	}

	public static long ProfileGeneration(byte[] profile) => long.Parse(Encoding.ASCII.GetString(profile).Split('|')[1]);
}

/// <summary>A save slot on disk for the kill test (sidecar: "{generation}|{hash}|{progress}").</summary>
public sealed class DiskSlot : ISaveSlot
{
	private DiskSlot(string dir)
	{
		SidecarPath = Path.Combine(dir, "save_4.cms21mp");
		ProfilePath = Path.Combine(dir, "profile4.cms21b");
	}

	public string SidecarPath { get; }
	public string ProfilePath { get; }
	public string SaveId => SaveKill.SaveId;
	public string Name => "Kill test";
	public long Generation { get; private set; }
	public string Hash { get; private set; }
	public string Progress { get; set; } = "";

	public static DiskSlot Open(string dir)
	{
		var slot = new DiskSlot(dir);
		if (!File.Exists(slot.SidecarPath)) return slot;
		var parts = File.ReadAllText(slot.SidecarPath).Split(new[] { '|' }, 3);
		slot.Generation = long.Parse(parts[0]);
		slot.Hash = parts[1] == "-" ? null : parts[1];
		slot.Progress = parts[2];
		return slot;
	}

	public byte[] WriteSidecar(long generation, string profileSha256)
	{
		Generation = generation;
		Hash = profileSha256;
		var bytes = Encoding.UTF8.GetBytes($"{Generation}|{Hash ?? "-"}|{Progress}");
		AtomicFile.WriteAllBytes(SidecarPath, bytes, SidecarPath + ".bak");
		return bytes;
	}

	public byte[] ReadProfile(out byte format)
	{
		format = 1;
		return File.Exists(ProfilePath) ? File.ReadAllBytes(ProfilePath) : null;
	}

	public (DateTime writtenUtc, long length)? ProfileFileState
	{
		get
		{
			var info = new FileInfo(ProfilePath);
			return info.Exists ? (info.LastWriteTimeUtc, info.Length) : null;
		}
	}

	public SlotInput Input(BundleStore backups)
	{
		var list = backups.List(SaveId);
		return new SlotInput
		{
			AlreadyLoaded = File.Exists(SidecarPath),
			Schema = SlotValidator.CurrentSchema,
			Generation = Generation,
			ProfileSha256 = Hash,
			Profile = File.Exists(ProfilePath) ? File.ReadAllBytes(ProfilePath) : null,
			Readable = SaveKill.Readable,
			NewestBackup = list.Count > 0 ? list[0].Meta : null
		};
	}
}