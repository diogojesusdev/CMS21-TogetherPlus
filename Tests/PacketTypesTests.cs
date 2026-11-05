using System;
using System.Linq;
using CMS21Together.Shared;
using NUnit.Framework;

namespace CMS21Together.Tests;

/// <summary>
/// The wire type of a packet is the enum's ordinal, and the enum is shared by every build. Two players can only
/// connect on the same build (the handshake refuses mixed versions), but a reordered enum still breaks an old
/// build's packets in ways that look like random desyncs. Pinning every number turns that into a test failure.
/// New types go at the END of the enum and at the end of the table; never renumber an existing one.
/// </summary>
[TestFixture]
public class PacketTypesTests
{
	private static readonly (PacketTypes Type, int Id)[] Pinned =
	{
		new(PacketTypes.connect, 0),
		new(PacketTypes.playerLeft, 1),
		new(PacketTypes.identityRequest, 2),
		new(PacketTypes.userData, 3),
		new(PacketTypes.readyState, 4),
		new(PacketTypes.start, 5),
		new(PacketTypes.spawn, 6),
		new(PacketTypes.position, 7),
		new(PacketTypes.rotation, 8),
		new(PacketTypes.item, 9),
		new(PacketTypes.groupItem, 10),
		new(PacketTypes.lifter, 11),
		new(PacketTypes.loadJobCar, 12),
		new(PacketTypes.loadCar, 13),
		new(PacketTypes.bodyPart, 14),
		new(PacketTypes.partScript, 15),
		new(PacketTypes.deleteCar, 16),
		new(PacketTypes.carPosition, 17),
		new(PacketTypes.garageUpgrade, 18),
		new(PacketTypes.newJob, 19),
		new(PacketTypes.jobAction, 20),
		new(PacketTypes.selectedJob, 21),
		new(PacketTypes.sceneChange, 22),
		new(PacketTypes.contentInfo, 23),
		new(PacketTypes.toolMove, 24),
		new(PacketTypes.setSpringClamp, 25),
		new(PacketTypes.clearSpringClamp, 26),
		new(PacketTypes.setTireChanger, 27),
		new(PacketTypes.clearTireChanger, 28),
		new(PacketTypes.wheelBalance, 29),
		new(PacketTypes.endJob, 30),
		new(PacketTypes.oilBinUse, 31),
		new(PacketTypes.engineCrane, 32),
		new(PacketTypes.skillChange, 33),
		new(PacketTypes.engineStandAngle, 34),
		new(PacketTypes.engineStandSetGroup, 35),
		new(PacketTypes.engineStandTakeOff, 36),
		new(PacketTypes.carFluid, 37),
		new(PacketTypes.exp, 38),
		new(PacketTypes.point, 39),
		new(PacketTypes.resync, 40),
		new(PacketTypes.carWash, 41),
		new(PacketTypes.carPaint, 42),
		new(PacketTypes.useWelder, 43),
		new(PacketTypes.repairPart, 44),
		new(PacketTypes.parkAdd, 45),
		new(PacketTypes.parkRemove, 46),
		new(PacketTypes.playerInCar, 47),
		new(PacketTypes.carEngineSound, 48),
		new(PacketTypes.lockRequest, 49),
		new(PacketTypes.lockRenew, 50),
		new(PacketTypes.lockRelease, 51),
		new(PacketTypes.lockReply, 52),
		new(PacketTypes.lockState, 53),
		new(PacketTypes.lockSnapshot, 54),
		new(PacketTypes.econTxn, 55),
		new(PacketTypes.econBalance, 56),
		new(PacketTypes.replicaBegin, 57),
		new(PacketTypes.replicaChunk, 58),
		new(PacketTypes.replicaAck, 59),
		new(PacketTypes.replicaCancel, 60),
		new(PacketTypes.replicaOffer, 61),
		new(PacketTypes.replicaRequest, 62),
		new(PacketTypes.invPush, 63),
		new(PacketTypes.invOp, 64),
		new(PacketTypes.invSnapshot, 65),
		new(PacketTypes.invRequest, 66),
		new(PacketTypes.invCheck, 67),
		new(PacketTypes.purchaseAsk, 68),
		new(PacketTypes.purchaseAnswer, 69),
		new(PacketTypes.carCheck, 70),
		new(PacketTypes.statsFix, 71),
		new(PacketTypes.features, 72),
		new(PacketTypes.feature, 73),
		new(PacketTypes.bulkSell, 74),
		new(PacketTypes.uidRange, 75),
	};

	[Test]
	public void EveryValueKeepsItsWireNumber()
	{
		foreach (var (type, id) in Pinned)
			Assert.That((int)type, Is.EqualTo(id), $"{type} was renumbered: an old build would route its packets to another handler");
	}

	[Test]
	public void TheTableCoversEveryValue()
	{
		var values = Enum.GetValues(typeof(PacketTypes)).Cast<PacketTypes>().ToArray();
		Assert.That(Pinned.Select(p => p.Type), Is.EquivalentTo(values),
			"a packet type was added or removed without updating the pinned table: append it with the next number");
		Assert.That(Pinned.Length, Is.EqualTo(values.Length));
	}

	[Test]
	public void TheTableIsDenseFromZero()
	{
		var ids = Pinned.Select(p => p.Id).OrderBy(id => id).ToArray();
		Assert.That(ids, Is.EqualTo(Enumerable.Range(0, ids.Length)),
			"the ids are consecutive today; if a gap is deliberate, delete this test instead of shifting one");
	}

	[Test]
	public void EveryNumberHasExactlyOneName()
	{
		foreach (var (type, id) in Pinned)
			Assert.That(Enum.GetName(typeof(PacketTypes), id), Is.EqualTo(type.ToString()));
	}
}
