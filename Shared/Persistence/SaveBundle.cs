using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace CMS21Together.Shared.Persistence;

/// <summary>What a save bundle holds; readable without decompressing it.</summary>
public sealed class BundleMeta
{
	public string SaveId;
	public long Generation;
	public DateTime CreatedUtc;
	public string Name;

	/// <summary>Vanilla profile format: 1 = <c>.cms21b</c>, 0 = <c>.cms21</c>.</summary>
	public byte ProfileFormat = 1;

	/// <summary>Sidecar schema version.</summary>
	public int Schema;

	public string ProfileSha256;
	public string SidecarSha256;
	public int ProfileLength;
	public int SidecarLength;

	/// <summary>Why the bundle exists when it is not a regular save ("before restore", "deleted save", ...).</summary>
	public string Note;

	public BundleMeta Clone() => (BundleMeta)MemberwiseClone();

	public override string ToString() => $"\"{Name}\" generation {Generation} ({CreatedUtc:yyyy-MM-dd HH:mm} UTC)";
}

/// <summary>
/// One save generation in one file: the vanilla profile plus the mod sidecar, compressed, with the hash of each file
/// and a SHA-256 over the whole bundle, so a torn, truncated or tampered copy is rejected. Layout:
/// "TPB1", version byte, header length + header, body length + gzip(profile, sidecar), SHA-256 of everything before it.
/// </summary>
public sealed class SaveBundle
{
	public const byte Version = 1;
	public const int MaxProfileBytes = 64 * 1024 * 1024;
	public const int MaxSidecarBytes = 16 * 1024 * 1024;
	public const int MaxHeaderBytes = 8 * 1024;
	public const int MaxTextLength = 128;

	/// <summary>Largest encoded bundle accepted (gzip of incompressible data grows it by a fraction of a percent).</summary>
	public const int MaxBundleBytes = MaxProfileBytes + MaxSidecarBytes + 1024 * 1024;

	private const int HashSize = 32;
	private const int PrefixSize = 4 + 1 + 4;
	private static readonly byte[] Magic = { (byte)'T', (byte)'P', (byte)'B', (byte)'1' };

	private SaveBundle(BundleMeta meta, byte[] profile, byte[] sidecar)
	{
		Meta = meta;
		Profile = profile;
		Sidecar = sidecar;
	}

	public BundleMeta Meta { get; }
	public byte[] Profile { get; }
	public byte[] Sidecar { get; }

	/// <summary>Builds a bundle. Fills in the hashes and lengths of <paramref name="meta"/>.</summary>
	public static byte[] Encode(BundleMeta meta, byte[] profile, byte[] sidecar)
	{
		if (meta == null) throw new ArgumentNullException(nameof(meta));
		profile ??= Array.Empty<byte>();
		sidecar ??= Array.Empty<byte>();
		meta.ProfileLength = profile.Length;
		meta.SidecarLength = sidecar.Length;
		meta.ProfileSha256 = Sha256Hex.Of(profile);
		meta.SidecarSha256 = Sha256Hex.Of(sidecar);
		var problem = Check(meta);
		if (problem != null) throw new ArgumentException(problem, nameof(meta));

		var header = WriteHeader(meta);
		byte[] body;
		using (var compressed = new MemoryStream())
		{
			using (var gzip = new GZipStream(compressed, CompressionLevel.Optimal, true))
			{
				gzip.Write(profile, 0, profile.Length);
				gzip.Write(sidecar, 0, sidecar.Length);
			}
			body = compressed.ToArray();
		}

		using var output = new MemoryStream(PrefixSize + header.Length + 4 + body.Length + HashSize);
		output.Write(Magic, 0, Magic.Length);
		output.WriteByte(Version);
		WriteInt(output, header.Length);
		output.Write(header, 0, header.Length);
		WriteInt(output, body.Length);
		output.Write(body, 0, body.Length);
		using (var sha = SHA256.Create())
		{
			var hash = sha.ComputeHash(output.GetBuffer(), 0, (int)output.Length);
			output.Write(hash, 0, hash.Length);
		}
		return output.ToArray();
	}

	/// <summary>Fully verifies and unpacks a bundle.</summary>
	public static bool TryDecode(byte[] data, out SaveBundle bundle, out string error)
	{
		bundle = null;
		if (!TryParse(data, true, out var meta, out var bodyOffset, out var bodyLength, out error)) return false;

		var expected = meta.ProfileLength + meta.SidecarLength;
		byte[] plain;
		try
		{
			plain = Gunzip(data, bodyOffset, bodyLength, expected);
		}
		catch (Exception ex) when (ex is InvalidDataException || ex is IOException)
		{
			error = "The compressed data is damaged: " + ex.Message;
			return false;
		}
		if (plain == null)
		{
			error = "The compressed data does not have the announced size.";
			return false;
		}

		var profile = new byte[meta.ProfileLength];
		var sidecar = new byte[meta.SidecarLength];
		Buffer.BlockCopy(plain, 0, profile, 0, profile.Length);
		Buffer.BlockCopy(plain, profile.Length, sidecar, 0, sidecar.Length);
		if (Sha256Hex.Of(profile) != meta.ProfileSha256 || Sha256Hex.Of(sidecar) != meta.SidecarSha256)
		{
			error = "A file inside the bundle does not match its hash.";
			return false;
		}
		bundle = new SaveBundle(meta, profile, sidecar);
		return true;
	}

	/// <summary>Reads the header of a complete bundle. <paramref name="verifyHash"/> also checks the whole-bundle hash.</summary>
	public static bool TryReadMeta(byte[] data, bool verifyHash, out BundleMeta meta, out string error) =>
		TryParse(data, verifyHash, out meta, out _, out _, out error);

	/// <summary>Bytes to read from the start of a file for <see cref="TryReadHeader"/>.</summary>
	public const int HeaderPrefixBytes = PrefixSize + MaxHeaderBytes;

	/// <summary>Reads the header from the first bytes of a bundle file (listings: nothing is verified beyond the header).</summary>
	public static bool TryReadHeader(byte[] prefix, int count, out BundleMeta meta, out string error)
	{
		meta = null;
		if (prefix == null || count < PrefixSize)
		{
			error = "The file is too short to be a save bundle.";
			return false;
		}
		if (!CheckPrefix(prefix, out error)) return false;
		var headerLength = ReadInt(prefix, 5);
		if (headerLength <= 0 || headerLength > MaxHeaderBytes || PrefixSize + headerLength > count)
		{
			error = "The bundle header has an invalid size.";
			return false;
		}
		return TryReadHeaderAt(prefix, headerLength, out meta, out error);
	}

	/// <summary>The SHA-256 stored at the end of a bundle (identifies it; verified by <see cref="TryDecode"/>).</summary>
	public static string StoredHash(byte[] data) =>
		data == null || data.Length < HashSize ? null : Sha256Hex.ToHex(Copy(data, data.Length - HashSize, HashSize));

	private static byte[] Copy(byte[] data, int offset, int count)
	{
		var result = new byte[count];
		Buffer.BlockCopy(data, offset, result, 0, count);
		return result;
	}

	private static bool CheckPrefix(byte[] data, out string error)
	{
		error = null;
		for (var i = 0; i < Magic.Length; i++)
			if (data[i] != Magic[i])
			{
				error = "Not a Together+ save bundle.";
				return false;
			}
		if (data[4] != Version)
		{
			error = $"Unsupported bundle version {data[4]}.";
			return false;
		}
		return true;
	}

	private static bool TryReadHeaderAt(byte[] data, int headerLength, out BundleMeta meta, out string error)
	{
		try
		{
			meta = ReadHeader(data, PrefixSize, headerLength);
		}
		catch (Exception ex) when (ex is EndOfStreamException || ex is IOException || ex is ArgumentException || ex is DecoderFallbackException)
		{
			error = "The bundle header is damaged: " + ex.Message;
			meta = null;
			return false;
		}
		error = Check(meta);
		if (error == null) return true;
		meta = null;
		return false;
	}

	private static bool TryParse(byte[] data, bool verifyHash, out BundleMeta meta, out int bodyOffset, out int bodyLength, out string error)
	{
		meta = null;
		bodyOffset = bodyLength = 0;
		if (data == null || data.Length < PrefixSize + 4 + HashSize)
		{
			error = "The file is too short to be a save bundle.";
			return false;
		}
		if (data.Length > MaxBundleBytes)
		{
			error = "The file is too large to be a save bundle.";
			return false;
		}
		if (!CheckPrefix(data, out error)) return false;
		if (verifyHash)
		{
			using var sha = SHA256.Create();
			var hash = sha.ComputeHash(data, 0, data.Length - HashSize);
			for (var i = 0; i < HashSize; i++)
				if (hash[i] != data[data.Length - HashSize + i])
				{
					error = "The bundle does not match its SHA-256 (damaged or modified).";
					return false;
				}
		}

		var headerLength = ReadInt(data, 5);
		if (headerLength <= 0 || headerLength > MaxHeaderBytes || PrefixSize + headerLength + 4 + HashSize > data.Length)
		{
			error = "The bundle header has an invalid size.";
			return false;
		}
		if (!TryReadHeaderAt(data, headerLength, out meta, out error)) return false;

		bodyOffset = PrefixSize + headerLength + 4;
		bodyLength = ReadInt(data, PrefixSize + headerLength);
		if (bodyLength < 0 || (long)bodyOffset + bodyLength + HashSize != data.Length)
		{
			error = "The bundle body has an invalid size.";
			meta = null;
			return false;
		}
		return true;
	}

	private static string Check(BundleMeta meta)
	{
		if (!SaveIds.IsValid(meta.SaveId)) return "Invalid save id.";
		if (meta.Generation < 0) return "Invalid generation.";
		if (meta.ProfileFormat > 1) return "Unknown profile format.";
		if (meta.ProfileLength < 0 || meta.ProfileLength > MaxProfileBytes) return "Invalid profile size.";
		if (meta.SidecarLength < 0 || meta.SidecarLength > MaxSidecarBytes) return "Invalid sidecar size.";
		if (!Sha256Hex.IsValid(meta.ProfileSha256) || !Sha256Hex.IsValid(meta.SidecarSha256)) return "Invalid file hash.";
		if ((meta.Name?.Length ?? 0) > MaxTextLength || (meta.Note?.Length ?? 0) > MaxTextLength) return "Name or note too long.";
		return null;
	}

	private static byte[] WriteHeader(BundleMeta meta)
	{
		using var stream = new MemoryStream();
		using (var writer = new BinaryWriter(stream, new UTF8Encoding(false), true))
		{
			writer.Write(meta.SaveId);
			writer.Write(meta.Generation);
			writer.Write(meta.CreatedUtc.ToUniversalTime().Ticks);
			writer.Write(meta.Name ?? string.Empty);
			writer.Write(meta.ProfileFormat);
			writer.Write(meta.Schema);
			writer.Write(meta.ProfileSha256);
			writer.Write(meta.SidecarSha256);
			writer.Write(meta.ProfileLength);
			writer.Write(meta.SidecarLength);
			writer.Write(meta.Note ?? string.Empty);
		}
		return stream.ToArray();
	}

	private static BundleMeta ReadHeader(byte[] data, int offset, int length)
	{
		using var reader = new BinaryReader(new MemoryStream(data, offset, length, false), new UTF8Encoding(false, true));
		var meta = new BundleMeta
		{
			SaveId = reader.ReadString(),
			Generation = reader.ReadInt64()
		};
		var ticks = reader.ReadInt64();
		if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) throw new ArgumentException("Invalid time.");
		meta.CreatedUtc = new DateTime(ticks, DateTimeKind.Utc);
		meta.Name = reader.ReadString();
		meta.ProfileFormat = reader.ReadByte();
		meta.Schema = reader.ReadInt32();
		meta.ProfileSha256 = reader.ReadString();
		meta.SidecarSha256 = reader.ReadString();
		meta.ProfileLength = reader.ReadInt32();
		meta.SidecarLength = reader.ReadInt32();
		meta.Note = reader.ReadString();
		if (meta.Note.Length == 0) meta.Note = null;
		return meta;
	}

	/// <summary>Bounded gunzip: returns null unless the output is exactly <paramref name="expected"/> bytes.</summary>
	private static byte[] Gunzip(byte[] data, int offset, int count, int expected)
	{
		var output = new byte[expected];
		using var gzip = new GZipStream(new MemoryStream(data, offset, count, false), CompressionMode.Decompress);
		var total = 0;
		while (total < expected)
		{
			var read = gzip.Read(output, total, expected - total);
			if (read == 0) return null;
			total += read;
		}
		var probe = new byte[1];
		return gzip.Read(probe, 0, 1) == 0 ? output : null;
	}

	private static void WriteInt(Stream stream, int value)
	{
		stream.WriteByte((byte)value);
		stream.WriteByte((byte)(value >> 8));
		stream.WriteByte((byte)(value >> 16));
		stream.WriteByte((byte)(value >> 24));
	}

	private static int ReadInt(byte[] data, int offset) =>
		data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16 | data[offset + 3] << 24;
}