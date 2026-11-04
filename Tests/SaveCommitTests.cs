using System.Collections.Generic;
using System.IO;
using System.Linq;
using CMS21Together.Shared.Persistence;
using NUnit.Framework;

namespace CMS21Together.Tests;

[TestFixture]
public class SaveCommitTests
{
	private string dir;
	private long now;
	private BundleStore backups;
	private SaveCommitter committer;
	private FakeSlot slot;
	private List<CommittedSave> committed;

	[SetUp]
	public void SetUp()
	{
		dir = TestSaves.NewDirectory();
		now = 0;
		backups = new BundleStore(Path.Combine(dir, "Backups"), 10);
		committer = new SaveCommitter(backups, () => now, inline: true);
		committed = new List<CommittedSave>();
		committer.Committed += committed.Add;
		slot = new FakeSlot(dir);
	}

	[TearDown]
	public void TearDown() => TestSaves.DeleteDirectory(dir);

	private void Advance(long ms, bool gameSaving = false, int stepMs = 50)
	{
		for (long t = 0; t < ms; t += stepMs)
		{
			now += stepMs;
			committer.Pump(gameSaving);
		}
	}

	/// <summary>A save as the game does it: the hook, then the game's own write while it reports "saving".</summary>
	private void GameSave(long profileGeneration)
	{
		committer.Begin(slot);
		Advance(200, gameSaving: true);
		slot.GameWritesProfile(profileGeneration);
		Advance(200, gameSaving: true);
		Advance(1000);
	}

	[Test]
	public void SidecarFirstThenTheProfileHashAndABackup()
	{
		slot.GameWritesProfile(0);
		Assert.That(committer.Begin(slot), Is.EqualTo(1));
		Assert.That(slot.OnDisk(), Is.EqualTo((1L, (string)null, "")), "step 1: generation 1, hash pending");

		Advance(200, gameSaving: true);
		slot.GameWritesProfile(1);
		Advance(200, gameSaving: true);
		Assert.That(committed, Is.Empty, "nothing is final while the game is still writing");
		Advance(1000);

		var profile = File.ReadAllBytes(slot.ProfilePath);
		Assert.That(slot.OnDisk().hash, Is.EqualTo(Sha256Hex.Of(profile)));
		Assert.That(committed.Select(c => c.Meta.Generation), Is.EqualTo(new long[] { 1 }));
		var bundle = backups.NewestValid(TestSaves.SaveId, out _, out _);
		Assert.That(bundle.Profile, Is.EqualTo(profile));
		Assert.That(bundle.Sidecar, Is.EqualTo(File.ReadAllBytes(slot.SidecarPath)));
		Assert.That(SlotValidator.Check(slot.Input(backups)).State, Is.EqualTo(SlotState.Ok));
	}

	[Test]
	public void GenerationsIncreaseAndBothHooksOfOneSaveShareOne()
	{
		committer.Begin(slot);
		Assert.That(committer.Begin(slot), Is.EqualTo(1), "GarageLoader.Save and ProfileManager.Save in one save");
		Advance(100, gameSaving: true);
		slot.GameWritesProfile(1);
		Advance(1000);
		GameSave(2);
		GameSave(3);
		Assert.That(committed.Select(c => c.Meta.Generation), Is.EqualTo(new long[] { 1, 2, 3 }));
		Assert.That(backups.List(TestSaves.SaveId).Select(b => b.Meta.Generation), Is.EqualTo(new long[] { 3, 2, 1 }));
	}

	[Test]
	public void WithoutASavingSignalTheFileIsUsedOnceItSettles()
	{
		slot.GameWritesProfile(0);
		committer.Begin(slot);
		Advance(SaveCommitter.SettleMs - 100);
		Assert.That(committed, Is.Empty);
		Advance(300);
		Assert.That(committed, Has.Count.EqualTo(1));
	}

	[Test]
	public void NextGenerationIsAboveTheNewestBackup()
	{
		backups.Put(TestSaves.Bundle(7));
		slot.Generation = 3;
		Assert.That(committer.Begin(slot), Is.EqualTo(8));
	}

	[Test]
	public void ACrashBetweenTheStepsLeavesAReadableUnverifiedSave()
	{
		GameSave(1);
		committer.Begin(slot);
		slot.GameWritesProfile(2);
		// The process dies here: the committer never pumps again.
		var check = SlotValidator.Check(slot.Input(backups));
		Assert.That(check.State, Is.EqualTo(SlotState.Unverified));
		Assert.That(check.CanHost, Is.True);
	}

	[Test]
	public void ATornProfileIsCorruptAndTheLastGenerationCanBeRestored()
	{
		GameSave(1);
		committer.Begin(slot);
		File.WriteAllBytes(slot.ProfilePath, TestSaves.Profile(2).Take(9).ToArray());

		var check = SlotValidator.Check(slot.Input(backups));
		Assert.That(check.State, Is.EqualTo(SlotState.Corrupt));
		Assert.That(check.Backup.Generation, Is.EqualTo(1));
		var bundle = backups.NewestValid(TestSaves.SaveId, out _, out _);
		SaveRestore.Apply(bundle, slot.ProfilePath, slot.SidecarPath, new BundleStore(Path.Combine(dir, "Safety"), 10), 2);
		Assert.That(SlotValidator.Check(slot.Input(backups)).State, Is.EqualTo(SlotState.Ok));
		Assert.That(TestSaves.ProfileGeneration(File.ReadAllBytes(slot.ProfilePath)), Is.EqualTo(1));
	}

	[Test]
	public void AnUnfinishedGenerationIsBackedUpBeforeTheNextSessionSoATornSaveNeverGoesBack()
	{
		// Regression (save kill test): generation 2 was written by the game, then the process died before step 2.
		GameSave(1);
		committer.Begin(slot);
		slot.GameWritesProfile(2);

		var check = SlotValidator.Check(slot.Input(backups));
		Assert.That(check.State, Is.EqualTo(SlotState.Unverified));
		Assert.That(SessionStart.Prepare(slot, check, backups, slot.Name), Is.EqualTo(2));
		Assert.That(slot.OnDisk().hash, Is.EqualTo(Sha256Hex.Of(TestSaves.Profile(2))));
		Assert.That(SlotValidator.Check(slot.Input(backups)).State, Is.EqualTo(SlotState.Ok));

		// The next session's first save dies half-way through the game's write.
		var next = new SaveCommitter(backups, () => now, inline: true);
		next.Begin(slot);
		File.WriteAllBytes(slot.ProfilePath, TestSaves.Profile(3).Take(7).ToArray());
		var torn = SlotValidator.Check(slot.Input(backups));
		Assert.That(torn.State, Is.EqualTo(SlotState.Corrupt));
		SaveRestore.Apply(backups.NewestValid(TestSaves.SaveId, out _, out _), slot.ProfilePath, slot.SidecarPath,
			new BundleStore(Path.Combine(dir, "Safety"), 10), slot.Generation);
		Assert.That(TestSaves.ProfileGeneration(File.ReadAllBytes(slot.ProfilePath)), Is.EqualTo(2), "not generation 1");
	}

	[Test]
	public void SessionStartWritesNothingWhenTheBackupExists()
	{
		GameSave(1);
		var writes = slot.SidecarWrites;
		var check = SlotValidator.Check(slot.Input(backups));
		Assert.That(check.State, Is.EqualTo(SlotState.Ok));
		Assert.That(SessionStart.Prepare(slot, check, backups, slot.Name), Is.EqualTo(1));
		Assert.That(slot.SidecarWrites, Is.EqualTo(writes));
		Assert.That(backups.List(TestSaves.SaveId), Has.Count.EqualTo(1));
	}

	[Test]
	public void SessionStartBacksUpAnOkSaveWhoseBackupWasNeverWritten()
	{
		GameSave(1);
		Directory.Delete(backups.Root, true);
		var check = SlotValidator.Check(slot.Input(backups));
		SessionStart.Prepare(slot, check, backups, slot.Name);
		var bundle = backups.NewestValid(TestSaves.SaveId, out var info, out _);
		Assert.That(bundle.Profile, Is.EqualTo(File.ReadAllBytes(slot.ProfilePath)));
		Assert.That(info.Meta.Note, Is.EqualTo("before session"));
	}

	[Test]
	public void SessionStartLeavesDamagedFilesAlone()
	{
		GameSave(1);
		File.WriteAllBytes(slot.ProfilePath, new byte[] { 9, 9 });
		var check = SlotValidator.Check(slot.Input(backups));
		Assert.That(check.State, Is.EqualTo(SlotState.Corrupt));
		Assert.That(SessionStart.Prepare(slot, check, backups, slot.Name), Is.Zero);
		Assert.That(backups.List(TestSaves.SaveId), Has.Count.EqualTo(1));
	}

	[Test]
	public void AtomicWriteKeepsTheTargetAndCopiesThePreviousVersion()
	{
		var path = Path.Combine(dir, "file.json");
		CMS21Together.Shared.AtomicFile.WriteAllText(path, "one");
		CMS21Together.Shared.AtomicFile.WriteAllBytes(path, System.Text.Encoding.UTF8.GetBytes("two"), path + ".bak");
		Assert.That(File.ReadAllText(path), Is.EqualTo("two"));
		Assert.That(File.ReadAllText(path + ".bak"), Is.EqualTo("one"));
		Assert.That(File.Exists(path + ".tmp"), Is.False);
		CMS21Together.Shared.AtomicFile.WriteAllBytes(path, System.Text.Encoding.UTF8.GetBytes("three"), path + ".bak");
		Assert.That(File.ReadAllText(path), Is.EqualTo("three"));
		Assert.That(File.ReadAllText(path + ".bak"), Is.EqualTo("two"));
	}

	[Test]
	public void AMissingProfileKeepsTheGenerationPending()
	{
		committer.Begin(slot);
		Advance(SaveCommitter.SettleMs + 1000);
		Assert.That(committed, Is.Empty);
		Assert.That(slot.OnDisk().hash, Is.Null);
		Assert.That(committer.Idle, Is.True);
	}

	[Test]
	public void AGameSaveThatNeverEndsIsAbandoned()
	{
		slot.GameWritesProfile(0);
		committer.Begin(slot);
		Advance(SaveCommitter.MaxWaitMs + 1000, gameSaving: true, stepMs: 500);
		Assert.That(committer.Idle, Is.True);
		Advance(5000);
		Assert.That(committed, Is.Empty);
	}

	[Test]
	public void BackgroundWritesFinishAndAreReported()
	{
		var background = new SaveCommitter(backups, () => now);
		var done = new List<CommittedSave>();
		background.Committed += done.Add;
		slot.GameWritesProfile(0);
		background.Begin(slot);
		for (var i = 0; i < 80; i++)
		{
			now += 50;
			background.Pump(false);
		}
		background.WaitForWrites();
		Assert.That(done.Select(c => c.Meta.Generation), Is.EqualTo(new long[] { 1 }));
		Assert.That(File.Exists(done[0].Path), Is.True);
	}

	[Test]
	public void SkillUnlocksDoNotWriteAndTheNextAutosaveKeepsAllOfThem()
	{
		// Audit A18: the exit test of Phase 4.
		var scheduler = new SaveScheduler(() => now);
		slot.GameWritesProfile(0);
		for (var skill = 1; skill <= 10; skill++)
		{
			slot.Progress += $"skill{skill};";
			scheduler.MarkDirty();
			now += 1000;
			Assert.That(scheduler.Poll(true), Is.EqualTo(SaveAction.None));
		}
		Assert.That(slot.SidecarWrites, Is.Zero);
		Assert.That(scheduler.Dirty, Is.True);

		now = SaveScheduler.DefaultIntervalMs;
		Assert.That(scheduler.Poll(true), Is.EqualTo(SaveAction.Full));
		committer.Begin(slot);
		scheduler.FullSaveDone();
		Advance(SaveCommitter.SettleMs + 1000);
		var (_, _, progress) = FakeSlot.ReadSidecar(backups.NewestValid(TestSaves.SaveId, out _, out _).Sidecar);
		Assert.That(progress.Split(new[] { ';' }, System.StringSplitOptions.RemoveEmptyEntries), Has.Length.EqualTo(10));
		Assert.That(scheduler.Dirty, Is.False);
	}

	[Test]
	public void SchedulerSavesShortlyAfterAPlayerLeaves()
	{
		var scheduler = new SaveScheduler(() => now);
		now = 60_000;
		scheduler.PlayerLeft();
		scheduler.PlayerLeft();
		Assert.That(scheduler.Poll(true), Is.EqualTo(SaveAction.None), "debounced");
		now += SaveScheduler.LeaveDebounceMs;
		Assert.That(scheduler.Poll(true), Is.EqualTo(SaveAction.Full));
		scheduler.FullSaveDone();
		now += 1000;
		Assert.That(scheduler.Poll(true), Is.EqualTo(SaveAction.None), "one save for both leaves");
	}

	[Test]
	public void ALeaveRightAfterASaveOnlyWritesTheSidecar()
	{
		var scheduler = new SaveScheduler(() => now);
		now = 10_000;
		scheduler.PlayerLeft();
		now += SaveScheduler.LeaveDebounceMs;
		Assert.That(scheduler.Poll(true), Is.EqualTo(SaveAction.SidecarOnly));
		now += 1000;
		Assert.That(scheduler.Poll(true), Is.EqualTo(SaveAction.None));
	}

	[Test]
	public void ALeaveAwayFromTheGarageSavesTheSidecarNowAndTheRestLater()
	{
		var scheduler = new SaveScheduler(() => now);
		now = 60_000;
		scheduler.PlayerLeft();
		now += SaveScheduler.LeaveDebounceMs;
		Assert.That(scheduler.Poll(false), Is.EqualTo(SaveAction.SidecarOnly));
		now += 1000;
		Assert.That(scheduler.Poll(false), Is.EqualTo(SaveAction.None));
		Assert.That(scheduler.Poll(true), Is.EqualTo(SaveAction.Full), "back in the garage");
	}

	[Test]
	public void AFullSaveTheGameRefusedIsRetriedButNotEveryFrame()
	{
		var scheduler = new SaveScheduler(() => now);
		now = SaveScheduler.DefaultIntervalMs;
		Assert.That(scheduler.Poll(true), Is.EqualTo(SaveAction.Full));
		now += 16;
		Assert.That(scheduler.Poll(true), Is.EqualTo(SaveAction.None));
		now += SaveScheduler.RetryMs;
		Assert.That(scheduler.Poll(true), Is.EqualTo(SaveAction.Full));
	}

	[Test]
	public void AutosaveWaitsForTheGarageAndCanBeTurnedOff()
	{
		var scheduler = new SaveScheduler(() => now);
		now = SaveScheduler.DefaultIntervalMs * 2;
		Assert.That(scheduler.Poll(false), Is.EqualTo(SaveAction.None));
		Assert.That(scheduler.Poll(true), Is.EqualTo(SaveAction.Full));

		var off = new SaveScheduler(() => now, 0);
		now += SaveScheduler.DefaultIntervalMs * 10;
		Assert.That(off.Poll(true), Is.EqualTo(SaveAction.None));
	}

	[Test]
	public void AfterARestoreNewGenerationsNeverReuseOldNumbers()
	{
		var kept = new BundleStore(Path.Combine(dir, "Backups", "_kept"), 20);
		var restoring = new SaveCommitter(backups, () => now, inline: true, kept: kept);
		committer = restoring;
		for (var g = 1; g <= 5; g++) GameSave(g);
		Assert.That(backups.NewestGeneration(TestSaves.SaveId), Is.EqualTo(5));

		// Restore generation 3: 4 and 5 move to "kept", and the slot is back at 3.
		backups.MoveNewerThan(TestSaves.SaveId, 3, kept);
		slot.Generation = 3;
		Assert.That(restoring.Begin(slot), Is.EqualTo(6), "4 and 5 already named other saves");
	}

	[Test]
	public void AStoreHoldsABundleOnlyWithTheSameBytes()
	{
		GameSave(1);
		backups.NewestValid(TestSaves.SaveId, out _, out var bytes);
		var hash = Sha256Hex.Of(bytes);
		Assert.That(backups.Holds(TestSaves.SaveId, 1, bytes.Length, hash), Is.True);
		Assert.That(backups.Holds(TestSaves.SaveId, 1, bytes.Length, new string('0', 64)), Is.False, "same generation, other contents");
		Assert.That(backups.Holds(TestSaves.SaveId, 2, bytes.Length, hash), Is.False);
		Assert.That(backups.Holds(TestSaves.SaveId, 1, bytes.Length, null), Is.False);
	}}
