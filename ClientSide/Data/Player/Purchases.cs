using System;
using System.Collections.Generic;
using CMS.UI.Logic;
using CMS.UI.Logic.Upgrades;
using CMS21Together.ClientSide.Data.Garage;
using CMS21Together.ClientSide.Data.Garage.Campaign;
using CMS21Together.ClientSide.Data.Handle;
using CMS21Together.Shared;
using CMS21Together.Shared.Economy;
using UnityEngine;

namespace CMS21Together.ClientSide.Data.Player;

/// <summary>
/// Purchase rollback, player side (Phase 6, plan §3.8). Items, paint and parked cars bought in the same frame as a
/// debit carry the debit's transaction number; the host lets them happen only if it charged the debit, and a car
/// parked here is taken back if the debit is refused. Garage upgrades cannot be undone in the game, so they are
/// pre-authorized: the host charges first, then the game unlocks the upgrade.
/// </summary>
public static class Purchases
{
	private const float ToastEverySeconds = 2f;

	public static readonly PurchaseLinker Linker = new();
	private static readonly List<Action<int>> deferred = new();
	private static readonly Dictionary<int, UpgradeAsk> asks = new();
	private static int nextRequest;
	private static (int frame, int txn, long money, float at) lastDebit;
	private static float lastToast = -100f;

	/// <summary>The upgrade the host already charged for; its unlock goes through unchallenged.</summary>
	public static string AuthorizedUpgrade { get; private set; }

	public static void Reset(EconomyClient economy)
	{
		Linker.Reset();
		lastDebit = default;
		deferred.Clear();
		asks.Clear();
		Handover.Reset();
		AuthorizedUpgrade = null;
		if (economy == null) return;
		economy.RejectedTxn += (number, _) => Linker.Refused(number);
		economy.Changed += () => Linker.Processed(economy.Processed);
	}

	/// <summary>The debit that pays for what the game does in this frame (0 = none).</summary>
	public static int TxnNow() => Linker.TxnFor(Time.frameCount);

	public static void Debit(int number, long money = 0)
	{
		Linker.Debit(Time.frameCount, number);
		lastDebit = (Time.frameCount, number, money, Time.unscaledTime);
	}

	/// <summary>
	/// The debit made in <paramref name="frame"/>, or else the last one within <paramref name="seconds"/> (a purchase the game
	/// writes a frame after taking the money); (0, 0) when none.
	/// </summary>
	public static (int txn, long money) DebitNear(int frame, float seconds)
	{
		if (lastDebit.txn == 0) return (0, 0);
		if (lastDebit.frame == frame || Time.unscaledTime - lastDebit.at <= seconds) return (lastDebit.txn, lastDebit.money);
		return (0, 0);
	}

	/// <summary>A send that should carry this frame's debit, whether the debit comes before or after it in the frame.</summary>
	public static void Defer(Action<int> send) => deferred.Add(send);

	/// <summary>End of every frame (LateUpdate).</summary>
	public static void Flush()
	{
		var txn = TxnNow();
		Inventory.Flush(txn);
		if (deferred.Count == 0) return;
		var sends = deferred.ToArray();
		deferred.Clear();
		foreach (var send in sends)
		{
			try
			{
				send(txn);
			}
			catch (Exception ex)
			{
				Log.Error("[Purchases] Sending a purchase failed", ex);
			}
		}
	}

	public static void AskUpgrade(GarageAndToolsTab tab, string id, int level, int cost)
	{
		if (!Wallet.RequireCanSpend()) return;
		var request = NextRequest();
		asks[request] = new UpgradeAsk(tab, id, level, cost);
		using var packet = new Packet((int)PacketTypes.purchaseAsk);
		PurchaseWire.WriteAsk(packet, request, PurchaseKind.Upgrade, id, level, cost);
		Client.Instance.SendData(packet, true);
		Log.Debug($"[Purchases] Asked the host to charge {cost} for {id} {level}.");
	}

	public static void HandleAnswer(Packet packet)
	{
		var (request, status) = PurchaseWire.ReadAnswer(packet);
		if (request < 0)
		{
			// A bulk sale the host settled (Phase 6b).
			Features.Inventory.BulkSellFeature.Settled();
			return;
		}
		if (Handover.TryAnswer(request, status)) return;
		if (Features.Garage.ParkingFeature.TryAnswer(request, status)) return;
		if (!asks.TryGetValue(request, out var ask)) return;
		asks.Remove(request);
		if (status != TxnStatus.Applied)
		{
			Toast(status == TxnStatus.Insufficient ? "Not enough shared money" : "The host refused that purchase");
			return;
		}

		var applied = false;
		try
		{
			var tab = ask.Tab;
			var item = tab != null ? tab.currentUpgradeItem : null;
			if (item != null && item.upgradeID == ask.Id && item.UpgradeLevel == ask.Level)
			{
				// Already charged on the host: the game's own debit is not recorded, and it must find the money.
				using (ActionGate.Apply())
				{
					AuthorizedUpgrade = ask.Id + ask.Level;
					try
					{
						GlobalData.SetPlayerMoney(GlobalData.PlayerMoney + ask.Cost);
						tab.UnlockCurrentSelectedSkillAction();
					}
					finally
					{
						AuthorizedUpgrade = null;
					}
				}
				applied = tab.upgradeSystem.IsUnlocked(ask.Id, ask.Level, UpgradeType.Money);
			}
		}
		catch (Exception ex)
		{
			Log.Error("[Purchases] Unlocking a paid upgrade failed", ex);
		}
		Wallet.OnBalance();
		if (applied) return;

		// Paid but not applied (the selection changed, or the game said no): the money goes back.
		Client.Instance.Economy?.Record(ask.Cost, 0);
		Toast("The upgrade could not be bought; the money was refunded.");
	}

	/// <summary>Request numbers shared by every kind of ask on this connection.</summary>
	public static int NextRequest() => ++nextRequest;

	public static void Toast(string message)
	{
		try
		{
			var now = Time.unscaledTime;
			if (now - lastToast < ToastEverySeconds) return;
			lastToast = now;
			UIManager.Get()?.ShowPopup("Together+", message, PopupType.Normal);
		}
		catch (Exception ex)
		{
			Log.Warn($"[Purchases] Could not show the message: {ex.Message}");
		}
	}

	private sealed class UpgradeAsk
	{
		public UpgradeAsk(GarageAndToolsTab tab, string id, int level, int cost)
		{
			Tab = tab;
			Id = id;
			Level = level;
			Cost = cost;
		}

		public GarageAndToolsTab Tab { get; }
		public string Id { get; }
		public int Level { get; }
		public int Cost { get; }
	}
}