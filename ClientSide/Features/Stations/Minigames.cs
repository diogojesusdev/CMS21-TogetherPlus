// Based on desync_Dyno.cs, desync_HeadlampAlignment.cs, desync_WheelsAlignment.cs, desync_Wheelbalancer.cs and
// desync_Tuning.cs from FixForTogether by TogetherFixer (https://github.com/Stevii001100/Fix-For-CMS21-Together),
// custom non-commercial licence. Modified for Together+: same patch targets and payloads, sent on the Together+
// feature channel with station/car locks; game properties instead of raw memory offsets; the minigame skip is the
// host-decided SkipMinigames switch (default off).
using System;
using System.Collections;
using System.Linq;
using CMS.UI.Windows;
using CMS21Together.ClientSide.Data;
using CMS21Together.ClientSide.Data.Garage.Car;
using CMS21Together.Shared;
using CMS21Together.Shared.Data.Vanilla.Cars;
using CMS21Together.Shared.Features;
using CMS21Together.Shared.Locks;
using HarmonyLib;
using CMS.Containers;
using CMS.PartModules;
using CMS.UI.Logic.Tune;
using UnhollowerBaseLib;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CMS21Together.ClientSide.Features.Stations;

internal static class StationCars
{
	public static CarLoader Loader(int id)
	{
		var loaders = GameData.Instance?.carLoaders;
		return loaders != null && id >= 0 && id < loaders.Length ? loaders[id] : null;
	}

	public static NewCarData Data(CarLoader loader)
	{
		var index = Helper.GetIndexFromCarLoaderName(loader.name);
		// Through the reader: the array class can cache the wrong NewCarData stride (see CarDataReader).
		return CarDataReader.At(Singleton<GameManager>.Instance.GameDataManager.CurrentProfileData.carsInGarage, index);
	}
}

/// <summary>The dyno runs locally; its measured result is the same for everyone. One dyno run at a time.</summary>
public sealed class DynoFeature : GameFeature
{
	private static DynoFeature instance;
	private static CarLoader running;

	public override string Id => FeatureCatalog.Dyno;

	protected override void Patch()
	{
		instance = this;
		Prefix(Target(typeof(DynoManager), nameof(DynoManager.RunDyno)), nameof(Run), Priority.First);
		Postfix(Target(typeof(CarLoader), nameof(CarLoader.MeasurePower)), nameof(Measured));
		Prefix(Target(typeof(DynoManager), nameof(DynoManager.CloseDyno)), nameof(Closing));
	}

	protected override void Stopped() => running = null;

	private static bool Run(DynoManager __instance)
	{
		if (!Claim(LockKeys.Tool("dyno"))) return false;
		running = LocalAction ? __instance.CarLoader : null;
		return true;
	}

	private static void Measured(CarLoader __instance)
	{
		if (running == null || __instance == null || __instance.Pointer != running.Pointer) return;
		running = null;
		Publish(__instance);
	}

	// Fallback: the run was closed before the measurement was seen.
	private static void Closing(DynoManager __instance)
	{
		if (running == null) return;
		var loader = running;
		running = null;
		Publish(loader);
	}

	private static void Publish(CarLoader loader)
	{
		var id = CarLoaderRegistry.IdOf(loader);
		if (id < 0) return;
		var e = loader.EngineData;
		instance.Send(FeatureMessageKind.State, "car/" + id, p =>
		{
			p.Write(id);
			p.Write(e.isElectric);
			foreach (var v in new[] { e.idleRpm, e.idleRpmTorque, e.idleRpmCurveBias, e.peakRpm, e.peakRpmTorque, e.peakRpmCurveBias, e.maxRpm,
				         e.inertia, e.engineFrictionTorque, e.engineFrictionRotational, e.engineFrictionViscous, e.limiterTriggerRpm, e.tuningValue })
				p.Write(v);
			p.Write(e.measured);
			p.Write(loader.MeasuredDragIndex);
		});
	}

	protected override void Receive(FeatureMessage message)
	{
		using var p = Payload(message);
		var loader = StationCars.Loader(p.ReadInt());
		if (loader == null || string.IsNullOrEmpty(loader.carToLoad)) return;
		var e = loader.EngineData;
		e.isElectric = p.Read<bool>();
		e.idleRpm = p.Read<float>();
		e.idleRpmTorque = p.Read<float>();
		e.idleRpmCurveBias = p.Read<float>();
		e.peakRpm = p.Read<float>();
		e.peakRpmTorque = p.Read<float>();
		e.peakRpmCurveBias = p.Read<float>();
		e.maxRpm = p.Read<float>();
		e.inertia = p.Read<float>();
		e.engineFrictionTorque = p.Read<float>();
		e.engineFrictionRotational = p.Read<float>();
		e.engineFrictionViscous = p.Read<float>();
		e.limiterTriggerRpm = p.Read<float>();
		e.tuningValue = p.Read<float>();
		e.measured = p.Read<bool>();
		var drag = p.ReadInt();
		Remote(() =>
		{
			loader.EngineData = e;
			loader.MeasuredDragIndex = drag;
		});
	}
}

/// <summary>Headlamp alignment: the minigame is local; the final values are the same for everyone.</summary>
public sealed class HeadlampAlignmentFeature : GameFeature
{
	private static HeadlampAlignmentFeature instance;
	private static bool open;

	public override string Id => FeatureCatalog.HeadlampAlignment;

	protected override void Patch()
	{
		instance = this;
		Prefix(Target(typeof(LampAlignmentWindow), nameof(LampAlignmentWindow.Show)), nameof(Opening), Priority.First);
		// Not HideAction(): IL2CPP folded it with 38 other windows' HideAction/Close into one native function, so a
		// patch on it ran for every one of them with a foreign __instance (the crash when closing a window in a car).
		Prefix(Target(typeof(LampAlignmentWindow), nameof(LampAlignmentWindow.Hide), typeof(bool)), nameof(Closing));
	}

	private static bool Opening(ref bool __result)
	{
		if (Claim(LockKeys.Tool("headlamp")))
		{
			open = true;
			return true;
		}
		__result = false;
		return false;
	}

	private static void Closing(LampAlignmentWindow __instance)
	{
		if (!open) return;
		open = false;
		if (!LocalAction || __instance == null) return;
		var loader = __instance.carLoader;
		var id = loader != null ? CarLoaderRegistry.IdOf(loader) : -1;
		if (id < 0) return;
		var l = loader.HeadlampLeftAlignment;
		var r = loader.HeadlampRightAlignment;
		instance.Send(FeatureMessageKind.State, "car/" + id, p =>
		{
			p.Write(id);
			p.Write(l.Horizontal);
			p.Write(l.Vertical);
			p.Write(r.Horizontal);
			p.Write(r.Vertical);
		});
	}

	protected override void Receive(FeatureMessage message)
	{
		using var p = Payload(message);
		var loader = StationCars.Loader(p.ReadInt());
		if (loader == null || string.IsNullOrEmpty(loader.carToLoad)) return;
		var left = new HeadlampAlignment { Horizontal = p.Read<float>(), Vertical = p.Read<float>() };
		var right = new HeadlampAlignment { Horizontal = p.Read<float>(), Vertical = p.Read<float>() };
		Remote(() =>
		{
			loader.HeadlampLeftAlignment = left;
			loader.HeadlampRightAlignment = right;
		});
	}
}

/// <summary>Wheel alignment: the minigame is local; the final values are the same for everyone.</summary>
public sealed class WheelAlignmentFeature : GameFeature
{
	private static WheelAlignmentFeature instance;
	private static bool open;

	public override string Id => FeatureCatalog.WheelAlignment;

	protected override void Patch()
	{
		instance = this;
		Prefix(Target(typeof(WheelsAlignmentWindow), nameof(WheelsAlignmentWindow.Show)), nameof(Opening), Priority.First);
		// Hide(bool), not the shared HideAction(): see HeadlampAlignmentFeature.Patch.
		Prefix(Target(typeof(WheelsAlignmentWindow), nameof(WheelsAlignmentWindow.Hide), typeof(bool)), nameof(Closing));
	}

	private static bool Opening(ref bool __result)
	{
		if (Claim(LockKeys.Tool("alignment")))
		{
			open = true;
			return true;
		}
		__result = false;
		return false;
	}

	private static void Closing(WheelsAlignmentWindow __instance)
	{
		if (!open) return;
		open = false;
		var loader = __instance != null ? __instance.carLoader : null;
		if (!LocalAction || loader == null) return;
		var id = CarLoaderRegistry.IdOf(loader);
		var a = loader.WheelsAlignment;
		if (id < 0) return;
		instance.Send(FeatureMessageKind.State, "car/" + id, p =>
		{
			p.Write(id);
			p.Write(a.FL);
			p.Write(a.FR);
			p.Write(a.RL);
			p.Write(a.RR);
		});
	}

	protected override void Receive(FeatureMessage message)
	{
		using var p = Payload(message);
		var loader = StationCars.Loader(p.ReadInt());
		if (loader == null || string.IsNullOrEmpty(loader.carToLoad)) return;
		var fl = p.Read<float>();
		var fr = p.Read<float>();
		var rl = p.Read<float>();
		var rr = p.Read<float>();
		Remote(() =>
		{
			var a = loader.WheelsAlignment;
			a.FL = fl;
			a.FR = fr;
			a.RL = rl;
			a.RR = rr;
			loader.WheelsAlignment = a;
		});
	}
}

/// <summary>ECU, carburettor and gearbox tuning: the tuning screens are local; the applied result is the same for everyone.</summary>
public sealed class TuningFeature : GameFeature
{
	private const byte Ecu = 1;
	private const byte Carb = 2;
	private const byte Gearbox = 3;

	private static TuningFeature instance;

	public override string Id => FeatureCatalog.Tuning;

	protected override void Patch()
	{
		instance = this;
		var ecu = Target(typeof(EcuTuning), nameof(EcuTuning.ApplyAction));
		var carb = Target(typeof(CarbTuning), nameof(CarbTuning.ApplyAction));
		var gearbox = Target(typeof(GearboxTab), nameof(GearboxTab.ApplyAction));
		Prefix(ecu, nameof(GateEcu), Priority.First);
		Prefix(carb, nameof(GateCarb), Priority.First);
		Prefix(gearbox, nameof(GateGearbox), Priority.First);
		Postfix(ecu, nameof(EcuApplied));
		Postfix(carb, nameof(CarbApplied));
		Postfix(gearbox, nameof(GearboxApplied));
	}

	private static bool GateCar(CarLoader loader)
	{
		var id = loader != null ? CarLoaderRegistry.IdOf(loader) : -1;
		return id < 0 || Claim(LockKeys.Car(id));
	}

	private static bool GateEcu(EcuTuning __instance) => GateCar(__instance?.carLoader);
	private static bool GateCarb(CarbTuning __instance) => GateCar(__instance?.carLoader);
	private static bool GateGearbox(GearboxTab __instance) => GateCar(__instance?.carLoader);

	private static void WriteTuning(Packet p, TuningData data)
	{
		p.Write(data.IsTuned);
		var values = data.Values;
		var count = values?.Length ?? 0;
		p.Write(count);
		for (var i = 0; i < count; i++) p.Write((int)values[i]);
		p.Write(data.TuningValue);
	}

	private static (bool tuned, short[] values, float value) ReadTuning(Packet p)
	{
		var tuned = p.Read<bool>();
		var count = p.ReadInt();
		if (count < 0 || count > 64) throw new Shared.Net.NetFormatException($"Tuning with {count} values.");
		var values = new short[count];
		for (var i = 0; i < count; i++) values[i] = (short)p.ReadInt();
		return (tuned, values, p.Read<float>());
	}

	private static Il2CppStructArray<float> Floats(float[] values)
	{
		var array = new Il2CppStructArray<float>(values.Length);
		for (var i = 0; i < values.Length; i++) array[i] = values[i];
		return array;
	}

	internal static Il2CppStructArray<short> Shorts(short value, int count)
	{
		var array = new Il2CppStructArray<short>(count);
		for (var i = 0; i < count; i++) array[i] = value;
		return array;
	}

	private static Il2CppStructArray<short> ToGame(short[] values)
	{
		var array = new Il2CppStructArray<short>(values.Length);
		for (var i = 0; i < values.Length; i++) array[i] = values[i];
		return array;
	}

	private static int Prepare(CarLoader loader)
	{
		if (!LocalAction || loader == null) return -1;
		var id = CarLoaderRegistry.IdOf(loader);
		if (id >= 0) loader.SaveCarToFile();
		return id;
	}

	private static void EcuApplied(EcuTuning __instance, bool __runOriginal)
	{
		if (!__runOriginal || __instance == null) return;
		var loader = __instance.carLoader;
		var id = Prepare(loader);
		if (id < 0) return;
		var data = StationCars.Data(loader).ecuData;
		var stage = __instance.currentStage;
		instance.Send(FeatureMessageKind.State, $"car/{id}/ecu", p =>
		{
			p.Write(id);
			p.Write((int)Ecu);
			p.Write((int)stage);
			WriteTuning(p, data);
		});
	}

	private static void CarbApplied(CarbTuning __instance, bool __runOriginal)
	{
		if (!__runOriginal || __instance == null) return;
		var loader = __instance.carLoader;
		var id = Prepare(loader);
		if (id < 0) return;
		var selected = __instance.selectedCarb;
		var parts = StationCars.Data(loader).PartData;
		if (parts == null) return;
		// Every carburettor's tuning: the applied one is among them, and unchanged ones cost a few bytes.
		for (var i = 0; i < parts.Count; i++)
		{
			var part = parts[i];
			if (part == null || string.IsNullOrEmpty(part.Path) || part.TuningData == null || part.TuningData.Values == null || part.TuningData.Values.Length == 0) continue;
			var path = part.Path;
			var data = part.TuningData;
			instance.Send(FeatureMessageKind.State, $"car/{id}/carb/{path}", p =>
			{
				p.Write(id);
				p.Write((int)Carb);
				p.Write((int)selected);
				p.Write(path);
				WriteTuning(p, data);
			});
		}
	}

	private static void GearboxApplied(GearboxTab __instance, bool __runOriginal)
	{
		if (!__runOriginal || __instance == null) return;
		var loader = __instance.carLoader;
		var id = Prepare(loader);
		if (id < 0) return;
		var data = StationCars.Data(loader);
		var final = data.finalDriveRatio;
		var gears = data.gearRatio?.ToArray() ?? Array.Empty<float>();
		instance.Send(FeatureMessageKind.State, $"car/{id}/gearbox", p =>
		{
			p.Write(id);
			p.Write((int)Gearbox);
			p.Write(final);
			p.Write(gears.Length);
			foreach (var g in gears) p.Write(g);
		});
	}

	protected override void Receive(FeatureMessage message)
	{
		using var p = Payload(message);
		var loader = StationCars.Loader(p.ReadInt());
		var kind = p.ReadInt();
		if (loader == null || string.IsNullOrEmpty(loader.carToLoad)) return;
		var car = StationCars.Data(loader);
		switch (kind)
		{
			case Ecu:
			{
				var stage = (byte)p.ReadInt();
				var (tuned, values, value) = ReadTuning(p);
				Remote(() =>
				{
					var data = car.ecuData ?? new TuningData();
					data.IsTuned = tuned;
					data.Values = ToGame(values);
					data.TuningValue = value;
					car.ecuData = data;
					var modules = loader.root != null ? loader.root.GetComponentsInChildren<EcuModule>(true) : null;
					var ecu = modules?.FirstOrDefault(m => m != null && m.Stage == stage) ?? (modules?.Length == 1 ? modules[0] : null);
					if (ecu == null) return;
					if (ecu.Stage != stage) ecu.SetStage(stage);
					var live = new TuningData { IsTuned = tuned, Values = ToGame(values), TuningValue = value };
					if (tuned) ecu.Tune(live.Values, value);
					ecu.CopyDataFrom(ref live);
				});
				break;
			}
			case Carb:
			{
				var selected = p.ReadInt();
				var path = p.Read<string>();
				var (tuned, values, value) = ReadTuning(p);
				Remote(() =>
				{
					var parts = car.PartData;
					if (parts == null) return;
					for (var i = 0; i < parts.Count; i++)
					{
						var part = parts[i];
						if (part == null || part.Path != path) continue;
						var data = part.TuningData ?? new TuningData();
						data.IsTuned = tuned;
						data.Values = ToGame(values);
						data.TuningValue = value;
						part.TuningData = data;
						break;
					}
					var modules = loader.root != null ? loader.root.GetComponentsInChildren<CarbModule>(true) : null;
					if (modules == null || modules.Length == 0) return;
					var carb = modules.FirstOrDefault(m => m != null && m.PartScript != null && m.PartScript.GetGameObjectPathWithoutRoot() == path)
					           ?? (selected >= 0 && selected < modules.Length ? modules[selected] : null);
					if (carb == null) return;
					var live = new TuningData { IsTuned = tuned, Values = ToGame(values), TuningValue = value };
					if (tuned) carb.Tune(live.Values, value);
					carb.CopyDataFrom(ref live);
				});
				break;
			}
			case Gearbox:
			{
				var final = p.Read<float>();
				var count = p.ReadInt();
				if (count < 0 || count > 32) throw new Shared.Net.NetFormatException($"Gearbox with {count} gears.");
				var gears = new float[count];
				for (var i = 0; i < count; i++) gears[i] = p.Read<float>();
				Remote(() =>
				{
					car.finalDriveRatio = final;
					car.gearRatio = Floats(gears);
					if (loader.root == null) return;
					foreach (var gearbox in loader.root.GetComponentsInChildren<GearboxHandle>(true))
					{
						gearbox.finalDriveRatio = final;
						gearbox.gearRatio = Floats(gears);
					}
				});
				break;
			}
		}
	}
}

/// <summary>
/// Skips the station minigames (SkipMinigames, default off = vanilla): headlamp and wheel alignment end aligned,
/// wheels end balanced, tuning bars jump to the best values. The result then syncs like a played minigame.
/// </summary>
public sealed class SkipMinigamesFeature : GameFeature
{
	public override string Id => FeatureCatalog.SkipMinigames;

	protected override void Patch()
	{
		Postfix(Target(typeof(LampAlignmentWindow), nameof(LampAlignmentWindow.Show)), nameof(LampShown));
		Postfix(Target(typeof(WheelsAlignmentWindow), nameof(WheelsAlignmentWindow.Show)), nameof(WheelsShown));
		Postfix(Target(typeof(WheelBalanceWindow), nameof(WheelBalanceWindow.StartMiniGame)), nameof(BalanceStarted));
		Postfix(Target(typeof(TuningBar), nameof(TuningBar.IncrementValue)), nameof(BarChanged));
		Postfix(Target(typeof(TuningBar), nameof(TuningBar.DecrementValue)), nameof(BarChanged));
	}

	private static IEnumerator Later(Action action)
	{
		yield return new WaitForSeconds(0.3f);
		try
		{
			action();
		}
		catch (Exception ex)
		{
			Log.Warn($"[Features] Skipping a minigame failed: {ex.Message}");
		}
	}

	private static void LampShown(LampAlignmentWindow __instance, bool __result)
	{
		if (!__result || __instance == null || __instance.carLoader == null) return;
		var loader = __instance.carLoader;
		loader.HeadlampLeftAlignment = new HeadlampAlignment { Horizontal = 0, Vertical = 0 };
		loader.HeadlampRightAlignment = new HeadlampAlignment { Horizontal = 0, Vertical = 0 };
		MelonCoroutines.Start(Later(__instance.HideAction));
	}

	private static void WheelsShown(WheelsAlignmentWindow __instance, bool __result)
	{
		var loader = __instance != null ? __instance.carLoader : null;
		if (!__result || loader == null) return;
		var a = loader.WheelsAlignment;
		a.FL = a.FR = a.RL = a.RR = 0f;
		loader.WheelsAlignment = a;
		MelonCoroutines.Start(Later(__instance.HideAction));
	}

	private static void BalanceStarted(WheelBalanceWindow __instance)
	{
		if (__instance == null) return;
		MelonCoroutines.Start(Later(() =>
		{
			var balancer = Object.FindObjectOfType<global::WheelBalancerLogic>();
			var group = balancer?.GetGroupOnWheelBalancer();
			if (group?.ItemList != null)
				foreach (var item in group.ItemList)
				{
					if (item == null) continue;
					var wheel = item.WheelData;
					wheel.IsBalanced = true;
					item.WheelData = wheel;
				}
			__instance.CancelAction();
			if (balancer != null) balancer.balanceCanceled = false;
		}));
	}

	private static void BarChanged(TuningBar __instance)
	{
		if (__instance == null) return;
		if (__instance.carbTuning)
		{
			var carb = Object.FindObjectOfType<CarbTuning>();
			if (carb == null) return;
			carb.SetBars(TuningFeature.Shorts(2, 3));
			carb.currentTune = carb.CalcTuningValue();
			carb.UpdateTuningText();
			return;
		}
		var ecu = Object.FindObjectOfType<EcuTuning>();
		if (ecu == null) return;
		ecu.SetBars(TuningFeature.Shorts(6, 5));
		ecu.currentTune = ecu.CalcTuningValue();
		ecu.UpdateTuningText();
	}
}