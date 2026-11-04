using System;
using System.Collections.Generic;
using CMS21Together.Shared.Net;

namespace CMS21Together.Shared.Economy;

/// <summary>
/// Bulk sell (Phase 6b): a player's game sold some inventory items and credited a total. The host removes from the
/// shared inventory only the items still there and pays the matching share of the total into the shared wallet, so
/// an item two players sell at once is paid once. Transport-agnostic.
/// </summary>
public static class BulkSale
{
	public const int MaxItems = 4096;

	/// <param name="tryRemove">Removes one item from the shared inventory; false when it is gone or in use.</param>
	/// <returns>The credit to grant (0 when nothing was sold).</returns>
	public static long Settle(IReadOnlyList<long> uids, long total, Func<long, bool> tryRemove, out int sold)
	{
		sold = 0;
		if (uids == null || uids.Count == 0 || tryRemove == null) return 0;
		var seen = new HashSet<long>();
		foreach (var uid in uids)
			if (seen.Add(uid) && tryRemove(uid))
				sold++;
		if (total <= 0 || sold == 0) return 0;
		// Every item counted once, even if the list repeated one.
		return total * sold / Math.Max(seen.Count, 1);
	}

	public static void Write(Packet packet, int request, long total, IReadOnlyList<long> uids)
	{
		packet.Write(request);
		packet.Write(total);
		packet.Write(uids.Count);
		foreach (var uid in uids) packet.Write(uid);
	}

	public static (int request, long total, List<long> uids) Read(Packet packet)
	{
		var request = packet.ReadInt();
		var total = packet.Read<long>();
		var count = packet.ReadInt();
		if (request <= 0 || total < 0 || total > EconomyLedger.MaxMoneyDelta) throw new NetFormatException($"Bulk sale {request} of {total}.");
		if (count < 0 || count > MaxItems) throw new NetFormatException($"Bulk sale of {count} items.");
		var uids = new List<long>(count);
		for (var i = 0; i < count; i++) uids.Add(packet.Read<long>());
		return (request, total, uids);
	}
}