using System.Collections.Generic;
using System.Linq;
using CMS21Together.Shared;
using CMS21Together.Shared.Locks;
using NUnit.Framework;

namespace CMS21Together.Tests;

/// <summary><see cref="LockServer"/> and <see cref="LockClient"/> talking through in-memory queues (packets really serialized).</summary>
[TestFixture]
public class LockProtocolTests
{
	private long now;
	private LockServer server;
	private Dictionary<int, LockClient> clients;
	private Queue<(int slot, Packet packet)> toServer;
	private Queue<(int slot, Packet packet)> toClients;
	private List<(int slot, int holder)> denials;

	[SetUp]
	public void SetUp()
	{
		now = 1000;
		clients = new Dictionary<int, LockClient>();
		toServer = new Queue<(int, Packet)>();
		toClients = new Queue<(int, Packet)>();
		denials = new List<(int, int)>();
		server = new LockServer(() => now,
			(slot, p) => toClients.Enqueue((slot, Copy(p))),
			p =>
			{
				foreach (var slot in clients.Keys) toClients.Enqueue((slot, Copy(p)));
			});
		Join(1);
		Join(2);
		Join(3);
	}

	private static Packet Copy(Packet p) => new(p.Id, p.PayloadToArray());

	private LockClient Join(int slot)
	{
		var client = new LockClient(slot, () => now, p => toServer.Enqueue((slot, Copy(p))));
		client.Denied += holder => denials.Add((slot, holder));
		clients[slot] = client;
		server.SendSnapshot(slot);
		Deliver();
		return client;
	}

	private void Leave(int slot)
	{
		clients.Remove(slot);
		server.PlayerLeft(slot);
		Deliver();
	}

	private void Deliver()
	{
		while (toServer.Count > 0 || toClients.Count > 0)
		{
			while (toServer.Count > 0)
			{
				var (slot, p) = toServer.Dequeue();
				switch ((PacketTypes)p.Id)
				{
					case PacketTypes.lockRequest: server.HandleRequest(slot, p); break;
					case PacketTypes.lockRenew: server.HandleRenew(slot, p); break;
					case PacketTypes.lockRelease: server.HandleRelease(slot, p); break;
					default: Assert.Fail($"unexpected {p.Id} to the server"); break;
				}
			}
			while (toClients.Count > 0)
			{
				var (slot, p) = toClients.Dequeue();
				if (!clients.TryGetValue(slot, out var client)) continue;
				switch ((PacketTypes)p.Id)
				{
					case PacketTypes.lockReply: client.HandleReply(p); break;
					case PacketTypes.lockState: client.HandleState(p); break;
					case PacketTypes.lockSnapshot: client.HandleSnapshot(p); break;
					default: Assert.Fail($"unexpected {p.Id} to a client"); break;
				}
			}
		}
	}

	private void Run(int ms, params string[] touched)
	{
		for (var t = 0; t < ms; t += 100)
		{
			now += 100;
			foreach (var client in clients.Values)
			{
				foreach (var key in touched) client.Table.Touch(key);
				client.Pump();
			}
			server.Tick();
			Deliver();
		}
	}

	[Test]
	public void SimultaneousRequestsGrantOneAndNameTheHolderToTheOther()
	{
		const string key = "car/1/part/0/4/-1";
		clients[2].Begin(key);
		clients[3].Begin(key);
		Deliver();
		Assert.That(server.Manager.HolderOf(key), Is.EqualTo(2));
		Assert.That(clients[2].Table.StateOf(key), Is.EqualTo(ClientLockTable.State.Held));
		Assert.That(clients[3].Table.StateOf(key), Is.EqualTo(ClientLockTable.State.Denied));
		Assert.That(denials, Is.EqualTo(new[] { (3, 2) }));
		foreach (var slot in new[] { 1, 3 })
			Assert.That(clients[slot].Table.BlockedBy(key), Is.EqualTo(2), $"#{slot} sees the holder");
		Assert.That(clients[2].Table.BlockedBy(key), Is.EqualTo(0));
	}

	[Test]
	public void ParentLockBlocksPartsAndPartsBlockTheParent()
	{
		clients[2].Begin("car/1/part/0/4/-1");
		Deliver();
		clients[3].Begin(LockKeys.Car(1));
		Deliver();
		Assert.That(denials, Is.EqualTo(new[] { (3, 2) }), "moving or deleting a car someone works on is refused");
		Assert.That(clients[3].Table.BlockedBy(LockKeys.Car(1)), Is.EqualTo(2));
	}

	[Test]
	public void LeaseLivesWhileTouchedAndFreesWhenIdle()
	{
		const string key = "lifter/1";
		clients[2].Begin(key);
		Deliver();
		Run(3 * ResourceLockManager.LeaseMs, key);
		Assert.That(server.Manager.HolderOf(key), Is.EqualTo(2), "renewed past several lease lengths");

		Run(ClientLockTable.IdleMs + 200);
		Assert.That(server.Manager.HolderOf(key), Is.EqualTo(0), "idle release");
		Assert.That(clients[3].Table.BlockedBy(key), Is.EqualTo(0));
		Assert.That(server.Manager.Count, Is.EqualTo(0));
	}

	[Test]
	public void StalledHolderLosesTheLeaseAndIsToldSo()
	{
		const string key = "lifter/1";
		clients[2].Begin(key);
		Deliver();
		now += ResourceLockManager.LeaseMs + 1;
		server.Tick();
		Deliver();
		Assert.That(server.Manager.HolderOf(key), Is.EqualTo(0));
		Assert.That(clients[3].Table.BlockedBy(key), Is.EqualTo(0));

		clients[2].Table.Touch(key);
		clients[2].Pump();
		Deliver();
		Assert.That(clients[2].Table.StateOf(key), Is.EqualTo(ClientLockTable.State.Lost));
	}

	[Test]
	public void LeavingFreesEverythingAndTheSlotStartsOver()
	{
		clients[2].Begin("lifter/1");
		clients[2].Begin("car/1/body/3");
		Deliver();
		Assert.That(server.Manager.Count, Is.EqualTo(2));
		Leave(2);
		Assert.That(server.Manager.Count, Is.EqualTo(0));
		Assert.That(clients[3].Table.AnyForeign, Is.False);

		var again = Join(2);
		Assert.That(again.Begin("lifter/1"), Is.EqualTo(1), "a fresh connection reuses token 1");
		Deliver();
		Assert.That(server.Manager.HolderOf("lifter/1"), Is.EqualTo(2));
	}

	[Test]
	public void LateJoinerReceivesCurrentHolders()
	{
		clients[2].Begin("lifter/1");
		Deliver();
		var late = Join(4);
		Assert.That(late.Table.BlockedBy("lifter/1"), Is.EqualTo(2));
	}

	[Test]
	public void SceneChangeFreesLocksButKeepsTokenOrder()
	{
		clients[2].Begin("lifter/1");
		Deliver();
		server.PlayerChangedScene(2);
		clients[2].Table.DropOwn();
		Deliver();
		Assert.That(clients[3].Table.BlockedBy("lifter/1"), Is.EqualTo(0));
		Assert.That(clients[2].Begin("lifter/1"), Is.GreaterThan(1));
		Deliver();
		Assert.That(server.Manager.HolderOf("lifter/1"), Is.EqualTo(2));
	}

	[Test]
	public void DeniedSwitchStillReleasesThePreviousComponent()
	{
		clients[2].Begin("car/1/body/3");
		clients[3].Begin("car/1/body/4");
		Deliver();
		clients[2].Begin("car/1/body/4");
		Deliver();
		Assert.That(denials, Is.EqualTo(new[] { (2, 3) }));
		clients[2].Pump();
		Deliver();
		Assert.That(server.Manager.HolderOf("car/1/body/3"), Is.EqualTo(0), "no 10 s ghost lock on the panel left behind");
		Assert.That(clients[1].Table.BlockedBy("car/1/body/3"), Is.EqualTo(0));
	}

	[Test]
	public void FencingRejectsWritesToAnotherPlayersResource()
	{
		const string key = "car/1/part/0/4/-1";
		clients[2].Begin(key);
		Deliver();
		Assert.That(server.AcceptWrite(2, key, clients[2].Stamp(key)), Is.True);
		Assert.That(server.AcceptWrite(3, key, clients[3].Stamp(key)), Is.False, "unlocked write while held");
		clients[3].Begin(key);
		Deliver();
		Assert.That(server.AcceptWrite(3, key, clients[3].Stamp(key)), Is.False, "denied write");
		Assert.That(server.AcceptWrite(1, LockKeys.Car(1), 0), Is.False, "car-wide write while a part is held");
		Assert.That(server.AcceptWrite(3, "car/1/part/0/5/-1", 0), Is.True, "other parts are free");
		Assert.That(server.RejectedWrites, Is.EqualTo(3));
	}

	[Test]
	public void WriteAfterTheHolderLetGoIsAccepted()
	{
		const string key = "car/1/body/3";
		clients[2].Begin(key);
		clients[3].Begin(key);
		Deliver();
		Run(ClientLockTable.IdleMs + 200);
		Assert.That(server.Manager.HolderOf(key), Is.EqualTo(0));
		Assert.That(server.AcceptWrite(3, key, clients[3].Stamp(key)), Is.True,
			"the denied player saw the holder's changes, so a later write is not stale");
	}

	[Test]
	public void ResyncIsRateLimitedPerPlayerAndKey()
	{
		Assert.That(server.ShouldResync(2, "car/1/body/3"), Is.True);
		Assert.That(server.ShouldResync(2, "car/1/body/3"), Is.False);
		Assert.That(server.ShouldResync(3, "car/1/body/3"), Is.True);
		Assert.That(server.ShouldResync(2, "car/1/body/4"), Is.True);
		now += LockServer.ResyncEveryMs;
		Assert.That(server.ShouldResync(2, "car/1/body/3"), Is.True);
	}

	[Test]
	public void ResyncTableStaysBounded()
	{
		for (var i = 0; i < LockServer.MaxResyncEntries; i++)
			Assert.That(server.ShouldResync(2, "item/" + i), Is.True);
		Assert.That(server.ShouldResync(2, "item/x"), Is.False, "full and nothing old enough to drop");
		now += LockServer.ResyncEveryMs;
		Assert.That(server.ShouldResync(2, "item/x"), Is.True, "old entries are pruned");
	}

	[Test]
	public void MalformedRequestIsRefusedWithoutStateChange()
	{
		clients[2].Begin("bogus/../x");
		Deliver();
		Assert.That(server.Manager.Count, Is.EqualTo(0));
		Assert.That(clients[2].Table.StateOf("bogus/../x"), Is.EqualTo(ClientLockTable.State.Released));
	}

	[Test]
	public void ManyPlayersOnHotKeysNeverShareAGrant()
	{
		for (var slot = 4; slot <= 8; slot++) Join(slot);
		var keys = new[] { LockKeys.Car(1), "car/1/part/0/4/-1", "car/1/body/3", "lifter/1" };
		var random = new System.Random(7);
		for (var round = 0; round < 2000; round++)
		{
			foreach (var client in clients.Values)
				if (random.Next(3) == 0) client.Begin(keys[random.Next(keys.Length)]);
			now += 50 + random.Next(100);
			foreach (var client in clients.Values) client.Pump();
			server.Tick();
			Deliver();

			var held = server.Manager.Snapshot();
			foreach (var a in held)
			foreach (var b in held)
				if (a.Value != b.Value)
					Assert.That(LockKeys.Conflicts(a.Key, b.Key), Is.False, $"{a.Key}@{a.Value} vs {b.Key}@{b.Value}");
			foreach (var client in clients.Values)
			foreach (var pair in held)
				Assert.That(client.Table.HolderOf(pair.Key), Is.EqualTo(pair.Value), "every mirror matches the server");
		}
	}
}
