using System;
using System.Collections.Generic;

namespace CMS21Together.Shared.Net;

[Flags]
public enum FrameFlags : byte
{
	None = 0,
	Compressed = 1,
	Chunk = 2,
	Unreliable = 4
}

/// <summary>Transport-level frame types, kept out of the game's <c>PacketTypes</c> range and never routed to game handlers.</summary>
public static class NetFrameTypes
{
	public const ushort Chunk = 0xFFF0;
	public const ushort Hello = 0xFFF1;
	public const ushort Welcome = 0xFFF2;
	public const ushort Reject = 0xFFF3;
	public const ushort Ping = 0xFFF4;
	public const ushort Pong = 0xFFF5;
	public const ushort Bye = 0xFFF6;

	public static bool IsTransport(ushort type) => type >= Chunk;
}

/// <summary>A decoded frame: <c>len:u32 | type:u16 | flags:u8 | seq:u32 | payload</c>.</summary>
public sealed class NetFrame
{
	public NetFrame(ushort type, FrameFlags flags, uint seq, byte[] buffer, int payloadOffset, int payloadLength)
	{
		Type = type;
		Flags = flags;
		Seq = seq;
		Buffer = buffer;
		PayloadOffset = payloadOffset;
		PayloadLength = payloadLength;
	}

	public ushort Type { get; }
	public FrameFlags Flags { get; }
	public uint Seq { get; }
	public byte[] Buffer { get; }
	public int PayloadOffset { get; }
	public int PayloadLength { get; }

	public NetReader CreateReader() => new(Buffer, PayloadOffset, PayloadLength);

	public byte[] CopyPayload()
	{
		var copy = new byte[PayloadLength];
		System.Buffer.BlockCopy(Buffer, PayloadOffset, copy, 0, PayloadLength);
		return copy;
	}
}

public static class FrameCodec
{
	public const int LengthSize = 4;
	public const int HeaderSize = 11;
	public const int TypeOffset = 4;
	public const int FlagsOffset = 6;
	public const int SeqOffset = 7;

	/// <summary>Largest value accepted in the length field of a wire frame.</summary>
	public const int MaxWireFrameLength = 1 << 20;

	public static byte[] Encode(ushort type, FrameFlags flags, uint seq, byte[] payload, int offset, int count)
	{
		var frame = new byte[HeaderSize + count];
		WriteHeader(frame, type, flags, seq, count);
		if (count > 0) Buffer.BlockCopy(payload, offset, frame, HeaderSize, count);
		return frame;
	}

	public static byte[] Encode(ushort type, FrameFlags flags, uint seq, byte[] payload) =>
		Encode(type, flags, seq, payload ?? Array.Empty<byte>(), 0, payload?.Length ?? 0);

	public static void WriteHeader(byte[] frame, ushort type, FrameFlags flags, uint seq, int payloadLength)
	{
		PutUInt32(frame, 0, (uint)(payloadLength + HeaderSize - LengthSize));
		frame[TypeOffset] = (byte)type;
		frame[TypeOffset + 1] = (byte)(type >> 8);
		frame[FlagsOffset] = (byte)flags;
		PutUInt32(frame, SeqOffset, seq);
	}

	public static void PatchSeq(byte[] frame, uint seq) => PutUInt32(frame, SeqOffset, seq);

	public static void PatchFlags(byte[] frame, FrameFlags flags) => frame[FlagsOffset] = (byte)flags;

	public static ushort PeekType(byte[] frame) => (ushort)(frame[TypeOffset] | (frame[TypeOffset + 1] << 8));

	public static FrameFlags PeekFlags(byte[] frame) => (FrameFlags)frame[FlagsOffset];

	/// <summary>Decodes exactly one frame occupying <paramref name="count"/> bytes.</summary>
	public static NetFrame Decode(byte[] data, int offset, int count, int maxLength = MaxWireFrameLength)
	{
		if (count < HeaderSize) throw new NetFormatException($"Frame of {count} bytes is shorter than the header.");
		var length = GetUInt32(data, offset);
		if (length > maxLength) throw new NetFormatException($"Frame length {length} exceeds {maxLength}.");
		if (length != count - LengthSize) throw new NetFormatException($"Frame length {length} does not match {count - LengthSize} available bytes.");
		var type = (ushort)(data[offset + TypeOffset] | (data[offset + TypeOffset + 1] << 8));
		var flags = (FrameFlags)data[offset + FlagsOffset];
		var seq = GetUInt32(data, offset + SeqOffset);
		return new NetFrame(type, flags, seq, data, offset + HeaderSize, count - HeaderSize);
	}

	internal static void PutUInt32(byte[] buffer, int offset, uint value)
	{
		buffer[offset] = (byte)value;
		buffer[offset + 1] = (byte)(value >> 8);
		buffer[offset + 2] = (byte)(value >> 16);
		buffer[offset + 3] = (byte)(value >> 24);
	}

	internal static uint GetUInt32(byte[] buffer, int offset) =>
		(uint)(buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24));
}

/// <summary>Turns an arbitrary byte stream (split or merged reads) back into frames. Not thread-safe; one per connection.</summary>
public sealed class FrameDecoder
{
	private readonly int maxFrameLength;
	private byte[] buffer = new byte[4096];
	private int start;
	private int end;

	public FrameDecoder(int maxFrameLength = FrameCodec.MaxWireFrameLength)
	{
		this.maxFrameLength = maxFrameLength;
	}

	public int BufferedBytes => end - start;

	/// <summary>Appends received bytes and adds every completed frame to <paramref name="output"/>. Throws <see cref="NetFormatException"/> on a bad length.</summary>
	public void Push(byte[] data, int offset, int count, List<NetFrame> output)
	{
		Append(data, offset, count);
		while (end - start >= FrameCodec.LengthSize)
		{
			var length = FrameCodec.GetUInt32(buffer, start);
			if (length < FrameCodec.HeaderSize - FrameCodec.LengthSize || length > maxFrameLength)
				throw new NetFormatException($"Invalid frame length {length}.");
			var total = (int)length + FrameCodec.LengthSize;
			if (end - start < total) break;
			var frame = new byte[total];
			Buffer.BlockCopy(buffer, start, frame, 0, total);
			start += total;
			output.Add(FrameCodec.Decode(frame, 0, total, maxFrameLength));
		}
		if (start == end) start = end = 0;
	}

	private void Append(byte[] data, int offset, int count)
	{
		if (count <= 0) return;
		if (buffer.Length - end < count)
		{
			var live = end - start;
			if (live + count <= buffer.Length)
			{
				Buffer.BlockCopy(buffer, start, buffer, 0, live);
			}
			else
			{
				var size = buffer.Length;
				while (size < live + count) size *= 2;
				var bigger = new byte[size];
				Buffer.BlockCopy(buffer, start, bigger, 0, live);
				buffer = bigger;
			}
			start = 0;
			end = live;
		}
		Buffer.BlockCopy(data, offset, buffer, end, count);
		end += count;
	}
}

public static class Crc32
{
	private static readonly uint[] Table = BuildTable();

	private static uint[] BuildTable()
	{
		var table = new uint[256];
		for (uint i = 0; i < 256; i++)
		{
			var c = i;
			for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
			table[i] = c;
		}
		return table;
	}

	public static uint Compute(byte[] data, int offset, int count)
	{
		var crc = 0xFFFFFFFFu;
		for (var i = offset; i < offset + count; i++) crc = Table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
		return ~crc;
	}

	public static uint Compute(byte[] data) => Compute(data, 0, data.Length);
}
