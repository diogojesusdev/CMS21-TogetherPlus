using CMS21Together.ClientSide.Data.Garage.Car;
using CMS21Together.ClientSide.Data.Handle;
using HarmonyLib;

namespace CMS21Together.ClientSide.Data.Garage.Tools;

[HarmonyPatch]
public static class OilBin
{
	public static bool listen = true;
        
	[HarmonyPatch(typeof(CarLoader), nameof(CarLoader.UseOilbin))]
	[HarmonyPrefix]
	public static void UseOilBinPatch(CarLoader __instance, bool __runOriginal)
	{
		// A gate (StationGates) cancelled the action: nothing happened, nothing to send.
		if (!__runOriginal) return;
		if(!Client.Instance.isConnected) return;
		if (!listen) { listen = true; return;}
		
		int carLoaderID = CarLoaderRegistry.IdOf(__instance);
		ClientSend.SendOilBin(carLoaderID);
	}
}