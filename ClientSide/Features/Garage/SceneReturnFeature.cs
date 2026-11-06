// Based on SceneChangeManager.cs, SceneReturnCarSync.cs, AwayCarBuy.cs, SChangeWhitelist.cs and
// 66SceneReturnCarSyncPaket.cs from FixForTogether by TogetherFixer
// (https://github.com/Stevii001100/Fix-For-CMS21-Together), custom non-commercial licence.
// Modified for Together+: the original keeps the returner's garage private while away, then replaces it with the
// stayer's garage and relays the returner's own purchases (found by comparing the garage before and after). Here the
// join snapshot and per-car resync of Phase 5 already replace the returner's cars, so this feature keeps only:
// - away purchases are found the same way (the lists before and after the game writes them), taken out of the
//   buyer's stale lists and handed to the host, which places them in a free parking space for everyone only after it
//   charged the buyer's debit (a refused debit never ships a car; a full parking lot refunds exactly that debit);
// - during the return load, cars the buyer's stale save shows but the host does not have are never sent (the hf3
//   path could bring back sold cars or overwrite a car another player put in that slot) and are removed afterwards.
using System;
using System.Collections;
using System.Collections.Generic;
using CMS.Managers;
using CMS21Together.ClientSide.Data;
using CMS21Together.ClientSide.Data.Garage;
using CMS21Together.ClientSide.Data.Garage.Car;
using CMS21Together.ClientSide.Data.Player;
using CMS21Together.ServerSide;
using CMS21Together.ServerSide.Data;
using CMS21Together.Shared;
using CMS21Together.Shared.Data;
using CMS21Together.Shared.Data.Vanilla.Cars;
using CMS21Together.Shared.Economy;
using CMS21Together.Shared.Features;
using MelonLoader;
using UnityEngine;

namespace CMS21Together.ClientSide.Features.Garage;

/// <summary>Cars bought or changed while away from the garage reach everyone on return, and only if paid.</summary>
public sealed class SceneReturnFeature : GameFeature
{
	private const string BuyKey = "buy";
	private const string ResultKey = "result";
	private const float StaleGraceSeconds = 5f;
	private const float StepEverySeconds = 0.5f;

	private static SceneReturnFeature instance;
	private static int depth;
	private static List<(string uid, string name)> garageBefore, parkingBefore;
	private static NewCarData[] garageRefs, parkingRefs;

	private readonly AwayCarQueue<ModNewCarData> queue = new();
	private readonly List<CarLoader> stale = new();
	private float staleAt = -1f;
	private float nextStep;
	private bool waitForGarage;

	public override string Id => FeatureCatalog.SceneReturnSync;

	protected override bool ReceiveAnytime => true;

	private static bool IsHost => Server.Instance != null && Server.Instance.isRunning;

	private static bool IsAway(GameScene scene) => scene is GameScene.junkyard or GameScene.auto_salon or GameScene.barn;

	protected override void Patch()
	{
		instance = this;
		var manager = typeof(GameDataManager);
		foreach (var target in new[]
		         {
			         Target(manager, nameof(GameDataManager.SaveCarInGarage), typeof(NewCarData), typeof(int)),
			         Target(manager, nameof(GameDataManager.SaveCarInParking), typeof(NewCarData), typeof(int)),
			         Target(manager, nameof(GameDataManager.SaveCar), typeof(NewCarData), typeof(int), typeof(bool))
		         })
		{
			Prefix(target, nameof(Before));
			Finalizer(target, nameof(After));
		}
	}

	protected override void Started()
	{
		queue.Clear();
		stale.Clear();
		staleAt = -1f;
		depth = 0;
	}

	protected override void Stopped()
	{
		if (queue.Count > 0) Log.Warn($"[SceneReturn] Session ended with {queue.Count} car(s) bought away not yet placed.");
		queue.Clear();
		stale.Clear();
	}

	// Buyer side.

	private static ProfileData Profile => Singleton<GameManager>.Instance?.GameDataManager?.CurrentProfileData;

	private static bool Watching =>
		instance != null && instance.On && Client.Instance != null && Client.Instance.isConnected && !ActionGate.ApplyingServerState &&
		!SceneManager.IsLoading && IsAway(SceneManager.CurrentScene());

	private static List<(string uid, string name)> Ids(NewCarData[] cars)
	{
		var ids = new List<(string, string)>(cars.Length);
		foreach (var car in cars) ids.Add(car == null ? (null, null) : (car.UId, car.carToLoad));
		return ids;
	}

	private static NewCarData[] Copy(UnhollowerBaseLib.Il2CppReferenceArray<NewCarData> cars)
	{
		if (cars == null) return Array.Empty<NewCarData>();
		var copy = new NewCarData[cars.Length];
		for (var i = 0; i < cars.Length; i++) copy[i] = cars[i];
		return copy;
	}

	private static void Before()
	{
		// The game's own save runs again while the garage is being torn down: walking its data then builds wrappers
		// out of objects that are already being destroyed (a GC handle to freed memory is the menu-return crash).
		if (Shutdown.Leaving) return;
		if (depth++ > 0) return;
		garageRefs = parkingRefs = null;
		try
		{
			var profile = Profile;
			if (!Watching || profile == null) return;
			garageRefs = Copy(profile.carsInGarage);
			parkingRefs = Copy(profile.carsOnParking);
			garageBefore = Ids(garageRefs);
			parkingBefore = Ids(parkingRefs);
		}
		catch (Exception ex)
		{
			garageRefs = parkingRefs = null;
			Log.Error("[SceneReturn] Reading the car lists failed", ex);
		}
	}

	private static void After()
	{
		if (--depth > 0) return;
		depth = 0;
		if (garageRefs == null || parkingRefs == null) return;
		try
		{
			var profile = Profile;
			if (profile == null) return;
			var bought = new List<(bool parking, int index, NewCarData previous, NewCarData car)>();
			var garage = Copy(profile.carsInGarage);
			var parking = Copy(profile.carsOnParking);
			foreach (var i in AwayCars.Added(garageBefore, Ids(garage)))
				bought.Add((false, i, i < garageRefs.Length ? garageRefs[i] : null, garage[i]));
			foreach (var i in AwayCars.Added(parkingBefore, Ids(parking)))
				bought.Add((true, i, i < parkingRefs.Length ? parkingRefs[i] : null, parking[i]));
			if (bought.Count > 0) MelonCoroutines.Start(Hand(bought, Time.frameCount));
		}
		catch (Exception ex)
		{
			Log.Error("[SceneReturn] Finding a car bought away failed", ex);
		}
		finally
		{
			garageRefs = parkingRefs = null;
		}
	}

	/// <summary>A frame later (the game may take the money after writing the car): the car leaves the stale lists and goes to the host.</summary>
	private static IEnumerator Hand(List<(bool parking, int index, NewCarData previous, NewCarData car)> bought, int frame)
	{
		yield return null;
		if (instance == null || !instance.On) yield break;
		var (txn, _) = Purchases.DebitNear(frame, 3f);
		foreach (var (parking, index, previous, car) in bought)
		{
			ModNewCarData data;
			try
			{
				data = new ModNewCarData(car);
				var list = parking ? Profile?.carsOnParking : Profile?.carsInGarage;
				if (list != null && index < list.Length && list[index] != null && list[index].UId == car.UId && list[index].carToLoad == car.carToLoad)
					Il2CppArrays.Set(list, index, previous);
			}
			catch (Exception ex)
			{
				Log.Error("[SceneReturn] Taking a car bought away out of the local lists failed", ex);
				continue;
			}
			instance.Handover(txn, data);
			Log.Info($"[SceneReturn] Bought {data.carToLoad} away (txn {txn}); the host places it in the parking lot.");
		}
		if (txn > 0) Purchases.Linker.OnRefused(txn, () => Purchases.Toast("Not enough shared money: the car was not bought"));
	}

	private void Handover(int txn, ModNewCarData car)
	{
		var requiresDebit = ClientData.Instance == null || ClientData.Instance.gamemode != Gamemode.Sandbox;
		if (IsHost)
		{
			queue.Add(ClientData.UserData.playerID, txn, car.UId, car, Time.unscaledTime, requiresDebit);
			return;
		}
		Send(FeatureMessageKind.Event, BuyKey, p =>
		{
			p.Write(txn);
			p.Write(car);
		});
	}

	protected override void Receive(FeatureMessage message)
	{
		using var p = Payload(message);
		if (message.Key == BuyKey)
		{
			if (!IsHost) return;
			var txn = p.ReadInt();
			var car = p.Read<ModNewCarData>();
			if (car == null || string.IsNullOrEmpty(car.carToLoad)) return;
			// The host's own mode decides: a client cannot claim "sandbox" to get a car for free.
			var requiresDebit = ClientData.Instance == null || ClientData.Instance.gamemode != Gamemode.Sandbox;
			if (!queue.Add(message.From, txn, car.UId, car, Time.unscaledTime, requiresDebit))
				Log.Warn($"[SceneReturn] #{message.From}'s car {car.carToLoad} was already handed over; ignored.");
		}
		else if (message.Key == ResultKey)
		{
			var target = p.ReadInt();
			var outcome = (AwayCarOutcome)p.ReadInt();
			var space = p.ReadInt();
			if (target == ClientData.UserData.playerID) Tell(outcome, space);
		}
	}

	private static void Tell(AwayCarOutcome outcome, int space)
	{
		switch (outcome)
		{
			case AwayCarOutcome.Parked:
				Purchases.Toast($"Your car was delivered to parking space {space + 1}");
				break;
			case AwayCarOutcome.Refunded:
				Purchases.Toast("The parking lot is full: the car was not delivered and its price was refunded");
				break;
			default:
				Purchases.Toast("The car bought away could not be delivered");
				break;
		}
	}

	private void Answer(int target, AwayCarOutcome outcome, int space)
	{
		if (target == ClientData.UserData.playerID)
		{
			Tell(outcome, space);
			return;
		}
		Send(FeatureMessageKind.Event, ResultKey, p =>
		{
			p.Write(target);
			p.Write((int)outcome);
			p.Write(space);
		});
	}

	// Host side.

	protected internal override void Always()
	{
		if (!IsHost || queue.Count == 0 || Time.unscaledTime < nextStep) return;
		nextStep = Time.unscaledTime + StepEverySeconds;
		var economy = Server.Instance.Economy;
		var ready = queue.Step(
			(slot, txn) => economy == null ? TxnStatus.Applied : economy.StatusOf(slot, txn),
			Time.unscaledTime,
			(car, outcome) =>
			{
				Log.Warn($"[SceneReturn] #{car.From}'s car {car.Car.carToLoad} bought away was not placed: {outcome} (txn {car.Txn}).");
				Answer(car.From, outcome, -1);
			},
			(slot, txn) => economy?.DebitOf(slot, txn) ?? 0);
		var scene = SceneManager.CurrentScene();
		if (scene == GameScene.garage) waitForGarage = false;
		var canPlace = !SceneManager.IsLoading && (IsAway(scene) && !waitForGarage || scene == GameScene.garage && GameLoadHook.IsGameReady());
		if (!canPlace) return;
		foreach (var car in ready)
			if (!Place(car)) break;
	}

	private static bool Parked(string uid)
	{
		var parking = Profile?.carsOnParking;
		if (string.IsNullOrEmpty(uid) || parking == null) return false;
		for (var i = 0; i < parking.Length; i++)
			if (parking[i] != null && parking[i].UId == uid && !string.IsNullOrEmpty(parking[i].carToLoad))
				return true;
		return false;
	}

	/// <summary>Places one charged car; false when parking is busy (try again later).</summary>
	private bool Place(AwayCar<ModNewCarData> car)
	{
		if (Parked(car.Uid))
		{
			queue.Done(car);
			return true;
		}
		var host = ClientData.UserData.playerID;
		int space;
		try
		{
			space = ParkingCarPlaceManager.GetParkingFreePlaceIndex();
		}
		catch (Exception ex)
		{
			if (!waitForGarage) Log.Warn($"[SceneReturn] No parking space could be found here; waiting for the garage: {ex.Message}");
			waitForGarage = true;
			return false;
		}
		// One player parks at a time (the Parking feature's gate), so no one else takes the same space meanwhile.
		var gate = Server.Instance.Payouts?.Decide(host, PurchaseKind.ParkIn, "park", 0, false) ?? TxnStatus.Applied;
		if (gate != TxnStatus.Applied) return false;
		if (space < 0)
		{
			queue.Done(car);
			var refunded = car.Charge > 0 && Server.Instance.Economy?.Grant(car.From, car.Charge) == TxnStatus.Applied ? car.Charge : 0;
			Log.Warn($"[SceneReturn] Parking is full: #{car.From}'s car {car.Car.carToLoad} was not delivered; refunded {refunded}.");
			Answer(car.From, AwayCarOutcome.Refunded, -1);
			return true;
		}
		using (ActionGate.Apply())
		{
			ParkHook.listen = false;
			try
			{
				Singleton<GameManager>.Instance.GameDataManager.SaveCarInParking(car.Car.ToGame(), space);
			}
			finally
			{
				ParkHook.listen = true;
			}
		}
		ServerData.Instance.AddCarToPark(car.Car, space);
		ServerSend.AddCarToParkPacket(host, car.Car, space);
		queue.Done(car);
		Log.Info($"[SceneReturn] #{car.From}'s car {car.Car.carToLoad} bought away is in parking space {space}.");
		Answer(car.From, AwayCarOutcome.Parked, space);
		return true;
	}

	// Return load (players other than the host).

	private static bool IsGarageScene(string name) => name is "garage" or "Christmas" or "Easter" or "Halloween";

	/// <summary>
	/// A car the garage load took from this player's own (stale) save: true when it must not be sent, because the host
	/// does not have it in that loader. It is removed after the load unless the host's state puts a car there.
	/// </summary>
	public static bool HoldEcho(CarLoader loader)
	{
		var feature = instance;
		if (feature == null || !feature.On || loader == null || Client.Instance == null || !Client.Instance.isConnected || Client.Instance.IsHost) return false;
		if (GameLoadHook.IsGameReady() || !IsGarageScene(loader.gameObject.scene.name)) return false;
		if (ClientData.Instance.loadedCars.ContainsKey(CarLoaderRegistry.IdOf(loader))) return false;
		feature.stale.Add(loader);
		Log.Info($"[SceneReturn] {loader.carToLoad} came from this player's own save, not the host's: not sent.");
		return true;
	}

	protected internal override void Tick()
	{
		if (stale.Count == 0) return;
		if (staleAt < 0f) staleAt = Time.unscaledTime + StaleGraceSeconds;
		if (Time.unscaledTime < staleAt) return;
		staleAt = -1f;
		var loaders = stale.ToArray();
		stale.Clear();
		foreach (var loader in loaders)
		{
			if (loader == null || string.IsNullOrEmpty(loader.carToLoad)) continue;
			if (ClientData.Instance.loadedCars.ContainsKey(CarLoaderRegistry.IdOf(loader))) continue;
			Log.Info($"[SceneReturn] Removed {loader.carToLoad}, which only this player's old save had.");
			CarSpawnHooks.listenToDelete = false;
			try
			{
				loader.DeleteCar();
			}
			finally
			{
				CarSpawnHooks.listenToDelete = true;
			}
		}
	}
}