using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using CMS21Together.Shared.Net;

namespace CMS21Together.Shared.World;

/// <summary>One car part's synced state as a game shows it (read from the live game, on the host and the players alike).</summary>
public readonly struct PartState
{
	public PartState(string key, bool unmounted, float condition, int quality, string tuned, bool painted)
	{
		Key = key;
		Unmounted = unmounted;
		// Whole percent: what both games set from the same synced value always rounds the same way.
		Condition = (int)Math.Round(condition * 100f);
		Quality = quality;
		Tuned = tuned ?? string.Empty;
		Painted = painted;
	}

	public string Key { get; }
	public bool Unmounted { get; }
	public int Condition { get; }
	public int Quality { get; }
	public string Tuned { get; }
	public bool Painted { get; }
}

/// <summary>Order-independent fingerprint of a car's parts (Phase 6, per-car desync detector).</summary>
public static class CarHash
{
	public static string Of(IEnumerable<PartState> parts)
	{
		var text = new StringBuilder();
		foreach (var part in parts.OrderBy(p => p.Key, StringComparer.Ordinal))
			text.Append(part.Key).Append('|').Append(part.Unmounted ? 1 : 0).Append('|').Append(part.Condition).Append('|')
				.Append(part.Quality).Append('|').Append(part.Tuned).Append('|').Append(part.Painted ? 1 : 0).Append('\n');
		using var sha = SHA256.Create();
		var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()));
		return BitConverter.ToString(hash, 0, 12).Replace("-", string.Empty).ToLowerInvariant();
	}
}

/// <summary>
/// Host side of the per-car desync detector. A player's hash that differs from the host's live car is answered with
/// the host's full car, but only when nothing is moving: no lock on the car and no change seen for <see cref="IdleMs"/>.
/// (A player only reports cars it has not changed itself for that long, so its own change in flight is never undone.)
/// </summary>
public sealed class CarDesyncDetector
{
	public const long IdleMs = 3000;

	private readonly Func<long> clock;
	private readonly Dictionary<int, long> lastChange = new();
	private readonly Dictionary<(int slot, int car), (string player, string host)> pushed = new();

	public CarDesyncDetector(Func<long> clockMs) => clock = clockMs ?? throw new ArgumentNullException(nameof(clockMs));

	public int Pushes { get; private set; }
	public int Checks { get; private set; }

	/// <summary>Mismatches a push did not repair (a difference the player's game does not take from a push): not retried.</summary>
	public int Unrepaired { get; private set; }

	/// <summary>The host saw a change of this car (a part write, a load, a move).</summary>
	public void Touched(int carId) => lastChange[carId] = clock();

	public void Forget(int carId) => lastChange.Remove(carId);

	public bool Idle(int carId) => !lastChange.TryGetValue(carId, out var at) || clock() - at >= IdleMs;

	/// <summary>True: push the host's car to that player.</summary>
	/// <param name="hostHash">The host's live hash of the car, or null when the host cannot read it (not in the garage).</param>
	public bool ShouldPush(int slot, int carId, string playerHash, string hostHash, bool carLocked)
	{
		Checks++;
		if (hostHash == null || playerHash == hostHash || carLocked || !Idle(carId)) return false;
		// The same difference again after a push: pushing once more would not help, so it is not repeated.
		if (pushed.TryGetValue((slot, carId), out var last) && last.player == playerHash && last.host == hostHash)
		{
			Unrepaired++;
			return false;
		}
		pushed[(slot, carId)] = (playerHash, hostHash);
		Pushes++;
		return true;
	}

	public void PlayerLeft(int slot)
	{
		foreach (var key in pushed.Keys.Where(k => k.slot == slot).ToList()) pushed.Remove(key);
	}
}

/// <summary>Player side: every <see cref="EveryMs"/>, the hashes of the cars this player has not changed lately.</summary>
public sealed class CarCheckClient
{
	public const long DefaultEveryMs = 10_000;

	private readonly Func<long> clock;
	private readonly Dictionary<int, long> lastChange = new();
	private long nextReport;

	public CarCheckClient(Func<long> clockMs, long everyMs = DefaultEveryMs)
	{
		clock = clockMs ?? throw new ArgumentNullException(nameof(clockMs));
		EveryMs = everyMs;
		nextReport = clock() + everyMs;
	}

	public long EveryMs { get; }

	/// <summary>This player changed the car, or a change of it was just applied here.</summary>
	public void Touched(int carId) => lastChange[carId] = clock();

	/// <summary>Report cycles so far (a cycle may have no idle car to report).</summary>
	public long Cycles { get; private set; }

	/// <summary>The next <see cref="Due"/> reports at once (forced resync from the recovery keys).</summary>
	public void ForceNow() => nextReport = clock();

	/// <summary>Cars to report now (empty until the next cycle).</summary>
	public List<int> Due(IEnumerable<int> loadedCars)
	{
		var now = clock();
		if (now < nextReport) return new List<int>();
		nextReport = now + EveryMs;
		Cycles++;
		return loadedCars.Where(id => !lastChange.TryGetValue(id, out var at) || now - at >= CarDesyncDetector.IdleMs).ToList();
	}
}

/// <summary>
/// Ghost cars (Phase 6b): cars a player has that the host does not (deleted), and cars the host has that the player
/// lacks (missed spawn). A difference must show in two reports in a row before anything is done, so a car being
/// spawned or deleted right now is never touched.
/// </summary>
public sealed class CarRoster
{
	public const int ReportsBeforeActing = 2;

	private readonly Dictionary<(int slot, int car, bool ghost), int> seen = new();

	public (List<int> ghosts, List<int> missing) Compare(int slot, IEnumerable<int> playerCars, IEnumerable<int> hostCars)
	{
		var player = new HashSet<int>(playerCars ?? Enumerable.Empty<int>());
		var host = new HashSet<int>(hostCars ?? Enumerable.Empty<int>());
		var now = new HashSet<(int, int, bool)>();
		foreach (var car in player)
			if (!host.Contains(car))
				now.Add((slot, car, true));
		foreach (var car in host)
			if (!player.Contains(car))
				now.Add((slot, car, false));

		foreach (var key in seen.Keys.Where(k => k.slot == slot && !now.Contains(k)).ToList()) seen.Remove(key);
		var ghosts = new List<int>();
		var missing = new List<int>();
		foreach (var key in now)
		{
			seen.TryGetValue(key, out var count);
			count++;
			if (count < ReportsBeforeActing)
			{
				seen[key] = count;
				continue;
			}
			seen.Remove(key);
			(key.Item3 ? ghosts : missing).Add(key.Item2);
		}
		ghosts.Sort();
		missing.Sort();
		return (ghosts, missing);
	}

	public void PlayerLeft(int slot)
	{
		foreach (var key in seen.Keys.Where(k => k.slot == slot).ToList()) seen.Remove(key);
	}
}

public static class CarCheckWire
{
	public const int MaxCars = 64;

	public static void Write(Packet packet, IList<(int carId, string hash)> cars, IList<int> loaded = null)
	{
		packet.Write(cars.Count);
		foreach (var (carId, hash) in cars)
		{
			packet.Write(carId);
			packet.Write(hash);
		}
		if (loaded == null) return;
		// Phase 6b: every car loaded on the player's game (ghost car cleanup).
		packet.Write(loaded.Count);
		foreach (var id in loaded) packet.Write(id);
	}

	/// <summary>The loaded cars after <see cref="Read"/>, or null when the report has none.</summary>
	public static List<int> ReadLoaded(Packet packet)
	{
		if (packet.UnreadLength() == 0) return null;
		var count = packet.ReadInt();
		if (count < 0 || count > MaxCars) throw new NetFormatException($"Car report with {count} loaded cars.");
		var result = new List<int>(count);
		for (var i = 0; i < count; i++) result.Add(packet.ReadInt());
		return result;
	}

	public static List<(int carId, string hash)> Read(Packet packet)
	{
		var count = packet.ReadInt();
		if (count < 0 || count > MaxCars) throw new NetFormatException($"Car check with {count} cars.");
		var result = new List<(int, string)>(count);
		for (var i = 0; i < count; i++)
		{
			var carId = packet.ReadInt();
			var hash = packet.Read<string>();
			if (hash == null || hash.Length > 64) throw new NetFormatException("Invalid car hash.");
			result.Add((carId, hash));
		}
		return result;
	}
}