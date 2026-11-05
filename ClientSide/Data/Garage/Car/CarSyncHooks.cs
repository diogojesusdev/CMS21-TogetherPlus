using CMS21Together.ClientSide.Data.Handle;
using HarmonyLib;
using MelonLoader;
using CMS21Together.Shared;
using CMS21Together.Shared.Locks;

namespace CMS21Together.ClientSide.Data.Garage.Car;

[HarmonyPatch]
public static class CarSyncHooks
{
	public static bool listenToChangePosition = true;

	[HarmonyPatch(typeof(CarLoader), nameof(CarLoader.SwitchCarPart), typeof(string))]
	[HarmonyPostfix]
	public static void SwitchCarPartHook(string name, CarLoader __instance)
	{
		if (!Client.Instance.isConnected || ActionGate.ApplyingServerState) return;

		var carLoaderID = CarLoaderRegistry.IdOf(__instance);
		var car = ClientData.Instance.loadedCars[carLoaderID];

		if (PartUpdateHooks.FindBodyPartInDictionary(car, name, out var key))
		{
			var part = car.CarPartInfo.BodyPartsReferences[key];
			MelonCoroutines.Start(PartUpdateHooks.SendBodyPart(part, key, carLoaderID));
		}
	}

	[HarmonyPatch(typeof(CarLoader), nameof(CarLoader.ChangePosition), typeof(int))]
	[HarmonyPrefix]
	public static bool ChangePositionHook(int no, CarLoader __instance)
	{
		if (!Client.Instance.isConnected || !listenToChangePosition)
		{
			listenToChangePosition = true;
			return true;
		}

		var carLoaderID = CarLoaderRegistry.IdOf(__instance);
		Log.Info($"[Car] Local move of {__instance.carToLoad} (loader {carLoaderID}) to place {no}.");
		if (! ClientData.Instance.loadedCars.ContainsKey(carLoaderID))
			return true;
		if (no == -1)
			return false;

		// Never move a car while someone else works on it (checked only in play, not while the garage loads).
		if (GameLoadHook.IsGameReady())
		{
			var holder = ActionGate.BlockedBy(LockKeys.Car(carLoaderID));
			if (holder != 0)
			{
				ActionGate.ShowDenied(holder);
				return false;
			}
		}

		var car = ClientData.Instance.loadedCars[carLoaderID];
		car.carPosition = no;
		
		ClientSend.CarPositionPacket(carLoaderID, no);
		return true;
	}
}