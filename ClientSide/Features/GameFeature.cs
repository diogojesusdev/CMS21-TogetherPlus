using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using CMS21Together.ClientSide.Data;
using CMS21Together.ClientSide.Data.Garage;
using CMS21Together.ClientSide.Data.Player;
using CMS21Together.Shared;
using CMS21Together.Shared.Features;
using HarmonyLib;

namespace CMS21Together.ClientSide.Features;

/// <summary>
/// Base of a Phase 6b feature. Its patches are made by hand on its own Harmony instance (never by the mod's PatchAll),
/// so switching it off removes every one of them, and a missing target fails only this feature.
/// </summary>
public abstract class GameFeature : IFeatureModule
{
	private const int MaxQueued = 512;

	private HarmonyLib.Harmony harmony;
	private readonly Queue<FeatureMessage> queued = new();
	private readonly List<(FeatureMessage message, float since)> waitingForCar = new();

	/// <summary>A message about a car that does not get loaded in this time is dropped (the car is gone).</summary>
	private const float CarWaitSeconds = 120f;

	public abstract string Id { get; }

	public FeatureInfo Info => FeatureCatalog.Find(Id);

	/// <summary>True while the feature is on in this session.</summary>
	public bool On => Features.IsOn(Id);

	public void Install()
	{
		harmony = new HarmonyLib.Harmony("TogetherPlus." + Id);
		Patch();
		Started();
	}

	public void Uninstall()
	{
		try
		{
			Stopped();
		}
		finally
		{
			queued.Clear();
			waitingForCar.Clear();
			harmony?.UnpatchSelf();
			harmony = null;
		}
	}

	/// <summary>Makes the feature's patches. Throws when a target is missing.</summary>
	protected abstract void Patch();

	protected virtual void Started()
	{
	}

	protected virtual void Stopped()
	{
	}

	/// <summary>Every frame while on and the garage is ready.</summary>
	protected internal virtual void Tick()
	{
	}

	/// <summary>Every frame while on, also away from the garage and while loading.</summary>
	protected internal virtual void Always()
	{
	}

	/// <summary>True when messages should not wait for the garage (they arrive while away too).</summary>
	protected virtual bool ReceiveAnytime => false;

	/// <summary>A message from another player (or the host's kept state), on the main thread, once the garage is ready.</summary>
	protected virtual void Receive(FeatureMessage message)
	{
	}

	internal void Deliver(FeatureMessage message)
	{
		if (!ReceiveAnytime && !GameLoadHook.IsGameReady())
		{
			if (queued.Count >= MaxQueued) queued.Dequeue();
			queued.Enqueue(message);
			return;
		}
		Run(message);
	}

	internal void Flush()
	{
		while (queued.Count > 0 && GameLoadHook.IsGameReady()) Run(queued.Dequeue());
		if (waitingForCar.Count == 0 || !GameLoadHook.IsGameReady()) return;
		var now = UnityEngine.Time.unscaledTime;
		var waiting = waitingForCar.ToArray();
		waitingForCar.Clear();
		foreach (var (message, since) in waiting)
		{
			if (CarReady(FeatureRelay.CarOf(message.Key))) Receive0(message);
			else if (now - since < CarWaitSeconds) waitingForCar.Add((message, since));
		}
	}

	private static bool CarReady(int car) =>
		car < 0 || ClientData.Instance?.loadedCars != null && ClientData.Instance.loadedCars.TryGetValue(car, out var loaded) && loaded != null && loaded.isReady;

	/// <summary>
	/// Phase 8 (assessment): a message about a car waits until that car is loaded here: on a late join or a return to
	/// the garage the cars are (re)loaded from the host after the kept states arrived.
	/// </summary>
	private void Run(FeatureMessage message)
	{
		if (!CarReady(FeatureRelay.CarOf(message.Key)))
		{
			waitingForCar.Add((message, UnityEngine.Time.unscaledTime));
			return;
		}
		Receive0(message);
	}

	private void Receive0(FeatureMessage message)
	{
		try
		{
			Receive(message);
		}
		catch (Exception ex)
		{
			Log.Error($"[Features] {Id} could not apply a message from #{message.From}", ex);
		}
	}

	/// <summary>Sends a message; the payload is written with the mod's serializer (game types included).</summary>
	protected void Send(FeatureMessageKind kind, string key, Action<Packet> write = null)
	{
		if (!On) return;
		byte[] data;
		using (var payload = new Packet(0))
		{
			write?.Invoke(payload);
			data = payload.PayloadToArray();
		}
		Features.Send(new FeatureMessage { Feature = Info.Index, Kind = kind, Key = key ?? string.Empty, Data = data });
	}

	/// <summary>The message's payload, to read with the same serializer.</summary>
	protected static Packet Payload(FeatureMessage message) => new(0, message.Data);

	/// <summary>A change made by this player now (not one being applied from the host or another player, not a load).</summary>
	protected static bool LocalAction =>
		Client.Instance != null && Client.Instance.isConnected && !ActionGate.ApplyingServerState && GameLoadHook.IsGameReady();

	/// <summary>Deny-only station lock for a local action (true = go ahead).</summary>
	protected static bool Claim(string key) => !LocalAction || !ActionGate.Active || ActionGate.TryBegin(key);

	/// <summary>Applies another player's action without touching this game's inventory (it follows the host).</summary>
	protected static void Remote(Action apply) => CMS21Together.ClientSide.Data.Player.Inventory.Frozen(apply);

	protected MethodInfo Target(Type type, string method, params Type[] args)
	{
		var target = args.Length == 0 ? AccessTools.Method(type, method) : AccessTools.Method(type, method, args);
		return target ?? throw new MissingMethodException(type?.FullName, method);
	}

	private HarmonyMethod Handler(string name)
	{
		var method = AccessTools.Method(GetType(), name) ?? throw new MissingMethodException(GetType().FullName, name);
		return new HarmonyMethod(method);
	}

	protected void Prefix(MethodInfo target, string handler, int priority = -1)
	{
		Shared.Safety.PatchSafety.Require(target);
		var method = Handler(handler);
		if (priority >= 0) method.priority = priority;
		harmony.Patch(target, prefix: method);
	}

	// A target taking a game struct would corrupt the game's memory (PatchRules): Require throws, so the feature fails
	// on its own with the reason instead.
	protected void Postfix(MethodInfo target, string handler)
	{
		Shared.Safety.PatchSafety.Require(target);
		harmony.Patch(target, postfix: Handler(handler));
	}

	protected void Finalizer(MethodInfo target, string handler)
	{
		Shared.Safety.PatchSafety.Require(target);
		harmony.Patch(target, finalizer: Handler(handler));
	}
}