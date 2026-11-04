using System;
using System.Collections.Generic;
using CMS.UI.Windows;
using CMS21Together.ClientSide.Data.Garage.Car;
using CMS21Together.ClientSide.Data.Player;
using CMS21Together.Shared;
using CMS21Together.Shared.Economy;
using CMS21Together.Shared.Locks;
using HarmonyLib;

namespace CMS21Together.ClientSide.Data.Garage.Campaign;

/// <summary>
/// Accepting an order, handing it over and selling a car happen once (Phase 6, plan §3.8). The player asks the host
/// first; only the first ask wins, and the host pays the order's payout or the car's price into the shared wallet.
/// Then this player's game does the rest. Two players clicking at once never spawn a car twice or get paid twice.
/// </summary>
[HarmonyPatch]
public static class Handover
{
	private static readonly Dictionary<int, Ask> asks = new();
	private static bool approvedSale;

	/// <summary>The order the host let this player accept; its accept goes through once.</summary>
	public static int ApprovedAccept { get; set; } = -1;

	private static bool Connected => Client.Instance != null && Client.Instance.isConnected;

	public static void Reset()
	{
		asks.Clear();
		approvedSale = false;
		ApprovedAccept = -1;
	}

	private static void Send(PurchaseKind kind, string id, int level, long amount, Ask ask)
	{
		var request = Purchases.NextRequest();
		ask.Kind = kind;
		asks[request] = ask;
		using var packet = new Packet((int)PacketTypes.purchaseAsk);
		PurchaseWire.WriteAsk(packet, request, kind, id, level, amount);
		Client.Instance.SendData(packet, true);
		Log.Debug($"[Handover] Asked the host: {kind} {id} ({amount}).");
	}

	public static void AskAccept(OrdersWindow window, Job job)
	{
		// Phase 8: nothing that involves money before the shared wallet has arrived.
		if (!Wallet.RequireCanSpend()) return;
		Send(PurchaseKind.JobAccept, job.id.ToString(), 0, 0, new Ask { Window = window, JobId = job.id });
	}

	/// <summary>False when another player is working on the car (with the "in use" toast).</summary>
	public static bool AskEnd(Job job, CarLoader loader)
	{
		var carLoaderID = CarLoaderRegistry.IdOf(loader);
		if (carLoaderID >= 0 && !ActionGate.TryBegin(LockKeys.Car(carLoaderID))) return false;
		Send(PurchaseKind.JobEnd, job.id.ToString(), Math.Max(carLoaderID, 0), Math.Max(job.TotalPayout, 0),
			new Ask { Job = job, Loader = loader, JobId = job.id });
		return true;
	}

	[HarmonyPatch(typeof(GameScript), nameof(GameScript.SellCar))]
	[HarmonyPrefix]
	[HarmonyPriority(Priority.First)]
	public static bool SellCarHook(CarLoader carLoader, int sellPrice)
	{
		if (approvedSale || !Connected || !GameLoadHook.IsGameReady() || ActionGate.ApplyingServerState) return true;
		var carLoaderID = CarLoaderRegistry.IdOf(carLoader);
		if (carLoaderID < 0) return true;
		if (!ActionGate.TryBegin(LockKeys.Car(carLoaderID))) return false;
		Send(PurchaseKind.CarSell, carLoaderID.ToString(), carLoaderID, Math.Max(sellPrice, 0),
			new Ask { Loader = carLoader, Price = sellPrice });
		return false;
	}

	/// <summary>The host's answer to one of these asks; false when the request is not one of them.</summary>
	public static bool TryAnswer(int request, TxnStatus status)
	{
		if (!asks.TryGetValue(request, out var ask)) return false;
		asks.Remove(request);
		try
		{
			if (status != TxnStatus.Applied)
			{
				if (ask.Kind == PurchaseKind.AcceptCancel) return true;
				Purchases.Toast(ask.Kind switch
				{
					PurchaseKind.JobAccept => "Another player already accepted this order",
					PurchaseKind.JobEnd => "This order was already handed over",
					_ => "This car was already sold"
				});
				return true;
			}

			switch (ask.Kind)
			{
				case PurchaseKind.JobAccept:
					var window = ask.Window;
					if (window != null && window.currentJob != null && window.currentJob.id == ask.JobId)
					{
						ApprovedAccept = ask.JobId;
						try
						{
							window.AcceptOrderAction();
						}
						finally
						{
							ApprovedAccept = -1;
						}
					}
					else
					{
						// The order window closed meanwhile: give the order back so somebody can take it.
						Send(PurchaseKind.AcceptCancel, ask.JobId.ToString(), 0, 0, new Ask { JobId = ask.JobId });
					}
					break;
				case PurchaseKind.JobEnd:
					// Phase 8 (assessment): not inside the host-packet scope, where the reward box counted as host state
					// (never shared, then removed on the next load). The next frame is this player's own action again.
					MelonLoader.MelonCoroutines.Start(CompleteNextFrame(ask.Job, ask.Loader));
					break;
				case PurchaseKind.CarSell:
					Wallet.Expect(ask.Price);
					approvedSale = true;
					try
					{
						GameScript.Get().SellCar(ask.Loader, ask.Price);
					}
					finally
					{
						approvedSale = false;
					}
					Wallet.OnBalance();
					break;
			}
		}
		catch (Exception ex)
		{
			Log.Error($"[Handover] Applying the host's approval of {ask.Kind} failed", ex);
		}
		return true;
	}

	private sealed class Ask
	{
		public PurchaseKind Kind;
		public OrdersWindow Window;
		public Job Job;
		public CarLoader Loader;
		public int JobId;
		public int Price;
	}

	private static System.Collections.IEnumerator CompleteNextFrame(Job job, CarLoader loader)
	{
		yield return null;
		JobHooks.CompleteHandover(job, loader);
	}
}
