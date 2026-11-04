using System;
using System.IO;
using CMS21Together.ClientSide.Data;
using CMS21Together.Shared.Data;
using Newtonsoft.Json;

namespace CMS21Together.Shared;

public static class TogetherModManager
{
	private const string ModFolderPath = @"Mods\togetherMod\";
	private const string userDataPath = ModFolderPath + "userData.ini";

	public static UserData LoadUserData()
	{
		if (ClientData.UserData != null) return ClientData.UserData;

		ClientData.UserData = new UserData();
		if (File.Exists(userDataPath))
		{
			var serializedUserData = File.ReadAllText(userDataPath);
			if (serializedUserData.Length > 0)
			{
				UserData loaded = null;
				try
				{
					loaded = JsonConvert.DeserializeObject<UserData>(serializedUserData);
				}
				catch (Exception ex)
				{
					Log.Warn($"[TogetherModManager] {userDataPath} is unreadable, using defaults: {ex.Message}");
				}
				ClientData.UserData = loaded ?? new UserData();
				if (string.IsNullOrEmpty(ClientData.UserData.playerGUID)) ClientData.UserData.playerGUID = Guid.NewGuid().ToString();
				if (!MainMod.IsSteamAvailable)
					ClientData.UserData.selectedNetworkType = NetworkType.TCP;
				return ClientData.UserData;
			}
		}
		else
		{
			AtomicFile.WriteAllText(userDataPath, JsonConvert.SerializeObject(ClientData.UserData));
		}
		
		return ClientData.UserData;
	}

	public static void SavePreferences()
	{
		try
		{
			// Atomic: a crash while writing never leaves a torn file (plan §3.5).
			AtomicFile.WriteAllText(userDataPath, JsonConvert.SerializeObject(ClientData.UserData));
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
		{
			Log.Error("[TogetherModManager] Saving the preferences failed", ex);
		}
	}
}