using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CMS21Together.Shared.Economy;
using CMS21Together.Shared.Persistence;
using NUnit.Framework;

namespace CMS21Together.Tests;

[TestFixture]
public class PersistenceTests
{
	private string dir;

	[SetUp]
	public void SetUp() => dir = TestSaves.NewDirectory();

	[TearDown]
	public void TearDown() => TestSaves.DeleteDirectory(dir);

	[Test]
	public void BundleRoundTripsBothFilesAndTheirHashes()
	{
		var profile = TestSaves.RandomBytes(200_000, 1);
		var sidecar = Encoding.UTF8.GetBytes("{\"saveGeneration\":4}");
		var meta = TestSaves.Meta(4);
		var bytes = SaveBundle.Encode(meta, profile, sidecar);

		Assert.That(SaveBundle.TryDecode(bytes, out var bundle, out var error), Is.True, error);
		Assert.That(bundle.Profile, Is.EqualTo(profile));
		Assert.That(bundle.Sidecar, Is.EqualTo(sidecar));
		Assert.That(bundle.Meta.Generation, Is.EqualTo(4));
		Assert.That(bundle.Meta.SaveId, Is.EqualTo(TestSaves.SaveId));
		Assert.That(bundle.Meta.ProfileSha256, Is.EqualTo(Sha256Hex.Of(profile)));
		Assert.That(bundle.Meta.SidecarSha256, Is.EqualTo(Sha256Hex.Of(sidecar)));
		Assert.That(bundle.Meta.CreatedUtc, Is.EqualTo(meta.CreatedUtc));
	}

	[Test]
	public void EveryModifiedByteIsRejected()
	{
		var bytes = TestSaves.Bundle(2, profileSize: 120);
		for (var i = 0; i < bytes.Length; i++)
		{
			var copy = (byte[])bytes.Clone();
			copy[i] ^= 0x5A;
			Assert.That(SaveBundle.TryDecode(copy, out _, out _), Is.False, $"byte {i} of {bytes.Length}");
		}
	}

	[Test]
	public void TruncatedOrExtendedBundlesAreRejected()
	{
		var bytes = TestSaves.Bundle(2, profileSize: 500);
		for (var length = 0; length < bytes.Length; length += 7)
			Assert.That(SaveBundle.TryDecode(bytes.Take(length).ToArray(), out _, out _), Is.False, $"length {length}");
		Assert.That(SaveBundle.TryDecode(bytes.Concat(new byte[] { 0 }).ToArray(), out _, out _), Is.False);
	}

	[Test]
	public void HeaderCanBeReadFromTheFirstBytes()
	{
		var bytes = TestSaves.Bundle(9, profileSize: 50_000);
		var prefix = bytes.Take(SaveBundle.HeaderPrefixBytes).ToArray();
		Assert.That(SaveBundle.TryReadHeader(prefix, prefix.Length, out var meta, out var error), Is.True, error);
		Assert.That(meta.Generation, Is.EqualTo(9));
		Assert.That(meta.Name, Is.EqualTo("Test save"));
	}

	[TestCase(null)]
	[TestCase("")]
	[TestCase("..\\..\\Windows")]
	[TestCase("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
	[TestCase("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/")]
	public void SaveIdsThatAreNotPlainHexAreRefused(string saveId)
	{
		Assert.That(SaveIds.IsValid(saveId), Is.False);
		Assert.Throws<ArgumentException>(() => SaveBundle.Encode(TestSaves.Meta(1, saveId ?? ""), new byte[1], new byte[1]));
		Assert.Throws<ArgumentException>(() => new BundleStore(dir, 3).FolderOf(saveId));
	}

	[Test]
	public void StoreKeepsTheNewestGenerations()
	{
		var store = new BundleStore(dir, 3);
		for (var generation = 1; generation <= 6; generation++) store.Put(TestSaves.Bundle(generation));
		Assert.That(store.List(TestSaves.SaveId).Select(b => b.Meta.Generation), Is.EqualTo(new long[] { 6, 5, 4 }));
		Assert.That(store.NewestGeneration(TestSaves.SaveId), Is.EqualTo(6));
		Assert.That(Directory.GetFiles(store.FolderOf(TestSaves.SaveId)).Length, Is.EqualTo(3));
	}

	[Test]
	public void StoringTheSameBundleTwiceKeepsOneFile()
	{
		var store = new BundleStore(dir, 3);
		var bytes = TestSaves.Bundle(1);
		var first = store.Put(bytes);
		var second = store.Put(bytes);
		Assert.That(second, Is.EqualTo(first));
		Assert.That(store.List(TestSaves.SaveId), Has.Count.EqualTo(1));
	}

	[Test]
	public void AnOldGenerationJustStoredIsNotPrunedAtOnce()
	{
		var store = new BundleStore(dir, 2);
		store.Put(TestSaves.Bundle(5));
		store.Put(TestSaves.Bundle(6));
		store.Put(TestSaves.Bundle(1));
		Assert.That(store.List(TestSaves.SaveId).Select(b => b.Meta.Generation), Is.EqualTo(new long[] { 6, 5, 1 }));
	}

	[Test]
	public void NewestValidSkipsADamagedBackup()
	{
		var store = new BundleStore(dir, 5);
		store.Put(TestSaves.Bundle(1));
		var newest = store.Put(TestSaves.Bundle(2));
		var bytes = File.ReadAllBytes(newest);
		bytes[bytes.Length / 2] ^= 1;
		File.WriteAllBytes(newest, bytes);

		var bundle = store.NewestValid(TestSaves.SaveId, out var info, out var raw);
		Assert.That(bundle.Meta.Generation, Is.EqualTo(1));
		Assert.That(info.Meta.Generation, Is.EqualTo(1));
		Assert.That(SaveBundle.TryDecode(raw, out _, out _), Is.True);
	}

	[Test]
	public void BundlesNewerThanARestoredGenerationAreMovedAsideNotDeleted()
	{
		var main = new BundleStore(Path.Combine(dir, "Backups"), 10);
		var safety = new BundleStore(Path.Combine(dir, "Safety"), 10);
		for (var generation = 1; generation <= 4; generation++) main.Put(TestSaves.Bundle(generation));

		Assert.That(main.MoveNewerThan(TestSaves.SaveId, 2, safety), Is.EqualTo(2));
		Assert.That(main.List(TestSaves.SaveId).Select(b => b.Meta.Generation), Is.EqualTo(new long[] { 2, 1 }));
		Assert.That(safety.List(TestSaves.SaveId).Select(b => b.Meta.Generation), Is.EqualTo(new long[] { 4, 3 }));
	}

	[Test]
	public void ReplicaStoreDropsTheLeastRecentlyWrittenSaves()
	{
		var store = new BundleStore(dir, 3, maxSaves: 2);
		var ids = new[] { new string('1', 32), new string('2', 32), new string('3', 32) };
		store.Put(TestSaves.Bundle(1, ids[0]));
		store.Put(TestSaves.Bundle(1, ids[1]));
		foreach (var file in Directory.GetFiles(store.FolderOf(ids[0]))) File.SetLastWriteTimeUtc(file, new DateTime(2020, 1, 1));
		foreach (var file in Directory.GetFiles(store.FolderOf(ids[1]))) File.SetLastWriteTimeUtc(file, new DateTime(2021, 1, 1));

		store.Put(TestSaves.Bundle(1, ids[2]));
		Assert.That(store.SaveIdsOnDisk().OrderBy(i => i), Is.EqualTo(new[] { ids[1], ids[2] }));
	}

	[Test]
	public void ValidatorStates()
	{
		var profile = TestSaves.Profile(3);
		var sha = Sha256Hex.Of(profile);
		SlotInput Input() => new()
		{
			AlreadyLoaded = true,
			Schema = 2,
			Generation = 3,
			ProfileSha256 = sha,
			Profile = profile,
			Readable = TestSaves.Readable
		};
		SlotState Check(Action<SlotInput> change)
		{
			var input = Input();
			change(input);
			return SlotValidator.Check(input).State;
		}

		Assert.That(Check(_ => { }), Is.EqualTo(SlotState.Ok));
		Assert.That(Check(i => i.AlreadyLoaded = false), Is.EqualTo(SlotState.New));
		Assert.That(Check(i => i.ProfileSha256 = null), Is.EqualTo(SlotState.Unverified));
		Assert.That(Check(i => i.Profile = TestSaves.Profile(4)), Is.EqualTo(SlotState.Unverified), "vanilla saved after the hook");
		Assert.That(Check(i => i.Profile = profile.Take(10).ToArray()), Is.EqualTo(SlotState.Corrupt));
		Assert.That(Check(i => i.Readable = _ => throw new InvalidOperationException()), Is.EqualTo(SlotState.Ok), "matching hash needs no parse");
		Assert.That(Check(i => { i.ProfileSha256 = null; i.Readable = _ => throw new InvalidOperationException(); }), Is.EqualTo(SlotState.Corrupt));
		Assert.That(Check(i => i.Profile = null), Is.EqualTo(SlotState.Missing));
		Assert.That(Check(i => i.Profile = Array.Empty<byte>()), Is.EqualTo(SlotState.Missing));
		Assert.That(Check(i => { i.Profile = null; i.Generation = 0; i.ProfileSha256 = null; }), Is.EqualTo(SlotState.New), "started, never saved");
		Assert.That(Check(i => { i.Profile = null; i.Generation = 0; i.Schema = 0; }), Is.EqualTo(SlotState.Missing), "hf3 save without its profile");
		Assert.That(Check(i => i.NewestBackup = TestSaves.Meta(5)), Is.EqualTo(SlotState.Older));
		Assert.That(Check(i => i.NewestBackup = TestSaves.Meta(3)), Is.EqualTo(SlotState.Ok));
		Assert.That(Check(i => i.Schema = SlotValidator.CurrentSchema + 1), Is.EqualTo(SlotState.NewerSchema));
		Assert.That(Check(i => { i.Schema = 0; i.Generation = 0; i.ProfileSha256 = null; }), Is.EqualTo(SlotState.Unverified), "hf3 save");
	}

	[Test]
	public void MigrationCopiesOnceAndKeepsTheOriginals()
	{
		var oldDir = Path.Combine(dir, "game", "Mods", "togetherMod", "saves");
		var newDir = Path.Combine(dir, "TogetherPlus", "Saves");
		Directory.CreateDirectory(oldDir);
		File.WriteAllText(Path.Combine(oldDir, "save_4.cms21mp"), "{\"Name\":\"old\"}");
		File.WriteAllText(Path.Combine(oldDir, "save_5.cms21mp"), "{\"Name\":\"other\"}");
		File.WriteAllText(Path.Combine(oldDir, "notes.txt"), "not a save");

		var first = SidecarMigration.Run(oldDir, newDir);
		Assert.That(first.Outcome, Is.EqualTo(MigrationOutcome.Migrated));
		Assert.That(first.Copied, Is.EquivalentTo(new[] { "save_4.cms21mp", "save_5.cms21mp" }));
		Assert.That(File.ReadAllText(Path.Combine(newDir, "save_4.cms21mp")), Is.EqualTo("{\"Name\":\"old\"}"));
		Assert.That(File.Exists(Path.Combine(oldDir, "save_4.cms21mp")), Is.True, "original kept");
		Assert.That(File.Exists(Path.Combine(newDir, "notes.txt")), Is.False);

		File.WriteAllText(Path.Combine(newDir, "save_4.cms21mp"), "{\"Name\":\"played since\"}");
		Assert.That(SidecarMigration.Run(oldDir, newDir).Outcome, Is.EqualTo(MigrationOutcome.AlreadyDone));
		Assert.That(File.ReadAllText(Path.Combine(newDir, "save_4.cms21mp")), Is.EqualTo("{\"Name\":\"played since\"}"));
	}

	[Test]
	public void MigrationNeverOverwritesAnExistingSave()
	{
		var oldDir = Path.Combine(dir, "old");
		var newDir = Path.Combine(dir, "new");
		Directory.CreateDirectory(oldDir);
		Directory.CreateDirectory(newDir);
		File.WriteAllText(Path.Combine(oldDir, "save_4.cms21mp"), "old");
		File.WriteAllText(Path.Combine(newDir, "save_4.cms21mp"), "new");

		var result = SidecarMigration.Run(oldDir, newDir);
		Assert.That(result.Skipped, Is.EqualTo(new[] { "save_4.cms21mp" }));
		Assert.That(File.ReadAllText(Path.Combine(newDir, "save_4.cms21mp")), Is.EqualTo("new"));
	}

	[Test]
	public void MigrationWithoutOldSavesLeavesNoMarker()
	{
		var newDir = Path.Combine(dir, "new");
		Assert.That(SidecarMigration.Run(Path.Combine(dir, "missing"), newDir).Outcome, Is.EqualTo(MigrationOutcome.NothingToDo));
		Assert.That(File.Exists(Path.Combine(newDir, SidecarMigration.MarkerName)), Is.False);
	}

	[Test]
	public void RecoveryPromptsOnlyForANewerCopyOrADamagedHostSave()
	{
		var sha = new string('f', 64);
		ReplicaOffer Offer(long generation, string saveId = null) => new() { SaveId = saveId ?? TestSaves.SaveId, Generation = generation, BundleSha256 = sha };

		Assert.That(RecoveryRules.Evaluate(TestSaves.SaveId, 5, false, Offer(6), false), Is.EqualTo(RecoveryDecision.Prompt));
		Assert.That(RecoveryRules.Evaluate(TestSaves.SaveId, 5, false, Offer(5), false), Is.EqualTo(RecoveryDecision.Ignore));
		Assert.That(RecoveryRules.Evaluate(TestSaves.SaveId, 5, true, Offer(4), false), Is.EqualTo(RecoveryDecision.Prompt));
		Assert.That(RecoveryRules.Evaluate(TestSaves.SaveId, 5, false, Offer(6), true), Is.EqualTo(RecoveryDecision.Ignore), "asked already");
		Assert.That(RecoveryRules.Evaluate(TestSaves.SaveId, 5, true, Offer(9, new string('b', 32)), false), Is.EqualTo(RecoveryDecision.Ignore), "other save");
		Assert.That(RecoveryRules.Evaluate(TestSaves.SaveId, 5, true, null, false), Is.EqualTo(RecoveryDecision.Ignore));
	}

	[Test]
	public void RestoreKeepsACopyOfTheCurrentFilesFirst()
	{
		var safety = new BundleStore(Path.Combine(dir, "Safety"), 10);
		var profilePath = Path.Combine(dir, "profile5.cms21b");
		var sidecarPath = Path.Combine(dir, "save_5.cms21mp");
		File.WriteAllBytes(profilePath, Encoding.UTF8.GetBytes("damaged"));
		File.WriteAllText(sidecarPath, "current sidecar");
		Assert.That(SaveBundle.TryDecode(TestSaves.Bundle(3), out var bundle, out _), Is.True);

		SaveRestore.Apply(bundle, profilePath, sidecarPath, safety, 7);

		Assert.That(File.ReadAllBytes(profilePath), Is.EqualTo(bundle.Profile));
		Assert.That(File.ReadAllBytes(sidecarPath), Is.EqualTo(bundle.Sidecar));
		var kept = safety.NewestValid(TestSaves.SaveId, out var info, out _);
		Assert.That(info.Meta.Note, Is.EqualTo("before restore"));
		Assert.That(Encoding.UTF8.GetString(kept.Profile), Is.EqualTo("damaged"));
		Assert.That(Encoding.UTF8.GetString(kept.Sidecar), Is.EqualTo("current sidecar"));
	}

	[Test]
	public void LedgerHistoryContinuesAcrossSessions()
	{
		var first = new EconomyLedger();
		first.Initialize(1000, 10);
		first.Apply(2, -100, 0);
		first.Apply(3, -5000, 0);
		first.Apply(2, 50, 1);
		var saved = first.History.Select(LedgerRecord.From).ToList();

		var server = new EconomyServer((_, _) => { }, () => new int[0]);
		server.Initialize(950, 11, saved);
		Assert.That(server.Ledger.Seq, Is.EqualTo(first.Seq));
		Assert.That(server.Ledger.History.Count(), Is.EqualTo(3));
		Assert.That(server.Ledger.Apply(2, -1, 0), Is.EqualTo(TxnStatus.Applied));
		Assert.That(server.Ledger.Seq, Is.EqualTo(first.Seq + 1));
		Assert.That(server.Ledger.History.Last().Seq, Is.EqualTo(first.Seq + 1));
	}

	[Test]
	public void LedgerHistoryIsRestoredOnlyBeforeTheWalletStarts()
	{
		var ledger = new EconomyLedger();
		ledger.Initialize(10, 0);
		Assert.That(ledger.Restore(new[] { new LedgerEntry(4, 2, -1, 0, TxnStatus.Applied, 9, 0) }), Is.False);
		Assert.That(ledger.Seq, Is.EqualTo(0));
	}

	[Test]
	public void DamagedLedgerRecordsAreIgnored()
	{
		var records = new List<LedgerRecord>
		{
			new() { seq = 1, slot = 2, money = -10, status = (int)TxnStatus.Applied, moneyAfter = 90 },
			new() { seq = 2, slot = 2, money = -10, status = 99, moneyAfter = 80 },
			null
		};
		var server = new EconomyServer((_, _) => { }, () => new int[0]);
		server.Initialize(90, 0, records);
		Assert.That(server.Ledger.History.Count(), Is.EqualTo(1));
		Assert.That(server.Ledger.Seq, Is.EqualTo(1));
	}
}