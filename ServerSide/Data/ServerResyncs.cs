using System;
using System.Collections.Generic;
using CMS21Together.Shared.Data;
using CMS21Together.Shared.Data.Vanilla;
using CMS21Together.Shared.Data.Vanilla.Cars;
using CMS21Together.Shared.Data.Vanilla.GarageTool;
using MelonLoader;
using CMS21Together.Shared;

namespace CMS21Together.ServerSide.Data;

public static class ServerResyncs
{
	public static void ResyncCar(int playerID, int carLoaderID)
	{
		// The car may have been deleted (or never existed) by the time the request arrives: nothing to resend.
		if (!ServerData.Instance.CarSpawnDatas.TryGetValue(carLoaderID, out var carToResync) ||
		    !ServerData.Instance.CarPartInfo.TryGetValue(carLoaderID, out var carInfo))
		{
			Log.Debug($"[ServerResyncs] Player #{playerID} asked for car {carLoaderID}, which does not exist.");
			return;
		}
		
		Log.Debug($"Sent a resync car from: {carLoaderID}  {carToResync.CarInfoData.CarFrom}");
		
		ServerSend.LoadCarPacket(playerID, carToResync, carLoaderID, true);

		foreach (KeyValuePair<int, ModCarPart> partsReference in carInfo.BodyPartsReferences)
		{
			ServerSend.BodyPartPacket(playerID, partsReference.Value, carLoaderID, true);
		}
		
		foreach (KeyValuePair<int,Dictionary<int,ModPartScript>> partsReference in carInfo.OtherPartsReferences)
		{
			foreach (KeyValuePair<int,ModPartScript> modPartScript in partsReference.Value)
			{
				ServerSend.PartScriptPacket(playerID, modPartScript.Value, carLoaderID, true);
				Log.Debug("Sent part.");
			}
		}
		
		foreach (KeyValuePair<int, ModPartScript> partsReference in carInfo.DriveshaftPartsReferences)
		{
			ServerSend.PartScriptPacket(playerID, partsReference.Value, carLoaderID, true);
		}
		
		foreach (KeyValuePair<int, ModPartScript> partsReference in carInfo.EnginePartsReferences)
		{
			ServerSend.PartScriptPacket(playerID, partsReference.Value, carLoaderID, true);
		}
		
		foreach (KeyValuePair<int,Dictionary<int,ModPartScript>> partsReference in carInfo.SuspensionPartsReferences)
		{
			foreach (KeyValuePair<int,ModPartScript> modPartScript in partsReference.Value)
			{
				ServerSend.PartScriptPacket(playerID, modPartScript.Value, carLoaderID, true);
				Log.Debug("Sent part.");
			}
		}
		if (ServerData.Instance.LifterStates.TryGetValue(carLoaderID, out var lifter) && lifter != ModLifterState.ground)
			ServerSend.LifterPacket(playerID, lifter, carLoaderID, true);
		// Phase 8 (assessment): tuning, alignment, dyno and examined results of this car go with it.
		Server.Instance?.FeatureRelay?.SendCarStates(playerID, carLoaderID);
		Log.Debug("[ServerResyncs->ResyncCar] Resent car info to client!");
	}
	
	/// <summary>Every car in the garage (late join, rejoin).</summary>
	public static void ResyncAllCars(int playerID)
	{
		foreach (var carLoaderID in new List<int>(ServerData.Instance.CarSpawnDatas.Keys))
			ResyncCar(playerID, carLoaderID);
	}

	/// <summary>The order board and the accepted orders (late join, rejoin).</summary>
	public static void ResyncJobs(int playerID)
	{
		foreach (var job in ServerData.Instance.jobs) ServerSend.JobPacket(playerID, job, true);
		foreach (var job in ServerData.Instance.selectedJobs) ServerSend.SelectedJobPacket(playerID, job, true, true);
	}

	public static void ResyncEngineStand(int fromClient, bool alt)
	{
		Log.Debug($"Client asked for es resync!");
		if (alt)
		{
			if (ServerData.Instance.engineStand2 != null && ServerData.Instance.engineStand2.engineGroupItem != null)
			{
				ServerSend.EngineStandSetGroupPacket(fromClient, ServerData.Instance.engineStand2.engineGroupItem,  ServerData.Instance.engineStand2.position, true, true);
				foreach (KeyValuePair<int,ModPartScript> part in ServerData.Instance.engineStand2.parts)
				{
					ServerSend.PartScriptPacket(fromClient, part.Value, -2, true);
					Log.Debug($"Sent Engine Stand part {part.Value.id}!");
				}
				Log.Debug($"Sent Engine Stand Resync!");
			}
		}
		else
		{
			if (ServerData.Instance.engineStand != null && ServerData.Instance.engineStand.engineGroupItem != null)
			{
				ServerSend.EngineStandSetGroupPacket(fromClient, ServerData.Instance.engineStand.engineGroupItem, ServerData.Instance.engineStand.position, false, true);
				foreach (KeyValuePair<int, ModPartScript> part in ServerData.Instance.engineStand.parts)
				{
					ServerSend.PartScriptPacket(fromClient, part.Value, -1, true);
				}
				Log.Debug($"Sent Engine Stand Resync!");
			}
		}
		
	}

	public static void ResyncTools(int fromClient)
	{
		foreach (KeyValuePair<ModIOSpecialType, ModCarPlace> tool in ServerData.Instance.toolsPosition)
		{
			ServerSend.ToolsMovePacket(fromClient, tool.Key, tool.Value, false, true);
		}
	}

	/// <summary>Contents of the spring clamp, tire changer and wheel balancer, for a joiner or a scene return.</summary>
	public static void ResyncStations(int playerID)
	{
		var data = ServerData.Instance;
		if (data == null) return;
		if (data.springClamp.groupItem != null)
			ServerSend.SetSpringClampPacket(playerID, data.springClamp.groupItem, true, true, true);
		if (data.tireChanger.groupItem != null)
			ServerSend.SetTireChangerPacket(playerID, data.tireChanger.groupItem, true, true, true);
		if (data.wheelBalancer.groupItem != null)
			ServerSend.WheelBalancerPacket(playerID, ModWheelBalancerActionType.setGroup, data.wheelBalancer.groupItem, true);
	}
	
	public static void ResyncPark(int fromClient)
	{
		foreach (KeyValuePair<int, ModNewCarData> car in ServerData.Instance.CarOnPark)
		{
			ServerSend.AddCarToParkPacket(fromClient, car.Value, car.Key, true);
		}
	}

	public static void ResyncUpgrade(int fromClient)
	{
		foreach (KeyValuePair<string,GarageUpgrade> upgrade in ServerData.Instance.garageUpgrades)
		{
			ServerSend.GarageUpgradePacket(fromClient, upgrade.Value, true);
		}
	}
}