using CMS21Together.ClientSide.Data.Garage.Car;
using System.Collections;
using System.Collections.Generic;
using CMS21Together.ClientSide.Data.Handle;
using CMS21Together.Shared;
using CMS21Together.Shared.Data.Vanilla;
using HarmonyLib;
using UnityEngine;

namespace CMS21Together.ClientSide.Data.Garage.Tools;

[HarmonyPatch]
public static class LifterLogic
{
	public static bool listen = true;

	/// <summary>Newest move per lifter: an older one still stepping stops when a newer state arrives.</summary>
	private static readonly Dictionary<int, int> moves = new();

	/// <summary>
	/// Moves a lifter to the absolute state another player's lifter is in, one level at a time (Phase 5: the old code
	/// took one relative step, even when both were already at the same level).
	/// </summary>
	public static IEnumerator MoveTo(int carLoaderID, ModLifterState target)
	{
		moves.TryGetValue(carLoaderID, out var last);
		var move = last + 1;
		moves[carLoaderID] = move;

		for (var step = 0; step < 4; step++)
		{
			if (moves[carLoaderID] != move || GameData.Instance == null) yield break;
			var lifter = GameData.Instance.carLoaders[carLoaderID].lifter;
			if (lifter == null) yield break;
			var current = (int)lifter.currentState;
			if (current == (int)target) yield break;

			using (ActionGate.Apply())
			{
				listen = false;
				lifter.Action((int)target > current ? 0 : 1);
				listen = true;
			}

			// Wait for this level to be reached before the next one.
			var waited = 0f;
			while (waited < 8f && (int)lifter.currentState == current && moves[carLoaderID] == move)
			{
				yield return new WaitForSeconds(0.1f);
				waited += 0.1f;
			}
			if ((int)lifter.currentState == current)
			{
				Log.Debug($"[LifterLogic] Lifter {carLoaderID} did not move to {target}.");
				yield break;
			}
			yield return new WaitForSeconds(0.2f);
		}
	}

	[HarmonyPatch(typeof(CarLifter), "Action")]
	[HarmonyPostfix]
	public static void LifterActionHook(int actionType, CarLifter __instance, bool __runOriginal)
	{
		if (!Client.Instance.isConnected || !listen || ActionGate.ApplyingServerState)
		{
			listen = true;
			return;
		}
		// Skipped by the lock gate: nothing moved, nothing to send.
		if (!__runOriginal) return;

		var action = 0;
		if (actionType == 0) action = 1;
		else if (actionType == 1) action = -1;

		var currentState = (int)__instance.currentState + action;
		var carLoaderID = CarLoaderRegistry.IdOf(__instance.connectedCarLoader);

		var state = GetState(currentState);
		ClientSend.LifterPacket(state, carLoaderID);
	}

	private static ModLifterState GetState(int value)
	{
		var state = ModLifterState.ground;
		switch (value)
		{
			case 0:
				state = ModLifterState.ground;
				break;
			case 1:
				state = ModLifterState.low;
				break;
			case 2:
				state = ModLifterState.high;
				break;
		}

		return state;
	}
}