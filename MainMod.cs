using System;
using System.IO;
using CMS21Together.ClientSide;
using CMS21Together.ClientSide.Data;
using CMS21Together.ClientSide.Data.NewUI;
using CMS21Together.ClientSide.Data.Player;
using CMS21Together.ServerSide;
using CMS21Together.Shared;
using CMS21Together.Shared.Data;
using CMS21Together.Shared.Safety;
using CrashReport = CMS21Together.Shared.Safety.CrashReport;
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
		public const string PLUS_VERSION = "0.5.0-rc44";
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
		
		// The incremental GC frees objects stored without a write barrier and crashes the game; Plugins\TogetherPlus.GcFix.dll turns it off.
		private static void LogGcMode()
		{
			try
			{
				if (UnityEngine.Scripting.GarbageCollector.isIncremental)
					Log.Warn("[GC] Incremental GC is on: the game may crash (e.g. when returning to the menu). Run the Together+ installer again to add Plugins\\TogetherPlus.GcFix.dll.");
				else
					Log.Info("[GC] Incremental GC is off.");
			}
			catch (Exception ex) { Log.Warn("[GC] Could not read the GC mode: " + ex.Message); }
		}

		/// <summary>
		/// Records what the updater needs: the version installed next to the DLL (readable without loading it) and the
		/// version that started successfully (so an update that cannot boot is rolled back on the next launch).
		/// </summary>
		private static void RecordInstalledVersion()
		{
			try
			{
				Shared.Updates.UpdateFiles.WriteInstalledVersion(MelonUtils.GameDirectory, PLUS_VERSION);
				Shared.Updates.UpdateFiles.WriteStarted(Shared.PlusPaths.Root, PLUS_VERSION);
				// This version clearly starts here, whatever an earlier launch made the updater believe.
				Shared.Updates.UpdateFiles.ClearFailed(Shared.PlusPaths.Root, PLUS_VERSION);
				var request = Shared.Updates.UpdateFiles.ReadRequest(Shared.PlusPaths.Root);
				if (request != null)
					Log.Info($"[Update] The updater will install Together+ {request}, the version the host runs.");
			}
			catch (Exception ex)
			{
				Log.Warn("[Update] Could not record the installed version: " + ex.Message);
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
			// After MelonLoader's PatchAll and the features' own patches: a patch on a method taking a game struct
			// corrupts memory (PatchRules), so one that slipped in is an error in the first lines of the log.
			Guard.Run("check the game methods that are patched", Shared.Safety.PatchSafety.AuditApplied);
			isModInitialized = true;
			CrashReport.BeginSession();
			CrashUI.Initialize();
			LogGcMode();
			RecordInstalledVersion();
			Log.Info("Together Mod Initialized!");
		}

		public override void OnSceneWasLoaded(int buildindex, string sceneName)
		{
			if (!isModInitialized) return;
			SceneManager.IsLoading = false;
			Shutdown.Clear();
			Log.Info($"[Scene] Loaded {sceneName} (connected={Client.Instance?.isConnected == true}).");
			
			if (sceneName == "Menu")
			{
				SavesManager.Initialize();
				ContentManager.Instance.Initialize();
				UIActions.ResetLaunch();
				CrashUI.ShowPreviousSessionNotice();

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
			Cheats.Pump();

			// While the garage is being torn down the mod touches nothing that belongs to the game.
			if (Shutdown.Leaving) return;

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
			CrashReport.EndSession();
		}
	}
}