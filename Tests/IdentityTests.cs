using System.Collections.Generic;
using System.IO;
using System.Linq;
using CMS21Together.Shared;
using NUnit.Framework;

namespace CMS21Together.Tests;

public class IdentityTests
{
	private sealed class Profile : IPlayerProfile
	{
		public string ProfileId { get; set; }
		public ulong SteamId { get; set; }
		public string TokenHash { get; set; }
		public int Exp;
	}

	private static readonly string TokenA = new('a', 64);
	private static readonly string TokenB = new('b', 64);

	private static IdentityResult<Profile> Join(List<Profile> profiles, string guid = null, string token = null,
		ulong steamId = 0, bool host = false) =>
		IdentityService.Resolve(profiles, new IdentityClaim { LegacyGuid = guid, Token = token, VerifiedSteamId = steamId, Trusted = host },
			id => new Profile());

	private static List<Profile> Hf3Save() => new()
	{
		new Profile { ProfileId = "host-guid", Exp = 500 },
		new Profile { ProfileId = "alice-guid", Exp = 1200 },
		new Profile { ProfileId = "bob-guid", Exp = 300 }
	};

	[Test]
	public void Hf3ProfileMigratesOverDirectIp_KeepingXp()
	{
		var profiles = Hf3Save();
		var result = Join(profiles, "alice-guid", TokenA);

		Assert.That(result.Outcome, Is.EqualTo(IdentityOutcome.Migrated));
		Assert.That(result.ProfileId, Is.EqualTo("alice-guid"));
		Assert.That(result.Profile.Exp, Is.EqualTo(1200));
		Assert.That(result.Profile.TokenHash, Is.EqualTo(IdentityService.HashToken(TokenA)));
		Assert.That(profiles, Has.Count.EqualTo(3));
	}

	[Test]
	public void SameTokenRejoins_TheSameProfile()
	{
		var profiles = Hf3Save();
		Join(profiles, "alice-guid", TokenA);
		// A new install has a new GUID, but the token proves who it is.
		var again = Join(profiles, "new-install-guid", TokenA);

		Assert.That(again.Outcome, Is.EqualTo(IdentityOutcome.Existing));
		Assert.That(again.ProfileId, Is.EqualTo("alice-guid"));
		Assert.That(again.Profile.Exp, Is.EqualTo(1200));
	}

	[Test]
	public void AnotherPlayersGuid_GetsAFreshProfile_OnceClaimed()
	{
		var profiles = Hf3Save();
		Join(profiles, "alice-guid", TokenA);
		var impostor = Join(profiles, "alice-guid", TokenB);

		Assert.That(impostor.Outcome, Is.EqualTo(IdentityOutcome.Created));
		Assert.That(impostor.ProfileId, Is.Not.EqualTo("alice-guid"));
		Assert.That(impostor.Profile.Exp, Is.Zero);
		var alice = profiles.Single(p => p.ProfileId == "alice-guid");
		Assert.That(alice.TokenHash, Is.EqualTo(IdentityService.HashToken(TokenA)), "the owner's binding is untouched");
	}

	[Test]
	public void WrongToken_GetsAFreshProfile()
	{
		var profiles = Hf3Save();
		Join(profiles, "alice-guid", TokenA);
		var other = Join(profiles, "someone-guid", TokenB);

		Assert.That(other.Outcome, Is.EqualTo(IdentityOutcome.Created));
		Assert.That(other.ProfileId, Is.EqualTo("someone-guid"));
	}

	[Test]
	public void InvalidToken_IsIgnored_AndNeverClaimsALegacyProfile()
	{
		var profiles = Hf3Save();
		var result = Join(profiles, "alice-guid", "not-a-token");

		Assert.That(result.Outcome, Is.EqualTo(IdentityOutcome.Created));
		Assert.That(result.ProfileId, Is.Not.EqualTo("alice-guid"));
		Assert.That(IdentityService.IsLegacy(profiles.Single(p => p.ProfileId == "alice-guid")), Is.True);
	}

	[Test]
	public void Hf3ProfileMigratesToSteam_AndSurvivesAReinstall()
	{
		var profiles = Hf3Save();
		var first = Join(profiles, "bob-guid", TokenA, steamId: 7656119);

		Assert.That(first.Outcome, Is.EqualTo(IdentityOutcome.Migrated));
		Assert.That(first.ProfileId, Is.EqualTo("steam:7656119"));
		Assert.That(first.Profile.Exp, Is.EqualTo(300));

		// Reinstall: new GUID, credentials file gone.
		var again = Join(profiles, "fresh-guid", TokenB, steamId: 7656119);
		Assert.That(again.Outcome, Is.EqualTo(IdentityOutcome.Existing));
		Assert.That(again.Profile, Is.SameAs(first.Profile));
		Assert.That(profiles, Has.Count.EqualTo(3));
	}

	[Test]
	public void DirectIpProfile_MovesToSteam_WhenItsTokenIsPresented()
	{
		var profiles = new List<Profile>();
		var tcp = Join(profiles, "g1", TokenA);
		tcp.Profile.Exp = 42;

		var steam = Join(profiles, "g1", TokenA, steamId: 99);
		Assert.That(steam.Outcome, Is.EqualTo(IdentityOutcome.Migrated));
		Assert.That(steam.Profile, Is.SameAs(tcp.Profile));
		Assert.That(steam.ProfileId, Is.EqualTo("steam:99"));

		// And back to direct IP with the same token.
		var back = Join(profiles, "g1", TokenA);
		Assert.That(back.Profile, Is.SameAs(tcp.Profile));
		Assert.That(back.Outcome, Is.EqualTo(IdentityOutcome.Existing));
	}

	[Test]
	public void SteamProfile_IsNeverMovedToAnotherSteamAccount()
	{
		var profiles = new List<Profile>();
		Join(profiles, "g1", TokenA);
		var first = Join(profiles, "g1", TokenA, steamId: 1);
		var second = Join(profiles, "g1", TokenA, steamId: 2);

		Assert.That(second.Profile, Is.Not.SameAs(first.Profile));
		Assert.That(first.ProfileId, Is.EqualTo("steam:1"));
		Assert.That(second.ProfileId, Is.EqualTo("steam:2"));
	}

	[Test]
	public void SteamPrefixedGuid_CannotSquatASteamProfile()
	{
		var profiles = new List<Profile> { new() { ProfileId = "steam:5", SteamId = 5, Exp = 900 } };
		var squat = Join(profiles, "steam:5", TokenA);
		var squatNew = Join(profiles, "steam:6", TokenB);

		Assert.That(squat.ProfileId, Does.Not.StartWith("steam:"));
		Assert.That(squatNew.ProfileId, Does.Not.StartWith("steam:"));
		Assert.That(profiles.Single(p => p.ProfileId == "steam:5").Exp, Is.EqualTo(900));
	}

	[Test]
	public void HostClaimsItsOwnProfile_AndNobodyCanTakeItWithTheGuid()
	{
		var profiles = Hf3Save();
		var host = Join(profiles, "host-guid", TokenA, host: true);
		Assert.That(host.ProfileId, Is.EqualTo("host-guid"));
		Assert.That(host.Profile.Exp, Is.EqualTo(500));

		var thief = Join(profiles, "host-guid", TokenB);
		Assert.That(thief.ProfileId, Is.Not.EqualTo("host-guid"));
		Assert.That(thief.Outcome, Is.EqualTo(IdentityOutcome.Created));

		// The host can later join someone else's copy of the save as a normal player with its token.
		var asClient = Join(profiles, "host-guid", TokenA);
		Assert.That(asClient.Profile, Is.SameAs(host.Profile));
	}

	[Test]
	public void HostWithSteam_UsesItsSteamProfile()
	{
		var profiles = Hf3Save();
		var host = Join(profiles, "host-guid", TokenA, steamId: 11, host: true);
		Assert.That(host.ProfileId, Is.EqualTo("steam:11"));
		Assert.That(host.Profile.Exp, Is.EqualTo(500));
	}

	[Test]
	public void FreshProfiles_NeverReuseAnExistingId()
	{
		var ids = new Queue<string>(new[] { "alice-guid", "bob-guid", "unique" });
		var profiles = Hf3Save();
		Join(profiles, "alice-guid", TokenA);
		var result = IdentityService.Resolve(profiles, new IdentityClaim { LegacyGuid = "alice-guid", Token = TokenB },
			id => new Profile(), () => ids.Dequeue());

		Assert.That(result.ProfileId, Is.EqualTo("unique"));
		Assert.That(profiles.Select(p => p.ProfileId).Distinct().Count(), Is.EqualTo(profiles.Count));
	}

	[Test]
	public void Tokens_AreRandomLowercaseHex()
	{
		var a = IdentityService.NewToken();
		var b = IdentityService.NewToken();
		Assert.That(IdentityService.IsValidToken(a), Is.True);
		Assert.That(a, Is.Not.EqualTo(b));
		Assert.That(IdentityService.IsValidToken(a.ToUpperInvariant()), Is.False);
		Assert.That(IdentityService.IsValidToken(a.Substring(1)), Is.False);
		Assert.That(IdentityService.IsValidToken(null), Is.False);
	}

	[Test]
	public void HashEquals_ComparesWholeStrings()
	{
		var h = IdentityService.HashToken(TokenA);
		Assert.That(IdentityService.HashEquals(h, IdentityService.HashToken(TokenA)), Is.True);
		Assert.That(IdentityService.HashEquals(h, IdentityService.HashToken(TokenB)), Is.False);
		Assert.That(IdentityService.HashEquals(h, h.Substring(1)), Is.False);
		Assert.That(IdentityService.HashEquals(null, h), Is.False);
	}
}

public class CredentialStoreTests
{
	private string dir;

	[SetUp]
	public void SetUp()
	{
		dir = Path.Combine(Path.GetTempPath(), "tp-cred-" + Path.GetRandomFileName());
		Directory.CreateDirectory(dir);
	}

	[TearDown]
	public void TearDown() => Directory.Delete(dir, true);

	[Test]
	public void TokenIsStoredBeforeItIsReturned_AndReloaded()
	{
		var path = Path.Combine(dir, "sub", "credentials.txt");
		var token = new CredentialStore(path).GetOrCreate("save-1");

		Assert.That(IdentityService.IsValidToken(token), Is.True);
		Assert.That(File.Exists(path), Is.True);
		Assert.That(new CredentialStore(path).GetOrCreate("save-1"), Is.EqualTo(token));
		Assert.That(Directory.GetFiles(Path.GetDirectoryName(path), "*.tmp"), Is.Empty);
	}

	[Test]
	public void EachSaveGetsItsOwnToken()
	{
		var store = new CredentialStore(Path.Combine(dir, "c.txt"));
		Assert.That(store.GetOrCreate("save-1"), Is.Not.EqualTo(store.GetOrCreate("save-2")));
		Assert.That(store.Count, Is.EqualTo(2));
	}

	[Test]
	public void InvalidSaveIds_GetNoToken()
	{
		var store = new CredentialStore(Path.Combine(dir, "c.txt"));
		Assert.That(store.GetOrCreate(null), Is.Null);
		Assert.That(store.GetOrCreate(""), Is.Null);
		Assert.That(store.GetOrCreate("a\tb"), Is.Null);
		Assert.That(store.GetOrCreate(new string('x', 65)), Is.Null);
		Assert.That(store.Count, Is.Zero);
	}

	[Test]
	public void AFileLockedAtStartIsReadAgain_NotWrittenOver()
	{
		// Phase 8 assessment: one failed read used to rewrite the file with only the new token, dropping every other
		// host's token (and the player's progress on those hosts).
		var path = Path.Combine(dir, "c.txt");
		var store1 = new CredentialStore(path);
		var first = store1.GetOrCreate("save-1");
		CredentialStore store2;
		using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) store2 = new CredentialStore(path);
		Assert.That(store2.Count, Is.Zero, "the read failed");
		Assert.That(store2.GetOrCreate("save-1"), Is.EqualTo(first), "read again on first use");
		var second = store2.GetOrCreate("save-2");
		var reloaded = new CredentialStore(path);
		Assert.That(reloaded.GetOrCreate("save-1"), Is.EqualTo(first));
		Assert.That(reloaded.GetOrCreate("save-2"), Is.EqualTo(second));
	}

	[Test]
	public void AnUnreadableFileIsKeptAside()
	{
		var path = Path.Combine(dir, "c.txt");
		File.WriteAllText(path, "some-future-format\nsave-1\tx\n");
		var token = new CredentialStore(path).GetOrCreate("save-9");
		Assert.That(IdentityService.IsValidToken(token), Is.True);
		var aside = Directory.GetFiles(dir, "c.txt.unreadable-*");
		Assert.That(aside, Has.Length.EqualTo(1));
		Assert.That(File.ReadAllText(aside[0]), Does.StartWith("some-future-format"));
		Assert.That(new CredentialStore(path).GetOrCreate("save-9"), Is.EqualTo(token));
	}

	[TestCase("C:/x/profile3.cms21", 0)]
	[TestCase("C:/x/profile3.cms21b", 1)]
	[TestCase(null, 1)]
	public void KeptCopiesRecordTheRealProfileFormat(string path, int format) =>
		Assert.That(CMS21Together.Shared.Persistence.SaveRestore.FormatOf(path), Is.EqualTo((byte)format));

	[Test]
	public void CorruptFile_IsIgnored_NotFatal()
	{
		var path = Path.Combine(dir, "c.txt");
		File.WriteAllText(path, "garbage\nsave-1\tnot-a-token\n");
		var store = new CredentialStore(path);
		Assert.That(store.Count, Is.Zero);
		Assert.That(IdentityService.IsValidToken(store.GetOrCreate("save-1")), Is.True);
	}
}

public class AtomicFileTests
{
	[Test]
	public void ReplacesExistingFile_AndKeepsABackup()
	{
		var dir = Path.Combine(Path.GetTempPath(), "tp-atomic-" + Path.GetRandomFileName());
		try
		{
			var path = Path.Combine(dir, "f.txt");
			AtomicFile.WriteAllText(path, "one");
			AtomicFile.WriteAllBytes(path, System.Text.Encoding.UTF8.GetBytes("two"), path + ".bak");

			Assert.That(File.ReadAllText(path), Is.EqualTo("two"));
			Assert.That(File.ReadAllText(path + ".bak"), Is.EqualTo("one"));
			Assert.That(File.Exists(path + ".tmp"), Is.False);
			Assert.That(AtomicFile.ReadAllTextOrNull(Path.Combine(dir, "missing")), Is.Null);
		}
		finally
		{
			if (Directory.Exists(dir)) Directory.Delete(dir, true);
		}
	}
}