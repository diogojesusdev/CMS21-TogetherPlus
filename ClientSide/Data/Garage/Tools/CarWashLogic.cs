using CMS21Together.ClientSide.Data.Garage.Car;
using System.Collections;
using CMS;
using CMS21Together.ClientSide.Data.Handle;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using CMS21Together.Shared;

namespace CMS21Together.ClientSide.Data.Garage.Tools;

[HarmonyPatch]
public static class CarWashLogic
{
	public static bool listen = true;

	public static void Reset() => listen = true;
	
	[HarmonyPatch(typeof(CarLoader), nameof(CarLoader.TweenExteriorDustWash))]
	[HarmonyPostfix]
	public static void DTweenExteriorDustWashHook(float targetDust, float targetWash, float time, CarLoader __instance, bool __runOriginal)
	{
		// A gate (StationGates) cancelled the action: nothing happened, nothing to send.
		if (!__runOriginal) return;
		// Business Rule: Only sync if connected and listening flag is enabled
		if(!Client.Instance.isConnected || !listen) 
		{ 
			listen = true; 
			return;
		}

		// Observation: Extract carLoaderID from gameObject name (format: "CarLoader_X")
		// Security Rule: Validate name length before accessing character
		if (__instance == null || __instance.gameObject == null || __instance.gameObject.name.Length < 11)
		{
			Log.Warn("[CarWashLogic->DTweenExteriorDustWashHook] Invalid CarLoader gameObject name.");
			return;
		}

		int carLoaderID = CarLoaderRegistry.IdOf(__instance);
		
		// Business Rule: Validate carLoaderID is within valid range
		if (carLoaderID < 0 || carLoaderID >= 5)
		{
			Log.Warn($"[CarWashLogic->DTweenExteriorDustWashHook] Invalid carLoaderID: {carLoaderID}");
			return;
		}

		ClientSend.CarWashPacket(carLoaderID);
	}
	
	[HarmonyPatch(typeof(InteriorDetailingToolkitLogic), nameof(InteriorDetailingToolkitLogic.DoWorkAnim))]
	[HarmonyPrefix]
	public static void DoWorkAnimHook(CarLoader carLoader, bool __runOriginal)
	{
		// A gate (StationGates) cancelled the action: nothing happened, nothing to send.
		if (!__runOriginal) return;
		// Business Rule: Only sync if connected and listening flag is enabled
		if(!Client.Instance.isConnected || !listen) 
		{ 
			listen = true; 
			return;
		}

		// Security Rule: Validate carLoader is not null
		if (carLoader == null || carLoader.gameObject == null || carLoader.gameObject.name.Length < 11)
		{
			Log.Warn("[CarWashLogic->DoWorkAnimHook] Invalid CarLoader reference.");
			return;
		}

		int carLoaderID = CarLoaderRegistry.IdOf(carLoader);
		
		// Business Rule: Validate carLoaderID is within valid range
		if (carLoaderID < 0 || carLoaderID >= 5)
		{
			Log.Warn($"[CarWashLogic->DoWorkAnimHook] Invalid carLoaderID: {carLoaderID}");
			return;
		}

		ClientSend.CarWashPacket(carLoaderID, true);
		Log.Debug($"[CarWashLogic->DoWorkAnimHook] Interior wash requested for carLoaderID: {carLoaderID}");
	}

	/// <summary>
	/// Handles car wash synchronization from server.
	/// Applies wash effect to the specified car loader.
	/// </summary>
	/// <param name="carLoaderID">The ID of the car loader to wash (0-4)</param>
	/// <param name="interior">Whether to wash interior (true) or exterior (false)</param>
	public static IEnumerator WashCar(int carLoaderID, bool interior)
	{
		// Business Rule: Wait for game to be ready before processing
		while (!GameLoadHook.IsGameReady())
		{
			// Security Rule: Check if client disconnected during wait
			if (!Client.Instance.isConnected)
			{
				Log.Warn("[CarWashLogic->WashCar] Client disconnected while waiting for game ready.");
				yield break;
			}
			yield return new WaitForSeconds(0.25f);
		}

		yield return new WaitForEndOfFrame();
		yield return new WaitForEndOfFrame();

		// Security Rule: Validate GameData.Instance is initialized
		if (GameData.Instance == null)
		{
			Log.Error("[CarWashLogic->WashCar] GameData.Instance is null. Cannot wash car.");
			yield break;
		}

		// Security Rule: Validate carLoaders array is initialized
		if (GameData.Instance.carLoaders == null)
		{
			Log.Error("[CarWashLogic->WashCar] carLoaders array is null. Cannot wash car.");
			yield break;
		}

		// Business Rule: Validate carLoaderID is within array bounds
		if (carLoaderID < 0 || carLoaderID >= GameData.Instance.carLoaders.Length)
		{
			Log.Error($"[CarWashLogic->WashCar] Invalid carLoaderID: {carLoaderID}. Array length: {GameData.Instance.carLoaders.Length}");
			yield break;
		}

		// Security Rule: Validate carLoader at index is not null
		if (GameData.Instance.carLoaders[carLoaderID] == null)
		{
			Log.Warn($"[CarWashLogic->WashCar] CarLoader at index {carLoaderID} is null. Car may not be loaded.");
			yield break;
		}

		// Business Rule: Check if car is already loaded (if loaded, skip to avoid duplicate operations)
		// Note: Original logic was inverted - should check if NOT loaded, not if loaded
		if (!ClientData.Instance.loadedCars.ContainsKey(carLoaderID))
		{
			Log.Warn($"[CarWashLogic->WashCar] Car at carLoaderID {carLoaderID} is not in loadedCars dictionary. Skipping wash.");
			yield break;
		}

		// Business Rule: Set listen flag to false to prevent feedback loop
		listen = false;

		try
		{
			// Business Logic: Apply wash effect based on interior/exterior
			if (!interior)
			{
				// Business Logic: Use longer duration (3f) to match interior wash and ensure animation is visible
				// Observation: Original duration (0.1f) was too short, causing animation to not be visible
				GameData.Instance.carLoaders[carLoaderID].TweenExteriorDustWash(0f, 1f, 3f);
				Log.Debug($"[CarWashLogic->WashCar] Exterior wash applied to carLoaderID: {carLoaderID} with 3s duration.");
			}
			else
			{
				GameData.Instance.carLoaders[carLoaderID].TweenInteriorConditionAndDust(1f, 0f, 3f);
				Log.Debug($"[CarWashLogic->WashCar] Interior wash applied to carLoaderID: {carLoaderID}");
			}
		}
		catch (System.Exception ex)
		{
			// Security Rule: Catch and log any exceptions during wash operation
			Log.Error($"[CarWashLogic->WashCar] Exception during wash operation: {ex.Message}\n{ex.StackTrace}");
		}
		finally
		{
			// Business Rule: Reset listen flag after operation completes
			listen = true;
		}
	}
}