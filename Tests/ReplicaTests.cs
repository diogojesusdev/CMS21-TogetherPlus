using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CMS21Together.Shared;
using CMS21Together.Shared.Net;
using CMS21Together.Shared.Persistence;
using NUnit.Framework;

namespace CMS21Together.Tests;

/// <summary>Client save replicas (plan §3.5) over the real net layer, on simulated lossy, laggy links.</summary>
[TestFixture]
public class ReplicaTests
{
	private const string Version = "test-replicas";
	private static readonly SimOptions Lossy = new() { LatencyMs = 80, JitterMs = 40, LossRate = 0.05, Seed = 3 };

	private string dir;
	private long now;
	private NetHost host;
	private NetClient client;
	private ReplicaHub hostHub;
	private ReplicaHub clientHub;
	private int clientSlot;
	private readonly List<(int peer, SaveBundle bundle, byte[] bytes)> hostReceived = new();
	private readonly List<(SaveBundle bundle, byte[] bytes)> clientReceived = new();
	private readonly List<(int peer, long generation, ReplicaStatus status)> hostSends = new();
	private int chunksAtClient;
	private Func<byte[], byte[]> tamperHostFrames;
	private Action onTick;

	public bool ClientLoading;
	public bool HostLoading;

	[SetUp]
	public void SetUp()
	{
		dir = TestSaves.NewDirectory();
		now = 0;
		hostReceived.Clear();
		clientReceived.Clear();
		hostSends.Clear();
		chunksAtClient = 0;
		tamperHostFrames = null;
		onTick = null;
		ClientLoading = HostLoading = false;
	}

	[TearDown]
	public void TearDown() => TestSaves.DeleteDirectory(dir);

	private void Connect(SimOptions options)
	{
		host = new NetHost(4, Version, () => now);
		client = new NetClient(Version, () => now);
		var (clientEnd, hostEnd) = SimLink.CreatePair(options, () => now);
		client.Connect(clientEnd);
		host.Accept(hostEnd);

		hostHub = new ReplicaHub(() => now, (slot, packet, bulk) =>
		{
			var frame = new byte[packet.FrameLength];
			Buffer.BlockCopy(packet.FrameBuffer, 0, frame, 0, frame.Length);
			if (tamperHostFrames != null) frame = tamperHostFrames(frame);
			host.Send(slot, frame, frame.Length, bulk ? SendPriority.Bulk : SendPriority.Normal);
		});
		clientHub = new ReplicaHub(() => now, (_, packet, bulk) =>
			client.Send(packet.FrameBuffer, packet.FrameLength, bulk ? SendPriority.Bulk : SendPriority.Normal));
		hostHub.Received += (peer, bundle, bytes) => hostReceived.Add((peer, bundle, bytes));
		hostHub.SendFinished += (peer, generation, status) => hostSends.Add((peer, generation, status));
		clientHub.Received += (_, bundle, bytes) => clientReceived.Add((bundle, bytes));
		host.FrameReceived += (connection, frame) => Dispatch(hostHub, connection.SlotId, frame);
		client.FrameReceived += frame =>
		{
			if (frame.Type == (int)PacketTypes.replicaChunk) chunksAtClient++;
			Dispatch(clientHub, 0, frame);
		};

		Run(2000, () => client.State == NetClientState.Connected);
		Assert.That(client.State, Is.EqualTo(NetClientState.Connected));
		clientSlot = client.SlotId;
	}

	private static void Dispatch(ReplicaHub hub, int peer, NetFrame frame)
	{
		using var packet = new Packet(frame);
		switch ((PacketTypes)frame.Type)
		{
			case PacketTypes.replicaBegin: hub.HandleBegin(peer, packet); break;
			case PacketTypes.replicaChunk: hub.HandleChunk(peer, packet); break;
			case PacketTypes.replicaAck: hub.HandleAck(peer, packet); break;
			case PacketTypes.replicaCancel: hub.HandleCancel(peer, packet); break;
		}
	}

	/// <summary>Runs the simulation in 10 ms steps until <paramref name="until"/> holds or the time runs out.</summary>
	private long Run(long maxMs, Func<bool> until = null)
	{
		var start = now;
		while (now - start < maxMs)
		{
			now += 10;
			host.Tick();
			client.Tick();
			hostHub.Pump(HostLoading, HostLoading);
			clientHub.Pump(ClientLoading, ClientLoading);
			onTick?.Invoke();
			if (until != null && until()) break;
		}
		return now - start;
	}

	private static byte[] BigBundle(long generation, int size = 300_000) => TestSaves.Bundle(generation, profileSize: size, seed: 11);

	[Test]
	public void ASaveReachesAClientOverALossyLaggyLinkAndVerifies()
	{
		Connect(Lossy);
		clientHub.Accept = (_, _) => ReplicaStatus.Continue;
		var bundle = BigBundle(4);

		hostHub.Send(clientSlot, bundle);
		var took = Run(60_000, () => clientReceived.Count > 0 && hostSends.Count > 0);

		Assert.That(clientReceived, Has.Count.EqualTo(1));
		Assert.That(clientReceived[0].bytes, Is.EqualTo(bundle));
		Assert.That(clientReceived[0].bundle.Meta.Generation, Is.EqualTo(4));
		Assert.That(hostSends, Is.EqualTo(new[] { (clientSlot, 4L, ReplicaStatus.Done) }));
		Assert.That(took, Is.GreaterThanOrEqualTo(bundle.Length * 1000L / ReplicaHub.DefaultBytesPerSecond - 500), "rate limited");
		Assert.That(took, Is.LessThan(30_000));
	}

	[Test]
	public void NothingIsSentWhileTheClientLoadsAScene()
	{
		Connect(Lossy);
		clientHub.Accept = (_, _) => ReplicaStatus.Continue;
		ClientLoading = true;
		hostHub.Send(clientSlot, BigBundle(2));
		Run(30_000);
		Assert.That(chunksAtClient, Is.Zero, "no chunk before the client accepted, and it accepts nothing while loading");
		Assert.That(hostHub.IsSending(clientSlot), Is.True, "waiting, not abandoned");

		ClientLoading = false;
		Run(60_000, () => clientReceived.Count > 0);
		Assert.That(clientReceived.Select(r => r.bundle.Meta.Generation), Is.EqualTo(new long[] { 2 }));
	}

	[Test]
	public void ALoadMidTransferPausesItWithinTheWindow()
	{
		Connect(Lossy);
		clientHub.Accept = (_, _) => ReplicaStatus.Continue;
		hostHub.Send(clientSlot, BigBundle(2, 600_000));
		Run(60_000, () => chunksAtClient >= 8);
		ClientLoading = true;
		Run(1000);
		var atPause = chunksAtClient;
		Run(20_000);
		Assert.That(chunksAtClient - atPause, Is.LessThanOrEqualTo(ReplicaHub.Window), "only what was already in flight");
		Assert.That(clientReceived, Is.Empty);
		ClientLoading = false;
		Run(60_000, () => clientReceived.Count > 0);
		Assert.That(clientReceived, Has.Count.EqualTo(1));
	}

	[Test]
	public void TheHostSendsNothingWhileItLoads()
	{
		Connect(Lossy);
		clientHub.Accept = (_, _) => ReplicaStatus.Continue;
		HostLoading = true;
		hostHub.Send(clientSlot, BigBundle(3));
		Run(10_000);
		Assert.That(chunksAtClient, Is.Zero);
		HostLoading = false;
		Run(60_000, () => clientReceived.Count > 0);
		Assert.That(clientReceived, Has.Count.EqualTo(1));
	}

	[Test]
	public void AChunkModifiedInTransitIsRejected()
	{
		Connect(Lossy);
		clientHub.Accept = (_, _) => ReplicaStatus.Continue;
		var tampered = false;
		tamperHostFrames = frame =>
		{
			if (tampered || BitConverter.ToUInt16(frame, FrameCodec.TypeOffset) != (ushort)PacketTypes.replicaChunk) return frame;
			tampered = true;
			frame[frame.Length - 1] ^= 0xFF;
			return frame;
		};
		hostHub.Send(clientSlot, BigBundle(5));
		Run(60_000, () => hostSends.Count > 0);

		Assert.That(tampered, Is.True);
		Assert.That(clientReceived, Is.Empty);
		Assert.That(hostSends, Is.EqualTo(new[] { (clientSlot, 5L, ReplicaStatus.Failed) }));
	}

	[Test]
	public void ATamperedBundleFailsItsHashEvenWhenTheTransferHashMatches()
	{
		Connect(SimOptions.Perfect);
		clientHub.Accept = (_, _) => ReplicaStatus.Continue;
		var bundle = BigBundle(5, 20_000);
		bundle[bundle.Length / 2] ^= 1;
		hostHub.Send(clientSlot, bundle);
		Run(30_000, () => hostSends.Count > 0);
		Assert.That(clientReceived, Is.Empty);
		Assert.That(hostSends.Single().status, Is.EqualTo(ReplicaStatus.Failed));
	}

	[Test]
	public void ANewerSaveReplacesOneStillInFlight()
	{
		Connect(Lossy);
		clientHub.Accept = (_, _) => ReplicaStatus.Continue;
		hostHub.Send(clientSlot, BigBundle(1, 500_000));
		Run(60_000, () => chunksAtClient >= 5);
		hostHub.Send(clientSlot, BigBundle(2, 100_000));
		Run(60_000, () => clientReceived.Count > 0 && !hostHub.IsSending(clientSlot));
		Run(2000);
		Assert.That(clientReceived.Select(r => r.bundle.Meta.Generation), Is.EqualTo(new long[] { 2 }));
		Assert.That(clientHub.IsReceiving(0), Is.False);
	}

	[Test]
	public void AClientThatAlreadyHasTheSaveSkipsIt()
	{
		Connect(Lossy);
		clientHub.Accept = (_, _) => ReplicaStatus.AlreadyHave;
		hostHub.Send(clientSlot, BigBundle(1));
		Run(10_000, () => hostSends.Count > 0);
		Assert.That(hostSends.Single().status, Is.EqualTo(ReplicaStatus.AlreadyHave));
		Assert.That(chunksAtClient, Is.Zero);
	}

	[Test]
	public void UploadsTheHostDidNotAskForAreRefused()
	{
		Connect(Lossy);
		hostHub.Accept = (_, _) => ReplicaStatus.Refused;
		var finished = new List<ReplicaStatus>();
		clientHub.SendFinished += (_, _, status) => finished.Add(status);
		clientHub.Send(0, BigBundle(9));
		Run(10_000, () => finished.Count > 0);
		Assert.That(finished, Is.EqualTo(new[] { ReplicaStatus.Refused }));
		Assert.That(hostReceived, Is.Empty);
	}

	[Test]
	public void RestoreFromAClientReplicaAfterTheHostLostItsSave()
	{
		// Phase 4 exit test: the host's save is deleted, then restored from a client's copy (byte-identical).
		Connect(Lossy);
		var hostDir = Path.Combine(dir, "host");
		var backups = new BundleStore(Path.Combine(hostDir, "Backups"), 10);
		var safety = new BundleStore(Path.Combine(hostDir, "Safety"), 10);
		var replicas = new BundleStore(Path.Combine(dir, "client", "Replicas"), 3, maxSaves: 20);
		var slot = new FakeSlot(hostDir);
		var committer = new SaveCommitter(backups, () => now, inline: true);
		committer.Committed += save => hostHub.Send(clientSlot, save.Bundle);
		onTick = () => committer.Pump(false);
		clientHub.Accept = (_, begin) => replicas.List(begin.SaveId).Any(b => b.Meta.Generation == begin.Generation)
			? ReplicaStatus.AlreadyHave
			: ReplicaStatus.Continue;
		clientHub.Received += (_, _, bytes) => replicas.Put(bytes);

		for (var generation = 1; generation <= 4; generation++)
		{
			slot.Progress = $"xp={generation * 100}";
			committer.Begin(slot);
			slot.GameWritesProfile(generation, "cars=" + generation);
			Run(60_000, () => committer.Idle && !hostHub.IsSending(clientSlot) && replicas.NewestGeneration(TestSaves.SaveId) == generation);
		}
		Assert.That(replicas.List(TestSaves.SaveId).Select(b => b.Meta.Generation), Is.EqualTo(new long[] { 4, 3, 2 }), "clients keep 3");
		var expectedProfile = File.ReadAllBytes(slot.ProfilePath);
		var expectedSidecar = File.ReadAllBytes(slot.SidecarPath);

		// The host loses its save and its backups.
		File.Delete(slot.ProfilePath);
		Directory.Delete(backups.Root, true);
		var check = SlotValidator.Check(slot.Input(backups));
		Assert.That(check.State, Is.EqualTo(SlotState.Missing));

		// The client joins and offers its newest valid copy; the host is asked and accepts.
		var replica = replicas.NewestValid(TestSaves.SaveId, out _, out var replicaBytes);
		var offer = new ReplicaOffer { SaveId = TestSaves.SaveId, Generation = replica.Meta.Generation, BundleSha256 = Sha256Hex.Of(replicaBytes) };
		Assert.That(RecoveryRules.Evaluate(TestSaves.SaveId, slot.Generation, !check.CanHost, offer, false), Is.EqualTo(RecoveryDecision.Prompt));
		hostHub.Accept = (peer, begin) => peer == clientSlot && begin.SaveId == offer.SaveId && begin.Generation == offer.Generation &&
		                                  begin.BundleSha256 == offer.BundleSha256
			? ReplicaStatus.Continue
			: ReplicaStatus.Refused;
		clientHub.Send(0, replicaBytes);
		Run(60_000, () => hostReceived.Count > 0);
		Assert.That(hostReceived, Has.Count.EqualTo(1));

		SaveRestore.Apply(hostReceived[0].bundle, slot.ProfilePath, slot.SidecarPath, safety, slot.Generation);
		Assert.That(File.ReadAllBytes(slot.ProfilePath), Is.EqualTo(expectedProfile));
		Assert.That(File.ReadAllBytes(slot.SidecarPath), Is.EqualTo(expectedSidecar));
		Assert.That(safety.List(TestSaves.SaveId).Single().Meta.Note, Is.EqualTo("before restore"));
		Assert.That(SlotValidator.Check(slot.Input(backups)).State, Is.EqualTo(SlotState.Ok));
	}

	[Test]
	public void ACorruptHostSaveIsRestoredAndATamperedReplicaIsNeverOffered()
	{
		var replicas = new BundleStore(Path.Combine(dir, "Replicas"), 3);
		replicas.Put(TestSaves.Bundle(3));
		var newest = replicas.Put(TestSaves.Bundle(4));
		var bytes = File.ReadAllBytes(newest);
		bytes[bytes.Length - 40] ^= 0x10;
		File.WriteAllBytes(newest, bytes);

		var valid = replicas.NewestValid(TestSaves.SaveId, out _, out _);
		Assert.That(valid.Meta.Generation, Is.EqualTo(3), "the tampered generation 4 fails its hash");

		var hostDir = Path.Combine(dir, "host");
		var slot = new FakeSlot(hostDir);
		slot.WriteSidecar(4, Sha256Hex.Of(TestSaves.Profile(4)));
		File.WriteAllBytes(slot.ProfilePath, new byte[] { 1, 2, 3 });
		var check = SlotValidator.Check(slot.Input(null));
		Assert.That(check.State, Is.EqualTo(SlotState.Corrupt));
		var offer = new ReplicaOffer { SaveId = TestSaves.SaveId, Generation = 3, BundleSha256 = new string('0', 64) };
		Assert.That(RecoveryRules.Evaluate(TestSaves.SaveId, 4, !check.CanHost, offer, false), Is.EqualTo(RecoveryDecision.Prompt));
	}
}