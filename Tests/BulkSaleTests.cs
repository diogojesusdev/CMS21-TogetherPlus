using System;
using System.Collections.Generic;
using System.Linq;
using CMS21Together.Shared;
using CMS21Together.Shared.Economy;
using CMS21Together.Shared.Net;
using NUnit.Framework;

namespace CMS21Together.Tests;

/// <summary>Phase 6b: a bulk sale pays each item once, also when players sell the same items at once.</summary>
[TestFixture]
public class BulkSaleTests
{
	[TestCase(2, 1)]
	[TestCase(8, 2)]
	public void RacingSellersArePaidForEachItemOnce(int players, int seed)
	{
		var random = new Random(seed);
		var inventory = new HashSet<long>(Enumerable.Range(1, 400).Select(i => (long)i));
		var economy = new EconomyServer((_, _) => { }, () => Enumerable.Range(1, players));
		economy.Initialize(0, 0);
		var sold = new List<long>();
		bool Remove(long uid)
		{
			if (!inventory.Remove(uid)) return false;
			sold.Add(uid);
			return true;
		}
		for (var round = 0; round < 200; round++)
		{
			var slot = random.Next(1, players + 1);
			var picked = Enumerable.Range(1, 400).Where(_ => random.Next(10) == 0).Select(i => (long)i).ToList();
			// Each item is worth 10; the game credited the full list, as if it still held all of them.
			var credit = BulkSale.Settle(picked, picked.Count * 10L, Remove, out var count);
			Assert.That(credit, Is.EqualTo(count * 10L));
			if (credit > 0) economy.Grant(slot, credit);
		}
		Assert.That(sold.Distinct().Count(), Is.EqualTo(sold.Count), "no item removed twice");
		Assert.That(economy.Ledger.Money, Is.EqualTo(sold.Count * 10L), "paid exactly for what left the inventory");
	}

	[Test]
	public void RepeatedOrMissingItemsAreNotPaid()
	{
		var inventory = new HashSet<long> { 1, 2 };
		Assert.That(BulkSale.Settle(new long[] { 1, 1, 2, 3 }, 300, inventory.Remove, out var sold), Is.EqualTo(200));
		Assert.That(sold, Is.EqualTo(2));
		Assert.That(BulkSale.Settle(new long[] { 1 }, 100, inventory.Remove, out _), Is.Zero, "already sold");
		Assert.That(BulkSale.Settle(new long[0], 100, inventory.Remove, out _), Is.Zero);
	}

	[Test]
	public void WireRoundTripsAndRejectsBadSales()
	{
		using var packet = new Packet((int)PacketTypes.bulkSell);
		BulkSale.Write(packet, 3, 500, new long[] { 7, 8 });
		using var read = new Packet(packet.Id, packet.PayloadToArray());
		var (request, total, uids) = BulkSale.Read(read);
		Assert.That((request, total, uids), Is.EqualTo((3, 500L, new List<long> { 7, 8 })));

		using var bad = new Packet((int)PacketTypes.bulkSell);
		BulkSale.Write(bad, 3, -1, new long[0]);
		using var badRead = new Packet(bad.Id, bad.PayloadToArray());
		Assert.Throws<NetFormatException>(() => BulkSale.Read(badRead));
	}
}