using System;
using System.Collections.Generic;
using System.Linq;
using CMS21Together.Shared;
using CMS21Together.Shared.Locks;
using CMS21Together.Shared.World;
using NUnit.Framework;

namespace CMS21Together.Tests;

/// <summary>
/// Phase 5 exit test (Phase 2 follow-up): two players race a mount or unmount of the same part. Whatever order the
/// host sees their lock requests, inventory changes and part writes in, exactly one wins, and the shared inventory
/// ends with exactly the items it should: no duplicate, nothing lost. Both players' game inventories agree with it.
/// </summary>
[TestFixture]
public class RaceReconcilerTests
{
	private const string PartId = "engine_block_v8";
	private const string Key = "car/0/part/0/12";

	private sealed class Race
	{
		public long Now;
		public readonly ResourceLockManager Locks;
		public readonly InventoryHost<TestItem> Host;
		public readonly RaceReconciler<TestItem> Reconciler;
		public readonly Dictionary<int, InventoryClient<TestItem>> Clients = new();
		public readonly Dictionary<int, Dictionary<long, TestItem>> Games = new();
		public readonly Dictionary<int, Queue<(int id, byte[] payload)>> Inbox = new();
		public bool PartMounted;
		public int Winner;

		public Race()
		{
			Locks = new ResourceLockManager(() => Now);
			Host = new InventoryHost<TestItem>(InventoryKind.Items, i => i.UID, (slot, p) => Inbox[slot].Enqueue((p.Id, p.PayloadToArray())),
				() => Clients.Keys.ToList());
			Reconciler = new RaceReconciler<TestItem>(i => i.UID, i => i.ID, () => Now);
			Host.Processed += Reconciler.Record;
			foreach (var slot in new[] { 1, 2 })
			{
				Inbox[slot] = new Queue<(int, byte[])>();
				Games[slot] = new Dictionary<long, TestItem>();
				var client = new InventoryClient<TestItem>(InventoryKind.Items, i => i.UID, p => HostReceives(slot, p), () => Now);
				var game = Games[slot];
				client.Changed += () =>
				{
					var (add, remove) = InventoryDiff.Compute(game, client.Mirror.Desired());
					foreach (var uid in remove) game.Remove(uid);
					foreach (var item in add) game[item.UID] = item;
				};
				Clients[slot] = client;
				Host.PlayerJoined(slot);
			}
			foreach (var slot in Clients.Keys) Host.SendSnapshot(slot);
			Flush();
		}

		/// <summary>Messages a player sends are queued here, then reach the host in an interleaving the test picks.</summary>
		public readonly Dictionary<int, Queue<Action>> ToHost = new() { [1] = new Queue<Action>(), [2] = new Queue<Action>() };

		private void HostReceives(int slot, Packet packet)
		{
			var copy = new Packet(packet.Id, packet.PayloadToArray());
			ToHost[slot].Enqueue(() =>
			{
				using (copy)
				{
					var push = InventoryWire.ReadPush<TestItem>(copy);
					Host.HandlePush(slot, push.seq, push.op, push.item);
				}
			});
		}

		public void Seed(params TestItem[] items)
		{
			foreach (var item in items) Clients[1].Local(InventoryOp.Add, item);
			foreach (var item in items) Games[1][item.UID] = item;
			while (ToHost[1].Count > 0) ToHost[1].Dequeue()();
			Flush();
		}

		/// <summary>A player's game: claims the part (optimistic: it does not wait), changes the inventory, writes the part.</summary>
		/// <summary>Body panels: the write names its exact item (no id match needed).</summary>
		public bool Exact;

		public void Play(int slot, bool unmount, TestItem item, int token)
		{
			ToHost[slot].Enqueue(() =>
			{
				var outcome = Locks.Acquire(slot, token, new[] { Key });
				if (outcome.Granted && Winner == 0) Winner = slot;
			});
			if (unmount) Games[slot][item.UID] = item;
			else Games[slot].Remove(item.UID);
			Clients[slot].Local(unmount ? InventoryOp.Add : InventoryOp.Remove, item);
			ToHost[slot].Enqueue(() =>
			{
				Now += 1000;
				if (Locks.CheckWrite(slot, Key, token) == WriteVerdict.Accept)
				{
					PartMounted = !unmount;
					return;
				}
				var fix = Exact ? Reconciler.RejectedExact(slot, item.UID, unmount) : Reconciler.Rejected(slot, PartId, unmount);
				if (fix.HasValue) Host.Correct(fix.Value.op, fix.Value.item);
			});
		}

		public void RunHost(Random random)
		{
			while (ToHost[1].Count + ToHost[2].Count > 0)
			{
				var slot = ToHost[1].Count == 0 ? 2 : ToHost[2].Count == 0 ? 1 : random.Next(2) + 1;
				ToHost[slot].Dequeue()();
				Now += random.Next(0, 300);
			}
			Flush();
		}

		public void Flush()
		{
			foreach (var pair in Inbox)
				while (pair.Value.Count > 0)
				{
					var (id, payload) = pair.Value.Dequeue();
					using var packet = new Packet(id, payload);
					if ((PacketTypes)id == PacketTypes.invOp)
					{
						var op = InventoryWire.ReadOp<TestItem>(packet);
						Clients[pair.Key].HandleOp(op.version, op.ack, op.op, op.item);
					}
					else
					{
						var snapshot = InventoryWire.ReadSnapshot<TestItem>(packet);
						Clients[pair.Key].HandleSnapshot(snapshot.version, snapshot.ack, snapshot.items);
					}
				}
		}

		public List<long> HostParts => Host.Authority.Items.Where(i => i.ID == PartId).Select(i => i.UID).OrderBy(u => u).ToList();

		public void AssertPlayersAgree()
		{
			var host = new HashSet<long>(Host.Authority.Items.Select(i => i.UID));
			foreach (var pair in Games)
				Assert.That(new HashSet<long>(pair.Value.Keys).SetEquals(host), Is.True, $"player {pair.Key}'s game inventory differs from the host's");
		}
	}

	[Test]
	public void TwoPlayersUnmountingTheSamePartLeaveExactlyOneItem([Range(1, 500)] int seed)
	{
		var random = new Random(seed);
		var race = new Race();
		race.Seed(new TestItem(1, "other"));
		race.PartMounted = true;
		// Each game creates its own item (its own UID) when the part comes off.
		race.Play(1, true, new TestItem(100, PartId), 11);
		race.Play(2, true, new TestItem(200, PartId), 22);
		race.RunHost(random);

		Assert.That(race.PartMounted, Is.False);
		Assert.That(race.HostParts, Is.EqualTo(new[] { race.Winner == 1 ? 100L : 200L }), "exactly the winner's item");
		Assert.That(race.Host.Authority.Contains(1), Is.True);
		race.AssertPlayersAgree();
	}

	[Test]
	public void TwoPlayersMountingTheSameItemUseItUpOnce([Range(1, 500)] int seed)
	{
		var random = new Random(seed);
		var race = new Race();
		var item = new TestItem(100, PartId);
		race.Seed(item, new TestItem(1, "other"));
		race.Play(1, false, item, 11);
		race.Play(2, false, item, 22);
		race.RunHost(random);

		Assert.That(race.PartMounted, Is.True);
		Assert.That(race.HostParts, Is.Empty, "the one item is on the car");
		Assert.That(race.Host.Authority.Contains(1), Is.True);
		race.AssertPlayersAgree();
	}

	[Test]
	public void TwoPlayersMountingWithTheirOwnItemsKeepTheLosersItem([Range(1, 500)] int seed)
	{
		var random = new Random(seed);
		var race = new Race();
		var first = new TestItem(100, PartId);
		var second = new TestItem(200, PartId);
		race.Seed(first, second);
		race.Play(1, false, first, 11);
		race.Play(2, false, second, 22);
		race.RunHost(random);

		Assert.That(race.PartMounted, Is.True);
		Assert.That(race.HostParts, Is.EqualTo(new[] { race.Winner == 1 ? 200L : 100L }), "the loser's item is back");
		race.AssertPlayersAgree();
	}

	[Test]
	public void TwoPlayersTakingOffTheSamePanelLeaveExactlyOneItem([Range(1, 300)] int seed)
	{
		// Phase 6 body-panel race: each game makes its own item for the panel (named however the game names it).
		var random = new Random(seed);
		var race = new Race { Exact = true };
		race.Seed(new TestItem(1, "other"));
		race.PartMounted = true;
		race.Play(1, true, new TestItem(100, "door_fl_car7"), 11);
		race.Play(2, true, new TestItem(200, "door_fl_car7"), 22);
		race.RunHost(random);

		var panels = race.Host.Authority.Items.Where(i => i.ID == "door_fl_car7").Select(i => i.UID).ToList();
		Assert.That(panels, Is.EqualTo(new[] { race.Winner == 1 ? 100L : 200L }));
		race.AssertPlayersAgree();
	}

	[Test]
	public void TwoPlayersPuttingOnTheSamePanelUseItUpOnce([Range(1, 300)] int seed)
	{
		var random = new Random(seed);
		var race = new Race { Exact = true };
		var panel = new TestItem(100, "hood_car7");
		race.Seed(panel, new TestItem(1, "other"));
		race.Play(1, false, panel, 11);
		race.Play(2, false, panel, 22);
		race.RunHost(random);

		Assert.That(race.Host.Authority.Contains(100), Is.False, "the panel is on the car");
		Assert.That(race.Host.Authority.Contains(1), Is.True);
		race.AssertPlayersAgree();
	}

	[Test]
	public void AnUnrelatedItemWithTheSameIdIsNeverTaken()
	{
		var now = 0L;
		var reconciler = new RaceReconciler<TestItem>(i => i.UID, i => i.ID, () => now);
		reconciler.Record(2, InventoryOp.Add, new TestItem(5, PartId), InventoryResult.Added);
		now += RaceReconciler<TestItem>.UnmountWindowMs + 1;
		Assert.That(reconciler.Rejected(2, PartId, true), Is.Null, "too old to belong to this write");
		reconciler.Record(3, InventoryOp.Add, new TestItem(6, PartId), InventoryResult.Added);
		Assert.That(reconciler.Rejected(2, PartId, true), Is.Null, "another player's item");
		reconciler.Record(2, InventoryOp.Add, new TestItem(7, "door"), InventoryResult.Added);
		Assert.That(reconciler.Rejected(2, PartId, true), Is.Null, "another part");
	}

	[Test]
	public void AChangeIsUndoneOnlyOnce()
	{
		var now = 0L;
		var reconciler = new RaceReconciler<TestItem>(i => i.UID, i => i.ID, () => now);
		reconciler.Record(2, InventoryOp.Add, new TestItem(5, PartId), InventoryResult.Added);
		Assert.That(reconciler.Rejected(2, PartId, true)?.item.UID, Is.EqualTo(5));
		Assert.That(reconciler.Rejected(2, PartId, true), Is.Null);
	}
}