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
	public static bool listen = true;

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
	/// space gets back what it held before.
	/// </summary>
	[HarmonyPatch(typeof(GameDataManager), nameof(GameDataManager.SaveCarInParking))]
	[HarmonyPrefix]
	public static void SaveCarInParkingPurchaseHook(NewCarData carData, int index, GameDataManager __instance)
	{
		if (!listen)
		{
			// The host's state being applied (AddCarToPark / RemoveCarFromPark below).
			listen = true;
			return;
		}
		if (!Client.Instance.isConnected || ActionGate.ApplyingServerState) return;
		var txn = Player.Purchases.TxnNow();
		if (txn == 0 || carData == null || string.IsNullOrEmpty(carData.carToLoad)) return;

		var previous = __instance.LoadCarInParking(index);
		var manager = __instance;
		Player.Purchases.Linker.OnRefused(txn, () =>
		{
			using (ActionGate.Apply())
			{
				listen = false;
				manager.SaveCarInParking(previous, index);
				listen = true;
			}
			Log.Info($"[ParkHook] The car bought into parking space {index} was not paid; it was taken back.");
		});
	}

	public static IEnumerator AddCarToPark(ModNewCarData car, int index)
	{
		while (!GameLoadHook.IsGameReady())
			yield return new WaitForSeconds(0.25f);
		yield return new WaitForEndOfFrame();

		listen = false;
		GameManager.Instance.GameDataManager.SaveCarInParking(car.ToGame(), index);
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
		listen = false;
		GameManager.Instance.GameDataManager.SaveCarInParking(default(NewCarData), index);
	}
}