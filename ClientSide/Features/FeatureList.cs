using System.Collections.Generic;

namespace CMS21Together.ClientSide.Features;

/// <summary>The Phase 6b features this build carries (each in its own file, each behind its own switch).</summary>
public static class FeatureList
{
	public static IEnumerable<GameFeature> Create() => new GameFeature[]
	{
		new Stations.BatteryChargerFeature(),
		new Stations.BrakeLatheFeature(),
		new Stations.DynoFeature(),
		new Stations.HeadlampAlignmentFeature(),
		new Stations.WheelAlignmentFeature(),
		new Stations.TuningFeature(),
		new Stations.SkipMinigamesFeature(),
		new Inventory.BulkSellFeature(),
		new Inventory.WarehouseFeature(),
		new Visuals.ItemPaintFeature(),
		new Visuals.LicensePlatesFeature(),
		new Visuals.WheelVisualsFeature(),
		new Visuals.WindowTintFeature(),
		new Visuals.TuningPartsFeature(),
		new Visuals.ExaminedFeature(),
		new Garage.LiftResetFeature(),
		new Garage.ParkingFeature(),
		new Garage.QolParkingFeature(),
		new Garage.SceneReturnFeature(),
		new Lobby.SteamInviteFeature(),
		new Lobby.WorkshopContentFeature(),
	};
}