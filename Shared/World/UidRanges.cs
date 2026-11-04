using System;
using System.Linq;

namespace CMS21Together.Shared.World;

/// <summary>
/// Item UID counters, one per player slot (hf3 kept 4: 1000, 10M, 20M, 30M). Phase 7 grows them for 8 players: a new
/// slot starts at its own 10M range, or past every counter already in use, so two slots never hand out the same UID.
/// </summary>
public static class UidRanges
{
	public const long Range = 10_000_000;

	public static long[] Initial(int players) => Ensure(null, players);

	/// <summary>
	/// The counter after a slot used <paramref name="uid"/> (Phase 8 assessment: it counted one per item push, but group
	/// items and their parts use UIDs too, so a rejoin could hand out a UID already in use). Only UIDs in the slot's
	/// own range move it; a re-added item from another range never does.
	/// </summary>
	public static long Advance(long counter, long uid) => uid >= counter && uid - counter < Range ? uid + 1 : counter;

	/// <summary>The counters, grown to <paramref name="players"/> slots; existing counters are kept as they are.</summary>
	public static long[] Ensure(long[] counters, int players)
	{
		counters ??= Array.Empty<long>();
		if (counters.Length >= players) return counters;
		var grown = new long[players];
		Array.Copy(counters, grown, counters.Length);
		// The first range above every counter in use; each new slot takes its own range at or above it.
		var next = counters.Length == 0 ? 0 : Math.Max(0, counters.Max()) / Range + 1;
		for (var i = counters.Length; i < players; i++)
		{
			var index = Math.Max(i, next);
			grown[i] = index == 0 ? 1000 : index * Range;
			next = index + 1;
		}
		return grown;
	}
}