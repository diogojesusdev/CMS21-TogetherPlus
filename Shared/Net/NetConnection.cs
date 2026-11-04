using System;
using System.Collections.Generic;
using System.Threading;

namespace CMS21Together.Shared.Net;

public enum NetConnectionState
{
	Handshaking,
	Connected,
	Closed
}

internal enum NetEventKind
{
	Accepted,
	Frame,
	Closed
}

internal readonly struct NetEvent
{
	private NetEvent(NetEventKind kind, NetConnection connection, NetFrame frame, string reason)
	{
		Kind = kind;
		Connection = connection;
		Frame = frame;
		Reason = reason;
	}

	public NetEventKind Kind { get; }
	public NetConnection Connection { get; }
	public NetFrame Frame { get; }
	public string Reason { get; }

	public static NetEvent Accepted(NetConnection connection) => new(NetEventKind.Accepted, connection, null, null);
	public static NetEvent Received(NetConnection connection, NetFrame frame) => new(NetEventKind.Frame, connection, frame, null);
	public static NetEvent Closed(NetConnection connection, string reason) => new(NetEventKind.Closed, connection, null, reason);
}

/// <summary>
/// One peer over one <see cref="NetLink"/>: sequencing, compression and chunking on send; reassembly, ping/pong and
/// Bye/Reject handling on the link's receive thread. Everything else is queued for the owner's main-thread Tick.
/// </summary>
public sealed class NetConnection
{
	private readonly FrameEncoder encoder = new();
	private readonly Reassembler reassembler = new();
	private readonly Func<long> clock;
	private readonly Action<NetEvent> sink;
	private long lastReceiveMs;
	private int rttMs = -1;
	private string remoteReason;

	internal NetConnection(NetLink link, Func<long> clock, Action<NetEvent> sink)
	{
		Link = link ?? throw new ArgumentNullException(nameof(link));
		this.clock = clock;
		this.sink = sink;
		lastReceiveMs = clock();
		link.FrameReceived += OnLinkFrame;
		link.Closed += OnLinkClosed;
	}

	public NetLink Link { get; }

	/// <summary>Player slot (1 = host) once the handshake is done; 0 before.</summary>
	public int SlotId { get; internal set; }

	/// <summary>Owner's view, changed on its main thread only.</summary>
	public NetConnectionState State { get; internal set; }

	public ulong SteamId => Link.SteamId;

	/// <summary>Last measured round trip in ms, or -1 before the first pong.</summary>
	public int RttMs => Volatile.Read(ref rttMs);

	public long LastReceiveMs => Interlocked.Read(ref lastReceiveMs);

	/// <summary>Reason sent by the other side in a Bye or Reject, if any.</summary>
	public string RemoteReason => Volatile.Read(ref remoteReason);

	/// <summary>Set when this side decided to drop the peer: its remaining frames are ignored.</summary>
	internal bool LocalClose { get; set; }

	internal long HandshakeDeadline { get; set; }
	internal long NextPingMs { get; set; }

	/// <summary>Owner data (the game stores per-peer state here).</summary>
	public object Tag { get; set; }

	public override string ToString() => SlotId > 0 ? $"#{SlotId} ({Link.Description})" : Link.Description;

	/// <summary>Sends one logical frame (header reserved at the start of <paramref name="frame"/>, as written by <c>Packet</c>).</summary>
	public EnqueueResult Send(byte[] frame, int length, SendPriority priority = SendPriority.Normal, bool unreliable = false, long coalesceKey = 0)
	{
		if (Link.IsClosing) return EnqueueResult.Dropped;
		var frames = new List<byte[]>(1);
		encoder.Encode(frame, length, unreliable, frames);
		var result = Link.Send(frames, priority, unreliable, coalesceKey);
		if (result == EnqueueResult.Overflow)
		{
			Log.Warn($"[Net] Send queue of {this} overflowed; disconnecting the peer.");
			Link.Close("Connection too slow (send queue overflow).");
		}
		return result;
	}

	internal EnqueueResult SendControl(ushort type, byte[] payload)
	{
		if (Link.IsClosing) return EnqueueResult.Dropped;
		return Link.Send(new[] { encoder.EncodeSingle(type, payload) }, SendPriority.Normal, false);
	}

	internal void SendPing(long now) => SendControl(NetFrameTypes.Ping, NetProtocol.EncodeTimestamp(now));

	/// <summary>Tells the peer why, then closes once queued frames are flushed.</summary>
	internal void Disconnect(string reason, ushort frameType = NetFrameTypes.Bye)
	{
		LocalClose = true;
		if (Link.IsClosing) return;
		SendControl(frameType, NetProtocol.EncodeReason(reason));
		Link.Close(reason, flush: true);
	}

	private void OnLinkFrame(NetLink link, NetFrame wire)
	{
		Interlocked.Exchange(ref lastReceiveMs, clock());
		NetFrame frame;
		try
		{
			frame = reassembler.Accept(wire);
			if (frame == null) return;
			switch (frame.Type)
			{
				case NetFrameTypes.Ping:
					SendControl(NetFrameTypes.Pong, frame.CopyPayload());
					return;
				case NetFrameTypes.Pong:
					Volatile.Write(ref rttMs, (int)Math.Min(int.MaxValue, Math.Max(0, clock() - NetProtocol.DecodeTimestamp(frame))));
					return;
				case NetFrameTypes.Bye:
				case NetFrameTypes.Reject:
					var reason = NetProtocol.DecodeReason(frame) ?? "Disconnected by the other side.";
					Volatile.Write(ref remoteReason, reason);
					link.Close(reason);
					return;
			}
		}
		catch (NetFormatException ex)
		{
			Log.Warn($"[Net] Dropping {this}: {ex.Message}");
			link.Close("Protocol error: " + ex.Message);
			return;
		}
		catch (Exception ex)
		{
			Log.Error($"[Net] Receive error on {this}", ex);
			link.Close("Connection error: " + ex.Message);
			return;
		}
		sink(NetEvent.Received(this, frame));
	}

	private void OnLinkClosed(NetLink link, string reason) => sink(NetEvent.Closed(this, RemoteReason ?? reason));
}
