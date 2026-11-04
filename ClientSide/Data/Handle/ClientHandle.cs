using System;
using CMS21Together.ClientSide.Data.Garage;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CMS21Together.ClientSide.Data.Garage.Campaign;
using CMS21Together.ClientSide.Data.Garage.Car;
using CMS21Together.ClientSide.Data.Garage.Tools;
using CMS21Together.ClientSide.Data.NewUI;
using CMS21Together.ClientSide.Data.Player;
using CMS21Together.Shared;
using CMS21Together.Shared.Data;
using CMS21Together.Shared.Data.Vanilla;
using CMS21Together.Shared.Data.Vanilla.Cars;
using CMS21Together.Shared.Data.Vanilla.GarageTool;
using CMS21Together.Shared.Data.Vanilla.Jobs;
using CMS21Together.Shared.Persistence;
using MelonLoader;
using UnityEngine;

namespace CMS21Together.ClientSide.Data.Handle;

public static class ClientHandle
{
	public static void IdentityRequestPacket(Packet packet)
	{
		var saveId = packet.Read<string>();
		// Only the first request is answered, so a host cannot collect this player's tokens for other saves.
		if (Client.Instance.identitySent) return;
		Client.Instance.identitySent = true;
		Client.Instance.HostSaveId = SaveIds.IsValid(saveId) ? saveId : null;
		ClientSend.ConnectValidationPacket(saveId);
		// Sent right behind the validation (same ordered channel): the host reads them once this player is in the lobby.
		Player.Inventory.RequestSnapshots();
		PlusSaves.SendOffer(Client.Instance.HostSaveId);
	}

	/// <summary>A new profile's item UID counter, applied once the garage is loaded (the load sets its own).</summary>
	public static void UidRangePacket(Packet packet)
	{
		var start = packet.Read<long>();
		MelonCoroutines.Start(ApplyUidRange(ClientData.Instance, start));
	}

	private static System.Collections.IEnumerator ApplyUidRange(ClientData session, long start)
	{
		while (!GameLoadHook.IsGameReady())
		{
			if (ClientData.Instance != session) yield break;
			yield return new WaitForSeconds(0.1f);
		}
		if (ClientData.Instance != session || !Client.Instance.isConnected) yield break;
		UIDManager.LastUID = start;
		Log.Info($"[ClientHandle] Item UIDs start at {start}.");
	}

	public static void PlayerLeftPacket(Packet packet)
	{
		var slot = packet.ReadInt();
		var data = ClientData.Instance;
		if (data == null || !data.connectedClients.TryGetValue(slot, out var user)) return;

		if (slot != ClientData.UserData.playerID)
			user.DestroyPlayer();
		data.connectedClients.Remove(slot);
		Movement.Forget(slot);
		Log.Info($"[ClientHandle] {user.username} left the session.");
		UILobby.RefreshPlayers();
	}
	public static void UserDataPacket(Packet packet)
	{
		var data = packet.Read<UserData>();

		ClientData.Instance.connectedClients[data.playerID] = data;
		UILobby.RefreshPlayers();
		//Log.Info("[ClientHandle->UserDataPacket] Receive userData from server.");
	}

	public static void ContentsInfoPacket(Packet _packet)
	{
		var infos = _packet.Read<ReadOnlyDictionary<string, bool>>();
		ApiCalls.API_M2(infos);
	}

	public static void ReadyPacket(Packet packet)
	{
		var id = packet.ReadInt();
		var ready = packet.Read<bool>();

		if (!ClientData.Instance.connectedClients.TryGetValue(id, out var user)) return;
		user.isReady = ready;
		UILobby.RefreshPlayers();
	}

	public static void StartPacket(Packet packet)
	{
		var gamemode = packet.Read<Gamemode>();
		var parkCars = packet.Read<Dictionary<int, ModNewCarData>>();
		// Joining a game already running: the first garage load asks for everything that happened before (Phase 5).
		ClientData.Instance.lateJoin = packet.Read<bool>();

		var data = new ModSaveData();
		data.selectedGamemode = gamemode;

		SavesManager.LoadSave(data, parkCars,true);
	}
	
	public static void SpawnPacket(Packet packet)
	{
		int playerMoney = packet.ReadInt();
		int playerExp = packet.ReadInt();
		int playerLevel = packet.ReadInt();
		int skillPoints = packet.ReadInt();
		Vector3Serializable position = packet.Read<Vector3Serializable>();
		QuaternionSerializable rotation = packet.Read<QuaternionSerializable>();
		Dictionary<string, List<bool>> skills = packet.Read<Dictionary<string, List<bool>>>();
		long startItemUID = packet.Read<long>();
		int missionFinished = packet.ReadInt();
		bool missionInProgress = packet.Read<bool>();
		
		MelonCoroutines.Start(ClientData.Instance.SpawnPlayer(playerMoney, playerExp, playerLevel, position.toVector3(),
			rotation.toQuaternion(), skillPoints, skills, startItemUID, missionFinished, missionInProgress));
	}

	public static void PositionPacket(Packet packet)
	{
		var id = packet.ReadInt();
		var position = packet.Read<Vector3Serializable>();
		Movement.UpdatePosition(id, position);
		packet.Dispose();
	}

	public static void RotationPacket(Packet packet)
	{
		var id = packet.ReadInt();
		var rotation = packet.Read<QuaternionSerializable>();
		Rotation.UpdateRotation(id, rotation);
		packet.Dispose();
	}


	public static void LifterPacket(Packet packet)
	{
		if (SceneManager.CurrentScene() != GameScene.garage) return;

		var state = packet.Read<ModLifterState>();
		var carLoaderID = packet.ReadInt();
		if (GameData.Instance == null || carLoaderID < 0 || carLoaderID >= GameData.Instance.carLoaders.Length) return;
		if (!ClientData.Instance.loadedCars.ContainsKey(carLoaderID)) return;
		MelonCoroutines.Start(LifterLogic.MoveTo(carLoaderID, state));
	}

	public static void SetTireChangerPacket(Packet packet)
	{
		var item = packet.Read<ModGroupItem>();
		var instant = packet.Read<bool>();
		var connect = packet.Read<bool>();

		Garage.Tools.TireChangerLogic.listen = false;
		GameData.Instance.tireChanger.SetGroupOnTireChanger(item.ToGame(), instant, connect);
	}

	public static void ClearTireChangerPacket(Packet packet)
	{
		GameData.Instance.tireChanger.ResetActions();
	}
	public static void WheelBalancePacket(Packet packet)
	{
		if(SceneManager.CurrentScene() != GameScene.garage) return;
                
		ModWheelBalancerActionType aType = packet.Read<ModWheelBalancerActionType>();
		ModGroupItem _item = null;
		if(aType == ModWheelBalancerActionType.start || aType == ModWheelBalancerActionType.setGroup)
			_item = packet.Read<ModGroupItem>();

		if (aType == ModWheelBalancerActionType.remove)
		{
			GameData.Instance.wheelBalancer.ResetActions();
			GameData.Instance.wheelBalancer.Clear();
		}
		else
		{
			WheelBalancer.listen = false;
			//Log.Info("CL: Received WheelBalance!");
			GameData.Instance.wheelBalancer.SetGroupOnWheelBalancer(_item!.ToGame(_item), true);
		}
	}
	
	public static void OilBinPacket(Packet _packet)
	{
		int carLoaderID = _packet.ReadInt();

		if(SceneManager.CurrentScene() != GameScene.garage) return;
                
		OilBin.listen = false;
		GameData.Instance.carLoaders[carLoaderID].UseOilbin();
	}
	
	public static void WelderPacket(Packet _packet)
	{
		int carLoaderID = _packet.ReadInt();

		MelonCoroutines.Start(Garage.Tools.WelderLogic.UseWelder(carLoaderID));
	}
	
	public static void SetSpringClampPacket(Packet packet)
	{
		var item = packet.Read<ModGroupItem>();
		var instant = packet.Read<bool>();
		var mount = packet.Read<bool>();

		MelonCoroutines.Start(Garage.Tools.SpringClampLogic.Action(item, instant, mount));
	}

	public static void SpringClampClearPacket(Packet packet)
	{
		if (GameData.Instance.springClampLogic.GroupOnSpringClamp != null)
			if (GameData.Instance.springClampLogic.GroupOnSpringClamp.ItemList != null)
			{
				GameData.Instance.springClampLogic.GroupOnSpringClamp.ItemList.Clear();
				Garage.Tools.SpringClampLogic.listen = false;
				GameData.Instance.springClampLogic.ClearSpringClamp();
			}
	}

	public static void ToolsMovePacket(Packet _packet)
	{
		var tool = _packet.Read<ModIOSpecialType>();
		var place = _packet.Read<ModCarPlace>();
		var playSound = _packet.Read<bool>();

		MelonCoroutines.Start(Garage.Tools.ToolsMoveManager.UpdateToolMove((IOSpecialType)tool, place, playSound));
	}

	public static void CarWashPacket(Packet _packet)
	{
		int carLoaderID = _packet.ReadInt();
		bool interior = _packet.Read<bool>();

		MelonCoroutines.Start(Garage.Tools.CarWashLogic.WashCar(carLoaderID, interior));
	}
	
	public static void CarPaintPacket(Packet _packet)
	{
		ModColor color = _packet.Read<ModColor>();
		var own = _packet.UnreadLength() > 0 && _packet.Read<bool>();

		MelonCoroutines.Start(CarPaintLogic.ChangeColor(color, own));
	}
	
	public static void LoadCarPacket(Packet packet)
	{
		var carData = packet.Read<ModNewCarData>();
		var carLoaderID = packet.ReadInt();

		//Log.Info("[ClientHandle->LoadCarPacket] Received new car info.");

		MelonCoroutines.Start(CarSpawnManager.LoadCarFromServer(carData, carLoaderID));
	}

	public static void BodyPartPacket(Packet packet)
	{
		var carPart = packet.Read<ModCarPart>();
		var carLoaderID = packet.ReadInt();

		//Log.Info("[ClientHandle->BodyPartPacket] Receive BodyPart.");
		MelonCoroutines.Start(PartsUpdater.UpdateBodyParts(carPart, carLoaderID));
	}

	public static void PartScriptPacket(Packet packet)
	{
		var partScript = packet.Read<ModPartScript>();
		var carLoaderID = packet.ReadInt();

		Log.Debug("[ClientHandle->PartScriptPacket] Receive PartScript.");
		MelonCoroutines.Start(PartsUpdater.UpdatePartScripts(partScript, carLoaderID));
	}

	public static void DeleteCarPacket(Packet packet)
	{
		var carLoaderID = packet.ReadInt();

		Log.Debug($"[ClientHandle->DeleteCarPacket] Delete Car {carLoaderID}.");
		MelonCoroutines.Start(CarSyncManager.DeleteCar(carLoaderID));
	}

	public static void CarPositionPacket(Packet packet)
	{
		var placeNo = packet.ReadInt();
		var carLoaderID = packet.ReadInt();
		
		Log.Debug($"[ClientHandle->CarPositionPacket] Move {carLoaderID} to {placeNo}.");
		MelonCoroutines.Start(CarSyncManager.ChangePosition(carLoaderID, placeNo));
	}

	public static void GarageUpgradePacket(Packet packet)
	{
		var upgrade = packet.Read<GarageUpgrade>();

		//Log.Info($"[ClientHandle->GarageUpgradePacket] Received upgrade for {upgrade.upgradeID}.");
		MelonCoroutines.Start(GarageUpgradeManager.SetUpgrade(upgrade));
	}

	public static void JobPacket(Packet packet)
	{
		var job = packet.Read<ModJob>();
		
		//Log.Info("[ClientHandle->JobPacket] Received a job.");
		MelonCoroutines.Start(JobManager.AddJob(job));
	}

	public static void JobActionPacket(Packet packet)
	{
		ModJob job = packet.Read<ModJob>();
		var takeJob = packet.Read<bool>();
		
		MelonCoroutines.Start(JobManager.JobAction(job, takeJob));
	}

	public static void SelectedJobPacket(Packet packet)
	{
		var job = packet.Read<ModJob>();
		var action = packet.Read<bool>();

		MelonCoroutines.Start(JobManager.SelectedJob(job, action));
	}

	/// <summary>The host kept higher stored progress than this game reported: show the stored level and XP.</summary>
	public static void StatsFixPacket(Packet packet)
	{
		var exp = packet.ReadInt();
		var level = packet.ReadInt();
		if (exp < 0 || level < 1) return;
		using (ActionGate.Apply())
		{
			GlobalData.PlayerLevel = level;
			GlobalData.PlayerExp = exp;
		}
		try
		{
			UIManager.Get()?.RefreshAllStats();
		}
		catch (Exception ex)
		{
			Log.Warn($"[ClientHandle] Could not refresh the stats: {ex.Message}");
		}
		Log.Info($"[ClientHandle] The host restored this player's level {level} / {exp} XP.");
	}

	public static void EndJobPacket(Packet packet)
	{
		var job = packet.Read<ModJob>();

		MelonCoroutines.Start(JobManager.OnJobComplete(job));
	}
	
	public static void EngineCraneHandlePacket(Packet packet)
	{
		int action = packet.ReadInt();
		int carLoaderID = packet.ReadInt();

		if (action == 1)
		{
			ModGroupItem item = packet.Read<ModGroupItem>();
			MelonCoroutines.Start(EngineCrane.InsertEngineIntoCar(item));
			return;
		}
		MelonCoroutines.Start(EngineCrane.UseEngineCrane(carLoaderID));
	}
	
	public static void EngineSetGroupPacket(Packet packet)
	{
		ModGroupItem engineGroup = packet.Read<ModGroupItem>();
		Vector3Serializable position = packet.Read<Vector3Serializable>();
		bool alt = packet.Read<bool>();
		
		MelonCoroutines.Start(EngineStand.TakeOnEngineFromStand(engineGroup, position, alt));
	}
	public static void EngineTakeOffPacket(Packet packet)
	{
		bool alt = packet.Read<bool>();
		
		MelonCoroutines.Start(EngineStand.TakeOffEngineFromStand(alt));
	}
	
	public static void EngineStandAnglePacket(Packet packet)
	{
		float angle = packet.Read<float>();
		bool alt = packet.Read<bool>();
		
		MelonCoroutines.Start(EngineStand.IncreaseEngineStandAngle(angle, alt));
	}
	
	public static void RepairPartPacket(Packet packet)
	{
		ModPartInfo info = packet.Read<ModPartInfo>();
		bool isBody = packet.Read<bool>();
		bool success = packet.Read<bool>();
		
		MelonCoroutines.Start(RepairPartLogic.RepairAction(info, isBody, success));
	}
	
	public static void CarFluidPacket(Packet packet)
	{
		var carLoaderID = packet.ReadInt();
		ModFluidData fluid = packet.Read<ModFluidData>();
		
		MelonCoroutines.Start(PartsUpdater.UpdateFluid(fluid, carLoaderID));
	}
	
	public static void AddCarToParkPacket(Packet packet)
	{
		ModNewCarData car = packet.Read<ModNewCarData>();
		int index = packet.ReadInt();
		
		MelonCoroutines.Start(ParkHook.AddCarToPark(car, index));
	}
	
	public static void RemoveCarFromParkPacket(Packet packet)
	{
		int index = packet.ReadInt();
		
		MelonCoroutines.Start(ParkHook.RemoveCarFromPark(index));
	}

	public static void SceneChangePacket(Packet packet)
	{
		var scene = packet.Read<GameScene>();
		var id = packet.ReadInt();

		if (!ClientData.Instance.connectedClients.ContainsKey(id)) return;
		ClientData.Instance.connectedClients[id].scene = scene;
		if (scene != SceneManager.CurrentScene())
		{
			// Business Logic: Destroy player when changing to different scene
			ClientData.Instance.connectedClients[id].DestroyPlayer();
		}
		else if (ClientData.Instance.connectedClients[id].userObject == null)
		{
			// Business Logic: Spawn player only if not already spawned
			ClientData.Instance.connectedClients[id].SpawnPlayer();
		}
	}

	public static void PlayerInCarPacket(Packet packet)
	{
		var id = packet.ReadInt();
		var isInCar = packet.Read<bool>();
		var carLoaderID = packet.ReadInt();

		if (!ClientData.Instance.connectedClients.ContainsKey(id)) return;

		var player = ClientData.Instance.connectedClients[id];
		player.isInCar = isInCar;
		player.carLoaderID = carLoaderID;

		// Business Logic: Hide/show player object based on car state
		if (player.userObject != null)
		{
			player.userObject.SetActive(!isInCar);
			Log.Debug($"[ClientHandle->PlayerInCarPacket] Player {player.username} (ID: {id}) car state: InCar={isInCar}, CarLoaderID={carLoaderID}");
		}

		packet.Dispose();
	}

	public static void CarEngineSoundPacket(Packet packet)
	{
		var playerID = packet.ReadInt();
		var carLoaderID = packet.ReadInt();
		var isPlaying = packet.Read<bool>();
		var rpm = packet.Read<float>();

		// Business Logic: Play/stop engine sound for remote player's car
		if (GameData.Instance == null || GameData.Instance.carLoaders == null) return;
		if (carLoaderID < 0 || carLoaderID >= GameData.Instance.carLoaders.Length) return;

		var carLoader = GameData.Instance.carLoaders[carLoaderID];
		if (carLoader == null) return;

		// Business Logic: Find AudioSource component on car and play/stop engine sound
		// Observation: Car engine sounds are typically on the car's AudioSource component
		// Note: Temporarily disabled due to AudioModule reference issues - will be re-enabled when AudioModule DLL is available
		// TODO: Re-enable engine sound playback when UnityEngine.AudioModule.dll is added to project references
		
		// Business Logic: For now, we'll skip engine sound playback to avoid compilation errors
		// The engine sound synchronization can be implemented later when the AudioModule reference is available
		Log.Debug($"[ClientHandle->CarEngineSoundPacket] Engine sound sync received for car {carLoaderID} (Playing: {isPlaying}, RPM: {rpm}) - AudioModule not available");

		packet.Dispose();
	}
}