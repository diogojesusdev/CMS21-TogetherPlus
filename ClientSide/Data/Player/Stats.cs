using System.Collections;
using CMS.UI.Logic.Upgrades;
using CMS21Together.ClientSide.Data.Handle;
using CMS21Together.ServerSide;
using CMS21Together.Shared.Data;
using HarmonyLib;
using UnityEngine;
using CMS21Together.Shared;

namespace CMS21Together.ClientSide.Data.Player;

[HarmonyPatch]
public static class Stats
{
	/// <summary>The host's loaded save sets the shared wallet's starting balances (once per session).</summary>
	public static IEnumerator SendInitialStats()
	{
		if (!Server.Instance.isRunning) yield break;

		while (!GameLoadHook.IsGameReady())
			yield return new WaitForSeconds(0.2f);

		SeedWallet();
	}

	/// <summary>
	/// The host's loaded save sets the shared wallet (once per session). Also called by the garage load right before the
	/// garage becomes playable (Phase 8), so the host never plays a moment without a synced wallet.
	/// </summary>
	public static void SeedWallet()
	{
		if (Server.Instance == null || !Server.Instance.isRunning) return;
		// The ledger's history continues from the save (Phase 4); the balances come from the game's own save.
		var history = SavesManager.ModSaves.TryGetValue(SavesManager.currentSaveIndex, out var save) ? save?.ledgerHistory : null;
		Server.Instance.Economy?.Initialize(GlobalData.PlayerMoney, GlobalData.PlayerScraps, history);
	}

	[HarmonyPatch(typeof(GlobalData), nameof(GlobalData.AddPlayerExp))]
	[HarmonyPostfix]
	public static void AddPlayerExpHook(int exp, bool instant = false)
	{
		if (!Client.Instance.isConnected) return;
		if (ClientData.Instance.gamemode == Gamemode.Sandbox) return;

		Log.Debug($"Send XP Packet : {GlobalData.PlayerExp} , {GlobalData.PlayerLevel}");
		ClientSend.ExpPacket(GlobalData.PlayerExp, GlobalData.PlayerLevel);
	}
	
	[HarmonyPatch(typeof(UpgradeSystem), nameof(UpgradeSystem.AddPoints))]
	[HarmonyPostfix]
	public static void AddPointsHook(UpgradeSystem __instance)
	{
		if (!Client.Instance.isConnected) return;
		if (ClientData.Instance.gamemode == Gamemode.Sandbox) return;
		
		Log.Debug($"Send Point Packet : {__instance.AvailablePoints}");
		ClientSend.PointPacket(__instance.AvailablePoints);
	}
	
	[HarmonyPatch(typeof(GlobalData), nameof(GlobalData.AddPlayerMoney))]
	[HarmonyPostfix]
	public static void AddPlayerMoneyHook(int money) => Wallet.OnLocalChange(money, 0);

	[HarmonyPatch(typeof(GlobalData), nameof(GlobalData.AddPlayerScraps))]
	[HarmonyPostfix]
	public static void AddPlayerScrapsHook(int amount) => Wallet.OnLocalChange(0, amount);
}