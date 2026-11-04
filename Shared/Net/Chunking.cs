using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace CMS21Together.Shared.Net;

public static class NetCompression
{
	/// <summary>Gzips the data; returns null if compression is unavailable on this runtime.</summary>
	public static byte[] TryCompress(byte[] data, int offset, int count)
	{
		try
		{
			using var output = new MemoryStream(count / 2 + 64);
			using (var gzip = new GZipStream(output, CompressionLevel.Fastest, true))
				gzip.Write(data, offset, count);
			return output.ToArray();
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			Log.Warn($"[Net] Compression unavailable, sending uncompressed: {ex.Message}");
			return null;
		}
	}

	/// <summary>Bounded gunzip: a decompression bomb fails instead of exhausting memory.</summary>
	public static byte[] Decompress(byte[] data, int offset, int count, int maxOutput)
	{
		try
		{
			using var input = new MemoryStream(data, offset, count, false);
			using var gzip = new GZipStream(input, CompressionMode.Decompress);
			using var output = new MemoryStream();
			var block = new byte[16384];
			int read;
			while ((read = gzip.Read(block, 0, block.Length)) > 0)
			{
				if (output.Length + read > maxOutput) throw new NetFormatException($"Decompressed data exceeds {maxOutput} bytes.");
				output.Write(block, 0, read);
			}
			return output.ToArray();
		}
		catch (NetFormatException)
		{
			throw;
		}
		catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or NotSupportedException)
		{
			throw new NetFormatException("Corrupt compressed frame.", ex);
		}
	}
}

/// <summary>
/// Sender side of a connection: assigns sequence numbers, compresses large frames and splits anything
/// still over <see cref="ChunkThreshold"/> into CRC-protected chunk frames. Every output frame is at most
/// <see cref="ChunkThreshold"/> bytes, which keeps Steam messages and TCP writes small.
/// </summary>
public sealed class FrameEncoder
{
	public const int ChunkThreshold = 64 * 1024;
	public const int ChunkHeaderSize = 16;
	public const int MaxMessageSize = 64 * 1024 * 1024;
	public const int ChunkDataSize = ChunkThreshold - FrameCodec.HeaderSize - ChunkHeaderSize;

	private readonly object sync = new();
	private uint seq;
	private uint transfer;

	public bool CompressionEnabled { get; set; } = true;

	/// <summary>Encodes a logical frame (header reserved by <c>Packet</c>; its seq field is overwritten).</summary>
	public void Encode(byte[] frame, int length, bool unreliable, List<byte[]> output)
	{
		if (length < FrameCodec.HeaderSize) throw new ArgumentException("Frame shorter than its header.", nameof(length));
		if (length - FrameCodec.HeaderSize > MaxMessageSize) throw new InvalidOperationException($"Message of {length} bytes exceeds {MaxMessageSize}.");

		var type = FrameCodec.PeekType(frame);
		var flags = FrameCodec.PeekFlags(frame) & ~(FrameFlags.Unreliable | FrameFlags.Chunk | FrameFlags.Compressed);
		lock (sync)
		{
			var messageSeq = ++seq;
			if (length <= ChunkThreshold)
			{
				var single = new byte[length];
				Buffer.BlockCopy(frame, 0, single, 0, length);
				FrameCodec.WriteHeader(single, type, unreliable ? flags | FrameFlags.Unreliable : flags, messageSeq, length - FrameCodec.HeaderSize);
				output.Add(single);
				return;
			}

			var payloadLength = length - FrameCodec.HeaderSize;
			var compressed = CompressionEnabled ? NetCompression.TryCompress(frame, FrameCodec.HeaderSize, payloadLength) : null;
			var inner = compressed != null && compressed.Length < payloadLength
				? FrameCodec.Encode(type, flags | FrameFlags.Compressed, messageSeq, compressed, 0, compressed.Length)
				: FrameCodec.Encode(type, flags, messageSeq, frame, FrameCodec.HeaderSize, payloadLength);
			if (inner.Length <= ChunkThreshold)
			{
				output.Add(inner);
				return;
			}

			var transferId = ++transfer;
			var crc = Crc32.Compute(inner);
			for (var offset = 0; offset < inner.Length; offset += ChunkDataSize)
			{
				var count = Math.Min(ChunkDataSize, inner.Length - offset);
				var chunk = new byte[FrameCodec.HeaderSize + ChunkHeaderSize + count];
				FrameCodec.WriteHeader(chunk, NetFrameTypes.Chunk, FrameFlags.Chunk, ++seq, ChunkHeaderSize + count);
				FrameCodec.PutUInt32(chunk, FrameCodec.HeaderSize, transferId);
				FrameCodec.PutUInt32(chunk, FrameCodec.HeaderSize + 4, (uint)inner.Length);
				FrameCodec.PutUInt32(chunk, FrameCodec.HeaderSize + 8, (uint)offset);
				FrameCodec.PutUInt32(chunk, FrameCodec.HeaderSize + 12, crc);
				Buffer.BlockCopy(inner, offset, chunk, FrameCodec.HeaderSize + ChunkHeaderSize, count);
				output.Add(chunk);
			}
		}
	}

	public byte[] EncodeSingle(ushort type, byte[] payload, bool unreliable = false)
	{
		var frame = FrameCodec.Encode(type, FrameFlags.None, 0, payload);
		var output = new List<byte[]>(1);
		Encode(frame, frame.Length, unreliable, output);
		if (output.Count != 1) throw new InvalidOperationException("Control frame unexpectedly chunked.");
		return output[0];
	}
}

/// <summary>Receiver side: rebuilds chunked transfers, verifies their CRC and inflates compressed frames.</summary>
public sealed class Reassembler
{
	public const int MaxPendingTransfers = 8;
	public const int MaxPendingBytes = FrameEncoder.MaxMessageSize + FrameCodec.HeaderSize;

	private readonly Dictionary<uint, Transfer> pending = new();
	private long pendingBytes;

	public int PendingTransfers => pending.Count;

	/// <summary>Returns the logical frame, or null while a chunked transfer is incomplete. Throws <see cref="NetFormatException"/> on bad data.</summary>
	public NetFrame Accept(NetFrame wire)
	{
		if (wire.Type != NetFrameTypes.Chunk)
		{
			if ((wire.Flags & FrameFlags.Chunk) != 0) throw new NetFormatException("Chunk flag on a non-chunk frame.");
			return Unpack(wire);
		}
		if ((wire.Flags & FrameFlags.Chunk) == 0) throw new NetFormatException("Chunk frame without the chunk flag.");

		var reader = wire.CreateReader();
		var transferId = reader.ReadUInt32();
		var total = reader.ReadUInt32();
		var offset = reader.ReadUInt32();
		var crc = reader.ReadUInt32();
		var count = reader.Remaining;

		if (total <= FrameEncoder.ChunkThreshold - FrameCodec.HeaderSize || total > MaxPendingBytes) throw new NetFormatException($"Invalid chunked transfer size {total}.");
		if (count == 0) throw new NetFormatException("Empty chunk.");

		if (!pending.TryGetValue(transferId, out var transfer))
		{
			if (offset != 0) throw new NetFormatException("Chunked transfer does not start at offset 0.");
			if (pending.Count >= MaxPendingTransfers) throw new NetFormatException("Too many concurrent chunked transfers.");
			if (pendingBytes + total > MaxPendingBytes) throw new NetFormatException("Chunked transfers exceed the memory budget.");
			transfer = new Transfer(new byte[total], crc);
			pending.Add(transferId, transfer);
			pendingBytes += total;
		}
		else if (transfer.Data.Length != total || transfer.Crc != crc)
		{
			throw new NetFormatException("Chunk header changed mid-transfer.");
		}

		if (offset != transfer.Received) throw new NetFormatException($"Out-of-order chunk (offset {offset}, expected {transfer.Received}).");
		if (count > total - offset) throw new NetFormatException("Chunk overruns its transfer.");

		Buffer.BlockCopy(reader.Buffer, reader.Position, transfer.Data, (int)offset, count);
		transfer.Received += count;
		if (transfer.Received < total) return null;

		pending.Remove(transferId);
		pendingBytes -= total;
		if (Crc32.Compute(transfer.Data) != crc) throw new NetFormatException("Chunked transfer failed its CRC check.");
		var inner = FrameCodec.Decode(transfer.Data, 0, transfer.Data.Length, MaxPendingBytes);
		if (inner.Type == NetFrameTypes.Chunk || (inner.Flags & FrameFlags.Chunk) != 0) throw new NetFormatException("Nested chunk frame.");
		return Unpack(inner);
	}

	private static NetFrame Unpack(NetFrame frame)
	{
		if ((frame.Flags & FrameFlags.Compressed) == 0) return frame;
		var payload = NetCompression.Decompress(frame.Buffer, frame.PayloadOffset, frame.PayloadLength, FrameEncoder.MaxMessageSize);
		return new NetFrame(frame.Type, frame.Flags & ~FrameFlags.Compressed, frame.Seq, payload, 0, payload.Length);
	}

	private sealed class Transfer
	{
		public Transfer(byte[] data, uint crc)
		{
			Data = data;
			Crc = crc;
		}

		public byte[] Data { get; }
		public uint Crc { get; }
		public long Received { get; set; }
	}
}
