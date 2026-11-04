using System;

namespace CMS21Together.Shared.Net;

/// <summary>Connection handshake and control frames.</summary>
public static class NetProtocol
{
	/// <summary>"CT+!" - rejects anything that is not a Together+ client before reading further.</summary>
	public const uint Magic = 0x212B5443;

	/// <summary>Bump on any wire-incompatible change; peers with another version are rejected with a clear message.</summary>
	public const ushort Version = 1;

	public const int HostKeyLength = 16;
	public const int MaxReasonLength = 256;
	public const int HandshakeTimeoutMs = 10000;
	public const int ClientConnectTimeoutMs = 20000;
	public const int PingIntervalMs = 1000;
	public const int TimeoutMs = 15000;

	/// <summary>A gap this long between two ticks means our own main thread stalled (loading a save or a scene).</summary>
	public const int StallThresholdMs = 3000;

	/// <summary>
	/// Tracks stalls of the local main thread. Pings are sent from the main thread, so when every player loads at
	/// the same time nobody pings anybody; silence is therefore measured from when we resumed, which gives each
	/// peer a full window to answer the pings we send now. A peer that is really gone still times out.
	/// </summary>
	public struct StallClock
	{
		private long lastTickMs;
		private long resumedAtMs;
		private bool started;

		public void Tick(long now)
		{
			if (started && now - lastTickMs > StallThresholdMs) resumedAtMs = now;
			if (!started) resumedAtMs = long.MinValue;
			started = true;
			lastTickMs = now;
		}

		public bool TimedOut(long now, long lastReceiveMs) => now - Math.Max(lastReceiveMs, resumedAtMs) > TimeoutMs;
	}

	public sealed class Hello
	{
		public ushort Protocol { get; set; }
		public string ModVersion { get; set; }
		public byte[] HostKey { get; set; }
	}

	public static byte[] EncodeHello(string modVersion, byte[] hostKey, ushort protocol = Version)
	{
		using var writer = new NetWriter();
		writer.WriteUInt32(Magic);
		writer.WriteUInt16(protocol);
		writer.WriteString(modVersion ?? "");
		var key = hostKey ?? Array.Empty<byte>();
		writer.WriteByte((byte)key.Length);
		writer.WriteBytes(key);
		return writer.ToArray();
	}

	public static Hello DecodeHello(NetFrame frame)
	{
		var reader = frame.CreateReader();
		if (reader.ReadUInt32() != Magic) throw new NetFormatException("Not a Together+ client.");
		var hello = new Hello { Protocol = reader.ReadUInt16() };
		// Older protocols may lay out the rest differently: stop here so they get a clean version mismatch.
		if (hello.Protocol != Version) return hello;
		hello.ModVersion = reader.ReadString(64) ?? "";
		var keyLength = reader.ReadByte();
		if (keyLength != 0 && keyLength != HostKeyLength) throw new NetFormatException($"Invalid host key length {keyLength}.");
		hello.HostKey = reader.ReadBytes(keyLength);
		return hello;
	}

	public static byte[] EncodeWelcome(int slotId, int maxPlayers)
	{
		using var writer = new NetWriter();
		writer.WriteInt32(slotId);
		writer.WriteInt32(maxPlayers);
		return writer.ToArray();
	}

	public static (int slotId, int maxPlayers) DecodeWelcome(NetFrame frame)
	{
		var reader = frame.CreateReader();
		var slot = reader.ReadInt32();
		var max = reader.ReadInt32();
		if (max < 1 || max > 64 || slot < 1 || slot > max) throw new NetFormatException($"Invalid welcome (slot {slot} of {max}).");
		return (slot, max);
	}

	public static byte[] EncodeReason(string reason)
	{
		using var writer = new NetWriter();
		reason ??= "";
		writer.WriteString(reason.Length > MaxReasonLength ? reason.Substring(0, MaxReasonLength) : reason);
		return writer.ToArray();
	}

	public static string DecodeReason(NetFrame frame)
	{
		var reader = frame.CreateReader();
		var reason = reader.ReadString(MaxReasonLength * 4);
		return string.IsNullOrEmpty(reason) ? null : reason;
	}

	public static byte[] EncodeTimestamp(long ms)
	{
		using var writer = new NetWriter(8);
		writer.WriteInt64(ms);
		return writer.ToArray();
	}

	public static long DecodeTimestamp(NetFrame frame) => frame.CreateReader().ReadInt64();

	/// <summary>Constant-time comparison for the host key.</summary>
	public static bool KeyEquals(byte[] a, byte[] b)
	{
		if (a == null || b == null || a.Length != b.Length || a.Length == 0) return false;
		var diff = 0;
		for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
		return diff == 0;
	}
}
