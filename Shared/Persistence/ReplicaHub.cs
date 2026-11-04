using System;
using System.Collections.Generic;
using System.Linq;
using CMS21Together.Shared.Net;

namespace CMS21Together.Shared.Persistence;

/// <summary>
/// Sends and receives save bundles in small, rate-limited chunks (plan §3.5, client save replicas). Works in both
/// directions: host to players after each save, and a player to the host for a restore. The sender waits for the
/// receiver to accept, keeps at most <see cref="Window"/> chunks unacknowledged, and stays under
/// <see cref="BytesPerSecond"/> per peer. A receiver that is loading a scene holds its acks, so nothing is sent
/// while it loads. A completed bundle is accepted only if its SHA-256 and contents verify. Main thread only.
/// </summary>
public sealed class ReplicaHub
{
	public const int ChunkSize = 16 * 1024;
	public const int Window = 4;
	public const int DefaultBytesPerSecond = 128 * 1024;
	public const int DefaultMaxConcurrentSends = 2;

	/// <summary>A transfer that makes no progress for this long (while not paused) is abandoned.</summary>
	public const long StallTimeoutMs = 180_000;

	private readonly Func<long> clock;
	private readonly Action<int, Packet, bool> send;
	private readonly Dictionary<int, Outgoing> outgoing = new();
	private readonly Dictionary<int, Incoming> incoming = new();
	private readonly List<int> scratch = new();
	private int nextTransferId;

	/// <param name="send">Sends a packet to a peer; the flag asks for the bulk (low-priority) lane.</param>
	public ReplicaHub(Func<long> clockMs, Action<int, Packet, bool> send, int bytesPerSecond = DefaultBytesPerSecond,
		int maxConcurrentSends = DefaultMaxConcurrentSends)
	{
		clock = clockMs ?? throw new ArgumentNullException(nameof(clockMs));
		this.send = send ?? throw new ArgumentNullException(nameof(send));
		BytesPerSecond = bytesPerSecond;
		MaxConcurrentSends = maxConcurrentSends;
		nextTransferId = new Random().Next(1, 1 << 20);
	}

	public int BytesPerSecond { get; set; }
	public int MaxConcurrentSends { get; set; }

	/// <summary>Decides whether to take a bundle a peer announces: Continue accepts, anything else declines.</summary>
	public Func<int, ReplicaBegin, ReplicaStatus> Accept { get; set; }

	/// <summary>A verified bundle arrived: peer, decoded bundle, raw bytes.</summary>
	public event Action<int, SaveBundle, byte[]> Received;

	/// <summary>A send ended: peer, save generation, how it ended.</summary>
	public event Action<int, long, ReplicaStatus> SendFinished;

	public bool IsSending(int peer) => outgoing.ContainsKey(peer);
	public bool IsReceiving(int peer) => incoming.ContainsKey(peer);
	public int ActiveSends => outgoing.Values.Count(o => o.Started);

	/// <summary>Queues a bundle for a peer, replacing whatever was queued or in flight to it.</summary>
	public void Send(int peer, byte[] bundle)
	{
		if (bundle == null) throw new ArgumentNullException(nameof(bundle));
		if (!SaveBundle.TryReadMeta(bundle, false, out var meta, out var error)) throw new ArgumentException(error, nameof(bundle));
		if (outgoing.TryGetValue(peer, out var old)) Abort(peer, old, ReplicaStatus.Refused, "replaced by a newer save");
		if (++nextTransferId <= 0) nextTransferId = 1;
		outgoing[peer] = new Outgoing(nextTransferId, meta, bundle, Sha256Hex.Of(bundle));
	}

	public void CancelSend(int peer)
	{
		if (outgoing.TryGetValue(peer, out var o)) Abort(peer, o, ReplicaStatus.Refused, "cancelled");
	}

	/// <summary>Forgets a peer that left (no packets are sent to it).</summary>
	public void PeerLeft(int peer)
	{
		if (outgoing.TryGetValue(peer, out var o))
		{
			outgoing.Remove(peer);
			Finished(peer, o, ReplicaStatus.Failed);
		}
		incoming.Remove(peer);
	}

	public void Clear()
	{
		outgoing.Clear();
		incoming.Clear();
	}

	/// <param name="pauseSending">Send no chunks (this side is loading a scene or sending a join snapshot).</param>
	/// <param name="holdAcks">Acknowledge nothing (this side is loading: the senders wait).</param>
	public void Pump(bool pauseSending, bool holdAcks)
	{
		var now = clock();
		PumpOutgoing(now, pauseSending);
		PumpIncoming(now, holdAcks);
	}

	public void HandleBegin(int peer, Packet packet)
	{
		var begin = ReplicaWire.ReadBegin(packet);
		incoming.Remove(peer);
		var decision = ReplicaStatus.Refused;
		try
		{
			decision = Accept?.Invoke(peer, begin) ?? ReplicaStatus.Refused;
		}
		catch (Exception ex)
		{
			Log.Error("[Replicas] Deciding about an incoming save failed", ex);
		}
		if (decision != ReplicaStatus.Continue)
		{
			SendAck(peer, begin.TransferId, 0, decision == ReplicaStatus.AlreadyHave ? ReplicaStatus.AlreadyHave : ReplicaStatus.Refused);
			return;
		}
		incoming[peer] = new Incoming(begin, clock());
	}

	public void HandleChunk(int peer, Packet packet)
	{
		var (transferId, index, data) = ReplicaWire.ReadChunk(packet);
		if (!incoming.TryGetValue(peer, out var transfer) || transfer.Begin.TransferId != transferId) return;

		var begin = transfer.Begin;
		var offset = (long)index * begin.ChunkSize;
		var expected = (int)Math.Min(begin.ChunkSize, begin.Length - offset);
		if (index != transfer.Next || expected <= 0 || data.Length != expected)
		{
			incoming.Remove(peer);
			SendAck(peer, transferId, transfer.Next, ReplicaStatus.Failed);
			throw new NetFormatException($"Replica chunk {index} ({data.Length} bytes) does not fit (expected {transfer.Next}, {expected} bytes).");
		}
		Buffer.BlockCopy(data, 0, transfer.Buffer, (int)offset, data.Length);
		transfer.Next++;
		transfer.LastMs = clock();
		transfer.AckDue = true;
		if (transfer.Next < begin.ChunkCount) return;

		incoming.Remove(peer);
		Complete(peer, transfer);
	}

	public void HandleCancel(int peer, Packet packet)
	{
		var transferId = ReplicaWire.ReadCancel(packet);
		if (incoming.TryGetValue(peer, out var transfer) && transfer.Begin.TransferId == transferId)
		{
			incoming.Remove(peer);
			Log.Debug($"[Replicas] Peer #{peer} cancelled its save transfer.");
		}
	}

	public void HandleAck(int peer, Packet packet)
	{
		var (transferId, next, status) = ReplicaWire.ReadAck(packet);
		if (!outgoing.TryGetValue(peer, out var o) || !o.Started || o.TransferId != transferId) return;
		if (status != ReplicaStatus.Continue)
		{
			outgoing.Remove(peer);
			Finished(peer, o, status);
			return;
		}
		o.Accepted = true;
		if (next > o.Acked) o.Acked = Math.Min(next, o.ChunkCount);
		o.LastProgressMs = clock();
	}

	private void Complete(int peer, Incoming transfer)
	{
		var begin = transfer.Begin;
		string error = null;
		SaveBundle bundle = null;
		if (Sha256Hex.Of(transfer.Buffer) != begin.BundleSha256)
			error = "it does not match its SHA-256";
		else if (!SaveBundle.TryDecode(transfer.Buffer, out bundle, out error))
			error = "it does not verify: " + error;
		else if (bundle.Meta.SaveId != begin.SaveId || bundle.Meta.Generation != begin.Generation)
			error = "its contents are not the announced save";

		if (error != null)
		{
			Log.Warn($"[Replicas] Rejected the save copy from #{peer} (generation {begin.Generation}): {error}.");
			SendAck(peer, begin.TransferId, transfer.Next, ReplicaStatus.Failed);
			return;
		}
		SendAck(peer, begin.TransferId, transfer.Next, ReplicaStatus.Done);
		try
		{
			Received?.Invoke(peer, bundle, transfer.Buffer);
		}
		catch (Exception ex)
		{
			Log.Error("[Replicas] Storing a received save failed", ex);
		}
	}

	private void PumpOutgoing(long now, bool paused)
	{
		if (outgoing.Count == 0) return;
		scratch.Clear();
		scratch.AddRange(outgoing.Keys);
		var active = outgoing.Values.Count(o => o.Started);
		foreach (var peer in scratch)
		{
			var o = outgoing[peer];
			if (!o.Started)
			{
				if (paused || active >= MaxConcurrentSends) continue;
				o.Started = true;
				o.LastProgressMs = now;
				o.LastRefillMs = now;
				active++;
				var begin = new ReplicaBegin
				{
					TransferId = o.TransferId,
					SaveId = o.Meta.SaveId,
					Generation = o.Meta.Generation,
					Length = o.Bundle.Length,
					BundleSha256 = o.Sha,
					ChunkSize = ChunkSize
				};
				using var packet = new Packet((int)PacketTypes.replicaBegin);
				ReplicaWire.WriteBegin(packet, begin);
				send(peer, packet, true);
				continue;
			}

			if (paused)
			{
				// Waiting on our own load is not the receiver's fault.
				o.LastProgressMs = now;
				o.LastRefillMs = now;
				continue;
			}
			if (now - o.LastProgressMs > StallTimeoutMs)
			{
				Abort(peer, o, ReplicaStatus.Failed, "no progress");
				continue;
			}
			if (!o.Accepted) continue;

			var rate = Math.Max(1, BytesPerSecond);
			o.Tokens = Math.Min(2.0 * ChunkSize, o.Tokens + (now - o.LastRefillMs) * rate / 1000.0);
			o.LastRefillMs = now;
			while (o.NextToSend < o.ChunkCount && o.NextToSend < o.Acked + Window)
			{
				var offset = o.NextToSend * ChunkSize;
				var length = Math.Min(ChunkSize, o.Bundle.Length - offset);
				if (o.Tokens < length) break;
				var data = new byte[length];
				Buffer.BlockCopy(o.Bundle, offset, data, 0, length);
				using var packet = new Packet((int)PacketTypes.replicaChunk);
				ReplicaWire.WriteChunk(packet, o.TransferId, o.NextToSend, data);
				send(peer, packet, true);
				o.Tokens -= length;
				o.NextToSend++;
			}
		}
	}

	private void PumpIncoming(long now, bool holdAcks)
	{
		if (incoming.Count == 0) return;
		scratch.Clear();
		scratch.AddRange(incoming.Keys);
		foreach (var peer in scratch)
		{
			var transfer = incoming[peer];
			if (holdAcks)
			{
				transfer.LastMs = now;
				continue;
			}
			if (!transfer.Accepted || transfer.AckDue)
			{
				transfer.Accepted = true;
				transfer.AckDue = false;
				SendAck(peer, transfer.Begin.TransferId, transfer.Next, ReplicaStatus.Continue);
			}
			else if (now - transfer.LastMs > StallTimeoutMs)
			{
				incoming.Remove(peer);
				Log.Warn($"[Replicas] The save transfer from #{peer} stalled; dropped.");
			}
		}
	}

	private void Abort(int peer, Outgoing o, ReplicaStatus status, string why)
	{
		outgoing.Remove(peer);
		if (o.Started)
		{
			using var packet = new Packet((int)PacketTypes.replicaCancel);
			ReplicaWire.WriteCancel(packet, o.TransferId);
			send(peer, packet, true);
		}
		Log.Debug($"[Replicas] Save transfer to #{peer} stopped: {why}.");
		Finished(peer, o, status);
	}

	private void Finished(int peer, Outgoing o, ReplicaStatus status)
	{
		try
		{
			SendFinished?.Invoke(peer, o.Meta.Generation, status);
		}
		catch (Exception ex)
		{
			Log.Error("[Replicas] A transfer listener failed", ex);
		}
	}

	private void SendAck(int peer, int transferId, int next, ReplicaStatus status)
	{
		using var packet = new Packet((int)PacketTypes.replicaAck);
		ReplicaWire.WriteAck(packet, transferId, next, status);
		send(peer, packet, false);
	}

	private sealed class Outgoing
	{
		public Outgoing(int transferId, BundleMeta meta, byte[] bundle, string sha)
		{
			TransferId = transferId;
			Meta = meta;
			Bundle = bundle;
			Sha = sha;
			ChunkCount = (bundle.Length + ChunkSize - 1) / ChunkSize;
		}

		public int TransferId { get; }
		public BundleMeta Meta { get; }
		public byte[] Bundle { get; }
		public string Sha { get; }
		public int ChunkCount { get; }
		public bool Started;
		public bool Accepted;
		public int NextToSend;
		public int Acked;
		public double Tokens;
		public long LastRefillMs;
		public long LastProgressMs;
	}

	private sealed class Incoming
	{
		public Incoming(ReplicaBegin begin, long now)
		{
			Begin = begin;
			Buffer = new byte[begin.Length];
			LastMs = now;
		}

		public ReplicaBegin Begin { get; }
		public byte[] Buffer { get; }
		public int Next;
		public bool Accepted;
		public bool AckDue;
		public long LastMs;
	}
}