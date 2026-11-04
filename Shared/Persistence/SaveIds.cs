using System;
using System.Security.Cryptography;
using System.Text;

namespace CMS21Together.Shared.Persistence;

/// <summary>Save ids name folders on disk, so only the exact format the host generates is accepted (no path tricks).</summary>
public static class SaveIds
{
	public static string New() => Guid.NewGuid().ToString("N");

	public static bool IsValid(string id) => IsLowerHex(id, 32);

	internal static bool IsLowerHex(string value, int length)
	{
		if (value == null || value.Length != length) return false;
		foreach (var c in value)
			if (!(c >= '0' && c <= '9' || c >= 'a' && c <= 'f'))
				return false;
		return true;
	}
}

public static class Sha256Hex
{
	public static string Of(byte[] data) => data == null ? null : Of(data, 0, data.Length);

	public static string Of(byte[] data, int offset, int count)
	{
		using var sha = SHA256.Create();
		return ToHex(sha.ComputeHash(data, offset, count));
	}

	public static bool IsValid(string hex) => SaveIds.IsLowerHex(hex, 64);

	internal static string ToHex(byte[] hash)
	{
		var text = new StringBuilder(hash.Length * 2);
		foreach (var b in hash) text.Append(b.ToString("x2"));
		return text.ToString();
	}
}