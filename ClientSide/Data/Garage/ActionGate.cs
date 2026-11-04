using System;
using CMS21Together.ClientSide.Data.Garage.Car;
using CMS21Together.Shared;
using CMS21Together.Shared.Data.Vanilla.GarageTool;
using CMS21Together.Shared.Locks;
using UnityEngine;

namespace CMS21Together.ClientSide.Data.Garage;

/// <summary>
/// Client half of "one player per component" (Phase 2). Gates are optimistic and deny-only: an action goes ahead unless
/// the host already reported another player on that resource, and the lock request is sent alongside it. Writes that
/// lose a race in flight are dropped by the host's fencing and the writer gets the stored state back.
/// </summary>
public static class ActionGate
{
	private const float ToastEverySeconds = 1f;

	private static int applying;
	private static float lastToast = -10f;

	/// <summary>True while the client applies state received from the host: hooks must neither gate nor send it back.</summary>
	public static bool ApplyingServerState => applying > 0;

	/// <summary>Marks a synchronous block that applies host state. Never keep it open across a coroutine yield.</summary>
	public static Scope Apply()
	{
		applying++;
		return new Scope(true);
	}

	public readonly struct Scope : IDisposable
	{
		private readonly bool open;

		internal Scope(bool open) => this.open = open;

		public void Dispose()
		{
			if (open && applying > 0) applying--;
		}
	}

	private static LockClient Locks => Client.Instance is { isConnected: true } client ? client.Locks : null;

	/// <summary>Gates apply: in a session and not applying host state.</summary>
	public static bool Active => applying == 0 && Locks != null;

	/// <summary>Another player holding <paramref name="key"/>, its car or one of its parts; 0 when free or when gates are off.</summary>
	public static int BlockedBy(string key)
	{
		var locks = Locks;
		if (locks == null || applying > 0 || key == null || !locks.Table.AnyForeign) return 0;
		return locks.Table.BlockedBy(key);
	}

	/// <summary>Claims <paramref name="key"/> for the local player. False, with a toast, when another player has it.</summary>
	public static bool TryBegin(string key)
	{
		var locks = Locks;
		if (locks == null || applying > 0 || key == null) return true;
		var holder = locks.Table.AnyForeign ? locks.Table.BlockedBy(key) : 0;
		if (holder != 0)
		{
			ShowDenied(holder);
			return false;
		}
		locks.Begin(key);
		return true;
	}

	/// <summary>Fencing token for a state write of <paramref name="key"/> (0 when this player has no claim on it).</summary>
	public static int Stamp(string key) => key == null ? 0 : Locks?.Stamp(key) ?? 0;

	/// <summary>"In use by" message, at most once per second.</summary>
	public static void ShowDenied(int holder)
	{
		try
		{
			var now = Time.unscaledTime;
			if (now - lastToast < ToastEverySeconds) return;
			lastToast = now;
			var name = ClientData.Instance != null && ClientData.Instance.connectedClients.TryGetValue(holder, out var user) &&
			           !string.IsNullOrEmpty(user.username)
				? user.username
				: $"player #{holder}";
			UIManager.Get()?.ShowPopup("Together+", $"In use by {name}", PopupType.Normal);
		}
		catch (Exception ex)
		{
			Log.Debug($"[ActionGate] Could not show the lock message: {ex.Message}");
		}
	}

	public static int LoaderId(CarLoader loader) => CarLoaderRegistry.IdOf(loader);

	/// <summary>Lock key of a car or engine-stand part, or null when it is not tracked.</summary>
	public static string KeyOf(PartScript part)
	{
		var data = ClientData.Instance;
		if (part == null || data == null) return null;

		var onCar = part.GetComponentInParent<CarLoaderOnCar>();
		if (onCar != null)
		{
			if (onCar.CarLoader == null) return null;
			var loader = LoaderId(onCar.CarLoader);
			if (!data.loadedCars.TryGetValue(loader, out var car)) return null;
			return PartUpdateHooks.FindPartInDictionaries(car, part, out var type, out var key, out var index, false)
				? LockKeys.Part(loader, (int)type, key, index ?? -1)
				: null;
		}

		return StandKey(data.engineStand, 1, part) ?? StandKey(data.engineStand2, 2, part);
	}

	/// <summary>Lock key of a body panel by its game name, or null when it is not tracked.</summary>
	public static string KeyOfBody(CarLoader loader, string name)
	{
		var data = ClientData.Instance;
		if (loader == null || data == null || string.IsNullOrEmpty(name)) return null;
		var id = LoaderId(loader);
		return data.loadedCars.TryGetValue(id, out var car) && PartUpdateHooks.FindBodyPartInDictionary(car, name, out var key, false)
			? LockKeys.Body(id, key)
			: null;
	}

	private static string StandKey(ModEngineStand stand, int number, PartScript part)
	{
		if (stand?.partReferences == null) return null;
		foreach (var pair in stand.partReferences)
			if (PartUpdateHooks.Same(pair.Value, part))
				return LockKeys.StandPart(number, pair.Key);
		return null;
	}
}
