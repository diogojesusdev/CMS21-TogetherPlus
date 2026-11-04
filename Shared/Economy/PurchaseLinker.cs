using System;
using System.Collections.Generic;
using System.Linq;

namespace CMS21Together.Shared.Economy;

/// <summary>
/// Purchase rollback, player side (Phase 6, plan §3.8). A purchase is whatever the game does in the same frame as a
/// debit: the items it puts in the inventory, the car it parks, the paint it applies. Those changes are sent with the
/// debit's transaction number, so the host only lets them happen if it charged the debit. Changes that never leave
/// this game (a car bought into the parking lot) register an undo that runs if the debit is refused.
/// </summary>
public sealed class PurchaseLinker
{
	private readonly Dictionary<int, List<Action>> undo = new();
	private int frame = int.MinValue;
	private int txn;

	/// <summary>Purchases undone because their debit was refused.</summary>
	public int RolledBack { get; private set; }

	/// <summary>A debit was sent as transaction <paramref name="number"/> during <paramref name="currentFrame"/>.</summary>
	public void Debit(int currentFrame, int number)
	{
		frame = currentFrame;
		txn = number;
	}

	/// <summary>The debit that pays for changes made during <paramref name="currentFrame"/> (0 = none).</summary>
	public int TxnFor(int currentFrame) => currentFrame == frame ? txn : 0;

	public void OnRefused(int number, Action action)
	{
		if (number <= 0 || action == null) return;
		if (!undo.TryGetValue(number, out var list)) undo[number] = list = new List<Action>();
		list.Add(action);
	}

	/// <summary>The host refused transaction <paramref name="number"/>: undo what it paid for.</summary>
	public void Refused(int number)
	{
		if (!undo.TryGetValue(number, out var list)) return;
		undo.Remove(number);
		foreach (var action in list)
		{
			try
			{
				action();
				RolledBack++;
			}
			catch (Exception ex)
			{
				Log.Error($"[Economy] Undoing a refused purchase failed", ex);
			}
		}
	}

	/// <summary>Every transaction up to <paramref name="upTo"/> was processed; the applied ones keep their purchase.</summary>
	public void Processed(int upTo)
	{
		foreach (var number in undo.Keys.Where(n => n <= upTo).ToList()) undo.Remove(number);
	}

	public void Reset()
	{
		undo.Clear();
		frame = int.MinValue;
		txn = 0;
	}
}