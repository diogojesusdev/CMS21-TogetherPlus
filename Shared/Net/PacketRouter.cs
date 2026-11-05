using System;
using System.Collections.Generic;
using System.Diagnostics;
using CMS21Together.Shared.Safety;

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
	private readonly LogThrottle trace = new(5, 5000);

	private static readonly string[] Names = Enum.GetNames(typeof(PacketTypes));

	/// <summary>Packets not logged on receipt: per-frame streams (movement, tools in motion, transfers, renewals).</summary>
	private static readonly bool[] Quiet = BuildQuiet();
	private static readonly long SlowTicks = Stopwatch.Frequency / 10;

	public PacketRouter(string name) => this.name = name;

	private static bool[] BuildQuiet()
	{
		var quiet = new bool[Names.Length];
		foreach (var type in new[]
		         {
			         PacketTypes.position, PacketTypes.rotation, PacketTypes.carPosition, PacketTypes.toolMove,
			         PacketTypes.engineStandAngle, PacketTypes.carEngineSound, PacketTypes.playerInCar,
			         PacketTypes.replicaChunk, PacketTypes.replicaAck, PacketTypes.lockRenew, PacketTypes.econBalance,
			         PacketTypes.invCheck, PacketTypes.carCheck
		         })
			quiet[(int)type] = true;
		return quiet;
	}

	private static string NameOf(int type) => type >= 0 && type < Names.Length ? Names[type] : type.ToString();

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

		// Breadcrumb before the handler: the last line before a native crash names the packet being applied.
		int type = frame.Type;
		if ((type >= Quiet.Length || !Quiet[type]) && Log.IsEnabled(LogLevel.Info) && trace.Allow(type, out var skipped))
			Log.Info($"[{name}] <- {NameOf(type)} from #{fromSlot} ({frame.PayloadLength} B){LogThrottle.Skipped(skipped)}");

		var started = Stopwatch.GetTimestamp();
		try
		{
			using var packet = new Packet(frame);
			handler(fromSlot, packet);
			var elapsed = Stopwatch.GetTimestamp() - started;
			if (elapsed > SlowTicks)
				Log.Warn($"[{name}] Handler for {NameOf(type)} from #{fromSlot} took {elapsed * 1000 / Stopwatch.Frequency} ms.");
			return true;
		}
		catch (Exception ex)
		{
			var count = Bump(errorCounts, fromSlot);
			if (count <= MaxLoggedPerPeer)
			{
				Log.Error($"[{name}] Handler for {NameOf(type)} ({type}, {frame.PayloadLength} B) from #{fromSlot} failed", ex);
				CrashReport.Capture($"{name} handler for {NameOf(type)} from #{fromSlot}", ex);
			}
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
		trace.Clear();
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
