using CMS21Together.ClientSide.Data.Garage.Tools;
using CMS21Together.Shared.Locks;
using HarmonyLib;

namespace CMS21Together.ClientSide.Data.Garage;

/// <summary>
/// Deny-only lock gates on the interactive entry points (Phase 2): bolts, part mount/unmount, body panels and lifters.
/// An action is skipped only when the host reported another player on that resource, so nothing has moved yet when we
/// cancel. Group and save-load paths (FastMount/FastUnmount, MountByGroup, *FromSave) are never gated.
/// </summary>
[HarmonyPatch]
public static class LockGates
{
	[HarmonyPatch(typeof(MountObject), nameof(MountObject.Action))]
	[HarmonyPrefix]
	public static bool BoltActionGate(MountObject __instance)
	{
		if (!ActionGate.Active) return true;
		var part = __instance.GetComponentInParent<PartScript>();
		return part == null || ActionGate.TryBegin(ActionGate.KeyOf(part));
	}

	[HarmonyPatch(typeof(PartScript), nameof(PartScript.ActionUnMount))]
	[HarmonyPrefix]
	public static bool UnmountGate(PartScript __instance) => !ActionGate.Active || ActionGate.TryBegin(ActionGate.KeyOf(__instance));

	[HarmonyPatch(typeof(PartScript), nameof(PartScript.ActionMount), typeof(bool))]
	[HarmonyPrefix]
	public static bool MountGate(PartScript __instance) => !ActionGate.Active || ActionGate.TryBegin(ActionGate.KeyOf(__instance));

	[HarmonyPatch(typeof(CarLifter), nameof(CarLifter.Action), typeof(int))]
	[HarmonyPrefix]
	public static bool LifterGate(CarLifter __instance)
	{
		// The host's lifter updates run with listen off; LifterLogic turns it back on in its postfix.
		if (!LifterLogic.listen || !ActionGate.Active) return true;
		var loader = __instance.connectedCarLoader;
		return loader == null || ActionGate.TryBegin(LockKeys.Lifter(ActionGate.LoaderId(loader)));
	}

	/// <summary>Body panels: the game asks this before taking a panel off, so answering "no" cancels cleanly.</summary>
	[HarmonyPatch(typeof(CarLoader), nameof(CarLoader.CanTakeOffCarPart))]
	[HarmonyPostfix]
	public static void TakeOffBodyGate(string name, CarLoader __instance, ref bool __result)
	{
		if (!__result || !ActionGate.Active) return;
		var holder = ActionGate.BlockedBy(ActionGate.KeyOfBody(__instance, name));
		if (holder == 0) return;
		__result = false;
		ActionGate.ShowDenied(holder);
	}
}
