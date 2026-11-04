using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace CMS21Together.Shared.Net;

/// <summary>Small size-bucketed byte[] pool (net472 has no ArrayPool without extra packages).</summary>
public static class BufferPool
{
	private static readonly int[] Sizes = { 256, 1024, 4096, 16384, 65536 };
	private const int MaxRetainedPerBucket = 64;
	private static readonly ConcurrentBag<byte[]>[] Buckets =
	{
		new(), new(), new(), new(), new()
	};

	public static byte[] Rent(int minimumLength)
	{
		for (var i = 0; i < Sizes.Length; i++)
		{
			if (Sizes[i] < minimumLength) continue;
			return Buckets[i].TryTake(out var buffer) ? buffer : new byte[Sizes[i]];
		}
		return new byte[minimumLength];
	}

	public static void Return(byte[] buffer)
	{
		if (buffer == null) return;
		for (var i = 0; i < Sizes.Length; i++)
		{
			if (buffer.Length != Sizes[i]) continue;
			if (Buckets[i].Count < MaxRetainedPerBucket) Buckets[i].Add(buffer);
			return;
		}
	}
}

[StructLayout(LayoutKind.Explicit)]
internal struct FloatBits
{
	[FieldOffset(0)] public float Float;
	[FieldOffset(0)] public uint UInt;
}

/// <summary>Little-endian binary writer over a pooled, growable buffer.</summary>
public sealed class NetWriter : IDisposable
{
	private static readonly UTF8Encoding Utf8 = new(false, true);
	private byte[] buffer;
	private int position;

	public NetWriter(int capacity = 256)
	{
		buffer = BufferPool.Rent(Math.Max(16, capacity));
	}

	public int Length => position;
	public byte[] RawBuffer => buffer;

	public void Dispose()
	{
		BufferPool.Return(buffer);
		buffer = null;
	}

	public void Reset() => position = 0;

	private void Ensure(int extra)
	{
		if (buffer == null) throw new ObjectDisposedException(nameof(NetWriter));
		var needed = (long)position + extra;
		if (needed <= buffer.Length) return;
		if (needed > int.MaxValue) throw new InvalidOperationException("NetWriter overflow.");
		long size = buffer.Length;
		while (size < needed) size *= 2;
		var bigger = BufferPool.Rent((int)Math.Min(size, int.MaxValue));
		Buffer.BlockCopy(buffer, 0, bigger, 0, position);
		BufferPool.Return(buffer);
		buffer = bigger;
	}

	public int Reserve(int count)
	{
		Ensure(count);
		var offset = position;
		Array.Clear(buffer, offset, count);
		position += count;
		return offset;
	}

	public void PatchUInt32(int offset, uint value)
	{
		buffer[offset] = (byte)value;
		buffer[offset + 1] = (byte)(value >> 8);
		buffer[offset + 2] = (byte)(value >> 16);
		buffer[offset + 3] = (byte)(value >> 24);
	}

	public void PatchUInt16(int offset, ushort value)
	{
		buffer[offset] = (byte)value;
		buffer[offset + 1] = (byte)(value >> 8);
	}

	public void PatchByte(int offset, byte value) => buffer[offset] = value;

	public void WriteByte(byte value)
	{
		Ensure(1);
		buffer[position++] = value;
	}

	public void WriteSByte(sbyte value) => WriteByte((byte)value);
	public void WriteBool(bool value) => WriteByte(value ? (byte)1 : (byte)0);

	public void WriteUInt16(ushort value)
	{
		Ensure(2);
		buffer[position++] = (byte)value;
		buffer[position++] = (byte)(value >> 8);
	}

	public void WriteInt16(short value) => WriteUInt16((ushort)value);
	public void WriteChar(char value) => WriteUInt16(value);

	public void WriteUInt32(uint value)
	{
		Ensure(4);
		PatchUInt32(position, value);
		position += 4;
	}

	public void WriteInt32(int value) => WriteUInt32((uint)value);

	public void WriteUInt64(ulong value)
	{
		Ensure(8);
		PatchUInt32(position, (uint)value);
		PatchUInt32(position + 4, (uint)(value >> 32));
		position += 8;
	}

	public void WriteInt64(long value) => WriteUInt64((ulong)value);
	public void WriteSingle(float value) => WriteUInt32(new FloatBits { Float = value }.UInt);
	public void WriteDouble(double value) => WriteInt64(BitConverter.DoubleToInt64Bits(value));

	public void WriteDecimal(decimal value)
	{
		var bits = decimal.GetBits(value);
		for (var i = 0; i < 4; i++) WriteInt32(bits[i]);
	}

	/// <summary>Int32 byte count (-1 for null) followed by UTF-8 bytes.</summary>
	public void WriteString(string value)
	{
		if (value == null)
		{
			WriteInt32(-1);
			return;
		}
		var count = Utf8.GetByteCount(value);
		WriteInt32(count);
		Ensure(count);
		Utf8.GetBytes(value, 0, value.Length, buffer, position);
		position += count;
	}

	public void WriteBytes(byte[] value) => WriteBytes(value, 0, value.Length);

	public void WriteBytes(byte[] value, int offset, int count)
	{
		if (count == 0) return;
		Ensure(count);
		Buffer.BlockCopy(value, offset, buffer, position, count);
		position += count;
	}

	public byte[] ToArray()
	{
		var result = new byte[position];
		Buffer.BlockCopy(buffer, 0, result, 0, position);
		return result;
	}
}
