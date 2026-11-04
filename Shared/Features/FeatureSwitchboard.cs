using System;
using System.Collections.Generic;

namespace CMS21Together.Shared.Features;

/// <summary>A feature's patches. Install patches everything or throws; Uninstall removes every patch it made.</summary>
public interface IFeatureModule
{
	string Id { get; }
	void Install();
	void Uninstall();
}

public enum FeatureState
{
	Off,
	On,

	/// <summary>Installing failed (a patch target is missing): the feature stays off for this run of the game.</summary>
	Broken
}

/// <summary>
/// Turns features on and off (Phase 6b). Each feature installs on its own: a broken one is undone, logged and kept
/// off, and never takes the others down. A feature that is off has no patch in effect.
/// </summary>
public sealed class FeatureSwitchboard
{
	private readonly Dictionary<string, IFeatureModule> modules = new();
	private readonly Dictionary<string, FeatureState> states = new();
	private readonly List<string> order = new();

	public void Register(IFeatureModule module)
	{
		if (module == null || FeatureCatalog.Find(module.Id) == null) throw new ArgumentException($"Unknown feature {module?.Id}.");
		if (modules.ContainsKey(module.Id)) throw new ArgumentException($"Feature {module.Id} registered twice.");
		modules[module.Id] = module;
		states[module.Id] = FeatureState.Off;
		order.Add(module.Id);
	}

	public FeatureState StateOf(string id) => states.TryGetValue(id, out var state) ? state : FeatureState.Off;

	/// <summary>True while the feature is installed and its behaviour should run.</summary>
	public bool IsOn(string id) => StateOf(id) == FeatureState.On;

	public IEnumerable<string> Registered => order;

	/// <summary>Brings every registered feature to its switch in <paramref name="config"/> (missing = off).</summary>
	public void Apply(IReadOnlyDictionary<string, bool> config)
	{
		foreach (var id in order)
		{
			var want = config != null && config.TryGetValue(id, out var on) && on;
			var state = states[id];
			if (want && state == FeatureState.Off) Install(id);
			else if (!want && state == FeatureState.On) Uninstall(id);
		}
	}

	private void Install(string id)
	{
		try
		{
			modules[id].Install();
			states[id] = FeatureState.On;
			Log.Debug($"[Features] {id} on.");
		}
		catch (Exception ex)
		{
			Log.Error($"[Features] {id} could not be turned on and stays off", ex);
			states[id] = FeatureState.Broken;
			try
			{
				modules[id].Uninstall();
			}
			catch (Exception undo)
			{
				Log.Error($"[Features] Undoing {id} failed", undo);
			}
		}
	}

	private void Uninstall(string id)
	{
		try
		{
			modules[id].Uninstall();
			Log.Debug($"[Features] {id} off.");
		}
		catch (Exception ex)
		{
			Log.Error($"[Features] Turning {id} off failed", ex);
		}
		states[id] = FeatureState.Off;
	}
}