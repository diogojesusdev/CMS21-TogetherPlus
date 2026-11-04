// Based on QolParkingExtraSync.cs from FixForTogether by TogetherFixer
// (https://github.com/Stevii001100/Fix-For-CMS21-Together), custom non-commercial licence.
// Modified for Together+: instead of refreshing the QoL mod's extra parking cars after every parking change, the
// extra parking is turned off for the session (plan §3.7: "force-disable unsynced extra parking"): its spawn routine
// is skipped and loaders it already made are removed locally (never sent). Same QoL types, routine and placeNo range
// as the original. Without the QoL mod the feature does nothing; the mod's own settings are never changed.
using System;
using System.Collections;
using System.Reflection;
using CMS21Together.ClientSide.Data.Garage.Car;
using CMS21Together.Shared;
using CMS21Together.Shared.Features;
using HarmonyLib;
using UnityEngine;

namespace CMS21Together.ClientSide.Features.Garage;

/// <summary>QoL mod compatibility: no unsynced extra parking cars in a session.</summary>
public sealed class QolParkingFeature : GameFeature
{
	private const float CheckEvery = 3f;

	private float nextCheck;
	private bool found;

	public override string Id => FeatureCatalog.QolParkingCompat;

	protected override void Patch()
	{
		found = false;
		var routines = AccessTools.TypeByName("CMS2021mod.Routines");
		if (routines == null || AccessTools.TypeByName("CMS2021mod.Main") == null) return;
		foreach (var method in routines.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
		{
			if (method.Name != "SpawnAndCheckParkingCars") continue;
			if (method.ReturnType == typeof(IEnumerator)) Prefix(method, nameof(SkipRoutine), Priority.First);
			else if (method.ReturnType == typeof(void)) Prefix(method, nameof(Skip), Priority.First);
			else throw new MissingMethodException("CMS2021mod.Routines", "SpawnAndCheckParkingCars (unknown return type)");
			found = true;
		}
		if (!found) throw new MissingMethodException("CMS2021mod.Routines", "SpawnAndCheckParkingCars");
		Log.Info("[QolParking] QoL mod found: its extra parking cars are off while in a session.");
	}

	protected override void Started() => nextCheck = 0f;

	private static IEnumerator Nothing()
	{
		yield break;
	}

	private static bool SkipRoutine(ref IEnumerator __result)
	{
		__result = Nothing();
		return false;
	}

	private static bool Skip() => false;

	protected internal override void Tick()
	{
		if (!found || Time.unscaledTime < nextCheck) return;
		nextCheck = Time.unscaledTime + CheckEvery;
		if (!SceneManager.IsInGarage()) return;
		var removed = RemoveExtraLoaders();
		if (removed > 0) Log.Info($"[QolParking] Removed {removed} unsynced QoL extra parking car(s).");
	}

	private static int RemoveExtraLoaders()
	{
		var loaders = UnityEngine.Object.FindObjectsOfType<CarLoader>();
		if (loaders == null) return 0;
		var script = GameScript.Get();
		var removed = 0;
		foreach (var loader in loaders)
		{
			if (loader == null || !QolParking.IsExtraPlace(loader.placeNo)) continue;
			try
			{
				CarSpawnHooks.listenToDelete = false;
				loader.DeleteCar();
			}
			catch (Exception ex)
			{
				Log.Error($"[QolParking] Removing the extra car at {loader.placeNo} failed", ex);
			}
			finally
			{
				CarSpawnHooks.listenToDelete = true;
			}
			try
			{
				if (script != null && script.carOnScene != null && script.carOnScene.Count > 0)
					script.SetCarsOnSceneAmount(script.carOnScene.Count - 1);
				if (loader.gameObject != null) UnityEngine.Object.Destroy(loader.gameObject);
			}
			catch (Exception ex)
			{
				Log.Error("[QolParking] Cleaning up an extra car loader failed", ex);
			}
			removed++;
		}
		return removed;
	}
}