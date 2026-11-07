using System;
using CMS21Together.ClientSide.Features;
using CMS21Together.Shared.Features;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using CMS21Together.ClientSide;
using CMS21Together.ClientSide.Data;
using CMS21Together.ServerSide.Data;
using CMS21Together.Shared;
using CMS21Together.Shared.Data;
using CMS21Together.Shared.Economy;
using CMS21Together.Shared.Locks;
using CMS21Together.Shared.Net;
using CMS21Together.Shared.Persistence;
using CMS21Together.Shared.Data.Vanilla;
using CMS21Together.Shared.World;
using CMS21Together.Shared.Steam;
using Steamworks;
using UnityEngine;

namespace CMS21Together.ServerSide;

/// <summary>
/// Listen server. The host plays through its own <see cref="Client"/>, joined over an in-process loopback link in
/// slot 1. Everything runs on the main thread inside <see cref="Tick"/>.
/// </summary>
public class Server
{
	public static Server Instance;

	public NetworkType networkType;
	public bool isRunning;
	public string serverID;

	private NetHost host;
	private PacketRouter router;
	private SteamServerSocket steam;
	private TcpAcceptor tcp;
	private string pendingStopReason;
	private bool stopping;

	public NetHost Host => host;

	/// <summary>The host started the game (left the lobby): players who join now load straight in (Phase 5).</summary>
	public bool GameStarted { get; set; }

	/// <summary>Who works on which car part, tool or lifter (Phase 2). Null when no session runs.</summary>
	public LockServer Locks { get; private set; }

	/// <summary>The shared wallet (Phase 3). Null when no session runs.</summary>
	public EconomyServer Economy { get; private set; }

	/// <summary>The shared inventory, items and item groups (Phase 5). Null when no session runs.</summary>
	public InventoryHost<ModItem> Inventory { get; private set; }

	public InventoryHost<ModGroupItem> GroupInventory { get; private set; }

	/// <summary>Per-car desync detector (Phase 6). Null when no session runs.</summary>
	public CarDesyncDetector CarDetector { get; private set; }

	/// <summary>Ghost and missing cars per player (Phase 6b). Null when no session runs.</summary>
	public CarRoster CarRoster { get; private set; }

	/// <summary>Phase 6b feature messages, relayed while the feature is on in the session. Null when no session runs.</summary>
	public FeatureRelay FeatureRelay { get; private set; }

	/// <summary>Orders accepted and handed over, cars sold: each happens once (Phase 6). Null when no session runs.</summary>
	public Payouts Payouts { get; private set; }

	/// <summary>Undoes the inventory side of part writes lost to another player (Phase 5).</summary>
	public RaceReconciler<ModItem> Races { get; private set; }

	/// <summary>First removal of an item wins, and pays for one thing (Phase 8). Null when no session runs.</summary>
	public RemovalArbiter<ModItem> Removals { get; private set; }

	public RemovalArbiter<ModGroupItem> GroupRemovals { get; private set; }

	/// <summary>Save copies sent to players and received from them (Phase 4). Null when no session runs.</summary>
	public ReplicaHub Replicas { get; private set; }

	/// <summary>Starts listening and joins the host's own client. Returns false (with a user-facing error) on failure.</summary>
	public bool StartServer(NetworkType type, out string error)
	{
		error = null;
		if (isRunning || host != null) Stop("The host restarted the session.");

		networkType = type;
		serverID = null;
		pendingStopReason = null;
		ServerData.Instance = new ServerData();
		var clock = Stopwatch.StartNew();
		Locks = new LockServer(() => clock.ElapsedMilliseconds, (slot, p) => Send(slot, p, true), p => SendToAll(p, true));
		Economy = new EconomyServer((slot, p) => Send(slot, p, true), () => ServerData.Instance?.connectedClients.Keys ?? (IEnumerable<int>)Array.Empty<int>());
		IEnumerable<int> Validated() => ServerData.Instance?.connectedClients.Keys.ToList() ?? (IEnumerable<int>)Array.Empty<int>();
		Inventory = new InventoryHost<ModItem>(InventoryKind.Items, i => i.UID, (slot, p) => Send(slot, p, true), Validated);
		GroupInventory = new InventoryHost<ModGroupItem>(InventoryKind.Groups, g => g.UID, (slot, p) => Send(slot, p, true), Validated);
		// Phase 6: an item bought with a debit joins the shared inventory only if that debit was charged.
		Inventory.Charged = (slot, txn) => Economy == null || Economy.Charged(slot, txn);
		GroupInventory.Charged = Inventory.Charged;
		Races = new RaceReconciler<ModItem>(i => i.UID, i => i.ID, () => clock.ElapsedMilliseconds);
		CarDetector = new CarDesyncDetector(() => clock.ElapsedMilliseconds);
		CarRoster = new CarRoster();
		Payouts = new Payouts(Economy, () => clock.ElapsedMilliseconds);
		Features.HostStarted();
		FeatureRelay = Features.CreateRelay(this);
		Inventory.Processed += Races.Record;
		Removals = new RemovalArbiter<ModItem>(i => i.UID, i => i.ID, () => clock.ElapsedMilliseconds);
		GroupRemovals = new RemovalArbiter<ModGroupItem>(g => g.UID, g => g.ID, () => clock.ElapsedMilliseconds);
		Inventory.Processed += Removals.Record;
		GroupInventory.Processed += GroupRemovals.Record;
		Replicas = new ReplicaHub(() => clock.ElapsedMilliseconds, (slot, p, bulk) => Send(slot, p, true, bulk)) { Accept = PlusSaves.AcceptUpload };
		Replicas.Received += PlusSaves.OnUpload;
		PlusSaves.SessionStarted();

		var netHost = new NetHost(MainMod.Config.MaxPlayers, MainMod.NET_VERSION);
		host = netHost;
		router = CreateRouter();
		router.PeerMisbehaved += (slot, reason) =>
		{
			// The host's own player is never kicked: that would end everyone's session.
			if (slot == 1) Log.Error($"[Server] The host's own packets keep failing: {reason}");
			else netHost.Kick(slot, reason);
		};
		netHost.PeerConnected += OnPeerConnected;
		netHost.FrameReceived += OnFrame;
		netHost.PeerDisconnected += OnPeerDisconnected;

		try
		{
			if (type == NetworkType.Steam)
			{
				if (!MainMod.IsSteamAvailable)
					throw new InvalidOperationException("Steam is not available. Switch the network type to TCP.");
				steam = SteamNetworkingSockets.CreateRelaySocket<SteamServerSocket>();
				if (steam == null)
					throw new InvalidOperationException("Steam could not create the relay socket.");
				steam.LinkConnected = link => netHost.Accept(link);
				serverID = SteamworksUtils.GetServerID(SteamClient.SteamId.Value);
				Log.Info($"[Server] Listening on Steam relay, lobby code {serverID}.");
			}
			else
			{
				tcp = new TcpAcceptor(MainMod.PORT, link => netHost.Accept(link));
				Log.Info($"[Server] Listening on TCP port {MainMod.PORT}.");
			}
		}
		catch (Exception ex)
		{
			error = ex.Message;
			Log.Error("[Server] Could not start the server", ex);
			Cleanup();
			return false;
		}

		isRunning = true;
		Application.runInBackground = true;

		var (serverEnd, clientEnd) = SimLink.CreatePair(name: "loopback");
		netHost.Accept(serverEnd);
		Client.Instance.ConnectToHost(clientEnd, netHost.HostKey);
		Log.Info("[Server] Server started.");
		return true;
	}

	/// <summary>Main-thread pump: Steam receive, transport events, packet handlers.</summary>
	public void Tick()
	{
		if (host == null) return;
		try
		{
			steam?.Receive();
		}
		catch (Exception ex)
		{
			Log.Error("[Server] Steam receive failed", ex);
		}

		host.Tick();
		Locks?.Tick();

		if (pendingStopReason != null)
		{
			var reason = pendingStopReason;
			pendingStopReason = null;
			Stop(reason);
		}
	}

	/// <summary>Saves the session, says goodbye to every player and closes the listeners. Synchronous.</summary>
	public void Stop(string reason = "The host closed the session.")
	{
		if (stopping || host == null) return;
		stopping = true;
		try
		{
			if (isRunning)
			{
				try
				{
					SaveSession();
				}
				catch (Exception ex)
				{
					Log.Error("[Server] Saving the session failed", ex);
				}
			}

			if (Client.Instance != null && Client.Instance.IsHost)
				Client.Instance.Disconnect(reason, false);

			try
			{
				host.Stop(reason);
			}
			catch (Exception ex)
			{
				Log.Error("[Server] Stopping the transport failed", ex);
			}
			Cleanup();
			Log.Info($"[Server] Server closed: {reason}");
		}
		finally
		{
			stopping = false;
		}
	}

	/// <summary>Kept for existing callers; the shutdown itself is synchronous.</summary>
	public IEnumerator CloseServer()
	{
		Stop();
		yield break;
	}

	/// <summary>Disconnects one player with a reason shown on their screen.</summary>
	public void Kick(int slot, string reason) => host?.Kick(slot, reason);

	public bool IsConnected(int slot) => host?.GetPeer(slot) != null;

	internal EnqueueResult Send(int slot, Packet packet, bool reliable, bool bulk = false)
	{
		if (host == null || !isRunning) return EnqueueResult.Dropped;
		return host.Send(slot, packet.FrameBuffer, packet.FrameLength, bulk ? SendPriority.Bulk : SendPriority.Normal, !reliable);
	}

	internal void SendToAll(Packet packet, bool reliable, int except = 0)
	{
		if (host == null || !isRunning) return;
		var validated = ServerData.Instance?.connectedClients;
		foreach (var peer in host.Peers)
			// Players still validating get nothing until they are in the lobby.
			if (peer.SlotId != except && validated != null && validated.ContainsKey(peer.SlotId))
				peer.Send(packet.FrameBuffer, packet.FrameLength, SendPriority.Normal, !reliable);
	}

	/// <summary>
	/// A player's movement to the others in the same scene (Phase 7): unreliable, and at most one pending frame per
	/// player and kind on each link (latest wins), so a slow link never shows stale positions.
	/// </summary>
	internal void SendMovement(Packet packet, int player)
	{
		if (host == null || !isRunning) return;
		var players = ServerData.Instance?.connectedClients;
		if (players == null || !players.TryGetValue(player, out var mover)) return;
		var key = CoalesceKeys.Movement(player, packet.Id);
		foreach (var peer in host.Peers)
		{
			if (peer.SlotId == player || !players.TryGetValue(peer.SlotId, out var viewer)) continue;
			if (!Interest.SameScene(mover.scene, viewer.scene)) continue;
			peer.Send(packet.FrameBuffer, packet.FrameLength, SendPriority.Normal, true, key);
		}
	}

	private void SaveSession()
	{
		if (!SavesManager.ModSaves.ContainsKey(SavesManager.currentSaveIndex)) return;
		// In game this is a full save: the game writes its profile right after (exit to menu), and the commit finishes
		// once it has. Closing from the lobby only writes the sidecar (money and missions are not loaded there).
		if (GameData.Instance != null) SavesManager.BeginSave(SavesManager.currentSaveIndex);
		else SavesManager.SaveModSave(SavesManager.currentSaveIndex);
	}

	private void OnFrame(NetConnection connection, NetFrame frame)
	{
		var slot = connection.SlotId;
		// Game traffic is only accepted once the player passed validation and is in the lobby.
		if (frame.Type != (int)PacketTypes.connect && (ServerData.Instance == null || !ServerData.Instance.connectedClients.ContainsKey(slot)))
		{
			Log.Trace($"[Server] Ignoring packet {frame.Type} from #{slot} before validation.");
			return;
		}
		router.Dispatch(slot, frame);
	}

	private void OnPeerConnected(NetConnection connection)
	{
		var saveId = SavesManager.ModSaves.TryGetValue(SavesManager.currentSaveIndex, out var save) ? save?.saveId : null;
		ServerSend.IdentityRequestPacket(connection.SlotId, saveId);
	}

	private void OnPeerDisconnected(NetConnection connection, string reason)
	{
		var slot = connection.SlotId;
		router?.ResetPeer(slot);
		Locks?.PlayerLeft(slot);
		Economy?.PlayerLeft(slot);
		Inventory?.PlayerLeft(slot);
		Races?.PlayerLeft(slot);
		Removals?.PlayerLeft(slot);
		GroupRemovals?.PlayerLeft(slot);
		CarDetector?.PlayerLeft(slot);
		CarRoster?.PlayerLeft(slot);
		ServerHandle.ForgetApprovals(slot);
		GroupInventory?.PlayerLeft(slot);
		PlusSaves.PlayerLeft(slot);
		var data = ServerData.Instance;
		if (data != null && data.connectedClients.TryGetValue(slot, out var user))
		{
			// Audit A8: keep the leaver's progress, then forget the slot so nothing stale stays behind.
			if (!stopping)
			{
				try
				{
					SavesManager.CapturePlayer(user);
				}
				catch (Exception ex)
				{
					Log.Error($"[Server] Keeping the progress of {user.username} failed", ex);
				}
			}
			data.connectedClients.Remove(slot);
			Log.Info($"[Server] {user.username} left the session: {reason}");
		}
		if (stopping) return;

		ServerSend.PlayerLeftPacket(slot);
		if (slot == 1)
			pendingStopReason = "The host left the session.";
		else if (data != null)
			PlusSaves.Scheduler.PlayerLeft();
	}

	private void Cleanup()
	{
		try
		{
			steam?.Shutdown("The host closed the session.");
		}
		catch (Exception ex)
		{
			Log.Warn($"[Server] Closing the Steam socket failed: {ex.Message}");
		}
		steam = null;
		tcp?.Dispose();
		tcp = null;
		router?.Clear();
		router = null;
		Locks = null;
		Economy = null;
		Inventory = null;
		GroupInventory = null;
		Races = null;
		Removals = null;
		GroupRemovals = null;
		CarDetector = null;
		CarRoster = null;
		Payouts = null;
		FeatureRelay = null;
		Replicas?.Clear();
		Replicas = null;
		host = null;
		isRunning = false;
		GameStarted = false;
		pendingStopReason = null;
		Application.runInBackground = false;
	}

	private static PacketRouter CreateRouter()
	{
		var r = new PacketRouter("Server");
		r.Register((int)PacketTypes.connect, ServerHandle.ConnectValidationPacket);
		r.Register((int)PacketTypes.readyState, ServerHandle.ReadyPacket);

		r.Register((int)PacketTypes.position, ServerHandle.PositionPacket);
		r.Register((int)PacketTypes.rotation, ServerHandle.RotationPacket);
		r.Register((int)PacketTypes.sceneChange, ServerHandle.SceneChangePacket);

		r.Register((int)PacketTypes.lockRequest, (slot, p) => Instance?.Locks?.HandleRequest(slot, p));
		r.Register((int)PacketTypes.lockRenew, (slot, p) => Instance?.Locks?.HandleRenew(slot, p));
		r.Register((int)PacketTypes.lockRelease, (slot, p) => Instance?.Locks?.HandleRelease(slot, p));

		r.Register((int)PacketTypes.invPush, ServerHandle.InventoryPushPacket);
		r.Register((int)PacketTypes.invRequest, ServerHandle.InventoryRequestPacket);
		r.Register((int)PacketTypes.invCheck, ServerHandle.InventoryCheckPacket);

		r.Register((int)PacketTypes.econTxn, (slot, p) => Instance?.Economy?.HandleTxn(slot, p));
		r.Register((int)PacketTypes.purchaseAsk, ServerHandle.PurchaseAskPacket);
		r.Register((int)PacketTypes.cheatMoney, ServerHandle.CheatMoneyPacket);
		r.Register((int)PacketTypes.bulkSell, ClientSide.Features.Inventory.BulkSellFeature.HandleSale);
		r.Register((int)PacketTypes.feature, (slot, packet) => Instance.FeatureRelay?.Handle(slot, FeatureMessage.Read(packet)));
		r.Register((int)PacketTypes.carCheck, ServerHandle.CarCheckPacket);
		r.Register((int)PacketTypes.replicaAck, (slot, p) => Instance?.Replicas?.HandleAck(slot, p));
		r.Register((int)PacketTypes.replicaBegin, (slot, p) => Instance?.Replicas?.HandleBegin(slot, p));
		r.Register((int)PacketTypes.replicaChunk, (slot, p) => Instance?.Replicas?.HandleChunk(slot, p));
		r.Register((int)PacketTypes.replicaCancel, (slot, p) => Instance?.Replicas?.HandleCancel(slot, p));
		r.Register((int)PacketTypes.replicaOffer, PlusSaves.HandleOffer);
		r.Register((int)PacketTypes.exp, ServerHandle.ExpPacket);
		r.Register((int)PacketTypes.point, ServerHandle.PointPacket);
		r.Register((int)PacketTypes.skillChange, ServerHandle.SkillChangePacket);
		r.Register((int)PacketTypes.garageUpgrade, ServerHandle.GarageUpgradePacket);
		r.Register((int)PacketTypes.resync, ServerHandle.ResyncPacket);

		r.Register((int)PacketTypes.lifter, ServerHandle.LifterPacket);
		r.Register((int)PacketTypes.setSpringClamp, ServerHandle.SetSpringClampPacket);
		r.Register((int)PacketTypes.clearSpringClamp, ServerHandle.SpringClampClearPacket);
		r.Register((int)PacketTypes.setTireChanger, ServerHandle.SetTireChangerPacket);
		r.Register((int)PacketTypes.clearTireChanger, ServerHandle.ClearTireChangerPacket);
		r.Register((int)PacketTypes.wheelBalance, ServerHandle.WheelBalancePacket);
		r.Register((int)PacketTypes.toolMove, ServerHandle.ToolsMovePacket);
		r.Register((int)PacketTypes.oilBinUse, ServerHandle.OilBin);
		r.Register((int)PacketTypes.engineCrane, ServerHandle.EngineCraneHandlePacket);
		r.Register((int)PacketTypes.engineStandSetGroup, ServerHandle.EngineStandSetGroupPacket);
		r.Register((int)PacketTypes.engineStandTakeOff, ServerHandle.EngineStandTakeOffPacket);
		r.Register((int)PacketTypes.engineStandAngle, ServerHandle.EngineStandAnglePacket);
		r.Register((int)PacketTypes.carWash, ServerHandle.CarWashPacket);
		r.Register((int)PacketTypes.useWelder, ServerHandle.WelderPacket);
		r.Register((int)PacketTypes.carPaint, ServerHandle.CarPaintPacket);
		r.Register((int)PacketTypes.repairPart, ServerHandle.RepairPartPacket);

		r.Register((int)PacketTypes.parkAdd, ServerHandle.AddCarToParkPacket);
		r.Register((int)PacketTypes.parkRemove, ServerHandle.RemoveCarFromParkPacket);
		r.Register((int)PacketTypes.loadJobCar, ServerHandle.LoadJobCarPacket);
		r.Register((int)PacketTypes.loadCar, ServerHandle.LoadCarPacket);
		r.Register((int)PacketTypes.bodyPart, ServerHandle.BodyPartPacket);
		r.Register((int)PacketTypes.partScript, ServerHandle.PartScriptPacket);
		r.Register((int)PacketTypes.carFluid, ServerHandle.CarFluidPacket);

		r.Register((int)PacketTypes.deleteCar, ServerHandle.DeleteCarPacket);
		r.Register((int)PacketTypes.carPosition, ServerHandle.CarPositionPacket);

		r.Register((int)PacketTypes.newJob, ServerHandle.JobPacket);
		r.Register((int)PacketTypes.jobAction, ServerHandle.JobActionPacket);
		r.Register((int)PacketTypes.selectedJob, ServerHandle.SelectedJobPacket);
		r.Register((int)PacketTypes.endJob, ServerHandle.EndJobPacket);
		r.Register((int)PacketTypes.playerInCar, ServerHandle.PlayerInCarPacket);
		return r;
	}
}
