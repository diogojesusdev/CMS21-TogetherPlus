using System.Collections;
using System.Collections.Generic;
using CMS21Together.ClientSide.Data.Garage;
using CMS21Together.ClientSide.Data.Garage.Campaign;
using CMS21Together.ClientSide.Data.Garage.Car;
using CMS21Together.ClientSide.Data.Garage.Tools;
using CMS21Together.ClientSide.Data.Handle;
using CMS21Together.ClientSide.Data.Player;
using CMS21Together.Shared;
using CMS21Together.Shared.Data;
using CMS21Together.Shared.Data.Vanilla;
using CMS21Together.Shared.Data.Vanilla.GarageTool;
using MelonLoader;
using UnityEngine;

namespace CMS21Together.ClientSide.Data;

public class ClientData
{
	public static ClientData Instance;
	public static UserData UserData;

	private static CredentialStore credentials;

	/// <summary>Per-save secrets that prove this player's identity to direct-IP hosts (audit A19).</summary>
	public static CredentialStore Credentials => credentials ??= new CredentialStore(CredentialStore.DefaultPath);
	private bool initRoutine;

	public Dictionary<int, UserData> connectedClients = new();
	public Gamemode gamemode;
	public Dictionary<string, GarageUpgrade> garageUpgrades = new();
	public Dictionary<int, ModCar> loadedCars = new();
	public ModEngineStand engineStand;
	public ModEngineStand engineStand2;
	public GameObject playerPrefab;

	/// <summary>This player joined a game already in progress; cleared once everything was asked for.</summary>
	public bool lateJoin;

	public ClientData()
	{
		initRoutine = false;
		GameData.Instance = null;
		
		Player.Inventory.Reset();
		CarSpawnHooks.Reset();
		JobManager.Reset();
		Wallet.Reset();
		GarageUpgradeHooks.Reset();
		Garage.Tools.ToolsMoveManager.Reset();
		Garage.Tools.CarWashLogic.Reset();
		CarPaintLogic.Reset();
		engineStand = new(null);
		engineStand2 = new(null);
		garageUpgrades = new Dictionary<string, GarageUpgrade>();
	}

	private static float lastCarCheckTime = 0f;
	private static readonly float carCheckInterval = 0.5f; // Check every 0.5 seconds

	public void UpdateClient()
	{
		if (!initRoutine)
			MelonCoroutines.Start(InitializeGameData());

		if (GameLoadHook.IsGameReady())
		{
			Movement.SendPosition();
			Movement.CheckForInactivity();
			Movement.Smooth();
			Rotation.Smooth();
			Rotation.SendRotation();
			
			// Business Logic: Periodically check if local player entered/exited a car
			if (Time.time - lastCarCheckTime >= carCheckInterval)
			{
				lastCarCheckTime = Time.time;
				CarEnterExit.CheckCarState();
			}
		}
	}

	private IEnumerator InitializeGameData()
	{
		initRoutine = true;
		// Phase 8 (assessment): a routine from an earlier connection never applies to the next one.
		while (!GameLoadHook.IsGameReady())
		{
			if (Instance != this) yield break;
			yield return new WaitForSeconds(0.1f);
		}
		if (Instance != this || !Client.Instance.isConnected) yield break;
		
		yield return new WaitForEndOfFrame();
		yield return new WaitForEndOfFrame();
		MelonCoroutines.Start(Stats.SendInitialStats());
		MelonCoroutines.Start(GarageUpgradeHooks.SendInitial());
		
		if (playerPrefab == null)
		{
			LoadPlayerPrefab();
			yield return new WaitForEndOfFrame();
			yield return new WaitForEndOfFrame();
			yield return new WaitForEndOfFrame();
			
			if (playerPrefab == null)
			{
				Log.Error("[ClientData->InitializeGameData] Failed to load playerPrefab after multiple attempts. Retrying...");
				LoadPlayerPrefab();
				yield return new WaitForEndOfFrame();
				yield return new WaitForEndOfFrame();
			}
		}

		yield return new WaitForSeconds(2);
		yield return new WaitForEndOfFrame();
		gamemode = SavesManager.GetGamemodeFromDifficulty(SavesManager.currentSave.Difficulty);
		
		if (playerPrefab != null)
		{
			if (SavesManager.currentSaveIndex != MainMod.MAX_SAVE_COUNT)
				SavesManager.SaveModSave(SavesManager.currentSaveIndex);
			foreach (var client in connectedClients)
			{
				if (client.Value.scene == GameScene.garage)
					client.Value.SpawnPlayer();
			}
		}
		else
		{
			Log.Error("[ClientData->InitializeGameData] Cannot set GameReady: playerPrefab is still null!");
			initRoutine = false;
		}
	}

	public void LoadPlayerPrefab()
	{
		var playerBundle = AssetBundle.LoadFromStream(DataHelper.DeepCopy(DataHelper.LoadContent("CMS21Together.Assets.player.assets")));

		if (playerBundle)
		{
			GameObject player = playerBundle.LoadAsset<GameObject>("playerModel");
			if (player == null)
			{
				Log.Warn("Impossible de charger l'AssetBundle !");
				return;
			}

			var playerInstance = Object.Instantiate(player);

			Material material;
			Texture baseTexture = playerBundle.LoadAsset<Texture>("tex_base");
			if (baseTexture != null)
			{
				baseTexture.filterMode = FilterMode.Bilinear;
			}
			Texture normalTexture = playerBundle.LoadAsset<Texture>("tex_normal");
			if (normalTexture != null)
			{
				normalTexture.filterMode = FilterMode.Bilinear;
			}

			material = new Material(Shader.Find("HDRP/Unlit"));
			if (baseTexture != null)
				material.mainTexture = baseTexture;
			if (normalTexture != null)
				material.SetTexture("_BumpMap", normalTexture);

			var skinnedMesh = playerInstance.GetComponentInChildren<SkinnedMeshRenderer>();
			if (skinnedMesh != null)
			{
				skinnedMesh.material = material;
			}

			playerInstance.transform.localScale = new Vector3(0.095f, 0.095f, 0.095f);
			playerInstance.transform.position = new Vector3(0, -10, 0);
			playerInstance.transform.rotation = new Quaternion(0, 180, 0, 0);

			playerPrefab = playerInstance;
			Object.DontDestroyOnLoad(playerInstance);

			playerBundle.Unload(false);
			Log.Info("[ClientData->LoadPlayerPrefab] Loaded player prefab successfully!");
		}
	}

	public IEnumerator SpawnPlayer(int _money, int _exp, int _level, Vector3 pos, Quaternion rot, int skillPoints, Dictionary<string,
		List<bool>> skills, long startItemUid, int missionFinished, bool missionInProgress)
	{
		// Phase 8 (assessment): spawn data of an earlier connection (a lobby left before the start) is never applied
		// to the next session.
		while (!GameLoadHook.IsGameReady())
		{
			if (Instance != this) yield break;
			yield return new WaitForSeconds(0.1f);
		}
		if (Instance != this || !Client.Instance.isConnected) yield break;

		UIDManager.LastUID = startItemUid;
		Log.Debug("\nReceived Player info! : \n"
		                + $"MissionFinished : {missionFinished}\n"
		                + $"StoryInProgress : {missionInProgress}\n"
		                + $"StartItemUID : {startItemUid}\n"
		                + $"Exp : {_exp}\n"
		                + $"Money : {_money}\n"
		                + $"Level : {_level}\n"
		                + $"Exp : {_exp}\n"
		                + $"SkillPoints : {skillPoints}\n");
		// The host's state for this player: nothing here is a local action to send back (or to charge the shared wallet).
		if (GameManager.Instance.GameDataManager.CurrentProfileData.Difficulty != DifficultyLevel.Sandbox)
		using (ActionGate.Apply())
		{
			GlobalData.PlayerLevel = _level;
			UIManager.Get().StatsContainer.CurrentLevel = _level;
			UIManager.Get().StatsContainer.Refresh(StatType.Level, true);
			GlobalData.PlayerExp = _exp;
			UIManager.Get().StatsContainer.Refresh(StatType.Experience, true);
			// The shared wallet, when it already arrived, is newer than the money in the spawn packet.
			var economy = Client.Instance?.Economy;
			GlobalData.PlayerMoney = economy != null && economy.Synced ? (int)economy.Money : _money;
			UIManager.Get().StatsContainer.Refresh(StatType.Money, true);
			
			Singleton<GameManager>.Instance.UpgradeSystem.availablePoints = skillPoints;
			if (skills != null)
			{
				GameData.Instance.garageTools.upgradeSystem.LockUpgradesForPoints();
				foreach (KeyValuePair<string, List<bool>> skill in skills)
				{
					int lvl = 0;
					foreach (bool unlocked in skill.Value)
					{
						if(unlocked)
							GameData.Instance.garageTools.upgradeSystem.UnlockUpgrade(skill.Key, lvl);
						lvl++;
					}
				}
			}
		}

		GlobalData.MissionsFinished = missionFinished;
		GlobalData.IsStoryMissionInProgress = missionInProgress;
		
		if (pos != Vector3.zero)
			GameData.Instance.localPlayer.transform.position = pos;
		if (rot != Quaternion.identity)
			GameData.Instance.localPlayer.transform.rotation = rot;

		while (!GameLoadHook.IsGameReady())
			yield return new WaitForSeconds(0.1f);
		yield return new WaitForEndOfFrame();
		
		ClientSend.ResyncEngineStandPacket(true);
	}
}