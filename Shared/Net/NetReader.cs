using System;
using System.IO;
using System.Text;

namespace CMS21Together.Shared.Net;

/// <summary>Malformed or hostile network data. Never crashes the host: the offending packet is dropped.</summary>
public class NetFormatException : IOException
{
	public NetFormatException(string message) : base(message) { }
	public NetFormatException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Bounds-checked little-endian reader. Every read fails with <see cref="NetFormatException"/> instead of over-reading.</summary>
public sealed class NetReader
{
	public const int MaxStringBytes = 1 << 20;
	private static readonly UTF8Encoding Utf8 = new(false, true);

	private readonly byte[] buffer;
	private readonly int end;
	private int position;

	public NetReader(byte[] buffer) : this(buffer, 0, buffer?.Length ?? 0) { }

	public NetReader(byte[] buffer, int offset, int count)
	{
		if (buffer == null) throw new ArgumentNullException(nameof(buffer));
		if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
		this.buffer = buffer;
		position = offset;
		end = offset + count;
	}

	public int Position => position;
	public int Remaining => end - position;
	public byte[] Buffer => buffer;

	private void Need(int count)
	{
		if (count < 0 || end - position < count)
			throw new NetFormatException($"Read past end of data (need {count} bytes, {end - position} left).");
	}

	public void Skip(int count)
	{
		Need(count);
		position += count;
	}

	public byte ReadByte()
	{
		Need(1);
		return buffer[position++];
	}

	public sbyte ReadSByte() => (sbyte)ReadByte();

	public bool ReadBool()
	{
		var value = ReadByte();
		if (value > 1) throw new NetFormatException($"Invalid bool value {value}.");
		return value == 1;
	}

	public ushort ReadUInt16()
	{
		Need(2);
		var value = (ushort)(buffer[position] | (buffer[position + 1] << 8));
		position += 2;
		return value;
	}

	public short ReadInt16() => (short)ReadUInt16();
	public char ReadChar() => (char)ReadUInt16();

	public uint ReadUInt32()
	{
		Need(4);
		var value = (uint)(buffer[position] | (buffer[position + 1] << 8) | (buffer[position + 2] << 16) | (buffer[position + 3] << 24));
		position += 4;
		return value;
	}

	public int ReadInt32() => (int)ReadUInt32();

	public ulong ReadUInt64()
	{
		ulong low = ReadUInt32();
		ulong high = ReadUInt32();
		return low | (high << 32);
	}

	public long ReadInt64() => (long)ReadUInt64();
	public float ReadSingle() => new FloatBits { UInt = ReadUInt32() }.Float;
	public double ReadDouble() => BitConverter.Int64BitsToDouble(ReadInt64());

	public decimal ReadDecimal()
	{
		var bits = new int[4];
		for (var i = 0; i < 4; i++) bits[i] = ReadInt32();
		try
		{
			return new decimal(bits);
		}
		catch (ArgumentException ex)
		{
			throw new NetFormatException("Invalid decimal.", ex);
		}
	}

	public string ReadString(int maxBytes = MaxStringBytes)
	{
		var count = ReadInt32();
		if (count == -1) return null;
		if (count < -1) throw new NetFormatException($"Invalid string length {count}.");
		if (count > maxBytes) throw new NetFormatException($"String of {count} bytes exceeds the {maxBytes} byte limit.");
		Need(count);
		try
		{
			var value = Utf8.GetString(buffer, position, count);
			position += count;
			return value;
		}
		catch (ArgumentException ex)
		{
			throw new NetFormatException("Invalid UTF-8 string.", ex);
		}
	}

	public byte[] ReadBytes(int count)
	{
		Need(count);
		var result = new byte[count];
		System.Buffer.BlockCopy(buffer, position, result, 0, count);
		position += count;
		return result;
	}
}
