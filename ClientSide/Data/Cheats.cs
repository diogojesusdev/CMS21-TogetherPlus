using System;
using CMS21Together.ClientSide.Data.Handle;
using CMS21Together.ClientSide.Data.Player;
using CMS21Together.Shared;
using UnityEngine;

namespace CMS21Together.ClientSide.Data;

/// <summary>Holding Z, E, N, G and O together asks the host for money (the host decides the amount).</summary>
public static class Cheats
{
	private static bool held;
	private static float nextAllowed;

	public static void Pump()
	{
		if (Client.Instance == null || !Client.Instance.isConnected || !GameLoadHook.IsGameReady())
		{
			held = false;
			return;
		}
		var now = Input.GetKey(KeyCode.Z) && Input.GetKey(KeyCode.E) && Input.GetKey(KeyCode.N) &&
		          Input.GetKey(KeyCode.G) && Input.GetKey(KeyCode.O);
		var pressed = now && !held;
		held = now;
		if (!pressed || Time.unscaledTime < nextAllowed) return;
		nextAllowed = Time.unscaledTime + 1f;
		try
		{
			ClientSend.CheatMoneyPacket();
			Purchases.Toast("Zengo: +500");
		}
		catch (Exception ex)
		{
			Log.Error("[Cheat] Sending the cheat failed", ex);
		}
	}
}
