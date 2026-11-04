using System;
using System.Collections.Generic;
using System.Linq;
using CMS21Together.BotHarness;
using CMS21Together.Shared;
using CMS21Together.Shared.Net;
using CMS21Together.Shared.World;
using NUnit.Framework;

namespace CMS21Together.Tests;

[Serializable]
public sealed class TestItem
{
	public long UID;
	public string ID;

	public TestItem()
	{
	}

	public TestItem(long uid, string id = "part")
	{
		UID = uid;
		ID = id;
	}

	public override string ToString() => $"{ID}#{UID}";
}

/// <summary>Phase 5: the shared inventory is host-authoritative and versioned (A15, A23, desync detector).</summary>
[TestFixture]
public class InventorySyncTests
{
	private static long Uid(TestItem item) => item.UID;

	/// <summary>Host and players wired directly (packets encoded and decoded), with an inbox per player.</summary>
	private sealed class Rig
	{
		public readonly InventoryHost<TestItem> Host;
		public readonly Dictionary<int, InventoryClient<TestItem>> Clients = new();
		public readonly Dictionary<int, Queue<(int id, byte[] payload)>> ToClient = new();
		public readonly Queue<(int slot, (int id, byte[] payload) frame)> ToHost = new();
		public long Now { get; set; }

		public Rig(params int[] slots)
		{
			Host = new InventoryHost<TestItem>(InventoryKind.Items, Uid, (slot, p) => ToClient[slot].Enqueue(Copy(p)), () => Clients.Keys.ToList());
			foreach (var slot in slots) Join(slot);
		}

		public InventoryClient<TestItem> Join(int slot)
		{
			ToClient[slot] = new Queue<(int id, byte[] payload)>();
			var client = new InventoryClient<TestItem>(InventoryKind.Items, Uid, p => ToHost.Enqueue((slot, Copy(p))), () => Now);
			Clients[slot] = client;
			Host.PlayerJoined(slot);
			client.RequestSnapshot(true);
			return client;
		}

		public void Deliver()
		{
			while (ToHost.Count > 0 || ToClient.Values.Any(q => q.Count > 0))
			{
				while (ToHost.Count > 0)
				{
					var (slot, frame) = ToHost.Dequeue();
					using var packet = Read(frame);
					switch ((PacketTypes)packet.Id)
					{
						case PacketTypes.invPush:
							var push = InventoryWire.ReadPush<TestItem>(packet);
							Host.HandlePush(slot, push.seq, push.op, push.item);
							break;
						case PacketTypes.invRequest:
							InventoryWire.ReadRequest(packet);
							Host.SendSnapshot(slot);
							break;
						case PacketTypes.invCheck:
							var check = InventoryWire.ReadCheck(packet);
							Host.HandleCheck(slot, check.version, check.hash);
							break;
					}
				}
				foreach (var pair in ToClient)
					while (pair.Value.Count > 0)
						DeliverToClient(pair.Key, pair.Value.Dequeue());
			}
		}

		public void DeliverToClient(int slot, (int id, byte[] payload) frame)
		{
			using var packet = Read(frame);
			var client = Clients[slot];
			switch ((PacketTypes)packet.Id)
			{
				case PacketTypes.invOp:
					var op = InventoryWire.ReadOp<TestItem>(packet);
					client.HandleOp(op.version, op.ack, op.op, op.item);
					break;
				case PacketTypes.invSnapshot:
					var snapshot = InventoryWire.ReadSnapshot<TestItem>(packet);
					client.HandleSnapshot(snapshot.version, snapshot.ack, snapshot.items);
					break;
			}
		}

		public HashSet<long> HostSet => new(Host.Authority.Items.Select(i => i.UID));
	}

	private static (int id, byte[] payload) Copy(Packet packet) => (packet.Id, packet.PayloadToArray());

	private static Packet Read((int id, byte[] payload) frame) => new(frame.id, frame.payload);

	[Test]
	public void ARemovedUidCanBeAddedAgain()
	{
		// Audit A23 exit test: add, remove, then re-add the same UID on a player.
		var rig = new Rig(1, 2);
		rig.Deliver();
		var player = rig.Clients[2];
		player.Local(InventoryOp.Add, new TestItem(42));
		rig.Deliver();
		player.Local(InventoryOp.Remove, new TestItem(42));
		rig.Deliver();
		player.Local(InventoryOp.Add, new TestItem(42));
		rig.Deliver();

		Assert.That(rig.HostSet, Is.EquivalentTo(new[] { 42L }));
		foreach (var client in rig.Clients.Values)
		{
			Assert.That(client.Mirror.Desired().Keys, Is.EquivalentTo(new[] { 42L }));
			Assert.That(client.Mirror.PendingCount, Is.Zero);
			Assert.That(client.Mirror.ConfirmedHash, Is.EqualTo(rig.Host.Authority.Hash));
		}
	}

	[Test]
	public void ASnapshotReachesOnlyTheRequester()
	{
		// Audit A15: the reply went to everyone except the requester.
		var rig = new Rig(1);
		rig.Deliver();
		rig.Clients[1].Local(InventoryOp.Add, new TestItem(1));
		rig.Clients[1].Local(InventoryOp.Add, new TestItem(2));
		rig.Deliver();
		var late = rig.Join(3);
		Assert.That(rig.ToClient[1], Is.Empty);
		rig.Deliver();
		Assert.That(late.Mirror.Desired().Keys, Is.EquivalentTo(new[] { 1L, 2L }));
		Assert.That(rig.Host.SnapshotsSent, Is.EqualTo(2));
	}

	[Test]
	public void OwnChangesShowAtOnceAndSurviveASnapshotThatPredatesThem()
	{
		var rig = new Rig(1, 2);
		rig.Deliver();
		var player = rig.Clients[2];
		player.Local(InventoryOp.Add, new TestItem(7));
		// A snapshot built before the host saw the add arrives first.
		using (var snapshot = new Packet((int)PacketTypes.invSnapshot))
		{
			InventoryWire.WriteSnapshot(snapshot, InventoryKind.Items, rig.Host.Authority.Version, 0, new List<TestItem>());
			rig.DeliverToClient(2, Copy(snapshot));
		}
		Assert.That(player.Mirror.Desired().ContainsKey(7), Is.True, "the pending add stays");
		rig.Deliver();
		Assert.That(player.Mirror.Desired().ContainsKey(7), Is.True);
		Assert.That(player.Mirror.PendingCount, Is.Zero);
	}

	[Test]
	public void AMissedChangeTriggersASnapshot()
	{
		var rig = new Rig(1, 2);
		rig.Deliver();
		rig.Clients[1].Local(InventoryOp.Add, new TestItem(1));
		rig.Clients[1].Local(InventoryOp.Add, new TestItem(2));
		rig.Deliver();
		rig.Clients[1].Local(InventoryOp.Add, new TestItem(3));
		rig.Clients[1].Local(InventoryOp.Add, new TestItem(4));
		while (rig.ToHost.Count > 0)
		{
			var (slot, frame) = rig.ToHost.Dequeue();
			using var packet = Read(frame);
			var push = InventoryWire.ReadPush<TestItem>(packet);
			rig.Host.HandlePush(slot, push.seq, push.op, push.item);
		}
		rig.ToClient[2].Dequeue(); // player 2 never sees version 3
		rig.Deliver();

		Assert.That(rig.Clients[2].Mirror.Desired().Keys, Is.EquivalentTo(new[] { 1L, 2L, 3L, 4L }));
		Assert.That(rig.Clients[2].Mirror.Synced, Is.True);
	}

	[Test]
	public void ADivergedPlayerIsCaughtByTheCheckAndRepaired()
	{
		var rig = new Rig(1, 2);
		rig.Deliver();
		rig.Clients[1].Local(InventoryOp.Add, new TestItem(5));
		rig.Deliver();
		// Player 2's copy goes wrong without any missed version (e.g. a bug applying a change).
		using (var bad = new Packet((int)PacketTypes.invSnapshot))
		{
			InventoryWire.WriteSnapshot(bad, InventoryKind.Items, rig.Host.Authority.Version, 0, new List<TestItem> { new(99) });
			rig.DeliverToClient(2, Copy(bad));
		}
		rig.Clients[2].SendCheck();
		rig.Deliver();

		Assert.That(rig.Host.Desyncs, Is.EqualTo(1));
		Assert.That(rig.Clients[2].Mirror.Desired().Keys, Is.EquivalentTo(new[] { 5L }));
		rig.Clients[2].SendCheck();
		rig.Deliver();
		Assert.That(rig.Host.Desyncs, Is.EqualTo(1), "repaired: the next check matches");
	}

	[Test]
	public void ReplayedOperationsChangeNothing()
	{
		var authority = new InventoryAuthority<TestItem>(Uid);
		authority.PlayerJoined(2);
		Assert.That(authority.Apply(2, 1, InventoryOp.Add, new TestItem(1)), Is.EqualTo(InventoryResult.Added));
		Assert.That(authority.Apply(2, 2, InventoryOp.Remove, new TestItem(1)), Is.EqualTo(InventoryResult.Removed));
		Assert.That(authority.Apply(2, 1, InventoryOp.Add, new TestItem(1)), Is.EqualTo(InventoryResult.Replayed));
		Assert.That(authority.Apply(2, 2, InventoryOp.Add, new TestItem(1)), Is.EqualTo(InventoryResult.Replayed), "the latest number again");
		Assert.That(authority.Apply(2, 3, InventoryOp.Remove, new TestItem(1)), Is.EqualTo(InventoryResult.NoChange));
		Assert.That(authority.Count, Is.Zero);
		Assert.That(authority.Version, Is.EqualTo(2));
	}

	[Test]
	public void UpdatesReplaceTheItemInPlaceAndPlayersFollowInOrder()
	{
		var authority = new InventoryAuthority<TestItem>(Uid);
		authority.PlayerJoined(2);
		authority.Apply(2, 1, InventoryOp.Add, new TestItem(1, "plain"));
		Assert.That(authority.Apply(2, 2, InventoryOp.Update, new TestItem(1, "painted")), Is.EqualTo(InventoryResult.Updated));
		Assert.That(authority.TryGet(1, out var stored) && stored.ID == "painted", Is.True);
		Assert.That(authority.Version, Is.EqualTo(2), "an update is a version like any change");
		Assert.That(authority.Apply(2, 3, InventoryOp.Update, new TestItem(9, "ghost")), Is.EqualTo(InventoryResult.NoChange), "never creates an item");
		Assert.That(authority.Count, Is.EqualTo(1));

		var mirror = new InventoryMirror<TestItem>(Uid);
		mirror.Snapshot(1, 0, new[] { new TestItem(1, "plain") });
		var before = mirror.ConfirmedHash;
		mirror.Remote(2, 0, InventoryOp.Update, new TestItem(1, "painted"));
		Assert.That(mirror.Desired()[1].ID, Is.EqualTo("painted"));
		Assert.That(mirror.ConfirmedHash, Is.EqualTo(before), "same UIDs, same hash");
		mirror.Remote(3, 0, InventoryOp.Update, new TestItem(7, "ghost"));
		Assert.That(mirror.Desired().ContainsKey(7), Is.False);

		mirror.Local(InventoryOp.Update, new TestItem(1, "plate"));
		Assert.That(mirror.Desired()[1].ID, Is.EqualTo("plate"), "own pending update shows at once");
	}

	[Test]
	public void UpdateOpsCrossTheWire()
	{
		using var packet = new Packet((int)PacketTypes.invOp);
		InventoryWire.WriteOp(packet, InventoryKind.Items, 5, 2, InventoryOp.Update, new TestItem(3, "x"));
		using var read = new Packet(packet.Id, packet.PayloadToArray());
		var op = InventoryWire.ReadOp<TestItem>(read);
		Assert.That((op.op, op.item.UID, op.item.ID), Is.EqualTo((InventoryOp.Update, 3L, "x")));
	}
	[Test]
	public void DiffTurnsTheGameInventoryIntoTheDesiredOne()
	{
		var current = new Dictionary<long, TestItem> { [1] = new(1), [2] = new(2) };
		var desired = new Dictionary<long, TestItem> { [2] = new(2), [3] = new(3) };
		var (add, remove) = InventoryDiff.Compute(current, desired);
		Assert.That(add.Select(i => i.UID), Is.EqualTo(new[] { 3L }));
		Assert.That(remove, Is.EqualTo(new[] { 1L }));
	}

	[Test]
	public void HashIgnoresOrder()
	{
		Assert.That(UidSetHash.Of(new long[] { 1, 2, 3 }), Is.EqualTo(UidSetHash.Of(new long[] { 3, 1, 2 })));
		Assert.That(UidSetHash.Of(new long[] { 1, 2 }), Is.Not.EqualTo(UidSetHash.Of(new long[] { 1, 3 })));
	}

	[TestCase(2, 60, 0, 0, 0.0, 1)]
	[TestCase(8, 120, 80, 40, 0.05, 2)]
	[TestCase(4, 90, 250, 150, 0.2, 3)]
	public void BotsChangingTheInventoryTogetherAlwaysConverge(int players, int seconds, int latency, int jitter, double loss, int seed)
	{
		var result = InventorySoak.Run(new InventorySoakOptions
		{
			Players = players,
			Seconds = seconds,
			LatencyMs = latency,
			JitterMs = jitter,
			LossRate = loss,
			Seed = seed
		});
		Assert.That(result.Passed, Is.True, result.ToString());
		Assert.That(result.Changes, Is.GreaterThan(players * 20));
	}
}