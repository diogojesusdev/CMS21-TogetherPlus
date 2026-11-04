using System;
using System.Collections.Generic;
using System.Threading;

namespace CMS21Together.Shared.Net;

/// <summary>
/// One transport connection carrying wire frames (<see cref="TcpLink"/>, <see cref="SimLink"/>, the game's SteamLink).
/// <see cref="Send"/> and <see cref="Close"/> are thread-safe. <see cref="FrameReceived"/> fires on the link's
/// receive thread (TCP reader thread, or inside <see cref="Poll"/>); <see cref="Closed"/> fires exactly once.
/// </summary>
public abstract class NetLink
{
	private int closeRequested;
	private int closedRaised;
	private string closeReason;

	public event Action<NetLink, NetFrame> FrameReceived;
	public event Action<NetLink, string> Closed;

	public abstract string Description { get; }

	/// <summary>Remote Steam ID taken from the transport (0 when the transport has no authenticated identity).</summary>
	public virtual ulong SteamId => 0;

	/// <summary>False when the transport detects dead peers by itself (Steam, in-memory).</summary>
	public virtual bool NeedsHeartbeat => true;

	public bool IsClosing => Volatile.Read(ref closeRequested) != 0;
	public bool IsClosed => Volatile.Read(ref closedRaised) != 0;
	public string CloseReason => Volatile.Read(ref closeReason);

	public abstract void Start();

	/// <summary>Queues the wire frames of one message atomically.</summary>
	/// <param name="coalesceKey">Non-zero: an unreliable frame that replaces a pending one with the same key (see <see cref="SendQueue"/>).</param>
	public abstract EnqueueResult Send(IList<byte[]> frames, SendPriority priority, bool unreliable, long coalesceKey = 0);

	/// <summary>Idempotent. With <paramref name="flush"/>, queued normal-priority frames are delivered first (bounded wait).</summary>
	public void Close(string reason, bool flush = false)
	{
		if (Interlocked.Exchange(ref closeRequested, 1) != 0) return;
		Interlocked.CompareExchange(ref closeReason, reason, null);
		try
		{
			OnClose(reason, flush);
		}
		catch (Exception ex)
		{
			Log.Warn($"[Net] Error while closing {Description}: {ex.Message}");
			RaiseClosed(reason);
		}
	}

	/// <summary>Main-thread pump for transports without their own threads.</summary>
	public virtual void Poll() { }

	protected abstract void OnClose(string reason, bool flush);

	protected void RaiseFrame(NetFrame frame)
	{
		if (IsClosed) return;
		FrameReceived?.Invoke(this, frame);
	}

	/// <summary>Marks the link closed and fires <see cref="Closed"/> exactly once.</summary>
	protected void RaiseClosed(string reason)
	{
		Interlocked.Exchange(ref closeRequested, 1);
		Interlocked.CompareExchange(ref closeReason, reason ?? "Connection closed", null);
		if (Interlocked.Exchange(ref closedRaised, 1) != 0) return;
		try
		{
			Closed?.Invoke(this, CloseReason);
		}
		catch (Exception ex)
		{
			Log.Error($"[Net] Close handler for {Description} failed", ex);
		}
	}

	public override string ToString() => Description;
}
