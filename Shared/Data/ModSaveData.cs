using System;
using System.Collections.Generic;
using CMS21Together.Shared.Economy;
using CMS21Together.Shared.Data.Vanilla.GarageTool;
using Steamworks.Data;
using UnityEngine.Serialization;

namespace CMS21Together.Shared.Data;

[Serializable]
public class ModSaveData
{
	public string Name;

	/// <summary>Stable id of this save; direct-IP credentials are scoped to it.</summary>
	public string saveId;
	public int saveIndex;
	public Gamemode selectedGamemode = Gamemode.Sandbox;
	public bool alreadyLoaded;
	public List<PlayerInfo> playerInfos = new List<PlayerInfo>();
	public long[] inventoryItemUID;
	public bool storyMissionInProgress;
	public int missionFinished;
	public int money;
	public ModEngineStand additionnalStand;

	/// <summary>Sidecar format version; 0 or 1 = Together hf3. A newer version is never overwritten.</summary>
	public int schemaVersion;

	/// <summary>Increases with every full save; matches the backup bundle of the same save.</summary>
	public long saveGeneration;

	/// <summary>SHA-256 of the vanilla profile this sidecar belongs to (null until the game's write was checked).</summary>
	public string profileSha256;

	public string savedUtc;
	public string plusVersion;

	/// <summary>Last shared-wallet transactions (Phase 3), restored when the session starts again.</summary>
	public List<LedgerRecord> ledgerHistory;
	public ModSaveData(string saveName, int index, bool loaded)
	{
		Name = saveName;
		saveIndex = index;
		selectedGamemode = Gamemode.Sandbox;
		alreadyLoaded = loaded;
		inventoryItemUID = World.UidRanges.Initial(Net.ServerConfig.MaxSupportedPlayers);
		storyMissionInProgress = false;
		missionFinished = 0;
		money = 0;
		additionnalStand = null;
	}

	public ModSaveData()
	{
	}
}