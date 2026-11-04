using System;
using CMS21Together.ClientSide.Data.Garage;
using CMS21Together.Shared;
using CMS21Together.Shared.Data;
using CMS21Together.Shared.Economy;
using UnityEngine;

namespace CMS21Together.ClientSide.Data.Player;

/// <summary>
/// Keeps the game's money and scrap equal to the shared wallet (Phase 3, audit A7): local changes become
/// transactions, and the game is set to the host's balance plus this player's pending changes. Anything that changed
/// the game's values without being sent (state applied from the host, a load) is overwritten within a second.
/// </summary>
public static class Wallet
{
	private const float CheckEverySeconds = 1f;
	private const float ToastEverySeconds = 2f;

	private static bool applied;
	private static bool dirty;
	private static float nextCheck;
	private static float lastToast = -100f;
	private static long expected;
	private static long? held;
	private static float expectedUntil;

	private static EconomyClient Economy => Client.Instance?.Economy;

	private static bool Enabled =>
		Client.Instance != null && Client.Instance.isConnected && ClientData.Instance != null && !Sandbox();

	/// <summary>
	/// From the loaded profile (Phase 8: ClientData.gamemode is only set ~2 s after the garage is ready and reads
	/// Sandbox until then, which switched the wallet off, so purchases in that window were free).
	/// </summary>
	private static bool Sandbox()
	{
		try
		{
			var profile = Singleton<GameManager>.Instance?.GameDataManager?.CurrentProfileData;
			if (profile != null) return profile.Difficulty == DifficultyLevel.Sandbox;
		}
		catch (Exception)
		{
			// Not loaded yet.
		}
		return ClientData.Instance.gamemode == Gamemode.Sandbox;
	}

	private static bool holding;
	private static float lastSyncToast = -100f;

	/// <summary>Money may be spent now (Phase 8): the host's wallet arrived and is shown.</summary>
	public static bool CanSpend => WalletRules.CanSpend(Enabled, Economy?.Synced == true, applied);

	/// <summary>False (with a short "Syncing money" message) while the shared wallet has not arrived yet.</summary>
	public static bool RequireCanSpend()
	{
		if (CanSpend) return true;
		ToastSyncing();
		return false;
	}

	private static void ToastSyncing()
	{
		var now = Time.unscaledTime;
		if (now - lastSyncToast < ToastEverySeconds) return;
		lastSyncToast = now;
		Purchases.Toast("Syncing money…");
	}

	public static void Reset()
	{
		applied = false;
		holding = false;
		dirty = false;
		nextCheck = 0f;
		expected = 0;
	}

	/// <summary>
	/// The host already credited <paramref name="money"/> (an order handed over or a car sold, Phase 6): the game's own
	/// credit of that amount in the next few seconds is not sent again.
	/// </summary>
	/// <summary>Starts holding this player's money changes instead of sending them (the host credits a bulk sale itself).</summary>
	public static void BeginHold() => held = 0;

	/// <summary>The money changes held since <see cref="BeginHold"/>; holding stops and the shared balance is shown again.</summary>
	public static long EndHold()
	{
		var money = held ?? 0;
		held = null;
		dirty = true;
		return money;
	}

	public static void Expect(long money)
	{
		expected = money;
		expectedUntil = Time.unscaledTime + 5f;
	}

	/// <summary>Postfix of the game's AddPlayerMoney/AddPlayerScraps.</summary>
	public static void OnLocalChange(long money, long scrap)
	{
		var economy = Economy;
		if (economy == null || !Enabled) return;
		// Changes made while applying the host's state, or before the game shows the host's balance, are not this
		// player's: they are overwritten instead of sent (sending them counted remote actions twice).
		if (held.HasValue)
		{
			held += money;
			dirty = true;
			return;
		}
		if (expected != 0 && money == expected && scrap == 0 && Time.unscaledTime < expectedUntil)
		{
			expected = 0;
			dirty = true;
			return;
		}
		if (!WalletRules.Record(ActionGate.ApplyingServerState, applied, Place(), GameLoadHook.IsGameReady()))
		{
			dirty = true;
			return;
		}
		if (!economy.Record(money, scrap)) dirty = true;
		else if (money < 0 || scrap < 0) Purchases.Debit(economy.LastNumber, money);
	}

	public static void OnBalance() => dirty = true;

	private static WalletPlace Place()
	{
		if (SceneManager.IsLoading) return WalletPlace.None;
		return SceneManager.CurrentScene() switch
		{
			GameScene.garage => WalletPlace.Garage,
			GameScene.junkyard or GameScene.auto_salon or GameScene.barn => WalletPlace.Away,
			_ => WalletPlace.None
		};
	}

	public static void OnRejected(TxnStatus status)
	{
		try
		{
			var now = Time.unscaledTime;
			if (now - lastToast < ToastEverySeconds) return;
			lastToast = now;
			var message = status == TxnStatus.Insufficient ? "Not enough shared money" : "The host refused that transaction";
			UIManager.Get()?.ShowPopup("Together+", message, PopupType.Normal);
		}
		catch (Exception ex)
		{
			Log.Warn($"[Wallet] Could not show the message: {ex.Message}");
		}
	}

	/// <summary>Every frame: puts the shared balance into the game when it changed, and at least once a second.</summary>
	public static void Pump()
	{
		var economy = Economy;
		if (economy != null && Client.Instance != null && WalletRules.Live(Place(), GameLoadHook.IsGameReady()) &&
		    WalletRules.HoldAtZero(Client.Instance.IsHost, Enabled, economy.Synced))
		{
			// Phase 8: 0 money and scrap until the host's wallet arrives, so the game itself refuses every purchase.
			if (!holding) ToastSyncing();
			holding = true;
			applied = false;
			if (GlobalData.PlayerMoney != 0 || GlobalData.PlayerScraps != 0)
				using (ActionGate.Apply())
				{
					GlobalData.SetPlayerMoney(0);
					GlobalData.SetPlayerScraps(0);
				}
			return;
		}
		if (holding)
		{
			holding = false;
			dirty = true;
		}
		if (economy == null || !economy.Synced || !Enabled || !WalletRules.Live(Place(), GameLoadHook.IsGameReady())) return;
		var now = Time.unscaledTime;
		if (!dirty && now < nextCheck) return;
		nextCheck = now + CheckEverySeconds;
		dirty = false;

		var money = (int)economy.Money;
		var scrap = (int)economy.Scrap;
		if (applied && GlobalData.PlayerMoney == money && GlobalData.PlayerScraps == scrap) return;
		if (applied) Log.Debug($"[Wallet] Showing the shared balance {money}/{scrap} (the game had {GlobalData.PlayerMoney}/{GlobalData.PlayerScraps}).");
		using (ActionGate.Apply())
		{
			if (GlobalData.PlayerMoney != money) GlobalData.SetPlayerMoney(money);
			if (GlobalData.PlayerScraps != scrap) GlobalData.SetPlayerScraps(scrap);
		}
		applied = true;
	}
}