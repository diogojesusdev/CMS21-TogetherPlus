using CMS21Together.ClientSide.Data.Handle;
using CMS21Together.Shared.Data;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using CMS21Together.Shared;

namespace CMS21Together.ClientSide.Data.Player;

/// <summary>
/// Handles synchronization of player entering/exiting cars in multiplayer.
/// </summary>
[HarmonyPatch]
public static class CarEnterExit
{
	private static bool wasInCar = false;
	private static int lastCarLoaderID = -1;

	/// <summary>A scene reload destroys the avatar but not this state; a stale "in car" hides the next one forever.</summary>
	public static void Reset()
	{
		wasInCar = false;
		lastCarLoaderID = -1;
	}

	/// <summary>
	/// Checks if the local player is currently inside a car.
	/// This method should be called periodically to detect car enter/exit events.
	/// </summary>
	public static void CheckCarState()
	{
		if (!Client.Instance.isConnected || GameData.Instance == null || GameData.Instance.localPlayer == null)
			return;

		// Business Logic: Check if player is inside a car by checking camera parent or FPSInputController state
		// Observation: When player is in car, the camera is typically a child of the car or the camera position is inside the car
		bool currentlyInCar = false;
		int currentCarLoaderID = -1;

		// Business Logic: Check camera and FPSInputController to determine if player is inside a car
		var mainCamera = Camera.main;
		var fpsController = GameData.Instance.localPlayer?.GetComponent<FPSInputController>();
		
		if (mainCamera != null && mainCamera.transform != null && GameData.Instance.carLoaders != null)
		{
			// Business Logic: Primary check: Check if camera is a child of any car (indicates player is in car)
			var cameraParent = mainCamera.transform.parent;
			if (cameraParent != null)
			{
				for (int i = 0; i < GameData.Instance.carLoaders.Length; i++)
				{
					var carLoader = GameData.Instance.carLoaders[i];
					if (carLoader == null || carLoader.gameObject == null) continue;
					if (string.IsNullOrEmpty(carLoader.carToLoad)) continue;

					// Business Logic: Check if camera is a child of this car
					var currentParent = cameraParent;
					while (currentParent != null)
					{
						if (currentParent == carLoader.transform)
						{
							currentlyInCar = true;
							currentCarLoaderID = i;
							break;
						}
						currentParent = currentParent.parent;
					}
					
					if (currentlyInCar) break;
				}
			}
			
			// Business Logic: Secondary check: Check if FPSInputController is disabled (indicates player is in car)
			// Observation: When player enters car, FPSInputController is typically disabled
			if (!currentlyInCar && fpsController != null && !fpsController.enabled)
			{
				// Business Logic: If FPSInputController is disabled, check which car the player is closest to
				var cameraPos = mainCamera.transform.position;
				float closestDistance = float.MaxValue;
				int closestCarID = -1;
				
				for (int i = 0; i < GameData.Instance.carLoaders.Length; i++)
				{
					var carLoader = GameData.Instance.carLoaders[i];
					if (carLoader == null || carLoader.gameObject == null) continue;
					if (string.IsNullOrEmpty(carLoader.carToLoad)) continue;

					var carPos = carLoader.transform.position;
					var distance = Vector3.Distance(cameraPos, carPos);
					
					if (distance < closestDistance && distance < 3f) // Within 3 meters
					{
						closestDistance = distance;
						closestCarID = i;
					}
				}
				
				if (closestCarID >= 0)
				{
					currentlyInCar = true;
					currentCarLoaderID = closestCarID;
				}
			}
		}

		// Business Logic: Only send packet if car state changed
		if (currentlyInCar != wasInCar || currentCarLoaderID != lastCarLoaderID)
		{
			wasInCar = currentlyInCar;
			lastCarLoaderID = currentCarLoaderID;
			
			ClientSend.PlayerInCarPacket(currentlyInCar, currentCarLoaderID);
			Log.Debug($"[CarEnterExit->CheckCarState] Player car state changed: InCar={currentlyInCar}, CarLoaderID={currentCarLoaderID}");
		}
	}
}

