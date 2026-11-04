using System;
using CMS21Together.Shared.Net;

namespace CMS21Together.Shared;

/// <summary>
/// One game message.
/// Send mode (<see cref="Packet(int)"/>) writes into a pooled buffer that already reserves the frame header,
/// so the transport encodes it without another copy. Receive mode reads a bounds-checked payload.
/// <see cref="Write{T}"/> stores an int32 length followed by the <see cref="NetSerializer"/> payload; there is
/// no BinaryFormatter anywhere (audit A1), and <see cref="Read{T}"/> rejects a value that does not consume
/// exactly its length, which catches a writer/reader type mismatch instead of silently misreading.
/// </summary>
public sealed class Packet : IDisposable
{
	private NetWriter writer;
	private NetReader reader;
	private readonly int payloadLength;
	private bool disposed;

	public Packet(int id)
	{
		if (id < 0 || id >= NetFrameTypes.Chunk) throw new ArgumentOutOfRangeException(nameof(id), id, "Packet id outside the game range.");
		Id = id;
		writer = new NetWriter();
		writer.Reserve(FrameCodec.HeaderSize);
		FrameCodec.WriteHeader(writer.RawBuffer, (ushort)id, FrameFlags.None, 0, 0);
	}

	public Packet(NetFrame frame)
	{
		if (frame == null) throw new ArgumentNullException(nameof(frame));
		Id = frame.Type;
		reader = frame.CreateReader();
		payloadLength = frame.PayloadLength;
	}

	public Packet(int id, byte[] payload)
	{
		Id = id;
		reader = new NetReader(payload ?? Array.Empty<byte>());
		payloadLength = payload?.Length ?? 0;
	}

	public int Id { get; }

	/// <summary>Frame bytes (header + payload) of a packet being written; valid until <see cref="Dispose"/>.</summary>
	public byte[] FrameBuffer => Writer.RawBuffer;

	public int FrameLength => Writer.Length;

	public int Length() => reader != null ? payloadLength : Writer.Length - FrameCodec.HeaderSize;

	public int UnreadLength() => reader?.Remaining ?? 0;

	/// <summary>Copy of the payload written so far (tests and diagnostics).</summary>
	public byte[] PayloadToArray()
	{
		var result = new byte[Length()];
		if (reader != null) Buffer.BlockCopy(reader.Buffer, reader.Position - (payloadLength - reader.Remaining), result, 0, result.Length);
		else Buffer.BlockCopy(Writer.RawBuffer, FrameCodec.HeaderSize, result, 0, result.Length);
		return result;
	}

	public void Dispose()
	{
		if (disposed) return;
		disposed = true;
		writer?.Dispose();
		writer = null;
		reader = null;
	}

	private NetWriter Writer
	{
		get
		{
			if (disposed) throw new ObjectDisposedException(nameof(Packet));
			return writer ?? throw new InvalidOperationException("Packet is in read mode.");
		}
	}

	private NetReader Reader
	{
		get
		{
			if (disposed) throw new ObjectDisposedException(nameof(Packet));
			return reader ?? throw new InvalidOperationException("Packet is in write mode.");
		}
	}

	#region Write/Read

	/// <summary>Raw little-endian int32 (no length prefix).</summary>
	public void Write(int value) => Writer.WriteInt32(value);

	/// <summary>Raw bytes (no length prefix).</summary>
	public void Write(byte[] value)
	{
		if (value == null) throw new ArgumentNullException(nameof(value));
		Writer.WriteBytes(value);
	}

	/// <summary>Length-prefixed value serialized with its declared (compile-time) type.</summary>
	public void Write<T>(T value)
	{
		var w = Writer;
		var lengthAt = w.Reserve(4);
		NetSerializer.Write(w, value);
		w.PatchUInt32(lengthAt, (uint)(w.Length - lengthAt - 4));
	}

	public int ReadInt() => Reader.ReadInt32();

	public byte[] ReadBytes(int length) => Reader.ReadBytes(length);

	public T Read<T>()
	{
		var r = Reader;
		var length = r.ReadInt32();
		if (length < 0 || length > r.Remaining)
			throw new NetFormatException($"Invalid length {length} for {typeof(T).Name} ({r.Remaining} bytes left).");
		var value = new NetReader(r.Buffer, r.Position, length);
		var result = NetSerializer.Read<T>(value);
		if (value.Remaining != 0)
			throw new NetFormatException($"{typeof(T).Name} left {value.Remaining} of {length} bytes unread (writer/reader type mismatch).");
		r.Skip(length);
		return result;
	}

	#endregion
}
