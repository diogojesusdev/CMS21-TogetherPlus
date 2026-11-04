using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using CMS21Together.ClientSide.Data.Handle;
using CMS21Together.Shared;
using CMS21Together.Shared.Data;
using CMS21Together.Shared.Data.Vanilla.Cars;
using CMS21Together.Shared.World;

namespace CMS21Together.ClientSide.Data.Garage.Car;

/// <summary>
/// Per-car desync detector (Phase 6), the part that reads the live game. The host and every player hash a car the
/// same way from their own <see cref="CarLoader"/>; players report the cars they have not touched lately, and the
/// host answers a mismatch with its full car read from its live game (the same data the car spawn path sends).
/// </summary>
public static class CarChecks
{
	private static readonly Stopwatch Clock = Stopwatch.StartNew();
	private static CarCheckClient checks = new(() => Clock.ElapsedMilliseconds);

	public static void Reset() => checks = new CarCheckClient(() => Clock.ElapsedMilliseconds);

	/// <summary>Reports every idle car on the next frame (forced resync).</summary>
	public static void ForceCheck() => checks.ForceNow();

	/// <summary>This player changed the car, or a change of it was just applied here.</summary>
	public static void Touched(int carLoaderID) => checks.Touched(carLoaderID);

	/// <summary>The car's fingerprint, read from this game; null when it is not loaded and ready here.</summary>
	public static string Hash(int carLoaderID)
	{
		var data = ClientData.Instance;
		if (data == null || GameData.Instance == null || !GameLoadHook.IsGameReady()) return null;
		if (!data.loadedCars.TryGetValue(carLoaderID, out var car) || car == null || !car.isReady || car.CarPartInfo == null) return null;
		var parts = new List<PartState>();
		var info = car.CarPartInfo;
		foreach (var pair in info.OtherPartsReferences)
			for (var i = 0; i < pair.Value.Count; i++) parts.Add(State($"o{pair.Key}.{i}", pair.Value[i]));
		foreach (var pair in info.SuspensionPartsReferences)
			for (var i = 0; i < pair.Value.Count; i++) parts.Add(State($"s{pair.Key}.{i}", pair.Value[i]));
		foreach (var pair in info.EnginePartsReferences) parts.Add(State($"e{pair.Key}", pair.Value));
		foreach (var pair in info.DriveshaftPartsReferences) parts.Add(State($"d{pair.Key}", pair.Value));
		foreach (var pair in info.BodyPartsReferences)
			if (pair.Value != null)
				parts.Add(new PartState($"b{pair.Key}", pair.Value.Unmounted, pair.Value.Condition, 0, null, false));
		return CarHash.Of(parts);
	}

	// Only what a push always sets on the other side: mounted or not, condition, quality.
	private static PartState State(string key, PartScript part) =>
		part == null ? new PartState(key, true, 0f, 0, null, false) : new PartState(key, part.IsUnmounted, part.Condition, part.Quality, null, false);

	/// <summary>Player side, every frame: the report every 10 s.</summary>
	public static void Pump()
	{
		var data = ClientData.Instance;
		if (data == null || Client.Instance.IsHost || !GameLoadHook.IsGameReady() || SceneManager.CurrentScene() != GameScene.garage) return;
		var cycles = checks.Cycles;
		var due = checks.Due(data.loadedCars.Keys);
		if (checks.Cycles == cycles) return;
		// Phase 6b (GhostCarCleanup): every loaded car goes with the report, so the host can spot ghosts and gaps.
		var ghosts = Features.Features.IsOn(Shared.Features.FeatureCatalog.GhostCarCleanup);
		var report = new List<(int, string)>();
		foreach (var id in due)
		{
			var hash = Hash(id);
			if (hash != null) report.Add((id, hash));
		}
		if (report.Count > 0 || ghosts) ClientSend.CarCheck(report, ghosts ? data.loadedCars.Keys.Take(CarCheckWire.MaxCars).ToList() : null);
	}

	/// <summary>Host side: the host's own game's view of the car, sent part by part to one player (and kept).</summary>
	public static int PushCar(int slot, int carLoaderID)
	{
		var data = ClientData.Instance;
		if (data == null || !data.loadedCars.TryGetValue(carLoaderID, out var car) || car?.CarPartInfo == null) return 0;
		var info = car.CarPartInfo;
		var sent = 0;
		void Part(PartScript part, int key, int index, ModPartType type)
		{
			if (part == null) return;
			var state = new ModPartScript(part, key, index, type);
			ServerSide.Data.ServerData.Instance.UpdatePartScripts(state, carLoaderID);
			ServerSide.Data.ServerSend.PartScriptPacket(slot, state, carLoaderID, true);
			sent++;
		}
		foreach (var pair in info.OtherPartsReferences)
			for (var i = 0; i < pair.Value.Count; i++) Part(pair.Value[i], pair.Key, i, ModPartType.other);
		foreach (var pair in info.SuspensionPartsReferences)
			for (var i = 0; i < pair.Value.Count; i++) Part(pair.Value[i], pair.Key, i, ModPartType.suspension);
		foreach (var pair in info.EnginePartsReferences) Part(pair.Value, pair.Key, -1, ModPartType.engine);
		foreach (var pair in info.DriveshaftPartsReferences) Part(pair.Value, pair.Key, -1, ModPartType.driveshaft);
		foreach (var pair in info.BodyPartsReferences)
		{
			if (pair.Value == null) continue;
			var state = new ModCarPart(pair.Value, pair.Key, carLoaderID);
			ServerSide.Data.ServerData.Instance.UpdateBodyParts(state, carLoaderID);
			ServerSide.Data.ServerSend.BodyPartPacket(slot, state, carLoaderID, true);
			sent++;
		}
		return sent;
	}
}