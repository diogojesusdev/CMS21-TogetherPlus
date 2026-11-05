using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CMS21Together.ClientSide.Data;
using CMS21Together.ClientSide.Data.Garage.Tools;
using CMS21Together.ClientSide.Data.Player;
using CMS21Together.Shared;
using CMS21Together.Shared.Data;
using CMS21Together.Shared.Data.Vanilla;
using CMS21Together.Shared.Data.Vanilla.Cars;
using CMS21Together.Shared.Data.Vanilla.GarageTool;
using CMS21Together.Shared.Data.Vanilla.Jobs;
using CMS21Together.Shared.Economy;
using CMS21Together.Shared.Locks;
using CMS21Together.Shared.Net;
using CMS21Together.Shared.World;
using MelonLoader;
using Steamworks;
using UnityEngine;

namespace CMS21Together.ServerSide.Data;

public static class ServerHandle
{
	public static void ConnectValidationPacket(int fromClient, Packet packet)
	{
		var clientIdCheck = packet.ReadInt();
		var username = packet.Read<string>();
		var content = packet.Read<ReadOnlyDictionary<string, bool>>();
		var gameVersion = packet.Read<string>();
		packet.Read<string>(); // Mod version: the transport handshake already checked it.
		var legacyGuid = packet.Read<string>();
		var saveId = packet.Read<string>();
		var token = packet.Read<string>();

		var data = ServerData.Instance;
		if (data.connectedClients.ContainsKey(fromClient))
		{
			Log.Warn($"[ServerHandle] Player #{fromClient} sent its validation twice; ignoring.");
			return;
		}
		if (fromClient != clientIdCheck)
		{
			Server.Instance.Kick(fromClient, "Error on ClientIdCheck.");
			return;
		}
		if (gameVersion != ContentManager.Instance.gameVersion)
		{
			Server.Instance.Kick(fromClient, $"The host is on game version {ContentManager.Instance.gameVersion}.");
			return;
		}
		var save = SavesManager.ModSaves[SavesManager.currentSaveIndex];
		if (!string.Equals(saveId ?? string.Empty, save.saveId ?? string.Empty, StringComparison.Ordinal))
		{
			Server.Instance.Kick(fromClient, "The host changed the save. Please join again.");
			return;
		}
		if (fromClient != 1 && !IdentityService.IsValidToken(token))
		{
			Server.Instance.Kick(fromClient, "Invalid player identity.");
			return;
		}

		// Audit A19: the profile comes from the Steam connection or a secret token, never from a client-chosen id.
		var claim = new IdentityClaim
		{
			VerifiedSteamId = VerifiedSteamId(fromClient),
			Trusted = fromClient == 1,
			LegacyGuid = legacyGuid,
			Token = token
		};
		var identity = IdentityService.Resolve(save.playerInfos, claim,
			id => new PlayerInfo(id, Vector3.zero, Quaternion.identity, 0, 1, 0));
		var playerID = identity.ProfileId;
		var info = identity.Profile;

		var previous = data.connectedClients.Values.FirstOrDefault(u => u.playerGUID == playerID);
		if (previous != null)
		{
			if (previous.playerID == 1)
			{
				Server.Instance.Kick(fromClient, "You are already in this session as the host.");
				return;
			}
			// A stale connection of the same player: close it, and let the player retry once its progress was kept.
			Server.Instance.Kick(previous.playerID, "You joined again from another connection.");
			Server.Instance.Kick(fromClient, "Your previous connection was still open. Please join again in a few seconds.");
			return;
		}

		username = PlayerNames.Sanitize(username, fromClient);
		var user = new UserData(username, fromClient, playerID);
		data.connectedClients[fromClient] = user;
		Server.Instance?.Locks?.SendSnapshot(fromClient);
		Server.Instance?.Economy?.PlayerJoined(fromClient);
		Server.Instance?.Inventory?.PlayerJoined(fromClient);
		Server.Instance?.GroupInventory?.PlayerJoined(fromClient);
		ClientSide.Features.Features.SendSession(Server.Instance, fromClient);

		var shared = ApiCalls.API_M1(content, ContentManager.Instance.ownedContents);
		ServerSend.ContentInfoPacket(new ReadOnlyDictionary<string, bool>(shared));

		// Live stats start from the saved profile, so saving before the first update never resets them.
		data.SetPlayerInfo(fromClient, info);

		foreach (var other in data.connectedClients.Values)
			if (other.playerID != fromClient)
				ServerSend.UserDataPacket(other.ForBroadcast(), fromClient);
		ServerSend.UserDataPacket(user.ForBroadcast());
		Log.Info($"[ServerHandle] {username} joined the lobby as player #{fromClient} ({identity.Outcome} profile).");

		if (identity.Outcome != IdentityOutcome.Existing)
			SavesManager.SaveModSave(SavesManager.currentSaveIndex);
		if (identity.Outcome != IdentityOutcome.Created)
			ServerSend.PlayerSpawnPacket(fromClient, info);
		else
			ServerSend.UidRangePacket(fromClient);
		PlusSaves.PlayerJoined(fromClient);

		// Late join or rejoin (Phase 5): the session is already in game, so this player loads straight into it.
		if (fromClient != 1 && Server.Instance.GameStarted)
		{
			Log.Info($"[ServerHandle] {username} joins the running game.");
			ServerSend.StartPacket(fromClient, save.selectedGamemode, CurrentParkCars());
		}
	}

	/// <summary>The cars parked outside the garage right now, as the host's game knows them.</summary>
	public static Dictionary<int, ModNewCarData> CurrentParkCars()
	{
		var result = new Dictionary<int, ModNewCarData>();
		var profile = Singleton<GameManager>.Instance?.GameDataManager?.CurrentProfileData ?? SavesManager.currentSave;
		if (profile?.carsOnParking == null) return result;
		var i = 0;
		foreach (var carData in profile.carsOnParking)
		{
			if (carData != null && !string.IsNullOrEmpty(carData.carToLoad)) result.Add(i, new ModNewCarData(carData));
			i++;
		}
		return result;
	}

	private static ulong VerifiedSteamId(int slot)
	{
		try
		{
			if (slot == 1) return MainMod.IsSteamAvailable ? SteamClient.SteamId.Value : 0;
			return Server.Instance.Host?.GetPeer(slot)?.SteamId ?? 0;
		}
		catch (Exception ex)
		{
			Log.Warn($"[ServerHandle] Could not read the Steam id of player #{slot}: {ex.Message}");
			return 0;
		}
	}
	public static void ReadyPacket(int fromClient, Packet packet)
	{
		packet.ReadInt(); // Sender-supplied id: the connection decides who is ready (audit A9).
		var ready = packet.Read<bool>();

		if (!ServerData.Instance.connectedClients.TryGetValue(fromClient, out var user)) return;
		user.isReady = ready;
		ServerSend.ReadyPacket(fromClient, ready, fromClient);
	}
	
	public static void SkillChangePacket(int fromClient, Packet packet)
	{
		packet.Read<string>(); // Sender-supplied identity: use the connection's player instead.
		string id = packet.Read<string>();
		List<bool> skill = packet.Read<List<bool>>();

		if (!ServerData.Instance.connectedClients.TryGetValue(fromClient, out var user)) return;
		var info = SavesManager.ModSaves[SavesManager.currentSaveIndex].playerInfos.FirstOrDefault(p => p.id == user.playerGUID);
		if (info == null)
		{
			Log.Warn($"[ServerHandle] No saved profile for {user.username}; skill change ignored.");
			return;
		}
		info.UpdateSkill(id, skill);
	}
	public static void PositionPacket(int fromClient, Packet packet)
	{
		var _position = packet.Read<Vector3Serializable>();
		if (!ServerData.Instance.connectedClients.TryGetValue(fromClient, out var mover)) return;
		mover.position = _position;

		ServerSend.PositionPacket(fromClient, _position);
	}

	public static void RotationPacket(int fromClient, Packet packet)
	{
		var _rotation = packet.Read<QuaternionSerializable>();
		// Same as the position handler: a packet from a slot the host no longer has (a disconnect in flight) is ignored.
		if (!ServerData.Instance.connectedClients.TryGetValue(fromClient, out var mover)) return;
		mover.rotation = _rotation;

		ServerSend.RotationPacket(fromClient, _rotation);
	}

	/// <summary>A player's change to the shared inventory (Phase 5): applied once, in order, then sent to everyone.</summary>
	public static void InventoryPushPacket(int fromClient, Packet packet)
	{
		var server = Server.Instance;
		if (server?.Inventory == null) return;
		var kind = PeekInventoryKind(packet);
		if (kind == InventoryKind.Items)
		{
			var (_, seq, op, item, txn) = InventoryWire.ReadPush<ModItem>(packet);
			if (server.Inventory.HandlePush(fromClient, seq, op, item, txn) != Shared.World.InventoryResult.Added) return;
			// The per-player UID counter persisted with the save (hf3) follows every new item.
			var save = SavesManager.ModSaves[SavesManager.currentSaveIndex];
			var counters = save.inventoryItemUID = Shared.World.UidRanges.Ensure(save.inventoryItemUID, Math.Max(fromClient, Shared.Net.ServerConfig.MaxSupportedPlayers));
			if (counters != null && fromClient >= 1 && fromClient <= counters.Length)
				counters[fromClient - 1] = Math.Max(counters[fromClient - 1] + 1, Shared.World.UidRanges.Advance(counters[fromClient - 1], item.UID));
		}
		else
		{
			var (_, seq, op, item, txn) = InventoryWire.ReadPush<ModGroupItem>(packet);
			if (server.GroupInventory.HandlePush(fromClient, seq, op, item, txn) != Shared.World.InventoryResult.Added || item == null) return;
			var save = SavesManager.ModSaves[SavesManager.currentSaveIndex];
			var counters = save.inventoryItemUID = Shared.World.UidRanges.Ensure(save.inventoryItemUID, Math.Max(fromClient, Shared.Net.ServerConfig.MaxSupportedPlayers));
			if (fromClient < 1 || fromClient > counters.Length) return;
			counters[fromClient - 1] = Shared.World.UidRanges.Advance(counters[fromClient - 1], item.UID);
			if (item.ItemList != null)
				foreach (var part in item.ItemList)
					if (part != null) counters[fromClient - 1] = Shared.World.UidRanges.Advance(counters[fromClient - 1], part.UID);
		}
	}

	/// <summary>Audit A15: the snapshot goes to the player who asked, and only to them.</summary>
	public static void InventoryRequestPacket(int fromClient, Packet packet)
	{
		var server = Server.Instance;
		if (server?.Inventory == null) return;
		if (InventoryWire.ReadRequest(packet) == InventoryKind.Items) server.Inventory.SendSnapshot(fromClient);
		else server.GroupInventory.SendSnapshot(fromClient);
	}

	public static void InventoryCheckPacket(int fromClient, Packet packet)
	{
		var server = Server.Instance;
		if (server?.Inventory == null) return;
		var (kind, version, hash) = InventoryWire.ReadCheck(packet);
		if (kind == InventoryKind.Items) server.Inventory.HandleCheck(fromClient, version, hash);
		else server.GroupInventory.HandleCheck(fromClient, version, hash);
	}

	private static InventoryKind PeekInventoryKind(Packet packet)
	{
		var payload = packet.PayloadToArray();
		var kind = payload.Length >= 4 ? BitConverter.ToInt32(payload, 0) : -1;
		if (kind < 0 || kind > (int)InventoryKind.Groups) throw new NetFormatException($"Unknown inventory {kind}.");
		return (InventoryKind)kind;
	}
	public static void ExpPacket(int fromClient, Packet packet)
	{
		int exp = packet.ReadInt();
		int lvl = packet.ReadInt();
		
		if (!ServerData.Instance.connectedClients.TryGetValue(fromClient, out var player)) return;
		if (ProgressGuard.Lowers(player.playerLevel, player.playerExp, lvl, exp))
		{
			// Level and XP never go down: keep the stored progress and give it back to the player.
			Log.Warn($"[ServerHandle] #{fromClient} reported level {lvl} / {exp} XP, below the stored level {player.playerLevel} / {player.playerExp} XP; kept the stored values.");
			ServerSend.StatsFixPacket(fromClient, player.playerExp, player.playerLevel);
			return;
		}
		player.playerExp = exp;
		player.playerLevel = lvl;
	}
	
	public static void PointPacket(int fromClient, Packet packet)
	{
		int points = packet.ReadInt();
		
		//Log.Info($"Received PointPacket Packet : {points}");
		if (ServerData.Instance.connectedClients.TryGetValue(fromClient, out var player))
			player.playerSkillPoints = points;
	}

	public static void LifterPacket(int fromClient, Packet packet)
	{
		var state = packet.Read<ModLifterState>();
		var carLoaderID = packet.ReadInt();
		if (state < ModLifterState.ground || state > ModLifterState.high) return;

		// Kept so a player who loads the car later sees the lifter where it is.
		ServerData.Instance.LifterStates[carLoaderID] = state;
		ServerSend.LifterPacket(fromClient, state, carLoaderID);
	}

	public static void LoadJobCarPacket(int fromClient, Packet packet)
	{
		var carData = packet.Read<ModCar>();
		ServerData.Instance.SetLoadJobCar(carData);
	}

	public static void LoadCarPacket(int fromClient, Packet packet)
	{
		var carData = packet.Read<ModNewCarData>();
		var carLoaderID = packet.ReadInt();

		ServerData.Instance.CarSpawnDatas[carLoaderID] = carData;
		Server.Instance?.CarDetector?.Touched(carLoaderID);
		ServerData.Instance.SetLoadJobCar(new ModCar(carLoaderID, carData.carToLoad, carData.configVersion, carData.carPosition, carData.customerCar));

		ServerSend.LoadCarPacket(fromClient, carData, carLoaderID);
	}

	public static void BodyPartPacket(int fromClient, Packet packet)
	{
		var carPart = packet.Read<ModCarPart>();
		var carLoaderID = packet.ReadInt();
		var token = LockWire.ReadOptionalToken(packet);
		var linkedItem = packet.UnreadLength() > 0 ? packet.Read<long>() : 0;
		var off = packet.UnreadLength() > 0 && packet.Read<bool>();

		var key = LockKeys.Body(carLoaderID, carPart.carPartID);
		var locks = Server.Instance?.Locks;
		if (locks != null && !locks.AcceptWrite(fromClient, key, token))
		{
			// Another player works on this panel: drop the write and show the writer the stored state again.
			if (locks.ShouldResync(fromClient, key) && ServerData.Instance.CarPartInfo.TryGetValue(carLoaderID, out var car) &&
			    car.BodyPartsReferences.TryGetValue(carPart.carPartID, out var stored))
				ServerSend.BodyPartPacket(fromClient, stored, carLoaderID, true);
			// Phase 6: the panel's item, linked by the writer's game, is undone exactly (body-panel race).
			var fix = Server.Instance.Races?.RejectedExact(fromClient, linkedItem, off);
			if (fix.HasValue)
			{
				Log.Info($"[Locks] #{fromClient} lost {key}: {(fix.Value.op == InventoryOp.Add ? "returning" : "removing")} item {fix.Value.item.UID}.");
				Server.Instance.Inventory?.Correct(fix.Value.op, fix.Value.item);
			}
			return;
		}

		// Phase 8: a panel mounted with an item another player took first (warehouse, sale, another mount) is undone.
		if (!off && !carPart.unmounted && linkedItem != 0 && Server.Instance?.Removals?.ClaimUid(fromClient, linkedItem) == Shared.World.RemovalClaim.Lost)
		{
			Log.Info($"[Items] #{fromClient} mounted {key} with item {linkedItem}, which another player took first: undone.");
			if (ServerData.Instance.CarPartInfo.TryGetValue(carLoaderID, out var info) && info.BodyPartsReferences.TryGetValue(carPart.carPartID, out var before))
				ServerSend.BodyPartPacket(fromClient, before, carLoaderID, true);
			else
			{
				carPart.unmounted = true;
				ServerSend.BodyPartPacket(fromClient, carPart, carLoaderID, true);
			}
			return;
		}

		ServerData.Instance.UpdateBodyParts(carPart, carLoaderID);
		Server.Instance?.CarDetector?.Touched(carLoaderID);
		ServerSend.BodyPartPacket(fromClient, carPart, carLoaderID);
	}

	public static void PartScriptPacket(int fromClient, Packet packet)
	{
		var partScript = packet.Read<ModPartScript>();
		var carLoaderID = packet.ReadInt();
		var token = LockWire.ReadOptionalToken(packet);

		var key = LockKeys.ForPartScript(carLoaderID, (int)partScript.type, partScript.partID, partScript.partIdNumber);
		var locks = Server.Instance?.Locks;
		if (locks != null && !locks.AcceptWrite(fromClient, key, token))
		{
			// Another player works on this part: drop the write and show the writer the stored state again.
			if (locks.ShouldResync(fromClient, key))
			{
				var stored = StoredPartScript(partScript, carLoaderID);
				if (stored != null) ServerSend.PartScriptPacket(fromClient, stored, carLoaderID, true);
			}
			// The writer's game already moved the part's item in or out of the inventory: undo that here (Phase 5).
			var fix = Server.Instance.Races?.Rejected(fromClient, partScript.id, partScript.unmounted);
			if (fix.HasValue)
			{
				Log.Info($"[Locks] #{fromClient} lost {key}: {(fix.Value.op == InventoryOp.Add ? "returning" : "removing")} item {fix.Value.item.UID} ({partScript.id}).");
				Server.Instance.Inventory?.Correct(fix.Value.op, fix.Value.item);
			}
			return;
		}

		// Phase 8: a part mounted with an item another player took first (warehouse, sale, another mount) is undone: the
		// writer gets the part back as it was, and the item stays where the first removal put it.
		if (!partScript.unmounted && Server.Instance?.Removals?.ClaimId(fromClient, partScript.id) == Shared.World.RemovalClaim.Lost)
		{
			Log.Info($"[Items] #{fromClient} mounted {key} ({partScript.id}) with an item another player took first: undone.");
			var before = StoredPartScript(partScript, carLoaderID);
			if (before == null) partScript.unmounted = true;
			ServerSend.PartScriptPacket(fromClient, before ?? partScript, carLoaderID, true);
			return;
		}

		ServerData.Instance.UpdatePartScripts(partScript, carLoaderID);
		Server.Instance?.CarDetector?.Touched(carLoaderID);
		ServerSend.PartScriptPacket(fromClient, partScript, carLoaderID);
	}

	private static ModPartScript StoredPartScript(ModPartScript incoming, int carLoaderID)
	{
		var data = ServerData.Instance;
		if (carLoaderID is -1 or -2)
		{
			var stand = carLoaderID == -1 ? data.engineStand : data.engineStand2;
			return stand?.parts != null && stand.parts.TryGetValue(incoming.partID, out var standPart) ? standPart : null;
		}
		if (!data.CarPartInfo.TryGetValue(carLoaderID, out var car)) return null;

		var key = incoming.partID;
		var index = incoming.partIdNumber;
		switch (incoming.type)
		{
			case ModPartType.engine:
				return car.EnginePartsReferences.TryGetValue(key, out var engine) ? engine : null;
			case ModPartType.driveshaft:
				return car.DriveshaftPartsReferences.TryGetValue(key, out var driveshaft) ? driveshaft : null;
			case ModPartType.suspension:
				return car.SuspensionPartsReferences.TryGetValue(key, out var suspension) && suspension.TryGetValue(index, out var s) ? s : null;
			case ModPartType.other:
				return car.OtherPartsReferences.TryGetValue(key, out var other) && other.TryGetValue(index, out var o) ? o : null;
			default:
				return null;
		}
	}
	
	public static void CarFluidPacket(int fromClient, Packet packet)
	{
		var carLoaderID = packet.ReadInt();
		ModFluidData fluid = packet.Read<ModFluidData>();
		
		ServerData.Instance.UpdateFluid(fluid, carLoaderID);
		ServerSend.CarFluidPacket(fromClient, carLoaderID, fluid);
	}

	public static void DeleteCarPacket(int fromClient, Packet packet)
	{
		var carLoaderID = packet.ReadInt();

		ServerData.Instance.DeleteCar(carLoaderID);
		Server.Instance?.CarDetector?.Touched(carLoaderID);
		ServerSend.DeleteCarPacket(fromClient, carLoaderID);
	}

	public static void CarPositionPacket(int fromClient, Packet packet)
	{
		var placeNo = packet.ReadInt();
		var carLoaderID = packet.ReadInt();

		ServerData.Instance.ChangePosition(carLoaderID, placeNo);
		Server.Instance?.CarDetector?.Touched(carLoaderID);
		ServerSend.CarPositionPacket(fromClient, carLoaderID, placeNo);
	}

	public static void GarageUpgradePacket(int fromClient, Packet packet)
	{
		var upgrade = packet.Read<GarageUpgrade>();
		var paid = packet.UnreadLength() > 0 && packet.Read<bool>();

		// Phase 6: an unlock only spreads if the host charged for it; the host's own initial list is its save.
		PurgeApprovals();
		var approved = paid ? UpgradeApprovals.Remove((fromClient, upgrade.upgradeID + upgrade.upgradeLevel)) : fromClient == 1;
		if (!approved)
		{
			Log.Warn($"[ServerHandle] Ignored an unpaid upgrade {upgrade.upgradeID} {upgrade.upgradeLevel} from #{fromClient}.");
			return;
		}
		ServerData.Instance.SetGarageUpgrade(upgrade);
		ServerSend.GarageUpgradePacket(fromClient, upgrade);
	}

	/// <summary>Upgrades the host charged for and whose unlock it is waiting for: (player, upgrade + level) → when.</summary>
	private static readonly Dictionary<(int slot, string name), long> UpgradeApprovals = new();

	private static readonly System.Diagnostics.Stopwatch ApprovalClock = System.Diagnostics.Stopwatch.StartNew();

	/// <summary>A charged unlock that did not arrive by then failed on the buyer's side (and was refunded there).</summary>
	private const long ApprovalMs = 30_000;

	private static void PurgeApprovals()
	{
		var now = ApprovalClock.ElapsedMilliseconds;
		foreach (var key in UpgradeApprovals.Where(a => now - a.Value > ApprovalMs).Select(a => a.Key).ToList()) UpgradeApprovals.Remove(key);
	}

	/// <summary>A player wants to buy an upgrade (Phase 6, pre-authorized purchase): charge first, then let it happen.</summary>
	public static void PurchaseAskPacket(int fromClient, Packet packet)
	{
		var (request, kind, id, level, cost) = PurchaseWire.ReadAsk(packet);
		if (Payouts.Handles(kind))
		{
			AnswerOnce(fromClient, request, kind, id, cost);
			return;
		}
		var economy = Server.Instance?.Economy;
		var name = id + level;
		PurgeApprovals();
		TxnStatus status;
		if (kind != PurchaseKind.Upgrade || economy == null)
			status = TxnStatus.NotReady;
		else if (ServerData.Instance.garageUpgrades.TryGetValue(id, out var owned) && owned.unlocked && owned.upgradeLevel >= level ||
		         UpgradeApprovals.Keys.Any(a => a.name == name))
			status = TxnStatus.Invalid; // Already bought, or being bought (two players clicked at once): nobody pays twice.
		else
			status = cost == 0 ? TxnStatus.Applied : economy.Charge(fromClient, -cost);
		if (status == TxnStatus.Applied) UpgradeApprovals[(fromClient, name)] = ApprovalClock.ElapsedMilliseconds;
		Log.Info($"[ServerHandle] #{fromClient} buys upgrade {id} {level} for {cost}: {status}.");
		using var answer = new Packet((int)PacketTypes.purchaseAnswer);
		PurchaseWire.WriteAnswer(answer, request, status);
		Server.Instance.Send(fromClient, answer, true);
	}

	/// <summary>A player's car fingerprints (Phase 6): a car that differs from the host's own game gets the host's.</summary>
	public static void CarCheckPacket(int fromClient, Packet packet)
	{
		var server = Server.Instance;
		var detector = server?.CarDetector;
		if (detector == null) return;
		var reported = CarCheckWire.Read(packet);
		var loaded = CarCheckWire.ReadLoaded(packet);
		if (loaded != null) GhostCheck(server, fromClient, loaded);
		foreach (var (carLoaderID, hash) in reported)
		{
			var locked = server.Locks != null && server.Locks.Manager.Conflict(0, LockKeys.Car(carLoaderID)) != 0;
			if (!detector.ShouldPush(fromClient, carLoaderID, hash, ClientSide.Data.Garage.Car.CarChecks.Hash(carLoaderID), locked)) continue;
			var sent = ClientSide.Data.Garage.Car.CarChecks.PushCar(fromClient, carLoaderID);
			Log.Warn($"[Cars] Player #{fromClient}'s car {carLoaderID} differed from the host's; sent the host's ({sent} parts).");
		}
	}

	/// <summary>Phase 6b (GhostCarCleanup): cars only the player has are deleted there; cars it lacks are sent again.</summary>
	private static void GhostCheck(Server server, int fromClient, List<int> loaded)
	{
		var host = ClientData.Instance;
		if (server.CarRoster == null || host == null || !ClientSide.Features.Features.InSession(Shared.Features.FeatureCatalog.GhostCarCleanup)) return;
		if (!GameLoadHook.IsGameReady() || SceneManager.CurrentScene() != GameScene.garage) return;
		var hostCars = host.loadedCars.Where(c => c.Value != null && c.Value.isReady).Select(c => c.Key).ToList();
		var (ghosts, missing) = server.CarRoster.Compare(fromClient, loaded, hostCars);
		foreach (var car in ghosts)
		{
			Log.Warn($"[Cars] Player #{fromClient} has car {car}, which the host does not: deleting it there.");
			ServerSend.DeleteCarPacketTo(fromClient, car);
		}
		foreach (var car in missing)
		{
			Log.Warn($"[Cars] Player #{fromClient} lacks car {car}: sending it again.");
			ServerResyncs.ResyncCar(fromClient, car);
		}
	}

	public static void ForgetApprovals(int slot)
	{
		foreach (var key in UpgradeApprovals.Keys.Where(a => a.slot == slot).ToList()) UpgradeApprovals.Remove(key);
	}

	public static void JobPacket(int fromClient, Packet packet)
	{
		var job = packet.Read<ModJob>();

		//Log.Info("SV: Received JobPacket!");
		ServerData.Instance.AddJob(job);
		ServerSend.JobPacket(fromClient, job);
	}

	public static void JobActionPacket(int fromClient, Packet packet)
	{
		ModJob job = packet.Read<ModJob>();
		var takeJob = packet.Read<bool>();

		//Log.Info("SV: Received JobAction!");
		ServerData.Instance.RemoveJob(job.id);
		// An accepted order moves from the board to the accepted list; without this mirror a late-join resync sends
		// the customer car but not its order.
		if (takeJob) ServerData.Instance.UpdateSelectedJobs(job, true);
		ServerSend.JobActionPacket(fromClient, job, takeJob);
	}

	public static void SelectedJobPacket(int fromClient, Packet packet)
	{
		var job = packet.Read<ModJob>();
		var action = packet.Read<bool>();
		
		//Log.Info("cl: Received SelectedJobPacket!");
		ServerData.Instance.UpdateSelectedJobs(job, action);
		ServerSend.SelectedJobPacket(fromClient, job, action);
	}

	/// <summary>Accepting or handing over an order, selling a car (Phase 6): the first player wins, nobody is paid twice.</summary>
	private static void AnswerOnce(int fromClient, int request, PurchaseKind kind, string id, long amount)
	{
		var payouts = Server.Instance?.Payouts;
		var paid = ClientData.Instance != null && ClientData.Instance.gamemode != Gamemode.Sandbox;
		var status = payouts?.Decide(fromClient, kind, id, amount, paid) ?? TxnStatus.NotReady;
		Log.Info($"[ServerHandle] #{fromClient} {kind} {id} ({amount}): {status}.");
		using var answer = new Packet((int)PacketTypes.purchaseAnswer);
		PurchaseWire.WriteAnswer(answer, request, status);
		Server.Instance.Send(fromClient, answer, true);
	}

	public static void EndJobPacket(int fromClient, Packet packet)
	{
		var job = packet.Read<ModJob>();
		// Only the player the host approved may hand the order over, and the payout is granted here (not on the ask):
		// a lost answer no longer pays without completing, and the amount comes from the host's own order when known.
		var stored = ServerData.Instance.selectedJobs.FirstOrDefault(j => j.id == job.id)
		             ?? ServerData.Instance.jobs.FirstOrDefault(j => j.id == job.id);
		var amount = Math.Min((long)Math.Max(0, stored?.TotalPayout ?? job.TotalPayout), EconomyLedger.MaxMoneyDelta);
		var paid = ClientData.Instance == null || ClientData.Instance.gamemode != Gamemode.Sandbox;
		var status = Server.Instance?.Payouts?.CompleteEnd(fromClient, job.id.ToString(), amount, paid) ?? TxnStatus.NotReady;
		if (status != TxnStatus.Applied)
		{
			Log.Warn($"[ServerHandle] #{fromClient} ended order {job.id} without the host's approval ({status}); ignored.");
			return;
		}

		//Log.Info("SV : endjob !");
		ServerData.Instance.EndJob(job);
		// The hand-over deletes the car on every game without sending a delete: drop the host's stored copy too, or a
		// late join/resync re-sends a customer car whose order no longer exists.
		if (job.carLoaderID >= 0)
		{
			ServerData.Instance.DeleteCar(job.carLoaderID);
			Server.Instance?.CarDetector?.Touched(job.carLoaderID);
		}
		ServerSend.EndJobPacket(fromClient, job);
	}

	public static void SceneChangePacket(int fromClient, Packet packet)
	{
		var scene = packet.Read<GameScene>();

		if (!ServerData.Instance.connectedClients.TryGetValue(fromClient, out var user)) return;
		user.scene = scene;
		Server.Instance?.Locks?.PlayerChangedScene(fromClient);
		ServerSend.SceneChangePacket(fromClient, scene);
	}

	public static void ToolsMovePacket(int _fromClient, Packet _packet)
	{
		var tool = _packet.Read<ModIOSpecialType>();
		var place = _packet.Read<ModCarPlace>();
		var playSound = _packet.Read<bool>();

		ServerData.Instance.ChangeToolPosition(tool, place);

		ServerSend.ToolsMovePacket(_fromClient, tool, place, playSound);
	}
	

	public static void SetSpringClampPacket(int fromClient, Packet packet)
	{
		var item = packet.Read<ModGroupItem>();
		var instant = packet.Read<bool>();
		var mount = packet.Read<bool>();

		ServerData.Instance.SetSpringClampState(false, item);
		ServerSend.SetSpringClampPacket(fromClient, item, instant, mount);
	}

	public static void SpringClampClearPacket(int fromClient, Packet packet)
	{
		ServerData.Instance.SetSpringClampState(true, null);
		ServerSend.SpringClampClearPacket(fromClient);
	}

	public static void SetTireChangerPacket(int fromClient, Packet packet)
	{
		var item = packet.Read<ModGroupItem>();
		var instant = packet.Read<bool>();
		var connect = packet.Read<bool>();

		ServerData.Instance.SetTireChangerState(false, item);
		ServerSend.SetTireChangerPacket(fromClient, item, instant, connect);
	}

	public static void ClearTireChangerPacket(int fromClient, Packet packet)
	{
		ServerData.Instance.SetTireChangerState(true, null);
		ServerSend.ClearTireChangerPacket(fromClient);
	}
	
	public static void OilBin(int _fromclient, Packet _packet)
	{
		int loaderID = _packet.ReadInt();

		ServerSend.SendOilBin(_fromclient, loaderID);
	}

	public static void WheelBalancePacket(int fromClient, Packet packet)
	{
		int aType = packet.ReadInt();
		ModGroupItem item;
                
		if ((ModWheelBalancerActionType)aType == ModWheelBalancerActionType.start ||(ModWheelBalancerActionType)aType == ModWheelBalancerActionType.setGroup)
		{
			item = packet.Read<ModGroupItem>();
			ServerData.Instance.SetWheelBalancerState(item);
			ServerSend.WheelBalancerPacket(fromClient, (ModWheelBalancerActionType)aType, item);
			return;
		}
		ServerData.Instance.SetWheelBalancerState(null);
		ServerSend.WheelBalancerPacket(fromClient, (ModWheelBalancerActionType)aType);
	}
	

	public static void EngineCraneHandlePacket(int fromClient, Packet packet)
	{
		int action = packet.ReadInt();
		int carLoaderID = packet.ReadInt();
		ModGroupItem item;
		if (action == 1)
		{
			item = packet.Read<ModGroupItem>();
			
			ServerSend.EngineCraneHandlePacket(fromClient, action, carLoaderID, item);			
			return;
		}
		ServerSend.EngineCraneHandlePacket(fromClient, action, carLoaderID);
	}
	public static void EngineStandSetGroupPacket(int fromClient, Packet packet)
	{
		ModGroupItem engineGroup = packet.Read<ModGroupItem>();
		Vector3Serializable position = packet.Read<Vector3Serializable>();
		bool alt = packet.Read<bool>();

		Log.Debug("SV: received new engine");
		ServerData.Instance.SetEngineOnStand(engineGroup, position, alt);
		ServerSend.EngineStandSetGroupPacket(fromClient, engineGroup, position, alt);
	}
	
	
	public static void EngineStandTakeOffPacket(int fromClient, Packet packet)
	{
		bool alt = packet.Read<bool>();
		
		ServerData.Instance.ClearEngineFromStand(alt);
		ServerSend.EngineStandTakeOffPacket(fromClient, alt);
	}	
	public static void EngineStandAnglePacket(int fromClient, Packet packet)
	{
		float val = packet.Read<float>();
		bool alt = packet.Read<bool>();
		
		ServerData.Instance.IncreaseStandAngle(val, alt);
		ServerSend.IncreaseStandAnglePacket(fromClient, val, alt);
	}
	
	public static void CarWashPacket(int fromClient, Packet packet)
	{
		int loaderID = packet.ReadInt();
		bool interior = packet.Read<bool>();
		
		ServerData.Instance.SetCarWash(loaderID, interior); 
		ServerSend.CarWashPacket(fromClient, loaderID, interior);
	}
	
	public static void WelderPacket(int fromClient, Packet packet)
	{
		int loaderID = packet.ReadInt();
		
		ServerData.Instance.SetWelder(loaderID);
		ServerSend.WelderPacket(fromClient, loaderID);
	}
	
	public static void CarPaintPacket(int fromClient, Packet packet)
	{
		ModColor color = packet.Read<ModColor>();
		var txn = packet.UnreadLength() > 0 ? packet.ReadInt() : 0;

		// Phase 6: a paint job whose debit was refused never reaches the others, and the painter gets the old colour back.
		var economy = Server.Instance?.Economy;
		if (txn > 0 && economy != null && !economy.Charged(fromClient, txn))
		{
			Log.Info($"[ServerHandle] #{fromClient}'s paint job was not paid; undoing it.");
			var previous = ServerData.Instance.PaintshopColor();
			if (previous != null)
			{
				ServerSend.CarPaintPacket(fromClient, previous, true);
			}
			return;
		}
		ServerData.Instance.SetCarColor(color);
		ServerSend.CarPaintPacket(fromClient, color);
		// Phase 8 (assessment): the painter gets its paid colour back too (marked as its own), so every player's last
		// colour is the host's, also when a revert of an earlier refused job reached the painter after a later paint.
		ServerSend.CarPaintEchoPacket(fromClient, color);
	}
	
	public static void AddCarToParkPacket(int fromClient, Packet packet)
	{
		ModNewCarData car = packet.Read<ModNewCarData>();
		int index = packet.ReadInt();
		
		ServerData.Instance.AddCarToPark(car, index);
		ServerSend.AddCarToParkPacket(fromClient, car, index);
	}
	
	public static void RemoveCarFromParkPacket(int fromClient, Packet packet)
	{
		int index = packet.ReadInt();
		
		ServerData.Instance.RemoveCarFromPark(index);
		ServerSend.RemoveCarFromParkPacket(fromClient, index);
	}
	
	public static void RepairPartPacket(int fromClient, Packet packet)
	{
		ModPartInfo info = packet.Read<ModPartInfo>();
		bool isBody = packet.Read<bool>();
		bool success = packet.Read<bool>();
		
		ServerData.Instance.UpdatePartInfo(info, isBody, success);
		ServerSend.RepairPartPacket(fromClient, info, isBody, success);
	}
	
	public static void ResyncPacket(int fromClient, Packet packet)
	{
		PacketTypes resyncType = packet.Read<PacketTypes>();

		switch (resyncType)
		{
			case PacketTypes.loadCar:
				int carLoaderID = packet.ReadInt();
				if (carLoaderID == -1) ServerResyncs.ResyncAllCars(fromClient);
				else ServerResyncs.ResyncCar(fromClient, carLoaderID);
				break;
			case PacketTypes.newJob:
				ServerResyncs.ResyncJobs(fromClient);
				break;
			case PacketTypes.parkAdd:
				ServerResyncs.ResyncPark(fromClient);
				break;
			case PacketTypes.toolMove:
				ServerResyncs.ResyncTools(fromClient);
				break;
			case PacketTypes.garageUpgrade:
				ServerResyncs.ResyncUpgrade(fromClient);
				break;
			case PacketTypes.setSpringClamp:
			case PacketTypes.setTireChanger:
			case PacketTypes.wheelBalance:
				ServerResyncs.ResyncStations(fromClient);
				break;
			case PacketTypes.engineStandSetGroup:
				bool alt = packet.Read<bool>();
				ServerResyncs.ResyncEngineStand(fromClient, alt);
				break;
		}
	}

	public static void PlayerInCarPacket(int fromClient, Packet _packet)
	{
		var isInCar = _packet.Read<bool>();
		var carLoaderID = _packet.ReadInt();
		
		// Business Logic: Update player's car state on server
		if (ServerData.Instance.connectedClients.TryGetValue(fromClient, out var player))
		{
			player.isInCar = isInCar;
			player.carLoaderID = carLoaderID;
			Log.Debug($"[ServerHandle->PlayerInCarPacket] Player {fromClient} car state: InCar={isInCar}, CarLoaderID={carLoaderID}");
		}
		
		// Business Logic: Broadcast to all clients
		ServerSend.PlayerInCarPacket(fromClient, isInCar, carLoaderID);
	}
}