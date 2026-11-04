using System;
using System.Collections.Generic;

namespace CMS21Together.Shared.Net;

/// <summary>
/// Main-thread dispatch of game frames to handlers. One handler failure never takes down the session (audit A16):
/// the error is logged (rate-limited) and only a peer that keeps sending broken packets is reported as misbehaving.
/// </summary>
public sealed class PacketRouter
{
	public const int MaxHandlerErrors = 50;
	private const int MaxLoggedPerPeer = 5;

	private readonly Dictionary<int, Action<int, Packet>> handlers = new();
	private readonly Dictionary<int, int> errorCounts = new();
	private readonly Dictionary<int, int> unknownCounts = new();
	private readonly string name;

	public PacketRouter(string name) => this.name = name;

	/// <summary>Raised once per peer when it exceeded <see cref="MaxHandlerErrors"/> (the host kicks it).</summary>
	public event Action<int, string> PeerMisbehaved;

	public int HandlerCount => handlers.Count;

	public void Register(int id, Action<int, Packet> handler) => handlers[id] = handler ?? throw new ArgumentNullException(nameof(handler));

	public void Register(int id, Action<Packet> handler)
	{
		if (handler == null) throw new ArgumentNullException(nameof(handler));
		handlers[id] = (_, packet) => handler(packet);
	}

	public bool IsRegistered(int id) => handlers.ContainsKey(id);

	/// <summary>Returns true when a handler ran to completion.</summary>
	public bool Dispatch(int fromSlot, NetFrame frame)
	{
		if (frame == null) return false;
		if (!handlers.TryGetValue(frame.Type, out var handler))
		{
			if (Bump(unknownCounts, fromSlot) <= MaxLoggedPerPeer)
				Log.Warn($"[{name}] Ignoring unknown packet type {frame.Type} from #{fromSlot}.");
			return false;
		}

		try
		{
			using var packet = new Packet(frame);
			handler(fromSlot, packet);
			return true;
		}
		catch (Exception ex)
		{
			var count = Bump(errorCounts, fromSlot);
			if (count <= MaxLoggedPerPeer)
				Log.Error($"[{name}] Handler for packet {frame.Type} from #{fromSlot} failed", ex);
			if (count == MaxHandlerErrors)
				PeerMisbehaved?.Invoke(fromSlot, $"Too many invalid packets ({count}).");
			return false;
		}
	}

	public int ErrorCount(int slot) => errorCounts.TryGetValue(slot, out var count) ? count : 0;

	public void ResetPeer(int slot)
	{
		errorCounts.Remove(slot);
		unknownCounts.Remove(slot);
	}

	public void Clear()
	{
		handlers.Clear();
		errorCounts.Clear();
		unknownCounts.Clear();
	}

	private static int Bump(Dictionary<int, int> counts, int slot)
	{
		counts.TryGetValue(slot, out var count);
		counts[slot] = ++count;
		return count;
	}
}
