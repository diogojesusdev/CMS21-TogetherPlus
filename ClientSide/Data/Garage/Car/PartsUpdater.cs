using System.Collections;
using CMS21Together.ClientSide.Data.Garage.Tools;
using CMS21Together.Shared.Data;
using CMS21Together.Shared.Data.Vanilla;
using CMS21Together.Shared.Data.Vanilla.Cars;
using MelonLoader;
using UnityEngine;
using CMS21Together.Shared;

namespace CMS21Together.ClientSide.Data.Garage.Car;

public static class PartsUpdater
{
	private const int TracePart = 1, TraceBody = 2, TraceMissing = 3;
	private static readonly LogThrottle Trace = new(20, 5000);

	/// <summary>Breadcrumb before the game applies a remote change (the last line before a native crash).</summary>
	private static void Breadcrumb(int key, string message)
	{
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
			while (!ClientData.Instance.engineStand.isHandled)
				yield return new WaitForSeconds(.15f);
			yield return new WaitForEndOfFrame();
			
			Breadcrumb(TracePart, $"[Apply] Part {partScript.id} (#{partScript.partID}) on engine stand 1, unmounted={partScript.unmounted}.");
			using (ActionGate.Apply())
				UpdatePartScript(partScript, ClientData.Instance.engineStand.partReferences[partScript.partID], -1);
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
			
			Breadcrumb(TracePart, $"[Apply] Part {partScript.id} (#{partScript.partID}) on engine stand 2, unmounted={partScript.unmounted}.");
			using (ActionGate.Apply())
				UpdatePartScript(partScript, ClientData.Instance.engineStand2.partReferences[partScript.partID], -2);
			yield break;
		}
		
		if (ClientData.Instance.loadedCars.TryGetValue(carLoaderID, out var _car))
			while (!_car.isReady)
				yield return new WaitForSeconds(0.25f);
		yield return new WaitForEndOfFrame();

		//Log.Info("[PartsUpdater->UpdatePartScripts] Car ready, updating..");
		if (ClientData.Instance.loadedCars.TryGetValue(carLoaderID, out var car))
		{
			var key = partScript.partID;
			var index = partScript.partIdNumber;

			PartScript reference;

			switch (partScript.type)
			{
				case ModPartType.engine:
					reference = car.CarPartInfo.EnginePartsReferences[key];
					break;
				case ModPartType.suspension:
					reference = car.CarPartInfo.SuspensionPartsReferences[key][index];
					break;
				case ModPartType.other:
					reference = car.CarPartInfo.OtherPartsReferences[key][index];
					break;
				case ModPartType.driveshaft:
					reference = car.CarPartInfo.DriveshaftPartsReferences[key];
					break;
				default:
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
			for (var index = 0; index < part.unmountWith.Count; index++)
			{
				var partScript = part.unmountWith[index];
				UpdatePartScript(partScript, reference.unmountWith.ToArray()[index], carLoaderID);
			}
		}
	}

	public static IEnumerator UpdateBodyParts(ModCarPart carPart, int carLoaderID)
	{
		CarChecks.Touched(carLoaderID);
		if (ClientData.Instance.loadedCars.TryGetValue(carLoaderID, out var _car))
			while (!_car.isReady)
				yield return new WaitForSeconds(0.25f);
		yield return new WaitForEndOfFrame();

		Log.Debug("[PartsUpdater->UpdateBodyParts] Car ready, updating..");
		if (ClientData.Instance.loadedCars.TryGetValue(carLoaderID, out var car))
		{
			var key = carPart.carPartID;

			var reference = car.CarPartInfo.BodyPartsReferences[key];
			Breadcrumb(TraceBody, $"[Apply] Body part #{key} on car {carLoaderID} {car.carID}, unmounted={carPart.unmounted}, switched={carPart.switched}.");
			using (ActionGate.Apply())
				UpdateBodyPart(carPart, reference, carLoaderID);
		}
		else
			Breadcrumb(TraceMissing, $"[Apply] Body part #{carPart.carPartID} dropped: car {carLoaderID} is not loaded here.");
	}

	private static void UpdateBodyPart(ModCarPart carPart, CarPart reference, int carLoaderID)
	{
		if (carPart == null || reference == null)
		{
			Log.Warn("Invalid bodyPart!");
			return;
		}

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
			var key = _carPart.carPartID;
			var _reference = ClientData.Instance.loadedCars[carLoaderID].CarPartInfo.BodyPartsReferences[key];
			Log.Debug("[PartsUpdater->UpdateBodyParts] Updating BodyPart..");
			UpdateBodyPart(_carPart, _reference, carLoaderID);
		}

		if (carPart.isTinted && carPart.TintColor != null)
			PaintHelper.SetWindowProperties(reference.handle, (int)(carPart.TintColor.a * 255), tintColor);

		GameData.Instance.carLoaders[carLoaderID].SetCondition(reference, carPart.condition);
		GameData.Instance.carLoaders[carLoaderID].UpdateCarBodyPart(reference);
	}

	public static IEnumerator UpdateFluid(ModFluidData fluid, int carLoaderID)
	{
		if (ClientData.Instance.loadedCars.TryGetValue(carLoaderID, out var _car))
			while (!_car.isReady)
				yield return new WaitForSeconds(0.25f);
		yield return new WaitForEndOfFrame();

		PartUpdateHooks.listen = false;
		GameData.Instance.carLoaders[carLoaderID].FluidsData
			.SetLevelAndCondition(fluid.Level, fluid.Condition, (CarFluidType)fluid.CarFluid.FluidType);
	}
}