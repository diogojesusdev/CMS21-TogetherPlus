using System;
using System.Linq;
using CMS.MainMenu.Controls;
using CMS21Together.Shared;
using CMS21Together.Shared.Data;
using CMS21Together.Shared.Persistence;
using Il2CppSystem.Collections.Generic;
using Il2CppSystem.Globalization;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace CMS21Together.ClientSide.Data.NewUI;

public static class UICustomPanel
{
	public static void CreateSplitter(Transform parent, Vector2 pos, Vector2 size)
	{
		var splitter = new GameObject("splitter");
		splitter.transform.SetParent(parent, false);
		var splitterImg = splitter.AddComponent<Image>();
		splitterImg.color = new Color(1f, 1f, 1f, 0.5f);
		splitterImg.rectTransform.anchorMin = new Vector2(0.5f, 1f);
		splitterImg.rectTransform.anchorMax = new Vector2(0.5f, 1f);
		splitterImg.rectTransform.pivot = new Vector2(0.5f, 1f);
		splitterImg.rectTransform.sizeDelta = size;
		splitterImg.rectTransform.anchoredPosition = pos;
	}
	
	public static void CreateSaveInfoPanel(MainMenuButton button, int index)
	{
		UICore.last_index_pressed = index;
		if (UICore.TMP_Window)
			Object.Destroy(UICore.TMP_Window);
		UICore.TMP_Window = new GameObject("SaveInfoWindow");
		UICore.TMP_Window.transform.SetParent(UICore.UI_Main.transform, false);

		var panelRect = UICore.TMP_Window.AddComponent<RectTransform>();
		panelRect.anchorMin = new Vector2(0.5f, 0.5f);
		panelRect.anchorMax = new Vector2(0.5f, 0.5f);
		panelRect.pivot = new Vector2(0.5f, 0.5f);
		panelRect.sizeDelta = new Vector2(400, 250);
		panelRect.anchoredPosition = Vector2.zero;

		var img = UICore.TMP_Window.AddComponent<Image>();
		img.color = new Color(.031f, .027f, .033f, 0.85f);
		
		var saveTxt = UIElements.CreateText(UICore.TMP_Window.transform, "Save Info", 24);
		var saveTxtRect = saveTxt.GetComponent<RectTransform>();
		saveTxtRect.anchorMin = new Vector2(0.5f, 1f);
		saveTxtRect.anchorMax = new Vector2(0.5f, 1f);
		saveTxtRect.pivot = new Vector2(0.5f, 1f);
		saveTxtRect.sizeDelta = new Vector2(230, 45);
		saveTxtRect.anchoredPosition = new Vector2(-50, 0);

		CreateSplitter(UICore.TMP_Window.transform, new Vector2(0, -40), new(390, 2));

		string lastSave = "Never";
		string time = "0 min";
		ModSaveData data = SavesManager.ModSaves[index];
		if (data != null && data.alreadyLoaded)
		{
			var timePlayed = TimeSpan.FromMinutes(SavesManager.profileData[data.saveIndex].PlayTime);
			if (timePlayed.TotalHours >= 1)
				time = $"{Math.Round(timePlayed.TotalHours)} h";
			else if (timePlayed.TotalMinutes >= 1.0)
				time = $"{Math.Round(timePlayed.TotalMinutes)} min";
			else
				time = "less than 1 min";
			
			var currentCulture = CultureInfo.CurrentCulture;
			CultureInfo.CurrentCulture = GlobalData.DefaultCultureInfo;
			lastSave = DateTimeOffset.FromUnixTimeSeconds(
				Convert.ToInt64(SavesManager.profileData[data.saveIndex].LastSave)).ToLocalTime().DateTime.ToString("g");
			CultureInfo.CurrentCulture = currentCulture;
		}
		var nameTxt = UIElements.CreateText(UICore.TMP_Window.transform, "Name : " + data?.Name, 20);
		var nameTxtRect = nameTxt.GetComponent<RectTransform>();
		nameTxtRect.anchorMin = new Vector2(0f, 1f);
		nameTxtRect.anchorMax = new Vector2(0f, 1f);
		nameTxtRect.pivot = new Vector2(0f, 1f);
		nameTxtRect.sizeDelta = new Vector2(230, 45);
		nameTxtRect.anchoredPosition = new Vector2(10, -50);
		
		var gmTxt = UIElements.CreateText(UICore.TMP_Window.transform, "Gamemode : " + data?.selectedGamemode, 20);
		var gmTxtRect = gmTxt.GetComponent<RectTransform>();
		gmTxtRect.anchorMin = new Vector2(0f, 1f);
		gmTxtRect.anchorMax = new Vector2(0f, 1f);
		gmTxtRect.pivot = new Vector2(0f, 1f);
		gmTxtRect.sizeDelta = new Vector2(230, 45);
		gmTxtRect.anchoredPosition = new Vector2(10, -80);
		
		var timeTxt = UIElements.CreateText(UICore.TMP_Window.transform, "Time Played : " + time, 20);
		var timeTxtRect = timeTxt.GetComponent<RectTransform>();
		timeTxtRect.anchorMin = new Vector2(0f, 1f);
		timeTxtRect.anchorMax = new Vector2(0f, 1f);
		timeTxtRect.pivot = new Vector2(0f, 1f);
		timeTxtRect.sizeDelta = new Vector2(300, 45);
		timeTxtRect.anchoredPosition = new Vector2(9.5f, -108);
		
		var lsaveTxt = UIElements.CreateText(UICore.TMP_Window.transform, "Last save : " + lastSave, 20);
		var lsaveTxtRect = lsaveTxt.GetComponent<RectTransform>();
		lsaveTxtRect.anchorMin = new Vector2(0f, 1f);
		lsaveTxtRect.anchorMax = new Vector2(0f, 1f);
		lsaveTxtRect.pivot = new Vector2(0f, 1f);
		lsaveTxtRect.sizeDelta = new Vector2(230, 45);
		lsaveTxtRect.anchoredPosition = new Vector2(10, -140);
		
		CreateSplitter(UICore.TMP_Window.transform, new Vector2(0, -185), new(390, 2));
		
		var backBtn = UIElements.CreateButton(UICore.TMP_Window.transform, "Delete Save", 
		() => { UIActions.DeleteSave(button, index); Object.Destroy(UICore.TMP_Window); });
		var backRect = backBtn.GetComponent<RectTransform>();
		backRect.anchorMin = new Vector2(0.5f, 0f);
		backRect.anchorMax = new Vector2(0.5f, 0f);
		backRect.pivot = new Vector2(0.5f, 0f);
		backRect.sizeDelta = new Vector2(233, 44);
		backRect.anchoredPosition = new Vector2(0, 10);
	}

	public static void CreateInfoPanel(string msg)
	{
		UIUtils.SwitchPanelButton(UICore.Active_Panel.transform, true);
		if (UICore.TMP_Info_Window)
			Object.Destroy(UICore.TMP_Window);
		UICore.TMP_Info_Window = new GameObject("NoticeWindow");
		UICore.TMP_Info_Window.transform.SetParent(UICore.UI_Main.transform, false);

		var panelRect = UICore.TMP_Info_Window.AddComponent<RectTransform>();
		panelRect.anchorMin = new Vector2(0.5f, 0.5f);
		panelRect.anchorMax = new Vector2(0.5f, 0.5f);
		panelRect.pivot = new Vector2(0.5f, 0.5f);
		panelRect.sizeDelta = new Vector2(400, 200);
		panelRect.anchoredPosition = Vector2.zero;

		var img = UICore.TMP_Info_Window.AddComponent<Image>();
		img.color = new Color(.031f, .027f, .033f, 0.98f);
		
		
		var label = UIElements.CreateText(UICore.TMP_Info_Window.transform, "Notice", 24);
		var labelRect = label.GetComponent<RectTransform>();
		labelRect.anchorMin = new Vector2(1, 1f);
		labelRect.anchorMax = new Vector2(1, 1f);
		labelRect.pivot = new Vector2(1, 1f);
		labelRect.sizeDelta = new Vector2(230, 45);
		labelRect.anchoredPosition = new Vector2(-12, 0);
		
		CreateSplitter(UICore.TMP_Info_Window.transform, new Vector2(0, -40), new(390, 2));
		
		var txt = UIElements.CreateText(UICore.TMP_Info_Window.transform, msg, 18);
		txt.alignment = TextAnchor.MiddleCenter;
		var txtRect = txt.GetComponent<RectTransform>();
		txtRect.anchorMin = new Vector2(0.5f, 1f);
		txtRect.anchorMax = new Vector2(0.5f, 1f);
		txtRect.pivot = new Vector2(0.5f, 1f);
		txtRect.sizeDelta = new Vector2(230, 90);
		txtRect.anchoredPosition = new Vector2(0, -50);
		
		CreateSplitter(UICore.TMP_Info_Window.transform, new Vector2(0, -140), new(390, 2));
		
		var confirmBtn = UIElements.CreateButton(UICore.TMP_Info_Window.transform,
			"Confirm", (() => { UIUtils.SwitchPanelButton(UICore.Active_Panel.transform, false); Object.Destroy(UICore.TMP_Info_Window); }));
		var confirmRect = confirmBtn.GetComponent<RectTransform>();
		confirmRect.anchorMin = new Vector2(1f, 0f);
		confirmRect.anchorMax = new Vector2(1f, 0f);
		confirmRect.pivot = new Vector2(1f, 0f);
		confirmRect.sizeDelta = new Vector2(133, 44);
		confirmRect.anchoredPosition = new Vector2(-130, 5);
		
		Log.Info("Notice : " + msg); 
	}

	private static GameObject choiceWindow;

	/// <summary>The version picker: option 0 is Latest, then one entry per published release.</summary>
	public static void CreateVersionPanel(List<string> options, int selected, Action<int> applied)
	{
		if (choiceWindow) Object.Destroy(choiceWindow);
		var behind = UICore.Active_Panel;
		if (behind) UIUtils.SwitchPanelButton(behind.transform, true);

		choiceWindow = new GameObject("VersionWindow");
		choiceWindow.transform.SetParent(UICore.UI_Main.transform, false);
		var window = choiceWindow;

		var panelRect = window.AddComponent<RectTransform>();
		panelRect.anchorMin = new Vector2(0.5f, 0.5f);
		panelRect.anchorMax = new Vector2(0.5f, 0.5f);
		panelRect.pivot = new Vector2(0.5f, 0.5f);
		panelRect.sizeDelta = new Vector2(600, 340);
		panelRect.anchoredPosition = Vector2.zero;
		var img = window.AddComponent<Image>();
		img.color = new Color(.031f, .027f, .033f, 0.98f);

		var title = UIElements.CreateText(window.transform, "Together+ version", 24, TextAnchor.MiddleCenter);
		var titleRect = title.GetComponent<RectTransform>();
		titleRect.anchorMin = new Vector2(0.5f, 1f);
		titleRect.anchorMax = new Vector2(0.5f, 1f);
		titleRect.pivot = new Vector2(0.5f, 1f);
		titleRect.sizeDelta = new Vector2(560, 45);
		titleRect.anchoredPosition = Vector2.zero;
		CreateSplitter(window.transform, new Vector2(0, -40), new(590, 2));

		var info = UIElements.CreateText(window.transform,
			"Installed: " + MainMod.PLUS_VERSION + "\n" +
			"Latest follows new releases. Pinning keeps a version until you change it; it is installed on the next launch.",
			15, TextAnchor.MiddleCenter);
		info.horizontalOverflow = HorizontalWrapMode.Wrap;
		info.verticalOverflow = VerticalWrapMode.Truncate;
		var infoRect = info.GetComponent<RectTransform>();
		infoRect.anchorMin = new Vector2(0.5f, 1f);
		infoRect.anchorMax = new Vector2(0.5f, 1f);
		infoRect.pivot = new Vector2(0.5f, 1f);
		infoRect.sizeDelta = new Vector2(560, 70);
		infoRect.anchoredPosition = new Vector2(0, -48);

		var selector = UIElements.CreateSelector(window.transform, options);
		selector.EnableArrows();
		selector.SetValue(Math.Max(0, Math.Min(selected, options.Count - 1)));
		var selectorRect = selector.GetComponent<RectTransform>();
		selectorRect.anchorMin = new Vector2(0.5f, 1f);
		selectorRect.anchorMax = new Vector2(0.5f, 1f);
		selectorRect.pivot = new Vector2(0.5f, 1f);
		selectorRect.sizeDelta = new Vector2(440, 65);
		selectorRect.anchoredPosition = new Vector2(0, -125);

		CreateSplitter(window.transform, new Vector2(0, -200), new(590, 2));

		void Close()
		{
			if (behind) UIUtils.SwitchPanelButton(behind.transform, false);
			if (window) Object.Destroy(window);
		}

		var applyBtn = UIElements.CreateButton(window.transform, "Apply", () =>
		{
			var index = selector.Current;
			Close();
			try
			{
				applied?.Invoke(index);
			}
			catch (Exception ex)
			{
				Log.Error("[UI] Applying the version failed", ex);
			}
		});
		var applyRect = applyBtn.GetComponent<RectTransform>();
		applyRect.anchorMin = new Vector2(0.5f, 0f);
		applyRect.anchorMax = new Vector2(0.5f, 0f);
		applyRect.pivot = new Vector2(0.5f, 0f);
		applyRect.sizeDelta = new Vector2(180, 44);
		applyRect.anchoredPosition = new Vector2(-100, 8);

		var cancelBtn = UIElements.CreateButton(window.transform, "Cancel", Close);
		var cancelRect = cancelBtn.GetComponent<RectTransform>();
		cancelRect.anchorMin = new Vector2(0.5f, 0f);
		cancelRect.anchorMax = new Vector2(0.5f, 0f);
		cancelRect.pivot = new Vector2(0.5f, 0f);
		cancelRect.sizeDelta = new Vector2(180, 44);
		cancelRect.anchoredPosition = new Vector2(100, 8);
	}


	/// <summary>A question with buttons. Each button closes the window, then runs its action (null = just close).</summary>
	public static void CreateChoicePanel(string title, string msg, params (string label, Action action)[] options)
	{
		if (choiceWindow) Object.Destroy(choiceWindow);
		var behind = UICore.Active_Panel;
		if (behind) UIUtils.SwitchPanelButton(behind.transform, true);
		choiceWindow = new GameObject("ChoiceWindow");
		choiceWindow.transform.SetParent(UICore.UI_Main.transform, false);
		var window = choiceWindow;

		var panelRect = window.AddComponent<RectTransform>();
		panelRect.anchorMin = new Vector2(0.5f, 0.5f);
		panelRect.anchorMax = new Vector2(0.5f, 0.5f);
		panelRect.pivot = new Vector2(0.5f, 0.5f);
		panelRect.sizeDelta = new Vector2(480, 270);
		panelRect.anchoredPosition = Vector2.zero;
		var img = window.AddComponent<Image>();
		img.color = new Color(.031f, .027f, .033f, 0.98f);

		var label = UIElements.CreateText(window.transform, title, 24, TextAnchor.MiddleCenter);
		var labelRect = label.GetComponent<RectTransform>();
		labelRect.anchorMin = new Vector2(0.5f, 1f);
		labelRect.anchorMax = new Vector2(0.5f, 1f);
		labelRect.pivot = new Vector2(0.5f, 1f);
		labelRect.sizeDelta = new Vector2(450, 45);
		labelRect.anchoredPosition = Vector2.zero;
		CreateSplitter(window.transform, new Vector2(0, -40), new(470, 2));

		var txt = UIElements.CreateText(window.transform, msg, 16, TextAnchor.MiddleCenter);
		txt.horizontalOverflow = HorizontalWrapMode.Wrap;
		txt.verticalOverflow = VerticalWrapMode.Truncate;
		var txtRect = txt.GetComponent<RectTransform>();
		txtRect.anchorMin = new Vector2(0.5f, 1f);
		txtRect.anchorMax = new Vector2(0.5f, 1f);
		txtRect.pivot = new Vector2(0.5f, 1f);
		txtRect.sizeDelta = new Vector2(450, 160);
		txtRect.anchoredPosition = new Vector2(0, -45);
		CreateSplitter(window.transform, new Vector2(0, -210), new(470, 2));

		const float spacing = 150f;
		var x = -(options.Length - 1) * spacing / 2f;
		foreach (var (text, action) in options)
		{
			var button = UIElements.CreateButton(window.transform, text, () =>
			{
				if (behind) UIUtils.SwitchPanelButton(behind.transform, false);
				if (window) Object.Destroy(window);
				try
				{
					action?.Invoke();
				}
				catch (Exception ex)
				{
					Log.Error($"[UI] \"{text}\" failed", ex);
				}
			});
			var rect = button.GetComponent<RectTransform>();
			rect.anchorMin = new Vector2(0.5f, 0f);
			rect.anchorMax = new Vector2(0.5f, 0f);
			rect.pivot = new Vector2(0.5f, 0f);
			rect.sizeDelta = new Vector2(140, 44);
			rect.anchoredPosition = new Vector2(x, 8);
			x += spacing;
		}
		Log.Info($"Question : {title}: {msg}");
	}

	/// <summary>Recovery path 2: copies of saves this player hosted or joined, to put into an empty slot.</summary>
	public static void CreateBackupListPanel(MainMenuButton btn, int index)
	{
		var entries = new System.Collections.Generic.List<(BundleInfo info, string source)>();
		void Collect(BundleStore store, string source)
		{
			try
			{
				foreach (var info in store.ListAll()) entries.Add((info, source));
			}
			catch (Exception ex)
			{
				Log.Warn($"[Saves] Could not list {store.Root}: {ex.Message}");
			}
		}
		Collect(PlusSaves.Backups, "backup");
		Collect(PlusSaves.Replicas, "copy from a session");
		Collect(PlusSaves.Received, "copy from a player");
		Collect(PlusSaves.Safety, "kept copy");

		// The newest copy of each save that is not in a slot already.
		var inSlots = new System.Collections.Generic.HashSet<string>(SavesManager.ModSaves.Values
			.Where(s => s != null && s.alreadyLoaded && !string.IsNullOrEmpty(s.saveId)).Select(s => s.saveId));
		var shown = entries
			.Where(e => !inSlots.Contains(e.info.Meta.SaveId))
			.GroupBy(e => e.info.Meta.SaveId)
			.Select(g => g.OrderByDescending(e => e.info.Meta.Generation).ThenByDescending(e => e.info.Meta.CreatedUtc).First())
			.OrderByDescending(e => e.info.Meta.CreatedUtc)
			.Take(6)
			.ToArray();
		if (shown.Length == 0)
		{
			CreateInfoPanel("No backups or session copies were found.");
			return;
		}

		if (UICore.TMP_Window)
			Object.Destroy(UICore.TMP_Window);
		UICore.TMP_Window = new GameObject("BackupListWindow");
		UICore.TMP_Window.transform.SetParent(UICore.UI_Main.transform, false);
		var window = UICore.TMP_Window;

		var panelRect = window.AddComponent<RectTransform>();
		panelRect.anchorMin = new Vector2(0.5f, 0.5f);
		panelRect.anchorMax = new Vector2(0.5f, 0.5f);
		panelRect.pivot = new Vector2(0.5f, 0.5f);
		panelRect.sizeDelta = new Vector2(520, 110 + shown.Length * 49);
		panelRect.anchoredPosition = Vector2.zero;
		var img = window.AddComponent<Image>();
		img.color = new Color(.031f, .027f, .033f, 0.95f);

		var title = UIElements.CreateText(window.transform, "Restore a backup", 24, TextAnchor.MiddleCenter);
		var titleRect = title.GetComponent<RectTransform>();
		titleRect.anchorMin = new Vector2(0.5f, 1f);
		titleRect.anchorMax = new Vector2(0.5f, 1f);
		titleRect.pivot = new Vector2(0.5f, 1f);
		titleRect.sizeDelta = new Vector2(490, 45);
		titleRect.anchoredPosition = Vector2.zero;
		CreateSplitter(window.transform, new Vector2(0, -40), new(510, 2));

		var y = -50f;
		foreach (var (info, source) in shown)
		{
			var meta = info.Meta;
			var name = string.IsNullOrEmpty(meta.Name) ? "(no name)" : meta.Name;
			if (name.Length > 22) name = name.Substring(0, 22) + "…";
			var path = info.Path;
			var row = UIElements.CreateButton(window.transform, $"{name} | {meta.CreatedUtc.ToLocalTime():g} | {source}",
				() => ImportBackup(btn, index, path));
			var rowRect = row.GetComponent<RectTransform>();
			rowRect.anchorMin = new Vector2(0.5f, 1f);
			rowRect.anchorMax = new Vector2(0.5f, 1f);
			rowRect.pivot = new Vector2(0.5f, 1f);
			rowRect.sizeDelta = new Vector2(500, 44);
			rowRect.anchoredPosition = new Vector2(0, y);
			y -= 49;
		}

		var cancel = UIElements.CreateButton(window.transform, "Cancel", () => { if (UICore.TMP_Window) Object.Destroy(UICore.TMP_Window); });
		var cancelRect = cancel.GetComponent<RectTransform>();
		cancelRect.anchorMin = new Vector2(0.5f, 0f);
		cancelRect.anchorMax = new Vector2(0.5f, 0f);
		cancelRect.pivot = new Vector2(0.5f, 0f);
		cancelRect.sizeDelta = new Vector2(133, 44);
		cancelRect.anchoredPosition = new Vector2(0, 8);
	}

	private static void ImportBackup(MainMenuButton btn, int index, string path)
	{
		if (UICore.TMP_Window) Object.Destroy(UICore.TMP_Window);
		if (!BundleStore.TryLoad(path, out var bundle, out _, out var error))
		{
			CreateInfoPanel("This copy is damaged and was not used: " + error);
			return;
		}
		if (!SavesManager.Import(index, bundle, out error))
		{
			CreateInfoPanel(error);
			return;
		}
		btn.text.text = SavesManager.ModSaves[index].Name;
		btn.text.OnEnable();
		CreateInfoPanel($"\"{SavesManager.ModSaves[index].Name}\" is back. Select it to host it.");
	}

	public static void CreateNewSavePanel(MainMenuButton btn, int index)
	{
		if (UICore.TMP_Window)
			Object.Destroy(UICore.TMP_Window);
		UICore.TMP_Window = new GameObject("CreateSaveWindow");
		UICore.TMP_Window.transform.SetParent(UICore.UI_Main.transform, false);

		var panelRect = UICore.TMP_Window.AddComponent<RectTransform>();
		panelRect.anchorMin = new Vector2(0.5f, 0.5f);
		panelRect.anchorMax = new Vector2(0.5f, 0.5f);
		panelRect.pivot = new Vector2(0.5f, 0.5f);
		panelRect.sizeDelta = new Vector2(400, 300);
		panelRect.anchoredPosition = Vector2.zero;

		var img = UICore.TMP_Window.AddComponent<Image>();
		img.color = new Color(.031f, .027f, .033f, 0.85f);

		
		var saveTxt = UIElements.CreateText(UICore.TMP_Window.transform, "Create Save", 24);
		var saveTxtRect = saveTxt.GetComponent<RectTransform>();
		saveTxtRect.anchorMin = new Vector2(0.5f, 1f);
		saveTxtRect.anchorMax = new Vector2(0.5f, 1f);
		saveTxtRect.pivot = new Vector2(0.5f, 1f);
		saveTxtRect.sizeDelta = new Vector2(230, 45);
		saveTxtRect.anchoredPosition = new Vector2(-70, 0);

		CreateSplitter(UICore.TMP_Window.transform, new Vector2(0, -40), new(390, 2));
		
		var nameTxt = UIElements.CreateText(UICore.TMP_Window.transform, "Save Name : ", 22);
		var nameTxtRect = nameTxt.GetComponent<RectTransform>();
		nameTxtRect.anchorMin = new Vector2(0f, 1f);
		nameTxtRect.anchorMax = new Vector2(0f, 1f);
		nameTxtRect.pivot = new Vector2(0f, 1f);
		nameTxtRect.sizeDelta = new Vector2(230, 45);
		nameTxtRect.anchoredPosition = new Vector2(10, -50);
		
		var nameField = UIElements.CreateInput(UICore.TMP_Window.transform, "");
		nameField.transform.parent.GetChild(1).gameObject.SetActive(false);
		var nameFieldRect = nameField.transform.parent.GetComponent<RectTransform>();
		nameFieldRect.anchorMin = new Vector2(0f, 1f);
		nameFieldRect.anchorMax = new Vector2(0f, 1f);
		nameFieldRect.pivot = new Vector2(0f, 1f);
		nameFieldRect.sizeDelta = new Vector2(350, 42);
		nameFieldRect.anchoredPosition = new Vector2(20, -100);
		var nameFieldRect2 = nameField.GetComponent<RectTransform>();
		nameFieldRect2.anchorMin = new Vector2(0.01f, 1f);
		nameFieldRect2.anchorMax = new Vector2(0.99f, 0.7f);
		nameFieldRect2.sizeDelta = new Vector2(1, 46);
		nameFieldRect2.anchoredPosition = new Vector2(0, -15);
		
		var gmTxt = UIElements.CreateText(UICore.TMP_Window.transform, "Gamemode : ", 22);
		var gmTxtRect = gmTxt.GetComponent<RectTransform>();
		gmTxtRect.anchorMin = new Vector2(0f, 1f);
		gmTxtRect.anchorMax = new Vector2(0f, 1f);
		gmTxtRect.pivot = new Vector2(0f, 1f);
		gmTxtRect.sizeDelta = new Vector2(230, 45);
		gmTxtRect.anchoredPosition = new Vector2(10, -140);
		
		List<string> gamemodes = new List<string>();
		gamemodes.Add("Easy");
		gamemodes.Add("Normal");
		gamemodes.Add("Expert");
		gamemodes.Add("Sandbox");
		var gmSelec = UIElements.CreateSelector(UICore.TMP_Window.transform,  gamemodes);
		var gmSelecRect = gmSelec.GetComponent<RectTransform>();
		gmSelecRect.anchorMin = new Vector2(0f, 1f);
		gmSelecRect.anchorMax = new Vector2(0f, 1f);
		gmSelecRect.pivot = new Vector2(0f, 1f);
		gmSelecRect.sizeDelta = new Vector2(130, 65);
		gmSelecRect.anchoredPosition = new Vector2(130, -132);
		gmSelec.EnableArrows();
		gmSelec.SetValue(1);
		
		CreateSplitter(UICore.TMP_Window.transform, new Vector2(0, -185), new(390, 2));

		var restoreBtn = UIElements.CreateButton(UICore.TMP_Window.transform, "Restore a backup", () => CreateBackupListPanel(btn, index));
		var restoreRect = restoreBtn.GetComponent<RectTransform>();
		restoreRect.anchorMin = new Vector2(0.5f, 0f);
		restoreRect.anchorMax = new Vector2(0.5f, 0f);
		restoreRect.pivot = new Vector2(0.5f, 0f);
		restoreRect.sizeDelta = new Vector2(233, 44);
		restoreRect.anchoredPosition = new Vector2(0, 60);
		
		var backBtn = UIElements.CreateButton(UICore.TMP_Window.transform,
			"Cancel", (() => { if (UICore.TMP_Window) Object.Destroy(UICore.TMP_Window); }));
		var backRect = backBtn.GetComponent<RectTransform>();
		backRect.anchorMin = new Vector2(0.5f, 0f);
		backRect.anchorMax = new Vector2(0.5f, 0f);
		backRect.pivot = new Vector2(0.5f, 0f);
		backRect.sizeDelta = new Vector2(133, 44);
		backRect.anchoredPosition = new Vector2(-120, 10);
		
		var confirmBtn = UIElements.CreateButton(UICore.TMP_Window.transform,
			"Confirm", (() => { UIActions.CreateNewSave(nameField, gmSelec, btn, index); }));
		var confirmRect = confirmBtn.GetComponent<RectTransform>();
		confirmRect.anchorMin = new Vector2(0.5f, 0f);
		confirmRect.anchorMax = new Vector2(0.5f, 0f);
		confirmRect.pivot = new Vector2(0.5f, 0f);
		confirmRect.sizeDelta = new Vector2(133, 44);
		confirmRect.anchoredPosition = new Vector2(120, 10);
	}

	public static void JoinAsHostPanel(MainMenuButton btn, int index)
	{
		if (UICore.TMP_Window)
			Object.Destroy(UICore.TMP_Window);
		UICore.TMP_Window = new GameObject("HostJoinWindow");
		UICore.TMP_Window.transform.SetParent(UICore.UI_Main.transform, false);

		var panelRect = UICore.TMP_Window.AddComponent<RectTransform>();
		panelRect.anchorMin = new Vector2(0.5f, 0.5f);
		panelRect.anchorMax = new Vector2(0.5f, 0.5f);
		panelRect.pivot = new Vector2(0.5f, 0.5f);
		panelRect.sizeDelta = new Vector2(400, 230);
		panelRect.anchoredPosition = Vector2.zero;

		var img = UICore.TMP_Window.AddComponent<Image>();
		img.color = new Color(.031f, .027f, .033f, 0.85f);

		
		var saveTxt = UIElements.CreateText(UICore.TMP_Window.transform, "Create Lobby", 24);
		var saveTxtRect = saveTxt.GetComponent<RectTransform>();
		saveTxtRect.anchorMin = new Vector2(0.5f, 1f);
		saveTxtRect.anchorMax = new Vector2(0.5f, 1f);
		saveTxtRect.pivot = new Vector2(0.5f, 1f);
		saveTxtRect.sizeDelta = new Vector2(145, 45);
		saveTxtRect.anchoredPosition = new Vector2(-118, 0);

		CreateSplitter(UICore.TMP_Window.transform, new Vector2(0, -40), new(390, 2));
		
		var nameTxt = UIElements.CreateText(UICore.TMP_Window.transform, "Username : ", 22);
		var nameTxtRect = nameTxt.GetComponent<RectTransform>();
		nameTxtRect.anchorMin = new Vector2(0f, 1f);
		nameTxtRect.anchorMax = new Vector2(0f, 1f);
		nameTxtRect.pivot = new Vector2(0f, 1f);
		nameTxtRect.sizeDelta = new Vector2(230, 45);
		nameTxtRect.anchoredPosition = new Vector2(10, -50);
		
		var nameField = UIElements.CreateInput(UICore.TMP_Window.transform, ClientData.UserData.username);
		nameField.transform.parent.GetChild(1).gameObject.SetActive(false);
		var nameFieldRect = nameField.transform.parent.GetComponent<RectTransform>();
		nameFieldRect.anchorMin = new Vector2(0f, 1f);
		nameFieldRect.anchorMax = new Vector2(0f, 1f);
		nameFieldRect.pivot = new Vector2(0f, 1f);
		nameFieldRect.sizeDelta = new Vector2(350, 42);
		nameFieldRect.anchoredPosition = new Vector2(20, -100);
		var nameFieldRect2 = nameField.GetComponent<RectTransform>();
		nameFieldRect2.anchorMin = new Vector2(0.01f, 1f);
		nameFieldRect2.anchorMax = new Vector2(0.99f, 0.7f);
		nameFieldRect2.sizeDelta = new Vector2(1, 46);
		nameFieldRect2.anchoredPosition = new Vector2(0, -15);
		
		CreateSplitter(UICore.TMP_Window.transform, new Vector2(0, -160), new(390, 2));
		
		var backBtn = UIElements.CreateButton(UICore.TMP_Window.transform,
			"Cancel", (() => { if (UICore.TMP_Window) Object.Destroy(UICore.TMP_Window); }));
		var backRect = backBtn.GetComponent<RectTransform>();
		backRect.anchorMin = new Vector2(0.5f, 0f);
		backRect.anchorMax = new Vector2(0.5f, 0f);
		backRect.pivot = new Vector2(0.5f, 0f);
		backRect.sizeDelta = new Vector2(133, 44);
		backRect.anchoredPosition = new Vector2(-120, 10);
		
		var confirmBtn = UIElements.CreateButton(UICore.TMP_Window.transform,
			"Confirm", (() =>
			{
				if (nameField.text == "")
				{
					UICustomPanel.CreateInfoPanel("Invalid username.");
					return;
				}
				UIActions.StartServer(nameField.text, index);
			}));
		var confirmRect = confirmBtn.GetComponent<RectTransform>();
		confirmRect.anchorMin = new Vector2(0.5f, 0f);
		confirmRect.anchorMax = new Vector2(0.5f, 0f);
		confirmRect.pivot = new Vector2(0.5f, 0f);
		confirmRect.sizeDelta = new Vector2(133, 44);
		confirmRect.anchoredPosition = new Vector2(120, 10);
	}

	public static void JoinPanel()
	{
		if (UICore.TMP_Window)
			Object.Destroy(UICore.TMP_Window);
		UICore.TMP_Window = new GameObject("JoinWindow");
		UICore.TMP_Window.transform.SetParent(UICore.UI_Main.transform, false);

		var panelRect = UICore.TMP_Window.AddComponent<RectTransform>();
		panelRect.anchorMin = new Vector2(0.5f, 0.5f);
		panelRect.anchorMax = new Vector2(0.5f, 0.5f);
		panelRect.pivot = new Vector2(0.5f, 0.5f);
		panelRect.sizeDelta = new Vector2(400, 330);
		panelRect.anchoredPosition = Vector2.zero;

		var img = UICore.TMP_Window.AddComponent<Image>();
		img.color = new Color(.031f, .027f, .033f, 0.85f);

		
		var saveTxt = UIElements.CreateText(UICore.TMP_Window.transform, "Join a Lobby", 24);
		var saveTxtRect = saveTxt.GetComponent<RectTransform>();
		saveTxtRect.anchorMin = new Vector2(0.5f, 1f);
		saveTxtRect.anchorMax = new Vector2(0.5f, 1f);
		saveTxtRect.pivot = new Vector2(0.5f, 1f);
		saveTxtRect.sizeDelta = new Vector2(145, 45);
		saveTxtRect.anchoredPosition = new Vector2(-118, 0);

		CreateSplitter(UICore.TMP_Window.transform, new Vector2(0, -40), new(390, 2));
		
		var nameTxt = UIElements.CreateText(UICore.TMP_Window.transform, "Username : ", 22);
		var nameTxtRect = nameTxt.GetComponent<RectTransform>();
		nameTxtRect.anchorMin = new Vector2(0f, 1f);
		nameTxtRect.anchorMax = new Vector2(0f, 1f);
		nameTxtRect.pivot = new Vector2(0f, 1f);
		nameTxtRect.sizeDelta = new Vector2(230, 45);
		nameTxtRect.anchoredPosition = new Vector2(10, -50);
		
		var nameField = UIElements.CreateInput(UICore.TMP_Window.transform, ClientData.UserData.username);
		nameField.transform.parent.GetChild(1).gameObject.SetActive(false);
		var nameFieldRect = nameField.transform.parent.GetComponent<RectTransform>();
		nameFieldRect.anchorMin = new Vector2(0f, 1f);
		nameFieldRect.anchorMax = new Vector2(0f, 1f);
		nameFieldRect.pivot = new Vector2(0f, 1f);
		nameFieldRect.sizeDelta = new Vector2(350, 42);
		nameFieldRect.anchoredPosition = new Vector2(20, -100);
		var nameFieldRect2 = nameField.GetComponent<RectTransform>();
		nameFieldRect2.anchorMin = new Vector2(0.01f, 1f);
		nameFieldRect2.anchorMax = new Vector2(0.99f, 0.7f);
		nameFieldRect2.sizeDelta = new Vector2(1, 46);
		nameFieldRect2.anchoredPosition = new Vector2(0, -15);
		
		var addressTxt = UIElements.CreateText(UICore.TMP_Window.transform, "Server Address : ", 22);
		var addressTxtRect = addressTxt.GetComponent<RectTransform>();
		addressTxtRect.anchorMin = new Vector2(0f, 1f);
		addressTxtRect.anchorMax = new Vector2(0f, 1f);
		addressTxtRect.pivot = new Vector2(0f, 1f);
		addressTxtRect.sizeDelta = new Vector2(230, 45);
		addressTxtRect.anchoredPosition = new Vector2(10, -150);
		
		var addressField = UIElements.CreateInput(UICore.TMP_Window.transform, ClientData.UserData.ip);
		addressField.transform.parent.GetChild(1).gameObject.SetActive(false);
		var addressFieldRect = addressField.transform.parent.GetComponent<RectTransform>();
		addressFieldRect.anchorMin = new Vector2(0f, 1f);
		addressFieldRect.anchorMax = new Vector2(0f, 1f);
		addressFieldRect.pivot = new Vector2(0f, 1f);
		addressFieldRect.sizeDelta = new Vector2(350, 42);
		addressFieldRect.anchoredPosition = new Vector2(20, -200);
		var addressFieldRect2 = addressField.GetComponent<RectTransform>();
		addressFieldRect2.anchorMin = new Vector2(0.01f, 1f);
		addressFieldRect2.anchorMax = new Vector2(0.99f, 0.7f);
		addressFieldRect2.sizeDelta = new Vector2(1, 46);
		addressFieldRect2.anchoredPosition = new Vector2(0, -15);
		
		CreateSplitter(UICore.TMP_Window.transform, new Vector2(0, -260), new(390, 2));
		
		var backBtn = UIElements.CreateButton(UICore.TMP_Window.transform,
			"Cancel", (() => { if (UICore.TMP_Window) Object.Destroy(UICore.TMP_Window); }));
		var backRect = backBtn.GetComponent<RectTransform>();
		backRect.anchorMin = new Vector2(0.5f, 0f);
		backRect.anchorMax = new Vector2(0.5f, 0f);
		backRect.pivot = new Vector2(0.5f, 0f);
		backRect.sizeDelta = new Vector2(133, 44);
		backRect.anchoredPosition = new Vector2(-120, 10);
		
		var confirmBtn = UIElements.CreateButton(UICore.TMP_Window.transform,
			"Confirm", (() =>
			{
				if (nameField.text == "")
				{
					UICustomPanel.CreateInfoPanel("Invalid username.");
					return;
				}
				if (addressField.text == "")
				{
					UICustomPanel.CreateInfoPanel("Invalid server address.");
					return;
				}
				UIActions.StartClient(nameField.text, addressField.text);
			}));
		var confirmRect = confirmBtn.GetComponent<RectTransform>();
		confirmRect.anchorMin = new Vector2(0.5f, 0f);
		confirmRect.anchorMax = new Vector2(0.5f, 0f);
		confirmRect.pivot = new Vector2(0.5f, 0f);
		confirmRect.sizeDelta = new Vector2(133, 44);
		confirmRect.anchoredPosition = new Vector2(120, 10);
	}
}