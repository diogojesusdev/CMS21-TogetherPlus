using System;
using System.Collections.Generic;
using System.Linq;
using CMS21Together.Shared.Net;
using NUnit.Framework;

namespace CMS21Together.Tests;

[TestFixture]
public class FramingTests
{
	private static byte[] LogicalFrame(ushort type, byte[] payload) => FrameCodec.Encode(type, FrameFlags.None, 0, payload);

	private static List<NetFrame> Decode(IEnumerable<byte[]> wireFrames, Reassembler reassembler = null)
	{
		reassembler ??= new Reassembler();
		var decoder = new FrameDecoder();
		var raw = new List<NetFrame>();
		foreach (var frame in wireFrames) decoder.Push(frame, 0, frame.Length, raw);
		return raw.Select(reassembler.Accept).Where(f => f != null).ToList();
	}

	private static byte[] RandomBytes(int count, int seed)
	{
		var data = new byte[count];
		new Random(seed).NextBytes(data);
		return data;
	}

	[Test]
	public void HeaderLayoutIsStable()
	{
		var frame = FrameCodec.Encode(0x1234, FrameFlags.Unreliable, 0xA1B2C3D4, new byte[] { 9, 8 });
		Assert.That(frame, Is.EqualTo(new byte[] { 9, 0, 0, 0, 0x34, 0x12, 4, 0xD4, 0xC3, 0xB2, 0xA1, 9, 8 }));
	}

	[Test]
	public void SmallFramePassesThroughWithSequenceNumbers()
	{
		var encoder = new FrameEncoder();
		var output = new List<byte[]>();
		var logical = LogicalFrame(5, new byte[] { 1, 2, 3 });
		encoder.Encode(logical, logical.Length, false, output);
		encoder.Encode(logical, logical.Length, true, output);

		var frames = Decode(output);
		Assert.That(frames, Has.Count.EqualTo(2));
		Assert.That(frames[0].Type, Is.EqualTo(5));
		Assert.That(frames[0].Seq, Is.EqualTo(1));
		Assert.That(frames[1].Seq, Is.EqualTo(2));
		Assert.That(frames[1].Flags, Is.EqualTo(FrameFlags.Unreliable));
		Assert.That(frames[0].CopyPayload(), Is.EqualTo(new byte[] { 1, 2, 3 }));
	}

	[Test]
	public void DecoderHandlesSplitAndMergedReads()
	{
		var encoder = new FrameEncoder();
		var output = new List<byte[]>();
		for (var i = 0; i < 50; i++)
		{
			var logical = LogicalFrame((ushort)i, RandomBytes(i * 37, i));
			encoder.Encode(logical, logical.Length, false, output);
		}
		var stream = output.SelectMany(f => f).ToArray();

		var random = new Random(7);
		var decoder = new FrameDecoder();
		var frames = new List<NetFrame>();
		var position = 0;
		while (position < stream.Length)
		{
			var count = Math.Min(random.Next(1, 300), stream.Length - position);
			decoder.Push(stream, position, count, frames);
			position += count;
		}

		Assert.That(frames, Has.Count.EqualTo(50));
		for (var i = 0; i < 50; i++)
		{
			Assert.That(frames[i].Type, Is.EqualTo(i));
			Assert.That(frames[i].CopyPayload(), Is.EqualTo(RandomBytes(i * 37, i)));
		}
		Assert.That(decoder.BufferedBytes, Is.Zero);
	}

	[TestCase(0u)]
	[TestCase(3u)]
	[TestCase((uint)FrameCodec.MaxWireFrameLength + 1)]
	[TestCase(uint.MaxValue)]
	public void DecoderRejectsBadLengths(uint length)
	{
		var bytes = new byte[16];
		FrameCodec.PutUInt32(bytes, 0, length);
		Assert.Throws<NetFormatException>(() => new FrameDecoder().Push(bytes, 0, bytes.Length, new List<NetFrame>()));
	}

	[Test]
	public void CompressibleLargeMessageIsSentAsOneCompressedFrame()
	{
		var payload = Enumerable.Repeat((byte)'a', 500_000).ToArray();
		var encoder = new FrameEncoder();
		var output = new List<byte[]>();
		var logical = LogicalFrame(3, payload);
		encoder.Encode(logical, logical.Length, true, output);

		Assert.That(output, Has.Count.EqualTo(1));
		Assert.That(FrameCodec.PeekFlags(output[0]) & FrameFlags.Compressed, Is.EqualTo(FrameFlags.Compressed));
		Assert.That(FrameCodec.PeekFlags(output[0]) & FrameFlags.Unreliable, Is.EqualTo(FrameFlags.None), "large frames are always reliable");
		var frames = Decode(output);
		Assert.That(frames.Single().CopyPayload(), Is.EqualTo(payload));
		Assert.That(frames.Single().Flags, Is.EqualTo(FrameFlags.None));
	}

	[TestCase(true)]
	[TestCase(false)]
	public void IncompressibleLargeMessageIsChunkedAndReassembled(bool compression)
	{
		var payload = RandomBytes(1_000_000, 42);
		var encoder = new FrameEncoder { CompressionEnabled = compression };
		var output = new List<byte[]>();
		var logical = LogicalFrame(9, payload);
		encoder.Encode(logical, logical.Length, false, output);

		Assert.That(output.Count, Is.GreaterThan(10));
		Assert.That(output.All(f => f.Length <= FrameEncoder.ChunkThreshold), Is.True);
		var frames = Decode(output);
		Assert.That(frames, Has.Count.EqualTo(1));
		Assert.That(frames[0].Type, Is.EqualTo(9));
		Assert.That(frames[0].CopyPayload(), Is.EqualTo(payload));
	}

	[Test]
	public void InterleavedTransfersReassembleIndependently()
	{
		var encoder = new FrameEncoder { CompressionEnabled = false };
		var a = new List<byte[]>();
		var b = new List<byte[]>();
		var pa = RandomBytes(200_000, 1);
		var pb = RandomBytes(150_000, 2);
		encoder.Encode(LogicalFrame(1, pa), FrameCodec.HeaderSize + pa.Length, false, a);
		encoder.Encode(LogicalFrame(2, pb), FrameCodec.HeaderSize + pb.Length, false, b);
		var small = new List<byte[]>();
		encoder.Encode(LogicalFrame(3, new byte[] { 1 }), FrameCodec.HeaderSize + 1, false, small);

		var interleaved = new List<byte[]>();
		for (var i = 0; i < Math.Max(a.Count, b.Count); i++)
		{
			if (i < a.Count) interleaved.Add(a[i]);
			if (i == 1) interleaved.AddRange(small);
			if (i < b.Count) interleaved.Add(b[i]);
		}

		var frames = Decode(interleaved);
		Assert.That(frames.Select(f => (int)f.Type), Is.EqualTo(new[] { 3, 2, 1 }).Or.EqualTo(new[] { 3, 1, 2 }));
		Assert.That(frames.Single(f => f.Type == 1).CopyPayload(), Is.EqualTo(pa));
		Assert.That(frames.Single(f => f.Type == 2).CopyPayload(), Is.EqualTo(pb));
	}

	[Test]
	public void TamperedChunkFailsCrc()
	{
		var payload = RandomBytes(300_000, 5);
		var encoder = new FrameEncoder { CompressionEnabled = false };
		var output = new List<byte[]>();
		encoder.Encode(LogicalFrame(1, payload), FrameCodec.HeaderSize + payload.Length, false, output);
		output[2][FrameCodec.HeaderSize + FrameEncoder.ChunkHeaderSize + 10] ^= 0xFF;

		var ex = Assert.Throws<NetFormatException>(() => Decode(output));
		Assert.That(ex.Message, Does.Contain("CRC"));
	}

	[Test]
	public void DroppedOrReorderedChunkIsRejected()
	{
		var payload = RandomBytes(300_000, 6);
		var encoder = new FrameEncoder { CompressionEnabled = false };
		var output = new List<byte[]>();
		encoder.Encode(LogicalFrame(1, payload), FrameCodec.HeaderSize + payload.Length, false, output);
		output.RemoveAt(1);
		Assert.Throws<NetFormatException>(() => Decode(output));
	}

	[Test]
	public void ChunkFloodIsBounded()
	{
		var reassembler = new Reassembler();
		Assert.Throws<NetFormatException>(() =>
		{
			for (uint id = 1; id <= Reassembler.MaxPendingTransfers + 1; id++)
				reassembler.Accept(ForgedChunk(id, 1_000_000, 0, new byte[10]));
		});

		Assert.Throws<NetFormatException>(() =>
			new Reassembler().Accept(ForgedChunk(1, (uint)Reassembler.MaxPendingBytes + 1, 0, new byte[10])));
	}

	[Test]
	public void DecompressionBombIsRejected()
	{
		var bomb = NetCompression.TryCompress(new byte[FrameEncoder.MaxMessageSize + 1024], 0, FrameEncoder.MaxMessageSize + 1024);
		Assert.That(bomb, Is.Not.Null);
		var wire = FrameCodec.Encode(1, FrameFlags.Compressed, 1, bomb);
		Assert.Throws<NetFormatException>(() => Decode(new[] { wire }));
	}

	[Test]
	public void CorruptCompressedFrameIsRejected()
	{
		var wire = FrameCodec.Encode(1, FrameFlags.Compressed, 1, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 });
		Assert.Throws<NetFormatException>(() => Decode(new[] { wire }));
	}

	[Test]
	public void FuzzedStreamOnlyEverThrowsFormatErrors()
	{
		var random = new Random(99);
		for (var round = 0; round < 500; round++)
		{
			var decoder = new FrameDecoder();
			var reassembler = new Reassembler();
			var frames = new List<NetFrame>();
			try
			{
				for (var i = 0; i < 20; i++)
				{
					var data = new byte[random.Next(4, 64)];
					random.NextBytes(data);
					if (random.Next(3) == 0) FrameCodec.PutUInt32(data, 0, (uint)random.Next(7, 60));
					decoder.Push(data, 0, data.Length, frames);
					foreach (var frame in frames) reassembler.Accept(frame);
					frames.Clear();
				}
			}
			catch (NetFormatException)
			{
			}
		}
	}

	[Test]
	public void Crc32MatchesTheStandardCheckValue()
	{
		Assert.That(Crc32.Compute(System.Text.Encoding.ASCII.GetBytes("123456789")), Is.EqualTo(0xCBF43926u));
	}

	private static NetFrame ForgedChunk(uint transferId, uint total, uint offset, byte[] data)
	{
		var payload = new byte[FrameEncoder.ChunkHeaderSize + data.Length];
		FrameCodec.PutUInt32(payload, 0, transferId);
		FrameCodec.PutUInt32(payload, 4, total);
		FrameCodec.PutUInt32(payload, 8, offset);
		Buffer.BlockCopy(data, 0, payload, FrameEncoder.ChunkHeaderSize, data.Length);
		var wire = FrameCodec.Encode(NetFrameTypes.Chunk, FrameFlags.Chunk, 1, payload);
		return FrameCodec.Decode(wire, 0, wire.Length);
	}
}

[TestFixture]
public class SendQueueTests
{
	[Test]
	public void NormalTrafficGoesBeforeBulk()
	{
		var queue = new SendQueue();
		queue.Enqueue(new byte[10], SendPriority.Bulk);
		queue.Enqueue(new byte[20]);
		Assert.That(queue.TryDequeue(out var first, out _), Is.True);
		Assert.That(first, Has.Length.EqualTo(20));
		Assert.That(queue.TryDequeue(out var second, out _), Is.True);
		Assert.That(second, Has.Length.EqualTo(10));
		Assert.That(queue.TryDequeue(out _, out _), Is.False);
	}

	[Test]
	public void BulkIsRateLimited()
	{
		long now = 0;
		var queue = new SendQueue(bulkBytesPerSecond: 100_000, clockMs: () => now);
		for (var i = 0; i < 10; i++) queue.Enqueue(new byte[60_000], SendPriority.Bulk);

		var sent = 0;
		while (queue.TryDequeue(out _, out _)) sent++;
		Assert.That(sent, Is.EqualTo(2), "burst allowance is one chunk threshold");
		Assert.That(queue.TryDequeue(out _, out var retry), Is.False);
		Assert.That(retry, Is.InRange(1, 1000));

		now += 1000;
		var later = 0;
		while (queue.TryDequeue(out _, out _)) later++;
		Assert.That(later, Is.InRange(1, 3));
	}

	[Test]
	public void UnreliableFramesAreDroppedWhenBackedUp()
	{
		var queue = new SendQueue(unreliableDropThreshold: 100);
		Assert.That(queue.Enqueue(new byte[150]), Is.EqualTo(EnqueueResult.Queued));
		Assert.That(queue.Enqueue(new byte[10], SendPriority.Normal, true), Is.EqualTo(EnqueueResult.Dropped));
		Assert.That(queue.Enqueue(new byte[10]), Is.EqualTo(EnqueueResult.Queued));
	}

	[Test]
	public void OverflowRejectsTheWholeMessage()
	{
		var queue = new SendQueue(maxQueuedBytes: 100);
		Assert.That(queue.Enqueue(new[] { new byte[60], new byte[60] }, SendPriority.Normal, false), Is.EqualTo(EnqueueResult.Overflow));
		Assert.That(queue.Count, Is.Zero);
		Assert.That(queue.QueuedBytes, Is.Zero);
	}
}
