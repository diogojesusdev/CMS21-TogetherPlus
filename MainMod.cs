using System;
using System.IO;
using CMS21Together.ClientSide;
using CMS21Together.ClientSide.Data;
using CMS21Together.ClientSide.Data.NewUI;
using CMS21Together.ClientSide.Data.Player;
using CMS21Together.ServerSide;
using CMS21Together.Shared;
using CMS21Together.Shared.Data;
using Il2CppSystem.Collections;
using MelonLoader;
using Steamworks;
using UnhollowerRuntimeLib;
using UnityEngine;

// ReSharper disable All

namespace CMS21Together
{
	public class MainMod : MelonMod
	{
		public const int MAX_SAVE_COUNT = 22;
		public const int PORT = 7777;
		public const string ASSEMBLY_MOD_VERSION = "0.4.16" + ASSEMBLY_HOTFIX_VERSION;
		public const string ASSEMBLY_HOTFIX_VERSION = "hf3";
		public const string PLUS_VERSION = "0.5.0-rc8";
		public const string MOD_VERSION = "Together+ " + PLUS_VERSION + " (Together " + ASSEMBLY_MOD_VERSION + ")";
		/// <summary>Sent in the handshake; host and clients must match exactly.</summary>
		public const string NET_VERSION = "TogetherPlus-" + PLUS_VERSION;
		public bool isModInitialized;
		public static bool IsSteamAvailable { get; private set; }

		/// <summary>Host settings from UserData/TogetherPlus.json (Phase 7: MaxPlayers, default 8).</summary>
		public static Shared.Net.ServerConfig Config { get; private set; } = new();

		public static bool isClosing;

		private void InitializeSteam()
		{
			string dllPath = Path.Combine(MelonUtils.UserLibsDirectory, "steam_api64.dll");

			if (!File.Exists(dllPath))
			{
				IsSteamAvailable = false;
				Log.Warn("Steam DLL not found in UserLibs. Switching to Non-Steam mode.");
				return;
			}
			
			try 
			{
				// Callbacks are pumped from OnUpdate so every Steam event is handled on the main thread.
				SteamClient.Init(1190000, false);
				if (!SteamClient.IsValid || SteamClient.AppId.Value != 1190000)
				{
					IsSteamAvailable = false;
					SteamClient.Shutdown();
					Log.Warn("Steam environment invalid or emulated. Features disabled.");
					return;
				}
				
				SteamNetworkingUtils.InitRelayNetworkAccess();
				IsSteamAvailable = true;
				Log.Info("Steamworks initialized successfully.");
			}
			catch (Exception ex)
			{
				IsSteamAvailable = false;
				try { SteamClient.Shutdown(); } catch (Exception) { }
				Log.Warn("Steamworks could not be initialized (Non-Steam version or Steam not running). Steam features will be disabled. " + ex.Message);
				if (ex is EntryPointNotFoundException || ex.Message.Contains("SteamAPI_"))
					Log.Warn("Probable cause: the wrong steam_api64.dll is in UserLibs (e.g. the game's newer copy). Run the Together+ installer again to restore the right one.");
			}
		}
		
		public override void OnLateInitializeMelon()
		{
			InitializeSteam();
			Config = Shared.Net.ServerConfig.LoadOrCreate(MelonUtils.UserDataDirectory);
			Log.Info($"[Config] Up to {Config.MaxPlayers} players per session.");
			Client.Instance = new Client();
			Server.Instance = new Server();
			ContentManager.Instance = new ContentManager();

			ClientData.UserData = TogetherModManager.LoadUserData();
			if (IsSteamAvailable)
			{
				try { ClientData.UserData.username = PlayerNames.Default(ClientData.UserData.username, SteamClient.Name); }
				catch (Exception ex) { Log.Warn("Could not read the Steam name: " + ex.Message); }
			}
			ClientSide.Features.Features.Initialize(ClientSide.Features.FeatureList.Create());
			isModInitialized = true;
			Log.Info("Together Mod Initialized!");
		}

		public override void OnSceneWasLoaded(int buildindex, string sceneName)
		{
			if (!isModInitialized) return;
			SceneManager.IsLoading = false;
			Log.Info($"[Scene] Loaded {sceneName} (connected={Client.Instance?.isConnected == true}).");
			
			if (sceneName == "Menu")
			{
				SavesManager.Initialize();
				ContentManager.Instance.Initialize();

				ClientData.UserData.scene = SceneManager.UpdateScene(sceneName);
				Application.runInBackground = false;
			}
			UICore.InitializeUI(sceneName);
			if (Client.Instance.isConnected)
			{
				ClientData.UserData.UpdateScene(sceneName);
				
				if (SceneManager.CurrentScene() == GameScene.garage && ClientData.Instance.playerPrefab == null)
					ClientData.Instance.LoadPlayerPrefab();

			}
		}

		public override void OnUpdate()
		{
			if (!isModInitialized)
				return;

			if (IsSteamAvailable)
			{
				try
				{
					SteamClient.RunCallbacks();
				}
				catch (Exception ex)
				{
					Log.Error("Steam callbacks failed", ex);
				}
			}

			Server.Instance.Tick();
			Client.Instance.Tick();
			PlusSaves.Pump();
			Recovery.Pump();
			ClientSide.Features.Features.Pump();

			if (Client.Instance.isConnected && ClientData.Instance != null && SceneManager.CurrentScene() == GameScene.garage)
				ClientData.Instance.UpdateClient();
		}


		public static void StartCoroutine(IEnumerator routine)
		{
			GameManager.Instance.StartCoroutine(routine);
		}

		public override void OnLateUpdate()
		{
			if (!isModInitialized)
			{
				return;
			}
			if (Client.Instance.isConnected)
			{
				try
				{
					Purchases.Flush();
				}
				catch (Exception ex)
				{
					Log.Error("Sending this frame's changes failed", ex);
				}
			}
		}
		
		public override void OnInitializeMelon()
		{
			MelonLogSink.Initialize();
			ClassInjector.RegisterTypeInIl2Cpp<InfoBillboard>();
		}

		public override void OnPreferencesLoaded()
		{
			MelonLogSink.ApplyLevel();
		}

		public override void OnApplicationQuit()
		{
			isClosing = true;
			TogetherModManager.SavePreferences();
			Server.Instance.Stop("The host closed the game.");
			Client.Instance.Disconnect("Closed the game.", false);
		}
	}
}