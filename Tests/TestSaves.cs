using System;
using System.IO;
using System.Text;
using CMS21Together.Shared;
using CMS21Together.Shared.Persistence;

namespace CMS21Together.Tests;

/// <summary>Stand-ins for the game's save files: a profile with a recognisable format and a tiny sidecar.</summary>
internal static class TestSaves
{
	public static readonly string SaveId = new('a', 32);

	public static string NewDirectory()
	{
		var dir = Path.Combine(Path.GetTempPath(), "tp-tests-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		return dir;
	}

	public static void DeleteDirectory(string dir)
	{
		try
		{
			if (Directory.Exists(dir)) Directory.Delete(dir, true);
		}
		catch (IOException)
		{
		}
	}

	public static byte[] RandomBytes(int count, int seed)
	{
		var data = new byte[count];
		new Random(seed).NextBytes(data);
		return data;
	}

	public static BundleMeta Meta(long generation, string saveId = null) => new()
	{
		SaveId = saveId ?? SaveId,
		Generation = generation,
		CreatedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(generation),
		Name = "Test save",
		ProfileFormat = 1,
		Schema = SlotValidator.CurrentSchema
	};

	public static byte[] Bundle(long generation, string saveId = null, int profileSize = 300, int seed = 0) =>
		SaveBundle.Encode(Meta(generation, saveId), RandomBytes(profileSize, seed + (int)generation), Encoding.UTF8.GetBytes($"sidecar {generation}"));

	/// <summary>A profile the fake parser can check: "PROFILE|{generation}|{payload}|END".</summary>
	public static byte[] Profile(long generation, string payload = "garage") => Encoding.UTF8.GetBytes($"PROFILE|{generation}|{payload}|END");

	public static bool Readable(byte[] profile)
	{
		var text = Encoding.UTF8.GetString(profile);
		return text.StartsWith("PROFILE|", StringComparison.Ordinal) && text.EndsWith("|END", StringComparison.Ordinal);
	}

	public static long ProfileGeneration(byte[] profile) => long.Parse(Encoding.UTF8.GetString(profile).Split('|')[1]);
}

/// <summary>The committer's view of a slot, backed by real files (sidecar: "{generation}|{hash}|{progress}").</summary>
internal sealed class FakeSlot : ISaveSlot
{
	private readonly string dir;

	public FakeSlot(string dir, string saveId = null)
	{
		this.dir = dir;
		SaveId = saveId ?? TestSaves.SaveId;
	}

	public string SaveId { get; set; }
	public string Name => "Test save";
	public long Generation { get; set; }
	public string Hash { get; set; }
	public string Progress { get; set; } = "";
	public int SidecarWrites { get; private set; }
	public string SidecarPath => Path.Combine(dir, "save_5.cms21mp");
	public string ProfilePath => Path.Combine(dir, "profile5.cms21b");

	public byte[] WriteSidecar(long generation, string profileSha256)
	{
		Generation = generation;
		Hash = profileSha256;
		var bytes = Encoding.UTF8.GetBytes($"{Generation}|{Hash ?? "-"}|{Progress}");
		AtomicFile.WriteAllBytes(SidecarPath, bytes);
		SidecarWrites++;
		return bytes;
	}

	public static (long generation, string hash, string progress) ReadSidecar(byte[] bytes)
	{
		var parts = Encoding.UTF8.GetString(bytes).Split(new[] { '|' }, 3);
		return (long.Parse(parts[0]), parts[1] == "-" ? null : parts[1], parts[2]);
	}

	public (long generation, string hash, string progress) OnDisk() => ReadSidecar(File.ReadAllBytes(SidecarPath));

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

	/// <summary>What the game does after the hook: writes its profile (not atomically).</summary>
	public void GameWritesProfile(long generation, string payload = "garage") => File.WriteAllBytes(ProfilePath, TestSaves.Profile(generation, payload));

	/// <summary>Load validation input for the files on disk.</summary>
	public SlotInput Input(BundleStore backups)
	{
		var (generation, hash, _) = File.Exists(SidecarPath) ? OnDisk() : (0L, null, "");
		return new SlotInput
		{
			AlreadyLoaded = true,
			Schema = SlotValidator.CurrentSchema,
			Generation = generation,
			ProfileSha256 = hash,
			Profile = File.Exists(ProfilePath) ? File.ReadAllBytes(ProfilePath) : null,
			Readable = TestSaves.Readable,
			NewestBackup = backups?.List(SaveId).Count > 0 ? backups.List(SaveId)[0].Meta : null
		};
	}
}