using System;
using System.Diagnostics;
using CMS21Together.ClientSide.Data;
using CMS21Together.ClientSide.Data.Garage;
using CMS21Together.ClientSide.Data.Garage.Car;
using CMS21Together.ClientSide.Data.Handle;
using CMS21Together.ClientSide.Data.Player;
using CMS21Together.ServerSide;
using CMS21Together.Shared;
using CMS21Together.Shared.Data;
using CMS21Together.Shared.Economy;
using CMS21Together.Shared.Locks;
using CMS21Together.Shared.Net;
using CMS21Together.Shared.Persistence;
using CMS21Together.Shared.Steam;
using CMS21Together.Shared.Updates;
using Steamworks;
using UnityEngine;
using SceneManager = UnityEngine.SceneManagement.SceneManager;

namespace CMS21Together.ClientSide;

/// <summary>
/// The local player's connection. Remote players connect over TCP or the Steam relay; the host connects to its own
/// server over an in-process loopback link. Everything runs on the main thread inside <see cref="Tick"/>.
/// </summary>
public class Client
{
	public static Client Instance;

	/// <summary>True while in a session (connecting, lobby or in game). Gameplay hooks only sync when this is set.</summary>
	public bool isConnected;

	/// <summary>Set once this connection answered the host's identity request; later requests are ignored.</summary>
	internal bool identitySent;

	public NetworkType networkType;

	private NetClient net;
	private PacketRouter router;
	private SteamClientConnection steam;
	private bool walletFailed;
	private bool inventoryFailed;
	private bool carChecksFailed;

	public event Action OnConnected;
	public event Action<string> OnDisconnected;

	/// <summary>True when this client is the host's own player.</summary>
	public bool IsHost { get; private set; }

	public int RttMs => net?.RttMs ?? -1;

	/// <summary>Players the host allows (from the handshake); 0 when not connected.</summary>
	public int MaxPlayers => net?.MaxPlayers ?? 0;

	/// <summary>This player's view of who works on what (Phase 2). Null outside a session.</summary>
	public LockClient Locks { get; private set; }

	/// <summary>This player's view of the shared wallet (Phase 3). Null when not connected.</summary>
	public EconomyClient Economy { get; private set; }

	/// <summary>Save copies from the host, and this player's copy sent back for a restore (Phase 4). Null for the host.</summary>
	public ReplicaHub Replicas { get; private set; }

	/// <summary>Id of the host's save, from its identity request (null until then).</summary>
	public string HostSaveId { get; internal set; }

	/// <summary>Connects to a remote host. <paramref name="address"/> is an IP/host name (TCP) or a lobby code (Steam).</summary>
	public bool ConnectToServer(NetworkType type, string address, out string error)
	{
		error = null;
		if (isConnected) Disconnect("Joining another session.", false);

		NetLink link;
		try
		{
			if (type == NetworkType.Steam)
			{
				if (!MainMod.IsSteamAvailable)
				{
					error = "Steam is not available. Switch the network type to TCP.";
					return false;
				}
				if (!SteamworksUtils.TryConvertServerID(address, out var hostId))
				{
					error = "This lobby code is not valid.";
					return false;
				}
				Log.Debug($"[Client] Lobby code {address} -> {hostId}.");
				steam = SteamNetworkingSockets.ConnectRelay<SteamClientConnection>(hostId);
				if (steam == null)
				{
					error = "Steam could not open a connection.";
					return false;
				}
				link = steam.Attach(hostId);
			}
			else
			{
				if (string.IsNullOrWhiteSpace(address))
				{
					error = "Enter the host's IP address.";
					return false;
				}
				link = new TcpLink(address.Trim(), MainMod.PORT);
			}
		}
		catch (Exception ex)
		{
			Log.Error("[Client] Could not start the connection", ex);
			CloseSteam();
			error = "Could not connect: " + ex.Message;
			return false;
		}

		Begin(type, false, link, null);
		return true;
	}

	/// <summary>Joins the local server as the host's player.</summary>
	public void ConnectToHost(NetLink link, byte[] hostKey)
	{
		if (isConnected) Disconnect("Hosting a new session.", false);
		Begin(Server.Instance.networkType, true, link, hostKey);
	}

	private void Begin(NetworkType type, bool host, NetLink link, byte[] hostKey)
	{
		networkType = type;
		IsHost = host;
		ClientData.Instance = new ClientData();
		Data.Player.Movement.Reset();
		router = CreateRouter();
		var clock = Stopwatch.StartNew();
		Locks = new LockClient(0, () => clock.ElapsedMilliseconds, p => SendData(p, true));
		Locks.Denied += ActionGate.ShowDenied;
		Economy = new EconomyClient(p => SendData(p, true));
		Economy.Changed += Wallet.OnBalance;
		Economy.Rejected += Wallet.OnRejected;
		Wallet.Reset();
		Purchases.Reset(Economy);
		CarChecks.Reset();
		HostSaveId = null;
		Replicas = null;
		if (!host)
		{
			Replicas = new ReplicaHub(() => clock.ElapsedMilliseconds, (_, p, bulk) => SendData(p, true, bulk)) { Accept = PlusSaves.AcceptReplica };
			Replicas.Received += PlusSaves.OnReplica;
		}

		var client = new NetClient(MainMod.NET_VERSION);
		net = client;
		client.Connected += OnNetConnected;
		client.FrameReceived += frame =>
		{
			// Game changes made while handling the host's packets are the host's state, not this player's actions:
			// hooks must not send them back (echoes, money counted twice).
			using (ActionGate.Apply()) router?.Dispatch(0, frame);
		};
		client.Disconnected += OnNetDisconnected;

		isConnected = true;
		identitySent = false;
		Application.runInBackground = true;
		client.Connect(link, hostKey);
	}

	/// <summary>Main-thread pump: Steam receive, transport events, packet handlers.</summary>
	public void Tick()
	{
		if (net == null) return;
		try
		{
			steam?.Receive();
		}
		catch (Exception ex)
		{
			Log.Error("[Client] Steam receive failed", ex);
		}
		net?.Tick();
		Locks?.Pump();
		try
		{
			CarChecks.Pump();
		}
		catch (Exception ex)
		{
			if (!carChecksFailed) Log.Error("[Client] Checking the cars failed", ex);
			carChecksFailed = true;
		}
		try
		{
			Data.Player.Inventory.Pump();
		}
		catch (Exception ex)
		{
			if (!inventoryFailed) Log.Error("[Client] Updating the shared inventory failed", ex);
			inventoryFailed = true;
		}
		try
		{
			Wallet.Pump();
		}
		catch (Exception ex)
		{
			if (!walletFailed) Log.Error("[Client] Updating the shared wallet failed", ex);
			walletFailed = true;
		}
	}

	public void SendData(Packet packet, bool reliable, bool bulk = false, long coalesceKey = 0)
	{
		if (net == null || net.State != NetClientState.Connected) return;
		net.Send(packet.FrameBuffer, packet.FrameLength, bulk ? SendPriority.Bulk : SendPriority.Normal, !reliable, coalesceKey);
	}

	/// <summary>Leaves the session. When <paramref name="returnToMenu"/> is set and a level is loaded, goes back to the menu.</summary>
	public void Disconnect(string reason = "Left the session.", bool returnToMenu = true)
	{
		if (net == null && !isConnected) return;
		try
		{
			net?.Disconnect(reason);
		}
		catch (Exception ex)
		{
			Log.Warn($"[Client] Disconnect failed: {ex.Message}");
		}
		Cleanup();
		Log.Info($"[Client] Disconnected: {reason}");
		if (returnToMenu) LoadMenu();
		RestoreOwnedContents();
	}

	/// <summary>Removes every UI callback; the menu sets fresh ones before each connection attempt.</summary>
	public void ResetEvents()
	{
		OnConnected = null;
		OnDisconnected = null;
	}

	public void OnConnectedInvoke() => OnConnected?.Invoke();

	/// <summary>
	/// The host refused the join because it runs another Together+ version: remember the host's version so the updater
	/// installs exactly that one on the next launch (the DLL cannot be swapped while the game runs).
	/// </summary>
	private static void NoteVersionMismatch(string reason)
	{
		var hostVersion = VersionText.HostVersionIn(reason);
		if (hostVersion == null) return;
		if (VersionText.AreEqual(hostVersion, MainMod.PLUS_VERSION))
		{
			Log.Warn($"[Update] The host rejected the join while reporting the same version ({hostVersion}). Rejoin, or run the installer again on both sides.");
			return;
		}

		Log.Info($"[Update] The host runs Together+ {hostVersion}; this game runs {MainMod.PLUS_VERSION}.");
		try
		{
			UpdateFiles.WriteRequest(PlusPaths.Root, hostVersion);
			Log.Info($"[Update] Restart the game: Together+ will download and install {hostVersion} before loading, to match the host.");
		}
		catch (Exception ex)
		{
			Log.Warn($"[Update] Could not save the version to install: {ex.Message}. Run install.ps1 and choose the host's version.");
		}
	}

	private void OnNetConnected()
	{
		ClientData.UserData.playerID = net.SlotId;
		if (Locks != null) Locks.Table.Self = net.SlotId;
		Log.Info($"[Client] Connected as player #{net.SlotId}.");
		OnConnectedInvoke();
		// The host answers with an identity request; validation is sent from there (audit A19).
	}

	private void OnNetDisconnected(string reason)
	{
		Log.Info($"[Client] Connection ended: {reason}");
		NoteVersionMismatch(reason);
		var inMenu = IsMenuActive();
		Cleanup();
		if (inMenu)
		{
			try
			{
				OnDisconnected?.Invoke(reason);
			}
			catch (Exception ex)
			{
				Log.Error("[Client] Disconnect callback failed", ex);
			}
		}
		else
			LoadMenu();
		RestoreOwnedContents();
	}

	private void Cleanup()
	{
		isConnected = false;
		IsHost = false;
		net = null;
		router?.Clear();
		router = null;
		Locks = null;
		Economy = null;
		Replicas?.Clear();
		Replicas = null;
		HostSaveId = null;
		Wallet.Reset();
		// Review fix: a garage left by a disconnect is not ready any more, or the next join's spawn ran in the menu.
		GameLoadHook.Reset();
		Features.Features.SessionEnded();
		CloseSteam();
		if (Server.Instance == null || !Server.Instance.isRunning)
			Application.runInBackground = false;
	}

	private void CloseSteam()
	{
		if (steam == null) return;
		try
		{
			steam.Link?.Close("Left the session.");
			steam.Close();
		}
		catch (Exception ex)
		{
			Log.Warn($"[Client] Closing the Steam connection failed: {ex.Message}");
		}
		steam = null;
	}

	/// <summary>Gives back the DLC ownership the host's content list replaced while in the session.</summary>
	private static void RestoreOwnedContents()
	{
		try
		{
			if (ContentManager.Instance != null) ApiCalls.API_M2(ContentManager.Instance.ownedContents);
		}
		catch (Exception ex)
		{
			Log.Warn($"[Client] Restoring owned contents failed: {ex.Message}");
		}
	}

	private static bool IsMenuActive() => SceneManager.GetActiveScene().name == "Menu";

	private static void LoadMenu()
	{
		if (IsMenuActive()) return;
		var manager = NotificationCenter.m_instance;
		if (manager != null)
			manager.StartCoroutine(manager.SelectSceneToLoad("Menu", SceneType.Menu, true, true));
	}

	private static PacketRouter CreateRouter()
	{
		var r = new PacketRouter("Client");
		r.Register((int)PacketTypes.userData, ClientHandle.UserDataPacket);
		r.Register((int)PacketTypes.identityRequest, ClientHandle.IdentityRequestPacket);
		r.Register((int)PacketTypes.playerLeft, ClientHandle.PlayerLeftPacket);
		r.Register((int)PacketTypes.readyState, ClientHandle.ReadyPacket);
		r.Register((int)PacketTypes.start, ClientHandle.StartPacket);
		r.Register((int)PacketTypes.contentInfo, ClientHandle.ContentsInfoPacket);

		r.Register((int)PacketTypes.spawn, ClientHandle.SpawnPacket);
		r.Register((int)PacketTypes.uidRange, ClientHandle.UidRangePacket);
		r.Register((int)PacketTypes.position, ClientHandle.PositionPacket);
		r.Register((int)PacketTypes.rotation, ClientHandle.RotationPacket);
		r.Register((int)PacketTypes.sceneChange, ClientHandle.SceneChangePacket);

		r.Register((int)PacketTypes.lockReply, p => Instance?.Locks?.HandleReply(p));
		r.Register((int)PacketTypes.lockState, p => Instance?.Locks?.HandleState(p));
		r.Register((int)PacketTypes.lockSnapshot, p => Instance?.Locks?.HandleSnapshot(p));

		r.Register((int)PacketTypes.invOp, Data.Player.Inventory.HandleOp);
		r.Register((int)PacketTypes.invSnapshot, Data.Player.Inventory.HandleSnapshot);

		r.Register((int)PacketTypes.econBalance, p => Instance?.Economy?.HandleBalance(p));
		r.Register((int)PacketTypes.purchaseAnswer, Purchases.HandleAnswer);
		r.Register((int)PacketTypes.replicaBegin, p => Instance?.Replicas?.HandleBegin(0, p));
		r.Register((int)PacketTypes.replicaChunk, p => Instance?.Replicas?.HandleChunk(0, p));
		r.Register((int)PacketTypes.replicaCancel, p => Instance?.Replicas?.HandleCancel(0, p));
		r.Register((int)PacketTypes.replicaAck, p => Instance?.Replicas?.HandleAck(0, p));
		r.Register((int)PacketTypes.replicaRequest, PlusSaves.HandleRequest);

		r.Register((int)PacketTypes.lifter, ClientHandle.LifterPacket);
		r.Register((int)PacketTypes.setSpringClamp, ClientHandle.SetSpringClampPacket);
		r.Register((int)PacketTypes.clearSpringClamp, ClientHandle.SpringClampClearPacket);
		r.Register((int)PacketTypes.setTireChanger, ClientHandle.SetTireChangerPacket);
		r.Register((int)PacketTypes.clearTireChanger, ClientHandle.ClearTireChangerPacket);
		r.Register((int)PacketTypes.wheelBalance, ClientHandle.WheelBalancePacket);
		r.Register((int)PacketTypes.oilBinUse, ClientHandle.OilBinPacket);
		r.Register((int)PacketTypes.toolMove, ClientHandle.ToolsMovePacket);
		r.Register((int)PacketTypes.engineCrane, ClientHandle.EngineCraneHandlePacket);
		r.Register((int)PacketTypes.engineStandSetGroup, ClientHandle.EngineSetGroupPacket);
		r.Register((int)PacketTypes.engineStandTakeOff, ClientHandle.EngineTakeOffPacket);
		r.Register((int)PacketTypes.engineStandAngle, ClientHandle.EngineStandAnglePacket);
		r.Register((int)PacketTypes.repairPart, ClientHandle.RepairPartPacket);
		r.Register((int)PacketTypes.carFluid, ClientHandle.CarFluidPacket);
		r.Register((int)PacketTypes.carWash, ClientHandle.CarWashPacket);
		r.Register((int)PacketTypes.useWelder, ClientHandle.WelderPacket);
		r.Register((int)PacketTypes.carPaint, ClientHandle.CarPaintPacket);

		r.Register((int)PacketTypes.parkAdd, ClientHandle.AddCarToParkPacket);
		r.Register((int)PacketTypes.parkRemove, ClientHandle.RemoveCarFromParkPacket);
		r.Register((int)PacketTypes.loadCar, ClientHandle.LoadCarPacket);
		r.Register((int)PacketTypes.bodyPart, ClientHandle.BodyPartPacket);
		r.Register((int)PacketTypes.partScript, ClientHandle.PartScriptPacket);

		r.Register((int)PacketTypes.deleteCar, ClientHandle.DeleteCarPacket);
		r.Register((int)PacketTypes.carPosition, ClientHandle.CarPositionPacket);

		r.Register((int)PacketTypes.garageUpgrade, ClientHandle.GarageUpgradePacket);
		r.Register((int)PacketTypes.newJob, ClientHandle.JobPacket);
		r.Register((int)PacketTypes.jobAction, ClientHandle.JobActionPacket);
		r.Register((int)PacketTypes.selectedJob, ClientHandle.SelectedJobPacket);
		r.Register((int)PacketTypes.endJob, ClientHandle.EndJobPacket);
		r.Register((int)PacketTypes.statsFix, ClientHandle.StatsFixPacket);
		r.Register((int)PacketTypes.features, Features.Features.HandleConfig);
		r.Register((int)PacketTypes.feature, Features.Features.HandleMessage);
		r.Register((int)PacketTypes.playerInCar, ClientHandle.PlayerInCarPacket);
		r.Register((int)PacketTypes.carEngineSound, ClientHandle.CarEngineSoundPacket);
		return r;
	}
}
