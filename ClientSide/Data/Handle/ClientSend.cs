using System.Collections.Generic;
using CMS21Together.Shared.World;
using CMS21Together.ClientSide.Data.Garage;
using CMS21Together.ClientSide.Data.Garage.Tools;
using CMS21Together.ClientSide.Data.Player;
using CMS21Together.Shared;
using CMS21Together.Shared.Net;
using CMS21Together.Shared.Data;
using CMS21Together.Shared.Data.Vanilla;
using CMS21Together.Shared.Data.Vanilla.Cars;
using CMS21Together.Shared.Data.Vanilla.GarageTool;
using CMS21Together.Shared.Data.Vanilla.Jobs;
using CMS21Together.Shared.Locks;
using MelonLoader;

namespace CMS21Together.ClientSide.Data.Handle;

public class ClientSend
{
	private static void SendData(Packet _packet, bool reliable = true)
	{
		Client.Instance.SendData(_packet, reliable);
	}

	public static void ConnectValidationPacket(string saveId)
	{
		// The token is stored before it is sent, so the host never binds a profile to a secret this player lost.
		var token = ClientData.Credentials.GetOrCreate(saveId);
		if (token == null) Log.Warn("[Client] The host sent an invalid save id; joining without saved credentials.");
		using (var packet = new Packet((int)PacketTypes.connect))
		{
			packet.Write(ClientData.UserData.playerID);
			packet.Write(ClientData.UserData.username);
			packet.Write(ContentManager.Instance.ownedContents);
			packet.Write(ContentManager.Instance.gameVersion);
			packet.Write(MainMod.ASSEMBLY_MOD_VERSION);
			packet.Write(ClientData.UserData.playerGUID);
			packet.Write(saveId ?? string.Empty);
			packet.Write(token ?? string.Empty);

			SendData(packet);
		}
	}

	public static void ReadyPacket(bool isReady, int playerID)
	{
		using (var packet = new Packet((int)PacketTypes.readyState))
		{
			packet.Write(playerID);
			packet.Write(isReady);

			SendData(packet);
		}
	}

	public static void PositionPacket(Vector3Serializable position)
	{
		using (var packet = new Packet((int)PacketTypes.position))
		{
			packet.Write(position);
			// Phase 7: only the latest position matters; at most one waits on a slow link.
			Client.Instance.SendData(packet, false, false, CoalesceKeys.Movement(0, (int)PacketTypes.position));
		}
	}

	public static void RotationPacket(QuaternionSerializable rotation)
	{
		using (var packet = new Packet((int)PacketTypes.rotation))
		{
			packet.Write(rotation);
			Client.Instance.SendData(packet, false, false, CoalesceKeys.Movement(0, (int)PacketTypes.rotation));
		}
	}

	public static void ExpPacket(int exp, int level)
	{
		using (var packet = new Packet((int)PacketTypes.exp))
		{
			packet.Write(exp);
			packet.Write(level);

			SendData(packet);
		}
	}
	
	public static void PointPacket(int availablePoints)
	{
		using (var packet = new Packet((int)PacketTypes.point))
		{
			packet.Write(availablePoints);

			SendData(packet);
		}
	}

	public static void LifterPacket(ModLifterState state, int carLoaderID)
	{
		using (var packet = new Packet((int)PacketTypes.lifter))
		{
			packet.Write(state);
			packet.Write(carLoaderID);

			SendData(packet);
		}
	}

	public static void LoadJobCarPacket(ModCar car)
	{
		using (var packet = new Packet((int)PacketTypes.loadJobCar))
		{
			packet.Write(car);

			SendData(packet);
		}
	}

	public static void LoadCarPacket(ModNewCarData carData, int carLoaderID)
	{
		using (var packet = new Packet((int)PacketTypes.loadCar))
		{
			packet.Write(carData);
			packet.Write(carLoaderID);

			SendData(packet);
		}
	}

	/// <param name="linkedItem">The item this panel became (taken off) or came from (put on), or 0.</param>
	public static void BodyPartPacket(ModCarPart carPart, int carLoaderID, long linkedItem = 0, bool off = false)
	{
		using (var packet = new Packet((int)PacketTypes.bodyPart))
		{
			packet.Write(carPart);
			packet.Write(carLoaderID);
			packet.Write(ActionGate.Stamp(LockKeys.Body(carLoaderID, carPart.carPartID)));
			packet.Write(linkedItem);
			packet.Write(off);

			SendData(packet);
		}
		Garage.Car.CarChecks.Touched(carLoaderID);
	}

	public static void PartScriptPacket(ModPartScript partScript, int carLoaderID)
	{
		using (var packet = new Packet((int)PacketTypes.partScript))
		{
			packet.Write(partScript);
			packet.Write(carLoaderID);
			packet.Write(ActionGate.Stamp(LockKeys.ForPartScript(carLoaderID, (int)partScript.type, partScript.partID, partScript.partIdNumber)));

			SendData(packet);
		}
		Garage.Car.CarChecks.Touched(carLoaderID);
	}

	public static void DeleteCarPacket(int carLoaderID)
	{
		using (var packet = new Packet((int)PacketTypes.deleteCar))
		{
			packet.Write(carLoaderID);

			SendData(packet);
		}
	}

	public static void CarPositionPacket(int carLoaderID, int no)
	{
		using (var packet = new Packet((int)PacketTypes.carPosition))
		{
			packet.Write(no);
			packet.Write(carLoaderID);

			SendData(packet);
		}
	}

	/// <param name="paid">A purchase the host already charged (Phase 6); the host checks it against its approval.</param>
	public static void GarageUpgradePacket(GarageUpgrade upgrade, bool paid = false)
	{
		using (var packet = new Packet((int)PacketTypes.garageUpgrade))
		{
			packet.Write(upgrade);
			packet.Write(paid);

			SendData(packet);
		}
	}

	public static void JobPacket(ModJob job)
	{
		using (var packet = new Packet((int)PacketTypes.newJob))
		{
			packet.Write(job);
			Log.Debug("newJob packet");
			SendData(packet);
		}
	}

	public static void JobActionPacket(ModJob job, bool takeJob)
	{
		using (var packet = new Packet((int)PacketTypes.jobAction))
		{
			packet.Write(job);
			packet.Write(takeJob);

			SendData(packet);
		}
	}

	public static void SelectedJobPacket(ModJob job, bool action)
	{
		using (var packet = new Packet((int)PacketTypes.selectedJob))
		{
			Log.Debug("SelectedJob packet");
			packet.Write(job);
			packet.Write(action);

			SendData(packet);
		}
	}

	public static void EndJobPacket(ModJob modJob)
	{
		using (var packet = new Packet((int)PacketTypes.endJob))
		{
			Log.Debug("EndJobPacket");
			packet.Write(modJob);

			SendData(packet);
		}
	}

	public static void SceneChangePacket(GameScene scene)
	{
		using (var packet = new Packet((int)PacketTypes.sceneChange))
		{
			packet.Write(scene);

			SendData(packet);
		}
	}

	public static void ToolPositionPacket(IOSpecialType tool, ModCarPlace place, bool playSound = false)
	{
		using (var packet = new Packet((int)PacketTypes.toolMove))
		{
			packet.Write((ModIOSpecialType)tool);
			packet.Write(place);
			packet.Write(playSound);

			SendData(packet);
		}
	}

	public static void SetSpringClampPacket(ModGroupItem item, bool instant, bool mount)
	{
		using (var packet = new Packet((int)PacketTypes.setSpringClamp))
		{
			packet.Write(item);
			packet.Write(instant);
			packet.Write(mount);

			SendData(packet);
		}
	}

	public static void ClearSpringClampPacket()
	{
		using (var packet = new Packet((int)PacketTypes.clearSpringClamp))
		{
			SendData(packet);
		}
	}

	public static void SetTireChangerPacket(ModGroupItem modGroupItem, bool instant, bool connect)
	{
		using (var packet = new Packet((int)PacketTypes.setTireChanger))
		{
			packet.Write(modGroupItem);
			packet.Write(instant);
			packet.Write(connect);

			SendData(packet);
		}
	}

	public static void ClearTireChangerPacket()
	{
		using (var packet = new Packet((int)PacketTypes.clearTireChanger))
		{
			SendData(packet);
		}
	}

	public static void SendOilBin(int carLoaderID)
	{
		using (var packet = new Packet((int)PacketTypes.oilBinUse))
		{
			packet.Write(carLoaderID);
			SendData(packet);
		}
	}

	public static void SendWheelBalancer(int action, GroupItem items=null)
	{
		using (var packet = new Packet((int)PacketTypes.wheelBalance))
		{
			packet.Write(action);
			if (items != null) packet.Write(new ModGroupItem(items));
			SendData(packet);
		}
	}

	public static void SkillChangePacket(string id, List<bool> skill)
	{
		using (var packet = new Packet((int)PacketTypes.skillChange))
		{
			packet.Write(ClientData.UserData.playerGUID);
			packet.Write(id);
			packet.Write(skill);
			
			SendData(packet);
		}
	}

	public static void EngineCraneHandlePacket(int action, int carLoaderID, ModGroupItem modGroupItem = null)
	{
		using (var packet = new Packet((int)PacketTypes.engineCrane))
		{
			packet.Write(action);
			packet.Write(carLoaderID);
			if(action == 1) packet.Write(modGroupItem);
			
			SendData(packet);
		}
	}

	public static void EngineStandAnglePacket(float val, bool useAlt)
	{
		using (var packet = new Packet((int)PacketTypes.engineStandAngle))
		{
			packet.Write(val);
			packet.Write(useAlt);
			
			SendData(packet);
		}
	}

	public static void EngineStandSetGroupPacket(ModGroupItem engineGroupItem, Vector3Serializable position, bool useAlt)
	{
		using (var packet = new Packet((int)PacketTypes.engineStandSetGroup))
		{
			packet.Write(engineGroupItem);
			packet.Write(position);
			packet.Write(useAlt);
			
			SendData(packet);
		}
	}

	public static void TakeOffEnginePacket(bool useAlt)
	{
		using (var packet = new Packet((int)PacketTypes.engineStandTakeOff))
		{
			packet.Write(useAlt);
			
			SendData(packet);
		}
	}

	public static void CarFluid(int carLoaderID, ModFluidData carFluid)
	{
		using (var packet = new Packet((int)PacketTypes.carFluid))
		{
			packet.Write(carLoaderID);
			packet.Write(carFluid);
			SendData(packet);
		}
	}


	public static void ResyncCar(int carLoaderID)
	{
		using (var packet = new Packet((int)PacketTypes.resync))
		{
			packet.Write(PacketTypes.loadCar);
			packet.Write(carLoaderID);
			SendData(packet);
		}
	}
	
	/// <summary>Per-car desync detector: this player's fingerprints of the cars it has not changed lately.</summary>
	public static void CarCheck(List<(int carLoaderID, string hash)> cars, List<int> loaded = null)
	{
		using (var packet = new Packet((int)PacketTypes.carCheck))
		{
			CarCheckWire.Write(packet, cars, loaded);
			SendData(packet);
		}
	}

	public static void ResyncJobs()
	{
		using (var packet = new Packet((int)PacketTypes.resync))
		{
			packet.Write(PacketTypes.newJob);
			SendData(packet);
		}
	}

	public static void ResyncTools()
	{
		using (var packet = new Packet((int)PacketTypes.resync))
		{
			packet.Write(PacketTypes.toolMove);
			SendData(packet);
		}
	}

	/// <summary>Contents of the spring clamp, tire changer and wheel balancer (the server reads this marker).</summary>
	public static void ResyncStations()
	{
		using (var packet = new Packet((int)PacketTypes.resync))
		{
			packet.Write(PacketTypes.setSpringClamp);
			SendData(packet);
		}
	}
	
	public static void ResyncUpgrade()
	{
		using (var packet = new Packet((int)PacketTypes.resync))
		{
			packet.Write(PacketTypes.garageUpgrade);
			SendData(packet);
		}
	}

	public static void CarWashPacket(int carLoaderID, bool interior=false)
	{
		using (var packet = new Packet((int)PacketTypes.carWash))
		{
			packet.Write(carLoaderID);
			packet.Write(interior);
			SendData(packet);
		}
	}

	/// <param name="txn">The debit that paid for the paint job (0 = none); the host applies it only if charged.</param>
	public static void CarPaint(ModColor modColor, int txn = 0)
	{
		using (var packet = new Packet((int)PacketTypes.carPaint))
		{
			packet.Write(modColor);
			packet.Write(txn);
			SendData(packet);
		}
	}

	public static void WelderPacket(int carLoaderID)
	{
		using (var packet = new Packet((int)PacketTypes.useWelder))
		{
			packet.Write(carLoaderID);
			SendData(packet);
		}
	}

	public static void RepairPart(ModPartInfo modPartInfo,  bool isBody, bool success)
	{
		using (var packet = new Packet((int)PacketTypes.repairPart))
		{
			packet.Write(modPartInfo);
			packet.Write(isBody);
			packet.Write(success);
			SendData(packet);
		}
	}

	public static void ResyncEngineStandPacket(bool alt)
	{
		using (var packet = new Packet((int)PacketTypes.resync))
		{
			Log.Debug("Ask resync for addition engine stand");
			packet.Write(PacketTypes.engineStandSetGroup);
			packet.Write(alt);
			SendData(packet);
		}
	}
	public static void ResyncPark()
	{
		using (var packet = new Packet((int)PacketTypes.resync))
		{
			Log.Debug("Ask resync for park");
			packet.Write(PacketTypes.parkAdd);
			SendData(packet);
		}
	}

	public static void AddCarToParkPacket(ModNewCarData modNewCarData, int index)
	{
		using (var packet = new Packet((int)PacketTypes.parkAdd))
		{
			Log.Debug("Add a car to parking");
			packet.Write(modNewCarData);
			packet.Write(index);
			SendData(packet);
		}
	}

	public static void RemoveCarFromParkPacket(int index)
	{
		using (var packet = new Packet((int)PacketTypes.parkRemove))
		{
			packet.Write(index);
			SendData(packet);
		}
	}


	public static void PlayerInCarPacket(bool isInCar, int carLoaderID)
	{
		using (var packet = new Packet((int)PacketTypes.playerInCar))
		{
			packet.Write(isInCar);
			packet.Write(carLoaderID);
			SendData(packet);
		}
	}

	public static void CarEngineSoundPacket(int carLoaderID, bool isPlaying, float rpm = 0f)
	{
		using (var packet = new Packet((int)PacketTypes.carEngineSound))
		{
			packet.Write(carLoaderID);
			packet.Write(isPlaying);
			packet.Write(rpm);
			SendData(packet, false); // Use UDP for sound sync (non-reliable)
		}
	}
}