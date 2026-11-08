using System.Collections.Generic;
using System.Linq;
using CMS21Together.Shared;
using CMS21Together.Shared.Locks;
using NUnit.Framework;

namespace CMS21Together.Tests;

[TestFixture]
public class LockKeyTests
{
	[Test]
	public void BuildsKeysAndMapsEngineStands()
	{
		Assert.That(LockKeys.Part(2, 1, 7, 0), Is.EqualTo("car/2/part/1/7/0"));
		Assert.That(LockKeys.Part(0, 0, 3, -1), Is.EqualTo("car/0/part/0/3/-1"));
		Assert.That(LockKeys.ForPartScript(-1, 0, 5, -1), Is.EqualTo("stand/1/part/5"));
		Assert.That(LockKeys.ForPartScript(-2, 0, 5, -1), Is.EqualTo("stand/2/part/5"));
		Assert.That(LockKeys.ForPartScript(4, 2, 5, 1), Is.EqualTo("car/4/part/2/5/1"));
		Assert.That(LockKeys.Body(1, 12), Is.EqualTo("car/1/body/12"));
	}

	[TestCase("car/1", true)]
	[TestCase("car/1/part/0/3/-1", true)]
	[TestCase("tool/TireChanger", true)]
	[TestCase("item/123456789012", true)]
	[TestCase("car", false)]
	[TestCase("car//1", false)]
	[TestCase("plane/1", false)]
	[TestCase("car/1 2", false)]
	[TestCase("car/../x", false)]
	[TestCase("", false)]
	[TestCase(null, false)]
	public void Validates(string key, bool valid) => Assert.That(LockKeys.IsValid(key), Is.EqualTo(valid));

	[Test]
	public void RejectsOverlongKeysAndSegments()
	{
		Assert.That(LockKeys.IsValid("tool/" + new string('a', 25)), Is.False);
		Assert.That(LockKeys.IsValid("car/1/" + string.Join("/", Enumerable.Repeat("aaaaaaaaaaaaaaaaaaaa", 4))), Is.True, "89 chars, 6 segments");
		Assert.That(LockKeys.IsValid("car/1/" + string.Join("/", Enumerable.Repeat("aaaaaaaaaaaaaaaaaaaa", 5))), Is.False, "too long");
		Assert.That(LockKeys.IsValid("car/1/a/b/c/d/e"), Is.False, "too many segments");
	}

	[Test]
	public void HierarchyAndComponents()
	{
		Assert.That(LockKeys.Parent("car/1/part/0/3/-1"), Is.EqualTo("car/1"));
		Assert.That(LockKeys.Parent("stand/2/part/5"), Is.EqualTo("stand/2"));
		Assert.That(LockKeys.Parent("car/1"), Is.Null);
		Assert.That(LockKeys.Conflicts("car/1", "car/1/body/3"), Is.True);
		Assert.That(LockKeys.Conflicts("car/1/body/3", "car/1"), Is.True);
		Assert.That(LockKeys.Conflicts("car/1/body/3", "car/1/body/4"), Is.False);
		Assert.That(LockKeys.Conflicts("car/1", "car/10/body/4"), Is.False);
		Assert.That(LockKeys.IsComponent("car/1/body/3"), Is.True);
		Assert.That(LockKeys.IsComponent("stand/1/part/5"), Is.True);
		Assert.That(LockKeys.IsComponent("lifter/1"), Is.False);
		Assert.That(LockKeys.IsComponent("car/1"), Is.False);
	}
}

[TestFixture]
public class ResourceLockManagerTests
{
	private long now;
	private ResourceLockManager manager;
	private List<(string key, int holder)> events;

	[SetUp]
	public void SetUp()
	{
		now = 1000;
		manager = new ResourceLockManager(() => now);
		events = new List<(string, int)>();
		manager.Changed += (k, h) => events.Add((k, h));
	}

	[Test]
	public void GrantsAndDeniesOtherPlayers()
	{
		Assert.That(manager.Acquire(1, 1, new[] { "car/1/body/3" }).Granted);
		var denied = manager.Acquire(2, 1, new[] { "car/1/body/3" });
		Assert.That(denied.Status, Is.EqualTo(LockStatus.Denied));
		Assert.That(denied.Holder, Is.EqualTo(1));
		Assert.That(manager.Acquire(2, 2, new[] { "car/1/body/4" }).Granted, "different panel is free");
	}

	[Test]
	public void ParentAndChildConflictBothWays()
	{
		Assert.That(manager.Acquire(1, 1, new[] { "car/1/part/0/3/-1" }).Granted);
		Assert.That(manager.Acquire(2, 1, new[] { "car/1" }).Holder, Is.EqualTo(1), "whole car blocked by a part");
		Assert.That(manager.Acquire(2, 2, new[] { "car/2" }).Granted);
		Assert.That(manager.Acquire(1, 2, new[] { "car/2/body/1" }).Holder, Is.EqualTo(2), "part blocked by whole car");
		Assert.That(manager.Acquire(2, 3, new[] { "car/2/body/1" }).Granted, "own parent does not block");
	}

	[Test]
	public void MultiKeyRequestIsAllOrNothing()
	{
		manager.Acquire(1, 1, new[] { "tool/TireChanger" });
		events.Clear();
		var outcome = manager.Acquire(2, 1, new[] { "lifter/1", "tool/TireChanger" });
		Assert.That(outcome.Status, Is.EqualTo(LockStatus.Denied));
		Assert.That(manager.HolderOf("lifter/1"), Is.EqualTo(0));
		Assert.That(events, Is.Empty);
	}

	[Test]
	public void TokensMustIncreasePerOwner()
	{
		Assert.That(manager.Acquire(1, 5, new[] { "lifter/1" }).Granted);
		Assert.That(manager.Acquire(1, 5, new[] { "lifter/2" }).Status, Is.EqualTo(LockStatus.Invalid));
		Assert.That(manager.Acquire(1, 4, new[] { "lifter/2" }).Status, Is.EqualTo(LockStatus.Invalid));
		Assert.That(manager.Acquire(1, 0, new[] { "lifter/2" }).Status, Is.EqualTo(LockStatus.Invalid));
		Assert.That(manager.Acquire(2, 1, new[] { "lifter/2" }).Granted, "tokens are per owner");
	}

	[Test]
	public void RejectsBadRequests()
	{
		Assert.That(manager.Acquire(1, 1, new[] { "nope/1" }).Status, Is.EqualTo(LockStatus.Invalid));
		Assert.That(manager.Acquire(1, 2, Enumerable.Range(0, 9).Select(i => "lifter/" + i).ToList()).Status, Is.EqualTo(LockStatus.Invalid));
		Assert.That(manager.Acquire(0, 3, new[] { "lifter/1" }).Status, Is.EqualTo(LockStatus.Invalid));
		Assert.That(manager.Count, Is.EqualTo(0));
	}

	[Test]
	public void CapsGrantsPerOwner()
	{
		var token = 0;
		for (var i = 0; i < ResourceLockManager.MaxGrantsPerOwner; i++)
			Assert.That(manager.Acquire(1, ++token, new[] { "park/" + i }).Granted);
		Assert.That(manager.Acquire(1, ++token, new[] { "park/99" }).Status, Is.EqualTo(LockStatus.Invalid));
		Assert.That(manager.Acquire(1, ++token, new[] { "park/3" }).Granted, "re-taking a held key adds nothing");
	}

	[Test]
	public void TakingAComponentReleasesTheOwnersOtherComponents()
	{
		manager.Acquire(1, 1, new[] { "car/1/body/3" });
		manager.Acquire(1, 2, new[] { "lifter/1" });
		manager.Acquire(1, 3, new[] { "car/1/body/4" });
		Assert.That(manager.HolderOf("car/1/body/3"), Is.EqualTo(0));
		Assert.That(manager.HolderOf("car/1/body/4"), Is.EqualTo(1));
		Assert.That(manager.HolderOf("lifter/1"), Is.EqualTo(1), "stations are kept");
		Assert.That(events, Does.Contain(("car/1/body/3", 0)));
	}

	[Test]
	public void LeaseExpiresUnlessRenewed()
	{
		manager.Acquire(1, 1, new[] { "lifter/1" });
		manager.Acquire(2, 1, new[] { "lifter/2" });
		now += ResourceLockManager.LeaseMs - 1;
		Assert.That(manager.Renew(1, 1), Is.True);
		now += 2;
		Assert.That(manager.Expire(), Is.EqualTo(1));
		Assert.That(manager.HolderOf("lifter/1"), Is.EqualTo(1));
		Assert.That(manager.HolderOf("lifter/2"), Is.EqualTo(0));
		Assert.That(manager.Renew(2, 1), Is.False, "late renewal is a lost lease");
		Assert.That(events, Does.Contain(("lifter/2", 0)));
	}

	[Test]
	public void ReleaseFreesOnlyThatToken()
	{
		manager.Acquire(1, 1, new[] { "lifter/1", "tool/TireChanger" });
		manager.Acquire(1, 2, new[] { "lifter/2" });
		manager.Release(1, 1);
		Assert.That(manager.HolderOf("lifter/1"), Is.EqualTo(0));
		Assert.That(manager.HolderOf("tool/TireChanger"), Is.EqualTo(0));
		Assert.That(manager.HolderOf("lifter/2"), Is.EqualTo(1));
		manager.Release(2, 2);
		Assert.That(manager.HolderOf("lifter/2"), Is.EqualTo(1), "another owner cannot release it");
	}

	[Test]
	public void ReleaseAllAndForgetTokens()
	{
		manager.Acquire(1, 7, new[] { "lifter/1" });
		manager.ReleaseAll(1, forgetTokens: false);
		Assert.That(manager.Count, Is.EqualTo(0));
		Assert.That(manager.Acquire(1, 1, new[] { "lifter/1" }).Status, Is.EqualTo(LockStatus.Invalid), "same connection keeps its counter");
		manager.ReleaseAll(1, forgetTokens: true);
		Assert.That(manager.Acquire(1, 1, new[] { "lifter/1" }).Granted, "a new connection in the slot starts over");
	}

	[Test]
	public void SnapshotListsHolders()
	{
		manager.Acquire(1, 1, new[] { "lifter/1" });
		manager.Acquire(3, 1, new[] { "car/2/body/1" });
		var snapshot = manager.Snapshot().ToDictionary(p => p.Key, p => p.Value);
		Assert.That(snapshot, Is.EquivalentTo(new Dictionary<string, int> { ["lifter/1"] = 1, ["car/2/body/1"] = 3 }));
	}

	[Test]
	public void FencingRejectsWritesWhileSomeoneElseHoldsIt()
	{
		manager.Acquire(1, 1, new[] { "car/1/body/3" });
		Assert.That(manager.CheckWrite(2, "car/1/body/3", 0, out var holder), Is.EqualTo(WriteVerdict.RejectHeld));
		Assert.That(holder, Is.EqualTo(1));
		Assert.That(manager.CheckWrite(1, "car/1/body/3", 1), Is.EqualTo(WriteVerdict.Accept));
		manager.Acquire(1, 2, new[] { "car/2" });
		Assert.That(manager.CheckWrite(2, "car/2/part/0/1/-1", 0), Is.EqualTo(WriteVerdict.RejectHeld), "parent held by another");
		Assert.That(manager.CheckWrite(1, "car/2/part/0/1/-1", 2), Is.EqualTo(WriteVerdict.Accept), "own parent covers the part");
	}

	[Test]
	public void FencingRejectsDeniedRequestsAndBadKeys()
	{
		Assert.That(manager.CheckWrite(1, "car/1/body/3", -1), Is.EqualTo(WriteVerdict.RejectStale));
		Assert.That(manager.CheckWrite(1, "bad key", 0), Is.EqualTo(WriteVerdict.RejectStale));
	}

	[Test]
	public void FencingRejectsAStaleWriteAfterSomeoneElseWrote()
	{
		manager.Acquire(1, 1, new[] { "car/1/body/3" });
		manager.ReleaseAll(1, false); // lease ended (idle release or expiry)
		manager.Acquire(2, 1, new[] { "car/1/body/3" });
		Assert.That(manager.CheckWrite(2, "car/1/body/3", 1), Is.EqualTo(WriteVerdict.Accept));
		manager.ReleaseAll(2, false);
		Assert.That(manager.CheckWrite(1, "car/1/body/3", 1), Is.EqualTo(WriteVerdict.RejectStale), "player 1's late write would undo player 2");
		Assert.That(manager.CheckWrite(1, "car/1/body/3", 0), Is.EqualTo(WriteVerdict.Accept), "an unlocked write (no token) is not fenced");
		Assert.That(manager.CheckWrite(1, "car/1/body/3", 1), Is.EqualTo(WriteVerdict.Accept), "player 1 is now the last writer");
	}

	[Test]
	public void FencingAcceptsTheWritersOwnLateWriteAfterItsClaimLapsed()
	{
		// A mount is only sent when the last bolt goes in, seconds after the claim, and the client releases a claim
		// it has not touched for 6 s. The rear suspension arm came back off because another player had worked on that
		// same part minutes earlier, which made the late write look stale.
		manager.Acquire(2, 1, new[] { "car/3/part/1/5/7" });
		Assert.That(manager.CheckWrite(2, "car/3/part/1/5/7", 1), Is.EqualTo(WriteVerdict.Accept));
		manager.ReleaseAll(2, false);

		manager.Acquire(1, 1, new[] { "car/3/part/1/5/7" });
		manager.ReleaseAll(1, false); // idle release while the player was still bolting the part on
		Assert.That(manager.CheckWrite(1, "car/3/part/1/5/7", 1), Is.EqualTo(WriteVerdict.Accept),
			"the player held the part last, so their late mount is theirs to write");
	}

	[Test]
	public void FencingStillRejectsALateWriteWhenSomeoneElseTookTheKeySince()
	{
		manager.Acquire(1, 1, new[] { "car/3/part/1/5/7" });
		manager.ReleaseAll(1, false);
		manager.Acquire(2, 1, new[] { "car/3/part/1/5/7" }); // the other player claimed it after player 1 let go
		manager.ReleaseAll(2, false);
		Assert.That(manager.CheckWrite(1, "car/3/part/1/5/7", 1), Is.EqualTo(WriteVerdict.RejectStale));
	}

	[Test]
	public void AcceptedWritesRenewTheLease()
	{
		manager.Acquire(1, 1, new[] { "car/1/body/3" });
		now += ResourceLockManager.LeaseMs - 10;
		manager.CheckWrite(1, "car/1/body/3", 1);
		now += 20;
		manager.Expire();
		Assert.That(manager.HolderOf("car/1/body/3"), Is.EqualTo(1));
	}

	[Test]
	public void WriterRecordsAreBounded()
	{
		for (var i = 0; i < ResourceLockManager.MaxWriteRecords + 10; i++)
			manager.CheckWrite(1, "item/" + i, 0);
		// The oldest record was evicted, so a stale write to it is no longer recognised as stale (bounded memory wins).
		Assert.That(manager.CheckWrite(2, "item/0", 5), Is.EqualTo(WriteVerdict.Accept));
		Assert.That(manager.CheckWrite(2, "item/" + (ResourceLockManager.MaxWriteRecords + 5), 5), Is.EqualTo(WriteVerdict.RejectStale));
	}
}

[TestFixture]
public class ClientLockTableTests
{
	private long now;
	private ClientLockTable table;

	[SetUp]
	public void SetUp()
	{
		now = 1000;
		table = new ClientLockTable(2, () => now);
	}

	private List<LockCommand> Pump()
	{
		var output = new List<LockCommand>();
		table.Pump(output);
		return output;
	}

	[Test]
	public void BlockedByOtherHoldersIncludingParentsAndChildren()
	{
		table.OnState("car/1/body/3", 1);
		table.OnState("car/2", 3);
		table.OnState("lifter/1", 2);
		Assert.That(table.BlockedBy("car/1/body/3"), Is.EqualTo(1));
		Assert.That(table.BlockedBy("car/1"), Is.EqualTo(1));
		Assert.That(table.BlockedBy("car/2/part/0/1/-1"), Is.EqualTo(3));
		Assert.That(table.BlockedBy("lifter/1"), Is.EqualTo(0), "own lock");
		Assert.That(table.BlockedBy("car/1/body/4"), Is.EqualTo(0));
		table.OnState("car/1/body/3", 0);
		Assert.That(table.BlockedBy("car/1"), Is.EqualTo(0));
	}

	[Test]
	public void SnapshotReplacesHolders()
	{
		table.OnState("lifter/1", 3);
		table.OnSnapshot(new[] { new KeyValuePair<string, int>("lifter/2", 4) });
		Assert.That(table.BlockedBy("lifter/1"), Is.EqualTo(0));
		Assert.That(table.BlockedBy("lifter/2"), Is.EqualTo(4));
	}

	[Test]
	public void BeginReturnsATokenOnceAndStampsWrites()
	{
		var token = table.Begin("car/1/body/3");
		Assert.That(token, Is.GreaterThan(0));
		Assert.That(table.Begin("car/1/body/3"), Is.EqualTo(0), "already pending");
		Assert.That(table.TokenFor("car/1/body/3"), Is.EqualTo(token));
		Assert.That(table.TokenFor("car/1/body/4"), Is.EqualTo(0));
		table.OnReply(token, LockStatus.Granted, 2);
		Assert.That(table.StateOf("car/1/body/3"), Is.EqualTo(ClientLockTable.State.Held));
		Assert.That(table.Begin("car/1/body/3"), Is.EqualTo(0), "already held");
	}

	[Test]
	public void DeniedRequestStampsZeroAndNamesTheHolder()
	{
		var token = table.Begin("car/1/body/3");
		Assert.That(table.OnReply(token, LockStatus.Denied, 4), Is.EqualTo(4));
		Assert.That(table.TokenFor("car/1/body/3"), Is.EqualTo(0), "fenced by the holder, not by a stale token");
		var retry = table.Begin("car/1/body/3");
		Assert.That(retry, Is.GreaterThan(token), "a new attempt asks again");
		Assert.That(table.TokenFor("car/1/body/3"), Is.EqualTo(retry));
	}

	[Test]
	public void RenewsWhileTouchedAndReleasesWhenIdle()
	{
		var token = table.Begin("lifter/1");
		table.OnReply(token, LockStatus.Granted, 2);
		now += ClientLockTable.RenewMs;
		table.Touch("lifter/1");
		var commands = Pump();
		Assert.That(commands.Single().Kind, Is.EqualTo(LockCommandKind.Renew));
		Assert.That(commands.Single().Token, Is.EqualTo(token));
		now += ClientLockTable.IdleMs;
		commands = Pump();
		Assert.That(commands.Single().Kind, Is.EqualTo(LockCommandKind.Release));
		Assert.That(table.StateOf("lifter/1"), Is.EqualTo(ClientLockTable.State.Released));
		Assert.That(table.TokenFor("lifter/1"), Is.EqualTo(token), "late writes still carry the token");
		now += ClientLockTable.RetainMs;
		Pump();
		Assert.That(table.TokenFor("lifter/1"), Is.EqualTo(0));
		Assert.That(table.OwnCount, Is.EqualTo(0));
	}

	[Test]
	public void PendingRequestsAreNotRenewedButStillReleaseWhenIdle()
	{
		table.Begin("lifter/1");
		now += ClientLockTable.RenewMs;
		Assert.That(Pump(), Is.Empty);
		now += ClientLockTable.IdleMs;
		Assert.That(Pump().Single().Kind, Is.EqualTo(LockCommandKind.Release));
	}

	[Test]
	public void NewComponentReleasesOtherComponentsLocally()
	{
		var first = table.Begin("car/1/body/3");
		var station = table.Begin("lifter/1");
		table.Begin("car/1/body/4");
		Assert.That(table.StateOf("car/1/body/3"), Is.EqualTo(ClientLockTable.State.Released));
		Assert.That(table.StateOf("lifter/1"), Is.EqualTo(ClientLockTable.State.Pending));
		Assert.That(table.TokenFor("car/1/body/3"), Is.EqualTo(first));
		Assert.That(station, Is.GreaterThan(first));
		var commands = Pump();
		Assert.That(commands.Single().Kind, Is.EqualTo(LockCommandKind.Release), "the server is told even if the new request is denied");
		Assert.That(commands.Single().Token, Is.EqualTo(first));
		Assert.That(Pump(), Is.Empty);
	}

	[Test]
	public void AnyForeignIgnoresOwnHolds()
	{
		Assert.That(table.AnyForeign, Is.False);
		table.OnState("lifter/1", 2);
		Assert.That(table.AnyForeign, Is.False);
		table.OnState("lifter/2", 3);
		Assert.That(table.AnyForeign, Is.True);
		table.OnState("lifter/2", 0);
		Assert.That(table.AnyForeign, Is.False);
	}

	[Test]
	public void LostLeaseKeepsTokenForFencing()
	{
		var token = table.Begin("lifter/1");
		table.OnReply(token, LockStatus.Granted, 2);
		table.OnReply(token, LockStatus.Lost, 0);
		Assert.That(table.StateOf("lifter/1"), Is.EqualTo(ClientLockTable.State.Lost));
		Assert.That(table.TokenFor("lifter/1"), Is.EqualTo(token));
		Assert.That(table.Begin("lifter/1"), Is.GreaterThan(token));
	}

	[Test]
	public void DropOwnAndClear()
	{
		table.Begin("lifter/1");
		table.OnState("lifter/2", 3);
		table.DropOwn();
		Assert.That(table.OwnCount, Is.EqualTo(0));
		Assert.That(table.BlockedBy("lifter/2"), Is.EqualTo(3));
		table.Clear();
		Assert.That(table.BlockedBy("lifter/2"), Is.EqualTo(0));
	}

	[Test]
	public void ServerAndClientAgreeUnderAutoRelease()
	{
		var server = new ResourceLockManager(() => now);
		var a = table.Begin("car/1/body/3");
		Assert.That(server.Acquire(2, a, new[] { "car/1/body/3" }).Granted);
		var b = table.Begin("car/1/body/4");
		Assert.That(server.Acquire(2, b, new[] { "car/1/body/4" }).Granted);
		Assert.That(server.HolderOf("car/1/body/3"), Is.EqualTo(0));
		Assert.That(table.StateOf("car/1/body/3"), Is.EqualTo(ClientLockTable.State.Released));
		Assert.That(server.CheckWrite(2, "car/1/body/3", table.TokenFor("car/1/body/3")), Is.EqualTo(WriteVerdict.Accept),
			"a late write of the released panel is accepted while nobody else touched it");
	}
}

[TestFixture]
public class LockWireTests
{
	private static Packet RoundTrip(Packet written) => new(written.Id, written.PayloadToArray());

	[Test]
	public void RequestReplyStateAndSnapshotRoundTrip()
	{
		using (var p = new Packet((int)PacketTypes.lockRequest))
		{
			LockWire.WriteRequest(p, 9, new[] { "car/1", "lifter/1" });
			var (token, keys) = LockWire.ReadRequest(RoundTrip(p));
			Assert.That(token, Is.EqualTo(9));
			Assert.That(keys, Is.EqualTo(new[] { "car/1", "lifter/1" }));
		}
		using (var p = new Packet((int)PacketTypes.lockReply))
		{
			LockWire.WriteReply(p, 9, LockStatus.Denied, 3);
			Assert.That(LockWire.ReadReply(RoundTrip(p)), Is.EqualTo((9, LockStatus.Denied, 3)));
		}
		using (var p = new Packet((int)PacketTypes.lockState))
		{
			LockWire.WriteState(p, "tool/TireChanger", 0);
			Assert.That(LockWire.ReadState(RoundTrip(p)), Is.EqualTo(("tool/TireChanger", 0)));
		}
		using (var p = new Packet((int)PacketTypes.lockSnapshot))
		{
			var list = new List<KeyValuePair<string, int>> { new("lifter/1", 2), new("car/2/body/1", 5) };
			LockWire.WriteSnapshot(p, list);
			Assert.That(LockWire.ReadSnapshot(RoundTrip(p)), Is.EqualTo(list));
		}
	}

	[Test]
	public void RejectsHostileCounts()
	{
		using var p = new Packet((int)PacketTypes.lockRequest);
		p.Write(1);
		p.Write(1000);
		Assert.Throws<CMS21Together.Shared.Net.NetFormatException>(() => LockWire.ReadRequest(RoundTrip(p)));
		using var s = new Packet((int)PacketTypes.lockSnapshot);
		s.Write(int.MaxValue);
		Assert.Throws<CMS21Together.Shared.Net.NetFormatException>(() => LockWire.ReadSnapshot(RoundTrip(s)));
		using var r = new Packet((int)PacketTypes.lockReply);
		r.Write(1);
		r.Write(77);
		r.Write(0);
		Assert.Throws<CMS21Together.Shared.Net.NetFormatException>(() => LockWire.ReadReply(RoundTrip(r)));
	}

	[Test]
	public void OptionalTokenDefaultsToZero()
	{
		using var p = new Packet((int)PacketTypes.partScript);
		p.Write(3);
		var read = RoundTrip(p);
		Assert.That(read.ReadInt(), Is.EqualTo(3));
		Assert.That(LockWire.ReadOptionalToken(read), Is.EqualTo(0));
	}
}
