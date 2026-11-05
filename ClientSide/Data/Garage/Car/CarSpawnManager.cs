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
using CMS21Together.Shared.Safety;

namespace CMS21Together.ClientSide.Data.Garage.Car;

public static class CarSpawnManager
{
	// The game saves the car into the profile asynchronously; on a slow frame that takes seconds, not frames.
	private const float SendWindowSeconds = 20f;
	private const float PollSeconds = 0.25f;
	private const float ResaveSeconds = 2f;

	public static IEnumerator LoadCar(NewCarData carData, int carLoaderID, int placeNo)
	{
		if (ClientData.Instance.loadedCars.ContainsKey(carLoaderID)) yield break;

		var car = new ModCar(carLoaderID, carData.carToLoad, carData.configVersion, placeNo, carData.customerCar);
		Log.Info($"[Car] Local load of {carData.carToLoad} on loader {carLoaderID} (place {placeNo}), sending it.");
		ClientSend.LoadCarPacket(new ModNewCarData(carData, placeNo), carLoaderID);

		// The garage's own car loop starts this before GameLoadHook.CustomLoad has created GameData: wait for it
		// instead of dereferencing null, and give up if the session ends while waiting.
		while (GameData.Instance == null || GameData.Instance.carLoaders == null)
		{
			if (!Client.Instance.isConnected) yield break;
			yield return YieldInstructions.WaitForEndOfFrame;
		}
		if ((uint)carLoaderID >= (uint)GameData.Instance.carLoaders.Length)
		{
			Log.Error($"[CarSpawnManager->LoadCar] {carData.carToLoad} was reported on loader {carLoaderID}, " +
			          $"but the garage has {GameData.Instance.carLoaders.Length} loaders; its parts stay untracked.");
			yield break;
		}

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

			// The game writes the car into the profile asynchronously, so carsInGarage does not hold it for a while
			// after the loader says the car is loaded. Looking exactly once (and giving up when the slot was still
			// empty) is why job cars stopped being sent. Poll until it turns up instead, re-asking for the save.
			var preferredIndex = Helper.GetIndexFromCarLoaderName(carLoader.name);
			ModNewCarData modCarData = null;
			var lastSlots = "(not read)";
			var sinceSave = float.MaxValue;
			var warned = false;

			for (var waited = 0f; modCarData == null && waited < SendWindowSeconds; waited += PollSeconds)
			{
				if (carLoader == null || !carLoader.IsCarLoaded()) yield break;
				if (!ClientData.Instance.loadedCars.ContainsKey(carLoaderID)) yield break;

				// Ask the game to write the car out again if the last request has had time to finish.
				if (sinceSave >= ResaveSeconds)
				{
					Guard.Run("save the job car into the profile", () => carLoader.SaveCarToFile());
					sinceSave = 0f;
				}

				yield return new WaitForSeconds(PollSeconds);
				sinceSave += PollSeconds;

				// Read it through CarDataReader, never through the raw array: the array class caches its element
				// stride in a static constructor that can run before NewCarData's, and a stale pointer inside the
				// struct takes the game down in il2cpp_object_get_class (see the car order crash report).
				var cars = GameManager.Instance.GameDataManager.CurrentProfileData?.carsInGarage;
				if (cars == null)
				{
					Log.Error($"[CarSpawnManager->LoadJobCar] The profile has no car array; {name} (loader {carLoaderID}) could not be sent.");
					yield break;
				}

				var index = CarDataReader.FindSlot(cars, name, preferredIndex);
				if (index < 0)
				{
					lastSlots = CarDataReader.Describe(cars);
					if (!warned && waited >= 2f)
					{
						warned = true; // one breadcrumb, not one per poll
						Log.Info($"[Car] {name} (loader {carLoaderID}) is not in the garage array yet after {waited:0.0}s; still waiting. The slots hold: {lastSlots}");
					}
					continue;
				}

				if (index != preferredIndex)
					Log.Info($"[Car] {name} is in garage slot {index} (the loader-name lookup said {preferredIndex}).");

				try
				{
					if (CarDataReader.TryRead(cars, index, out var carData, out var problem))
						modCarData = new ModNewCarData(carData, carLoader.placeNo, carLoader.orderConnection);
					else if (problem != null)
						Log.Warn($"[CarSpawnManager->LoadJobCar] {name} (loader {carLoaderID}, slot {index}) is not readable yet: {problem}");
				}
				catch (Exception ex)
				{
					Log.Warn($"[CarSpawnManager->LoadJobCar] Reading {name} (loader {carLoaderID}, slot {index}) failed: {ex.Message}");
				}
			}

			if (modCarData == null)
			{
				Log.Error($"[CarSpawnManager->LoadJobCar] {name} (loader {carLoaderID}) never turned up in the garage array within {SendWindowSeconds:0}s and was not sent to the other players. The slots hold: {lastSlots}");
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

		// A car we cannot trust is dropped and asked for again: building game objects out of it is how a bad payload
		// becomes a native crash, which nothing can recover from (see CarPayloadRules).
		if (data == null)
		{
			Log.Warn($"[Car] The car for loader {carLoaderID} was not applied (no data); asking for it again.");
			ClientSend.ResyncCar(carLoaderID);
			yield break;
		}

		if (data.IfBroken(out var broken))
		{
			Log.Warn($"[Car] The car for loader {carLoaderID} was not applied ({broken}); asking for it again.");
			ClientSend.ResyncCar(carLoaderID);
			yield break;
		}

		if (GameData.Instance?.carLoaders == null || (uint)carLoaderID >= (uint)GameData.Instance.carLoaders.Length)
		{
			Log.Warn($"[Car] The car for loader {carLoaderID} was not applied (no such car loader here); asking for it again.");
			ClientSend.ResyncCar(carLoaderID);
			yield break;
		}

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
		var carData = Guard.Try("build the car from the host", () => data.ToGame(), null, () => ClientSend.ResyncCar(carLoaderID));
		if (carData == null) yield break;

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