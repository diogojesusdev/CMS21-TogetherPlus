using System;
using System.Collections.Generic;
using CMS21Together.Shared.Net;

namespace CMS21Together.Shared.Features;

public enum FeatureMessageKind : byte
{
	/// <summary>Something happened; players apply it, late joiners don't need it.</summary>
	Event = 0,

	/// <summary>The current state of <c>Key</c>; the host keeps the last one and gives it to late joiners.</summary>
	State = 1,

	/// <summary>The state of <c>Key</c> is gone (for example the station is empty again).</summary>
	Clear = 2
}

/// <summary>One message of a Phase 6b feature: which feature, what kind, a key and the feature's own payload.</summary>
public sealed class FeatureMessage
{
	public const int MaxKeyLength = 128;
	/// <summary>Large enough for a full warehouse (Phase 6b); big messages are chunked by the net layer.</summary>
	public const int MaxData = 4 * 1024 * 1024;

	public ushort Feature;
	public FeatureMessageKind Kind;
	public string Key = string.Empty;
	public byte[] Data = Array.Empty<byte>();

	/// <summary>Who sent it (the host fills it in when it relays; 0 = the host itself).</summary>
	public int From;

	public void Write(Packet packet)
	{
		packet.Write((int)Feature);
		packet.Write((int)Kind);
		packet.Write(Key ?? string.Empty);
		packet.Write(From);
		packet.Write(Data.Length);
		if (Data.Length > 0) packet.Write(Data);
	}

	public static FeatureMessage Read(Packet packet)
	{
		var feature = packet.ReadInt();
		var kind = packet.ReadInt();
		var key = packet.Read<string>() ?? string.Empty;
		var from = packet.ReadInt();
		var length = packet.ReadInt();
		if (FeatureCatalog.At((ushort)feature) == null || feature < 0 || feature > ushort.MaxValue) throw new NetFormatException($"Unknown feature {feature}.");
		if (kind < 0 || kind > (int)FeatureMessageKind.Clear) throw new NetFormatException($"Feature message kind {kind}.");
		if (key.Length > MaxKeyLength) throw new NetFormatException("Feature key too long.");
		if (length < 0 || length > MaxData || length > packet.UnreadLength()) throw new NetFormatException($"Feature payload of {length} bytes.");
		return new FeatureMessage
		{
			Feature = (ushort)feature,
			Kind = (FeatureMessageKind)kind,
			Key = key,
			From = from,
			Data = length > 0 ? packet.ReadBytes(length) : Array.Empty<byte>()
		};
	}
}

/// <summary>
/// Host side of the feature channel (Phase 6b): a player's message goes to every other player, but only while the
/// feature is on in the session. The last state per (feature, key) is kept for players who join later. Main thread.
/// </summary>
public sealed class FeatureRelay
{
	public const int MaxStates = 2048;

	private readonly Func<string, bool> enabled;
	private readonly Action<int, FeatureMessage> sendAllExcept;
	private readonly Action<int, FeatureMessage> sendTo;
	private readonly Dictionary<(ushort, string), FeatureMessage> states = new();
	private readonly LinkedList<(ushort, string)> age = new();

	/// <param name="enabled">The session's switch of a feature id.</param>
	/// <param name="sendAllExcept">Send to every validated player except the slot.</param>
	/// <param name="sendTo">Send to one slot.</param>
	public FeatureRelay(Func<string, bool> enabled, Action<int, FeatureMessage> sendAllExcept, Action<int, FeatureMessage> sendTo)
	{
		this.enabled = enabled ?? throw new ArgumentNullException(nameof(enabled));
		this.sendAllExcept = sendAllExcept ?? throw new ArgumentNullException(nameof(sendAllExcept));
		this.sendTo = sendTo ?? throw new ArgumentNullException(nameof(sendTo));
	}

	public int Dropped { get; private set; }
	public int StateCount => states.Count;

	/// <summary>A message from <paramref name="slot"/>. Returns false when it was dropped (feature off).</summary>
	public bool Handle(int slot, FeatureMessage message)
	{
		var info = FeatureCatalog.At(message.Feature);
		if (info == null || !enabled(info.Id))
		{
			Dropped++;
			return false;
		}
		message.From = slot;
		var key = (message.Feature, message.Key ?? string.Empty);
		if (message.Kind == FeatureMessageKind.State)
		{
			// Phase 8 (assessment): an overwritten state is the newest again, and per-car states (many, short-lived)
			// go first when over the cap, so session-wide states (warehouse, parking rows) are never dropped.
			if (states.ContainsKey(key)) age.Remove(key);
			age.AddLast(key);
			states[key] = message;
			while (states.Count > MaxStates) Evict();
		}
		else if (message.Kind == FeatureMessageKind.Clear && states.Remove(key))
		{
			age.Remove(key);
		}
		sendAllExcept(slot, message);
		return true;
	}

	private void Evict()
	{
		var victim = age.First;
		for (var node = age.First; node != null; node = node.Next)
			if (CarOf(node.Value.Item2) >= 0)
			{
				victim = node;
				break;
			}
		states.Remove(victim.Value);
		age.Remove(victim);
	}

	/// <summary>The car loader a key belongs to (<c>car/{id}</c> or <c>car/{id}/...</c>), or -1.</summary>
	public static int CarOf(string key)
	{
		if (key == null || !key.StartsWith("car/", StringComparison.Ordinal)) return -1;
		var end = key.IndexOf('/', 4);
		var digits = end < 0 ? key.Substring(4) : key.Substring(4, end - 4);
		return int.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) ? id : -1;
	}

	/// <summary>
	/// The kept states of one car, to a player whose car was just sent again (late join, scene return, resync): the
	/// car's spawn data does not hold tuning, alignment or dyno results (Phase 8 assessment).
	/// </summary>
	public int SendCarStates(int slot, int car)
	{
		var sent = 0;
		foreach (var key in age)
		{
			if (CarOf(key.Item2) != car) continue;
			var message = states[key];
			var info = FeatureCatalog.At(message.Feature);
			if (info == null || !enabled(info.Id)) continue;
			sendTo(slot, message);
			sent++;
		}
		return sent;
	}

	/// <summary>A car was deleted: its states must not stick to the next car in that loader.</summary>
	public int ForgetCar(int car)
	{
		var gone = new List<(ushort, string)>();
		foreach (var key in age)
			if (CarOf(key.Item2) == car)
				gone.Add(key);
		foreach (var key in gone)
		{
			states.Remove(key);
			age.Remove(key);
		}
		return gone.Count;
	}

	/// <summary>Every kept state of features still on, to a player who just joined.</summary>
	public int SendStates(int slot)
	{
		var sent = 0;
		foreach (var key in age)
		{
			var message = states[key];
			var info = FeatureCatalog.At(message.Feature);
			if (info == null || !enabled(info.Id)) continue;
			sendTo(slot, message);
			sent++;
		}
		return sent;
	}

	public void Clear()
	{
		states.Clear();
		age.Clear();
	}
}