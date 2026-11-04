using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CMS21Together.Shared;

/// <summary>
/// The secret this player uses to prove who they are on direct-IP hosts, one per host save (audit A19). Kept next to
/// the vanilla saves so reinstalling the game keeps it. Each host only ever receives the token for its own save id.
/// </summary>
public sealed class CredentialStore
{
	private const string Header = "TogetherPlus-credentials-1";
	private const int MaxSaveIdLength = 64;
	private readonly Dictionary<string, string> tokens = new(StringComparer.Ordinal);
	private readonly string path;

	/// <summary>The file exists but could not be read: it is never written over (it holds every host's token).</summary>
	private bool unreadable;

	public CredentialStore(string path)
	{
		this.path = path;
		Load();
	}

	public static string DefaultPath => Path.Combine(PlusPaths.Root, "credentials.txt");

	public int Count => tokens.Count;

	public static bool IsValidSaveId(string saveId)
	{
		if (string.IsNullOrEmpty(saveId) || saveId.Length > MaxSaveIdLength) return false;
		foreach (var c in saveId)
			if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_'))
				return false;
		return true;
	}

	/// <summary>
	/// The token for this save, created and written to disk before it is returned, so the host can never bind a profile
	/// to a token this player does not have. Returns null for an invalid save id.
	/// </summary>
	public string GetOrCreate(string saveId)
	{
		if (!IsValidSaveId(saveId)) return null;
		if (tokens.TryGetValue(saveId, out var token)) return token;

		// Phase 8 (assessment): a read that failed once (file briefly locked) is retried; a file that stays unreadable is
		// moved aside first. Writing over it dropped every other host's token, and with it that player's progress there.
		if (unreadable)
		{
			Load();
			if (tokens.TryGetValue(saveId, out token)) return token;
		}

		token = IdentityService.NewToken();
		tokens[saveId] = token;
		try
		{
			if (unreadable)
			{
				File.Move(path, path + ".unreadable-" + DateTime.UtcNow.Ticks);
				unreadable = false;
				Log.Warn($"[Identity] {path} could not be read; it was kept aside and a new one started.");
			}
			Save();
		}
		catch (Exception ex)
		{
			// Still usable for this run; it will be retried with the next new token.
			Log.Error($"[Identity] Could not store credentials in {path}", ex);
		}
		return token;
	}

	private void Load()
	{
		unreadable = false;
		string text;
		try
		{
			text = AtomicFile.ReadAllTextOrNull(path);
		}
		catch (Exception ex)
		{
			Log.Warn($"[Identity] Could not read {path}: {ex.Message}");
			unreadable = File.Exists(path);
			return;
		}
		if (text == null) return;

		var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
		if (lines.Length == 0 || lines[0] != Header)
		{
			Log.Warn($"[Identity] Ignoring {path}: unknown format.");
			unreadable = true;
			return;
		}
		for (var i = 1; i < lines.Length; i++)
		{
			var parts = lines[i].Split('\t');
			if (parts.Length == 2 && IsValidSaveId(parts[0]) && IdentityService.IsValidToken(parts[1]))
				tokens[parts[0]] = parts[1];
		}
	}

	private void Save()
	{
		var builder = new StringBuilder();
		builder.Append(Header).Append('\n');
		foreach (var pair in tokens) builder.Append(pair.Key).Append('\t').Append(pair.Value).Append('\n');
		AtomicFile.WriteAllText(path, builder.ToString());
	}
}