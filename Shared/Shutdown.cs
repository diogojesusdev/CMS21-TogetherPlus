using System;
using CMS21Together.ClientSide;
using CMS21Together.ClientSide.Data;
using CMS21Together.ClientSide.Data.Garage.Car;
using CMS21Together.Shared.Safety;
using UnityEngine;

namespace CMS21Together.Shared;

/// <summary>
/// Everything the mod must do - and stop doing - when a session goes back to the main menu.
/// <para>
/// Three crash reports in a row died in the same place: inside Unity's
/// <c>Unloading ... Unused Serialized files</c> while the garage was being torn down, straight after the game's own
/// <c>GarageLoader.Save</c>. That is a use-after-free during Unity's reference walk, not a fault in any one mod
/// action, which is why fixing individual callers never ended it.
/// </para>
/// <para>
/// The mod is what keeps those dead objects reachable. One loaded car holds well over a hundred IL2CPP wrappers
/// (<c>CarPart</c>, <c>PartScript</c>), and <see cref="GameData"/> holds the car loaders, the local player, the
/// inventory and every tool logic object. Each wrapper owns an il2cpp GC handle, so when Unity destroys the scene
/// those handles still point at freed objects while the unload walks them.
/// </para>
/// <para>
/// So rather than guessing which call is unsafe this time, the mod stops working the moment the menu is requested
/// and drops every reference it holds to the game. There is then nothing of ours left for the unload to trip over.
/// </para>
/// </summary>
public static class Shutdown
{
	private static bool leaving;

	/// <summary>True from "go to the menu" until the next scene is up: no mod work may touch the game.</summary>
	public static bool Leaving => leaving;

	/// <summary>Called from the scene-change hook, before anything else runs.</summary>
	public static void Begin(string reason)
	{
		if (leaving) return;
		leaving = true;
		Log.Info($"[Shutdown] Leaving to the menu ({reason}); the mod stops touching the game.");
	}

	/// <summary>Called once the new scene is loaded; the mod may work again.</summary>
	public static void Clear()
	{
		if (!leaving) return;
		leaving = false;
		Log.Info("[Shutdown] The new scene is up; the mod is active again.");
	}

	/// <summary>
	/// Drops every reference the mod holds to a game object, so the scene unload cannot walk into one of ours.
	/// Each step is separate: one failure must not keep the rest alive.
	/// </summary>
	public static void ReleaseGameReferences()
	{
		Guard.Run("release the cars the mod tracked", ReleaseCars);
		Guard.Run("release the player objects the mod tracked", ReleasePlayers);
		Guard.Run("release the garage objects the mod tracked", ReleaseGarage);
		Guard.Run("drop the saved-car captures", CarSaveHooks.Reset);
		Guard.Run("let go of the freed game objects", Collect);
		Guard.Run("release the assets the mod no longer uses", () => Resources.UnloadUnusedAssets());
		Log.Info("[Shutdown] Released the mod's references to the garage, including the avatar template.");
	}

	private static void ReleaseCars()
	{
		var cars = ClientData.Instance?.loadedCars;
		if (cars == null) return;

		foreach (var car in cars.Values)
			car?.CarPartInfo?.Clear();
		cars.Clear();
	}

	private static void ReleasePlayers()
	{
		var data = ClientData.Instance;
		if (data == null) return;

		if (data.connectedClients != null)
			foreach (var user in data.connectedClients.Values)
				Guard.Run("remove a player's avatar", () => user?.DestroyPlayerImmediate());

		// The avatar template is the one thing of the mod's that outlives a scene: it is instantiated from the mod's
		// own AssetBundle and marked DontDestroyOnLoad, and the bundle was unloaded with Unload(false), so its
		// serialized file is exactly what "Unloading N Unused Serialized files" is about to free while this object
		// still refers to it. It is destroyed here, right now rather than at the end of the frame, and rebuilt on the
		// next garage load. Clearing the field without destroying it would leak the instance and make the next load
		// read the same bundle a second time.
		Guard.Run("remove the avatar template", () =>
		{
			if (data.playerPrefab != null) UnityEngine.Object.DestroyImmediate(data.playerPrefab);
		});
		data.playerPrefab = null;
	}

	private static void ReleaseGarage()
	{
		GameData.Instance?.ReleaseReferences();
		GameData.Instance = null;
	}

	/// <summary>
	/// Unhollower frees an object's il2cpp GC handle in the wrapper's finaliser, so dropping the references is not
	/// enough on its own: the handles have to actually go before Unity unloads the scene. A scene change already
	/// stalls for seconds, so one collection here costs nothing noticeable.
	/// </summary>
	private static void Collect()
	{
		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();
	}
}
