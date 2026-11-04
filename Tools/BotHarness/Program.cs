using System;
using System.Globalization;

namespace CMS21Together.BotHarness;

public static class Program
{
	private const string Usage = @"Together+ bot harness: soak-tests the network layer with bots.

Usage: BotHarness [options]
  --players N      players including the host's own (default 8)
  --seconds N      duration; simulated time for --transport sim (default 3600)
  --transport T    sim (virtual clock, deterministic) or tcp (real loopback sockets)
  --latency MS     one-way latency (sim only)
  --jitter MS      extra random latency (sim only)
  --loss P         0..1: unreliable frames dropped, reliable ones retransmitted (sim only)
  --bandwidth B    bytes per second per link, 0 = unlimited (sim only)
  --churn N        leave-and-rejoin events per bot per minute
  --seed N         random seed
  --locks          lock contention soak instead (sim only): bots fight over hot keys, write fenced
                   state, change scenes and freeze; checks exclusivity, fencing and lock leaks
  --kills N        frozen games per bot per minute (--locks and --economy, default 0.3)
  --economy        shared wallet soak instead (sim only): bots spend and earn concurrently, replay
                   transactions and freeze; checks exactly-once, never negative and convergence
  --cars           per-car desync detector soak instead (sim only): cars are silently diverged and must
                   converge within one detector cycle, with no other car pushed
  --purchases      purchase rollback soak instead (sim only): bots race to spend the last shared money
                   on items, paint, cars and upgrades; checks nothing uncharged is ever owned
  --inventory      shared inventory soak instead (sim only): bots add, remove and re-add items and
                   rejoin; checks that every game inventory converges on the host's set
  --movement       bounded-lag soak instead (sim only): 20 Hz movement over links with too little bandwidth;
                   checks that movement stays about one send interval plus the RTT old (defaults: clumsy
                   profile 100+20 ms, 5% loss, 8000 B/s). --no-coalesce runs the old reliable path to compare
  --save-kill N    kills a saving process N times at random moments (real processes and files);
                   checks that a valid generation always loads, restoring a backup when needed
  --dir PATH       working folder for --save-kill (default: a new folder in %TEMP%)
Exit code 0 when every check passed.";

	public static int Main(string[] args)
	{
		var options = new SoakOptions { Seconds = 3600, Progress = Console.WriteLine };
		var locks = false;
		var economy = false;
		var inventory = false;
		var purchases = false;
		var cars = false;
		var movement = false;
		var coalesce = true;
		var kills = 0.3;
		var saveKills = 0;
		string dir = null;
		try
		{
			for (var i = 0; i < args.Length; i++)
			{
				string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
				switch (args[i])
				{
					case "--players": options.Players = int.Parse(Next(), CultureInfo.InvariantCulture); break;
					case "--seconds": options.Seconds = double.Parse(Next(), CultureInfo.InvariantCulture); break;
					case "--transport": options.Transport = (SoakTransport)Enum.Parse(typeof(SoakTransport), Next(), true); break;
					case "--latency": options.LatencyMs = int.Parse(Next(), CultureInfo.InvariantCulture); break;
					case "--jitter": options.JitterMs = int.Parse(Next(), CultureInfo.InvariantCulture); break;
					case "--loss": options.LossRate = double.Parse(Next(), CultureInfo.InvariantCulture); break;
					case "--bandwidth": options.BytesPerSecond = long.Parse(Next(), CultureInfo.InvariantCulture); break;
					case "--churn": options.ChurnPerMinute = double.Parse(Next(), CultureInfo.InvariantCulture); break;
					case "--seed": options.Seed = int.Parse(Next(), CultureInfo.InvariantCulture); break;
					case "--locks": locks = true; break;
					case "--economy": economy = true; break;
					case "--inventory": inventory = true; break;
					case "--purchases": purchases = true; break;
					case "--cars": cars = true; break;
					case "--movement": movement = true; break;
					case "--no-coalesce": coalesce = false; break;
					case "--kills": kills = double.Parse(Next(), CultureInfo.InvariantCulture); break;
					case "--save-kill": saveKills = int.Parse(Next(), CultureInfo.InvariantCulture); break;
					case "--dir": dir = Next(); break;
					case "--save-writer": return SaveKill.RunWriter(Next());
					case "-h":
					case "--help":
						Console.WriteLine(Usage);
						return 0;
					default: throw new ArgumentException($"Unknown option {args[i]}.");
				}
			}
		}
		catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
		{
			Console.Error.WriteLine(ex.Message);
			Console.Error.WriteLine(Usage);
			return 2;
		}

		if (saveKills > 0)
		{
			var temporary = dir == null;
			dir ??= System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tp-savekill-" + Guid.NewGuid().ToString("N"));
			var exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
			Console.WriteLine($"Save kill test: {saveKills} kills in {dir}");
			var saveResult = SaveKill.Run(exe, dir, saveKills, options.Seed, Console.WriteLine);
			Console.WriteLine(saveResult);
			if (saveResult.Passed && temporary) System.IO.Directory.Delete(dir, true);
			return saveResult.Passed ? 0 : 1;
		}

		if (movement)
		{
			var defaults = new MovementSoakOptions();
			var movementOptions = new MovementSoakOptions
			{
				Players = options.Players,
				Seconds = options.Seconds,
				LatencyMs = Array.IndexOf(args, "--latency") >= 0 ? options.LatencyMs : defaults.LatencyMs,
				JitterMs = Array.IndexOf(args, "--jitter") >= 0 ? options.JitterMs : defaults.JitterMs,
				LossRate = Array.IndexOf(args, "--loss") >= 0 ? options.LossRate : defaults.LossRate,
				BytesPerSecond = Array.IndexOf(args, "--bandwidth") >= 0 ? options.BytesPerSecond : defaults.BytesPerSecond,
				Coalesce = coalesce,
				Seed = options.Seed,
				Progress = Console.WriteLine
			};
			Console.WriteLine($"Movement soak: {movementOptions.Players} players, {movementOptions.Seconds}s, latency {movementOptions.LatencyMs}+{movementOptions.JitterMs} ms, loss {movementOptions.LossRate:P0}, {movementOptions.BytesPerSecond} B/s, coalescing {(coalesce ? "on" : "off")}");
			var movementResult = MovementSoak.Run(movementOptions);
			Console.WriteLine(movementResult);
			return movementResult.Passed ? 0 : 1;
		}

		if (cars)
		{
			var carOptions = new CarSoakOptions
			{
				Players = options.Players,
				Seconds = options.Seconds,
				LatencyMs = options.LatencyMs,
				JitterMs = options.JitterMs,
				LossRate = options.LossRate,
				Seed = options.Seed,
				Trace = Environment.GetEnvironmentVariable("TP_TRACE") == "1" ? Console.WriteLine : null
			};
			Console.WriteLine($"Car soak: {carOptions.Players} players, {carOptions.Seconds}s, latency {carOptions.LatencyMs}+{carOptions.JitterMs} ms, loss {carOptions.LossRate:P0}");
			var carResult = CarSoak.Run(carOptions);
			Console.WriteLine(carResult);
			return carResult.Passed ? 0 : 1;
		}

		if (purchases)
		{
			var purchaseOptions = new PurchaseSoakOptions
			{
				Players = options.Players,
				Seconds = options.Seconds,
				LatencyMs = options.LatencyMs,
				JitterMs = options.JitterMs,
				LossRate = options.LossRate,
				Seed = options.Seed
			};
			Console.WriteLine($"Purchase soak: {purchaseOptions.Players} players, {purchaseOptions.Seconds}s, latency {purchaseOptions.LatencyMs}+{purchaseOptions.JitterMs} ms, loss {purchaseOptions.LossRate:P0}");
			var purchaseResult = PurchaseSoak.Run(purchaseOptions);
			Console.WriteLine(purchaseResult);
			return purchaseResult.Passed ? 0 : 1;
		}

		if (inventory)
		{
			var inventoryOptions = new InventorySoakOptions
			{
				Players = options.Players,
				Seconds = options.Seconds,
				LatencyMs = options.LatencyMs,
				JitterMs = options.JitterMs,
				LossRate = options.LossRate,
				Seed = options.Seed,
				Progress = Console.WriteLine
			};
			Console.WriteLine($"Inventory soak: {inventoryOptions.Players} players, {inventoryOptions.Seconds}s, latency {inventoryOptions.LatencyMs}+{inventoryOptions.JitterMs} ms, loss {inventoryOptions.LossRate:P0}");
			var inventoryResult = InventorySoak.Run(inventoryOptions);
			Console.WriteLine(inventoryResult);
			return inventoryResult.Passed ? 0 : 1;
		}

		if (economy)
		{
			var economyOptions = new EconomySoakOptions
			{
				Players = options.Players,
				Seconds = options.Seconds,
				LatencyMs = options.LatencyMs,
				JitterMs = options.JitterMs,
				LossRate = options.LossRate,
				KillsPerMinute = kills,
				Seed = options.Seed,
				Progress = Console.WriteLine
			};
			Console.WriteLine($"Economy soak: {economyOptions.Players} players, {economyOptions.Seconds}s, latency {economyOptions.LatencyMs}+{economyOptions.JitterMs} ms, loss {economyOptions.LossRate:P0}, kills {kills}/min");
			var economyResult = EconomySoak.Run(economyOptions);
			Console.WriteLine(economyResult);
			return economyResult.Passed ? 0 : 1;
		}

		if (locks)
		{
			var lockOptions = new LockSoakOptions
			{
				Players = options.Players,
				Seconds = options.Seconds,
				LatencyMs = options.LatencyMs,
				JitterMs = options.JitterMs,
				LossRate = options.LossRate,
				KillsPerMinute = kills,
				Seed = options.Seed,
				Progress = Console.WriteLine
			};
			Console.WriteLine($"Lock soak: {lockOptions.Players} players, {lockOptions.Seconds}s, latency {lockOptions.LatencyMs}+{lockOptions.JitterMs} ms, loss {lockOptions.LossRate:P0}, kills {kills}/min");
			var lockResult = LockSoak.Run(lockOptions);
			Console.WriteLine(lockResult);
			return lockResult.Passed ? 0 : 1;
		}

		Console.WriteLine($"Soak: {options.Players} players, {options.Seconds}s, {options.Transport}, latency {options.LatencyMs}+{options.JitterMs} ms, loss {options.LossRate:P0}, churn {options.ChurnPerMinute}/min");
		var result = Soak.Run(options);
		Console.WriteLine(result);
		return result.Passed ? 0 : 1;
	}
}