using System.Collections;
using System.Linq;
using CMS.UI.Logic;
using CMS.UI.Logic.RepairPart;
using CMS.UI.Windows;
using CMS21Together.ClientSide.Data.Handle;
using CMS21Together.Shared.Data;
using CMS21Together.Shared.Data.Vanilla;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using CMS21Together.Shared;

namespace CMS21Together.ClientSide.Data.Garage.Tools;

[HarmonyPatch]
public static class RepairPartLogic
{
	public static bool listen = true;
	
	[HarmonyPatch(typeof(RepairPartWindow), nameof(RepairPartWindow.ProcessGameResult))]
	[HarmonyPrefix]
	public static void ProcessGameResultHook(BarType result, RepairPartWindow __instance)
	{
		if(!Client.Instance.isConnected) return;
		if (!listen) { listen = true; return;}

		bool success = result == BarType.Success;
		
		ClientSend.RepairPart(new ModPartInfo(__instance.currentItemInfo), __instance.currentRepairType == RepairType.Body, success);
		Log.Debug("Sent a part repair !");
	}

	public static IEnumerator RepairAction(ModPartInfo info, bool isBody, bool success)
	{
		while (!GameLoadHook.IsGameReady())
			yield return new WaitForSeconds(0.25f);
		yield return new WaitForEndOfFrame();

		if (Player.Inventory.TryGetTracked(info.Item.UID, out var item))
		{
			// update mod item list
			item.Condition = (success ? info.SuccessCondition : info.FailCondition);
			item.RepairAmount++;
			if (isBody)
			{
				item.Dent = (success ? info.DentSuccessCondition : info.DentFailCondition);
			}
			// update game item list to show properly on UI
			Item gameItem = GameData.Instance.localInventory.GetItem(item.UID);
			gameItem.Condition = item.Condition;
			gameItem.RepairAmount = item.RepairAmount;
			if (isBody)
			{
				gameItem.Dent = item.Dent;
			}
		}
	}
}