using System.Collections.Generic;
using CMS21Together.ClientSide;
using CMS21Together.ClientSide.Data;
using CMS21Together.ClientSide.Data.Garage;
using CMS21Together.ClientSide.Data.Garage.Tools;
using CMS21Together.ServerSide;
using CMS21Together.Shared.Data;
using HarmonyLib;
using MelonLoader;

namespace CMS21Together.Shared;

[HarmonyPatch]
public static class SceneManager
{
	/// <summary>The game's own profile slots; Together+ saves start right after them (SavesManager.FirstModSlot).</summary>
	private const int VanillaSaveSlots = 4;

	private static readonly System.Diagnostics.Stopwatch LoadClock = System.Diagnostics.Stopwatch.StartNew();
	private static long loadingSinceMs = -1;

	/// <summary>True from a scene change request until Unity reports the new scene loaded (at most 2 minutes).</summary>
	public static bool IsLoading
	{
		get => loadingSinceMs >= 0 && LoadClock.ElapsedMilliseconds - loadingSinceMs < 120_000;
		set => loadingSinceMs = value ? LoadClock.ElapsedMilliseconds : -1;
	}

	[HarmonyPatch(typeof(NotificationCenter), nameof(NotificationCenter.SelectSceneToLoad),
		new []{typeof(string), typeof(SceneType), typeof(bool), typeof(bool)})]
	[HarmonyPrefix]
	public static void SelectSceneToLoadHook(string newSceneName, SceneType sceneType, bool useFader, bool saveGame)
	{
		// Until the scene has loaded, nothing heavy runs here (save copies wait, autosave waits).
		IsLoading = true;
		// Any scene change ends garage readiness, also after a disconnect (the menu load runs unconnected).
		GameLoadHook.Reset();

		// The menu reloads the game's own profile array, which only has the four vanilla slots, and then reads
		// ProfileData[ProfileManager.selectedProfile] straight away. A mod save leaves that index at 4 or higher, so
		// the game throws IndexOutOfRangeException inside its own scene-load coroutine and the process dies. This
		// runs before the connected check on purpose: a single-player mod save returns to the menu the same way.
		if (newSceneName == "Menu") ResetProfileSelection();

		// Business Rule: Only process scene changes if client is connected
		if (!Client.Instance.isConnected) return;

		// Security Rule: Validate scene name is not null or empty
		if (string.IsNullOrEmpty(newSceneName))
		{
			Log.Warn("[SceneManager->SelectSceneToLoadHook] Scene name is null or empty.");
			return;
		}

		Log.Info($"[SceneManager->SelectSceneToLoadHook] Scene change requested: {newSceneName}");
		if (newSceneName == "Menu")
		{
			// Business Rule: Disconnect and cleanup when returning to menu
			Log.Info("[SceneManager->SelectSceneToLoadHook] Going to menu! Disconnecting...");
			
			// Security Rule: Stop outdoor operations before disconnecting
			StopOutdoorOperations();
			
			// The menu is already loading: stop synchronously (the session save happens here) and don't load it again.
			if (Server.Instance != null && Server.Instance.isRunning)
				Server.Instance.Stop("The host returned to the menu.");

			if (Client.Instance.isConnected)
				Client.Instance.Disconnect("Returned to the menu.", false);
		}
		else if (newSceneName == "garage" || newSceneName == "Christmas" || newSceneName == "Easter" || newSceneName == "Halloween")
		{
			// GameData exists once the first garage load finished (hf3: ClientData.GameReady).
			// Clients resync from GameLoadHook.CustomLoad on every garage load; the host resyncs here.
			if (GameData.Instance != null)
			{
				StopOutdoorOperations();
				if (Server.Instance != null && Server.Instance.isRunning)
					MelonCoroutines.Start(GarageResync.ResyncGarage());
			}
		}
		else
		{
			if (ClientData.Instance != null && ClientData.Instance.loadedCars != null)
			{
				StopOutdoorOperations();
				
				foreach (ModCar loadedCar in ClientData.Instance.loadedCars.Values)
				{
					if (loadedCar != null)
					{
						loadedCar.needResync = true;
					}
				}
				Log.Info($"[SceneManager->SelectSceneToLoadHook] Marked {ClientData.Instance.loadedCars.Count} cars for resync.");
			}
			else
			{
				Log.Warn("[SceneManager->SelectSceneToLoadHook] ClientData.Instance or loadedCars is null. Cannot mark cars for resync.");
			}
		}
	}
	
	/// <summary>
	/// Sends the game back to a vanilla profile slot before the menu loads. Together+ saves live at index 4 and up in
	/// an array the mod installs; the menu rebuilds that array with the game's four slots and then reads
	/// <c>ProfileData[selectedProfile]</c>, which throws (and kills the process) while the index still points at a
	/// mod save. SavesManager.Initialize reinstalls the bigger array once the menu scene is up.
	/// </summary>
	private static void ResetProfileSelection()
	{
		try
		{
			var profiles = Singleton<GameManager>.Instance?.ProfileManager;
			if (profiles == null || profiles.selectedProfile < VanillaSaveSlots) return;

			Log.Info($"[SceneManager] Save slot {profiles.selectedProfile} is a Together+ slot; selecting a vanilla slot for the menu.");
			profiles.selectedProfile = 0;
			Singleton<GameManager>.Instance.RDGPlayerPrefs?.SetInt("selectedProfile", 0);
		}
		catch (System.Exception ex)
		{
			Log.Warn($"[SceneManager] Could not reset the selected save slot for the menu: {ex.Message}");
		}
	}

	private static void StopOutdoorOperations()
	{
		try
		{
			if (GameData.Instance != null)
			{
				CMS21Together.ClientSide.Data.Garage.Tools.CarWashLogic.Reset();
				CarPaintLogic.Reset();
				
				Log.Info("[SceneManager->StopOutdoorOperations] Stopped outdoor operations (car wash, paint).");
			}
		}
		catch (System.Exception ex)
		{
			Log.Warn($"[SceneManager->StopOutdoorOperations] Error stopping outdoor operations: {ex.Message}");
		}
	}

	/// <summary>
	/// Updates the current scene based on scene name.
	/// Resets GameData readiness when entering garage.
	/// </summary>
	/// <param name="scene">The name of the scene being loaded</param>
	/// <returns>The corresponding GameScene enum value</returns>
	public static GameScene UpdateScene(string scene)
	{
		// Security Rule: Validate scene name is not null or empty
		if (string.IsNullOrEmpty(scene))
		{
			Log.Warn("[SceneManager->UpdateScene] Scene name is null or empty. Returning unknown.");
			return GameScene.unknow;
		}

		Log.Info($"[SceneManager->UpdateScene] Changed scene: {scene}!");
		
		// Business Rule: Map scene names to GameScene enum values
		if (scene == "Barn")
			return GameScene.barn;
		
		if (scene == "garage" || scene == "Christmas" || scene == "Easter" || scene == "Halloween")
		{
			return GameScene.garage;
		}
		
		if (scene == "Junkyard")
			return GameScene.junkyard;
		
		if (scene == "Auto_salon")
			return GameScene.auto_salon;
		
		if (scene == "Menu")
			return GameScene.menu;

		// Business Rule: Return unknown for unrecognized scene names
		Log.Warn($"[SceneManager->UpdateScene] Unknown scene name: {scene}. Returning unknown.");
		return GameScene.unknow;
	}

	public static GameScene CurrentScene(UserData user = null)
	{
		if (IsInBarn(user))
			return GameScene.barn;
		if (IsInGarage(user))
			return GameScene.garage;
		if (IsInJunkyard(user))
			return GameScene.junkyard;
		if (IsInDealer(user))
			return GameScene.auto_salon;
		if (IsInMenu(user))
			return GameScene.menu;

		return GameScene.unknow;
	}

	public static bool IsInMenu(UserData user = null)
	{
		if (user != null)
			return user.scene == GameScene.menu;

		return ClientData.UserData.scene == GameScene.menu;
	}

	public static bool IsInGarage(UserData user = null)
	{
		if (user != null)
			return user.scene == GameScene.garage;

		return ClientData.UserData.scene == GameScene.garage;
	}

	public static bool IsInJunkyard(UserData user = null)
	{
		if (user != null)
			return user.scene == GameScene.junkyard;

		return ClientData.UserData.scene == GameScene.junkyard;
	}

	public static bool IsInDealer(UserData user = null)
	{
		if (user != null)
			return user.scene == GameScene.auto_salon;

		return ClientData.UserData.scene == GameScene.auto_salon;
	}

	public static bool IsInBarn(UserData user = null)
	{
		if (user != null)
			return user.scene == GameScene.barn;

		return ClientData.UserData.scene == GameScene.barn;
	}
}