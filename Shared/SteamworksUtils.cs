using System;
using System.Security.Cryptography;
using System.Text;

namespace CMS21Together.Shared;

/// <summary>Lobby codes: a Steam id written in base 62 with a random rotation, so codes don't look sequential.</summary>
public static class SteamworksUtils
{
	private const string Characters = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

	public static string GetServerID(ulong steamId)
	{
		var offset = RandomOffset();
		var result = new StringBuilder();
		do
		{
			var index = (int)(steamId % 62);
			result.Insert(0, Characters[(index + offset) % 62]);
			steamId /= 62;
		} while (steamId > 0);

		result.Append(Characters[offset]);
		return result.ToString();
	}

	/// <summary>Decodes a lobby code. Returns false for empty, malformed or overflowing codes instead of throwing.</summary>
	public static bool TryConvertServerID(string code, out ulong steamId)
	{
		steamId = 0;
		code = code?.Trim();
		if (string.IsNullOrEmpty(code) || code.Length < 2) return false;

		var offset = Characters.IndexOf(code[code.Length - 1]);
		if (offset < 0) return false;

		ulong result = 0;
		for (var i = 0; i < code.Length - 1; i++)
		{
			var index = Characters.IndexOf(code[i]);
			if (index < 0) return false;
			var original = (ulong)((index - offset + 62) % 62);
			if (result > (ulong.MaxValue - original) / 62) return false;
			result = result * 62 + original;
		}

		if (result == 0) return false;
		steamId = result;
		return true;
	}

	private static int RandomOffset()
	{
		var bytes = new byte[4];
		using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
		return (int)(BitConverter.ToUInt32(bytes, 0) % 62);
	}
}
