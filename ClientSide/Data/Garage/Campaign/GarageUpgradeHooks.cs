using System.Collections;
using CMS.UI.Logic;
using CMS.UI.Logic.Navigation;
using CMS.UI.Logic.Upgrades;
using CMS21Together.ClientSide.Data.Handle;
using CMS21Together.ClientSide.Data.Player;
using CMS21Together.ServerSide;
using CMS21Together.Shared;
using CMS21Together.Shared.Data;
using CMS21Together.Shared.Data.Vanilla;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace CMS21Together.ClientSide.Data.Garage.Campaign;

[HarmonyPatch]
public static class GarageUpgradeHooks
{
	public static bool listenToUpgrades = true;
	public static bool sentInitial;
	public static bool receivedInitial;
	
	public static void Reset()
	{
		listenToUpgrades = false;
		sentInitial = false;
		receivedInitial = false;
	}


	/*[HarmonyPatch(typeof(GarageAndToolsTab), nameof(GarageAndToolsTab.SwitchInteractiveObjects))]
	[HarmonyPrefix]
	public static void SwitchInteractiveObjectsHook(string upgradeID, bool on)
	{
		if (!Client.Instance.isConnected || !listenToUpgrades)
		{
			listenToUpgrades = true;
			return;
		}
		if (SavesManager.currentSave.Difficulty == DifficultyLevel.Sandbox) return;

		Log.Debug($"[GarageUpgradeHooks-> SwitchInteractiveObjectsHook] Triggered: {upgradeID}, {on}");
		ClientData.Instance.garageUpgrades[upgradeID] = new GarageUpgrade(upgradeID, on);
		ClientSend.GarageUpgradePacket(ClientData.Instance.garageUpgrades[upgradeID]);
	}*/


	
	/// <summary>
	/// Garage upgrades are paid from the shared wallet and cannot be undone in the game, so they are pre-authorized
	/// (Phase 6): the first click only asks the host to charge; once it has, the unlock runs again, paid.
	/// </summary>
	[HarmonyPatch(typeof(GarageAndToolsTab), nameof(GarageAndToolsTab.UnlockCurrentSelectedSkillAction))]
	[HarmonyPrefix]
	public static bool UnlockCurrentSelectedSkillActionHook(GarageAndToolsTab __instance=null)
	{
		if (!Client.Instance.isConnected || !listenToUpgrades)
		{
			if (sentInitial || receivedInitial) listenToUpgrades = true;
			return true;
		}
		
		if (SavesManager.currentSave.Difficulty == DifficultyLevel.Sandbox) return true;
		if (__instance == null || __instance.currentUpgradeItem == null) return true;

		var item = __instance.currentUpgradeItem;
		string upgradeName = item.upgradeID + item.UpgradeLevel;
		int upgradeCost = __instance.upgradeSystem.GetUpgradeCost(item.UpgradeID, item.UpgradeLevel, UpgradeType.Money);
		if (Purchases.AuthorizedUpgrade == upgradeName)
		{
			if (item.UpgradeID == "crane")
				GameData.Instance.engineStandLogic2.gameObject.SetActive(true);
			ClientData.Instance.garageUpgrades[upgradeName] = new GarageUpgrade(item.upgradeID, item.UpgradeLevel, true);
			ClientSend.GarageUpgradePacket(ClientData.Instance.garageUpgrades[upgradeName], true);
			return true;
		}

		// Not enough money: the game says so itself. Otherwise nothing happens until the host has charged.
		if (upgradeCost > GlobalData.PlayerMoney) return true;
		Purchases.AskUpgrade(__instance, item.upgradeID, item.UpgradeLevel, upgradeCost);
		return false;
	}

	public static IEnumerator SendInitial()
	{
		if (sentInitial || !Server.Instance.isRunning) yield break;

		while (!GameLoadHook.IsGameReady())
			yield return new WaitForSeconds(0.2f);
		yield return new WaitForEndOfFrame();
		yield return new WaitForEndOfFrame();

		
		if (GameData.Instance == null) {
			Log.Error("CRITICAL: GameData.Instance is NULL. Are you in the main menu?");
			yield break;
		}
		GarageAndToolsTab tools = GameData.Instance.garageTools;
		if (tools == null) {
			Log.Error("CRITICAL: tools (GarageTools) is NULL!");
			yield break;
		}
		if (tools.upgradeSystem == null || tools.upgradeItems == null) {
			Log.Error($"Internal refs null: System={tools.upgradeSystem==null}, Items={tools.upgradeItems==null}");
			yield break;
		}
		
		GameData.Instance.garageTools.PrepareItems();
		yield return new WaitForEndOfFrame();
		
		foreach (UpgradeItem item in GameData.Instance.garageTools.upgradeItems)
		{
			//Log.Info($"Upgrade : {item.upgradeID} , state : {item.upgradeState}.");
			ClientData.Instance.garageUpgrades[item.upgradeID] = new GarageUpgrade(item.upgradeID, item.UpgradeLevel, item.upgradeState == UpgradeState.Unlocked);
			ClientSend.GarageUpgradePacket(ClientData.Instance.garageUpgrades[item.upgradeID]);
			
			if (item.upgradeID == "crane" && item.upgradeState != UpgradeState.Unlocked)
				GameData.Instance.engineStandLogic2.gameObject.SetActive(false);
			else if (item.upgradeID == "crane" && item.upgradeState == UpgradeState.Unlocked)
				GameData.Instance.engineStandLogic2.gameObject.SetActive(true);
		}
		ClientSend.GarageUpgradePacket(new GarageUpgrade("initialSent", 0, false));
		yield return new WaitForEndOfFrame();
		sentInitial = true;
		listenToUpgrades = true;
		Log.Debug($"[GarageUpgradeHooks->SendInitial] Sent initials upgrades to server");
	}
}