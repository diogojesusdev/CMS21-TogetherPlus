using System;
using System.Collections.Generic;
using System.Linq;
using CMS;
using CMS21Together.Shared.Safety;
using HarmonyLib;
using UnhollowerBaseLib;

namespace CMS21Together.Shared.Data.Vanilla.Cars;

/// <summary>
/// Checks a car slot of the profile right after something writes it, so a damaged car is reported where it is made
/// instead of crashing the game minutes later, when the garbage collector walks it (the menu-return crash).
/// <para>
/// Every reference field of the slot is checked (<see cref="HeapCheck"/>), not just the ones the mod reads: the
/// shifted cars that crashed rc28-rc34 had an empty name, so the old "is it a readable car" check saw an empty slot.
/// A field that is not an object is cleared, so the collector cannot fault on it, and the error names the writer.
/// </para>
/// </summary>
[HarmonyPatch]
public static class CarSlotAudit
{
	private const int ShownFields = 6;
	private static readonly HashSet<string> Reported = new();

	/// <summary>Every car the game saves into the profile passes here (it is the only caller of GameDataManager.SaveCar).</summary>
	[HarmonyPatch(typeof(CarLoader), nameof(CarLoader.SaveCarToFile), typeof(int), typeof(bool))]
	[HarmonyPostfix]
	public static void AfterGameSave(int index, bool toParking) => Slot("the game's car save", toParking, index);

	/// <summary>After <paramref name="writer"/> stored a car in a garage or parking slot of the current profile.</summary>
	public static void Slot(string writer, bool parking, int index)
	{
		Guard.Run("check the car just written", () =>
		{
			var profile = Singleton<GameManager>.Instance?.GameDataManager?.CurrentProfileData;
			if (profile == null) return;
			Check(writer, parking ? profile.carsOnParking : profile.carsInGarage, parking ? "carsOnParking" : "carsInGarage", index);
		});
	}

	/// <summary>Every car of <paramref name="profile"/>; the number of damaged slots.</summary>
	public static int Profile(string writer, ProfileData profile)
	{
		if (profile == null) return 0;
		return Guard.Try("check the profile cars", () =>
			All(writer, profile.carsInGarage, "carsInGarage") + All(writer, profile.carsOnParking, "carsOnParking"));
	}

	private static int All(string writer, Il2CppReferenceArray<NewCarData> cars, string list)
	{
		if (cars == null) return 0;
		var bad = 0;
		var count = CarDataReader.Count(cars);
		for (var i = 0; i < count; i++)
			if (!Check(writer, cars, list, i)) bad++;
		return bad;
	}

	/// <summary>False when the slot held a reference the collector would crash on.</summary>
	private static bool Check(string writer, Il2CppReferenceArray<NewCarData> cars, string list, int index)
	{
		if (cars == null || !CarDataReader.TryLocate(cars, index, out var slot, out var klass, out var size, out var bias, out _)) return true;

		var result = HeapCheck.CheckInline(slot, klass, size, bias, true);
		GC.KeepAlive(cars);
		if (result.Sound) return true;

		var key = $"{list}[{index}] {string.Join(",", result.Bad)}";
		lock (Reported)
			if (!Reported.Add(key)) return false;

		var shown = string.Join("; ", result.Bad.Take(ShownFields)) + (result.Bad.Count > ShownFields ? $"; +{result.Bad.Count - ShownFields} more" : "");
		Log.Error($"[CarAudit] After {writer}, {list}[{index}] held {result.Bad.Count} reference(s) the game's garbage collector would crash on: " +
		          $"{shown}. {result.Cleared} cleared so the game keeps running. Please send this log.");
		return false;
	}
}
