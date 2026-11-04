// Based on LobbySteamInvite.cs and SteamInviteReceiver.cs from FixForTogether by TogetherFixer
// (https://github.com/Stevii001100/Fix-For-CMS21-Together), custom non-commercial licence.
// Modified for Together+: the host publishes the Steam "connect" rich presence, so Steam's own friends list offers
// "Join game" and "Invite to game"; joining waits for the main menu and uses the normal Steam join.
using System;
using CMS21Together.ClientSide.Data;
using CMS21Together.ClientSide.Data.NewUI;
using CMS21Together.ServerSide;
using CMS21Together.Shared;
using CMS21Together.Shared.Data;
using CMS21Together.Shared.Features;
using Steamworks;

namespace CMS21Together.ClientSide.Features.Lobby;

/// <summary>Steam invites: friends join the host's session from Steam.</summary>
public sealed class SteamInviteFeature : GameFeature
{
	private static string pendingJoin;
	private static bool listening;
	private static bool published;
	private static int shownPlayers = -1;

	public override string Id => FeatureCatalog.SteamInvite;

	protected override void Patch()
	{
	}

	protected override void Stopped()
	{
		if (!published) return;
		published = false;
		shownPlayers = -1;
		if (MainMod.IsSteamAvailable) SteamFriends.ClearRichPresence();
	}

	/// <summary>Host side, every frame: once the Steam session code exists, Steam friends see "Join game".</summary>
	public static void PumpPresence()
	{
		if (!MainMod.IsSteamAvailable || !Features.IsOn(FeatureCatalog.SteamInvite)) return;
		var server = Server.Instance;
		if (server == null || !server.isRunning || string.IsNullOrEmpty(server.serverID)) return;
		if (!published)
		{
			SteamFriends.SetRichPresence("connect", SteamConnect.Of(server.serverID));
			published = true;
			Log.Info("[SteamInvite] Friends can join from Steam.");
		}
		// Phase 7: the friends list shows how full the session is (n/8).
		var players = ServerSide.Data.ServerData.Instance?.connectedClients.Count ?? 0;
		if (players == shownPlayers) return;
		shownPlayers = players;
		var max = server.Host?.MaxPlayers ?? MainMod.Config.MaxPlayers;
		SteamFriends.SetRichPresence("status", $"Together+ {players}/{max}");
		SteamFriends.SetRichPresence("steam_player_group", server.serverID);
		SteamFriends.SetRichPresence("steam_player_group_size", players.ToString());
	}

	/// <summary>Player side, at start-up: follows the player's own switch (it works from the menu, before any session).</summary>
	public static void Listen(bool enabled)
	{
		if (!enabled || listening || !MainMod.IsSteamAvailable) return;
		listening = true;
		SteamFriends.OnGameRichPresenceJoinRequested += (_, connect) => Request(connect);
		try
		{
			Request(SteamApps.CommandLine);
		}
		catch (Exception ex)
		{
			Log.Debug($"[SteamInvite] No Steam launch command line: {ex.Message}");
		}
		if (pendingJoin == null) Request(string.Join(" ", Environment.GetCommandLineArgs()));
	}


	private static void Request(string connect)
	{
		var code = SteamConnect.Parse(connect);
		if (code == null) return;
		pendingJoin = code;
		Log.Info("[SteamInvite] Joining a friend's session from Steam.");
	}

	/// <summary>Every frame: joins once the main menu is up and no session runs.</summary>
	public static void PumpJoin()
	{
		if (pendingJoin == null || Client.Instance == null || Client.Instance.isConnected) return;
		if (Server.Instance != null && Server.Instance.isRunning) return;
		if (ClientData.UserData == null || UICore.MP_Main == null || SceneManager.CurrentScene() != GameScene.menu) return;
		var code = pendingJoin;
		pendingJoin = null;
		ClientData.UserData.selectedNetworkType = NetworkType.Steam;
		UIActions.StartClient(ClientData.UserData.username, code);
	}
}