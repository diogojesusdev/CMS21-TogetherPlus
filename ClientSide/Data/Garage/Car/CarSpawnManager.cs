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

			// The game's copy of the car can be half-written for a frame or two: try a few times instead of losing the car.
			var cars = GameManager.Instance.GameDataManager.CurrentProfileData?.carsInGarage;
			if (cars == null)
			{
				Log.Error($"[CarSpawnManager->LoadJobCar] The profile has no car array; {name} (loader {carLoaderID}) could not be sent.");
				yield break;
			}

			var preferredIndex = Helper.GetIndexFromCarLoaderName(carLoader.name);
			var index = FindGarageIndex(cars, name, preferredIndex);
			if (index != preferredIndex)
				Log.Info($"[Car] {name} is in garage slot {index} (the loader-name lookup said {preferredIndex}).");
			if ((uint)index >= (uint)cars.Length)
			{
				Log.Error($"[CarSpawnManager->LoadJobCar] {name} has no garage slot (lookup {preferredIndex} of {cars.Length}); it could not be sent.");
				yield break;
			}

			ModNewCarData modCarData = null;
			for (var attempt = 1; attempt <= 3 && modCarData == null; attempt++)
			{
				try
				{
					var carData = cars[index];
					modCarData = new ModNewCarData(carData, carLoader.placeNo, carLoader.orderConnection);
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
				Log.Error($"[CarSpawnManager->LoadJobCar] {name} (loader {carLoaderID}) could not be sent to the other players.");
				yield break;
			}

			Log.Info($"[Car] Job car {name} (loader {carLoaderID}) sent to the other players.");
			ClientSend.LoadCarPacket(modCarData, carLoaderID);
		}
	}

	/// <summary>
	/// The game's garage slot for a car: the loader-name lookup when it points at the right car, otherwise a scan by
	/// car name. A freshly loaded job car has been reported in the wrong slot, and one bad lookup must not lose it.
	/// </summary>
	private static int FindGarageIndex(Il2CppReferenceArray<NewCarData> cars, string carName, int preferred)
	{
		if (cars == null) return -1;
		if ((uint)preferred < (uint)cars.Length && HoldsCar(cars[preferred], carName)) return preferred;
		for (var i = 0; i < cars.Length; i++)
			if (i != preferred && HoldsCar(cars[i], carName)) return i;
		return (uint)preferred < (uint)cars.Length ? preferred : -1;
	}

	private static bool HoldsCar(NewCarData data, string carName)
	{
		try
		{
			return data != null && string.Equals(data.carToLoad, carName, StringComparison.Ordinal);
		}
		catch (Exception)
		{
			return false;
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