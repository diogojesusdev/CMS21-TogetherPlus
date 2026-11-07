using System.Collections;
using CMS21Together.ClientSide.Data.Handle;
using CMS21Together.Shared;
using CMS21Together.Shared.Data.Vanilla.Cars;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace CMS21Together.ClientSide.Data.Garage.Car;

[HarmonyPatch]
public static class ParkHook
{
	// Never patch GameDataManager.SaveCarInParking / LoadCarInGarage: they take or return a NewCarData, which
	// MelonLoader 0.5.7 shifts by 16 bytes (see PatchRules). The two hooks below stay disabled for that reason too.

	/*[HarmonyPatch(typeof(CarLoader), nameof(GameDataManager.SaveCarInParking))]
	[HarmonyPostfix]
	public static void SaveCarInParkingHook(NewCarData carData, int index, GameDataManager __instance)
	{
		if (!Client.Instance.isConnected || !listen) { listen = true; return;}
		
		MelonCoroutines.Start(AddCarToParkHook(carData, index));
	}*/

	private static IEnumerator AddCarToParkHook(NewCarData carData, int index)
	{
		yield return new WaitForEndOfFrame();
		int i = 0;
		while (i < 10)
		{
			yield return new WaitForSeconds(0.10f);
			i++;
		}
		yield return new WaitForEndOfFrame();
		yield return new WaitForEndOfFrame();
		
		ClientSend.AddCarToParkPacket(new ModNewCarData(carData), index);
	}

	/*[HarmonyPatch(typeof(CarLoader), nameof(GameDataManager.LoadCarInGarage))]
	[HarmonyPostfix]
	public static void LoadCarInGarageHook(int index, GameDataManager __instance)
	{
		if (!Client.Instance.isConnected) return;

		ClientSend.RemoveCarFromParkPacket(index);
	}*/

	/// <summary>
	/// A car parked in the same frame as a debit was bought (Phase 6): if the host refuses the debit, the parking
	/// space gets back what it held before. A bought car reaches the parking list through
	/// <c>CarLoader.SaveCarToFile(index, toParking: true)</c> (its only path to <c>SaveCarInParking</c>), which is
	/// patched here: <c>GameDataManager.SaveCarInParking</c> itself must never be patched, it takes a
	/// <see cref="NewCarData"/> (see <see cref="Shared.Safety.PatchRules"/>).
	/// </summary>
	[HarmonyPatch(typeof(CarLoader), nameof(CarLoader.SaveCarToFile), typeof(int), typeof(bool))]
	[HarmonyPrefix]
	public static void SaveCarToParkingPurchaseHook(int index, bool toParking, CarLoader __instance)
	{
		if (!toParking || index < 0) return;
		if (Client.Instance == null || !Client.Instance.isConnected || ActionGate.ApplyingServerState) return;
		var txn = Player.Purchases.TxnNow();
		if (txn == 0 || __instance == null || string.IsNullOrEmpty(__instance.carToLoad)) return;

		var manager = GameManager.Instance.GameDataManager;
		var previous = manager.LoadCarInParking(index);
		Player.Purchases.Linker.OnRefused(txn, () =>
		{
			using (ActionGate.Apply())
			{
				manager.SaveCarInParking(previous, index);
				CarSlotAudit.Slot("the refused parking purchase", true, index);
			}
			Log.Info($"[ParkHook] The car bought into parking space {index} was not paid; it was taken back.");
		});
	}

	public static IEnumerator AddCarToPark(ModNewCarData car, int index)
	{
		while (!GameLoadHook.IsGameReady())
			yield return new WaitForSeconds(0.25f);
		yield return new WaitForEndOfFrame();

		GameManager.Instance.GameDataManager.SaveCarInParking(car.ToGame(), index);
		CarSlotAudit.Slot("the host's parked car", true, index);
	}

	public static IEnumerator RemoveCarFromPark(int index)
	{
		while (!GameLoadHook.IsGameReady())
			yield return new WaitForSeconds(0.25f);
		yield return new WaitForEndOfFrame();

		// Phase 6b (Parking, as in Together Fixes): the game's own removal, which also updates the parked cars shown.
		if (Features.Features.IsOn(Shared.Features.FeatureCatalog.Parking))
		{
			Features.Garage.ParkingFeature.ApplyRemoved(index);
			yield break;
		}
		GameManager.Instance.GameDataManager.SaveCarInParking(default(NewCarData), index);
	}
}