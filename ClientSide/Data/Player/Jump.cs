using HarmonyLib;
using UnityEngine;
using CMS21Together.Shared;

namespace CMS21Together.ClientSide.Data.Player;

/// <summary>Space bar jump. The game's character motor supports jumping but the game never turns it on.</summary>
[HarmonyPatch]
public static class Jump
{
	private const float DefaultHeight = 0.6f;
	private static bool logged;

	[HarmonyPatch(typeof(FPSInputController), nameof(FPSInputController.Update))]
	[HarmonyPostfix]
	public static void UpdateHook(FPSInputController __instance)
	{
		if (!Client.Instance.isConnected) return;
		var motor = __instance.motor;
		if (motor == null || motor.jumping == null) return;

		if (!motor.jumping.enabled)
		{
			motor.jumping.enabled = true;
			if (motor.jumping.baseHeight < 0.1f) motor.jumping.baseHeight = DefaultHeight;
			if (!logged)
			{
				logged = true;
				Log.Info($"[Jump] enabled (height {motor.jumping.baseHeight})");
			}
		}

		// A locked cursor means no window or menu is open, so space is not typing or pressing a button.
		motor.inputJump = Cursor.lockState == CursorLockMode.Locked && Input.GetKey(KeyCode.Space);
	}
}
