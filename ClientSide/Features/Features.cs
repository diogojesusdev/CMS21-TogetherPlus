using System;
using System.Collections.Generic;
using System.Linq;
using CMS21Together.ServerSide;
using CMS21Together.Shared;
using CMS21Together.Shared.Features;
using MelonLoader;

namespace CMS21Together.ClientSide.Features;

/// <summary>
/// The Phase 6b switches (plan §3.7): one <c>[TogetherPlus]</c> preference per feature. The host's preferences become
/// the session config and every player follows it; outside a session every feature is off (no patch in effect).
/// Features ported from Together Fixes by TogetherFixer (https://github.com/Stevii001100/Fix-For-CMS21-Together),
/// modified for Together+.
/// </summary>
public static class Features
{
	private const string Category = "TogetherPlus";

	private static readonly FeatureSwitchboard Board = new();
	private static readonly Dictionary<string, GameFeature> Modules = new();
	private static readonly Dictionary<string, MelonPreferences_Entry<bool>> Preferences = new();
	private static Dictionary<string, bool> session = FeatureConfig.AllOff();

	/// <summary>The session config (all off when there is no session).</summary>
	public static IReadOnlyDictionary<string, bool> Session => session;

	public static bool IsOn(string id) => Board.IsOn(id);

	/// <summary>In the session config, whether or not this game could turn it on (the host relays by this).</summary>
	public static bool InSession(string id) => session.TryGetValue(id, out var on) && on;

	public static void Initialize(IEnumerable<GameFeature> modules)
	{
		var category = MelonPreferences.CreateCategory(Category, "Together+");
		foreach (var info in FeatureCatalog.All)
			Preferences[info.Id] = category.CreateEntry(info.Id, info.DefaultOn, info.Id, info.Description, false, false, null);
		foreach (var module in modules)
		{
			Board.Register(module);
			Modules[module.Id] = module;
		}
		Log.Info($"[Features] {Modules.Count} features available: {string.Join(", ", Modules.Keys)}.");
		Lobby.SteamInviteFeature.Listen(Preferences[FeatureCatalog.SteamInvite].Value);
	}

	/// <summary>The host's own preferences, completed with the defaults.</summary>
	public static Dictionary<string, bool> HostConfig() =>
		FeatureConfig.FromPreferences(Preferences.ToDictionary(p => p.Key, p => p.Value.Value));

	/// <summary>The host starts a session with its preferences.</summary>
	public static void HostStarted() => Use(HostConfig(), "host");

	/// <summary>A player got the host's config.</summary>
	public static void HandleConfig(Packet packet) => Use(FeatureConfig.Read(packet), "the host");

	/// <summary>The session ended: every feature off.</summary>
	public static void SessionEnded() => Use(FeatureConfig.AllOff(), null);

	private static void Use(Dictionary<string, bool> config, string from)
	{
		session = config;
		Board.Apply(config);
		if (from != null)
			Log.Info($"[Features] Switches from {from}: on {string.Join(", ", config.Where(c => c.Value).Select(c => c.Key))}; " +
			         $"broken {string.Join(", ", Board.Registered.Where(id => Board.StateOf(id) == FeatureState.Broken))}.");
	}

	public static void Send(FeatureMessage message)
	{
		if (Client.Instance == null || !Client.Instance.isConnected) return;
		using var packet = new Packet((int)PacketTypes.feature);
		message.Write(packet);
		Client.Instance.SendData(packet, true);
	}

	public static void HandleMessage(Packet packet)
	{
		var message = FeatureMessage.Read(packet);
		var info = FeatureCatalog.At(message.Feature);
		if (info == null || !Modules.TryGetValue(info.Id, out var module) || !Board.IsOn(info.Id)) return;
		module.Deliver(message);
	}

	/// <summary>Every frame: messages that waited for the garage.</summary>
	public static void Pump()
	{
		Data.Garage.Tools.EngineStand.PumpSecondStand();
		Lobby.SteamInviteFeature.PumpJoin();
		Lobby.SteamInviteFeature.PumpPresence();
		var ready = Data.GameLoadHook.IsGameReady();
		foreach (var module in Modules.Values)
		{
			if (!Board.IsOn(module.Id)) continue;
			module.Flush();
			try
			{
				module.Always();
				if (ready) module.Tick();
			}
			catch (Exception ex)
			{
				Log.Error($"[Features] {module.Id} failed", ex);
			}
		}
	}

	// Host side.

	public static FeatureRelay CreateRelay(Server server) => new(
		InSession,
		(except, message) =>
		{
			using var packet = Pack(message);
			server.SendToAll(packet, true, except);
		},
		(slot, message) =>
		{
			using var packet = Pack(message);
			server.Send(slot, packet, true);
		});

	private static Packet Pack(FeatureMessage message)
	{
		var packet = new Packet((int)PacketTypes.feature);
		message.Write(packet);
		return packet;
	}

	/// <summary>A player joined: the session's switches, then the kept feature states.</summary>
	public static void SendSession(Server server, int slot)
	{
		using (var packet = new Packet((int)PacketTypes.features))
		{
			FeatureConfig.Write(packet, session);
			server.Send(slot, packet, true);
		}
		server.FeatureRelay?.SendStates(slot);
	}
}