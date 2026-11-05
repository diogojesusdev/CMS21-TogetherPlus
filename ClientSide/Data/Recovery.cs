using System;
using System.Linq;
using CMS21Together.ClientSide.Data.Garage.Car;
using CMS21Together.ClientSide.Data.Player;
using CMS21Together.ServerSide;
using CMS21Together.Shared;
using UnityEngine;

namespace CMS21Together.ClientSide.Data;

/// <summary>
/// Recovery keys (Phase 6, the Together Fixes debug/recovery menu, plan §3.7). Ctrl+Shift+R: forced resync, i.e. the
/// cars are checked against the host's now, and the shared inventory and wallet are fetched again. Ctrl+Shift+L: writes
/// the locks, the wallet and (on the host) the ledger's last entries to the log, for bug reports. Ctrl+Shift+E: copies
/// the last stopped-action report to the clipboard.
/// </summary>
public static class Recovery
{
	private static float nextAllowed;

	public static void Pump()
	{
		if (Client.Instance == null || !Client.Instance.isConnected || !GameLoadHook.IsGameReady()) return;
		if (!(Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))) return;
		if (!(Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))) return;
		var resync = Input.GetKeyDown(KeyCode.R);
		var report = Input.GetKeyDown(KeyCode.L);
		var copyError = Input.GetKeyDown(KeyCode.E);
		if (!resync && !report && !copyError) return;
		if (Time.unscaledTime < nextAllowed) return;
		nextAllowed = Time.unscaledTime + 2f;
		try
		{
			if (resync) Resync();
			else if (copyError) CrashUI.CopyLast();
			else Report();
		}
		catch (Exception ex)
		{
			Log.Error("[Recovery] The recovery key failed", ex);
		}
	}

	private static void Resync()
	{
		Log.Info("[Recovery] Forced resync: checking cars, inventory and wallet against the host.");
		CarChecks.ForceCheck();
		Player.Inventory.RequestSnapshots();
		Wallet.OnBalance();
		Purchases.Toast("Resyncing with the host");
	}

	private static void Report()
	{
		var client = Client.Instance;
		var lines = new System.Collections.Generic.List<string> { "[Recovery] Diagnostics:" };
		var table = client.Locks?.Table;
		if (table != null)
		{
			lines.Add($"  Locks: {table.OwnCount} mine; held: " +
			          string.Join(", ", table.Holders.Select(h => $"{h.Key}=#{h.Value}").Take(50)));
		}
		var economy = client.Economy;
		if (economy != null)
			lines.Add($"  Wallet: host {economy.HostMoney}/{economy.HostScrap} at #{economy.Seq}, {economy.PendingCount} pending, shown {economy.Money}/{economy.Scrap}.");
		var server = Server.Instance;
		if (server != null && server.isRunning)
		{
			var ledger = server.Economy?.Ledger;
			if (ledger != null)
			{
				lines.Add($"  Ledger: {ledger.Money}/{ledger.Scrap} at #{ledger.Seq}; last entries:");
				lines.AddRange(ledger.History.Reverse().Take(10).Select(e => "    " + e));
			}
			var net = server.Host;
			if (net != null)
			{
				lines.Add($"  Host tick: last {net.LastTickMs:0.00} ms, max {net.MaxTickMs:0.00} ms (budget {net.BudgetMs} ms, hit {net.BudgetHits}x), backlog {net.Backlog}; " +
				          $"{net.PeerCount}/{net.MaxPlayers} players, RTT " + string.Join(", ", net.Peers.Select(p => $"#{p.SlotId} {p.RttMs} ms")) + ".");
				net.ResetMetrics();
			}
			var detector = server.CarDetector;
			if (detector != null) lines.Add($"  Car checks: {detector.Checks}, pushes {detector.Pushes}, unrepaired {detector.Unrepaired}.");
		}
		Log.Info(string.Join("\n", lines));
		Purchases.Toast("Diagnostics written to the log");
	}
}
