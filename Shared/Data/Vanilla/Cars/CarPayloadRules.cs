using System.Collections.Generic;

namespace CMS21Together.Shared.Data.Vanilla.Cars;

/// <summary>
/// What a car received from another player must look like before the mod builds game objects out of it.
/// <para>
/// A native access violation cannot be caught, so a payload that would make the game walk nonsense has to be refused
/// *before* it reaches the garage: the receiver drops that car and asks for a resync instead (see
/// <c>CarSpawnManager.LoadCarFromServer</c>). Pure .NET so the limits are unit tested.
/// </para>
/// </summary>
public static class CarPayloadRules
{
	/// <summary>Car ids are file names ("car_katagiritamago"); 128 leaves room without accepting a runaway string.</summary>
	public const int MaxNameLength = 128;

	/// <summary>A body has a handful of panels; 256 is far above any real car and catches a corrupt count.</summary>
	public const int MaxBodyParts = 256;

	/// <summary>Every part of a car, wheels and bolts included; the largest cars have a few hundred.</summary>
	public const int MaxParts = 2000;

	public const int MaxWheelSizes = 64;
	public const int MaxGearRatios = 32;

	/// <summary>A car id that can be handed to the car loader.</summary>
	public static bool NameIsUsable(string carToLoad) =>
		!string.IsNullOrWhiteSpace(carToLoad) && carToLoad.Length <= MaxNameLength;

	/// <summary>A count read out of a packet: never negative, never above what a real car can have.</summary>
	public static bool CountIsSane(int count, int max) => count >= 0 && count <= max;

	/// <summary>Every entry must be present: a null entry becomes a null game object the game then walks.</summary>
	public static bool AllPresent<T>(IList<T> items) where T : class
	{
		if (items == null) return true;
		for (var i = 0; i < items.Count; i++)
			if (items[i] == null)
				return false;
		return true;
	}

	/// <summary>An optional array (wheel sizes, gear ratios): null is fine, a plausible length is required.</summary>
	public static bool SizesAreSane<T>(IList<T> items, int max) => items == null || CountIsSane(items.Count, max);

	/// <summary>The reason a car must be refused, or null when it can be applied.</summary>
	public static string Problem(string carToLoad, int bodyParts, int parts, bool allEntriesPresent,
		int wheelSizes, int gearRatios)
	{
		if (!NameIsUsable(carToLoad)) return "it has no usable car name";
		if (!CountIsSane(bodyParts, MaxBodyParts)) return $"it reports {bodyParts} body parts (limit {MaxBodyParts})";
		if (!CountIsSane(parts, MaxParts)) return $"it reports {parts} parts (limit {MaxParts})";
		if (!allEntriesPresent) return "one of its part entries is missing";
		if (!CountIsSane(wheelSizes, MaxWheelSizes)) return $"it reports {wheelSizes} wheel sizes (limit {MaxWheelSizes})";
		if (!CountIsSane(gearRatios, MaxGearRatios)) return $"it reports {gearRatios} gear ratios (limit {MaxGearRatios})";
		return null;
	}
}
