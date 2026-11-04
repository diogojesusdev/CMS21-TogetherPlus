using System;
using System.Collections.Generic;

namespace CMS21Together.Shared.World;

/// <summary>Parked cars are keyed by parking slot, and empty slots have no key (review fix #3).</summary>
public static class ParkSlots
{
	/// <summary>Puts each car into its own slot; keys outside <c>[0, slots)</c> are skipped. Returns the number placed.</summary>
	public static int Place<T>(IDictionary<int, T> cars, int slots, Action<int, T> put)
	{
		if (cars == null || put == null) return 0;
		var placed = 0;
		foreach (var pair in cars)
		{
			if (pair.Key < 0 || pair.Key >= slots || pair.Value == null) continue;
			put(pair.Key, pair.Value);
			placed++;
		}
		return placed;
	}
}