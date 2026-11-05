using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CMS21Together.ClientSide.Data;
using CMS21Together.ClientSide.Data.Player;
using CMS21Together.Shared;
using CMS21Together.Shared.Data;
using CMS21Together.Shared.Data.Vanilla;
using CMS21Together.Shared.Data.Vanilla.Cars;
using CMS21Together.Shared.Data.Vanilla.GarageTool;
using CMS21Together.Shared.Data.Vanilla.Jobs;
using CMS21Together.Shared.Net;
using CMS21Together.Shared.World;
using MelonLoader;

namespace CMS21Together.ServerSide.Data;

public static class ServerSend
{
	public static void PlayerSpawnPacket(int id, PlayerInfo info)
	{
		if (id == 1) return; // The host already runs the game with its own stats.
		using (var packet = new Packet((int)PacketTypes.spawn))
		{
			var economy = Server.Instance?.Economy;
			packet.Write(economy != null && economy.Ledger.Initialized ? (int)economy.Ledger.Money : SavesManager.ModSaves[SavesManager.currentSaveIndex].money);
			packet.Write(info.playerExp);
			packet.Write(info.playerLevel);
			packet.Write(info.skillPoints);
			packet.Write(info.position);
			packet.Write(info.rotation);
			packet.Write(info.skillsInfo);
			var save = SavesManager.ModSaves[SavesManager.currentSaveIndex];
			// Phase 7: hf3 saves have 4 UID counters; slots 5 to 8 get their own ranges.
			save.inventoryItemUID = UidRanges.Ensure(save.inventoryItemUID, Math.Max(id, ServerConfig.MaxSupportedPlayers));
			packet.Write(save.inventoryItemUID[id - 1]);
			packet.Write(SavesManager.ModSaves[SavesManager.currentSaveIndex].missionFinished);
			packet.Write(SavesManager.ModSaves[SavesManager.currentSaveIndex].storyMissionInProgress);

			SendData(id, packet);
		}
	}
	
	/// <summary>The stored level and XP, for a player whose game reported lower ones.</summary>
	public static void StatsFixPacket(int id, int exp, int level)
	{
		using var packet = new Packet((int)PacketTypes.statsFix);
		packet.Write(exp);
		packet.Write(level);
		SendData(id, packet);
	}

	public static void PositionPacket(int fromClient, Vector3Serializable position)
	{
		using (var packet = new Packet((int)PacketTypes.position))
		{
			packet.Write(fromClient);
			packet.Write(position);

			Server.Instance.SendMovement(packet, fromClient);
		}
	}

	public static void RotationPacket(int fromClient, QuaternionSerializable rotation)
	{
		using (var packet = new Packet((int)PacketTypes.rotation))
		{
			packet.Write(fromClient);
			packet.Write(rotation);

			Server.Instance.SendMovement(packet, fromClient);
		}
	}

	public static void LifterPacket(int fromClient, ModLifterState state, int carLoaderID, bool resync = false)
	{
		using (var packet = new Packet((int)PacketTypes.lifter))
		{
			packet.Write(state);
			packet.Write(carLoaderID);

			if (!resync)
				SendDataToAll(fromClient, packet);
			else
				SendData(fromClient, packet);
		}
	}

	public static void LoadCarPacket(int fromClient, ModNewCarData carData, int carLoaderID, bool resync=false)
	{
		using (var packet = new Packet((int)PacketTypes.loadCar))
		{
			packet.Write(carData);
			packet.Write(carLoaderID);

			if (!resync)
				SendDataToAll(fromClient, packet);
			else
				SendData(fromClient, packet);
		}
	}

	public static void BodyPartPacket(int fromClient, ModCarPart carPart, int carLoaderID, bool resync=false)
	{
		using (var packet = new Packet((int)PacketTypes.bodyPart))
		{
			packet.Write(carPart);
			packet.Write(carLoaderID);

			if (!resync)
				SendDataToAll(fromClient, packet);
			else
				SendData(fromClient, packet);
		}

		//Log.Info("[ServerSend->PartScriptPacket] Sent BodyPart.");
	}

	public static void PartScriptPacket(int fromClient, ModPartScript partScript, int carLoaderID, bool resync=false)
	{
		using (var packet = new Packet((int)PacketTypes.partScript))
		{
			packet.Write(partScript);
			packet.Write(carLoaderID);

			if (!resync)
				SendDataToAll(fromClient, packet);
			else
				SendData(fromClient, packet);
		}

	//	Log.Info("[ServerSend->PartScriptPacket] Sent PartScript.");
	}

	/// <summary>Deletes a car on one player's game only (a ghost car there; Phase 6b).</summary>
	public static void DeleteCarPacketTo(int toClient, int carLoaderID)
	{
		using var packet = new Packet((int)PacketTypes.deleteCar);
		packet.Write(carLoaderID);
		SendData(toClient, packet);
	}

	public static void DeleteCarPacket(int fromClient, int carLoaderID)
	{
		using (var packet = new Packet((int)PacketTypes.deleteCar))
		{
			packet.Write(carLoaderID);

			SendDataToAll(fromClient, packet);
		}
	}

	public static void CarPositionPacket(int fromClient, int carLoaderID, int placeNo)
	{
		using (var packet = new Packet((int)PacketTypes.carPosition))
		{
			packet.Write(placeNo);
			packet.Write(carLoaderID);

			SendDataToAll(fromClient, packet);
		}
	}

	public static void GarageUpgradePacket(int fromClient, GarageUpgrade upgrade, bool resync=false)
	{
		using (var packet = new Packet((int)PacketTypes.garageUpgrade))
		{
			packet.Write(upgrade);

			if (!resync)
				SendDataToAll(fromClient, packet);
			else
				SendData(fromClient, packet);
		}
	}

	public static void JobPacket(int fromClient, ModJob job, bool resync = false)
	{
		using (var packet = new Packet((int)PacketTypes.newJob))
		{
			packet.Write(job);

			if (!resync)
				SendDataToAll(fromClient, packet);
			else
				SendData(fromClient, packet);
		}
	}

	public static void JobActionPacket(int fromClient, ModJob job, bool takeJob)
	{
		using (var packet = new Packet((int)PacketTypes.jobAction))
		{
			packet.Write(job);
			packet.Write(takeJob);

			SendDataToAll(fromClient, packet);
		}
	}

	public static void SelectedJobPacket(int fromClient, ModJob job, bool action, bool resync = false)
	{
		using (var packet = new Packet((int)PacketTypes.selectedJob))
		{
			packet.Write(job);
			packet.Write(action);

			if (!resync)
				SendDataToAll(fromClient, packet);
			else
				SendData(fromClient, packet);
		}
	}

	public static void EndJobPacket(int fromClient, ModJob job)
	{
		using (var packet = new Packet((int)PacketTypes.endJob))
		{
			packet.Write(job);
			SendDataToAll(fromClient, packet);
		}
	}

	public static void SceneChangePacket(int fromClient, GameScene scene)
	{
		using (var packet = new Packet((int)PacketTypes.sceneChange))
		{
			packet.Write(scene);
			packet.Write(fromClient);

			SendDataToAll(fromClient, packet);
		}
	}

	public static void ContentInfoPacket(ReadOnlyDictionary<string, bool> dictionary)
	{
		using (var packet = new Packet((int)PacketTypes.contentInfo))
		{
			packet.Write(dictionary);

			SendDataToAll(packet);
		}
	}

	public static void ToolsMovePacket(int fromClient, ModIOSpecialType tool, ModCarPlace place, bool playSound, bool resync=false)
	{
		using (var packet = new Packet((int)PacketTypes.toolMove))
		{
			packet.Write(tool);
			packet.Write(place);
			packet.Write(playSound);

			if (!resync)
				SendDataToAll(fromClient, packet);
			else
				SendData(fromClient, packet);
		}
	}

	public static void SetSpringClampPacket(int fromClient, ModGroupItem item, bool instant, bool mount, bool resync = false)
	{
		using (var packet = new Packet((int)PacketTypes.setSpringClamp))
		{
			packet.Write(item);
			packet.Write(instant);
			packet.Write(mount);

			if (!resync) SendDataToAll(fromClient, packet);
			else SendData(fromClient, packet);
		}
	}

	public static void SpringClampClearPacket(int fromClient)
	{
		using (var packet = new Packet((int)PacketTypes.clearSpringClamp))
		{
			SendDataToAll(fromClient, packet);
		}
	}

	public static void SetTireChangerPacket(int fromClient, ModGroupItem item, bool instant, bool connect, bool resync = false)
	{
		using (var packet = new Packet((int)PacketTypes.setTireChanger))
		{
			packet.Write(item);
			packet.Write(instant);
			packet.Write(connect);

			if (!resync) SendDataToAll(fromClient, packet);
			else SendData(fromClient, packet);
		}
	}

	public static void ClearTireChangerPacket(int fromClient)
	{
		using (var packet = new Packet((int)PacketTypes.clearTireChanger))
		{
			SendDataToAll(fromClient, packet);
		}
	}

	public static void WheelBalancePacket(int fromClient, ModGroupItem item)
	{
		using (var packet = new Packet((int)PacketTypes.wheelBalance))
		{
			packet.Write(item);
			SendDataToAll(fromClient, packet);
		}
	}
	

	#region Functions

	private static void SendData(int _toClient, Packet _packet, bool reliable = true)
	{
		Server.Instance.Send(_toClient, _packet, reliable);
	}

	private static void SendDataToAll(Packet _packet, bool reliable = true)
	{
		Server.Instance.SendToAll(_packet, reliable);
	}

	private static void SendDataToAll(int _exceptClient, Packet _packet, bool reliable = true)
	{
		Server.Instance.SendToAll(_packet, reliable, _exceptClient);
	}

	#endregion

	#region User

	/// <summary>Asks a new connection who it is, naming the save so it can pick the right credentials (audit A19).</summary>
	public static void IdentityRequestPacket(int slot, string saveId)
	{
		using (var packet = new Packet((int)PacketTypes.identityRequest))
		{
			packet.Write(saveId ?? string.Empty);

			SendData(slot, packet);
		}
	}

	public static void PlayerLeftPacket(int slot)
	{
		using (var packet = new Packet((int)PacketTypes.playerLeft))
		{
			packet.Write(slot);

			SendDataToAll(slot, packet);
		}
	}
	public static void UserDataPacket(UserData userData, int id = -1)
	{
		using (var packet = new Packet((int)PacketTypes.userData))
		{
			packet.Write(userData);
			
			if (id == -1)
				SendDataToAll(packet);
			else
				SendData(id, packet);
		}
	}

	public static void ReadyPacket(int fromClient, bool ready, int id)
	{
		using (var packet = new Packet((int)PacketTypes.readyState))
		{
			packet.Write(id);
			packet.Write(ready);

			SendDataToAll(packet);
		}
	}

	public static void StartPacket(Gamemode gamemode, Dictionary<int, ModNewCarData> parkCars)
	{
		using (var packet = new Packet((int)PacketTypes.start))
		{
			packet.Write(gamemode);
			packet.Write(parkCars);
			packet.Write(false);
			
			SendDataToAll(1, packet);
		}
	}

	/// <summary>Late join or rejoin (Phase 5): one player loads into the session that is already running.</summary>
	public static void StartPacket(int toSlot, Gamemode gamemode, Dictionary<int, ModNewCarData> parkCars)
	{
		using (var packet = new Packet((int)PacketTypes.start))
		{
			packet.Write(gamemode);
			packet.Write(parkCars);
			packet.Write(true);

			SendData(toSlot, packet);
		}
	}

	#endregion

	public static void WheelBalancerPacket(int fromClient, ModWheelBalancerActionType aType, ModGroupItem item=null, bool resync = false)
	{
		using (Packet _packet = new Packet((int)PacketTypes.wheelBalance))
		{
			_packet.Write(aType);
			if(item != null)   {_packet.Write(item);}

			if (!resync) SendDataToAll(fromClient, _packet);
			else SendData(fromClient, _packet);
		}
	}

	public static void SendOilBin(int fromClient, int carLoaderID)
	{
		using (Packet _packet = new Packet((int)PacketTypes.oilBinUse))
		{
			_packet.Write(carLoaderID);

			SendDataToAll(fromClient, _packet);
		}
	}

	public static void EngineStandSetGroupPacket(int fromClient, ModGroupItem engineGroup, Vector3Serializable position, bool useAlt, bool resync=false)
	{
		using (Packet _packet = new Packet((int)PacketTypes.engineStandSetGroup))
		{
			_packet.Write(engineGroup);
			_packet.Write(position);
			_packet.Write(useAlt);

			if (!resync)
				SendDataToAll(fromClient, _packet);
			else
				SendData(fromClient, _packet);
		}
	}

	public static void EngineStandTakeOffPacket(int fromClient, bool alt)
	{
		using (Packet _packet = new Packet((int)PacketTypes.engineStandTakeOff))
		{
			_packet.Write(alt);
			
			SendDataToAll(fromClient, _packet);
		}
	}

	public static void IncreaseStandAnglePacket(int fromClient, float val, bool alt)
	{
		using (Packet _packet = new Packet((int)PacketTypes.engineStandAngle))
		{
			_packet.Write(val);
			_packet.Write(alt);
			
			SendDataToAll(fromClient, _packet);
		}
	}

	public static void EngineCraneHandlePacket(int fromClient, int action, int carLoaderID, ModGroupItem item = null)
	{
		using (Packet _packet = new Packet((int)PacketTypes.engineCrane))
		{
			_packet.Write(action);
			_packet.Write(carLoaderID);
			if (action == 1) _packet.Write(item);
			
			SendDataToAll(fromClient, _packet);
		}
	}

	public static void CarFluidPacket(int fromClient, int carLoaderID, ModFluidData fluid)
	{
		using (Packet _packet = new Packet((int)PacketTypes.carFluid))
		{
			_packet.Write(carLoaderID);
			_packet.Write(fluid);
			
			SendDataToAll(fromClient, _packet);
		}
	}

	public static void CarWashPacket(int fromClient, int loaderID, bool interior)
	{
		using (Packet _packet = new Packet((int)PacketTypes.carWash))
		{
			_packet.Write(loaderID);
			_packet.Write(interior);
			
			SendDataToAll(fromClient, _packet);
		}
	}

	public static void CarPaintPacket(int fromClient, ModColor color, bool resync = false)
	{
		using (Packet _packet = new Packet((int)PacketTypes.carPaint))
		{
			_packet.Write(color);
			
			if (!resync)
				SendDataToAll(fromClient, _packet);
			else
				SendData(fromClient, _packet);
		}
	}

	/// <summary>A new profile's item UID counter (the spawn packet carries it for known profiles).</summary>
	public static void UidRangePacket(int id)
	{
		var save = SavesManager.ModSaves[SavesManager.currentSaveIndex];
		save.inventoryItemUID = UidRanges.Ensure(save.inventoryItemUID, Math.Max(id, ServerConfig.MaxSupportedPlayers));
		using var packet = new Packet((int)PacketTypes.uidRange);
		packet.Write(save.inventoryItemUID[id - 1]);
		SendData(id, packet);
	}

	/// <summary>The painter's own paid colour, flagged so its game repaints only if it shows something else.</summary>
	public static void CarPaintEchoPacket(int toClient, ModColor color)
	{
		using var packet = new Packet((int)PacketTypes.carPaint);
		packet.Write(color);
		packet.Write(true);
		SendData(toClient, packet);
	}

	public static void WelderPacket(int fromClient, int loaderID)
	{
		using (Packet _packet = new Packet((int)PacketTypes.useWelder))
		{
			_packet.Write(loaderID);
			
			SendDataToAll(fromClient, _packet);
		}
	}

	public static void RepairPartPacket(int fromClient, ModPartInfo info, bool isBody, bool success)
	{
		using (Packet _packet = new Packet((int)PacketTypes.repairPart))
		{
			_packet.Write(info);
			_packet.Write(isBody);
			_packet.Write(success);
			
			SendDataToAll(fromClient, _packet);
		}
	}

	public static void AddCarToParkPacket(int fromClient, ModNewCarData car, int index, bool resync = false)
	{
		using (Packet _packet = new Packet((int)PacketTypes.parkAdd))
		{
			_packet.Write(car);
			_packet.Write(index);
			
			if (!resync)
				SendDataToAll(fromClient, _packet);
			else
				SendData(fromClient, _packet);
		}
	}

	public static void RemoveCarFromParkPacket(int fromClient, int index)
	{
		using (Packet _packet = new Packet((int)PacketTypes.parkRemove))
		{
			_packet.Write(index);
			
			SendDataToAll(fromClient, _packet);
		}
	}

	public static void PlayerInCarPacket(int fromClient, bool isInCar, int carLoaderID)
	{
		using (Packet _packet = new Packet((int)PacketTypes.playerInCar))
		{
			_packet.Write(fromClient);
			_packet.Write(isInCar);
			_packet.Write(carLoaderID);
			
			SendDataToAll(fromClient, _packet);
		}
	}

	public static void CarEngineSoundPacket(int fromClient, int carLoaderID, bool isPlaying, float rpm)
	{
		using (Packet _packet = new Packet((int)PacketTypes.carEngineSound))
		{
			_packet.Write(fromClient);
			_packet.Write(carLoaderID);
			_packet.Write(isPlaying);
			_packet.Write(rpm);
			
			SendDataToAll(fromClient, _packet, false); // Use UDP for sound sync (non-reliable)
		}
	}
}