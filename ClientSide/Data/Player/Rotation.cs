using System.Collections.Generic;
using CMS21Together.ClientSide.Data.Handle;
using CMS21Together.Shared.Data;
using UnityEngine;

namespace CMS21Together.ClientSide.Data.Player;

public static class Rotation
{
	private static readonly float minDistance = 0.01f;
	private static Quaternion lastRotation;
	private static float nextSend;
	private static float keyframeAt;
	private static readonly Dictionary<int, Quaternion> targets = new();

	public static void UpdateRotation(int id, QuaternionSerializable rotation)
	{
		if (!ClientData.Instance.connectedClients.ContainsKey(id)) return;
		if (!GameLoadHook.IsGameReady()) return;

		var player = ClientData.Instance.connectedClients[id];
		if (player.scene != ClientData.UserData.scene) return;

		if (player.userObject != null)
		{
			player.lastUpdateTime = Time.time;
			targets[id] = rotation.toQuaternion();
		}
		else
		{
			// Business Logic: Store rotation and spawn player if not already spawned
			player.rotation = rotation;
			if (player.userObject == null)
			{
				player.SpawnPlayer();
			}
		}
	}

	/// <summary>Every frame: other players' avatars turn towards their latest rotation (packets come at 20 Hz).</summary>
	public static void Smooth()
	{
		foreach (var pair in targets)
		{
			if (!ClientData.Instance.connectedClients.TryGetValue(pair.Key, out var player) || player?.userObject == null) continue;
			var transform = player.userObject.transform;
			transform.rotation = Quaternion.Slerp(transform.rotation, pair.Value, Mathf.Min(1f, Time.deltaTime * 15f));
		}
	}

	public static void Forget(int id) => targets.Remove(id);

	public static void Reset() => targets.Clear();

	public static void SendRotation()
	{
		if (GameData.Instance.localPlayer == null || Time.unscaledTime < nextSend) return;

		var rotation = GameData.Instance.localPlayer.transform.rotation;
		if (Quaternion.Angle(rotation, lastRotation) > minDistance || Time.unscaledTime >= keyframeAt)
		{
			lastRotation = rotation;
			nextSend = Time.unscaledTime + Movement.SendInterval;
			keyframeAt = Time.unscaledTime + Movement.KeyframeInterval;
			ClientSend.RotationPacket(new QuaternionSerializable(rotation));
		}
	}
}