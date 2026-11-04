// Based on ParkingAdd.cs, ParkingRemove.cs, ParkingMove.cs and ParkingRowsSync.cs from FixForTogether by TogetherFixer
// (https://github.com/Stevii001100/Fix-For-CMS21-Together), custom non-commercial licence.
// Modified for Together+: same patch targets; parking and taking a car out are approved by the host first (one
// player parks at a time, a space is emptied once), so two cars never race for one space and a parked car is never
// taken out twice. The added car is found by comparing the parking spaces before and after, as in the original.
using System;
using System.Collections;
using System.Collections.Generic;
using CMS.Managers;
using CMS.UI.Windows;
using CMS21Together.ClientSide.Data;
using CMS21Together.ClientSide.Data.Garage;
using CMS21Together.ClientSide.Data.Garage.Car;
using CMS21Together.ClientSide.Data.Handle;
using CMS21Together.ClientSide.Data.Player;
using CMS21Together.ServerSide;
using CMS21Together.Shared;
using CMS21Together.Shared.Data.Vanilla.Cars;
using CMS21Together.Shared.Economy;
using CMS21Together.Shared.Features;
using MelonLoader;
using UnityEngine;

namespace CMS21Together.ClientSide.Features.Garage;

/// <summary>Authoritative parking: one car per space, the same spaces for everyone.</summary>
public sealed class ParkingFeature : GameFeature
{
	private const string MoveKey = "move";
	private const string RowsKey = "rows";

	private static ParkingFeature instance;
	private static readonly Dictionary<int, Action<TxnStatus>> asks = new();
	private static bool approved;
	private static int moving;
	private int publishedRows = -1;

	public override string Id => FeatureCatalog.Parking;

	private static bool IsHost => Server.Instance != null && Server.Instance.isRunning;

	protected override void Patch()
	{
		instance = this;
		Prefix(Target(typeof(NotificationCenter), nameof(NotificationCenter.MoveCarToParking), typeof(CarLoader)), nameof(ParkIn), HarmonyLib.Priority.First);
		Prefix(Target(typeof(ParkingManagementWindow), nameof(ParkingManagementWindow.MoveCarFromParking)), nameof(ParkOut), HarmonyLib.Priority.First);
		Postfix(Target(typeof(ParkingCarPlaceManager), nameof(ParkingCarPlaceManager.MoveCar), typeof(int), typeof(int)), nameof(Moved));
	}

	protected override void Started() => publishedRows = -1;

	protected override void Stopped()
	{
		asks.Clear();
		approved = false;
	}

	private static Il2CppSystem.Collections.IEnumerator Nothing() => new Il2CppSystem.Collections.ArrayList().GetEnumerator();

	private static void Ask(PurchaseKind kind, string id, Action<TxnStatus> answer)
	{
		var request = Purchases.NextRequest();
		asks[request] = answer;
		using var packet = new Packet((int)PacketTypes.purchaseAsk);
		PurchaseWire.WriteAsk(packet, request, kind, id, 0, 0);
		Client.Instance.SendData(packet, true);
	}

	/// <summary>The host's answer to a parking ask; false when the request is not one.</summary>
	public static bool TryAnswer(int request, TxnStatus status)
	{
		if (!asks.TryGetValue(request, out var answer)) return false;
		asks.Remove(request);
		try
		{
			answer(status);
		}
		catch (Exception ex)
		{
			Log.Error("[Parking] Applying the host's answer failed", ex);
		}
		return true;
	}

	private static NewCarData[] Spaces()
	{
		var spaces = Singleton<GameManager>.Instance?.GameDataManager?.CurrentProfileData?.carsOnParking;
		if (spaces == null) return Array.Empty<NewCarData>();
		var copy = new NewCarData[spaces.Length];
		for (var i = 0; i < spaces.Length; i++) copy[i] = spaces[i];
		return copy;
	}

	private static string NameOf(NewCarData car) => car == null ? null : car.carToLoad;

	private static bool ParkIn(NotificationCenter __instance, CarLoader carLoader, ref Il2CppSystem.Collections.IEnumerator __result)
	{
		if (approved || !LocalAction || carLoader == null) return true;
		var center = __instance;
		Ask(PurchaseKind.ParkIn, "park", status =>
		{
			if (status != TxnStatus.Applied)
			{
				Purchases.Toast("Another player is parking a car; try again in a moment");
				return;
			}
			if (string.IsNullOrEmpty(carLoader.carToLoad)) return;
			var before = Spaces();
			approved = true;
			try
			{
				MainMod.StartCoroutine(center.MoveCarToParking(carLoader));
			}
			finally
			{
				approved = false;
			}
			MelonCoroutines.Start(SendParked(before));
		});
		__result = Nothing();
		return false;
	}

	private static IEnumerator SendParked(NewCarData[] before)
	{
		for (var waited = 0f; waited < 10f; waited += 0.25f)
		{
			yield return new WaitForSeconds(0.25f);
			var after = Spaces();
			for (var i = 0; i < after.Length; i++)
			{
				if (string.IsNullOrEmpty(NameOf(after[i])) || i < before.Length && NameOf(before[i]) == NameOf(after[i])) continue;
				ClientSend.AddCarToParkPacket(new ModNewCarData(after[i]), i);
				Log.Info($"[Parking] Parked a car in space {i}.");
				yield break;
			}
		}
		Log.Warn("[Parking] No parked car appeared; nothing was sent.");
	}

	private static bool ParkOut(ParkingManagementWindow __instance, ref Il2CppSystem.Collections.IEnumerator __result)
	{
		if (approved || !LocalAction || __instance == null) return true;
		var window = __instance;
		var space = window.currentSelectedSourceSaveIndex;
		if (space < 0) return true;
		Ask(PurchaseKind.ParkOut, space.ToString(), status =>
		{
			if (status != TxnStatus.Applied)
			{
				Purchases.Toast("Another player already took this car out");
				return;
			}
			if (window == null || window.currentSelectedSourceSaveIndex != space) return;
			approved = true;
			try
			{
				MainMod.StartCoroutine(window.MoveCarFromParking());
			}
			finally
			{
				approved = false;
			}
			MelonCoroutines.Start(SendTakenOut(space));
		});
		__result = Nothing();
		return false;
	}

	private static IEnumerator SendTakenOut(int space)
	{
		for (var waited = 0f; waited < 10f; waited += 0.25f)
		{
			yield return new WaitForSeconds(0.25f);
			var spaces = Spaces();
			if (space < spaces.Length && !string.IsNullOrEmpty(NameOf(spaces[space]))) continue;
			ClientSend.RemoveCarFromParkPacket(space);
			Log.Info($"[Parking] Took the car out of space {space}.");
			yield break;
		}
	}

	private static void Moved(int from, int to)
	{
		if (moving > 0 || !LocalAction || instance == null) return;
		instance.Send(FeatureMessageKind.Event, MoveKey, p =>
		{
			p.Write(from);
			p.Write(to);
		});
	}

	/// <summary>Another player's car taken out of a space (replaces the hf3 apply, as in the original).</summary>
	public static void ApplyRemoved(int space)
	{
		using (ActionGate.Apply()) ParkingCarPlaceManager.RemoveCar(space);
	}

	protected internal override void Tick()
	{
		if (!IsHost) return;
		var rows = GlobalData.UnlockedParkingLevels;
		if (rows == publishedRows) return;
		publishedRows = rows;
		Send(FeatureMessageKind.State, RowsKey, p => p.Write(rows));
	}

	protected override void Receive(FeatureMessage message)
	{
		using var p = Payload(message);
		if (message.Key == MoveKey)
		{
			var from = p.ReadInt();
			var to = p.ReadInt();
			moving++;
			try
			{
				Remote(() => ParkingCarPlaceManager.MoveCar(from, to));
			}
			finally
			{
				moving--;
			}
		}
		else if (message.Key == RowsKey && !IsHost)
		{
			var rows = p.ReadInt();
			if (rows > GlobalData.UnlockedParkingLevels) Remote(() => GlobalData.UnlockedParkingLevels = rows);
		}
	}
}