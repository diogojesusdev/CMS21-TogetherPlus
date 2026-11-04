using System.Collections;
using System.Collections.Generic;
using System.Linq;
using CMS21Together.ClientSide.Data.Garage.Car;
using CMS21Together.ClientSide.Data.Handle;
using CMS21Together.Shared;
using CMS21Together.Shared.Data;
using MelonLoader;
using UnityEngine;


namespace CMS21Together.ClientSide.Data.Garage;

public static class GarageResync
{
	public static IEnumerator ResyncCars()
	{
		yield return new WaitForEndOfFrame();
		Log.Debug("Remove all car !");
		List<ModCar> carsToCheck = ClientData.Instance.loadedCars.Values.ToList();
		for (int i = 0; i < carsToCheck.Count; i++)
		{
			ModCar car = carsToCheck[i];
			if (car.needResync)
			{
				CarSpawnHooks.listenToDelete = false;
				GameData.Instance.carLoaders[car.carLoaderID].DeleteCar();
				yield return new WaitForEndOfFrame();
				ClientData.Instance.loadedCars.Remove(car.carLoaderID);
				ClientSend.ResyncCar(car.carLoaderID);
				Log.Debug($"Asked resync for {car.carLoaderID} ({car.carID}) to server!");
			}
		}

	}

	public static IEnumerator ResyncGarage()
	{
		// Phase 8 (assessment): a load cut short (disconnect, scene change) never resyncs into a later session.
		var session = ClientData.Instance;
		while (!GameLoadHook.IsGameReady())
		{
			if (ClientData.Instance != session) yield break;
			yield return new WaitForSeconds(0.5f);
		}
		if (ClientData.Instance != session || !Client.Instance.isConnected) yield break;
		yield return new WaitForEndOfFrame();
		
		MelonCoroutines.Start(ResyncCars());
		yield return new WaitForEndOfFrame();
		if (ClientData.Instance.lateJoin)
		{
			// Late join or rejoin: the cars and orders that appeared before this player came.
			ClientData.Instance.lateJoin = false;
			ClientSend.ResyncCar(-1);
			ClientSend.ResyncJobs();
			yield return new WaitForEndOfFrame();
		}
		ClientSend.ResyncTools();
		/*yield return new WaitForEndOfFrame();
		ClientSend.ResyncPark();*/
		yield return new WaitForEndOfFrame();
		ClientSend.ResyncUpgrade();
		yield return new WaitForEndOfFrame();
		ClientSend.ResyncEngineStandPacket(true);
		ClientSend.ResyncEngineStandPacket(false);
	}


}