using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using CMS21Together.Shared.Net;
using Steamworks;
using Steamworks.Data;

namespace CMS21Together.Shared.Steam;

/// <summary>
/// One Steam relay connection as a <see cref="NetLink"/>. Every Steam message is exactly one wire frame.
/// Facepunch callbacks and <c>Receive</c> run on the main thread; sends are queued and drained in <see cref="Poll"/>,
/// so a full Steam send buffer (LimitExceeded) delays frames instead of losing them.
/// </summary>
public sealed class SteamLink : NetLink
{
	private readonly object sync = new();
	private readonly SendQueue queue = new();
	private readonly Connection connection;
	private readonly ulong steamId;
	private readonly string description;
	private byte[] pending;
	private bool ready;
	private bool handleClosed;

	public SteamLink(Connection connection, ulong steamId, string description, bool ready)
	{
		this.connection = connection;
		this.steamId = steamId;
		this.description = description;
		this.ready = ready;
	}

	public override string Description => description;
	public override ulong SteamId => steamId;
	public override bool NeedsHeartbeat => false;

	public override void Start() { }

	public override EnqueueResult Send(IList<byte[]> frames, SendPriority priority, bool unreliable, long coalesceKey = 0)
	{
		if (IsClosing) return EnqueueResult.Dropped;
		return queue.Enqueue(frames, priority, unreliable, coalesceKey);
	}

	public override void Poll()
	{
		if (!IsClosing) Drain();
	}

	internal void MarkReady()
	{
		lock (sync) ready = true;
	}

	/// <summary>Called by the socket when Steam reports the connection ended (audit A22: the handle is always released).</summary>
	internal void OnRemoteClosed(string reason, bool closeHandle)
	{
		lock (sync)
		{
			if (closeHandle) CloseHandle(false);
			else handleClosed = true;
		}
		RaiseClosed(reason);
	}

	internal void OnMessage(IntPtr data, int size)
	{
		if (IsClosing) return;
		if (size < FrameCodec.HeaderSize || size > FrameCodec.MaxWireFrameLength)
		{
			Close($"Protocol error: invalid Steam message size {size}.");
			return;
		}

		var buffer = new byte[size];
		Marshal.Copy(data, buffer, 0, size);
		NetFrame frame;
		try
		{
			frame = FrameCodec.Decode(buffer, 0, size);
		}
		catch (NetFormatException ex)
		{
			Close("Protocol error: " + ex.Message);
			return;
		}
		RaiseFrame(frame);
	}

	protected override void OnClose(string reason, bool flush)
	{
		if (flush)
		{
			queue.ClearBulk();
			Drain();
		}
		lock (sync)
		{
			if (!handleClosed && flush) connection.Flush();
			CloseHandle(flush);
			queue.Clear();
			pending = null;
		}
		RaiseClosed(reason);
	}

	private void CloseHandle(bool linger)
	{
		if (handleClosed) return;
		handleClosed = true;
		connection.Close(linger, 0, "Closing Connection");
	}

	private void Drain()
	{
		string failure = null;
		lock (sync)
		{
			if (!ready || handleClosed) return;
			while (true)
			{
				if (pending == null && !queue.TryDequeue(out pending, out _)) break;
				var unreliable = (FrameCodec.PeekFlags(pending) & FrameFlags.Unreliable) != 0;
				var result = connection.SendMessage(pending, unreliable ? SendType.Unreliable : SendType.Reliable);
				if (result == Result.OK || unreliable)
				{
					pending = null;
					continue;
				}
				if (result == Result.LimitExceeded) break;
				failure = $"Steam send failed ({result}).";
				break;
			}
		}
		if (failure != null) Close(failure);
	}
}

/// <summary>Host side Steam relay socket. Creates one <see cref="SteamLink"/> per connection and hands it to the host.</summary>
public sealed class SteamServerSocket : SocketManager
{
	private readonly Dictionary<uint, SteamLink> links = new();
	private int unknownMessages;

	/// <summary>Raised on the main thread when a connection is established.</summary>
	public Action<SteamLink> LinkConnected;

	public int LinkCount => links.Count;

	public override void OnConnected(Connection connection, ConnectionInfo info)
	{
		base.OnConnected(connection, info);
		ulong remote = info.Identity.SteamId.Value;
		var id = connection.Id;
		var link = new SteamLink(connection, remote, $"steam {remote}", true);
		link.Closed += (_, _) => links.Remove(id);
		links[id] = link;
		try
		{
			LinkConnected?.Invoke(link);
		}
		catch (Exception ex)
		{
			Log.Error("[Steam] Accepting a connection failed", ex);
			link.Close("Server error.");
		}
	}

	public override void OnDisconnected(Connection connection, ConnectionInfo info)
	{
		if (links.TryGetValue(connection.Id, out var link))
		{
			links.Remove(connection.Id);
			// The base call releases the handle.
			link.OnRemoteClosed(SteamTransport.DescribeEnd(info), closeHandle: false);
		}
		base.OnDisconnected(connection, info);
	}

	public override void OnMessage(Connection connection, NetIdentity identity, IntPtr data, int size, long messageNum, long recvTime, int channel)
	{
		try
		{
			if (links.TryGetValue(connection.Id, out var link)) link.OnMessage(data, size);
			else if (++unknownMessages <= 5) Log.Warn($"[Steam] Dropping a message from unknown connection {connection.Id}.");
		}
		catch (Exception ex)
		{
			Log.Error("[Steam] Receive failed", ex);
		}
	}

	public void Shutdown(string reason)
	{
		foreach (var link in new List<SteamLink>(links.Values)) link.Close(reason, flush: true);
		links.Clear();
		Close();
	}
}

/// <summary>Client side Steam relay connection, forwarding everything to its <see cref="SteamLink"/>.</summary>
public sealed class SteamClientConnection : ConnectionManager
{
	public SteamLink Link { get; private set; }

	public SteamLink Attach(ulong hostSteamId)
	{
		Link = new SteamLink(Connection, hostSteamId, $"steam host {hostSteamId}", Connected);
		return Link;
	}

	public override void OnConnected(ConnectionInfo info)
	{
		base.OnConnected(info);
		Link?.MarkReady();
	}

	public override void OnDisconnected(ConnectionInfo info)
	{
		base.OnDisconnected(info);
		Link?.OnRemoteClosed(SteamTransport.DescribeEnd(info), closeHandle: true);
	}

	public override void OnMessage(IntPtr data, int size, long messageNum, long recvTime, int channel)
	{
		try
		{
			Link?.OnMessage(data, size);
		}
		catch (Exception ex)
		{
			Log.Error("[Steam] Receive failed", ex);
		}
	}
}

public static class SteamTransport
{
	internal static string DescribeEnd(ConnectionInfo info) =>
		info.State == ConnectionState.ClosedByPeer
			? "Connection closed by the other side."
			: $"Steam connection lost ({info.EndReason}).";
}
