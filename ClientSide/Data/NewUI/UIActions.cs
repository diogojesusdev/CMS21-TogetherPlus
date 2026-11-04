using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using CMS.MainMenu.Controls;
using CMS.UI.Controls;
using CMS21Together.ClientSide.Data.Handle;
using CMS21Together.ServerSide;
using CMS21Together.ServerSide.Data;
using CMS21Together.Shared;
using CMS21Together.Shared.Data;
using CMS21Together.Shared.Data.Vanilla.Cars;
using CMS21Together.Shared.Persistence;
using MelonLoader;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace CMS21Together.ClientSide.Data.NewUI;

public static class UIActions
{

	public static void StartClient(string username, string address)
	{
		ClientData.UserData.username = username;
		if (ClientData.UserData.selectedNetworkType != NetworkType.Steam)
			ClientData.UserData.ip = address;
		else
			ClientData.UserData.lobbyID = address;
		TogetherModManager.SavePreferences();

		Client.Instance.ResetEvents();
		Client.Instance.OnConnected += () =>
		{
			UICore.ShowPanel(UICore.MP_Lobby);
			if (ClientData.UserData.selectedNetworkType == NetworkType.Steam)
				UILobby.CreateLobby(false, address);
			else
				UILobby.CreateLobby(false, "");
		};
		Client.Instance.OnDisconnected += reason =>
		{
			UICore.ShowPanel(UICore.MP_Main);
			UICustomPanel.CreateInfoPanel(reason);
		};
		if (!Client.Instance.ConnectToServer(ClientData.UserData.selectedNetworkType, address, out var error))
			UICustomPanel.CreateInfoPanel(error);
	}

	/// <summary>Checks the save's files before hosting it (plan §3.5); anything unusual asks first.</summary>
	public static void StartServer(string username, int save_index)
	{
		var save = SavesManager.ModSaves[save_index];
		if (SavesManager.IsUnreadable(save_index))
		{
			UICustomPanel.CreateInfoPanel("This save's mod file could not be read. It was left untouched.");
			return;
		}
		if (PlusSaves.Committer.PendingSaveId != null && PlusSaves.Committer.PendingSaveId == save.saveId)
		{
			UICustomPanel.CreateInfoPanel("This save is still being written. Try again in a few seconds.");
			return;
		}

		SlotCheck check;
		try
		{
			check = SavesManager.CheckSlot(save_index);
		}
		catch (Exception ex)
		{
			Log.Error("[Saves] Checking the save failed", ex);
			UICustomPanel.CreateInfoPanel("The save could not be checked: " + ex.Message);
			return;
		}
		if (check.State != SlotState.Ok && check.State != SlotState.New)
			Log.Info($"[Saves] Slot {save_index}: {check.State}.");

		if (check.State == SlotState.NewerSchema)
		{
			UICustomPanel.CreateInfoPanel(check.Describe());
			return;
		}
		if (!check.CanHost)
		{
			AskAboutSave(username, save_index, check);
			return;
		}
		try
		{
			SavesManager.PrepareForHosting(save_index, check);
		}
		catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
		{
			// The session still starts; this only means no copy was taken before it.
			Log.Error("[Saves] Backing up the save before the session failed", ex);
		}
		PlusSaves.HostDamaged = false;
		HostNow(username, save_index);
	}

	private static void AskAboutSave(string username, int save_index, SlotCheck check)
	{
		var save = SavesManager.ModSaves[save_index];
		var options = new List<(string, Action)>();
		var backup = SaveIds.IsValid(save.saveId) ? PlusSaves.Backups.NewestValid(save.saveId, out _, out _) : null;
		if (backup != null)
			options.Add(("Restore backup", () =>
			{
				if (SavesManager.Restore(save_index, backup, out var error)) StartServer(username, save_index);
				else UICustomPanel.CreateInfoPanel("The restore failed: " + error);
			}));
		options.Add(("Host anyway", () =>
		{
			var damaged = check.State == SlotState.Missing || check.State == SlotState.Corrupt;
			try
			{
				SavesManager.KeepCurrentFiles(save_index, "hosted while " + check.State.ToString().ToLowerInvariant());
			}
			catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
			{
				Log.Error("[Saves] Keeping a copy of the current files failed", ex);
			}
			PlusSaves.HostDamaged = damaged;
			HostNow(username, save_index);
		}));
		options.Add(("Cancel", null));

		var detail = backup != null
			? $" The newest good backup is {backup.Meta} ({backup.Meta.CreatedUtc.ToLocalTime():g})."
			: " There is no backup of it; a player who played it may still have a copy (host anyway and let them join).";
		UICustomPanel.CreateChoicePanel("Save problem", check.Describe() + detail + " Nothing is overwritten without a copy.", options.ToArray());
	}

	private static void HostNow(string username, int save_index)
	{
		ClientData.UserData.username = username;
		TogetherModManager.SavePreferences();

		Client.Instance.ResetEvents();
		Client.Instance.OnConnected += () =>
		{
			UICore.ShowPanel(UICore.MP_Lobby);
			if (ClientData.UserData.selectedNetworkType == NetworkType.Steam)
				UILobby.CreateLobby(true, Server.Instance.serverID, save_index);
			else
				UILobby.CreateLobby(true, "", save_index);
		};
		Client.Instance.OnDisconnected += reason =>
		{
			UICore.ShowPanel(UICore.MP_Main);
			UICustomPanel.CreateInfoPanel(reason);
		};
		if (!Server.Instance.StartServer(ClientData.UserData.selectedNetworkType, out var error))
		{
			UICustomPanel.CreateInfoPanel("Could not host: " + error);
			return;
		}
		SavesManager.LoadSave(SavesManager.ModSaves[save_index]);
	}
		public static UnityAction ChangeNetworkType(MainMenuButton button)
	{
		Action action = () =>
		{
			switch (ClientData.UserData.selectedNetworkType)
			{
				case NetworkType.Steam:
					ClientData.UserData.selectedNetworkType = NetworkType.TCP;
					break;
				case NetworkType.TCP:
					ClientData.UserData.selectedNetworkType = NetworkType.Steam;
					break;
			}

			button.text.text = $"Network type: {ClientData.UserData.selectedNetworkType}";
			button.text.OnEnable();
		};
		return action;
	}

	public static UnityAction LoadGame(MainMenuButton button, int save_index)
	{
		Action action = () =>
		{
			if (UIUtils.GetSaveName(save_index) != "New game" && UICore.last_index_pressed != save_index)
				UICore.ShowCustomPanel(UICore.MP_Host.transform, UICustomPanelType.SaveInfo, button, save_index);
			else if (UIUtils.GetSaveName(save_index) != "New game" && UICore.last_index_pressed == save_index)
				UICore.ShowCustomPanel(UICore.MP_Host.transform, UICustomPanelType.JoinAsHostMenu, button, save_index);
			else
				UICore.ShowCustomPanel(UICore.MP_Host.transform,UICustomPanelType.CreateSave, button, save_index);
		};
		return action;
	}

	public static void CreateNewSave(InputField input, StringSelector selector, MainMenuButton btn, int index)
	{
		if (SavesManager.IsUnreadable(index))
		{
			UICustomPanel.CreateInfoPanel("This slot's mod file could not be read. It was left untouched; use another slot.");
			return;
		}
		if (SavesManager.ModSaves.Any(s => s.Value.Name == input.text))
		{
			UICustomPanel.CreateInfoPanel("A save with the same name already exist.");
			return;
		}
		if (!SavesManager.KeepOrphanProfile(index))
		{
			UICustomPanel.CreateInfoPanel("This slot holds a game save that could not be backed up. It was left untouched; use another slot.");
			return;
		}

		SavesManager.ModSaves[index].Name = input.text;
		SavesManager.ModSaves[index].selectedGamemode = SavesManager.GetGamemodeFromInt(selector.Current);
		btn.text.text = input.text;
		btn.text.OnEnable();
		SavesManager.SaveModSave(index);
		UnityEngine.Object.Destroy(UICore.TMP_Window);
	}

	public static void DeleteSave(MainMenuButton button, int save_index)
	{
		SavesManager.RemoveModSave(save_index);

		button.GetComponentInChildren<Text>().text = "New Game";
		button.OnEnable();
	}

	public static UnityAction PreviousSaves(MainMenuButton button, MainMenuButton next_button)
	{
		Action action = () =>
		{
			if (UIMenu.save_btn_index == 0)
			{
				button.SetDisabled(true, true);
				return;
			}
			UIMenu.save_btn_index -= 4;

			UIUtils.DestroySavesButton();
			Vector2 b_pos = new Vector2(0, 344);

			int i = UIMenu.save_btn_index;
			while (i < UIMenu.save_btn_index + 4 && i < 16)
			{
				var saveBtn = UIElements.CreateButton(UICore.MP_Host.transform, UIUtils.GetSaveName(i + 4), null);
				var saveRect = saveBtn.GetComponent<RectTransform>();
				saveRect.anchorMin = new Vector2(0f, 0.5f);
				saveRect.anchorMax = new Vector2(0f, 0.5f);
				saveRect.pivot = new Vector2(0f, 0.5f);
				saveRect.sizeDelta = new Vector2(233, 44);
				saveRect.anchoredPosition = b_pos;
				saveBtn.transform.SetSiblingIndex(i % 4);
				
				saveBtn.OnClick.AddListener(UIActions.LoadGame(saveBtn, i + 4));
				saveBtn.SetLocked(false);
				saveBtn.SetDisabled(false, true);
				
				b_pos.y -= 49;
				i++;
			}

			if (UIMenu.save_btn_index == 0)
				button.SetDisabled(true, true);
			else
				button.SetDisabled(false, true);
			
			next_button.SetLocked(false);
			next_button.SetDisabled(false);
		};
		return action;
	}
	
	public static UnityAction NextSaves(MainMenuButton button, MainMenuButton prev_button)
	{
		Action action = () =>
		{
			if (UIMenu.save_btn_index >= 12)
			{
				button.SetDisabled(true, true);
				return;
			}
			UIMenu.save_btn_index += 4;

			UIUtils.DestroySavesButton();
			Vector2 b_pos = new Vector2(0, 344);

			int i = UIMenu.save_btn_index;
			while (i < UIMenu.save_btn_index + 4 && i < 16)
			{
				var saveBtn = UIElements.CreateButton(UICore.MP_Host.transform, UIUtils.GetSaveName(i + 4), null);
				var saveRect = saveBtn.GetComponent<RectTransform>();
				saveRect.anchorMin = new Vector2(0f, 0.5f);
				saveRect.anchorMax = new Vector2(0f, 0.5f);
				saveRect.pivot = new Vector2(0f, 0.5f);
				saveRect.sizeDelta = new Vector2(233, 44);
				saveRect.anchoredPosition = b_pos;
				saveBtn.transform.SetSiblingIndex(i % 4);
				
				saveBtn.OnClick.AddListener(UIActions.LoadGame(saveBtn, i + 4));
				saveBtn.SetLocked(false);
				saveBtn.SetDisabled(false, true);
				
				b_pos.y -= 49;
				i++;
			}

			if (UIMenu.save_btn_index >= 12)
				button.SetDisabled(true, true);
			else
				button.SetDisabled(false, true);
			
			prev_button.SetLocked(false);
			prev_button.SetDisabled(false);
		};
		return action;
	}

	public static UnityAction SwitchReady(MainMenuButton btn)
	{
		Action action = () =>
		{
			foreach (var i in ClientData.Instance.connectedClients.Keys)
			{
				var player = ClientData.Instance.connectedClients[i];
				if (player != null)
					if (player.playerID == ClientData.UserData.playerID)
					{
						player.isReady = !player.isReady;
						ClientSend.ReadyPacket(player.isReady, i);
						if (player.isReady)
							btn.text.text = "Unready";
						else
							btn.text.text = "Ready Up";
						btn.text.OnEnable();
					}
			}
		};
		return action;
	}

	public static void StartGame(int save_index)
	{
		if (ServerData.Instance == null) return;

		foreach (var player in ServerData.Instance.connectedClients.Values)
		{
			if (player != null && !player.isReady)
			{
				UICustomPanel.CreateInfoPanel("All player are not ready.");
				return;
			}
		}
		MelonCoroutines.Start(GameLaunch(save_index));
	}

	private static IEnumerator GameLaunch(int save_index)
	{
		yield return new WaitForEndOfFrame();
		
		SavesManager.StartGame(save_index);
		int i = 0;
		Dictionary<int, ModNewCarData> parksCars = new Dictionary<int, ModNewCarData>();
		foreach (NewCarData carData in SavesManager.currentSave.carsOnParking)
		{
			if (carData != null && !String.IsNullOrEmpty(carData.carToLoad))
			{
				parksCars.Add(i, new ModNewCarData(carData));
			}
			i++;	
		}
		ServerSend.StartPacket(SavesManager.ModSaves[save_index].selectedGamemode, parksCars);
		Server.Instance.GameStarted = true;
		
		SavesManager.ModSaves[save_index].alreadyLoaded = true;
		if (SavesManager.ModSaves[save_index].additionnalStand != null)
			ServerData.Instance.engineStand2 = SavesManager.ModSaves[save_index].additionnalStand;
		SavesManager.SaveModSave(save_index);
	}
}