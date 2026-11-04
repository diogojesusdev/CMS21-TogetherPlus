using System;
using System.Collections.Generic;

namespace CMS21Together.Shared.Locks;

/// <summary>Client side of the lock protocol around a <see cref="ClientLockTable"/>. Main thread only.</summary>
public sealed class LockClient
{
	private readonly Action<Packet> send;
	private readonly List<LockCommand> commands = new();

	public LockClient(int self, Func<long> clock, Action<Packet> send)
	{
		this.send = send ?? throw new ArgumentNullException(nameof(send));
		Table = new ClientLockTable(self, clock);
	}

	public ClientLockTable Table { get; }

	/// <summary>Raised with the holder's slot when the server refuses one of this player's requests.</summary>
	public event Action<int> Denied;

	/// <summary>Claims <paramref name="keys"/> (or keeps the current claim alive). Returns the new request token or 0.</summary>
	public int Begin(params string[] keys)
	{
		var token = Table.Begin(keys);
		if (token <= 0) return token;
		using var packet = new Packet((int)PacketTypes.lockRequest);
		LockWire.WriteRequest(packet, token, keys);
		send(packet);
		return token;
	}

	/// <summary>Token for a state write of <paramref name="key"/>; also counts as activity on it.</summary>
	public int Stamp(string key)
	{
		Table.Touch(key);
		return Table.TokenFor(key);
	}

	public void HandleReply(Packet packet)
	{
		var (token, status, holder) = LockWire.ReadReply(packet);
		var denier = Table.OnReply(token, status, holder);
		if (denier != 0) Denied?.Invoke(denier);
	}

	public void HandleState(Packet packet)
	{
		var (key, holder) = LockWire.ReadState(packet);
		Table.OnState(key, holder);
	}

	public void HandleSnapshot(Packet packet) => Table.OnSnapshot(LockWire.ReadSnapshot(packet));

	/// <summary>Sends due renewals and releases.</summary>
	public void Pump()
	{
		Table.Pump(commands);
		if (commands.Count == 0) return;
		foreach (var command in commands)
		{
			using var packet = new Packet((int)(command.Kind == LockCommandKind.Renew ? PacketTypes.lockRenew : PacketTypes.lockRelease));
			LockWire.WriteToken(packet, command.Token);
			send(packet);
		}
		commands.Clear();
	}
}
