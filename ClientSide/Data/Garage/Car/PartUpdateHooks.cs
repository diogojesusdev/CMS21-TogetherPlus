using System.Collections;
using CMS21Together.ClientSide.Data.Garage.Tools;
using CMS21Together.ClientSide.Data.Handle;
using CMS21Together.Shared.Data;
using CMS21Together.Shared.Data.Vanilla.Cars;
using CMS21Together.Shared.Data.Vanilla.GarageTool;
using CMS21Together.Shared.Locks;
using CMS21Together.Shared.World;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using CMS21Together.Shared;

namespace CMS21Together.ClientSide.Data.Garage.Car;

[HarmonyPatch]
public static class PartUpdateHooks
{
	public static bool listen = true;
	private static readonly LogThrottle Trace = new(20, 5000);
	
	[HarmonyPatch(typeof(FluidsData), nameof(FluidsData.SetLevel))]
	[HarmonyPostfix]
	public static void SetLevelAltHook(float level, CarFluidType carFluidType, int id, FluidsData __instance)
	{
		if (!Client.Instance.isConnected || !listen) {listen = true; return;}
		// Cars of the junkyard, auto salon or barn are not garage cars (their loader ids would hit garage cars).
		if (SceneManager.CurrentScene() != GameScene.garage) return;

		if (carFluidType == CarFluidType.EngineOil && __instance.Oil.CarFluid != null)
		{
			int carLoaderID = CarLoaderRegistry.IdOfParent(__instance.Oil.CarFluid);
			ClientSend.CarFluid(carLoaderID, new ModFluidData(__instance.Oil));
		}

	}
	
	[HarmonyPatch(typeof(FluidData), nameof(FluidData.SetLevel))]
	[HarmonyPostfix]
	public static void SetLevelHook(float level, FluidData __instance)
	{
		if (!Client.Instance.isConnected || !listen) {listen = true; return;}
		if (SceneManager.CurrentScene() != GameScene.garage) return;

		if (__instance != null && __instance.CarFluid != null)
		{
			int carLoaderID = CarLoaderRegistry.IdOfParent(__instance.CarFluid);
			ClientSend.CarFluid(carLoaderID, new ModFluidData(__instance));
		}

	}

	[HarmonyPatch(typeof(PartScript), nameof(PartScript.DoMount))]
	[HarmonyPostfix]
	public static void DoMountHook(PartScript __instance)
	{
		// Mounts done while applying the host's state are not this player's changes: never send them back.
		if (!Client.Instance.isConnected || ActionGate.ApplyingServerState || SceneManager.CurrentScene() != GameScene.garage) return;
		MelonCoroutines.Start(HandleDoMount(__instance));
	}

	private static bool AreBoltsMounted(PartScript partScript)
	{
		var allMounted = true;
		foreach (var bolt in partScript.MountObjects)
			if (bolt.unmounted)
			{
				allMounted = false;
				break;
			}

		return allMounted;
	}

	/// <summary>A part left half-bolted is not sent; it is sent when the player comes back and finishes it.</summary>
	private const float MountWatchSeconds = 600f;

	private static IEnumerator HandleDoMount(PartScript partScript)
	{
		// Audit A11: react to the state change itself (checked every frame) instead of 0.25 s polls with a 4 s minimum.
		var waited = 0f;
		while (partScript != null && (partScript.IsUnmounted || !partScript.oneClickUnmount && !AreBoltsMounted(partScript)))
		{
			if (waited > MountWatchSeconds || !Client.Instance.isConnected) yield break;
			yield return null;
			waited += Time.unscaledDeltaTime;
		}
		if (partScript == null) yield break;

		Log.Debug("[PartUpdateHooks->DoMountHook] Triggered.");
		if (partScript.GetComponentInParent<CarLoaderOnCar>())
		{
			var carLoaderID = CarLoaderRegistry.IdOfParent(partScript);
			if (!ClientData.Instance.loadedCars.TryGetValue(carLoaderID, out var car) || car?.CarPartInfo == null) yield break;

			if (FindPartInDictionaries(car, partScript, out var partType, out var key, out var index))
				MelonCoroutines.Start(SendPartUpdate(car, carLoaderID, key, index, partType));
			else
				Log.Warn("[PartUpdateHooks->DoMountHook] PartScript not found in any dictionary.");
		}
		else
		{
			ModEngineStand stand;
			if (EngineStand.useAlt)
				stand = ClientData.Instance.engineStand2;
			else
				stand = ClientData.Instance.engineStand;
			foreach (var kvp in stand.partReferences)
			{
				if (Same(kvp.Value, partScript))
				{
					Log.Debug($"Sending part:{partScript.id} , {kvp.Key}.");
					MelonCoroutines.Start(SendPartUpdate(null, EngineStand.useAlt ? -2 : -1, kvp.Key, null, ModPartType.engineStand));
					break;
				}
			}
			
		}
	}

	[HarmonyPatch(typeof(PartScript), nameof(PartScript.Hide))]
	[HarmonyPostfix]
	public static void HideHook(PartScript __instance) // best way i've found to detect when a partScript is unmounted
	{
		if (!Client.Instance.isConnected || ActionGate.ApplyingServerState) return;
		// Junkyard / salon / barn cars are not garage cars: their loader ids collide with the tracked ones.
		if (SceneManager.CurrentScene() != GameScene.garage) return;
		
		if (__instance.GetComponentInParent<CarLoaderOnCar>())
		{
			var carLoaderID = CarLoaderRegistry.IdOfParent(__instance);
			if (!ClientData.Instance.loadedCars.TryGetValue(carLoaderID, out var car) || car?.CarPartInfo == null) return;

			if (FindPartInDictionaries(car, __instance, out var partType, out var key, out var index))
				MelonCoroutines.Start(SendPartUpdate(car, carLoaderID, key, index, partType));
		}
		else // engine stand
		{
			// Phase 6b: both stands (an unmount on engine stand 2 used to be looked up on stand 1 and never sent).
			var standId = -1;
			foreach (var stand in new[] { ClientData.Instance.engineStand, ClientData.Instance.engineStand2 })
			{
				if (stand?.partReferences != null)
					foreach (var kvp in stand.partReferences)
					{
						if (Same(kvp.Value, __instance))
						{
							MelonCoroutines.Start(SendPartUpdate(null, standId, kvp.Key, null, ModPartType.engineStand));
							return;
						}
					}
				standId = -2;
			}
		}
	}

	[HarmonyPatch(typeof(CarLoader), nameof(CarLoader.TakeOffCarPart), typeof(string), typeof(bool))]
	[HarmonyPostfix]
	public static void TakeOffCarPartHook(string name, bool off, CarLoader __instance) // handle both Mount/Unmount with the boolean
	{
		if (!Client.Instance.isConnected || ActionGate.ApplyingServerState) return;
		if (SceneManager.CurrentScene() != GameScene.garage) return;

		var carLoaderID = CarLoaderRegistry.IdOf(__instance);
		if (!ClientData.Instance.loadedCars.TryGetValue(carLoaderID, out var car) || car?.CarPartInfo == null) return;

		if (FindBodyPartInDictionary(car, name, out var key) &&
		    car.CarPartInfo.BodyPartsReferences.TryGetValue(key, out var part))
		{
			// The player is working on this panel now: claim it before the write goes out.
			ActionGate.TryBegin(LockKeys.Body(carLoaderID, key));
			MelonCoroutines.Start(SendBodyPart(part, key, carLoaderID, Time.frameCount, off));
		}
	}

	/// <summary>
	/// Same game object, compared by address: no call into the game, so a stale stored reference can never be
	/// dereferenced natively (a crash the game's own == cannot guard against).
	/// </summary>
	public static bool Same(Object a, Object b) =>
		ReferenceEquals(a, b) || a is not null && b is not null && a.Pointer == b.Pointer;

	public static bool FindPartInDictionaries(ModCar car, PartScript partScript, out ModPartType partType, out int key, out int? index, bool warn = true)
	{
		index = null;

		foreach (var kvp in car.CarPartInfo.OtherPartsReferences)
		{
			var listIndex = kvp.Value.FindIndex(part => Same(part, partScript));
			if (listIndex >= 0)
			{
				partType = ModPartType.other;
				key = kvp.Key;
				index = listIndex;
				return true;
			}
		}

		foreach (var kvp in car.CarPartInfo.SuspensionPartsReferences)
		{
			var listIndex = kvp.Value.FindIndex(part => Same(part, partScript));
			if (listIndex >= 0)
			{
				partType = ModPartType.suspension;
				key = kvp.Key;
				index = listIndex;
				return true;
			}
		}

		foreach (var kvp in car.CarPartInfo.EnginePartsReferences)
			if (Same(kvp.Value, partScript))
			{
				partType = ModPartType.engine;
				key = kvp.Key;
				return true;
			}

		foreach (var kvp in car.CarPartInfo.DriveshaftPartsReferences)
			if (Same(kvp.Value, partScript))
			{
				partType = ModPartType.driveshaft;
				key = kvp.Key;
				return true;
			}

		partType = default;
		key = 0;
		if (warn) Log.Warn("[PartUpdateHooks->FindPartInDictionaries] PartScript not found in any dictionary.");
		return false;
	}

	public static bool FindBodyPartInDictionary(ModCar car, string carPartName, out int key, bool warn = true)
	{
		foreach (var kvp in car.CarPartInfo.BodyPartsReferences)
			if (kvp.Value.name == carPartName)
			{
				key = kvp.Key;
				return true;
			}

		key = 0;
		if (warn) Log.Warn("[PartUpdateHooks->FindBodyPartInDictionary] BodyPart not found in dictionary.");
		return false;
	}

	private static IEnumerator SendPartUpdate(ModCar car, int carLoaderID, int key, int? index, ModPartType partType)
	{
		yield return new WaitForEndOfFrame();

		PartScript part = null;
		switch (partType)
		{
			case ModPartType.engine:
				if (car?.CarPartInfo == null || !car.CarPartInfo.EnginePartsReferences.TryGetValue(key, out part)) yield break;
				break;
			case ModPartType.engineStand:
			{
				var stand = carLoaderID == -1 ? ClientData.Instance.engineStand : ClientData.Instance.engineStand2;
				if (stand?.partReferences == null || !stand.partReferences.TryGetValue(key, out part)) yield break;
				break;
			}
			case ModPartType.suspension:
			{
				if (car?.CarPartInfo == null || !car.CarPartInfo.SuspensionPartsReferences.TryGetValue(key, out var list) ||
				    list == null || !index.HasValue || index.Value < 0 || index.Value >= list.Count) yield break;
				part = list[index.Value];
				break;
			}
			case ModPartType.other:
			{
				if (car?.CarPartInfo == null || !car.CarPartInfo.OtherPartsReferences.TryGetValue(key, out var list) ||
				    list == null || !index.HasValue || index.Value < 0 || index.Value >= list.Count) yield break;
				part = list[index.Value];
				break;
			}
			case ModPartType.driveshaft:
				if (car?.CarPartInfo == null || !car.CarPartInfo.DriveshaftPartsReferences.TryGetValue(key, out part)) yield break;
				break;
			default:
				yield break;
		}

		yield return new WaitForEndOfFrame();

		if (Log.IsEnabled(LogLevel.Info) && Trace.Allow(1, out var skipped))
			Log.Info($"[Local] Sending part {part?.id} ({partType} {key}/{index?.ToString() ?? "-"}) car {carLoaderID}, unmounted={part?.IsUnmounted}{LogThrottle.Skipped(skipped)}");

		if (index.HasValue && partType != ModPartType.engineStand)
			ClientSend.PartScriptPacket(new ModPartScript(part, key, index.Value, partType), carLoaderID);
		else if (partType != ModPartType.engineStand)
			ClientSend.PartScriptPacket(new ModPartScript(part, key, -1, partType), carLoaderID);
		else
			ClientSend.PartScriptPacket(new ModPartScript(part, key, -1, partType), carLoaderID);
	}

	/// <param name="frame">Frame the panel moved in (-1: unknown); the item the game added or used then is linked.</param>
	public static IEnumerator SendBodyPart(CarPart part, int key, int carLoaderID, int frame = -1, bool off = false)
	{
		yield return new WaitForEndOfFrame();
		yield return new WaitForEndOfFrame();

		// Phase 6: if another player wins this panel, the host undoes exactly this item (no duplicate, nothing lost).
		var linked = frame < 0 ? 0 : Player.Inventory.LinkedItem(frame, off ? InventoryOp.Add : InventoryOp.Remove);
		if (Log.IsEnabled(LogLevel.Info) && Trace.Allow(2, out var skipped))
			Log.Info($"[Local] Sending body part #{key} {part?.name} car {carLoaderID}, off={off}, linked item {linked}{LogThrottle.Skipped(skipped)}");
		ClientSend.BodyPartPacket(new ModCarPart(part, key, carLoaderID), carLoaderID, linked, off);
	}
}