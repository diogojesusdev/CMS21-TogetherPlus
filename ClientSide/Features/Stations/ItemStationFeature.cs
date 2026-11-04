// Pattern based on BatteryChargerSync.cs / BrakeLatheSync.cs from FixForTogether by TogetherFixer
// (https://github.com/Stevii001100/Fix-For-CMS21-Together), custom non-commercial licence.
// Modified for Together+: feature channel, station lock, host-authoritative inventory (remote side frozen).
using System;
using System.Collections.Generic;
using System.Reflection;
using CMS21Together.Shared.Data.Vanilla;
using CMS21Together.Shared.Features;
using CMS21Together.Shared.Locks;
using HarmonyLib;
using Object = UnityEngine.Object;

namespace CMS21Together.ClientSide.Features.Stations;

/// <summary>A station that holds one item (put on with a set method, taken off with a clear method). One player at a time.</summary>
public abstract class ItemStationFeature : GameFeature
{
	private const string Key = "item";

	// Harmony handlers are static: the patched method tells which station it is.
	private static readonly Dictionary<MethodBase, ItemStationFeature> ByMethod = new();

	protected abstract string LockName { get; }
	protected abstract MethodInfo SetMethod();
	protected abstract MethodInfo ClearMethod();
	protected abstract Object Find();
	protected abstract bool IsEmpty(Object station);
	protected abstract void SetItem(Object station, Item item);
	protected abstract void ClearItem(Object station);

	protected override void Patch()
	{
		var set = SetMethod();
		var clear = ClearMethod();
		ByMethod[set] = this;
		ByMethod[clear] = this;
		Prefix(set, nameof(StationGate), Priority.First);
		Prefix(clear, nameof(StationGate), Priority.First);
		Postfix(set, nameof(StationAfterSet));
		Postfix(clear, nameof(StationAfterClear));
	}

	protected override void Stopped()
	{
		var mine = new List<MethodBase>();
		foreach (var pair in ByMethod)
			if (pair.Value == this)
				mine.Add(pair.Key);
		foreach (var method in mine) ByMethod.Remove(method);
	}

	private static ItemStationFeature Of(MethodBase method) => method != null && ByMethod.TryGetValue(method, out var f) && f.On ? f : null;

	private static bool StationGate(MethodBase __originalMethod)
	{
		var feature = Of(__originalMethod);
		return feature == null || Claim(LockKeys.Tool(feature.LockName));
	}

	private static void StationAfterSet(MethodBase __originalMethod, Item item, bool __runOriginal)
	{
		var feature = Of(__originalMethod);
		if (feature == null || !__runOriginal || !LocalAction || item == null) return;
		var modItem = new ModItem(item);
		feature.Send(FeatureMessageKind.State, Key, p => p.Write(modItem));
	}

	private static void StationAfterClear(MethodBase __originalMethod, bool __runOriginal)
	{
		var feature = Of(__originalMethod);
		if (feature == null || !__runOriginal || !LocalAction) return;
		feature.Send(FeatureMessageKind.Clear, Key);
	}

	protected override void Receive(FeatureMessage message)
	{
		var station = Find();
		if (station == null) return;
		if (message.Kind == FeatureMessageKind.Clear)
		{
			if (!IsEmpty(station)) Remote(() => ClearItem(station));
			return;
		}
		using var payload = Payload(message);
		var item = payload.Read<ModItem>()?.ToGame();
		if (item == null) return;
		Remote(() =>
		{
			if (!IsEmpty(station)) ClearItem(station);
			SetItem(station, item);
		});
	}
}

public sealed class BatteryChargerFeature : ItemStationFeature
{
	public override string Id => FeatureCatalog.BatteryCharger;
	protected override string LockName => "batterycharger";
	protected override MethodInfo SetMethod() => Target(typeof(BatteryChargerLogic), nameof(BatteryChargerLogic.SetItemOnBatteryCharger), typeof(Item), typeof(bool));
	protected override MethodInfo ClearMethod() => Target(typeof(BatteryChargerLogic), nameof(BatteryChargerLogic.ClearBatteryCharger));
	protected override Object Find() => Object.FindObjectOfType<BatteryChargerLogic>();
	protected override bool IsEmpty(Object s) => ((BatteryChargerLogic)s).IsEmpty();
	protected override void SetItem(Object s, Item item) => ((BatteryChargerLogic)s).SetItemOnBatteryCharger(item, true);
	protected override void ClearItem(Object s) => ((BatteryChargerLogic)s).ClearBatteryCharger();
}

public sealed class BrakeLatheFeature : ItemStationFeature
{
	public override string Id => FeatureCatalog.BrakeLathe;
	protected override string LockName => "brakelathe";
	protected override MethodInfo SetMethod() => Target(typeof(BrakeLatheLogic), nameof(BrakeLatheLogic.SetItem), typeof(Item), typeof(bool));
	protected override MethodInfo ClearMethod() => Target(typeof(BrakeLatheLogic), nameof(BrakeLatheLogic.Clear));
	protected override Object Find() => Object.FindObjectOfType<BrakeLatheLogic>();
	protected override bool IsEmpty(Object s) => ((BrakeLatheLogic)s).IsEmpty();
	protected override void SetItem(Object s, Item item) => ((BrakeLatheLogic)s).SetItem(item, true);
	protected override void ClearItem(Object s) => ((BrakeLatheLogic)s).Clear();
}