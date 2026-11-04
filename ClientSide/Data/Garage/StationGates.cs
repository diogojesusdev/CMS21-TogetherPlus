using CMS21Together.ClientSide.Data.Garage.Car;
using CMS21Together.ClientSide.Data.Garage.Tools;
using CMS21Together.Shared.Locks;
using HarmonyLib;
using TireC = TireChangerLogic;
using WelderL = WelderLogic;

namespace CMS21Together.ClientSide.Data.Garage;

/// <summary>
/// Station locks and the remaining gates (Phase 6): one player per station (<c>tool/{type}</c>), and car-wide actions
/// (engine crane, delete car) claim the whole car. Deny-only like the Phase 2 gates and run first, so the sync hooks
/// on the same methods see that the action was cancelled and send nothing. State applied from the host (the tool's
/// listen flag off, or inside <see cref="ActionGate.Apply"/>) is never gated.
/// </summary>
[HarmonyPatch]
public static class StationGates
{
	private static int groupMounts;

	private static bool Gate(bool listening, string key) => !listening || !ActionGate.Active || !GameLoadHook.IsGameReady() || ActionGate.TryBegin(key);

	[HarmonyPatch(typeof(TireC), nameof(TireC.SetGroupOnTireChanger), typeof(GroupItem), typeof(bool), typeof(bool))]
	[HarmonyPrefix]
	[HarmonyPriority(Priority.First)]
	public static bool TireChangerGate() => Gate(Tools.TireChangerLogic.listen, LockKeys.Tool("tirechanger"));

	[HarmonyPatch(typeof(global::WheelBalancerLogic), nameof(global::WheelBalancerLogic.SetGroupOnWheelBalancer))]
	[HarmonyPrefix]
	[HarmonyPriority(Priority.First)]
	public static bool WheelBalancerGate() => Gate(Tools.WheelBalancer.listen, LockKeys.Tool("wheelbalancer"));

	[HarmonyPatch(typeof(global::SpringClampLogic), nameof(global::SpringClampLogic.SetGroupOnSpringClamp))]
	[HarmonyPrefix]
	[HarmonyPriority(Priority.First)]
	public static bool SpringClampGate() => Gate(Tools.SpringClampLogic.listen, LockKeys.Tool("springclamp"));

	/// <summary>Pulling an engine changes the whole car: nobody else may be working on it.</summary>
	[HarmonyPatch(typeof(CarLoader), nameof(CarLoader.UseEngineCrane))]
	[HarmonyPrefix]
	[HarmonyPriority(Priority.First)]
	public static bool EngineCraneGate(CarLoader __instance) => Gate(Tools.EngineCrane.listen, LockKeys.Car(CarLoaderRegistry.IdOf(__instance)));

	[HarmonyPatch(typeof(NotificationCenter), nameof(NotificationCenter.InsertEngineToCar))]
	[HarmonyPrefix]
	[HarmonyPriority(Priority.First)]
	public static bool InsertEngineGate() => Gate(Tools.EngineCrane.listen, LockKeys.Tool("crane"));

	[HarmonyPatch(typeof(CarLoader), nameof(CarLoader.UseOilbin))]
	[HarmonyPrefix]
	[HarmonyPriority(Priority.First)]
	public static bool OilBinGate(CarLoader __instance) => Gate(Tools.OilBin.listen, LockKeys.Tool("oil/" + CarLoaderRegistry.IdOf(__instance)));

	[HarmonyPatch(typeof(WelderL), nameof(WelderL.DoWorkAnim))]
	[HarmonyPrefix]
	[HarmonyPriority(Priority.First)]
	public static bool WelderGate() => Gate(Tools.WelderLogic.listen, LockKeys.Tool("welder"));

	[HarmonyPatch(typeof(CarLoader), nameof(CarLoader.TweenExteriorDustWash))]
	[HarmonyPrefix]
	[HarmonyPriority(Priority.First)]
	public static bool CarWashGate(CarLoader __instance) => Gate(Tools.CarWashLogic.listen, LockKeys.Tool("carwash/" + CarLoaderRegistry.IdOf(__instance)));

	[HarmonyPatch(typeof(InteriorDetailingToolkitLogic), nameof(InteriorDetailingToolkitLogic.DoWorkAnim))]
	[HarmonyPrefix]
	[HarmonyPriority(Priority.First)]
	public static bool InteriorGate(CarLoader carLoader) => Gate(Tools.CarWashLogic.listen, LockKeys.Tool("carwash/" + CarLoaderRegistry.IdOf(carLoader)));

	/// <summary>Deleting a car someone works on is refused.</summary>
	[HarmonyPatch(typeof(CarLoader), nameof(CarLoader.DeleteCar), new System.Type[] { })]
	[HarmonyPrefix]
	[HarmonyPriority(Priority.First)]
	public static bool DeleteCarGate(CarLoader __instance) =>
		string.IsNullOrEmpty(__instance?.carToLoad) || Gate(CarSpawnHooks.listenToDelete, LockKeys.Car(CarLoaderRegistry.IdOf(__instance)));

	// A group mount (engine on a stand, a whole wheel) is never cut in half: only single quick mounts are gated.
	[HarmonyPatch(typeof(PartScript), nameof(PartScript.MountByGroup))]
	[HarmonyPrefix]
	public static void MountByGroupStart() => groupMounts++;

	[HarmonyPatch(typeof(PartScript), nameof(PartScript.MountByGroup))]
	[HarmonyFinalizer]
	public static void MountByGroupEnd() => groupMounts = groupMounts > 0 ? groupMounts - 1 : 0;

	[HarmonyPatch(typeof(PartScript), nameof(PartScript.FastMount))]
	[HarmonyPrefix]
	[HarmonyPriority(Priority.First)]
	public static bool FastMountGate(PartScript __instance) => groupMounts > 0 || Gate(true, ActionGate.KeyOf(__instance));

	[HarmonyPatch(typeof(PartScript), nameof(PartScript.FastUnmount))]
	[HarmonyPrefix]
	[HarmonyPriority(Priority.First)]
	public static bool FastUnmountGate(PartScript __instance) => groupMounts > 0 || Gate(true, ActionGate.KeyOf(__instance));
}