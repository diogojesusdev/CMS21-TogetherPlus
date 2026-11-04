using System.Collections.Generic;
using System.Linq;

namespace CMS21Together.Shared.Features;

/// <summary>One switchable feature of Phase 6b (plan §3.7, ported from Together Fixes by TogetherFixer).</summary>
public sealed class FeatureInfo
{
	public FeatureInfo(ushort index, string id, bool defaultOn, string description)
	{
		Index = index;
		Id = id;
		DefaultOn = defaultOn;
		Description = description;
	}

	/// <summary>Position in the catalog; feature messages carry it (host and players run the same build).</summary>
	public ushort Index { get; }

	/// <summary>Preference name under <c>[TogetherPlus]</c>.</summary>
	public string Id { get; }

	public bool DefaultOn { get; }
	public string Description { get; }
}

/// <summary>Every switch of Phase 6b. All on by default except <see cref="SkipMinigames"/> (vanilla minigames).</summary>
public static class FeatureCatalog
{
	public const string EngineStand2 = "EngineStand2";
	public const string BatteryCharger = "BatteryCharger";
	public const string BrakeLathe = "BrakeLathe";
	public const string Dyno = "Dyno";
	public const string HeadlampAlignment = "HeadlampAlignment";
	public const string WheelAlignment = "WheelAlignment";
	public const string Tuning = "Tuning";
	public const string SkipMinigames = "SkipMinigames";
	public const string Parking = "Parking";
	public const string GhostCarCleanup = "GhostCarCleanup";
	public const string QolParkingCompat = "QolParkingCompat";
	public const string SceneReturnSync = "SceneReturnSync";
	public const string WindowTint = "WindowTint";
	public const string LicensePlates = "LicensePlates";
	public const string TuningParts = "TuningParts";
	public const string WheelVisuals = "WheelVisuals";
	public const string ItemPaint = "ItemPaint";
	public const string BulkSell = "BulkSell";
	public const string WarehouseBulkMove = "WarehouseBulkMove";
	public const string ExaminedSync = "ExaminedSync";
	public const string WorkshopContentSync = "WorkshopContentSync";
	public const string SteamInvite = "SteamInvite";
	public const string LiftReset = "LiftReset";

	public static readonly IReadOnlyList<FeatureInfo> All = Build(
		(EngineStand2, true, "Sync the second engine stand"),
		(BatteryCharger, true, "Sync the battery charger"),
		(BrakeLathe, true, "Sync the brake lathe"),
		(Dyno, true, "Sync the dyno (one player per run)"),
		(HeadlampAlignment, true, "Sync headlamp alignment"),
		(WheelAlignment, true, "Sync wheel alignment"),
		(Tuning, true, "Sync ECU, carburettor and gearbox tuning"),
		(SkipMinigames, false, "Skip the station minigames (off = vanilla)"),
		(Parking, true, "Authoritative parking: one car per parking space"),
		(GhostCarCleanup, true, "Remove cars that exist only on this player's game"),
		(QolParkingCompat, true, "Turn off unsynced extra parking from QoL mods in a session"),
		(SceneReturnSync, true, "Bring cars bought or changed while away into the garage for everyone"),
		(WindowTint, true, "Sync window tint"),
		(LicensePlates, true, "Sync licence plates"),
		(TuningParts, true, "Sync tuning parts"),
		(WheelVisuals, true, "Sync wheel visuals"),
		(ItemPaint, true, "Sync painted inventory items"),
		(BulkSell, true, "Bulk sell from the inventory (paid once by the host)"),
		(WarehouseBulkMove, true, "Sync bulk moves between inventory and warehouse"),
		(ExaminedSync, true, "Sync which parts were examined"),
		(WorkshopContentSync, true, "Check workshop and DLC content on join"),
		(SteamInvite, true, "Steam invites (send and accept)"),
		(LiftReset, true, "F4 resets a stuck lifter for everyone"));

	private static readonly Dictionary<string, FeatureInfo> ById = All.ToDictionary(f => f.Id);

	private static IReadOnlyList<FeatureInfo> Build(params (string id, bool on, string description)[] items) =>
		items.Select((item, i) => new FeatureInfo((ushort)i, item.id, item.on, item.description)).ToList();

	public static FeatureInfo Find(string id) => id != null && ById.TryGetValue(id, out var info) ? info : null;

	public static FeatureInfo At(ushort index) => index < All.Count ? All[index] : null;

	/// <summary>The defaults, as a config.</summary>
	public static Dictionary<string, bool> Defaults() => All.ToDictionary(f => f.Id, f => f.DefaultOn);
}