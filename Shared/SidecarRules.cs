using System;
using System.Collections.Generic;

namespace CMS21Together.Shared;

/// <summary>Pure rules for writing the mod's sidecar save (no Unity types, unit tested).</summary>
public static class SidecarRules
{
	/// <summary>
	/// Live session state (connected players' stats, the extra engine stand) belongs only to the save that is being
	/// played. Saving any other slot must leave that slot's data untouched (audit A21).
	/// </summary>
	public static bool AppliesLiveStats(bool hosting, int saveIndex, int currentSaveIndex) =>
		hosting && saveIndex == currentSaveIndex;

	/// <summary>
	/// Copies every connected player's live stats into their saved profile. Players without a profile are skipped,
	/// never ending the loop early (audit A17). Returns how many profiles were updated.
	/// </summary>
	public static int ApplyPlayerStats<TUser, TInfo>(IEnumerable<TUser> connected, IList<TInfo> infos,
		Func<TUser, string> userId, Func<TInfo, string> infoId, Action<TUser, TInfo> apply)
		where TUser : class
		where TInfo : class
	{
		if (connected == null || infos == null) return 0;
		var updated = 0;
		foreach (var user in connected)
		{
			if (user == null) continue;
			var id = userId(user);
			if (string.IsNullOrEmpty(id)) continue;

			TInfo match = null;
			foreach (var info in infos)
			{
				if (info != null && infoId(info) == id)
				{
					match = info;
					break;
				}
			}
			if (match == null) continue;

			apply(user, match);
			updated++;
		}
		return updated;
	}
}