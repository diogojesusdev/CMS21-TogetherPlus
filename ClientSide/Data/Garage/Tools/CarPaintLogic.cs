using System.Collections;
using CMS.Managers;
using CMS21Together.ClientSide.Data.Handle;
using CMS21Together.Shared.Data;
using CMS21Together.Shared.Data.Vanilla;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using CMS21Together.Shared;

namespace CMS21Together.ClientSide.Data.Garage.Tools;

[HarmonyPatch]
public static class CarPaintLogic
{
	public static bool listen = true;

	public static void Reset() => listen = true;
	
	/// <summary>
	/// Hook for when player paints a car locally.
	/// Sends paint color to server for synchronization.
	/// </summary>
	[HarmonyPatch(typeof(PaintshopManager), nameof(PaintshopManager.MakePaintEffects))]
	[HarmonyPostfix]
	public static void MakePaintEffectsHook(PaintshopManager __instance)
	{
		// Business Rule: Only sync if connected and listening flag is enabled
		if(!Client.Instance.isConnected || !listen) 
		{ 
			listen = true; 
			return;
		}

		// Security Rule: Validate PaintshopManager instance and state
		if (__instance == null)
		{
			Log.Warn("[CarPaintLogic->MakePaintEffectsHook] PaintshopManager instance is null.");
			return;
		}

		// Security Rule: Validate paintshopState and Selected using ReferenceEquals for IL2CPP compatibility
		if (ReferenceEquals(__instance.paintshopState, null) || ReferenceEquals(__instance.paintshopState.Selected, null))
		{
			Log.Warn("[CarPaintLogic->MakePaintEffectsHook] PaintshopManager state or Selected color is null.");
			return;
		}

		// Observation: Extract color from paintshop state
		var color = __instance.paintshopState.Selected.Color;
		
		// Business Rule: Validate color is not null
		if (color == null)
		{
			Log.Warn("[CarPaintLogic->MakePaintEffectsHook] Selected color is null.");
			return;
		}

		Log.Debug($"[CarPaintLogic->MakePaintEffectsHook] Car color change: {color}!");
		// Sent at the end of the frame with the paint job's debit: the host repaints it back if it was not charged.
		var modColor = new ModColor(color);
		Player.Purchases.Defer(txn => ClientSend.CarPaint(modColor, txn));
	}

	/// <summary>
	/// Handles car paint synchronization from server.
	/// Applies paint color to the car in paintshop position (position 5).
	/// </summary>
	/// <param name="color">The color to apply to the car</param>
	/// <param name="own">This player's own paid paint job coming back from the host: skipped if it already shows it.</param>
	public static IEnumerator ChangeColor(ModColor color, bool own = false)
	{
		// Business Rule: Wait for game to be ready before processing
		while (!GameLoadHook.IsGameReady())
		{
			// Security Rule: Check if client disconnected during wait
			if (!Client.Instance.isConnected)
			{
				Log.Warn("[CarPaintLogic->ChangeColor] Client disconnected while waiting for game ready.");
				yield break;
			}
			yield return new WaitForSeconds(0.25f);
		}

		yield return new WaitForEndOfFrame();
		yield return new WaitForEndOfFrame();

		// Security Rule: Validate color parameter
		if (color == null)
		{
			Log.Error("[CarPaintLogic->ChangeColor] ModColor parameter is null. Cannot paint car.");
			yield break;
		}

		// Security Rule: Validate GameData.Instance is initialized
		if (GameData.Instance == null)
		{
			Log.Error("[CarPaintLogic->ChangeColor] GameData.Instance is null. Cannot paint car.");
			yield break;
		}

		// Security Rule: Validate paintshopManager is initialized
		if (GameData.Instance.paintshopManager == null)
		{
			Log.Error("[CarPaintLogic->ChangeColor] paintshopManager is null. Cannot paint car.");
			yield break;
		}

		// Security Rule: Validate ClientData.Instance and loadedCars
		if (ClientData.Instance == null || ClientData.Instance.loadedCars == null)
		{
			Log.Error("[CarPaintLogic->ChangeColor] ClientData.Instance or loadedCars is null. Cannot paint car.");
			yield break;
		}

		// Business Rule: Find car in paintshop position (position 5)
		ModCar carInPaintshop = null;
		foreach (ModCar car in ClientData.Instance.loadedCars.Values)
		{
			if (car != null && car.carPosition == 5)
			{
				carInPaintshop = car;
				break;
			}
		}

		// Business Rule: Validate car is in paintshop position
		if (carInPaintshop == null)
		{
			Log.Warn("[CarPaintLogic->ChangeColor] No car found in paintshop position (position 5). Cannot paint.");
			yield break;
		}

		// Security Rule: Validate paintshopState is initialized using ReferenceEquals for IL2CPP compatibility
		if (ReferenceEquals(GameData.Instance.paintshopManager.paintshopState, null))
		{
			Log.Error("[CarPaintLogic->ChangeColor] paintshopState is null. Cannot paint car.");
			yield break;
		}

		var selected = GameData.Instance.paintshopManager.paintshopState.Selected;
		if (own && !ReferenceEquals(selected, null) && !color.IsDifferent(selected.Color)) yield break;

		// Business Rule: Set listen flag to false to prevent feedback loop
		listen = false;

		try
		{
			// Business Logic: Convert ModColor to game color and apply
			var gameColor = color.ToGame();
			
			// Security Rule: Validate converted color is not null
			if (gameColor == null)
			{
				Log.Error("[CarPaintLogic->ChangeColor] Failed to convert ModColor to game color.");
				yield break;
			}

			// Business Logic: Set selected color and update paintshop
			GameData.Instance.paintshopManager.paintshopState.SetSelectedColor(gameColor);
			GameData.Instance.paintshopManager.UpdateColor(gameColor);
			
			// Business Logic: Start paint effects coroutine
			MainMod.StartCoroutine(GameData.Instance.paintshopManager.MakePaintEffects());
			
			Log.Debug($"[CarPaintLogic->ChangeColor] Car painted successfully with color: {gameColor}");
		}
		catch (System.Exception ex)
		{
			// Security Rule: Catch and log any exceptions during paint operation
			Log.Error($"[CarPaintLogic->ChangeColor] Exception during paint operation: {ex.Message}\n{ex.StackTrace}");
		}
		finally
		{
			// Business Rule: Reset listen flag after operation completes
			listen = true;
		}
	}
}