// Based on the F4 emergency reset in LiftInitialSync.cs from FixForTogether by TogetherFixer
// (https://github.com/Stevii001100/Fix-For-CMS21-Together), custom non-commercial licence.
// Modified for Together+: lifters with a car go through the authoritative lifter sync (absolute state, lifter lock),
// so the host's state follows; empty lifters are lowered on every game by a feature event.
using CMS21Together.ClientSide.Data.Garage;
using CMS21Together.ClientSide.Data.Garage.Car;
using CMS21Together.ClientSide.Data.Handle;
using CMS21Together.Shared;
using CMS21Together.Shared.Data.Vanilla;
using CMS21Together.Shared.Features;
using CMS21Together.Shared.Locks;
using UnityEngine;

namespace CMS21Together.ClientSide.Features.Garage;

/// <summary>F4: a stuck lifter is reset for everyone (with a car: middle; empty: ground).</summary>
public sealed class LiftResetFeature : GameFeature
{
	private const string Key = "reset";
	private float nextAllowed;

	public override string Id => FeatureCatalog.LiftReset;

	protected override void Patch()
	{
	}

	protected internal override void Tick()
	{
		if (!Input.GetKeyDown(KeyCode.F4) || !LocalAction || Time.unscaledTime < nextAllowed) return;
		nextAllowed = Time.unscaledTime + 2f;
		var lifters = GarageLoader.Get()?.carLifter;
		if (lifters == null) return;
		foreach (var lifter in lifters)
		{
			if (lifter == null || !lifter.gameObject.activeSelf) continue;
			var car = lifter.GetConnectedCarLoader();
			if (car == null)
			{
				lifter.InstantSet(0, true);
				continue;
			}
			var id = CarLoaderRegistry.IdOf(car);
			if (id < 0 || !ActionGate.TryBegin(LockKeys.Lifter(id))) continue;
			lifter.InstantSet(1, true);
			ClientSend.LifterPacket(ModLifterState.low, id);
		}
		Send(FeatureMessageKind.Event, Key);
		Log.Info("[LiftReset] Lifters reset (F4).");
	}

	protected override void Receive(FeatureMessage message)
	{
		var lifters = GarageLoader.Get()?.carLifter;
		if (lifters == null) return;
		foreach (var lifter in lifters)
			if (lifter != null && lifter.gameObject.activeSelf && lifter.GetConnectedCarLoader() == null)
				Remote(() => lifter.InstantSet(0, true));
	}
}