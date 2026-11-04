using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace CMS21Together.Shared;

/// <summary>What the identity service needs from a saved player profile.</summary>
public interface IPlayerProfile
{
	string ProfileId { get; set; }
	ulong SteamId { get; set; }
	string TokenHash { get; set; }
}

/// <summary>Who a joining player says they are, plus what the host knows for sure.</summary>
public sealed class IdentityClaim
{
	/// <summary>Steam id proven by the Steam relay connection (0 on direct IP).</summary>
	public ulong VerifiedSteamId;

	/// <summary>The host's own player: its claim is trusted.</summary>
	public bool Trusted;

	/// <summary>Per-install GUID used as identity by Together 0.4.x (only used to migrate old profiles).</summary>
	public string LegacyGuid;

	/// <summary>Secret this player keeps for this save; the host stores only its hash.</summary>
	public string Token;
}

public enum IdentityOutcome
{
	Existing,
	Migrated,
	Created
}

public sealed class IdentityResult<T>
{
	public T Profile;
	public IdentityOutcome Outcome;
	public string ProfileId => ((IPlayerProfile)Profile).ProfileId;
}

/// <summary>
/// Decides which saved profile a joining player gets (audit A19). The id never comes from the client alone:
/// Steam players are identified by the Steam id of their connection; direct-IP players by a per-save secret token
/// that the client creates and keeps, and the host remembers only as a SHA-256 hash. Because the client stores the
/// token before sending it, a lost packet or a host crash can never leave a profile bound to a token nobody has.
/// Profiles from Together 0.4.x (keyed by the client's GUID) are migrated the first time someone presents that GUID
/// (trust on first use), then bound to the Steam id or the token.
/// </summary>
public static class IdentityService
{
	public const string SteamPrefix = "steam:";
	public const int TokenLength = 64;

	public static string SteamProfileId(ulong steamId) => SteamPrefix + steamId;

	public static IdentityResult<T> Resolve<T>(IList<T> profiles, IdentityClaim claim, Func<string, T> create,
		Func<string> newId = null)
		where T : class, IPlayerProfile
	{
		if (profiles == null) throw new ArgumentNullException(nameof(profiles));
		if (claim == null) throw new ArgumentNullException(nameof(claim));
		if (create == null) throw new ArgumentNullException(nameof(create));
		newId ??= () => Guid.NewGuid().ToString();

		var token = IsValidToken(claim.Token) ? claim.Token : null;
		var tokenHash = token != null ? HashToken(token) : null;
		var credentialed = FindByToken(profiles, tokenHash);

		if (claim.VerifiedSteamId != 0)
		{
			var steamId = claim.VerifiedSteamId;
			var key = SteamProfileId(steamId);
			var existing = Find(profiles, key);
			if (existing != null)
			{
				existing.SteamId = steamId;
				return Result(existing, IdentityOutcome.Existing);
			}

			// A direct-IP profile proven by its token, or an old GUID profile, moves to the Steam id.
			var previous = credentialed != null && credentialed.SteamId == 0 ? credentialed : FindLegacy(profiles, claim.LegacyGuid);
			if (previous != null)
			{
				previous.ProfileId = key;
				previous.SteamId = steamId;
				return Result(previous, IdentityOutcome.Migrated);
			}

			var created = Add(profiles, create, key);
			created.SteamId = steamId;
			return Result(created, IdentityOutcome.Created);
		}

		if (credentialed != null) return Result(credentialed, IdentityOutcome.Existing);

		if (claim.Trusted && IsUsableGuid(claim.LegacyGuid))
		{
			var own = Find(profiles, claim.LegacyGuid);
			var outcome = IdentityOutcome.Existing;
			if (own == null)
			{
				own = Add(profiles, create, claim.LegacyGuid);
				outcome = IdentityOutcome.Created;
			}
			else if (IsLegacy(own))
				outcome = IdentityOutcome.Migrated;
			// Bind it so nobody else can claim the host's profile with its GUID.
			if (string.IsNullOrEmpty(own.TokenHash) && own.SteamId == 0) own.TokenHash = tokenHash ?? HashToken(NewToken());
			return Result(own, outcome);
		}

		if (tokenHash != null)
		{
			var legacy = FindLegacy(profiles, claim.LegacyGuid);
			if (legacy != null)
			{
				legacy.TokenHash = tokenHash;
				return Result(legacy, IdentityOutcome.Migrated);
			}
		}

		var id = IsUsableGuid(claim.LegacyGuid) ? claim.LegacyGuid : null;
		while (id == null || Find(profiles, id) != null) id = newId();
		var fresh = Add(profiles, create, id);
		// Without a token the profile cannot be claimed again; it still keeps progress for this session.
		fresh.TokenHash = tokenHash ?? HashToken(NewToken());
		return Result(fresh, IdentityOutcome.Created);
	}

	/// <summary>True for a profile written by Together 0.4.x that nobody has claimed yet.</summary>
	public static bool IsLegacy(IPlayerProfile profile) =>
		profile != null && profile.SteamId == 0 && string.IsNullOrEmpty(profile.TokenHash) && IsUsableGuid(profile.ProfileId);

	public static bool IsValidToken(string token)
	{
		if (token == null || token.Length != TokenLength) return false;
		foreach (var c in token)
			if (!(c >= '0' && c <= '9' || c >= 'a' && c <= 'f'))
				return false;
		return true;
	}

	public static string NewToken()
	{
		var bytes = new byte[TokenLength / 2];
		using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
		return ToHex(bytes);
	}

	public static string HashToken(string token)
	{
		using var sha = SHA256.Create();
		return ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(token ?? string.Empty)));
	}

	/// <summary>Constant-time comparison of two hashes.</summary>
	public static bool HashEquals(string a, string b)
	{
		if (a == null || b == null || a.Length != b.Length) return false;
		var diff = 0;
		for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
		return diff == 0;
	}

	private static T FindByToken<T>(IList<T> profiles, string tokenHash) where T : class, IPlayerProfile
	{
		if (tokenHash == null) return null;
		T match = null;
		// Compare against every profile so the time taken does not reveal which one matched.
		foreach (var profile in profiles)
			if (profile != null && HashEquals(profile.TokenHash, tokenHash) && match == null)
				match = profile;
		return match;
	}

	private static T FindLegacy<T>(IList<T> profiles, string guid) where T : class, IPlayerProfile
	{
		if (!IsUsableGuid(guid)) return null;
		var profile = Find(profiles, guid);
		return IsLegacy(profile) ? profile : null;
	}

	private static bool IsUsableGuid(string id) =>
		!string.IsNullOrWhiteSpace(id) && id.Length <= 64 && !id.StartsWith(SteamPrefix, StringComparison.Ordinal);

	private static T Find<T>(IList<T> profiles, string id) where T : class, IPlayerProfile
	{
		if (id == null) return null;
		foreach (var profile in profiles)
			if (profile != null && profile.ProfileId == id)
				return profile;
		return null;
	}

	private static T Add<T>(IList<T> profiles, Func<string, T> create, string id) where T : class, IPlayerProfile
	{
		var profile = create(id);
		profile.ProfileId = id;
		profiles.Add(profile);
		return profile;
	}

	private static IdentityResult<T> Result<T>(T profile, IdentityOutcome outcome) => new() { Profile = profile, Outcome = outcome };

	private static string ToHex(byte[] bytes)
	{
		var builder = new StringBuilder(bytes.Length * 2);
		foreach (var b in bytes) builder.Append(b.ToString("x2"));
		return builder.ToString();
	}
}