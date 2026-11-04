using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace CMS21Together.Shared.Net;

/// <summary>
/// TCP transport. A reader thread decodes frames as they arrive (so pings are answered even while the game's
/// main thread is stuck loading a scene) and a writer thread drains the <see cref="SendQueue"/>, one frame at a
/// time, so concurrent sends can never interleave on the stream (audit A3).
/// </summary>
public sealed class TcpLink : NetLink
{
	public const int DefaultConnectTimeoutMs = 10000;
	public const int FlushTimeoutMs = 3000;
	private const int SocketBufferSize = 256 * 1024;
	private const int WriteBatchBytes = 64 * 1024;

	private readonly object sync = new();
	private readonly SendQueue queue = new();
	private readonly AutoResetEvent signal = new(false);
	private readonly ManualResetEvent readerDone = new(false);
	private readonly string host;
	private readonly int port;
	private readonly int connectTimeoutMs;
	private readonly string description;
	private Socket socket;
	private bool finished;
	private volatile bool flushing;
	private Timer flushTimer;

	/// <summary>Wraps an accepted socket.</summary>
	public TcpLink(Socket connected)
	{
		socket = connected ?? throw new ArgumentNullException(nameof(connected));
		description = "tcp " + SafeEndPoint(connected);
	}

	/// <summary>Connects to <paramref name="host"/> on a background thread when started.</summary>
	public TcpLink(string host, int port, int connectTimeoutMs = DefaultConnectTimeoutMs)
	{
		this.host = host ?? throw new ArgumentNullException(nameof(host));
		this.port = port;
		this.connectTimeoutMs = connectTimeoutMs;
		description = $"tcp {host}:{port}";
	}

	public override string Description => description;

	public long QueuedBytes => queue.QueuedBytes;

	public override void Start()
	{
		new Thread(Run) { IsBackground = true, Name = "Together+ " + description }.Start();
	}

	public override EnqueueResult Send(IList<byte[]> frames, SendPriority priority, bool unreliable, long coalesceKey = 0)
	{
		if (IsClosing) return EnqueueResult.Dropped;
		var result = queue.Enqueue(frames, priority, unreliable, coalesceKey);
		if (result == EnqueueResult.Queued) signal.Set();
		return result;
	}

	protected override void OnClose(string reason, bool flush)
	{
		if (!flush)
		{
			Finish(reason);
			return;
		}
		queue.ClearBulk();
		flushing = true;
		flushTimer = new Timer(_ => Finish(reason), null, FlushTimeoutMs + 1000, Timeout.Infinite);
		signal.Set();
	}

	private void Run()
	{
		Socket connected;
		try
		{
			connected = socket ?? ConnectSocket();
			Configure(connected);
		}
		catch (Exception ex)
		{
			Finish(IsClosing ? CloseReason : $"Could not connect to {host}:{port} ({Describe(ex)}).");
			return;
		}

		lock (sync)
		{
			if (finished)
			{
				SafeClose(connected);
				return;
			}
			socket = connected;
		}

		new Thread(WriteLoop) { IsBackground = true, Name = "Together+ writer " + description }.Start();
		ReadLoop(connected);
	}

	private Socket ConnectSocket()
	{
		var addresses = IPAddress.TryParse(host, out var literal) ? new[] { literal } : Dns.GetHostAddresses(host);
		var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault()
			?? throw new SocketException((int)SocketError.HostNotFound);
		var s = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
		try
		{
			var pending = s.BeginConnect(address, port, null, null);
			if (!pending.AsyncWaitHandle.WaitOne(connectTimeoutMs)) throw new TimeoutException("timed out");
			s.EndConnect(pending);
			return s;
		}
		catch
		{
			SafeClose(s);
			throw;
		}
	}

	private static void Configure(Socket s)
	{
		s.NoDelay = true;
		s.ReceiveBufferSize = SocketBufferSize;
		s.SendBufferSize = SocketBufferSize;
		s.SendTimeout = 15000;
	}

	private void ReadLoop(Socket s)
	{
		var buffer = new byte[64 * 1024];
		var decoder = new FrameDecoder();
		var frames = new List<NetFrame>();
		var reason = "Connection closed by the other side.";
		try
		{
			while (true)
			{
				var read = s.Receive(buffer);
				if (read == 0) break;
				frames.Clear();
				decoder.Push(buffer, 0, read, frames);
				foreach (var frame in frames) RaiseFrame(frame);
			}
		}
		catch (NetFormatException ex)
		{
			reason = "Protocol error: " + ex.Message;
		}
		catch (Exception ex) when (ex is SocketException or ObjectDisposedException or IOException)
		{
			reason = IsClosing ? CloseReason : $"Connection lost ({Describe(ex)}).";
		}
		catch (Exception ex)
		{
			Log.Error($"[Net] Receive loop of {description} failed", ex);
			reason = "Connection error: " + ex.Message;
		}
		readerDone.Set();
		// During a flush-close the writer finishes once the peer has seen everything.
		if (!flushing) Finish(reason);
	}

	private void WriteLoop()
	{
		var batch = new byte[WriteBatchBytes];
		try
		{
			while (!IsClosed)
			{
				// Sampled before draining: Close enqueues its Bye before setting the flag, so the Bye is sent too.
				var flushNow = flushing;
				var length = 0;
				var retryAfter = 0;
				while (queue.TryDequeue(out var frame, out retryAfter))
				{
					if (length > 0 && length + frame.Length > batch.Length)
					{
						SendAll(batch, length);
						length = 0;
					}
					if (frame.Length > batch.Length)
					{
						SendAll(frame, frame.Length);
						continue;
					}
					Buffer.BlockCopy(frame, 0, batch, length, frame.Length);
					length += frame.Length;
				}
				if (length > 0) SendAll(batch, length);

				if (flushNow)
				{
					socket.Shutdown(SocketShutdown.Send);
					readerDone.WaitOne(FlushTimeoutMs);
					Finish(CloseReason);
					return;
				}
				signal.WaitOne(retryAfter > 0 ? retryAfter : 1000);
			}
		}
		catch (Exception ex) when (ex is SocketException or ObjectDisposedException or IOException)
		{
			Finish(IsClosing ? CloseReason : $"Connection lost ({Describe(ex)}).");
		}
		catch (Exception ex)
		{
			Log.Error($"[Net] Send loop of {description} failed", ex);
			Finish("Connection error: " + ex.Message);
		}
	}

	private void SendAll(byte[] data, int length)
	{
		var sent = 0;
		while (sent < length) sent += socket.Send(data, sent, length - sent, SocketFlags.None);
	}

	private void Finish(string reason)
	{
		Socket s;
		lock (sync)
		{
			if (finished) return;
			finished = true;
			s = socket;
		}
		SafeClose(s);
		flushTimer?.Dispose();
		queue.Clear();
		signal.Set();
		RaiseClosed(reason);
	}

	private static void SafeClose(Socket s)
	{
		if (s == null) return;
		try
		{
			s.Close();
		}
		catch (Exception)
		{
			// Already closed.
		}
	}

	private static string SafeEndPoint(Socket s)
	{
		try
		{
			return s.RemoteEndPoint?.ToString() ?? "?";
		}
		catch (Exception)
		{
			return "?";
		}
	}

	private static string Describe(Exception ex) => ex switch
	{
		SocketException se => se.SocketErrorCode.ToString(),
		TimeoutException => "timed out",
		ObjectDisposedException => "closed",
		IOException io when io.InnerException is SocketException inner => inner.SocketErrorCode.ToString(),
		_ => ex.Message
	};
}

/// <summary>Accepts TCP connections on a background thread and hands each one over as a <see cref="TcpLink"/>.</summary>
public sealed class TcpAcceptor : IDisposable
{
	private readonly Socket listener;
	private readonly Action<TcpLink> onAccept;
	private volatile bool stopped;

	public TcpAcceptor(int port, Action<TcpLink> onAccept, IPAddress bindAddress = null)
	{
		this.onAccept = onAccept ?? throw new ArgumentNullException(nameof(onAccept));
		listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
		try
		{
			listener.Bind(new IPEndPoint(bindAddress ?? IPAddress.Any, port));
			listener.Listen(16);
		}
		catch
		{
			listener.Close();
			throw;
		}
		Port = ((IPEndPoint)listener.LocalEndPoint).Port;
		new Thread(AcceptLoop) { IsBackground = true, Name = "Together+ TCP accept" }.Start();
	}

	public int Port { get; }

	private void AcceptLoop()
	{
		while (!stopped)
		{
			Socket accepted;
			try
			{
				accepted = listener.Accept();
			}
			catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
			{
				if (stopped) return;
				Log.Warn($"[Net] Accept failed: {ex.Message}");
				Thread.Sleep(100);
				continue;
			}

			if (stopped)
			{
				accepted.Close();
				return;
			}
			try
			{
				onAccept(new TcpLink(accepted));
			}
			catch (Exception ex)
			{
				Log.Error("[Net] Could not accept a TCP connection", ex);
				accepted.Close();
			}
		}
	}

	public void Dispose()
	{
		stopped = true;
		try
		{
			listener.Close();
		}
		catch (Exception)
		{
			// Already closed.
		}
	}
}
