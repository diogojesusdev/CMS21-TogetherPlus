// Based on PaintingFix.cs / 129VanillaItemPaintPacket.cs, LicensePlateInventorySync.cs, WindowTintSync.cs,
// TuningPartFix.cs and ExaminedSync.cs from FixForTogether by TogetherFixer
// (https://github.com/Stevii001100/Fix-For-CMS21-Together), custom non-commercial licence.
// Modified for Together+: item changes travel as versioned shared-inventory updates (InventoryOp.Update) instead of
// separate relay packets, so late joiners get them; window tint claims the car before re-sending its panels.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CMS.Managers;
using CMS.UI.Logic;
using CMS.UI.Windows;
using CMS21Together.ClientSide.Data;
using CMS21Together.ClientSide.Data.Garage;
using CMS21Together.ClientSide.Data.Garage.Car;
using CMS21Together.ClientSide.Data.Handle;
using CMS21Together.Shared;
using CMS21Together.Shared.Data;
using CMS21Together.Shared.Data.Vanilla;
using CMS21Together.Shared.Data.Vanilla.Cars;
using CMS21Together.Shared.Features;
using CMS21Together.Shared.Locks;
using UnityEngine;
using PlayerInventory = CMS21Together.ClientSide.Data.Player.Inventory;

namespace CMS21Together.ClientSide.Features.Visuals;

/// <summary>
/// Watches items this player just added or changed for a few seconds: when the part of their data this feature
/// cares about differs from what the shared inventory holds, the item is sent as an update.
/// </summary>
public abstract class ItemWatchFeature : GameFeature
{
	private const float WatchSeconds = 4f;
	private const float CheckEverySeconds = 0.25f;

	private readonly Dictionary<long, float> items = new();
	private readonly Dictionary<long, float> groups = new();
	private float nextCheck;

	private static readonly List<ItemWatchFeature> Active = new();

	/// <summary>The fields this feature syncs, as a comparable string.</summary>
	protected abstract string Signature(ModItem item);

	protected override void Patch()
	{
		Postfix(Target(typeof(global::Inventory), "Add", typeof(Item), typeof(bool)), nameof(ItemAdded));
		Postfix(Target(typeof(global::Inventory), "AddGroup"), nameof(GroupAdded));
		PatchMore();
	}

	protected virtual void PatchMore()
	{
	}

	protected override void Started() => Active.Add(this);

	protected override void Stopped()
	{
		Active.Remove(this);
		items.Clear();
		groups.Clear();
	}

	protected static void WatchItem(long uid, float seconds = WatchSeconds)
	{
		foreach (var feature in Active) feature.items[uid] = Time.unscaledTime + seconds;
	}

	private static void ItemAdded(Item item)
	{
		if (item != null && LocalAction) WatchItem(item.UID);
	}

	private static void GroupAdded(GroupItem group)
	{
		if (group == null || !LocalAction) return;
		foreach (var feature in Active) feature.groups[group.UID] = Time.unscaledTime + WatchSeconds;
	}

	protected internal override void Tick()
	{
		var now = Time.unscaledTime;
		if (now < nextCheck || items.Count + groups.Count == 0) return;
		nextCheck = now + CheckEverySeconds;
		var inventory = GameData.Instance?.localInventory;
		if (inventory == null) return;

		foreach (var uid in items.Keys.ToList())
		{
			var item = inventory.GetItem(uid);
			if (item != null && PlayerInventory.TryGetTracked(uid, out var tracked) && Signature(tracked) != Signature(new ModItem(item)))
				PlayerInventory.Update(item);
			if (now > items[uid]) items.Remove(uid);
		}
		foreach (var uid in groups.Keys.ToList())
		{
			var group = inventory.GetGroup(uid);
			if (group?.ItemList != null && PlayerInventory.TryGetTrackedGroup(uid, out var tracked) && GroupSignature(tracked) != GroupSignature(new ModGroupItem(group)))
				PlayerInventory.UpdateGroup(group);
			if (now > groups[uid]) groups.Remove(uid);
		}
	}

	private string GroupSignature(ModGroupItem group) =>
		string.Join(";", (group?.ItemList ?? new List<ModItem>()).Where(i => i != null).OrderBy(i => i.UID).Select(i => i.UID + ":" + Signature(i)));

	protected static string Num(float value) => value.ToString("0.00", CultureInfo.InvariantCulture);

	protected static string Colour(ModColor c) => c == null ? "-" : $"{Num(c.r)},{Num(c.g)},{Num(c.b)},{Num(c.a)}";
}

/// <summary>Painted inventory items look the same for everyone.</summary>
public sealed class ItemPaintFeature : ItemWatchFeature
{
	public override string Id => FeatureCatalog.ItemPaint;

	protected override string Signature(ModItem i) => $"{i.IsPainted}|{(int)i.PaintType}|{Colour(i.Color)}|{i.IsTinted}|{Colour(i.TintColor)}";

	protected override void PatchMore() =>
		Prefix(Target(typeof(PaintshopManager), nameof(PaintshopManager.MakePaintEffects)), nameof(Painting));

	// The paint shop paints over a few seconds.
	private static void Painting(PaintshopManager __instance)
	{
		var item = __instance != null ? __instance.item : null;
		if (item != null && LocalAction) WatchItem(item.UID, 15f);
	}
}

/// <summary>Licence plate items carry their text to everyone.</summary>
public sealed class LicensePlatesFeature : ItemWatchFeature
{
	public override string Id => FeatureCatalog.LicensePlates;

	protected override string Signature(ModItem i) => i.LPData == null ? "-" : $"{i.LPData.Name}|{i.LPData.Custom}";
}

/// <summary>Wheel data changed in place (balanced, tyre size) is the same for everyone.</summary>
public sealed class WheelVisualsFeature : ItemWatchFeature
{
	public override string Id => FeatureCatalog.WheelVisuals;

	protected override string Signature(ModItem i)
	{
		var w = i.WheelData;
		return w == null ? "-" : $"{w.IsBalanced}|{w.Size}|{w.Width}|{w.Profile}|{w.ET}";
	}
}

/// <summary>
/// A car's wheel width, rim size, tyre size and offset live in the car loader, not in its parts, and the host's copy of the car
/// is the one from when it loaded: a change made by one player (or before a player rejoined) never reached the others.
/// After a local change the four wheels are sent as the car's kept state, so late joiners and resyncs get them too.
/// </summary>
public sealed class WheelDimensionsFeature : GameFeature
{
	private const float CheckEverySeconds = 1f;
	private const int MaxWheels = 8;
	private const int MaxValue = 2000;

	private readonly Dictionary<int, (string car, string signature)> known = new();
	private float nextCheck;

	public override string Id => FeatureCatalog.WheelDimensions;

	protected override void Patch()
	{
	}

	protected override void Stopped() => known.Clear();

	private static string Key(int id) => "car/" + id + "/wheels";

	private static int[] Read(CarLoader loader)
	{
		var wheels = loader?.WheelsData?.Wheels;
		if (wheels == null || wheels.Count == 0 || wheels.Count > MaxWheels) return null;
		var values = new int[wheels.Count * 4];
		for (var i = 0; i < wheels.Count; i++)
		{
			var wheel = wheels[i];
			if (wheel == null) return null;
			values[i * 4] = (int)wheel.Width;
			values[i * 4 + 1] = (int)wheel.Size;
			values[i * 4 + 2] = (int)wheel.Profile;
			values[i * 4 + 3] = wheel.ET;
		}
		return values;
	}

	private static string Signature(int[] values) => string.Join(",", values.Select(v => v.ToString(CultureInfo.InvariantCulture)));

	protected internal override void Tick()
	{
		if (Time.unscaledTime < nextCheck) return;
		nextCheck = Time.unscaledTime + CheckEverySeconds;
		var cars = ClientData.Instance?.loadedCars;
		if (cars == null) return;
		foreach (var id in known.Keys.Where(k => !cars.ContainsKey(k)).ToList()) known.Remove(id);
		foreach (var pair in cars.ToList())
		{
			var car = pair.Value;
			if (car == null || !car.isReady) continue;
			var values = Read(Stations.StationCars.Loader(pair.Key));
			if (values == null) continue;
			var signature = Signature(values);
			// A different car in the same loader starts from what the game loaded.
			if (!known.TryGetValue(pair.Key, out var last) || last.car != car.carID)
			{
				known[pair.Key] = (car.carID, signature);
				continue;
			}
			if (last.signature == signature) continue;
			known[pair.Key] = (car.carID, signature);
			if (!LocalAction) continue;
			var id = pair.Key;
			if (Log.IsEnabled(LogLevel.Info)) Log.Info($"[Wheels] Car {id} {car.carID} wheels changed here: {signature}; sending.");
			Send(FeatureMessageKind.State, Key(id), p =>
			{
				p.Write(id);
				p.Write(values.Length / 4);
				foreach (var value in values) p.Write(value);
			});
		}
	}

	protected override void Receive(FeatureMessage message)
	{
		using var p = Payload(message);
		var id = p.ReadInt();
		var count = p.ReadInt();
		if (count < 1 || count > MaxWheels || count * 4 * sizeof(int) > p.UnreadLength()) return;
		var values = new int[count * 4];
		for (var i = 0; i < values.Length; i++)
		{
			values[i] = p.ReadInt();
			if (values[i] < -MaxValue || values[i] > MaxValue) return;
		}
		var loader = Stations.StationCars.Loader(id);
		var current = Read(loader);
		if (loader == null || current == null || current.Length != values.Length) return;
		if (!ClientData.Instance.loadedCars.TryGetValue(id, out var car) || car == null) return;
		if (Signature(current) == Signature(values)) return;
		Log.Info($"[Wheels] Car {id} {car.carID} wheels set from #{message.From}: {Signature(values)}.");
		Remote(() =>
		{
			for (var i = 0; i < count; i++)
			{
				loader.SetWheelSize(values[i * 4], values[i * 4 + 1], values[i * 4 + 2], (WheelType)i);
				loader.SetET((WheelType)i, values[i * 4 + 3]);
			}
			loader.SetWheelSizes();
		});
		var applied = Read(loader);
		known[id] = (car.carID, applied != null ? Signature(applied) : Signature(values));
	}
}

/// <summary>Window tint: the tinted car's panels are sent to everyone once the tint is applied.</summary>
public sealed class WindowTintFeature : GameFeature
{
	private static CarLoader tinting;

	public override string Id => FeatureCatalog.WindowTint;

	protected override void Patch()
	{
		Prefix(Target(typeof(ToolsMoveManager), nameof(ToolsMoveManager.Use), typeof(IOSpecialType)), nameof(UseTool), HarmonyLib.Priority.First);
		Postfix(Target(typeof(TintingWindow), nameof(TintingWindow.TintAction)), nameof(Tinted));
	}

	protected override void Stopped() => tinting = null;

	private static bool UseTool(ToolsMoveManager __instance, IOSpecialType tool)
	{
		if (tool != IOSpecialType.WindowTint || __instance == null) return true;
		var loader = __instance.GetConnectedCarLoader(tool);
		var id = loader != null ? CarLoaderRegistry.IdOf(loader) : -1;
		if (id >= 0 && !Claim(LockKeys.Car(id))) return false;
		tinting = loader;
		return true;
	}

	private static void Tinted()
	{
		var loader = tinting;
		if (loader == null || !LocalAction) return;
		var id = CarLoaderRegistry.IdOf(loader);
		if (id < 0 || ClientData.Instance == null || !ClientData.Instance.loadedCars.TryGetValue(id, out var car) || car?.CarPartInfo == null) return;
		ActionGate.TryBegin(LockKeys.Car(id));
		foreach (var pair in car.CarPartInfo.BodyPartsReferences)
			if (pair.Value != null)
				ClientSend.BodyPartPacket(new ModCarPart(pair.Value, pair.Key, id), id);
	}
}

/// <summary>Tuning variants of parts (tuned to normal and back) follow the part update.</summary>
public sealed class TuningPartsFeature : GameFeature
{
	private static TuningPartsFeature instance;

	public override string Id => FeatureCatalog.TuningParts;

	protected override void Patch() => instance = this;

	protected override void Stopped() => instance = null;

	/// <summary>Called by the part update before it applies the rest of the part (true = handled here).</summary>
	public static bool PrepareVariant(ModPartScript part, PartScript reference)
	{
		if (instance == null || !instance.On || part == null || reference == null) return false;
		var incoming = part.tunedID;
		var local = reference.tunedID;
		if (string.IsNullOrEmpty(incoming))
		{
			if (!string.IsNullOrEmpty(local) && !string.IsNullOrEmpty(part.id)) reference.TunePart(part.id, false);
			return true;
		}
		if (incoming != local) reference.TunePart(incoming, false);
		return true;
	}
}

/// <summary>Examining a part on a car shows it examined for everyone.</summary>
public sealed class ExaminedFeature : GameFeature
{
	private static ExaminedFeature instance;

	public override string Id => FeatureCatalog.ExaminedSync;

	protected override void Patch()
	{
		instance = this;
		var examine = Target(typeof(PartScript), nameof(PartScript.Examine), typeof(bool));
		Prefix(examine, nameof(Before));
		Postfix(examine, nameof(After));
	}

	private static void Before(PartScript __instance, out bool __state) => __state = __instance != null && __instance.IsExamined;

	private static void After(PartScript __instance, bool __state)
	{
		if (__state || __instance == null || !__instance.IsExamined || !LocalAction) return;
		var key = ActionGate.KeyOf(__instance);
		if (key != null) instance.Send(FeatureMessageKind.State, key);
	}

	protected override void Receive(FeatureMessage message)
	{
		var key = message.Key;
		var data = ClientData.Instance;
		if (data == null || string.IsNullOrEmpty(key)) return;
		foreach (var car in data.loadedCars.Values.ToList())
		{
			var info = car?.CarPartInfo;
			if (info == null) continue;
			foreach (var part in AllParts(info))
			{
				if (part == null || part.IsExamined || ActionGate.KeyOf(part) != key) continue;
				Remote(() => part.IsExamined = true);
				return;
			}
		}
	}

	private static IEnumerable<PartScript> AllParts(ModCarPartInfo info)
	{
		foreach (var list in info.OtherPartsReferences.Values) foreach (var p in list) yield return p;
		foreach (var list in info.SuspensionPartsReferences.Values) foreach (var p in list) yield return p;
		foreach (var p in info.EnginePartsReferences.Values) yield return p;
		foreach (var p in info.DriveshaftPartsReferences.Values) yield return p;
	}
}