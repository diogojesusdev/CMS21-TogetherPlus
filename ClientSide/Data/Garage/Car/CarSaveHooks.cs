using System;
using System.Collections.Generic;
using CMS21Together.Shared;
using CMS21Together.Shared.Data.Vanilla.Cars;
using CMS21Together.Shared.Safety;
using HarmonyLib;

namespace CMS21Together.ClientSide.Data.Garage.Car;

/// <summary>
/// Catches the car the game itself writes when it saves a garage slot.
/// <para>
/// A job car never turned up in <c>ProfileData.carsInGarage</c>: every slot stayed empty for the whole 20 second
/// window the mod waited (rc27-rc29 logs), so polling that array could not work and the waiting coroutine outlived
/// the garage. Watching the game's own save call is exact and immediate instead: whatever CarLoader.SaveCarToFile
/// ends up writing passes through here, for a customer car as much as any other.
/// </para>
/// <para>
/// The car is converted to the mod's own type straight away. Nothing keeps a reference to the game's object, which
/// would otherwise go stale (or be collected) before the waiting coroutine looks at it.
/// </para>
/// </summary>
[HarmonyPatch]
public static class CarSaveHooks
{
	private sealed class Saved
	{
		public ModNewCarData Car;
		public float At;
	}

	private static readonly Dictionary<int, Saved> ByGarageSlot = new();
	private static readonly object Gate = new object();

	/// <summary>A capture older than this is from a previous car and is ignored.</summary>
	private const float KeepSeconds = 60f;

	public static void Reset()
	{
		lock (Gate) ByGarageSlot.Clear();
	}

	[HarmonyPatch(typeof(GameDataManager), nameof(GameDataManager.SaveCar))]
	[HarmonyPostfix]
	public static void SaveCarHook(NewCarData carData, int index, bool toParking)
	{
		if (!toParking) Record(carData, index);
	}

	[HarmonyPatch(typeof(GameDataManager), nameof(GameDataManager.SaveCarInGarage))]
	[HarmonyPostfix]
	public static void SaveCarInGarageHook(NewCarData carData, int index) => Record(carData, index);

	private static void Record(NewCarData carData, int index)
	{
		if (carData == null || index < 0) return;
		if (!Client.Instance.isConnected) return;

		// The conversion is the risky part (it walks the game's lists), so it is guarded and happens once, here.
		var converted = Guard.Try($"read the car the game saved in garage slot {index}",
			() => string.IsNullOrEmpty(carData.carToLoad) ? null : new ModNewCarData(carData, 0, carData.orderConnection));
		if (converted == null) return;

		lock (Gate) ByGarageSlot[index] = new Saved { Car = converted, At = UnityEngine.Time.unscaledTime };
	}

	/// <summary>The car the game last saved into <paramref name="index"/>, when it is the one being waited for.</summary>
	public static ModNewCarData Take(int index, string carName)
	{
		lock (Gate)
		{
			if (!ByGarageSlot.TryGetValue(index, out var saved)) return null;
			if (UnityEngine.Time.unscaledTime - saved.At > KeepSeconds) { ByGarageSlot.Remove(index); return null; }
			if (!string.Equals(saved.Car.carToLoad, carName, StringComparison.Ordinal)) return null;
			ByGarageSlot.Remove(index);
			return saved.Car;
		}
	}
}
