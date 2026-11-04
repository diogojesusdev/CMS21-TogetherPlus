using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace CMS21Together.Shared.Net;

public sealed class SimOptions
{
	public static readonly SimOptions Perfect = new();

	public int LatencyMs { get; set; }
	public int JitterMs { get; set; }

	/// <summary>Probability that an unreliable frame is lost, or that a reliable one needs a retransmission.</summary>
	public double LossRate { get; set; }

	/// <summary>0 = unlimited.</summary>
	public long BytesPerSecond { get; set; }

	/// <summary>
	/// Frames wait in a <see cref="SendQueue"/> (like TCP and Steam) and go on the wire only when it is free, at
	/// <see cref="BytesPerSecond"/>; otherwise every frame is scheduled at once (an unbounded wire). Phase 7 movement soak.
	/// </summary>
	public bool Queued { get; set; }

	public int Seed { get; set; } = 1;
}

/// <summary>
/// In-memory link pair with simulated latency, jitter, loss and bandwidth. Delivery happens in <see cref="Poll"/>,
/// against an injectable clock, so tests and bots are deterministic. Reliable frames stay ordered (a "lost" one is
/// retransmitted after an extra round trip, delaying the frames behind it); unreliable frames can be dropped.
/// Also used in-game as the zero-latency loopback between the host's server and its own client.
/// </summary>
public sealed class SimLink : NetLink
{
	private readonly object sync;
	private readonly SimOptions options;
	private readonly Func<long> clock;
	private readonly Random random;
	private readonly string description;
	private readonly Queue<Pending> inbound = new();
	private SimLink peer;
	private long lastDue;
	private SendQueue outbound;
	private long wireFreeAt;
	private long remoteClosedAt = long.MaxValue;
	private string remoteCloseReason;

	private SimLink(object sync, SimOptions options, Func<long> clock, int seed, string description)
	{
		this.sync = sync;
		this.options = options;
		this.clock = clock;
		random = new Random(seed);
		this.description = description;
	}

	public static (SimLink a, SimLink b) CreatePair(SimOptions options = null, Func<long> clock = null, string name = "sim")
	{
		options ??= SimOptions.Perfect;
		if (clock == null)
		{
			var stopwatch = Stopwatch.StartNew();
			clock = () => stopwatch.ElapsedMilliseconds;
		}
		var sync = new object();
		var a = new SimLink(sync, options, clock, options.Seed, name + " A");
		var b = new SimLink(sync, options, clock, options.Seed * 7919 + 1, name + " B");
		a.peer = b;
		b.peer = a;
		return (a, b);
	}

	public override string Description => description;
	public override bool NeedsHeartbeat => HeartbeatRequired;

	/// <summary>Simulates a transport that needs heartbeats (to test timeouts).</summary>
	public bool HeartbeatRequired { get; set; }

	/// <summary>Stops delivering anything in both directions without closing (simulates a dead peer).</summary>
	public bool Frozen { get; set; }

	public int PendingInbound
	{
		get { lock (sync) return inbound.Count; }
	}

	public long FramesDropped { get; private set; }

	/// <summary>The send queue in <see cref="SimOptions.Queued"/> mode (null otherwise).</summary>
	public SendQueue Outbound => outbound;

	public override void Start() { }

	public override EnqueueResult Send(IList<byte[]> frames, SendPriority priority, bool unreliable, long coalesceKey = 0)
	{
		lock (sync)
		{
			if (IsClosing || peer.IsClosed || Frozen || peer.Frozen) return EnqueueResult.Dropped;
			if (options.Queued)
			{
				outbound ??= new SendQueue(clockMs: clock);
				return outbound.Enqueue(frames, priority, unreliable, coalesceKey);
			}
			var now = clock();
			foreach (var frame in frames)
			{
				if (unreliable && options.LossRate > 0 && random.NextDouble() < options.LossRate)
				{
					FramesDropped++;
					continue;
				}
				var delay = options.LatencyMs + (options.JitterMs > 0 ? random.Next(options.JitterMs + 1) : 0);
				if (!unreliable && options.LossRate > 0 && random.NextDouble() < options.LossRate) delay += 2 * Math.Max(options.LatencyMs, 1);
				var due = now + delay;
				if (options.BytesPerSecond > 0) due = Math.Max(due, peer.lastDue) + frame.Length * 1000L / options.BytesPerSecond;
				// Stream semantics: nothing overtakes an earlier frame.
				due = Math.Max(due, peer.lastDue);
				peer.lastDue = due;
				peer.inbound.Enqueue(new Pending(due, frame));
			}
			return EnqueueResult.Queued;
		}
	}

	/// <summary>Queued mode: moves frames from the send queue onto the wire while the wire is free.</summary>
	private void PumpOutbound()
	{
		lock (sync)
		{
			if (outbound == null || IsClosing || peer.IsClosed || Frozen || peer.Frozen) return;
			var now = clock();
			while (wireFreeAt <= now && outbound.TryDequeue(out var frame, out _))
			{
				var unreliable = (FrameCodec.PeekFlags(frame) & FrameFlags.Unreliable) != 0;
				var start = Math.Max(now, wireFreeAt);
				wireFreeAt = start + (options.BytesPerSecond > 0 ? frame.Length * 1000L / options.BytesPerSecond : 0);
				if (unreliable && options.LossRate > 0 && random.NextDouble() < options.LossRate)
				{
					FramesDropped++;
					continue;
				}
				var delay = options.LatencyMs + (options.JitterMs > 0 ? random.Next(options.JitterMs + 1) : 0);
				if (!unreliable && options.LossRate > 0 && random.NextDouble() < options.LossRate) delay += 2 * Math.Max(options.LatencyMs, 1);
				var due = Math.Max(wireFreeAt + delay, peer.lastDue);
				peer.lastDue = due;
				peer.inbound.Enqueue(new Pending(due, frame));
			}
		}
	}

	public override void Poll()
	{
		if (options.Queued)
		{
			PumpOutbound();
			peer?.PumpOutbound();
		}
		while (true)
		{
			byte[] frame;
			string closeReason = null;
			lock (sync)
			{
				if (IsClosed || Frozen) return;
				var now = clock();
				if (inbound.Count > 0 && inbound.Peek().Due <= now)
				{
					frame = inbound.Dequeue().Frame;
				}
				else
				{
					if (inbound.Count == 0 && remoteClosedAt <= now) closeReason = remoteCloseReason;
					frame = null;
				}
			}

			if (frame == null)
			{
				if (closeReason != null) RaiseClosed(closeReason);
				return;
			}

			NetFrame decoded;
			try
			{
				decoded = FrameCodec.Decode(frame, 0, frame.Length);
			}
			catch (NetFormatException ex)
			{
				Close("Protocol error: " + ex.Message);
				return;
			}
			RaiseFrame(decoded);
		}
	}

	protected override void OnClose(string reason, bool flush)
	{
		lock (sync)
		{
			// The peer still receives what is already in flight, then sees the connection end.
			peer.remoteClosedAt = Math.Max(clock(), peer.lastDue);
			peer.remoteCloseReason = "Connection closed by the other side.";
			inbound.Clear();
		}
		RaiseClosed(reason);
	}

	/// <summary>Injects raw bytes as if the peer had sent them (fuzzing).</summary>
	public void InjectInbound(byte[] wire)
	{
		lock (sync)
		{
			var due = Math.Max(clock(), lastDue);
			lastDue = due;
			inbound.Enqueue(new Pending(due, wire));
		}
	}

	private readonly struct Pending
	{
		public Pending(long due, byte[] frame)
		{
			Due = due;
			Frame = frame;
		}

		public long Due { get; }
		public byte[] Frame { get; }
	}
}
