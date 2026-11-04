// Based on WorkshopContentSync.cs from FixForTogether by TogetherFixer
// (https://github.com/Stevii001100/Fix-For-CMS21-Together), custom non-commercial licence.
// Modified for Together+: reports and the allowed list travel on the feature channel; the host's client computes
// the list every player has (WorkshopCars) and late joiners get it as a kept state.
using System.Collections.Generic;
using System.Linq;
using CMS21Together.ServerSide;
using CMS21Together.Shared;
using CMS21Together.Shared.Features;

namespace CMS21Together.ClientSide.Features.Lobby;

/// <summary>Workshop (mod) cars: only those every player has can appear in the session.</summary>
public sealed class WorkshopContentFeature : GameFeature
{
	private const string ReportKey = "report";
	private const string AllowedKey = "allowed";

	private readonly WorkshopCars reports = new();
	private readonly List<(CarConfigData data, int index)> hidden = new();
	private bool reported;

	public override string Id => FeatureCatalog.WorkshopContentSync;

	private static bool IsHost => Server.Instance != null && Server.Instance.isRunning;

	protected override void Patch()
	{
	}

	protected override void Started() => reported = false;

	protected override void Stopped()
	{
		Restore();
		reported = false;
	}

	private static CarBundleLoader Loader()
	{
		var loader = GameManager.Instance != null ? GameManager.Instance.CarBundleLoader : null;
		return loader != null && loader.IsReady && loader.CarNamesData != null ? loader : null;
	}

	private List<string> LocalCars()
	{
		var loader = Loader();
		if (loader == null) return null;
		var cars = new List<string>();
		foreach (var data in loader.CarNamesData)
			if (data != null && data.FileType == CarFileType.MOD && !string.IsNullOrEmpty(data.CarID))
				cars.Add(data.CarID);
		foreach (var (data, _) in hidden) cars.Add(data.CarID);
		return cars.Distinct().ToList();
	}

	protected internal override void Tick()
	{
		// Phase 8 (assessment): a player who left no longer hides the cars they lacked.
		var players = ServerSide.Data.ServerData.Instance?.connectedClients;
		if (IsHost && players != null && reports.KeepOnly(players.Keys.ToList())) Publish();
		if (reported) return;
		var cars = LocalCars();
		if (cars == null) return;
		reported = true;
		if (IsHost)
		{
			reports.Report(1, cars);
			Publish();
		}
		else Send(FeatureMessageKind.Event, ReportKey, p => WorkshopCars.Write(p, cars));
	}

	protected override void Receive(FeatureMessage message)
	{
		using var payload = Payload(message);
		var cars = WorkshopCars.Read(payload);
		if (message.Key == ReportKey)
		{
			if (!IsHost) return;
			reports.Report(message.From, cars);
			Publish();
		}
		else if (message.Key == AllowedKey && !IsHost)
			Apply(cars);
	}

	private void Publish()
	{
		var allowed = reports.Allowed();
		if (allowed == null) return;
		Apply(allowed);
		Send(FeatureMessageKind.State, AllowedKey, p => WorkshopCars.Write(p, allowed));
	}

	private void Apply(IReadOnlyCollection<string> allowed)
	{
		var loader = Loader();
		if (loader == null) return;
		Restore();
		var set = new HashSet<string>(allowed);
		for (var i = loader.CarNamesData.Count - 1; i >= 0; i--)
		{
			var data = loader.CarNamesData[i];
			if (data == null || data.FileType != CarFileType.MOD || set.Contains(data.CarID)) continue;
			hidden.Add((data, i));
			loader.CarNamesData.RemoveAt(i);
		}
		if (hidden.Count > 0) Log.Info($"[Workshop] {hidden.Count} workshop cars hidden: not every player has them.");
	}

	private void Restore()
	{
		var loader = GameManager.Instance != null ? GameManager.Instance.CarBundleLoader : null;
		if (loader?.CarNamesData == null || hidden.Count == 0)
		{
			hidden.Clear();
			return;
		}
		foreach (var (data, index) in hidden.OrderBy(h => h.index))
			loader.CarNamesData.Insert(System.Math.Min(index, loader.CarNamesData.Count), data);
		hidden.Clear();
	}
}