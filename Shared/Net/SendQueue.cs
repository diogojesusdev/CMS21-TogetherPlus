using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace CMS21Together.Shared.Net;

public enum SendPriority
{
	Normal,

	/// <summary>Rate-limited background traffic (save replicas). Never delays normal traffic.</summary>
	Bulk
}

public enum EnqueueResult
{
	Queued,

	/// <summary>An unreliable frame was dropped because the link is backed up.</summary>
	Dropped,

	/// <summary>The queue is over its byte budget; the peer is too slow and must be disconnected.</summary>
	Overflow
}

/// <summary>
/// Per-connection outbound queue. Thread-safe: game code enqueues on the main thread, heartbeats on a timer,
/// and the transport drains it from its writer, one frame at a time (audit A3).
/// Phase 7: an unreliable frame with a coalesce key (a player's movement) replaces the frame with the same key that is
/// still waiting, in its place (latest wins), so a slow link never builds a backlog of stale positions: at most one
/// frame per key is ever pending.
/// </summary>
public sealed class SendQueue
{
	public const long DefaultMaxQueuedBytes = 128L * 1024 * 1024;
	public const long DefaultUnreliableDropThreshold = 256 * 1024;
	public const int DefaultBulkBytesPerSecond = 2 * 1024 * 1024;

	private readonly object sync = new();
	private readonly Queue<Entry> normal = new();
	private readonly Queue<Entry> bulk = new();
	private readonly Dictionary<long, Entry> pendingByKey = new();
	private readonly Func<long> clockMs;
	private readonly long maxQueuedBytes;
	private readonly long unreliableDropThreshold;
	private long queuedBytes;
	private double bulkTokens;
	private long lastRefillMs;

	public SendQueue(long maxQueuedBytes = DefaultMaxQueuedBytes, long unreliableDropThreshold = DefaultUnreliableDropThreshold,
		int bulkBytesPerSecond = DefaultBulkBytesPerSecond, Func<long> clockMs = null)
	{
		this.maxQueuedBytes = maxQueuedBytes;
		this.unreliableDropThreshold = unreliableDropThreshold;
		BulkBytesPerSecond = bulkBytesPerSecond;
		var stopwatch = Stopwatch.StartNew();
		this.clockMs = clockMs ?? (() => stopwatch.ElapsedMilliseconds);
		lastRefillMs = this.clockMs();
		bulkTokens = Math.Max(bulkBytesPerSecond, FrameEncoder.ChunkThreshold);
	}

	public int BulkBytesPerSecond { get; set; }

	public long QueuedBytes
	{
		get { lock (sync) return queuedBytes; }
	}

	public int Count
	{
		get { lock (sync) return normal.Count + bulk.Count; }
	}

	public bool HasBulkPending
	{
		get { lock (sync) return bulk.Count > 0; }
	}

	/// <summary>Keyed frames that replaced an older pending one (diagnostics and tests).</summary>
	public long Coalesced
	{
		get { lock (sync) return coalesced; }
	}

	private long coalesced;

	/// <summary>Queues all frames of one message atomically, so chunks of a transfer are never split by an overflow.</summary>
	/// <param name="coalesceKey">
	/// Non-zero for a single unreliable normal-priority frame that only matters as the latest of its kind: it replaces a
	/// pending frame with the same key, and is never dropped for backlog (at most one per key is pending).
	/// </param>
	public EnqueueResult Enqueue(IList<byte[]> frames, SendPriority priority, bool unreliable, long coalesceKey = 0)
	{
		long size = 0;
		foreach (var frame in frames) size += frame.Length;
		var keyed = coalesceKey != 0 && unreliable && priority == SendPriority.Normal && frames.Count == 1;
		lock (sync)
		{
			if (keyed && pendingByKey.TryGetValue(coalesceKey, out var waiting))
			{
				queuedBytes += size - waiting.Frame.Length;
				waiting.Frame = frames[0];
				coalesced++;
				return EnqueueResult.Queued;
			}
			if (unreliable && !keyed && queuedBytes > unreliableDropThreshold) return EnqueueResult.Dropped;
			if (queuedBytes + size > maxQueuedBytes) return EnqueueResult.Overflow;
			var target = priority == SendPriority.Bulk ? bulk : normal;
			foreach (var frame in frames)
			{
				var entry = new Entry(frame, keyed ? coalesceKey : 0);
				target.Enqueue(entry);
				if (keyed) pendingByKey[coalesceKey] = entry;
			}
			queuedBytes += size;
			return EnqueueResult.Queued;
		}
	}

	public EnqueueResult Enqueue(byte[] frame, SendPriority priority = SendPriority.Normal, bool unreliable = false, long coalesceKey = 0) =>
		Enqueue(new[] { frame }, priority, unreliable, coalesceKey);

	/// <summary>
	/// Next frame to write: normal traffic first, then bulk while the token bucket allows.
	/// When only throttled bulk frames remain, returns false and the delay before trying again.
	/// </summary>
	public bool TryDequeue(out byte[] frame, out int retryAfterMs)
	{
		retryAfterMs = 0;
		lock (sync)
		{
			if (normal.Count > 0)
			{
				frame = Take(normal);
				queuedBytes -= frame.Length;
				return true;
			}
			if (bulk.Count > 0)
			{
				Refill();
				if (bulkTokens > 0)
				{
					frame = Take(bulk);
					queuedBytes -= frame.Length;
					bulkTokens -= frame.Length;
					return true;
				}
				var rate = Math.Max(1, BulkBytesPerSecond);
				retryAfterMs = (int)Math.Min(1000, Math.Ceiling(-bulkTokens * 1000.0 / rate) + 1);
			}
			frame = null;
			return false;
		}
	}

	public void Clear()
	{
		lock (sync)
		{
			normal.Clear();
			bulk.Clear();
			pendingByKey.Clear();
			queuedBytes = 0;
		}
	}

	/// <summary>Drops pending bulk frames (used when closing: replicas are not worth delaying a disconnect).</summary>
	public void ClearBulk()
	{
		lock (sync)
		{
			foreach (var entry in bulk) queuedBytes -= entry.Frame.Length;
			bulk.Clear();
		}
	}

	private byte[] Take(Queue<Entry> queue)
	{
		var entry = queue.Dequeue();
		if (entry.Key != 0 && pendingByKey.TryGetValue(entry.Key, out var mapped) && mapped == entry) pendingByKey.Remove(entry.Key);
		return entry.Frame;
	}

	private sealed class Entry
	{
		public Entry(byte[] frame, long key)
		{
			Frame = frame;
			Key = key;
		}

		public byte[] Frame;
		public readonly long Key;
	}

	private void Refill()
	{
		var now = clockMs();
		var elapsed = now - lastRefillMs;
		if (elapsed <= 0) return;
		lastRefillMs = now;
		var capacity = Math.Max(BulkBytesPerSecond, FrameEncoder.ChunkThreshold);
		bulkTokens = Math.Min(capacity, bulkTokens + elapsed * BulkBytesPerSecond / 1000.0);
	}
}
