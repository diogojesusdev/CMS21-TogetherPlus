using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using CMS;
using CMS.PartModules;
using CMS21Together.ClientSide.Data.Handle;
using CMS21Together.Shared.Data;
using CMS21Together.Shared.Data.Vanilla.Cars;
using MelonLoader;
using UnityEngine;
using CMS21Together.Shared;

namespace CMS21Together.ClientSide.Data.Garage.Car;

public static class CarSpawnManager
{
	public static IEnumerator LoadCar(NewCarData carData, int carLoaderID, int placeNo)
	{
		if (ClientData.Instance.loadedCars.ContainsKey(carLoaderID)) yield break;

		var car = new ModCar(carLoaderID, carData.carToLoad, carData.configVersion, placeNo, carData.customerCar);
		ClientSend.LoadCarPacket(new ModNewCarData(carData, placeNo), carLoaderID);
		
		while (!GameData.Instance.carLoaders[carLoaderID].IsCarLoaded()) yield return YieldInstructions.WaitForEndOfFrame;
		yield return YieldInstructions.WaitForEndOfFrame;
		yield return YieldInstructions.WaitForEndOfFrame;
		
		if (!ClientData.Instance.loadedCars.ContainsKey(carLoaderID))
		{
			ClientData.Instance.loadedCars.Add(carLoaderID, car);
			MelonCoroutines.Start(PartsReferencer.GetPartReferences(ClientData.Instance.loadedCars[carLoaderID]));
		}
	}

	public static IEnumerator LoadJobCar(string name, int carLoaderID, CarLoader carLoader)
	{
		while (!GameLoadHook.IsGameReady())
			yield return new WaitForSeconds(0.25f);

		yield return new WaitForEndOfFrame();
		yield return new WaitForEndOfFrame();

		if (ClientData.Instance.loadedCars.ContainsKey(carLoaderID)) yield break;
		yield return YieldInstructions.WaitForEndOfFrame;

		while (!carLoader.IsCarLoaded()) yield return YieldInstructions.WaitForEndOfFrame;
		yield return YieldInstructions.WaitForEndOfFrame;
		yield return YieldInstructions.WaitForEndOfFrame;
		
		var car = new ModCar(carLoaderID, name, carLoader.ConfigVersion, carLoader.placeNo, carLoader.customerCar);
		
		if (carLoader.customerCar)
			ClientSend.LoadJobCarPacket(car);

		yield return new WaitForEndOfFrame();
		if (!ClientData.Instance.loadedCars.ContainsKey(carLoaderID))
		{
			ClientData.Instance.loadedCars.Add(carLoaderID, car);
			MelonCoroutines.Start(PartsReferencer.GetPartReferences(ClientData.Instance.loadedCars[carLoaderID]));
			
			while (!ClientData.Instance.loadedCars[carLoaderID].isReady)
				yield return new WaitForSeconds(0.25f);
			yield return new WaitForEndOfFrame();
			carLoader.SaveCarToFile();
			yield return new WaitForEndOfFrame();
			yield return new WaitForEndOfFrame();

			// The game's copy of the car can be half-written for a frame or two: try a few times instead of losing the car.
			ModNewCarData modCarData = null;
			for (var attempt = 1; attempt <= 3 && modCarData == null; attempt++)
			{
				try
				{
					var carData = GameManager.Instance.GameDataManager.CurrentProfileData.carsInGarage[Helper.GetIndexFromCarLoaderName(carLoader.name)];
					modCarData = new ModNewCarData(carData, carLoader.placeNo, carLoader.orderConnection);
				}
				catch (Exception ex)
				{
					Log.Warn($"[CarSpawnManager->LoadJobCar] Reading {name} (loader {carLoaderID}) failed, attempt {attempt}/3: {ex.Message}");
				}
				if (modCarData == null)
				{
					yield return new WaitForSeconds(0.5f);
					if (carLoader == null || !carLoader.IsCarLoaded()) yield break;
					carLoader.SaveCarToFile();
					yield return new WaitForEndOfFrame();
				}
			}
			if (modCarData == null)
			{
				Log.Error($"[CarSpawnManager->LoadJobCar] {name} (loader {carLoaderID}) could not be sent to the other players.");
				yield break;
			}

			ClientSend.LoadCarPacket(modCarData, carLoaderID);
		}
	}

	public static IEnumerator LoadCarFromServer(ModNewCarData data, int carLoaderID)
	{
		while (!GameLoadHook.IsGameReady())
			yield return new WaitForSeconds(0.25f);

		yield return new WaitForEndOfFrame();

		// The same car again (a resync while it was already being loaded): its parts follow, no second load.
		if (ClientData.Instance.loadedCars.TryGetValue(carLoaderID, out var existing) && existing != null && existing.carID == data.carToLoad)
			yield break;

		var carLoader = GameData.Instance.carLoaders[carLoaderID];
		carLoader.placeNo = data.carPosition;
		carLoader.ConfigVersion = data.configVersion;
		var carData = data.ToGame();

		yield return new WaitForEndOfFrame();
		
		CarSpawnHooks.listenToSimpleLoad = false;
		if (data.jobID != -1)
			carLoader.SetCustomerCar(true, data.jobID);
		MainMod.StartCoroutine(carLoader.LoadCarFromFile(carData));
		var car = new ModCar(carLoaderID, data.carToLoad, data.configVersion);
		ClientData.Instance.loadedCars[carLoaderID] = car;
		MelonCoroutines.Start(PartsReferencer.GetPartReferences(ClientData.Instance.loadedCars[carLoaderID]));

		Log.Debug($"[CarManager->LoadCarFromServer] Loading {data.carToLoad} from server...");
	}
}