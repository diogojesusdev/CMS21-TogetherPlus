using System;
using System.Collections.Generic;
using System.Linq;
using CMS21Together.ClientSide.Data;
using CMS21Together.ClientSide.Data.Player;
using CMS21Together.Shared.Data;
using CMS21Together.Shared.Data.Vanilla;
using CMS21Together.Shared.Data.Vanilla.Cars;
using CMS21Together.Shared.Data.Vanilla.GarageTool;
using CMS21Together.Shared.Data.Vanilla.Jobs;
using MelonLoader;
using CMS21Together.Shared;

namespace CMS21Together.ServerSide.Data;

public class ServerData
{
	public static ServerData Instance;
	public Dictionary<ModIOSpecialType, ModCarPlace> toolsPosition = new();
	
	public Dictionary<int, ModCarInfo> CarPartInfo = new();
	public Dictionary<int, ModNewCarData> CarSpawnDatas = new();

	/// <summary>Last lifter level reported per car loader (Phase 5).</summary>
	public Dictionary<int, ModLifterState> LifterStates = new();
	public Dictionary<int, ModNewCarData> CarOnPark = new();
	public ModEngineStand engineStand = new(null);
	public ModEngineStand engineStand2 = new(null);
	public float engineStandAngle;
	public float engineStand2Angle;

	public Dictionary<int, UserData> connectedClients = new();

	public Dictionary<string, GarageUpgrade> garageUpgrades = new();


	public List<ModJob> jobs = new();
	public List<ModJob> selectedJobs = new();

	public GarageTool springClamp = new();
	public GarageTool tireChanger = new();
	public GarageTool wheelBalancer = new();

	public void SetGarageUpgrade(GarageUpgrade upgrade)
	{
		garageUpgrades[upgrade.upgradeID] = upgrade;
	}

	public void DeleteCar(int carLoaderID)
	{
		if (CarSpawnDatas.ContainsKey(carLoaderID))
			CarSpawnDatas.Remove(carLoaderID);
		if (CarPartInfo.ContainsKey(carLoaderID))
			CarPartInfo.Remove(carLoaderID);
		LifterStates.Remove(carLoaderID);
		Server.Instance?.FeatureRelay?.ForgetCar(carLoaderID);
	}

	public void UpdatePartScripts(ModPartScript partScript, int carLoaderID)
	{
		if (carLoaderID == -1 || carLoaderID == -2)
		{
			UpdateEngineStand(partScript, carLoaderID == -2);
			Log.Debug("received a enginestand part.");
			return;
		}
		
		if (!KnownCar(carLoaderID)) return;
		if (!Instance.CarPartInfo.ContainsKey(carLoaderID))
			Instance.CarPartInfo.Add(carLoaderID, new ModCarInfo());

		var carInfos = Instance.CarPartInfo[carLoaderID];
		var key = partScript.partID;
		var index = partScript.partIdNumber;

		switch (partScript.type)
		{
			case ModPartType.engine:
				carInfos.EnginePartsReferences[key] = partScript;
				break;
			case ModPartType.suspension:
				if (!carInfos.SuspensionPartsReferences.ContainsKey(key))
					carInfos.SuspensionPartsReferences.Add(key, new Dictionary<int, ModPartScript>());

				if (!carInfos.SuspensionPartsReferences[key].ContainsKey(index))
					carInfos.SuspensionPartsReferences[key].Add(index, partScript);
				else
					carInfos.SuspensionPartsReferences[key][index] = partScript;

				break;
			case ModPartType.other:
				if (!carInfos.OtherPartsReferences.ContainsKey(key))
					carInfos.OtherPartsReferences.Add(key, new Dictionary<int, ModPartScript>());

				if (!carInfos.OtherPartsReferences[key].ContainsKey(index))
					carInfos.OtherPartsReferences[key].Add(index, partScript);
				else
					carInfos.OtherPartsReferences[key][index] = partScript;
				break;
			case ModPartType.driveshaft:
				carInfos.DriveshaftPartsReferences[key] = partScript;
				break;
		}
	}

	private void UpdateEngineStand(ModPartScript partScript, bool alt)
	{
		if (!alt)
			engineStand.parts[partScript.partID] = partScript;
		else
			engineStand2.parts[partScript.partID] = partScript;
	}

	/// <summary>
	/// A write for a car the host does not know (deleted, or a late packet) must not bring it back as an empty entry:
	/// later resyncs would send that ghost to everyone (Phase 2 finding).
	/// </summary>
	private bool KnownCar(int carLoaderID)
	{
		if (Instance.CarPartInfo.ContainsKey(carLoaderID) || Instance.CarSpawnDatas.ContainsKey(carLoaderID)) return true;
		Log.Debug($"[ServerData] Ignored a part write for car {carLoaderID}, which does not exist.");
		return false;
	}

	public void UpdateBodyParts(ModCarPart carPart, int carLoaderID)
	{
		if (!KnownCar(carLoaderID)) return;
		if (!Instance.CarPartInfo.ContainsKey(carLoaderID))
			Instance.CarPartInfo.Add(carLoaderID, new ModCarInfo());

		var carInfos = Instance.CarPartInfo[carLoaderID];
		carInfos.BodyPartsReferences[carPart.carPartID] = carPart;
	}

	public void ChangePosition(int carLoaderID, int placeNo)
	{
		if (Instance.CarPartInfo.TryGetValue(carLoaderID, out var info)) info.placeNo = placeNo;
		if (Instance.CarSpawnDatas.TryGetValue(carLoaderID, out var info2)) info2.carPosition = placeNo;
	}

	public void AddJob(ModJob job)
	{
		// The host's client re-sends its whole board after every garage load, and a late join replays the list: the
		// server mirrors one entry per order id.
		if (job == null || jobs.Any(j => j.id == job.id)) return;
		jobs.Add(job);
	}

	public void RemoveJob(int jobID)
	{
		var job = jobs.Find(j => j.id == jobID);
		if (job != null)
			jobs.Remove(job);
	}

	public void SetLoadJobCar(ModCar carData)
	{
		if (Instance.CarPartInfo.ContainsKey(carData.carLoaderID)) return;

		Instance.CarPartInfo[carData.carLoaderID] = new ModCarInfo();
		ModCarInfo data = Instance.CarPartInfo[carData.carLoaderID];

		data.carToLoad = carData.carID;
		data.carLoaderID = carData.carLoaderID;
		data.configVersion = carData.configVersion;
		data.placeNo = carData.carPosition;
		data.customerCar = carData.customerCar;
	}

	public void UpdateSelectedJobs(ModJob job, bool action)
	{
		if (action)
		{
			if (selectedJobs.All(j => j.id != job.id)) selectedJobs.Add(job);
		}
		else
		{
			if (selectedJobs.Any(j => j.id == job.id)) selectedJobs.Remove(selectedJobs.First(j => j.id == job.id));
		}
	}

	public void ChangeToolPosition(ModIOSpecialType tool, ModCarPlace place)
	{
		toolsPosition[tool] = place;
	}

	public void SetSpringClampState(bool remove, ModGroupItem item)
	{
		if (remove)
		{
			springClamp.isMounted = true;
			springClamp.groupItem = null;
			return;
		}

		springClamp.isMounted = false;
		springClamp.groupItem = item;
	}

	public void SetTireChangerState(bool remove, ModGroupItem item)
	{
		if (remove)
		{
			tireChanger.isMounted = true;
			tireChanger.groupItem = null;
			return;
		}

		tireChanger.isMounted = false;
		tireChanger.groupItem = item;
	}

	public void SetWheelBalancerState(ModGroupItem item)
	{
		if (item == null)
		{
			wheelBalancer.isMounted = false;
			wheelBalancer.additionalState = false;
			wheelBalancer.groupItem = null;
			return;
		}

		if (!wheelBalancer.additionalState)
		{
			wheelBalancer.groupItem = item;
			wheelBalancer.additionalState = true;
			wheelBalancer.isMounted = true;
			return;
		}

		wheelBalancer.groupItem = item;
		wheelBalancer.additionalState = true;
		wheelBalancer.isMounted = true;
	}

	public void EndJob(ModJob job)
	{
		if (selectedJobs.Any(j => j.id == job.id)) selectedJobs.Remove(selectedJobs.First(j => j.id == job.id));
	}

	public void UpdateFluid(ModFluidData fluid, int carLoaderID)
	{
		if (fluid?.CarFluid == null) return;
		if (!CarSpawnDatas.TryGetValue(carLoaderID, out var car) || car == null) return;

		// The live packet already reaches every connected player; this keeps the stored car (late join, scene
		// return, resync) in step, or the resynced copy reverts the fluid level.
		var fluids = car.FluidsData;
		switch ((ModCarFluidType)fluid.CarFluid.FluidType)
		{
			case ModCarFluidType.EngineOil:
				fluids.Oil = fluid;
				break;
			case ModCarFluidType.Brake:
				fluids.Brake = MergeFluid(fluids.Brake, fluid);
				break;
			case ModCarFluidType.EngineCoolant:
				fluids.EngineCoolant = MergeFluid(fluids.EngineCoolant, fluid);
				break;
			case ModCarFluidType.PowerSteering:
				fluids.PowerSteering = MergeFluid(fluids.PowerSteering, fluid);
				break;
			case ModCarFluidType.WindscreenWash:
				fluids.WindscreenWash = MergeFluid(fluids.WindscreenWash, fluid);
				break;
			default:
				return;
		}
		car.FluidsData = fluids;
	}

	private static List<ModFluidData> MergeFluid(List<ModFluidData> list, ModFluidData fluid)
	{
		list ??= new List<ModFluidData>();
		var id = fluid.CarFluid.ID;
		for (var i = 0; i < list.Count; i++)
			if (list[i]?.CarFluid != null && list[i].CarFluid.ID == id)
			{
				list[i] = fluid;
				return list;
			}
		list.Add(fluid);
		return list;
	}

	public void SetEngineOnStand(ModGroupItem engineGroup, Vector3Serializable position, bool alt)
	{
		if (!alt)
		{
			engineStand = new ModEngineStand(null);
			engineStand.engineGroupItem = engineGroup;
			engineStand.position = position;
		}
		else
		{
			engineStand2 = new ModEngineStand(null);
			engineStand2.engineGroupItem = engineGroup;
			engineStand2.position = position;
		}
	}

	public void ClearEngineFromStand(bool alt)
	{
		Log.Debug("SV: Clear engine.");
		if (!alt)
			engineStand = new ModEngineStand(null);
		else
			engineStand2 = new ModEngineStand(null);
	}

	public void IncreaseStandAngle(float val, bool alt)
	{
		if (!alt)
			engineStandAngle = val;
		else
			engineStand2Angle = val;
	}

	public void SetPlayerInfo(int id, PlayerInfo info)
	{
		connectedClients[id].playerExp = info.playerExp;
		connectedClients[id].playerLevel = info.playerLevel;
		connectedClients[id].playerSkillPoints = info.skillPoints;
		connectedClients[id].position = info.position;
		connectedClients[id].rotation = info.rotation;
	}

	public void SetCarColor(ModColor color)
	{
		foreach (ModCarInfo car in CarPartInfo.Values)
		{
			if (car.placeNo == 5 && CarSpawnDatas.TryGetValue(car.carLoaderID, out var spawn))
			{
				spawn.color = color;
			}
		}
	}

	/// <summary>Colour of the car in the paint shop (place 5) as the host knows it, or null.</summary>
	public ModColor PaintshopColor()
	{
		foreach (ModCarInfo car in CarPartInfo.Values)
			if (car.placeNo == 5 && CarSpawnDatas.TryGetValue(car.carLoaderID, out var spawn) && spawn.color != null)
				return spawn.color;
		return null;
	}

	public void UpdatePartInfo(ModPartInfo info, bool isBody, bool success)
	{
		// The shared inventory keeps the item; a repair changes it in place (same UID).
		var inventory = Server.Instance?.Inventory?.Authority;
		if (inventory != null && info?.Item != null && inventory.TryGet(info.Item.UID, out var item))
		{
			item.Condition = (success ? info.SuccessCondition : info.FailCondition);
			item.RepairAmount++;
			if (isBody)
			{
				item.Dent = (success ? info.DentSuccessCondition : info.DentFailCondition);
			}
		}
	}
	
	public void SetCarWash(int loaderID, bool interior)
	{
		if (!CarPartInfo.TryGetValue(loaderID, out var car))
			return;

		// `interior == false` is an exterior wash (see CarWashLogic): it cleans the body panels, while the interior
		// branch reconditions the seats/bench/steering wheel. The two branches used to be swapped here.
		if (!interior)
		{
			foreach (KeyValuePair<int, ModCarPart> part in car.BodyPartsReferences)
			{
				if (!part.Value.unmounted)
				{
						part.Value.washFactor = 1;
						part.Value.Dust = 0;
					
				}
			}
			ModCarPart detailsPart = GetCarPart(loaderID, "details");
			if (detailsPart != null)
			{
				detailsPart.Dust = 0;
				ModCarPart details2 = GetCarPart(loaderID, "details2");
				if (details2 != null) details2.Dust = 0;
				ModCarPart details3 = GetCarPart(loaderID, "details3");
				if (details3 != null) details3.Dust = 0;
			}
		}
		else
		{
			string[] interiorParts = { "benchFront", "bench", "steeringWheel", "seatLeft", "seatRight", "details", "details2", "details3" };
			foreach (string partName in interiorParts)
			{
				ModCarPart part = GetCarPart(loaderID, partName);
				if (part != null && !part.unmounted)
				{
					part.condition = 1;
					part.Dust = 0;
					if (partName == "details")
					{
						ModCarPart details2 = GetCarPart(loaderID, "details2");
						if (details2 != null) details2.Dust = 0;

						ModCarPart details3 = GetCarPart(loaderID, "details3");
						if (details3 != null) details3.Dust = 0;
					}
				}
			}
		}
	}

	private ModCarPart GetCarPart(int loaderID, string partName)
	{
		return CarPartInfo[loaderID].BodyPartsReferences
			.Values.FirstOrDefault(p => p.name == partName);
	}

	public void SetWelder(int loaderID)
	{
		if (!CarPartInfo.ContainsKey(loaderID))
			return;

		// `body`/`details` are only present here if a client actually sent that panel; the shell is never taken off.
		// Dereferencing a missing panel used to throw on every weld, so the packet was never relayed.
		var body = GetCarPart(loaderID, "body");
		var details = GetCarPart(loaderID, "details");
		if (body != null)
		{
			body.condition = 1;
			body.dent = 1;
		}
		else
			Log.Warn($"[ServerData] Weld on car {loaderID}: the host has no stored body panel; the car resync will heal it.");
		if (details != null)
			details.dent = 1;

		// Keep the resync blob in step (late join / scene return) when the panel is in it.
		if (!CarSpawnDatas.TryGetValue(loaderID, out var spawn) || spawn?.BodyPartsData == null) return;
		foreach (var stored in spawn.BodyPartsData)
		{
			if (stored == null) continue;
			if (string.Equals(stored.Id, body != null ? body.name : "body", StringComparison.OrdinalIgnoreCase))
			{
				stored.Condition = 1;
				stored.Dent = 1;
			}
			else if (string.Equals(stored.Id, details != null ? details.name : "details", StringComparison.OrdinalIgnoreCase))
				stored.Dent = 1;
		}
	}

	public void AddCarToPark(ModNewCarData car, int index)
	{
		CarOnPark[index] = car;
	}

	public void RemoveCarFromPark(int index)
	{
		if (CarOnPark.ContainsKey(index))
			CarOnPark.Remove(index);
	}
}


public class GarageTool
{
	public bool additionalState;
	public ModGroupItem groupItem;
	public bool isMounted;
}

public class ModCarInfo
{
	public int carLoaderID;
	public string carToLoad;
	public int configVersion;
	public bool customerCar;
	public int placeNo;
	public Dictionary<int, ModCarPart> BodyPartsReferences = new();
	public Dictionary<int, ModPartScript> DriveshaftPartsReferences = new();
	public Dictionary<int, ModPartScript> EnginePartsReferences = new();
	public Dictionary<int, Dictionary<int, ModPartScript>> OtherPartsReferences = new();
	public Dictionary<int, Dictionary<int, ModPartScript>> SuspensionPartsReferences = new();
}