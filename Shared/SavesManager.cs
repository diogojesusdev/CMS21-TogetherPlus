using System;
using CMS21Together.Shared.World;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CMS.ContainersSave;
using CMS21Together.ClientSide;
using CMS21Together.ClientSide.Data;
using CMS21Together.ServerSide;
using CMS21Together.ServerSide.Data;
using CMS21Together.Shared.Data;
using CMS21Together.Shared.Data.Vanilla.Cars;
using CMS21Together.Shared.Economy;
using CMS21Together.Shared.Persistence;
using HarmonyLib;
using MelonLoader;
using Newtonsoft.Json;
using UnhollowerBaseLib;
using UnityEngine;
using BinaryWriter = Il2CppSystem.IO.BinaryWriter;

namespace CMS21Together.Shared;

[HarmonyPatch]
public static class SavesManager
{
	/// <summary>Where hf3 kept the mod saves (inside the game folder, audit A20). Only read, for the one-time move.</summary>
	private const string LEGACY_SAVE_FOLDER_PATH = @"Mods\togetherMod\saves";

	/// <summary>First and last host save slots (0-3 are vanilla, <see cref="MainMod.MAX_SAVE_COUNT"/> is a player's session).</summary>
	private const int FirstModSlot = 4;

	public static Dictionary<int, ModSaveData> ModSaves = new();
	public static Il2CppReferenceArray<ProfileData> profileData = new(MainMod.MAX_SAVE_COUNT + 1);
	public static ProfileData currentSave;
	public static int currentSaveIndex;

	/// <summary>Slots whose sidecar could not be read: never written, so the file is not lost.</summary>
	private static readonly HashSet<int> UnreadableSlots = new();

	/// <summary>Mod saves live next to the vanilla saves (audit A20).</summary>
	public static string SaveFolder => Path.Combine(PlusPaths.Root, "Saves");

	public static string SidecarPath(int index) => Path.Combine(SaveFolder, $"save_{index}.cms21mp");

	public static string ProfilePath(int index, byte format) =>
		string.Format("{0}/profile{1}{2}", GlobalStrings.SaveDirectory, index, format == 1 ? ".cms21b" : ".cms21");

	/// <summary>Players in someone else's session never write saves; only the host does.</summary>
	private static bool IsClientOnly => Client.Instance != null && Client.Instance.isConnected && !Client.Instance.IsHost;

	public static bool IsUnreadable(int index) => UnreadableSlots.Contains(index);

	public static void Initialize()
	{
		for (var i = 0; i < 4; i++) Il2CppArrays.Set(profileData, i, Singleton<GameManager>.Instance.GameDataManager.ProfileData[i]);

		MigrateLegacySaves();
		LoadExistingModSaves();

		for (var i = 0; i < MainMod.MAX_SAVE_COUNT + 1; i++)
			if (!ModSaves.ContainsKey(i))
				ModSaves.Add(i, new ModSaveData("EmptySave", i, false)); // Add empty save to ModSaves 

		Singleton<GameManager>.Instance.GameDataManager.ProfileData = profileData; // Set new array size
	}

	private static void MigrateLegacySaves()
	{
		try
		{
			var legacy = Path.GetFullPath(LEGACY_SAVE_FOLDER_PATH);
			var result = SidecarMigration.Run(legacy, SaveFolder);
			if (result.Outcome == MigrationOutcome.Migrated)
				Log.Info($"[Saves] Moved {result.Copied.Count} mod saves to {SaveFolder} ({result.Skipped.Count} already there). The originals stay in {legacy}.");
			else if (result.Outcome == MigrationOutcome.Failed)
				Log.Error($"[Saves] Moving the mod saves from {legacy} failed; trying again next time: {string.Join("; ", result.Errors)}");
		}
		catch (Exception ex)
		{
			Log.Error("[Saves] Moving the mod saves failed", ex);
		}
	}

	private static void LoadExistingModSaves()
	{
		if (Directory.Exists(SaveFolder))
		{
			var saveFiles = Directory.GetFiles(SaveFolder, "save_*.cms21mp").ToList();
			// A save whose file is gone but whose previous copy survived (older Together+ writes could leave that).
			foreach (var copy in Directory.GetFiles(SaveFolder, "save_*.cms21mp.bak"))
			{
				var main = copy.Substring(0, copy.Length - ".bak".Length);
				if (!File.Exists(main)) saveFiles.Add(main);
			}

			var vanillaSaveArray = new Il2CppReferenceArray<SaveData>(4);
			for (var i = 0; i < 4; i++) Il2CppArrays.Set(vanillaSaveArray, i, GetSave(i));

			foreach (var saveFile in saveFiles)
			{
				var name = Path.GetFileNameWithoutExtension(saveFile);
				if (!int.TryParse(name.Substring("save_".Length), out var index) || index < FirstModSlot || index >= MainMod.MAX_SAVE_COUNT)
				{
					Log.Debug($"[Saves] Ignoring {saveFile} (not a host save slot).");
					continue;
				}
				var modSave = ReadSidecar(saveFile, index);
				if (modSave == null) continue;
				UnreadableSlots.Remove(index);
				if (modSave.saveIndex != index)
				{
					// The file name decides the slot (audit A21): a stale index inside must not overwrite another save.
					Log.Warn($"[Saves] {saveFile} says slot {modSave.saveIndex}; using slot {index}.");
					modSave.saveIndex = index;
				}

				ModSaves[index] = modSave;
				if (modSave.alreadyLoaded)
				{
					// Native calls: the last line before a crash names the slot being rebuilt.
					Log.Info($"[Saves] Rebuilding the profile of save slot {modSave.saveIndex} for the menu.");
					var tempSaveArray = new Il2CppReferenceArray<SaveData>(4);
					Il2CppArrays.Set(tempSaveArray, 3, GetSave(modSave.saveIndex));

					Singleton<GameManager>.Instance.GameDataManager.ReloadProfiles(tempSaveArray);
					var copiedData = DataHelper.Copy(Singleton<GameManager>.Instance.GameDataManager.ProfileData[3]);

					Il2CppArrays.Set(profileData, modSave.saveIndex, copiedData);
					Log.Info($"[Saves] Save slot {modSave.saveIndex} is ready for the menu.");
				}
			}

			Singleton<GameManager>.Instance.GameDataManager.ReloadProfiles(vanillaSaveArray);
		}
	}

	/// <summary>Reads a sidecar, falling back to the copy kept by the last write. Null when neither can be read.</summary>
	private static ModSaveData ReadSidecar(string path, int index)
	{
		var damaged = false;
		foreach (var candidate in new[] { path, path + ".bak" })
		{
			if (!File.Exists(candidate)) continue;
			try
			{
				var text = File.ReadAllText(candidate, Encoding.UTF8);
				var save = JsonConvert.DeserializeObject<ModSaveData>(text);
				if (save == null) throw new JsonException("Empty file.");
				if (candidate != path)
				{
					Log.Warn($"[Saves] {path} could not be read; using the copy from the previous save.");
					RecoverFrom(path, text);
				}
				return save;
			}
			catch (Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException)
			{
				damaged |= ex is JsonException;
				Log.Error($"[Saves] Could not read {candidate}: {ex.Message}");
			}
		}
		UnreadableSlots.Add(index);
		Log.Error(damaged
			? $"[Saves] The mod save of slot {index} is damaged. It is left untouched; restore it from a backup."
			: $"[Saves] The mod save of slot {index} could not be read. It is left untouched.");
		return null;
	}

	/// <summary>Puts a readable copy back in place; a damaged file is kept aside, never deleted.</summary>
	private static void RecoverFrom(string path, string text)
	{
		try
		{
			if (File.Exists(path)) File.Move(path, $"{path}.damaged-{DateTime.UtcNow:yyyyMMddHHmmss}");
			AtomicFile.WriteAllText(path, text);
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
		{
			Log.Warn($"[Saves] Could not put {path} back: {ex.Message}");
		}
	}

	private static SaveData GetSave(int saveIndex)
	{
		Il2CppStructArray<byte> bytes = LoadProfileSave(saveIndex, out var format, out var parameter);

		var saveData = new SaveData();
		saveData.Data = bytes;
		saveData.Format = format;
		saveData.HasData = parameter;

		return saveData;
	}

	private static byte[] LoadProfileSave(int profileIndex, out byte format, out bool hasData)
	{
		var path = string.Format("{0}/profile{1}{2}b", GlobalStrings.SaveDirectory, profileIndex, ".cms21");
		if (File.Exists(path))
		{
			format = 1;
			hasData = true;
			return File.ReadAllBytes(path);
		}

		path = string.Format("{0}/profile{1}{2}", GlobalStrings.SaveDirectory, profileIndex, ".cms21");
		if (File.Exists(path))
		{
			format = 0;
			hasData = true;
			return File.ReadAllBytes(path);
		}

		format = 0;
		hasData = false;
		return Array.Empty<byte>();
	}


	public static void LoadSave(ModSaveData saveData, Dictionary<int, ModNewCarData> carOnPark=null, bool clientSave = false)
	{
		var gameManager = Singleton<GameManager>.Instance;
		int index;
		string name;

		if (clientSave)
		{
			index = MainMod.MAX_SAVE_COUNT;
			name = "ClientSave";
		}
		else
		{
			index = saveData.saveIndex;
			name = saveData.Name;
		}

		currentSaveIndex = index;
		if (!clientSave && string.IsNullOrEmpty(saveData.saveId)) saveData.saveId = Guid.NewGuid().ToString("N");
		var level = GetDifficultyFromGamemode(saveData.selectedGamemode);

		gameManager.ProfileManager.selectedProfile = index;
		gameManager.RDGPlayerPrefs.SetInt("selectedProfile", index);

		Log.Info("-------------------Load Save---------------------");
		Log.Info("Index : " + index);
		Log.Info("Name : " + name);
		if (!clientSave)
		{
			Log.Info("Already Loaded : " + saveData.alreadyLoaded);
			if (!saveData.alreadyLoaded) Log.Info("-------------------------------------------------");

			if (saveData.alreadyLoaded)
			{
				gameManager.ProfileManager.selectedProfile = index;
				gameManager.RDGPlayerPrefs.SetInt("selectedProfile", index);
				Singleton<GameManager>.Instance.ProfileManager.SetDifficultyForCurrentProfile(level); // ensure gamemode is not loss
				gameManager.ProfileManager.Load();

				Log.Info("-------------------Save Info---------------------");
				Log.Info("Selected Profile Name : " + gameManager.ProfileManager.GetSelectedProfileName());
				Log.Info("Selected Profile Difficulty : " + gameManager.ProfileManager.GetSelectedProfileDifficulty());
				Log.Info("Selected Profile : " + gameManager.ProfileManager.selectedProfile);
				Log.Info("-------------------------------------------------");
				currentSave = gameManager.ProfileManager.GetSelectedProfileData();
				SaveModSave(index);
				return;
			}

			var writer = new BinaryWriter();
			var save = new ProfileData();

			save.Init();
			save.WriteSaveHeader(writer);
			save.WriteSaveVersion(writer);

			Il2CppArrays.Set(profileData, index, save);
			Singleton<GameManager>.Instance.ProfileManager.SetNameForCurrentProfile(name);
			Singleton<GameManager>.Instance.ProfileManager.SetDifficultyForCurrentProfile(level);
			Singleton<GameManager>.Instance.ProfileManager.Load();

			ModSaves[index].Name = name;
			ModSaves[index].saveIndex = index;
		}
		else
		{
			var writer = new BinaryWriter();
			var save = new ProfileData();

			save.Init();
			save.WriteSaveHeader(writer);
			save.WriteSaveVersion(writer);

			Il2CppArrays.Set(profileData, index, save);
			gameManager.ProfileManager.selectedProfile = index;
			gameManager.RDGPlayerPrefs.SetInt("selectedProfile", index);
			Singleton<GameManager>.Instance.ProfileManager.SetNameForCurrentProfile(name);
			Singleton<GameManager>.Instance.ProfileManager.SetDifficultyForCurrentProfile(level);
			gameManager.ProfileManager.Load();
			
			currentSave = gameManager.ProfileManager.GetSelectedProfileData();
			if (carOnPark != null)
			{
				// Keyed by parking slot; an empty slot has no key (a late join with a gap used to throw here).
				var parking = currentSave.carsOnParking;
				var placed = ParkSlots.Place(carOnPark, parking.Length, (slot, car) => Il2CppArrays.Set(parking, slot, car.ToGame()));
				if (placed != carOnPark.Count) Log.Warn($"[SavesManager] {carOnPark.Count - placed} parked cars had no valid slot.");
			}

			Log.Info("-------------------Save Info---------------------");
			Log.Info("Selected Profile Name : " + gameManager.ProfileManager.GetSelectedProfileName());
			Log.Info("Selected Profile Difficulty : " + gameManager.ProfileManager.GetSelectedProfileDifficulty());
			Log.Info("Selected Profile : " + gameManager.ProfileManager.selectedProfile);
			Log.Info("-------------------------------------------------");
		}
		currentSave = gameManager.ProfileManager.GetSelectedProfileData();

		if (!clientSave) SaveModSave(index);
		if (clientSave) StartGame(MainMod.MAX_SAVE_COUNT);
	}

	private static DifficultyLevel GetDifficultyFromGamemode(Gamemode saveDataSelectedGamemode)
	{
		if (saveDataSelectedGamemode == Gamemode.Normal)
			return DifficultyLevel.Normal;
		if  (saveDataSelectedGamemode == Gamemode.Easy)
			return DifficultyLevel.Easy;
		if  (saveDataSelectedGamemode == Gamemode.Expert)
			return DifficultyLevel.Expert;
		return DifficultyLevel.Sandbox;
	}


	/// <summary>
	/// Writes only the mod sidecar (players' progress, session values), keeping the current generation. Full saves go
	/// through the game's save and <see cref="BeginSave"/>. Players in someone else's session never write.
	/// </summary>
	public static void SaveModSave(int saveIndex)
	{
		if (IsClientOnly) return;
		if (!ModSaves.TryGetValue(saveIndex, out var save) || save == null)
		{
			Log.Warn($"[SavesManager] No mod save in slot {saveIndex}; nothing to write.");
			return;
		}
		try
		{
			WriteSidecar(saveIndex, save);
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
		{
			Log.Error($"[Saves] Writing the mod save of slot {saveIndex} failed", ex);
		}
	}

	/// <summary>Step 1 of a full save (the game's save hook, or the session ending in game): see <see cref="SaveCommitter"/>.</summary>
	public static void BeginSave(int saveIndex)
	{
		if (IsClientOnly) return;
		if (!ModSaves.TryGetValue(saveIndex, out var save) || save == null) return;
		if (!SaveIds.IsValid(save.saveId))
		{
			SaveModSave(saveIndex);
			return;
		}
		if (PlusSaves.Committer.Begin(new GameSlot(saveIndex, save.saveId)) > 0) PlusSaves.Scheduler.FullSaveDone();
	}

	/// <summary>Captures the hosted session into the sidecar and writes it atomically. Returns the bytes, or null when refused.</summary>
	private static byte[] WriteSidecar(int saveIndex, ModSaveData save)
	{
		if (save.schemaVersion > SlotValidator.CurrentSchema)
		{
			Log.Error($"[Saves] Slot {saveIndex} was written by a newer Together+; it is not overwritten.");
			return null;
		}
		if (UnreadableSlots.Contains(saveIndex))
		{
			Log.Error($"[Saves] Slot {saveIndex} could not be read earlier; it is not overwritten.");
			return null;
		}
		if (saveIndex < FirstModSlot || saveIndex >= MainMod.MAX_SAVE_COUNT)
		{
			Log.Warn($"[Saves] Slot {saveIndex} is not a host save; not written.");
			return null;
		}

		var hosting = Server.Instance != null && Server.Instance.isRunning && ServerData.Instance != null;
		if (SidecarRules.AppliesLiveStats(hosting, saveIndex, currentSaveIndex))
		{
			if (ServerData.Instance.engineStand2 != null && ServerData.Instance.engineStand2.engineGroupItem != null)
				save.additionnalStand = ServerData.Instance.engineStand2;
			SidecarRules.ApplyPlayerStats(ServerData.Instance.connectedClients.Values, save.playerInfos,
				u => u.playerGUID, i => i.id, CopyStats);
			CaptureSession(save);
		}

		save.saveIndex = saveIndex;
		save.schemaVersion = SlotValidator.CurrentSchema;
		save.plusVersion = MainMod.PLUS_VERSION;
		save.savedUtc = DateTime.UtcNow.ToString("o");
		var bytes = new UTF8Encoding(false).GetBytes(JsonConvert.SerializeObject(save));
		var path = SidecarPath(saveIndex);
		AtomicFile.WriteAllBytes(path, bytes, path + ".bak");
		Log.Debug($"[Saves] Wrote the mod save of slot {saveIndex} (generation {save.saveGeneration}).");
		return bytes;
	}

	/// <summary>Session values that only exist while the game is loaded (hf3 kept them only when the host closed).</summary>
	private static void CaptureSession(ModSaveData save)
	{
		if (GameData.Instance == null) return;
		save.missionFinished = GlobalData.MissionsFinished;
		save.storyMissionInProgress = GlobalData.IsStoryMissionInProgress;
		var economy = Server.Instance?.Economy;
		if (economy != null && economy.Ledger.Initialized)
		{
			save.money = (int)economy.Ledger.Money;
			save.ledgerHistory = economy.Ledger.History.Select(LedgerRecord.From).ToList();
		}
		else
			save.money = GlobalData.PlayerMoney;
	}

	/// <summary>The committer's view of a slot; looks the save up again on every call (the menu reloads them).</summary>
	private sealed class GameSlot : ISaveSlot
	{
		private readonly int index;

		public GameSlot(int index, string saveId)
		{
			this.index = index;
			SaveId = saveId;
		}

		private ModSaveData Save => ModSaves.TryGetValue(index, out var save) && save != null && save.saveId == SaveId ? save : null;

		public string SaveId { get; }
		public string Name => Save?.Name;
		public long Generation => Save?.saveGeneration ?? 0;

		public byte[] WriteSidecar(long generation, string profileSha256)
		{
			var save = Save;
			if (save == null) return null;
			save.saveGeneration = generation;
			save.profileSha256 = profileSha256;
			return SavesManager.WriteSidecar(index, save);
		}

		public byte[] ReadProfile(out byte format)
		{
			var bytes = LoadProfileSave(index, out format, out var hasData);
			return hasData ? bytes : null;
		}

		public (DateTime writtenUtc, long length)? ProfileFileState
		{
			get
			{
				LoadedProfilePath(index, out var path);
				if (path == null) return null;
				var info = new FileInfo(path);
				return info.Exists ? (info.LastWriteTimeUtc, info.Length) : null;
			}
		}
	}

	// ---------------------------------------------------------------- load validation, restore, import

	/// <summary>Checks a slot's files before hosting it (plan §3.5, load validation).</summary>
	public static SlotCheck CheckSlot(int index)
	{
		var save = ModSaves[index];
		var bytes = LoadProfileSave(index, out var format, out var hasData);
		BundleMeta newest = null;
		if (SaveIds.IsValid(save.saveId))
		{
			var list = PlusSaves.Backups.List(save.saveId);
			if (list.Count > 0) newest = list[0].Meta;
		}
		return SlotValidator.Check(new SlotInput
		{
			AlreadyLoaded = save.alreadyLoaded,
			Schema = save.schemaVersion,
			Generation = save.saveGeneration,
			ProfileSha256 = save.profileSha256,
			Profile = hasData ? bytes : null,
			Readable = b => IsProfileReadable(b, format),
			NewestBackup = newest
		});
	}

	/// <summary>
	/// A slot that is fine to host: stamps a missing hash, gives an hf3 save its first generation, and makes sure the
	/// files as they are now have a backup (a copy is taken before every session).
	/// </summary>
	public static void PrepareForHosting(int index, SlotCheck check)
	{
		var save = ModSaves[index];
		if (!save.alreadyLoaded || check.ProfileSha256 == null) return;
		ReloadProfile(index);
		if (!SaveIds.IsValid(save.saveId)) save.saveId = SaveIds.New();
		SessionStart.Prepare(new GameSlot(index, save.saveId), check, PlusSaves.Backups, save.Name);
	}
	/// <summary>
	/// The session loads the checked file, not the copy read when the menu opened (the game may still have been
	/// writing it then, right after leaving a session).
	/// </summary>
	public static void ReloadProfile(int index)
	{
		if (!ModSaves.TryGetValue(index, out var save) || save == null || !save.alreadyLoaded) return;
		var parsed = ParseProfile(GetSave(index));
		if (parsed != null) Il2CppArrays.Set(profileData, index, parsed);
		else Log.Warn($"[Saves] Slot {index} could not be read again; using the copy from the menu.");
	}

	/// <summary>Keeps the files as they are (missing or damaged ones included) before hosting them anyway.</summary>
	public static void KeepCurrentFiles(int index, string note)
	{
		var save = ModSaves[index];
		if (!SaveIds.IsValid(save.saveId)) save.saveId = SaveIds.New();
		LoadedProfilePath(index, out var profilePath);
		profilePath ??= ProfilePath(index, 1);
		SaveRestore.KeepCopy(save.saveId, save.Name, profilePath, SidecarPath(index), PlusSaves.Safety,
			save.saveGeneration, note, SaveRestore.FormatOf(profilePath));
	}

	/// <summary>Restores a bundle of the save in a slot (host menu or lobby). The current files are kept first.</summary>
	public static bool Restore(int index, SaveBundle bundle, out string error)
	{
		error = null;
		var save = ModSaves.TryGetValue(index, out var s) ? s : null;
		if (save == null || save.saveId != bundle.Meta.SaveId)
		{
			error = "That backup belongs to another save.";
			return false;
		}
		return WriteBundle(index, bundle, save.saveGeneration, out error);
	}

	/// <summary>Recovery path 2: puts a backup or a player's copy into an empty slot, so it can be hosted.</summary>
	public static bool Import(int index, SaveBundle bundle, out string error)
	{
		error = null;
		if (ModSaves.TryGetValue(index, out var current) && current != null && current.alreadyLoaded)
		{
			error = "This slot is not empty.";
			return false;
		}
		foreach (var other in ModSaves)
			if (other.Key != index && other.Value?.saveId == bundle.Meta.SaveId && other.Value.alreadyLoaded)
			{
				error = $"This save is already in slot {other.Key - FirstModSlot + 1}. Restore it from there instead.";
				return false;
			}
		return WriteBundle(index, bundle, 0, out error);
	}

	/// <summary>Recovery path 1, in the lobby: restores a player's copy of the hosted save and reloads it.</summary>
	public static bool RestoreInLobby(SaveBundle bundle, out string error)
	{
		var index = currentSaveIndex;
		if (!Restore(index, bundle, out error)) return false;
		PlusSaves.HostDamaged = false;

		// Everyone's live progress came from the replaced save. It is reset to the restored values first, so nothing
		// written on the way out (leave capture, sidecar writes) puts the old progress back; then the players rejoin.
		var data = ServerData.Instance;
		if (data != null)
		{
			var restored = ModSaves[index].playerInfos ?? new List<PlayerInfo>();
			foreach (var slot in data.connectedClients.Keys.ToList())
			{
				var user = data.connectedClients[slot];
				var info = restored.FirstOrDefault(p => p.id == user.playerGUID);
				if (info != null) data.SetPlayerInfo(slot, info);
				else if (slot != 1) data.connectedClients.Remove(slot);
				if (slot != 1) Server.Instance.Kick(slot, "The host restored a newer copy of the save. Please join again.");
			}
		}

		LoadSave(ModSaves[index]);
		return true;
	}

	private static bool WriteBundle(int index, SaveBundle bundle, long currentGeneration, out string error)
	{
		error = null;
		if (index < FirstModSlot || index >= MainMod.MAX_SAVE_COUNT)
		{
			error = "Not a host save slot.";
			return false;
		}
		ModSaveData restored;
		byte[] sidecar;
		try
		{
			restored = JsonConvert.DeserializeObject<ModSaveData>(Encoding.UTF8.GetString(bundle.Sidecar));
			if (restored == null || restored.saveId != bundle.Meta.SaveId) throw new JsonException("It is not the sidecar of this save.");
			if (restored.schemaVersion > SlotValidator.CurrentSchema) throw new JsonException("It was written by a newer Together+.");
			sidecar = bundle.Sidecar;
			if (restored.saveIndex != index)
			{
				restored.saveIndex = index;
				sidecar = new UTF8Encoding(false).GetBytes(JsonConvert.SerializeObject(restored));
			}
		}
		catch (JsonException ex)
		{
			error = "The backup's mod save cannot be read: " + ex.Message;
			return false;
		}

		var format = bundle.Meta.ProfileFormat;
		var target = ProfilePath(index, format);
		LoadedProfilePath(index, out var currentProfile);
		try
		{
			SaveRestore.Apply(bundle, target, SidecarPath(index), PlusSaves.Safety, currentGeneration, sidecar, currentProfile);
			// The other format would win on load: it was kept with the copy above.
			var other = ProfilePath(index, (byte)(format == 1 ? 0 : 1));
			if (format == 0 && File.Exists(other)) File.Delete(other);
			if (SaveIds.IsValid(bundle.Meta.SaveId))
				PlusSaves.Backups.MoveNewerThan(bundle.Meta.SaveId, bundle.Meta.Generation, PlusSaves.Safety);
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
		{
			error = ex.Message;
			Log.Error("[Saves] Restoring a backup failed", ex);
			return false;
		}

		UnreadableSlots.Remove(index);
		ModSaves[index] = restored;
		var parsed = ParseProfile(GetSave(index));
		if (parsed != null) Il2CppArrays.Set(profileData, index, parsed);
		else Log.Warn($"[Saves] The restored profile of slot {index} could not be read back.");
		Log.Info($"[Saves] Slot {index} now holds {bundle.Meta}.");
		return true;
	}

	/// <summary>Whether the game can read a profile (only used when the stored hash does not match).</summary>
	private static bool IsProfileReadable(byte[] bytes, byte format)
	{
		var parsed = ParseProfile(new SaveData { Data = bytes, Format = format, HasData = true });
		return parsed != null && !string.IsNullOrEmpty(parsed.Name);
	}

	/// <summary>Reads a profile with the game's own loader, then puts the profile list back as it was.</summary>
	private static ProfileData ParseProfile(SaveData data)
	{
		var manager = Singleton<GameManager>.Instance.GameDataManager;
		try
		{
			var temp = new Il2CppReferenceArray<SaveData>(4);
			Il2CppArrays.Set(temp, 3, data);
			manager.ReloadProfiles(temp);
			return DataHelper.Copy(manager.ProfileData[3]);
		}
		catch (Exception ex)
		{
			Log.Warn($"[Saves] The game could not read a profile: {ex.Message}");
			return null;
		}
		finally
		{
			try
			{
				var vanilla = new Il2CppReferenceArray<SaveData>(4);
				for (var i = 0; i < 4; i++) Il2CppArrays.Set(vanilla, i, GetSave(i));
				manager.ReloadProfiles(vanilla);
				var reloaded = manager.ProfileData;
				for (var i = 0; i < 4; i++) Il2CppArrays.Set(profileData, i, reloaded[i]);
				manager.ProfileData = profileData;
			}
			catch (Exception ex)
			{
				Log.Error("[Saves] Restoring the profile list failed", ex);
			}
		}
	}

	/// <summary>
	/// Before a new game in a slot that has no mod file but still has a game profile (e.g. an hf3 save whose sidecar was
	/// lost when the game folder was verified): a copy is kept, listed under "Restore a backup" (Phase 8 assessment:
	/// the new game overwrote it with no copy). False when the copy could not be made; then nothing may be written.
	/// </summary>
	public static bool KeepOrphanProfile(int index)
	{
		if (ModSaves.TryGetValue(index, out var save) && save != null && save.alreadyLoaded) return true;
		LoadedProfilePath(index, out var profile);
		if (profile == null) return true;
		try
		{
			var kept = SaveRestore.KeepCopy(SaveIds.New(), $"Profile found in slot {index + 1}", profile, SidecarPath(index), PlusSaves.Safety, 0,
				"game profile without a mod file, kept before a new game", SaveRestore.FormatOf(profile));
			Log.Info($"[Saves] Slot {index} held a game profile without a mod file; kept a copy ({kept}).");
			return true;
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
		{
			Log.Error($"[Saves] Could not keep a copy of the profile in slot {index}", ex);
			return false;
		}
	}

	/// <summary>Path of the profile file the game would load for a slot (null when there is none).</summary>
	private static void LoadedProfilePath(int index, out string path)
	{
		path = ProfilePath(index, 1);
		if (File.Exists(path)) return;
		path = ProfilePath(index, 0);
		if (!File.Exists(path)) path = null;
	}

	private static string Truncate(string text)
	{
		text ??= string.Empty;
		return text.Length > SaveBundle.MaxTextLength ? text.Substring(0, SaveBundle.MaxTextLength) : text;
	}


	/// <summary>Stores a leaving player's live stats in the current save's profile list (audit A8).</summary>
	public static void CapturePlayer(UserData user)
	{
		if (user == null || !ModSaves.TryGetValue(currentSaveIndex, out var save) || save == null) return;
		SidecarRules.ApplyPlayerStats(new[] { user }, save.playerInfos, u => u.playerGUID, i => i.id, CopyStats);
	}

	private static void CopyStats(UserData user, PlayerInfo info) =>
		info.UpdateStats(user.position, user.rotation, user.playerExp, user.playerLevel, user.playerSkillPoints);
	public static void RemoveModSave(int index)
	{
		// A deleted save stays recoverable: "Restore a backup" on an empty slot lists this copy.
		if (ModSaves.TryGetValue(index, out var old) && old != null && old.alreadyLoaded && SaveIds.IsValid(old.saveId))
		{
			try
			{
				LoadedProfilePath(index, out var profile);
				profile ??= ProfilePath(index, 1);
				SaveRestore.KeepCopy(old.saveId, old.Name, profile, SidecarPath(index), PlusSaves.Safety,
					old.saveGeneration, "deleted save", SaveRestore.FormatOf(profile));
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
			{
				Log.Error("[Saves] Keeping a copy of the deleted save failed; nothing was deleted", ex);
				return;
			}
		}

		ModSaves[index] = new ModSaveData("EmptySave", index, false);
		UnreadableSlots.Remove(index);
		var modSaveFilePath = SidecarPath(index);
		var saveFilePath = ProfilePath(index, 1);
		Log.Info($"SavePath:{saveFilePath}");
		if (File.Exists(modSaveFilePath + ".bak")) File.Delete(modSaveFilePath + ".bak");
		if (File.Exists(modSaveFilePath))
		{
			File.Delete(modSaveFilePath);
			Log.Info($"Mod save file at {modSaveFilePath} deleted");
		}
		else
		{
			Log.Error("Error deleting  mod save file ");
		}
		
		if (File.Exists(saveFilePath))
		{
			if (Singleton<GameManager>.Instance.GameDataManager.ProfileData[index] != null)
			{
				var name = Singleton<GameManager>.Instance.GameDataManager.ProfileData[index].Name;
				Singleton<GameManager>.Instance.GameDataManager.ProfileData[index].Init();
				Singleton<GameManager>.Instance.GameDataManager.ProfileData[index].Name = name;
				Singleton<GameManager>.Instance.GameDataManager.ClearData();
			}
			File.Delete(saveFilePath);
			Log.Info($"Save file {saveFilePath} deleted");
		}
		// Phase 8 (assessment): a legacy-format profile is deleted too, or the next game in this slot could load it.
		var legacy = ProfilePath(index, 0);
		if (File.Exists(legacy)) File.Delete(legacy);
		else
		{
			Log.Error("Error deleting save file ");
		}
	}

	public static void StartGame(int index)
	{
		Application.runInBackground = true;
		Singleton<GameManager>.Instance.ProfileManager.selectedProfile = index; // <- needed

		Singleton<GameManager>.Instance.GameDataManager.LoadProfile();
		Singleton<GameManager>.Instance.StartCoroutine(Singleton<GameManager>.Instance.GameDataManager.Load(true));

		NotificationCenter.m_instance.StartCoroutine(NotificationCenter.m_instance.SelectSceneToLoad("garage", SceneType.Garage, true, true));
	}


	// The game writes its profile asynchronously after these hooks: they start the two-step commit (plan §3.5).
	[HarmonyPatch(typeof(ProfileManager), "Save")]
	[HarmonyPrefix]
	public static void SavePatch(ProfileManager __instance)
	{
		if (!Client.Instance.isConnected || !Client.Instance.IsHost) return;
		BeginSave(__instance.selectedProfile);
	}

	[HarmonyPatch(typeof(GarageLoader), nameof(GarageLoader.Save))]
	[HarmonyPrefix]
	public static bool SaveHook(bool showInfoIfSaveInProgress = false)
	{
		if (!Client.Instance.isConnected) return true;

		GameData.Instance?.orderGenerator?.Save();
		if (Client.Instance.IsHost) BeginSave(Singleton<GameManager>.Instance.ProfileManager.selectedProfile);
		return true;
	}

	public static Gamemode GetGamemodeFromInt(int selectedGamemode)
	{
		if (selectedGamemode == 1)
			return Gamemode.Normal;
		if (selectedGamemode == 0)
			return Gamemode.Easy;
		if (selectedGamemode == 2)
			return Gamemode.Expert;
		return Gamemode.Sandbox;
	}

	public static Gamemode GetGamemodeFromDifficulty(DifficultyLevel difficultyLevel)
	{
		if (difficultyLevel == DifficultyLevel.Sandbox)
			return Gamemode.Sandbox;
		if (difficultyLevel == DifficultyLevel.Easy)
			return Gamemode.Easy;
		if (difficultyLevel == DifficultyLevel.Expert)
			return Gamemode.Expert;
		return Gamemode.Normal;
	}
}