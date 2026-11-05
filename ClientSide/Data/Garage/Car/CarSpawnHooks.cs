using System;
using System.Collections;
using CMS21Together.ClientSide.Data.Handle;
using CMS21Together.Shared.Data;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using UnityEngine.SceneManagement;
using SceneManager = CMS21Together.Shared.SceneManager;
using CMS21Together.Shared;
using CMS21Together.Shared.Features;

namespace CMS21Together.ClientSide.Data.Garage.Car;

[HarmonyPatch]
public static class CarSpawnHooks
{
	public static bool listenToLoad = true;
	public static bool listenToSimpleLoad = true;
	public static bool listenToDelete = true;

	public static void Reset()
	{
		listenToLoad = true;
	}

	[HarmonyPatch(typeof(CarLoader), nameof(CarLoader.LoadCarFromFile), typeof(string))]
	[HarmonyPrefix]
	public static void LoadCarFromFileHook(string file, CarLoader __instance)
	{
		if (!Client.Instance.isConnected || !listenToLoad)
		{
			listenToLoad = true;
		}
		
		int indexFromCarLoaderName = Helper.GetIndexFromCarLoaderName(file);
		NewCarData newCarData = Singleton<GameManager>.Instance.GameDataManager.LoadCar(indexFromCarLoaderName, false);
		MainMod.StartCoroutine(__instance.LoadCarFromFile(newCarData));
		MelonCoroutines.Start(LoadCarFromFile(newCarData, __instance));
	}

	private static IEnumerator LoadCarFromFile(NewCarData carDataCheck, CarLoader __instance)
	{
		yield return new WaitForEndOfFrame();
		if (string.IsNullOrEmpty(__instance.carToLoad)) yield break;
		if (QolParking.IsExtraPlace(__instance.placeNo)) yield break;
		// Phase 6b (SceneReturnSync): a car from this player's own stale save is never sent to the host.
		if (Features.Garage.SceneReturnFeature.HoldEcho(__instance)) yield break;
		if (!SceneManager.IsInGarage()) yield break;
		
		var carLoaderID = CarLoaderRegistry.IdOf(__instance);
		//Log.Info($"[CarSpawnHooks->LoadCarFromFileHook] Triggered:{__instance.carToLoad} , {carLoaderID}");
		MelonCoroutines.Start(CarSpawnManager.LoadCar(carDataCheck, carLoaderID, __instance.placeNo));
	}

	[HarmonyPatch(typeof(CarLoader), nameof(CarLoader.LoadCar))]
	[HarmonyPostfix]
	public static void LoadCarHook(string name, CarLoader __instance)
	{
		if (!Client.Instance.isConnected || !listenToSimpleLoad)
		{
			listenToSimpleLoad = true;
			return;
		}
		
		if (string.IsNullOrEmpty(name)) return;
		if (!SceneManager.IsInGarage()) return;
		if (QolParking.IsExtraPlace(__instance.placeNo)) return;

		Log.Info($"[Car] Local job car load of {name} on loader {CarLoaderRegistry.IdOf(__instance)} (place {__instance.placeNo}).");

		var carLoaderID = CarLoaderRegistry.IdOf(__instance);
		MelonCoroutines.Start(CarSpawnManager.LoadJobCar(name, carLoaderID, __instance));
	}

	[HarmonyPatch(typeof(CarLoader), nameof(CarLoader.DeleteCar), new Type[] { })]
	[HarmonyPrefix]
	public static void DeleteCarHook(CarLoader __instance, bool __runOriginal)
	{
		// A gate (StationGates) cancelled the action: nothing happened, nothing to send.
		if (!__runOriginal) return;
		if (MainMod.isClosing || Client.Instance == null) return;
		if (!Client.Instance.isConnected || !listenToDelete)
		{
			listenToDelete = true;
			return;
		}
		
		if (!GameLoadHook.IsGameReady()) return;
		if (__instance == null || string.IsNullOrEmpty(__instance.carToLoad)) return;
		// QoL mod extra parking cars are local only (Phase 6b, QolParkingCompat).
		if (QolParking.IsExtraPlace(__instance.placeNo)) return;
		
		if (SceneManager.CurrentScene() != GameScene.garage) return;
		
		var carLoaderID = CarLoaderRegistry.IdOf(__instance);
		if (ClientData.Instance.loadedCars.TryGetValue(carLoaderID, out var car))
		{
			if (!car.needResync)
			{
				Log.Info($"[Car] Local delete of {car.carID} on loader {carLoaderID}, sending it.");
				ClientSend.DeleteCarPacket(carLoaderID);
				ClientData.Instance.loadedCars.Remove(carLoaderID);
			}
		}
	}
}