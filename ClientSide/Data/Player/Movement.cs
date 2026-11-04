using System.Collections.Generic;
using CMS21Together.ClientSide.Data.Handle;
using CMS21Together.Shared.Data;
using MelonLoader;
using UnityEngine;
using CMS21Together.Shared;

namespace CMS21Together.ClientSide.Data.Player;

public static class Movement
{
	private static readonly float minDistance = 0.01f;
	private static Vector3 lastPosition;
	private static float nextSend;
	private static float keyframeAt;

	/// <summary>A standing player is sent again this often, so a lost frame or a scene change never leaves it misplaced.</summary>
	public const float KeyframeInterval = 1f;

	/// <summary>Phase 7: positions go out at most 20 times a second (they went out every frame).</summary>
	public const float SendInterval = 0.05f;

	/// <summary>A jump longer than this is a teleport: no gliding across the garage.</summary>
	private const float SnapDistance = 5f;

	/// <summary>Sent positions are the player controller's position minus this.</summary>
	private const float SentHeightOffset = 0.72f;

	/// <summary>
	/// Avatars are moved by the network only (no physics), so they must be placed with their feet, their origin, on the
	/// floor. Every player has the same game controller: measure how far below a sent position the feet are on ours.
	/// </summary>
	private static float FeetBelowSent()
	{
		var controller = GameData.Instance?.localPlayer != null ? GameData.Instance.localPlayer.GetComponent<CharacterController>() : null;
		if (controller == null) return 0f;
		var feet = controller.bounds.min.y - controller.skinWidth;
		var offset = controller.transform.position.y - SentHeightOffset - feet;
		return Mathf.Clamp(offset, -1f, 1f);
	}

	// Other players' latest positions; their avatars glide there every frame.
	private static readonly Dictionary<int, Vector3> targets = new();
	private static readonly Dictionary<int, float> lastPacket = new();

	public static void UpdatePosition(int id, Vector3Serializable position)
	{
		if (!ClientData.Instance.connectedClients.ContainsKey(id)) return;
		if (!GameLoadHook.IsGameReady()) return;

		var player = ClientData.Instance.connectedClients[id];

		if (player.scene != ClientData.UserData.scene) return;
		
		// Business Logic: If player is in car, hide the player object instead of updating position
		if (player.isInCar)
		{
			if (player.userObject != null && player.userObject.activeSelf)
			{
				player.userObject.SetActive(false);
				Log.Debug($"[Movement->UpdatePosition] Hiding player {player.username} (ID: {id}) - player is in car {player.carLoaderID}");
			}
			return;
		}
		
		// Business Logic: If player exits car, show the player object
		if (!player.isInCar && player.userObject != null && !player.userObject.activeSelf)
		{
			player.userObject.SetActive(true);
			Log.Debug($"[Movement->UpdatePosition] Showing player {player.username} (ID: {id}) - player exited car");
		}
		
		if (player.userObject == null) player.SpawnPlayer();

		if (player.userObject)
		{
			Vector3 targetPos = position.toVector3();
			targetPos.y -= FeetBelowSent();
			var now = Time.time;

			if (player.lastPosition != null)
			{
				var step = position.toVector3() - player.lastPosition.toVector3();
				var interval = lastPacket.TryGetValue(id, out var last) ? Mathf.Clamp(now - last, 0.016f, 0.5f) : SendInterval;
				var speed = Mathf.Clamp(step.magnitude / interval, 0f, 20f);

				UpdateAnimations(player.userAnimator, step.normalized, speed);
				player.lastUpdateTime = now;
				if (step.magnitude > SnapDistance) player.userObject.transform.position = targetPos;
			}
			else
				player.userObject.transform.position = targetPos;
			targets[id] = targetPos;
			lastPacket[id] = now;
			player.lastPosition = position;
		}
	}

	/// <summary>Every frame: other players' avatars glide towards their latest position.</summary>
	public static void Smooth()
	{
		foreach (var pair in targets)
		{
			if (!ClientData.Instance.connectedClients.TryGetValue(pair.Key, out var player) || player?.userObject == null) continue;
			var transform = player.userObject.transform;
			transform.position = Vector3.Lerp(transform.position, pair.Value, Mathf.Min(1f, Time.deltaTime * 15f));
		}
	}

	/// <summary>A player left or the session ended.</summary>
	public static void Forget(int id)
	{
		targets.Remove(id);
		lastPacket.Remove(id);
		Rotation.Forget(id);
	}

	public static void Reset()
	{
		targets.Clear();
		lastPacket.Clear();
		Rotation.Reset();
	}

	private static void UpdateAnimations(Animator animator, Vector3 direction, float speed)
	{
		var horizontalSpeed = direction.x * speed;
		var verticalSpeed = direction.z * speed;

		animator.SetFloat("Vertical", Mathf.Lerp(animator.GetFloat("Vertical"), verticalSpeed, Time.deltaTime * 10f));
		animator.SetFloat("Horizontal", Mathf.Lerp(animator.GetFloat("Horizontal"), horizontalSpeed, Time.deltaTime * 10f));
	}
	
	public static void CheckForInactivity()
	{
		foreach (var client in ClientData.Instance.connectedClients.Values)
		{
			if (client == null) continue;
			if (client.userObject == null || client.userAnimator == null) continue;
			
			float elapsedTime = Time.time - client.lastUpdateTime;
			
			// Packets come every 50 ms (Phase 7); a lost one or two must not stop the walk animation.
			if (elapsedTime > 0.25f) 
			{
				client.userAnimator.SetFloat("Vertical", Mathf.Lerp(client.userAnimator.GetFloat("Vertical"), 0, Time.deltaTime * 10f));
				client.userAnimator.SetFloat("Horizontal", Mathf.Lerp(client.userAnimator.GetFloat("Horizontal"), 0, Time.deltaTime * 10f));
				
				var currentRotation = client.userObject.transform.rotation;
				var targetRotation = client.userObject.transform.rotation;
				client.userObject.transform.rotation = Quaternion.Slerp(currentRotation, targetRotation, Time.deltaTime * 5f);
			}
		}
	}

	public static void SendPosition()
	{
		if (GameData.Instance.localPlayer == null || Time.unscaledTime < nextSend) return;

		var position = GameData.Instance.localPlayer.transform.position;
		position.y -= SentHeightOffset; // also the spawn position the host saves: keep it as it was
		if (Vector3.Distance(position, lastPosition) > minDistance || Time.unscaledTime >= keyframeAt)
		{
			lastPosition = position;
			nextSend = Time.unscaledTime + SendInterval;
			keyframeAt = Time.unscaledTime + KeyframeInterval;
			ClientSend.PositionPacket(new Vector3Serializable(position));
		}
	}
}