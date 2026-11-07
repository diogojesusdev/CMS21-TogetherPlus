using System;
using System.Collections;
using CMS21Together.ClientSide.Data.Garage.Tools;
using CMS21Together.ClientSide.Data.Handle;
using CMS21Together.Shared.Data;
using CMS21Together.Shared.Data.Vanilla;
using CMS21Together.Shared.Data.Vanilla.Cars;
using MelonLoader;
using UnityEngine;
using CMS21Together.Shared;
using CMS21Together.Shared.Safety;
using CrashReport = CMS21Together.Shared.Safety.CrashReport;

namespace CMS21Together.ClientSide.Data.Garage.Car;

public static class PartsUpdater
{
	private const int TracePart = 1, TraceBody = 2, TraceMissing = 3;
	private static readonly LogThrottle Trace = new(20, 5000);

	/// <summary>Breadcrumb before the game applies a remote change (the last line before a native crash).</summary>
	private static void Breadcrumb(int key, string message)
	{
		CrashReport.Note(message);
		if (Log.IsEnabled(LogLevel.Info) && Trace.Allow(key, out var skipped))
			Log.Info(message + LogThrottle.Skipped(skipped));
	}

	public static IEnumerator UpdatePartScripts(ModPartScript partScript, int carLoaderID)
	{
		CarChecks.Touched(carLoaderID);
		while (!GameLoadHook.IsGameReady())
			yield return new WaitForSeconds(0.25f);

		yield return new WaitForEndOfFrame();
		yield return new WaitForEndOfFrame();
		yield return new WaitForEndOfFrame();
		
		if (carLoaderID == -1)
		{
			for (var counter = 0; counter < 25 && !ClientData.Instance.engineStand.isHandled; counter++)
				yield return new WaitForSeconds(.5f);
			yield return new WaitForEndOfFrame();
			if (ClientData.Instance.engineStand?.partReferences == null ||
			    !ClientData.Instance.engineStand.partReferences.TryGetValue(partScript.partID, out var stand1Part))
			{
				Breadcrumb(TraceMissing, $"[Apply] Part {partScript.id} dropped: engine stand 1 does not know it.");
				yield break;
			}
			Breadcrumb(TracePart, $"[Apply] Part {partScript.id} (#{partScript.partID}) on engine stand 1, unmounted={partScript.unmounted}.");
			using (ActionGate.Apply())
				UpdatePartScript(partScript, stand1Part, -1);
			yield break;
		}
		if (carLoaderID == -2)
		{
			int counter = 0;
			while (counter < 25 && !ClientData.Instance.engineStand2.isHandled)
			{
				yield return new WaitForSeconds(.5f);
				counter++;
			}
			yield return new WaitForEndOfFrame();
			if (ClientData.Instance.engineStand2?.partReferences == null ||
			    !ClientData.Instance.engineStand2.partReferences.TryGetValue(partScript.partID, out var stand2Part))
			{
				Breadcrumb(TraceMissing, $"[Apply] Part {partScript.id} dropped: engine stand 2 does not know it.");
				yield break;
			}
			Breadcrumb(TracePart, $"[Apply] Part {partScript.id} (#{partScript.partID}) on engine stand 2, unmounted={partScript.unmounted}.");
			using (ActionGate.Apply())
				UpdatePartScript(partScript, stand2Part, -2);
			yield break;
		}
		
		if (ClientData.Instance.loadedCars.TryGetValue(carLoaderID, out var _car))
		{
			var waited = 0;
			while (!_car.isReady && waited++ < 100 && Client.Instance.isConnected)
				yield return new WaitForSeconds(0.25f);
			if (!_car.isReady || !Client.Instance.isConnected)
			{
				Breadcrumb(TraceMissing, $"[Apply] Part {partScript.id} dropped: car {carLoaderID} never became ready here.");
				yield break;
			}
		}
		yield return new WaitForEndOfFrame();

		//Log.Info("[PartsUpdater->UpdatePartScripts] Car ready, updating..");
		if (ClientData.Instance.loadedCars.TryGetValue(carLoaderID, out var car) && car.CarPartInfo != null)
		{
			var key = partScript.partID;
			var index = partScript.partIdNumber;

			PartScript reference = null;

			switch (partScript.type)
			{
				case ModPartType.engine:
					if (!car.CarPartInfo.EnginePartsReferences.TryGetValue(key, out reference)) reference = null;
					break;
				case ModPartType.suspension:
					if (car.CarPartInfo.SuspensionPartsReferences.TryGetValue(key, out var suspension) && suspension != null &&
					    index >= 0 && index < suspension.Count) reference = suspension[index];
					break;
				case ModPartType.other:
					if (car.CarPartInfo.OtherPartsReferences.TryGetValue(key, out var other) && other != null &&
					    index >= 0 && index < other.Count) reference = other[index];
					break;
				case ModPartType.driveshaft:
					if (!car.CarPartInfo.DriveshaftPartsReferences.TryGetValue(key, out reference)) reference = null;
					break;
				default:
					yield break;
			}

			if (reference == null)
			{
				Breadcrumb(TraceMissing, $"[Apply] Part {partScript.id} dropped: the car's part map has no {partScript.type} {key}/{index}.");
				ClientSend.ResyncCar(carLoaderID);
				yield break;
			}
			Breadcrumb(TracePart, $"[Apply] Part {partScript.id} ({partScript.type} {key}/{index}) on car {carLoaderID} {car.carID}, unmounted={partScript.unmounted}.");
			using (ActionGate.Apply())
				UpdatePartScript(partScript, reference, carLoaderID);
		}
		else
			Breadcrumb(TraceMissing, $"[Apply] Part {partScript.id} dropped: car {carLoaderID} is not loaded here.");
	}

	private static void UpdatePartScript(ModPartScript part, PartScript reference, int carLoaderID)
	{
		if (part == null || reference == null)
		{
			Log.Warn("Invalid part!");
			return;
		}

		if (Features.Visuals.TuningPartsFeature.PrepareVariant(part, reference))
		{
			// Phase 6b (TuningParts): tuned and normal variants both ways.
		}
		else if(carLoaderID != -1 && carLoaderID != -2)
			if (!string.IsNullOrEmpty(part.tunedID) && !string.IsNullOrEmpty(reference.tunedID))
				if (reference.tunedID != part.tunedID)
					GameData.Instance.carLoaders[carLoaderID].TunePart(reference.id, part.tunedID);
		reference.IsExamined = part.isExamined;
		reference.Quality = part.quality;
		reference.SetCondition(part.condition);
		reference.UpdateDust(part.dust, true);
		reference.SetConditionNormal(part.condition);

		if (!part.unmounted)
		{
			reference.IsPainted = part.isPainted;
			if (part.isPainted)
			{
				reference.CurrentPaintType = (PaintType)part.paintType;
				reference.CurrentPaintData = new ModPaintData().ToGame(part.paintData);
				reference.SetColor(part.color.ToGame());
				if ((PaintType)part.paintType == PaintType.Custom)
					PaintHelper.SetCustomPaintType(reference.gameObject, part.paintData.ToGame(part.paintData), false);
				else
					PaintHelper.SetPaintType(reference.gameObject, (PaintType)part.paintType, false);
			}

			if (reference.IsUnmounted)
			{
				if (Log.IsEnabled(LogLevel.Debug)) Log.Debug($"[Apply] Showing {part.id} (car {carLoaderID}).");
				reference.ShowBySaveGame();

				reference.Show();
				MelonCoroutines.Start(CustomPartScriptMethod.ShowMounted(reference));
				reference.ShowMountAnimation();

				reference.SetCondition(part.condition);
				reference.SetConditionNormal(part.condition);
			}

			if (carLoaderID != -1 && carLoaderID != -2)
			{
				var wheelData = GameData.Instance.carLoaders[carLoaderID].WheelsData;
				for (var i = 0; i < GameData.Instance.carLoaders[carLoaderID].WheelsData.Wheels.Count; i++)
				{
					GameData.Instance.carLoaders[carLoaderID].SetWheelSize((int)wheelData.Wheels[i].Width,
						(int)wheelData.Wheels[i].Size, (int)wheelData.Wheels[i].Profile, (WheelType)i);
					GameData.Instance.carLoaders[carLoaderID].SetET((WheelType)i, wheelData.Wheels[i].ET);
				}

				GameData.Instance.carLoaders[carLoaderID].SetWheelSizes();
			}
		}
		else
		{
			if (reference.IsUnmounted == false)
			{
				if (Log.IsEnabled(LogLevel.Debug)) Log.Debug($"[Apply] Hiding {part.id} (car {carLoaderID}).");
				if (carLoaderID != -1 && carLoaderID != -2)
					reference.HideBySavegame(false, GameData.Instance.carLoaders[carLoaderID]);
				else
					reference.HideBySavegame(false);
			}
		}

		if (part.unmountWith != null)
		{
			// The sender's list and this game's live array can differ (variant, tuned engine, partly mounted).
			// Match by part id first, fall back to the position only when it exists, and never index out of range.
			var receiverChildren = reference.unmountWith != null ? reference.unmountWith.ToArray() : new PartScript[0];
			for (var index = 0; index < part.unmountWith.Count; index++)
			{
				var child = part.unmountWith[index];
				if (child == null) continue;
				PartScript target = null;
				foreach (var candidate in receiverChildren)
					if (candidate != null && string.Equals(candidate.id, child.id, StringComparison.Ordinal))
					{
						target = candidate;
						break;
					}
				if (target == null && index < receiverChildren.Length) target = receiverChildren[index];
				if (target == null)
				{
					Log.Warn($"[Apply] No matching child for {child.id} on car {carLoaderID}; the group is partly applied.");
					continue;
				}
				UpdatePartScript(child, target, carLoaderID);
			}
		}
	}

	public static IEnumerator UpdateBodyParts(ModCarPart carPart, int carLoaderID)
	{
		CarChecks.Touched(carLoaderID);
		if (carPart == null) yield break;
		if (ClientData.Instance.loadedCars.TryGetValue(carLoaderID, out var _car))
		{
			var waited = 0;
			while (!_car.isReady && waited++ < 100 && Client.Instance.isConnected)
				yield return new WaitForSeconds(0.25f);
			if (!_car.isReady || !Client.Instance.isConnected) yield break;
		}
		yield return new WaitForEndOfFrame();

		Log.Debug("[PartsUpdater->UpdateBodyParts] Car ready, updating..");
		if (ClientData.Instance.loadedCars.TryGetValue(carLoaderID, out var car) && car.CarPartInfo != null)
		{
			var key = carPart.carPartID;

			if (!car.CarPartInfo.BodyPartsReferences.TryGetValue(key, out var reference) || reference == null)
			{
				Breadcrumb(TraceMissing, $"[Apply] Body part #{key} dropped: the car's map has no such panel.");
				ClientSend.ResyncCar(carLoaderID);
				yield break;
			}
			Breadcrumb(TraceBody, $"[Apply] Body part #{key} on car {carLoaderID} {car.carID}, unmounted={carPart.unmounted}, switched={carPart.switched}.");
			using (ActionGate.Apply())
				UpdateBodyPart(carPart, reference, carLoaderID);
		}
		else
			Breadcrumb(TraceMissing, $"[Apply] Body part #{carPart.carPartID} dropped: car {carLoaderID} is not loaded here.");
	}

	private static void UpdateBodyPart(ModCarPart carPart, CarPart reference, int carLoaderID)
	{
		if (carPart == null || reference == null) return;
		if (GameData.Instance?.carLoaders == null || carLoaderID < 0 || carLoaderID >= GameData.Instance.carLoaders.Length ||
		    GameData.Instance.carLoaders[carLoaderID] == null) return;

		var color = carPart.colors.ToGame();
		var tintColor = carPart.TintColor.ToGame();

		if (reference.TunedID != carPart.tunedID)
			GameData.Instance.carLoaders[carLoaderID].TunePart(reference.name, carPart.tunedID);

		GameData.Instance.carLoaders[carLoaderID].SetDent(reference, carPart.dent);
		GameData.Instance.carLoaders[carLoaderID].EnableDust(reference, carPart.Dust);
		GameData.Instance.carLoaders[carLoaderID].SetCondition(reference, carPart.condition);
		GameData.Instance.carLoaders[carLoaderID].SetCarLivery(reference, carPart.livery, carPart.liveryStrength);

		reference.name = carPart.name;
		reference.IsTinted = carPart.isTinted;
		reference.PaintType = (PaintType)carPart.paintType;
		reference.OutsideRustEnabled = carPart.outsaidRustEnabled;
		reference.AdditionalString = carPart.additionalString;
		reference.Quality = carPart.quality;
		reference.WashFactor = carPart.washFactor;
		reference.StructureCondition = carPart.conditionStructure;
		reference.ConditionPaint = carPart.conditionPaint;

		if (!carPart.unmounted && !reference.name.StartsWith("license_plate"))
		{
			if (carPart.colors != null)
				GameData.Instance.carLoaders[carLoaderID].SetCarColor(reference, color);
			if (carPart.TintColor != null)
				GameData.Instance.carLoaders[carLoaderID].SetCarPaintType(reference, (PaintType)carPart.paintType);
		}

		if (!reference.Unmounted && carPart.unmounted)
		{
			Breadcrumb(TraceBody, $"[Apply] Taking off {reference.name} (car {carLoaderID}).");
			GameData.Instance.carLoaders[carLoaderID].TakeOffCarPartFromSave(reference.name);
		}

		if (reference.Unmounted && !carPart.unmounted)
		{
			Breadcrumb(TraceBody, $"[Apply] Putting on {reference.name} (car {carLoaderID}).");
			GameData.Instance.carLoaders[carLoaderID].TakeOnCarPartFromSave(reference.name);
		}

		if (reference.Switched != carPart.switched)
		{
			Breadcrumb(TraceBody, $"[Apply] Switching {reference.name} to {carPart.switched} (car {carLoaderID}).");
			GameData.Instance.carLoaders[carLoaderID].SwitchCarPart(reference, false, carPart.switched);
		}

		foreach (var _carPart in carPart.connectedParts)
		{
			if (_carPart == null) continue;
			var key = _carPart.carPartID;
			if (ClientData.Instance.loadedCars.TryGetValue(carLoaderID, out var connectedCar) &&
			    connectedCar.CarPartInfo != null &&
			    connectedCar.CarPartInfo.BodyPartsReferences.TryGetValue(key, out var _reference) && _reference != null)
			{
				Log.Debug("[PartsUpdater->UpdateBodyParts] Updating BodyPart..");
				UpdateBodyPart(_carPart, _reference, carLoaderID);
			}
		}

		if (carPart.isTinted && carPart.TintColor != null)
			PaintHelper.SetWindowProperties(reference.handle, (int)(carPart.TintColor.a * 255), tintColor);

		GameData.Instance.carLoaders[carLoaderID].SetCondition(reference, carPart.condition);
		GameData.Instance.carLoaders[carLoaderID].UpdateCarBodyPart(reference);
	}

	public static IEnumerator UpdateFluid(ModFluidData fluid, int carLoaderID)
	{
		if (fluid?.CarFluid == null) yield break;
		if (ClientData.Instance.loadedCars.TryGetValue(carLoaderID, out var _car))
		{
			var waited = 0;
			while (!_car.isReady && waited++ < 100 && Client.Instance.isConnected)
				yield return new WaitForSeconds(0.25f);
			if (!_car.isReady || !Client.Instance.isConnected) yield break;
		}
		yield return new WaitForEndOfFrame();

		if (GameData.Instance?.carLoaders == null || carLoaderID < 0 || carLoaderID >= GameData.Instance.carLoaders.Length ||
		    GameData.Instance.carLoaders[carLoaderID]?.FluidsData == null)
		{
			Log.Warn($"[Apply] Fluid change for car {carLoaderID} dropped: the car is not loaded here.");
			ClientSend.ResyncCar(carLoaderID);
			yield break;
		}

		// Silence both fluid hooks for this frame: the game may reach either or both for one applied change, and
		// whichever runs second must not send the value back (the report's FL-4).
		PartUpdateHooks.Suppress();
		PartUpdateHooks.Changed(carLoaderID, (int)fluid.CarFluid.FluidType, fluid.CarFluid.ID, fluid.Level, fluid.Condition);
		var fluids = GameData.Instance.carLoaders[carLoaderID].FluidsData;
		if (fluid.CarFluid.FluidType == ModCarFluidType.EngineOil)
		{
			// The game's own refill writes the oil straight into Oil, which has no CarFluid.
			if (fluids.Oil == null) yield break;
			fluids.Oil.Level = fluid.Level;
			fluids.Oil.Condition = fluid.Condition;
			if (Log.IsEnabled(LogLevel.Debug)) Log.Debug($"[Apply] Engine oil on car {carLoaderID} set to {fluid.Level:0.00}.");
			yield break;
		}
		fluids.SetLevelAndCondition(fluid.Level, fluid.Condition, (CarFluidType)fluid.CarFluid.FluidType);
	}
}