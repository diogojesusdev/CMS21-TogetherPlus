using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using CMS21Together.ClientSide;
using CMS21Together.ClientSide.Data;
using CMS21Together.ClientSide.Data.NewUI;
using CMS21Together.ServerSide;
using CMS21Together.ServerSide.Data;
using CMS21Together.Shared.Data;
using CMS21Together.Shared.Persistence;

namespace CMS21Together.Shared;

/// <summary>
/// Game side of Phase 4 (plan §3.5): the save commit pump, autosave, backups and client save replicas, including the
/// two recovery paths. The pure logic lives in <see cref="CMS21Together.Shared.Persistence"/>. Main thread only.
/// </summary>
public static class PlusSaves
{
	private static readonly Stopwatch Clock = Stopwatch.StartNew();
	private static readonly HashSet<string> Asked = new();
	private static Upload expected;
	private static string lastPumpError;

	public static long NowMs => Clock.ElapsedMilliseconds;

	/// <summary>Host backups: the last 10 generations of each save.</summary>
	public static readonly BundleStore Backups = new(Path.Combine(PlusPaths.Root, "Backups"), 10);

	/// <summary>Copies kept before a restore, an import or a delete.</summary>
	public static readonly BundleStore Safety = new(Path.Combine(PlusPaths.Root, "Backups", "_kept"), 20);

	/// <summary>Copies the host received from players (recovery path 1).</summary>
	public static readonly BundleStore Received = new(Path.Combine(PlusPaths.Root, "Backups", "_received"), 3, 20);

	/// <summary>A player's copies of the sessions they joined: the last 3 per save.</summary>
	public static readonly BundleStore Replicas = new(Path.Combine(PlusPaths.Root, "Replicas"), 3, 20);

	public static readonly SaveCommitter Committer = new(Backups, () => NowMs, kept: Safety);
	public static readonly SaveScheduler Scheduler = new(() => NowMs);

	static PlusSaves()
	{
		Committer.Committed += OnCommitted;
	}

	/// <summary>The host's files were missing or damaged when this session was hosted.</summary>
	public static bool HostDamaged { get; set; }

	/// <summary>Called when the host starts a session.</summary>
	public static void SessionStarted()
	{
		Scheduler.Reset();
		Asked.Clear();
		expected = null;
	}

	public static void Pump()
	{
		Guard("finishing a save", () => Committer.Pump(GameSaveInProgress()));

		var server = Server.Instance;
		if (server != null && server.isRunning)
		{
			Guard("autosave", () =>
			{
				switch (Scheduler.Poll(CanFullSave()))
				{
					case SaveAction.Full:
						TriggerFullSave();
						break;
					case SaveAction.SidecarOnly:
						SavesManager.SaveModSave(SavesManager.currentSaveIndex);
						Scheduler.SidecarSaved();
						break;
				}
			});
			Guard("sending save copies", () =>
			{
				var busy = LocalBusy();
				server.Replicas?.Pump(busy, busy);
			});
		}

		var client = Client.Instance;
		if (client != null && client.isConnected && !client.IsHost)
			Guard("receiving save copies", () =>
			{
				var busy = LocalBusy();
				client.Replicas?.Pump(busy, busy);
			});
	}

	/// <summary>The game can run a full save now: in the garage, loaded, no scene change under way.</summary>
	public static bool CanFullSave() =>
		GameData.Instance != null && !SceneManager.IsLoading && SceneManager.CurrentScene() == GameScene.garage && GameLoadHook.IsGameReady();

	/// <summary>A scene is loading here: no save copies move (plan §3.5).</summary>
	public static bool LocalBusy() =>
		SceneManager.IsLoading || SceneManager.CurrentScene() == GameScene.garage && !GameLoadHook.IsGameReady();

	private static bool GameSaveInProgress()
	{
		try
		{
			return Singleton<GameManager>.Instance?.PlatformManager?.GetPlatformSave()?.IsSaveInProgress ?? false;
		}
		catch (Exception)
		{
			return false;
		}
	}

	private static void TriggerFullSave()
	{
		var loader = GarageLoader.Get();
		if (loader == null) return;
		Log.Info("[Saves] Autosave.");
		if (!loader.Save(false)) Log.Debug("[Saves] The game did not start the save (busy); trying again soon.");
	}

	private static void Guard(string what, Action action)
	{
		try
		{
			action();
		}
		catch (Exception ex)
		{
			// One report per distinct failure: this runs every frame.
			var key = what + ex.GetType().Name + ex.Message;
			if (key == lastPumpError) return;
			lastPumpError = key;
			Log.Error($"[Saves] Failed while {what}", ex);
		}
	}

	// ---------------------------------------------------------------- host: replicas and recovery path 1

	private static void OnCommitted(CommittedSave save)
	{
		var server = Server.Instance;
		var data = ServerData.Instance;
		if (server == null || !server.isRunning || server.Replicas == null || data == null) return;
		if (!SavesManager.ModSaves.TryGetValue(SavesManager.currentSaveIndex, out var current) || current?.saveId != save.Meta.SaveId) return;
		// Played from damaged files: players keep their good copies instead of getting these.
		if (HostDamaged) return;
		foreach (var slot in data.connectedClients.Keys)
			if (slot != 1)
				server.Replicas.Send(slot, save.Bundle);
	}

	/// <summary>A player passed validation: they get the newest backup unless they already have it.</summary>
	public static void PlayerJoined(int slot)
	{
		var server = Server.Instance;
		if (slot == 1 || server?.Replicas == null || HostDamaged) return;
		Guard("sending the newest save copy", () =>
		{
			if (!SavesManager.ModSaves.TryGetValue(SavesManager.currentSaveIndex, out var save) || !SaveIds.IsValid(save?.saveId)) return;
			if (Backups.NewestValid(save.saveId, out _, out var bytes) != null) server.Replicas.Send(slot, bytes);
		});
	}

	public static void PlayerLeft(int slot)
	{
		Server.Instance?.Replicas?.PeerLeft(slot);
		if (expected != null && expected.Slot == slot) expected = null;
	}

	/// <summary>A joining player has a copy of this save (recovery path 1).</summary>
	public static void HandleOffer(int slot, Packet packet)
	{
		var offer = ReplicaWire.ReadOffer(packet);
		if (!SavesManager.ModSaves.TryGetValue(SavesManager.currentSaveIndex, out var save) || save == null) return;
		var key = $"{offer.SaveId}/{offer.Generation}/{offer.BundleSha256}";
		if (RecoveryRules.Evaluate(save.saveId, save.saveGeneration, HostDamaged, offer, Asked.Contains(key)) != RecoveryDecision.Prompt) return;
		Asked.Add(key);

		var who = PlayerName(slot);
		var when = new DateTime(Math.Max(0, Math.Min(offer.CreatedUtcTicks, DateTime.MaxValue.Ticks)), DateTimeKind.Utc).ToLocalTime();
		Log.Info($"[Saves] {who} has generation {offer.Generation} of this save (yours: {save.saveGeneration}{(HostDamaged ? ", damaged" : "")}).");
		if (InLobby)
		{
			var reason = HostDamaged
				? "Your copy of this save is missing or damaged."
				: $"Their copy is newer (generation {offer.Generation}, yours is {save.saveGeneration}).";
			UICustomPanel.CreateChoicePanel("Restore save?",
				$"{who} has a copy of this save from {when:g}. {reason} Restore it? Your current files are kept as a backup first, and players will rejoin.",
				("Restore", () => RequestUpload(slot, offer, true)),
				("Ignore", null));
		}
		else
		{
			// In game nothing is restored: the copy is only stored with the backups.
			RequestUpload(slot, offer, false);
			Toast($"Storing a newer copy of this save from {who} with your backups.");
		}
	}

	private static void RequestUpload(int slot, ReplicaOffer offer, bool restore)
	{
		if (Server.Instance?.IsConnected(slot) != true)
		{
			UICustomPanel.CreateInfoPanel("That player has left; the copy could not be fetched.");
			return;
		}
		expected = new Upload(slot, offer, restore);
		using var packet = new Packet((int)PacketTypes.replicaRequest);
		ReplicaWire.WriteRequest(packet, offer.SaveId, offer.Generation, offer.BundleSha256);
		Server.Instance.Send(slot, packet, true);
	}

	/// <summary>Only the copy the host asked for is accepted.</summary>
	public static ReplicaStatus AcceptUpload(int slot, ReplicaBegin begin)
	{
		var e = expected;
		if (e == null || e.Slot != slot || begin.SaveId != e.Offer.SaveId || begin.Generation != e.Offer.Generation ||
		    begin.BundleSha256 != e.Offer.BundleSha256)
		{
			Log.Warn($"[Saves] Refused a save upload from #{slot} that was not asked for.");
			return ReplicaStatus.Refused;
		}
		return ReplicaStatus.Continue;
	}

	/// <summary>
	/// The host is in the lobby: not started, not loading (Phase 8 assessment: GameData is also empty during the whole
	/// garage load, so a restore could run mid-load and mix two saves).
	/// </summary>
	private static bool InLobby =>
		Server.Instance?.isRunning == true && !Server.Instance.GameStarted && !SceneManager.IsLoading && GameData.Instance == null;

	public static void OnUpload(int slot, SaveBundle bundle, byte[] bytes)
	{
		var e = expected;
		if (e == null || e.Slot != slot) return;
		expected = null;
		// Only the copy that was offered and asked for (not one of an earlier request).
		if (bundle.Meta.Generation != e.Offer.Generation || !string.Equals(Sha256Hex.Of(bytes), e.Offer.BundleSha256, StringComparison.OrdinalIgnoreCase))
		{
			Log.Warn($"[Saves] The copy from #{slot} is not the one asked for; ignored.");
			return;
		}
		Received.Put(bytes);
		var who = PlayerName(slot);
		if (!e.Restore || !InLobby)
		{
			Toast($"Stored generation {bundle.Meta.Generation} of this save from {who} with your backups.");
			return;
		}
		if (SavesManager.RestoreInLobby(bundle, out var error))
			UICustomPanel.CreateInfoPanel($"Restored generation {bundle.Meta.Generation} from {who}. Players were asked to join again.");
		else
			UICustomPanel.CreateInfoPanel("The restore failed: " + error);
	}

	// ---------------------------------------------------------------- player: replicas

	/// <summary>After validation: tell the host which copy of its save this player has.</summary>
	public static void SendOffer(string saveId)
	{
		var client = Client.Instance;
		if (client == null || client.IsHost || !SaveIds.IsValid(saveId)) return;
		Guard("offering a save copy", () =>
		{
			var bundle = Replicas.NewestValid(saveId, out _, out var bytes);
			if (bundle == null) return;
			using var packet = new Packet((int)PacketTypes.replicaOffer);
			ReplicaWire.WriteOffer(packet, new ReplicaOffer
			{
				SaveId = saveId,
				Generation = bundle.Meta.Generation,
				BundleSha256 = Sha256Hex.Of(bytes),
				CreatedUtcTicks = bundle.Meta.CreatedUtc.Ticks
			});
			client.SendData(packet, true);
		});
	}

	public static ReplicaStatus AcceptReplica(int peer, ReplicaBegin begin)
	{
		var hostSave = Client.Instance?.HostSaveId;
		if (peer != 0 || hostSave == null || begin.SaveId != hostSave) return ReplicaStatus.Refused;
		// Same generation is not enough: after a restore the host may write a different save under a known number.
		return Replicas.Holds(begin.SaveId, begin.Generation, begin.Length, begin.BundleSha256) ? ReplicaStatus.AlreadyHave : ReplicaStatus.Continue;
	}

	public static void OnReplica(int peer, SaveBundle bundle, byte[] bytes)
	{
		var name = bundle.Meta.ToString();
		Task.Run(() =>
		{
			try
			{
				Replicas.Put(bytes);
				Log.Info($"[Saves] Kept a copy of the host's save: {name}.");
			}
			catch (Exception ex)
			{
				Log.Error("[Saves] Storing the host's save copy failed", ex);
			}
		});
	}

	/// <summary>The host asks for this player's copy (it accepted the restore prompt).</summary>
	public static void HandleRequest(Packet packet)
	{
		var (saveId, generation, sha) = ReplicaWire.ReadRequest(packet);
		var client = Client.Instance;
		if (client?.Replicas == null || saveId != client.HostSaveId) return;
		foreach (var info in Replicas.List(saveId))
		{
			if (info.Meta.Generation != generation || !BundleStore.TryLoad(info.Path, out _, out var bytes, out _)) continue;
			if (Sha256Hex.Of(bytes) != sha) continue;
			Log.Info($"[Saves] Sending generation {generation} of the save to the host.");
			client.Replicas.Send(0, bytes);
			return;
		}
		Log.Warn($"[Saves] The host asked for generation {generation}, which this player no longer has.");
	}

	// ---------------------------------------------------------------- helpers

	private static string PlayerName(int slot) =>
		ServerData.Instance != null && ServerData.Instance.connectedClients.TryGetValue(slot, out var user) && !string.IsNullOrEmpty(user.username)
			? user.username
			: $"Player #{slot}";

	public static void Toast(string message)
	{
		Log.Info("[Saves] " + message);
		try
		{
			UIManager.Get()?.ShowPopup("Together+", message, PopupType.Normal);
		}
		catch (Exception ex)
		{
			Log.Debug($"[Saves] Could not show the message: {ex.Message}");
		}
	}

	private sealed class Upload
	{
		public Upload(int slot, ReplicaOffer offer, bool restore)
		{
			Slot = slot;
			Offer = offer;
			Restore = restore;
		}

		public int Slot { get; }
		public ReplicaOffer Offer { get; }
		public bool Restore { get; }
	}
}