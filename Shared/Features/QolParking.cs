namespace CMS21Together.Shared.Features;

/// <summary>
/// Extra parking cars of the QoL mod (CMS2021mod): it shows parked cars in the garage on loaders with
/// <c>placeNo = -parkingIndex - 10</c> (as found by TogetherFixer's QolParkingExtraSync). Those loaders are not in the
/// garage's loader list, so their ids would collide with real cars; in a session they are never synced.
/// </summary>
public static class QolParking
{
	public static bool IsExtraPlace(int placeNo) => placeNo <= -10 && placeNo > -1000;
}