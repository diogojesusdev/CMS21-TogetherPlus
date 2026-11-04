using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CMS21Together.Shared.Persistence;

public enum MigrationOutcome
{
	NothingToDo,
	AlreadyDone,
	Migrated,
	Failed
}

public sealed class MigrationResult
{
	public MigrationOutcome Outcome;
	public readonly List<string> Copied = new();
	public readonly List<string> Skipped = new();
	public readonly List<string> Errors = new();
}

/// <summary>
/// Audit A20: mod saves used to live in the game folder (<c>Mods\togetherMod\saves</c>), where verifying or
/// reinstalling the game deletes them. They are copied once next to the vanilla saves. The originals are kept,
/// an existing target is never overwritten, and a marker stops the copy from running again.
/// </summary>
public static class SidecarMigration
{
	public const string MarkerName = "migrated-from-game-folder.txt";
	public const string Pattern = "save_*.cms21mp";

	public static MigrationResult Run(string oldDir, string newDir)
	{
		var result = new MigrationResult();
		var marker = Path.Combine(newDir, MarkerName);
		if (File.Exists(marker))
		{
			result.Outcome = MigrationOutcome.AlreadyDone;
			return result;
		}
		if (!Directory.Exists(oldDir) || Directory.GetFiles(oldDir, Pattern).Length == 0)
		{
			result.Outcome = MigrationOutcome.NothingToDo;
			return result;
		}

		foreach (var source in Directory.GetFiles(oldDir, Pattern).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
		{
			var name = Path.GetFileName(source);
			var target = Path.Combine(newDir, name);
			try
			{
				if (File.Exists(target))
				{
					result.Skipped.Add(name);
					continue;
				}
				var bytes = File.ReadAllBytes(source);
				AtomicFile.WriteAllBytes(target, bytes);
				if (Sha256Hex.Of(File.ReadAllBytes(target)) != Sha256Hex.Of(bytes))
					throw new IOException("The copy does not match the original.");
				result.Copied.Add(name);
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
			{
				result.Errors.Add($"{name}: {ex.Message}");
			}
		}

		if (result.Errors.Count > 0)
		{
			// No marker: the next start tries again (already copied files are skipped).
			result.Outcome = MigrationOutcome.Failed;
			return result;
		}
		var lines = new List<string>
		{
			$"Together+ copied these mod saves from {oldDir} on {DateTime.UtcNow:u}. The originals were left in place.",
		};
		lines.AddRange(result.Copied.Select(n => "copied  " + n));
		lines.AddRange(result.Skipped.Select(n => "skipped " + n + " (already here)"));
		AtomicFile.WriteAllText(marker, string.Join(Environment.NewLine, lines) + Environment.NewLine);
		result.Outcome = MigrationOutcome.Migrated;
		return result;
	}
}