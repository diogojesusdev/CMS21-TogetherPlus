using System;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace CMS21Together.Shared.Net;

public enum NetClientState
{
	Idle,
	Connecting,
	Connected,
	Closed
}

/// <summary>
/// Client side of the transport. <see cref="Connected"/>, <see cref="FrameReceived"/> and <see cref="Disconnected"/>
/// fire inside <see cref="Tick"/> on the main thread. <see cref="Disconnected"/> fires once, and only when the
/// connection ended for another reason than a call to <see cref="Disconnect"/>.
/// </summary>
public sealed class NetClient
{
	private readonly ConcurrentQueue<NetEvent> inbox = new();
	private readonly Func<long> clock;
	private readonly string modVersion;
	private long connectDeadline;
	private NetProtocol.StallClock stall;

	public NetClient(string modVersion, Func<long> clock = null)
	{
		this.modVersion = modVersion ?? "";
		if (clock == null)
		{
			var stopwatch = Stopwatch.StartNew();
			clock = () => stopwatch.ElapsedMilliseconds;
		}
		this.clock = clock;
	}

	public NetConnection Connection { get; private set; }
	public NetClientState State { get; private set; }
	public int SlotId { get; private set; }
	public int MaxPlayers { get; private set; }
	public string DisconnectReason { get; private set; }
	public int RttMs => Connection?.RttMs ?? -1;
	public ulong ServerSteamId => Connection?.SteamId ?? 0;

	public event Action Connected;
	public event Action<NetFrame> FrameReceived;
	public event Action<string> Disconnected;

	public void Connect(NetLink link, byte[] hostKey = null)
	{
		if (State is NetClientState.Connecting or NetClientState.Connected) throw new InvalidOperationException("Already connected.");
		while (inbox.TryDequeue(out _)) { }
		SlotId = 0;
		MaxPlayers = 0;
		DisconnectReason = null;
		Connection = new NetConnection(link, clock, inbox.Enqueue);
		State = NetClientState.Connecting;
		var now = clock();
		connectDeadline = now + NetProtocol.ClientConnectTimeoutMs;
		Connection.NextPingMs = now + NetProtocol.PingIntervalMs;
		link.Start();
		Connection.SendControl(NetFrameTypes.Hello, NetProtocol.EncodeHello(modVersion, hostKey));
	}

	public EnqueueResult Send(byte[] frame, int length, SendPriority priority = SendPriority.Normal, bool unreliable = false, long coalesceKey = 0)
	{
		var connection = Connection;
		if (State != NetClientState.Connected || connection == null || connection.LocalClose) return EnqueueResult.Dropped;
		return connection.Send(frame, length, priority, unreliable, coalesceKey);
	}

	/// <summary>Says goodbye (flushing what is queued) and closes. Does not raise <see cref="Disconnected"/>.</summary>
	public void Disconnect(string reason = "Left the session.")
	{
		if (State is NetClientState.Idle or NetClientState.Closed) return;
		State = NetClientState.Closed;
		DisconnectReason = reason;
		Connection?.Disconnect(reason);
	}

	public void Tick()
	{
		var connection = Connection;
		if (connection == null || State is NetClientState.Idle) return;
		if (State != NetClientState.Closed || !connection.Link.IsClosed)
		{
			try
			{
				connection.Link.Poll();
			}
			catch (Exception ex)
			{
				Log.Error("[Net] Client poll failed", ex);
				connection.Link.Close("Connection error: " + ex.Message);
			}
		}

		var now = clock();
		stall.Tick(now);
		while (inbox.TryDequeue(out var e))
		{
			if (e.Connection != Connection) continue;
			Dispatch(e);
		}
		if (State == NetClientState.Closed || connection.Link.IsClosing) return;

		if (State == NetClientState.Connecting && now > connectDeadline)
		{
			Fail(connection, "Could not join: the host did not answer in time.");
			return;
		}
		if (!connection.Link.NeedsHeartbeat) return;
		if (now >= connection.NextPingMs)
		{
			connection.SendPing(now);
			connection.NextPingMs = now + NetProtocol.PingIntervalMs;
		}
		if (State == NetClientState.Connected && stall.TimedOut(now, connection.LastReceiveMs))
			Fail(connection, "Connection to the host timed out.");
	}

	private void Dispatch(NetEvent e)
	{
		var connection = e.Connection;
		switch (e.Kind)
		{
			case NetEventKind.Frame:
				if (State == NetClientState.Closed || connection.LocalClose) return;
				if (State == NetClientState.Connecting)
				{
					if (e.Frame.Type != NetFrameTypes.Welcome)
					{
						Fail(connection, "Unexpected answer from the host.");
						return;
					}
					try
					{
						(SlotId, MaxPlayers) = NetProtocol.DecodeWelcome(e.Frame);
					}
					catch (NetFormatException ex)
					{
						Fail(connection, "Invalid answer from the host: " + ex.Message);
						return;
					}
					connection.SlotId = SlotId;
					connection.State = NetConnectionState.Connected;
					State = NetClientState.Connected;
					Log.Info($"[Net] Joined as player {SlotId} of {MaxPlayers}.");
					Raise(Connected);
					return;
				}
				if (NetFrameTypes.IsTransport(e.Frame.Type))
				{
					Fail(connection, "Unexpected control frame from the host.");
					return;
				}
				try
				{
					FrameReceived?.Invoke(e.Frame);
				}
				catch (Exception ex)
				{
					Log.Error("[Net] Client frame handler failed", ex);
				}
				return;

			case NetEventKind.Closed:
				connection.State = NetConnectionState.Closed;
				if (State == NetClientState.Closed) return;
				State = NetClientState.Closed;
				DisconnectReason = e.Reason;
				Log.Info($"[Net] Disconnected: {e.Reason}");
				try
				{
					Disconnected?.Invoke(e.Reason);
				}
				catch (Exception ex)
				{
					Log.Error("[Net] Disconnect handler failed", ex);
				}
				return;
		}
	}

	private void Fail(NetConnection connection, string reason)
	{
		connection.LocalClose = true;
		connection.Link.Close(reason);
		// The Closed event raises Disconnected with this reason on the next pass.
		while (inbox.TryDequeue(out var e))
			if (e.Kind == NetEventKind.Closed && e.Connection == connection)
				Dispatch(e);
	}

	private static void Raise(Action action)
	{
		try
		{
			action?.Invoke();
		}
		catch (Exception ex)
		{
			Log.Error("[Net] Connect handler failed", ex);
		}
	}
}
