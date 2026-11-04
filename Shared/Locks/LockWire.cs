using System.Collections.Generic;
using CMS21Together.Shared.Net;

namespace CMS21Together.Shared.Locks;

/// <summary>Payload layouts of the lock packets, shared by the game and the headless harness.</summary>
public static class LockWire
{
	/// <summary>Upper bound for a snapshot read (8 players x 32 grants, with headroom).</summary>
	public const int MaxSnapshot = 1024;

	public static void WriteRequest(Packet packet, int token, IList<string> keys)
	{
		packet.Write(token);
		packet.Write(keys.Count);
		foreach (var key in keys) packet.Write(key);
	}

	public static (int token, List<string> keys) ReadRequest(Packet packet)
	{
		var token = packet.ReadInt();
		var count = packet.ReadInt();
		if (count < 1 || count > ResourceLockManager.MaxKeysPerRequest) throw new NetFormatException($"Lock request with {count} keys.");
		var keys = new List<string>(count);
		for (var i = 0; i < count; i++) keys.Add(packet.Read<string>());
		return (token, keys);
	}

	/// <summary>Renew and release carry only the token.</summary>
	public static void WriteToken(Packet packet, int token) => packet.Write(token);

	public static int ReadToken(Packet packet) => packet.ReadInt();

	public static void WriteReply(Packet packet, int token, LockStatus status, int holder)
	{
		packet.Write(token);
		packet.Write((int)status);
		packet.Write(holder);
	}

	public static (int token, LockStatus status, int holder) ReadReply(Packet packet)
	{
		var token = packet.ReadInt();
		var status = packet.ReadInt();
		if (status < 0 || status > (int)LockStatus.Invalid) throw new NetFormatException($"Unknown lock status {status}.");
		return (token, (LockStatus)status, packet.ReadInt());
	}

	public static void WriteState(Packet packet, string key, int holder)
	{
		packet.Write(key);
		packet.Write(holder);
	}

	public static (string key, int holder) ReadState(Packet packet) => (packet.Read<string>(), packet.ReadInt());

	public static void WriteSnapshot(Packet packet, IList<KeyValuePair<string, int>> holders)
	{
		packet.Write(holders.Count);
		foreach (var pair in holders)
		{
			packet.Write(pair.Key);
			packet.Write(pair.Value);
		}
	}

	public static List<KeyValuePair<string, int>> ReadSnapshot(Packet packet)
	{
		var count = packet.ReadInt();
		if (count < 0 || count > MaxSnapshot) throw new NetFormatException($"Lock snapshot with {count} entries.");
		var result = new List<KeyValuePair<string, int>>(count);
		for (var i = 0; i < count; i++)
		{
			var key = packet.Read<string>();
			result.Add(new KeyValuePair<string, int>(key, packet.ReadInt()));
		}
		return result;
	}

	/// <summary>Fencing token appended to part and body writes; older senders omit it (treated as 0).</summary>
	public static int ReadOptionalToken(Packet packet) => packet.UnreadLength() >= 4 ? packet.ReadInt() : 0;
}
