using System;
using CMS21Together.ClientSide;
using CMS21Together.ClientSide.Data;
using CMS21Together.ClientSide.Data.Handle;
using CMS21Together.ClientSide.Data.Player;
using MelonLoader;
using Newtonsoft.Json;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CMS21Together.Shared.Data;

[Serializable]
public class UserData
{
	public string username;
	public string ip;
	public string lobbyID;
	public string playerGUID;

	public NetworkType selectedNetworkType = NetworkType.TCP;

	[JsonIgnore] public int playerID;
	[JsonIgnore] public bool isReady;

	[JsonIgnore] public GameScene scene;
	[JsonIgnore] public Vector3Serializable position = new(Vector3.zero);
	[JsonIgnore] public QuaternionSerializable rotation = new(Quaternion.identity);
	[JsonIgnore] public int playerLevel;
	[JsonIgnore] public int playerExp;
	[JsonIgnore] public int playerSkillPoints;
	[JsonIgnore] [NonSerialized] public Vector3Serializable lastPosition;
	[JsonIgnore] [NonSerialized] public Animator userAnimator;

	[JsonIgnore] public bool isInCar = false;
	[JsonIgnore] public int carLoaderID = -1; // -1 means not in any car

	[JsonIgnore] [NonSerialized] public GameObject userObject;
	[JsonIgnore] [NonSerialized] public float lastUpdateTime;

	public UserData()
	{
		username = "player";
		ip = "127.0.0.1";
		lobbyID = "";
		playerID = 1;
		playerGUID = Guid.NewGuid().ToString();
		selectedNetworkType = NetworkType.TCP;
	}

	public UserData(string _username, int _playerID, string playerGuid)
	{
		username = _username;
		playerID = _playerID;
		playerGUID = playerGuid;
	}

	/// <summary>Copy sent to other players: no identity token, address or lobby code.</summary>
	public UserData ForBroadcast() => new(username, playerID, null)
	{
		ip = null,
		lobbyID = null,
		selectedNetworkType = selectedNetworkType,
		isReady = isReady,
		scene = scene,
		position = position,
		rotation = rotation,
		playerLevel = playerLevel,
		playerExp = playerExp,
		playerSkillPoints = playerSkillPoints,
		isInCar = isInCar,
		carLoaderID = carLoaderID
	};
	public void UpdateScene(string sceneName)
	{
		scene = SceneManager.UpdateScene(sceneName);
		// The host releases this player's locks when it sees the scene change; forget them here too.
		Client.Instance?.Locks?.Table.DropOwn();
		ClientSend.SceneChangePacket(scene);
	}

	public void SpawnPlayer()
	{
		// Security Rule: Prevent duplicate player spawning
		if (userObject != null)
		{
			Log.Warn($"[UserData->SpawnPlayer] Player {username} (ID: {playerID}) already spawned. Skipping duplicate spawn.");
			return;
		}

		if (ClientData.Instance.playerPrefab == null)
		{
			Log.Error("[CMS21-Together] Cannot spawn player: playerPrefab is null.");
			return;
		}
		if (playerID == ClientData.UserData.playerID)
		{
			if (GameData.Instance.localPlayer == null)
			{
				Log.Error("[CMS21-Together] Cannot spawn local player: localPlayer is null.");
				return;
			}
			userObject = GameData.Instance.localPlayer;
		}
		else
		{
			// Security Rule: Validate GameData.Instance and localPlayer before accessing
			if (GameData.Instance == null || GameData.Instance.localPlayer == null)
			{
				Log.Warn($"[UserData->SpawnPlayer] GameData.Instance or localPlayer is null. Cannot spawn player {username}. Will retry later.");
				return;
			}

			// Security Rule: Validate localPlayer has Collider component
			var localPlayerCollider = GameData.Instance.localPlayer.GetComponent<Collider>();
			if (localPlayerCollider == null)
			{
				Log.Warn($"[UserData->SpawnPlayer] Local player has no Collider component. Cannot set up collision ignore for {username}.");
			}

			// Business Logic: Instantiate player prefab at correct position and rotation
			userObject = Object.Instantiate(ClientData.Instance.playerPrefab, position.toVector3(), rotation.toQuaternion());
			userObject.AddComponent<InfoBillboard>();
			userAnimator = userObject.GetComponent<Animator>();
			userObject.name = username;
			Tint(userObject, playerID);
			
			// Security Rule: Only ignore collision if both colliders exist
			var userObjectCollider = userObject.GetComponent<Collider>();
			if (localPlayerCollider != null && userObjectCollider != null)
			{
				Physics.IgnoreCollision(localPlayerCollider, userObjectCollider);
			}
			else
			{
				Log.Warn($"[UserData->SpawnPlayer] One or both players missing Collider. Collision ignore not set for {username}.");
			}
			
			Log.Info($"[UserData->SpawnPlayer] Spawned player {username} (ID: {playerID}) at position {position.toVector3()}");
		}

	}

	/// <summary>Phase 7: one colour per slot, so 8 players tell each other apart (textures stay visible).</summary>
	private static readonly float[,] Palette =
	{
		{ 1f, 1f, 1f }, { 0.95f, 0.35f, 0.3f }, { 0.3f, 0.6f, 1f }, { 0.35f, 0.85f, 0.4f },
		{ 1f, 0.8f, 0.25f }, { 0.75f, 0.4f, 0.95f }, { 0.3f, 0.9f, 0.9f }, { 1f, 0.55f, 0.15f }
	};

	private static void Tint(GameObject avatar, int slot)
	{
		try
		{
			// Built here, not in a static field: Unity colours cannot be made outside the game (headless tests).
			var i = Math.Abs(slot - 1) % Palette.GetLength(0);
			var tint = Color.Lerp(Color.white, new Color(Palette[i, 0], Palette[i, 1], Palette[i, 2]), 0.6f);
			foreach (var renderer in avatar.GetComponentsInChildren<Renderer>(true))
			foreach (var material in renderer.materials)
				if (material != null && material.HasProperty("_Color"))
					material.color *= tint;
		}
		catch (Exception ex)
		{
			Log.Warn($"[UserData] Could not colour player {slot}'s avatar: {ex.Message}");
		}
	}

	public void DestroyPlayer()
	{
		if (userObject == null) return;
		// The local player's "avatar" is the game's own player object, never one of ours: destroying it would take
		// the garage with it. Only an avatar the mod instantiated may be removed.
		if (!IsModAvatar())
		{
			userObject = null;
			return;
		}
		Object.Destroy(userObject);
		userObject = null;
	}

	/// <summary>
	/// Removes the avatar now rather than at the end of the frame. Used when the garage is being torn down: a
	/// deferred destroy would run after Unity has started unloading, which is too late to be of any use.
	/// </summary>
	public void DestroyPlayerImmediate()
	{
		if (userObject == null) return;
		if (!IsModAvatar())
		{
			userObject = null;
			return;
		}
		Object.DestroyImmediate(userObject);
		userObject = null;
	}

	/// <summary>True when <see cref="userObject"/> is an avatar the mod spawned, not the game's local player.</summary>
	private bool IsModAvatar()
	{
		if (playerID == ClientData.UserData?.playerID) return false;
		var local = GameData.Instance?.localPlayer;
		return local == null || local.GetInstanceID() != userObject.GetInstanceID();
	}
}