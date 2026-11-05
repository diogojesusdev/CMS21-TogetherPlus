using System;
using System.Collections;
using CMS;
using CMS.Difficulty;
using CMS.Garage.Customization;
using CMS.Managers;
using CMS.UI;
using CMS.UI.Windows;
using CMS21Together.ClientSide.Data.Garage;
using CMS21Together.ServerSide;
using HarmonyLib;
using Il2CppSystem.Collections.Generic;
using MelonLoader;
using UnityEngine;
using CMS21Together.Shared;
using CMS21Together.Shared.Safety;
using Object = UnityEngine.Object;

namespace CMS21Together.ClientSide.Data;

[HarmonyPatch]
public static class GameLoadHook
{
	private static bool isGameReady;
	private static int generation;

	public static bool IsGameReady() => isGameReady;

	/// <summary>Also cancels a garage load still running (Phase 8 assessment: it set the flag later, in the menu).</summary>
	public static void Reset()
	{
		isGameReady = false;
		generation++;
	}
	
	[HarmonyPrefix]
	[HarmonyPatch(typeof(GarageLoader), nameof(GarageLoader.Start))]
	public static bool GarageStartOverride(GarageLoader __instance)
	{
		if (!Client.Instance.isConnected) return true;
		
		Reset();
		Player.CarEnterExit.Reset();
		MelonCoroutines.Start(CustomPrepareGame(__instance));
		return false;
	}

	private static IEnumerator CustomPrepareGame(GarageLoader __instance)
	{
		if (__instance.loadOnStart)
		{
			ScreenFader.Get().SetBlack();
		}
		yield return __instance.StartCoroutine(Singleton<GameManager>.Instance.GameDataManager.Load(false));
		GlobalData.TrackInteriorVolumeMod = 1f;
		while (!Singleton<GameManager>.Instance.Localization.IsInitialized)
		{
			yield return YieldInstructions.WaitForEndOfFrame;
		}
		Singleton<GameManager>.Instance.Localization.Localize();
		Singleton<GameManager>.Instance.PlatformManager.SetPresence("console_richtxt_garage");
		__instance.carLoader = Singleton<GameManager>.Instance.CarLoadersInScene;
		Singleton<GameManager>.Instance.ProfileManager.Load();
		CarLoaderPlaces.Get().PrepareWithoutLoad();
		NotificationCenter.CanMount = true;
		NotificationCenter.CanUnmount = true;
		if (Singleton<GameManager>.Instance.Radio)
		{
			yield return __instance.StartCoroutine(Singleton<GameManager>.Instance.Radio.Init());
		}
		UIManager.Get().RefreshAllStats();
		if (__instance.loadOnStart)
		{
			MelonCoroutines.Start(CustomLoad(__instance));
		}
		else
		{
			__instance.isReady = true;
		}
		while (!__instance.isReady)
		{
			yield return YieldInstructions.WaitForEndOfFrame;
		}
		if (!GameMode.Get().CompareWithCurrentMode(gameMode.UI))
		{
			GameMode.Get().SetCurrentMode(gameMode.Garage);
		}
		SceneLoader.BlockProgress = false;
		NotificationCenter.IsGameReady = true;
		yield break;
	}

	private static IEnumerator VanillaLoad(GarageLoader __instance)
	{
		__instance.isReady = false;
		NotificationCenter.IsGameReady = false;
		ScreenFader screenFader = ScreenFader.Get();
		screenFader.SetBlack();
		WindowManager.Instance.DisableAllWindowsOpening();
		CarPlaceManager.ClearAll();
		if (NotificationCenter.BenchmarkActive || BenchmarkManager.Get().BenchmarkActive)
		{
			__instance.StartCoroutine(BenchmarkManager.Get().StartBenchmark());
			yield break;
		}
		Object.Destroy(__instance.GetComponent<BenchmarkManager>());
		Camera mainCamera = Camera.main;
		while (!mainCamera.GetComponent<FPSCamera>().IsReady)
		{
			yield return YieldInstructions.WaitForEndOfFrame;
		}
		GlobalData.Load();
		DifficultyManager difficultyManager = Singleton<GameManager>.Instance.DifficultyManager;
		difficultyManager.ActivateDifficultyLevel();
		string selectedProfileName = Singleton<GameManager>.Instance.ProfileManager.GetSelectedProfileName();
		DevSettings.DevMode = selectedProfileName.Equals("VoizePrice");
		if (Singleton<GameManager>.Instance.Radio != null)
		{
			Singleton<GameManager>.Instance.Radio.LoadDataFromSave();
		}
		__instance.LoadMachines();
		Singleton<GameManager>.Instance.Inventory.Load();
		Singleton<GameManager>.Instance.Warehouse.Load();
		TempInventory tempInventory = Singleton<GameManager>.Instance.TempInventory;
		if (tempInventory.GetItemsCount() > 0)
		{
			List<BaseItem> listOfItems = tempInventory.GetListOfItems();
			Singleton<GameManager>.Instance.Inventory.Add(listOfItems);
			tempInventory.ClearListOfItems();
		}
		CarLoaderPlaces.Get().Load();
		__instance.garageLevel.PrepareGarage();
		while (!__instance.garageLevel.IsReady)
		{
			yield return YieldInstructions.WaitForEndOfFrame;
		}
		GarageLookManager garageLookManager = __instance.garageLevel.SetupGarageLookManager();
		garageLookManager.Init();
		yield return __instance.StartCoroutine(garageLookManager.Load());
		while (garageLookManager.LoadingSaveInProcess)
		{
			yield return YieldInstructions.WaitForEndOfFrame;
		}
		TexturePackManager texturePackManager = __instance.garageLevel.SetupTexturePackManager();
		yield return __instance.StartCoroutine(texturePackManager.Initialize());
		texturePackManager.Load();
		DLCErrorWindow dlcErrorWindow = WindowManager.Instance.GetWindowByID<DLCErrorWindow>(WindowID.DLCError);
		dlcErrorWindow.Clear();
		var carLoaderCount = __instance.carLoader.Count;
		GameSettings.DoNotClearPartsIDCache = true;
		GameSettings.DoNotUnloadAssets = true;
		for (var i = 0; i < carLoaderCount; i++)
		{
			var car = __instance.carLoader[i];
			if (car == null) continue;

			// One car that cannot be read from its file used to abort the whole loop: the exception killed this
			// coroutine and left every loader after it emptied by DeleteCar. Fail that car only, name it, carry on.
			if (!TryStartCarLoad(car, i)) continue;

			while (!car.IsLoadedFromFile())
			{
				yield return YieldInstructions.WaitForEndOfFrame;
			}
			if (car.GetSaveName() == GlobalData.SelectedCarLoader && GlobalData.NewMileage != 0)
			{
				var carLoader = car;
				carLoader.CarInfoData = carLoader.CarInfoData with { Mileage = carLoader.CarInfoData.Mileage + GlobalData.NewMileage };
				GlobalData.NewMileage = 0;
			}
		}
		GameSettings.DoNotUnloadAssets = false;
		GameSettings.DoNotClearPartsIDCache = false;
		Helper.ClearCacheForIDs();
		ProfileData currentProfileData = Singleton<GameManager>.Instance.GameDataManager.CurrentProfileData;
		PlayerData profileData = currentProfileData.PlayerData;
		CharacterController characterController = Object.FindObjectOfType<CharacterController>();
		if (!profileData.IsDefault())
		{
			characterController.transform.position = profileData.GetPosition();
			mainCamera.GetComponent<FPSCamera>().SetSavedRotation(profileData.GetRotation());
		}
		if (__instance.carLifter[0].HaveToResetPlayer() || __instance.carLifter[1].HaveToResetPlayer())
		{
			characterController.GetComponent<FPSInputController>().ResetPosition();
		}
		GlobalData.SetUnlockedPositions(currentProfileData.unlockedPosition.position);
		Singleton<GameManager>.Instance.OrderGenerator.Load();
		ShopListWindow windowByID = WindowManager.Instance.GetWindowByID<ShopListWindow>(WindowID.ShopList);
		if (windowByID != null)
		{
			windowByID.Load();
		}
		NewCarLifterData[] carLiftersData = currentProfileData.carLiftersData;
		if (carLiftersData != null && carLiftersData.Length != 0)
		{
			int i = 0;
			while (i < __instance.carLifter.Length && i < carLiftersData.Length)
			{
				CarLifter cl = __instance.carLifter[i];
				if (cl.gameObject.activeSelf)
				{
					NewCarLifterData carLifterData = currentProfileData.carLiftersData[i];
					while (cl.isMoving)
					{
						yield return YieldInstructions.WaitForEndOfFrame;
					}
					CarLoader connectedCarLoader = cl.GetConnectedCarLoader();
					if (carLifterData.lifterData == 0 && connectedCarLoader)
					{
						MissingWheelTypes missingWheelTypes = connectedCarLoader.GetMissingWheelTypes();
						// Vanilla uses HasFlagFast on each wheel: any missing wheel keeps the lifter up.
						const MissingWheelTypes anyWheel = MissingWheelTypes.FrontLeft | MissingWheelTypes.FrontRight
						                                   | MissingWheelTypes.RearLeft | MissingWheelTypes.RearRight;
						if ((missingWheelTypes & anyWheel) != 0)
						{
							cl.InstantSet(1, true);
							goto IL_06AE;
						}
					}
					cl.InstantSet((__instance.carLifter[i].GetConnectedCarLoader() == null) ? 0 : carLifterData.lifterData, true);
					cl = null;
					carLifterData = null;
				}
				IL_06AE:
				i++;
			}
		}
	}

	/// <summary>
	/// Starts one car's file load. A failure (the game has thrown "routine is null" here) costs that car, not the whole
	/// garage: the caller skips the wait loop and loads the rest.
	/// </summary>
	private static bool TryStartCarLoad(CarLoader car, int index)
	{
		var started = false;
		Guard.Run($"load the car on loader {index} ({CarName(car)}) from its file", () =>
		{
			car.DeleteCar();
			car.LoadCarFromFile();
			started = true;
		});

		return started;
	}

	private static string CarName(CarLoader car)
	{
		try
		{
			return string.IsNullOrEmpty(car.carToLoad) ? "no car" : car.carToLoad;
		}
		catch (Exception)
		{
			return "unreadable";
		}
	}

	private static IEnumerator CustomLoad(GarageLoader __instance)
	{
		var load = generation;
		yield return VanillaLoad(__instance);
		
		ScreenFader screenFader = ScreenFader.Get();
		DLCErrorWindow dlcErrorWindow = WindowManager.Instance.GetWindowByID<DLCErrorWindow>(WindowID.DLCError);
		ProfileData currentProfileData = Singleton<GameManager>.Instance.GameDataManager.CurrentProfileData;
		PlayerData profileData = currentProfileData.PlayerData;

		yield return new WaitForEndOfFrame();
		yield return new WaitForEndOfFrame();
		GameData.Instance = new GameData();
		yield return new WaitForEndOfFrame();
		GameData.Instance.LoadEngineStand();
		if (!Server.Instance.isRunning)
			MelonCoroutines.Start(GarageResync.ResyncGarage());
		yield return new WaitForEndOfFrame();
		
		
		SceneLoader.BlockProgress = false; // needed to end loading
		NotificationCenter.IsGameReady = true; // needed to end loading
		CameraManager.Get().ChangeCamera(CameraState.FPS);
		yield return new WaitForSeconds(2f);
		// A disconnect or scene change during the load: this load is over, the flag belongs to the next one.
		if (load != generation || !Client.Instance.isConnected) yield break;
		Player.Stats.SeedWallet();
		isGameReady = true;
		screenFader.FadeTo(2f, 1f, 0f, false, true);
		bool canOpenPieMenu = true;
		if (GlobalData.TestToShow == "ExamineReport")
		{
			WindowManager.Instance.EnableWindowOpening(WindowID.ExamineReport);
			GameScript.Get().SetCurrentExamineType(ToolType.TestDrive);
			WindowManager.Instance.Show(WindowID.ExamineReport, false);
			canOpenPieMenu = false;
		}
		if (dlcErrorWindow.ShouldShow())
		{
			WindowManager.Instance.EnableWindowOpening(WindowID.DLCError);
			dlcErrorWindow.ModeToSetAfterClosing = gameMode.Garage;
			dlcErrorWindow.EnablePieMenuAfterClosing = true;
			WindowManager.Instance.ShowAfterWindowClose(WindowID.DLCError, WindowID.ExamineReport);
			canOpenPieMenu = false;
		}
		__instance.isReady = true; // needed to end loading
		yield return YieldInstructions.WaitForEndOfFrame;
		if (canOpenPieMenu)
		{
			GlobalData.CanOpenPieMenu = true;
			GameScript.Get().CanOpenPieMenu = true;
		}
		while (screenFader.IsRunning())
		{
			yield return YieldInstructions.WaitForEndOfFrame;
		}
		WindowManager.Instance.EnableAllWindowsOpening();
		Singleton<GameManager>.Instance.ProfileManager.BackupSave();
		
		Log.Info("Run Custom Load method successfully !!");
		yield break;
	}
}