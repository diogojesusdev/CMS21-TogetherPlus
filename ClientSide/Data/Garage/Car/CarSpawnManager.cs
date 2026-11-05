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
using UnhollowerBaseLib;
using CMS21Together.Shared;

namespace CMS21Together.ClientSide.Data.Garage.Car;

public static class CarSpawnManager
{
	public static IEnumerator LoadCar(NewCarData carData, int carLoaderID, int placeNo)
	{
		if (ClientData.Instance.loadedCars.ContainsKey(carLoaderID)) yield break;

		var car = new ModCar(carLoaderID, carData.carToLoad, carData.configVersion, placeNo, carData.customerCar);
		Log.Info($"[Car] Local load of {carData.carToLoad} on loader {carLoaderID} (place {placeNo}), sending it.");
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
		Log.Info($"[Car] Job car {name} loaded on loader {carLoaderID} (customer={carLoader.customerCar}).");
		
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

			// The game's copy of the car can be half-written for a frame or two: try a few times instead of losing the
			// car. Read it through CarDataReader, never through the raw array: the array class caches its element
			// stride in a static constructor that can run before NewCarData's, and a stale pointer inside the struct
			// takes the game down in il2cpp_object_get_class (see the car order crash report).
			var cars = GameManager.Instance.GameDataManager.CurrentProfileData?.carsInGarage;
			if (cars == null)
			{
				Log.Error($"[CarSpawnManager->LoadJobCar] The profile has no car array; {name} (loader {carLoaderID}) could not be sent.");
				yield break;
			}

			var preferredIndex = Helper.GetIndexFromCarLoaderName(carLoader.name);
			var index = CarDataReader.FindSlot(cars, name, preferredIndex);
			if (index < 0)
			{
				Log.Error($"[CarSpawnManager->LoadJobCar] {name} (loader {carLoaderID}) is not in the garage array; it could not be sent. The slots hold: {CarDataReader.Describe(cars)}");
				yield break;
			}
			if (index != preferredIndex)
				Log.Info($"[Car] {name} is in garage slot {index} (the loader-name lookup said {preferredIndex}).");

			ModNewCarData modCarData = null;
			for (var attempt = 1; attempt <= 3 && modCarData == null; attempt++)
			{
				try
				{
					if (CarDataReader.TryRead(cars, index, out var carData, out var problem))
					{
						modCarData = new ModNewCarData(carData, carLoader.placeNo, carLoader.orderConnection);
					}
					else
					{
						Log.Warn(problem == null
							? $"[CarSpawnManager->LoadJobCar] {name} (loader {carLoaderID}, slot {index}) is not spawned in the profile yet, attempt {attempt}/3."
							: $"[CarSpawnManager->LoadJobCar] {name} (loader {carLoaderID}, slot {index}) is not readable yet, attempt {attempt}/3: {problem}");
					}
				}
				catch (Exception ex)
				{
					Log.Warn($"[CarSpawnManager->LoadJobCar] Reading {name} (loader {carLoaderID}, slot {index}) failed, attempt {attempt}/3: {(attempt == 3 ? ex.ToString() : ex.Message)}");
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
				Log.Error($"[CarSpawnManager->LoadJobCar] {name} (loader {carLoaderID}) could not be read and was not sent to the other players.");
				yield break;
			}

			Log.Info($"[Car] Job car {name} (loader {carLoaderID}) sent to the other players.");
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
		{
			Log.Info($"[Car] {data.carToLoad} already on loader {carLoaderID}, not loading it again.");
			yield break;
		}

		Log.Info($"[Car] Loading {data.carToLoad} from the host on loader {carLoaderID} (place {data.carPosition}, job {data.jobID}, replacing {existing?.carID ?? "nothing"}).");
		var carLoader = GameData.Instance.carLoaders[carLoaderID];
		carLoader.placeNo = data.carPosition;
		carLoader.ConfigVersion = data.configVersion;
		var carData = data.ToGame();

		yield return new WaitForEndOfFrame();
		
		CarSpawnHooks.listenToSimpleLoad = false;
		if (data.jobID != -1)
			carLoader.SetCustomerCar(true, data.jobID);
		MainMod.StartCoroutine(carLoader.LoadCarFromFile(carData));
		var car = new ModCar(carLoaderID, data.carToLoad, data.configVersion, data.carPosition, data.customerCar);
		ClientData.Instance.loadedCars[carLoaderID] = car;
		MelonCoroutines.Start(PartsReferencer.GetPartReferences(ClientData.Instance.loadedCars[carLoaderID]));

		Log.Debug($"[CarManager->LoadCarFromServer] Loading {data.carToLoad} from server...");
	}
}