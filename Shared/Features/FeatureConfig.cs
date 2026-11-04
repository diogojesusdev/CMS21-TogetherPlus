using System.Collections.Generic;
using CMS21Together.Shared.Net;

namespace CMS21Together.Shared.Features;

/// <summary>
/// The session's switches (Phase 6b). The host decides: its own preferences become the session config, and every
/// player follows it. A switch the host did not send is off.
/// </summary>
public static class FeatureConfig
{
	/// <summary>The host's preferences completed with defaults for anything missing; unknown names are dropped.</summary>
	public static Dictionary<string, bool> FromPreferences(IReadOnlyDictionary<string, bool> preferences)
	{
		var config = FeatureCatalog.Defaults();
		if (preferences == null) return config;
		foreach (var pair in preferences)
			if (FeatureCatalog.Find(pair.Key) != null)
				config[pair.Key] = pair.Value;
		return config;
	}

	/// <summary>What a player runs: exactly the host's switches; anything the host did not send is off.</summary>
	public static Dictionary<string, bool> FollowHost(IReadOnlyDictionary<string, bool> host)
	{
		var config = new Dictionary<string, bool>();
		foreach (var info in FeatureCatalog.All)
			config[info.Id] = host != null && host.TryGetValue(info.Id, out var on) && on;
		return config;
	}

	/// <summary>Everything off (no session).</summary>
	public static Dictionary<string, bool> AllOff()
	{
		var config = new Dictionary<string, bool>();
		foreach (var info in FeatureCatalog.All) config[info.Id] = false;
		return config;
	}

	public static void Write(Packet packet, IReadOnlyDictionary<string, bool> config)
	{
		var on = new List<string>();
		foreach (var pair in config)
			if (pair.Value)
				on.Add(pair.Key);
		packet.Write(on.Count);
		foreach (var id in on) packet.Write(id);
	}

	public static Dictionary<string, bool> Read(Packet packet)
	{
		var count = packet.ReadInt();
		if (count < 0 || count > 256) throw new NetFormatException($"Feature config with {count} entries.");
		var on = new Dictionary<string, bool>();
		for (var i = 0; i < count; i++)
		{
			var id = packet.Read<string>();
			if (id == null || id.Length > 64) throw new NetFormatException("Feature name too long.");
			if (FeatureCatalog.Find(id) != null) on[id] = true;
		}
		return FollowHost(on);
	}
}