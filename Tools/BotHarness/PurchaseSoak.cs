using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using CMS21Together.Shared;
using CMS21Together.Shared.Economy;
using CMS21Together.Shared.Net;
using CMS21Together.Shared.World;

namespace CMS21Together.BotHarness;

public sealed class PurchaseSoakOptions
{
	public int Players = 4;
	public double Seconds = 120;
	public int LatencyMs;
	public int JitterMs;
	public double LossRate;
	public int TickMs = 10;

	/// <summary>Purchases tried per bot per second; money is kept scarce so they race for the last of it.</summary>
	public double ActionsPerSecond = 3;

	public long StartMoney = 600;
	public double QuietEverySeconds = 20;
	public int Seed = 1;
}

public sealed class PurchaseSoakResult
{
	public readonly List<string> Errors = new();
	public long Debits, Applied, Refused, ItemsBought, ItemsRefused, Paints, PaintsRefused, Cars, CarsRolledBack, UpgradeAsks, UpgradesBought, LogErrors;
	public double SimulatedSeconds;
	public TimeSpan WallTime;
	public bool Passed => Errors.Count == 0;

	internal void Fail(string message)
	{
		if (Errors.Count < 50) Errors.Add(message);
	}

	public override string ToString() =>
		$"{(Passed ? "PASS" : "FAIL")}: {SimulatedSeconds:0}s simulated in {WallTime.TotalSeconds:0.0}s, {Debits} debits ({Applied} applied, {Refused} refused); " +
		$"items {ItemsBought} bought / {ItemsRefused} refused, paint {Paints} / {PaintsRefused} refused, cars {Cars} / {CarsRolledBack} rolled back, " +
		$"upgrades {UpgradesBought} bought of {UpgradeAsks} asked, log errors {LogErrors}" +
		(Passed ? "" : Environment.NewLine + string.Join(Environment.NewLine, Errors.Select(e => "  - " + e)));
}

/// <summary>
/// Phase 6 exit test (purchase rollback): 2 to 8 bots race to spend the last shared money on shop items, paint, cars
/// and garage upgrades over lossy, laggy links, with the real economy, shared inventory, purchase linking and
/// pre-authorization. Afterwards nobody owns anything the ledger did not charge for, every owned purchase maps to
/// exactly one applied debit, and a refused purchase was rolled back on the buyer and never reached anyone else.
/// </summary>
public sealed class PurchaseSoak
{
	private const string Version = "purchase-soak-1";
	private static readonly string[] UpgradeIds = { "crane", "lift2", "paintshop", "dyno", "welder", "tirechanger" };

	private readonly PurchaseSoakOptions options;
	private readonly PurchaseSoakResult result = new();
	private readonly Random random;
	private readonly List<Bot> bots = new();
	private readonly Stopwatch wall = new();
	private readonly Dictionary<long, (int slot, int txn)> purchasedItems = new();
	private readonly Dictionary<(int slot, int number), TxnStatus> decided = new();
	private readonly Dictionary<int, (int slot, int txn)> paints = new();
	private readonly HashSet<int> hostPaintsAccepted = new();
	private readonly Dictionary<string, int> upgradeCharges = new();
	private readonly HashSet<string> hostUpgrades = new();
	private readonly Dictionary<(int, string), bool> approvals = new();
	private readonly Dictionary<(int slot, int number), long> amounts = new();
	private NetHost host;
	private EconomyServer economy;
	private InventoryHost<SoakItem> inventory;
	private readonly Dictionary<int, int> hostPaints = new();
	private long chargedTotal;
	private long now;
	private long nextUid = 1;
	private int nextPaint = 1;
	private bool quiet;

	public PurchaseSoak(PurchaseSoakOptions options)
	{
		this.options = options ?? new PurchaseSoakOptions();
		random = new Random(this.options.Seed);
	}

	public static PurchaseSoakResult Run(PurchaseSoakOptions options) => new PurchaseSoak(options).Run();

	private long Clock() => now;
	private long SettleMs => 1500 + 6 * (options.LatencyMs + options.JitterMs);

	public PurchaseSoakResult Run()
	{
		var previousSink = Log.Sink;
		var previousLevel = Log.MinLevel;
		Log.Sink = new Sink(result, previousSink);
		Log.MinLevel = LogLevel.Warn;
		wall.Start();
		try
		{
			host = new NetHost(options.Players, Version, Clock);
			IEnumerable<int> Players() => bots.Where(b => b.Joined).Select(b => b.Slot).ToList();
			economy = new EconomyServer((slot, p) => host.Send(slot, p.FrameBuffer, p.FrameLength), Players);
			inventory = new InventoryHost<SoakItem>(InventoryKind.Items, i => i.UID, (slot, p) => host.Send(slot, p.FrameBuffer, p.FrameLength), Players)
			{
				Charged = economy.Charged
			};
			host.FrameReceived += OnHostFrame;
			for (var i = 0; i < options.Players; i++) bots.Add(new Bot(this, i == 0));

			var end = (long)(options.Seconds * 1000);
			var nextQuiet = (long)(options.QuietEverySeconds * 1000);
			var initialized = false;
			while (now < end)
			{
				Step(!quiet);
				if (!initialized && now >= 1000 && bots.All(b => b.Joined))
				{
					economy.Initialize(options.StartMoney, 0);
					initialized = true;
				}
				if (now < nextQuiet) continue;
				Quiet();
				nextQuiet = now + (long)(options.QuietEverySeconds * 1000);
			}
			Quiet();
			Final();
		}
		finally
		{
			Log.Sink = previousSink;
			Log.MinLevel = previousLevel;
			wall.Stop();
			result.WallTime = wall.Elapsed;
			result.SimulatedSeconds = now / 1000.0;
		}
		return result;
	}

	private void Step(bool act)
	{
		now += options.TickMs;
		host.Tick();
		foreach (var bot in bots) bot.Tick(act);
	}

	private void Quiet()
	{
		quiet = true;
		var until = now + SettleMs;
		while (now < until) Step(false);
		quiet = false;
		Check($"{now} ms");
	}

	private void Check(string when)
	{
		var hostItems = new HashSet<long>(inventory.Authority.Items.Select(i => i.UID));
		foreach (var bot in bots)
		{
			if (!new HashSet<long>(bot.Game.Keys).SetEquals(hostItems)) result.Fail($"{when}: bot #{bot.Slot}'s inventory differs from the host's.");
			if (bot.Economy.PendingCount != 0) result.Fail($"{when}: bot #{bot.Slot} has pending transactions.");
			if (bot.Economy.Money != economy.Ledger.Money) result.Fail($"{when}: bot #{bot.Slot} shows {bot.Economy.Money}, the host has {economy.Ledger.Money}.");
			foreach (var car in hostPaints)
				if (!bot.Paints.TryGetValue(car.Key, out var shown) || shown != car.Value)
					result.Fail($"{when}: bot #{bot.Slot} shows car {car.Key} in colour {shown}, the host {car.Value}.");
			if (!bot.Upgrades.SetEquals(hostUpgrades)) result.Fail($"{when}: bot #{bot.Slot}'s upgrades differ from the host's.");
		}
		// Nobody owns an item the ledger did not charge for, and every charged item is owned.
		foreach (var pair in purchasedItems)
		{
			var charged = Charged(pair.Value.slot, pair.Value.txn);
			if (charged != hostItems.Contains(pair.Key))
				result.Fail($"{when}: item {pair.Key} (debit {pair.Value.txn} of #{pair.Value.slot}) is {(charged ? "charged but missing" : "owned but not charged")}.");
		}
		foreach (var bot in bots)
			foreach (var car in bot.Parking)
				if (!Charged(bot.Slot, car.Value))
					result.Fail($"{when}: bot #{bot.Slot} keeps car {car.Key} whose debit {car.Value} was not charged.");
	}

	private void Final()
	{
		foreach (var bot in bots)
		{
			foreach (var uid in bot.SeenFromHost)
				if (purchasedItems.TryGetValue(uid, out var p) && !Charged(p.slot, p.txn))
					result.Fail($"Bot #{bot.Slot} received item {uid}, a refused purchase of #{p.slot}.");
			foreach (var paint in bot.PaintsSeen)
				if (paints.TryGetValue(paint, out var p) && !Charged(p.slot, p.txn))
					result.Fail($"Bot #{bot.Slot} received paint {paint}, a refused paint job of #{p.slot}.");
			// Every car bought with an applied debit is still there, once.
			foreach (var car in bot.CarsBought)
				if (Charged(bot.Slot, car.Value) != bot.Parking.ContainsKey(car.Key))
					result.Fail($"Bot #{bot.Slot}'s car {car.Key}: charged {Charged(bot.Slot, car.Value)}, owned {bot.Parking.ContainsKey(car.Key)}.");
		}
		foreach (var paint in hostPaintsAccepted)
			if (paints.TryGetValue(paint, out var p) && !Charged(p.slot, p.txn))
				result.Fail($"The host accepted paint {paint}, whose debit was refused.");
		foreach (var id in hostUpgrades)
			if (!upgradeCharges.TryGetValue(id, out var count) || count != 1)
				result.Fail($"Upgrade {id} was charged {count} times.");
		if (upgradeCharges.Keys.Any(id => !hostUpgrades.Contains(id))) result.Fail("An upgrade was charged but never unlocked.");

		// The ledger is exactly the start plus every applied transaction and charge.
		long expected = options.StartMoney + chargedTotal;
		foreach (var pair in amounts)
			if (Charged(pair.Key.slot, pair.Key.number))
				expected += pair.Value;
		if (expected != economy.Ledger.Money) result.Fail($"The ledger holds {economy.Ledger.Money}, the applied changes add up to {expected}.");
		foreach (var pair in amounts)
		{
			result.Debits += pair.Value < 0 ? 1 : 0;
			if (pair.Value < 0 && Charged(pair.Key.slot, pair.Key.number)) result.Applied++;
			else if (pair.Value < 0) result.Refused++;
		}
		result.ItemsRefused = inventory.RefusedPurchases;
		result.PaintsRefused = paints.Keys.Count(p => !Charged(paints[p].slot, paints[p].txn));
		result.CarsRolledBack = bots.Sum(b => b.Linker.RolledBack);
		if (result.Refused == 0) result.Fail("No debit was ever refused: the soak did not race for the last money.");
	}

	private bool Charged(int slot, int number) => decided.TryGetValue((slot, number), out var status) && status == TxnStatus.Applied;

	private void OnHostFrame(NetConnection connection, NetFrame frame)
	{
		var slot = connection.SlotId;
		using var packet = new Packet(frame);
		switch ((PacketTypes)frame.Type)
		{
			case PacketTypes.econTxn:
				var number = EconomyWire.ReadTxn(new Packet(frame)).number;
				economy.HandleTxn(slot, packet);
				// Kept here: the host remembers only the last decisions per player, the checks look at every one.
				decided[(slot, number)] = economy.StatusOf(slot, number);
				break;
			case PacketTypes.invPush:
				var push = InventoryWire.ReadPush<SoakItem>(packet);
				inventory.HandlePush(slot, push.seq, push.op, push.item, push.txn);
				break;
			case PacketTypes.invRequest:
				InventoryWire.ReadRequest(packet);
				var bot = bots.First(b => b.Slot == slot);
				if (!bot.Joined)
				{
					bot.Joined = true;
					economy.PlayerJoined(slot);
					inventory.PlayerJoined(slot);
				}
				inventory.SendSnapshot(slot);
				break;
			case PacketTypes.carPaint:
				// Each bot paints its own car (one painter per car: the paint shop is a locked station in game).
				var car = packet.ReadInt();
				var color = packet.ReadInt();
				var txn = packet.ReadInt();
				if (txn > 0 && !economy.Charged(slot, txn))
				{
					var previous = hostPaints.TryGetValue(car, out var c0) ? c0 : 0;
					Send(slot, PacketTypes.carPaint, p =>
					{
						p.Write(car);
						p.Write(previous);
					});
					break;
				}
				hostPaints[car] = color;
				hostPaintsAccepted.Add(color);
				// As ServerHandle.CarPaintPacket: the paid colour goes back to the painter too.
				Send(slot, PacketTypes.carPaint, p =>
					{
						p.Write(car);
						p.Write(color);
					});
				foreach (var other in bots.Where(b => b.Joined && b.Slot != slot))
					Send(other.Slot, PacketTypes.carPaint, p =>
					{
						p.Write(car);
						p.Write(color);
					});
				break;
			case PacketTypes.purchaseAsk:
				var ask = PurchaseWire.ReadAsk(packet);
				TxnStatus status;
				if (hostUpgrades.Contains(ask.id) || approvals.Keys.Any(a => a.Item2 == ask.id)) status = TxnStatus.Invalid;
				else status = economy.Charge(slot, -ask.cost);
				if (status == TxnStatus.Applied)
				{
					approvals[(slot, ask.id)] = true;
					chargedTotal -= ask.cost;
					upgradeCharges[ask.id] = upgradeCharges.TryGetValue(ask.id, out var c) ? c + 1 : 1;
				}
				Send(slot, PacketTypes.purchaseAnswer, p => PurchaseWire.WriteAnswer(p, ask.request, status));
				break;
			case PacketTypes.garageUpgrade:
				var id = packet.Read<string>();
				if (!approvals.Remove((slot, id)))
				{
					result.Fail($"#{slot} unlocked {id} without the host's approval.");
					break;
				}
				hostUpgrades.Add(id);
				foreach (var other in bots.Where(b => b.Joined && b.Slot != slot)) Send(other.Slot, PacketTypes.garageUpgrade, p => p.Write(id));
				break;
		}
	}

	private void Send(int slot, PacketTypes type, Action<Packet> write)
	{
		using var packet = new Packet((int)type);
		write(packet);
		host.Send(slot, packet.FrameBuffer, packet.FrameLength);
	}

	private sealed class Bot
	{
		private readonly PurchaseSoak soak;
		private readonly NetClient client;
		private readonly List<Action<int>> deferred = new();
		private readonly Dictionary<int, (string id, long cost)> asks = new();
		private int frame;
		private int nextRequest;
		private int nextCar;

		public Bot(PurchaseSoak soak, bool isHost)
		{
			this.soak = soak;
			var options = isHost
				? SimOptions.Perfect
				: new SimOptions { LatencyMs = soak.options.LatencyMs, JitterMs = soak.options.JitterMs, LossRate = soak.options.LossRate, Seed = soak.random.Next() };
			var (clientEnd, hostEnd) = SimLink.CreatePair(options, soak.Clock, isHost ? "loopback" : "bot");
			client = new NetClient(Version, soak.Clock);
			Economy = new EconomyClient(p => client.Send(p.FrameBuffer, p.FrameLength));
			Economy.RejectedTxn += (number, _) => Linker.Refused(number);
			Economy.Changed += () => Linker.Processed(Economy.Processed);
			Inventory = new InventoryClient<SoakItem>(InventoryKind.Items, i => i.UID, p => client.Send(p.FrameBuffer, p.FrameLength), soak.Clock)
			{
				AutoFlush = false
			};
			Inventory.Changed += () =>
			{
				var (add, remove) = InventoryDiff.Compute(Game, Inventory.Mirror.Desired());
				foreach (var uid in remove) Game.Remove(uid);
				foreach (var item in add) Game[item.UID] = item;
			};
			client.Connected += () => Inventory.RequestSnapshot(true);
			client.FrameReceived += OnFrame;
			client.Connect(clientEnd, isHost ? soak.host.HostKey : null);
			soak.host.Accept(hostEnd);
		}

		public int Slot => client.SlotId;
		public bool Joined;
		public EconomyClient Economy { get; }
		public InventoryClient<SoakItem> Inventory { get; }
		public PurchaseLinker Linker { get; } = new();
		public Dictionary<long, SoakItem> Game { get; } = new();
		public Dictionary<int, int> Parking { get; } = new();
		public Dictionary<int, int> CarsBought { get; } = new();
		public HashSet<string> Upgrades { get; } = new();
		public HashSet<long> SeenFromHost { get; } = new();
		public HashSet<int> PaintsSeen { get; } = new();
		public Dictionary<int, int> Paints { get; } = new();

		private void OnFrame(NetFrame frame)
		{
			using var packet = new Packet(frame);
			switch ((PacketTypes)frame.Type)
			{
				case PacketTypes.econBalance:
					Economy.HandleBalance(packet);
					break;
				case PacketTypes.invOp:
					var op = InventoryWire.ReadOp<SoakItem>(packet);
					var before = Inventory.Mirror.Version;
					Inventory.HandleOp(op.version, op.ack, op.op, op.item);
					// Only a change that was applied counts (the sender's own acknowledgement carries the item too).
					if (op.op == InventoryOp.Add && Inventory.Mirror.Version > before) SeenFromHost.Add(op.item.UID);
					break;
				case PacketTypes.invSnapshot:
					var snapshot = InventoryWire.ReadSnapshot<SoakItem>(packet);
					foreach (var item in snapshot.items) SeenFromHost.Add(item.UID);
					Inventory.HandleSnapshot(snapshot.version, snapshot.ack, snapshot.items);
					break;
				case PacketTypes.carPaint:
					var car = packet.ReadInt();
					var color = packet.ReadInt();
					Paints[car] = color;
					PaintsSeen.Add(color);
					break;
				case PacketTypes.garageUpgrade:
					Upgrades.Add(packet.Read<string>());
					break;
				case PacketTypes.purchaseAnswer:
					var (request, status) = PurchaseWire.ReadAnswer(packet);
					if (!asks.TryGetValue(request, out var ask)) break;
					asks.Remove(request);
					if (status != TxnStatus.Applied) break;
					// Charged on the host: the game unlocks it (its own debit is not recorded) and tells everyone.
					Upgrades.Add(ask.id);
					soak.result.UpgradesBought++;
					using (var unlock = new Packet((int)PacketTypes.garageUpgrade))
					{
						unlock.Write(ask.id);
						client.Send(unlock.FrameBuffer, unlock.FrameLength);
					}
					break;
			}
		}

		public void Tick(bool act)
		{
			frame++;
			client.Tick();
			if (Joined && act && Economy.Synced && soak.random.NextDouble() < soak.options.ActionsPerSecond * soak.options.TickMs / 1000.0) Act();
			Flush();
		}

		/// <summary>End of the frame, as the mod does in LateUpdate.</summary>
		private void Flush()
		{
			var txn = Linker.TxnFor(frame);
			Inventory.Flush(txn);
			foreach (var send in deferred) send(txn);
			deferred.Clear();
		}

		private bool Debit(long amount)
		{
			if (!Economy.Record(-amount, 0)) return false;
			soak.amounts[(Slot, Economy.LastNumber)] = -amount;
			Linker.Debit(frame, Economy.LastNumber);
			return true;
		}

		private void Act()
		{
			var roll = soak.random.NextDouble();
			var price = soak.random.Next(40, 260);
			if (roll < 0.12)
			{
				var amount = soak.random.Next(50, 400);
				if (Economy.Record(amount, 0)) soak.amounts[(Slot, Economy.LastNumber)] = amount;
				return;
			}
			if (Economy.Money < price) return;
			if (roll < 0.55)
			{
				// Shop items: the game may add them before or after the debit within the frame.
				var count = soak.random.Next(1, 3);
				var before = soak.random.Next(2) == 0;
				var items = Enumerable.Range(0, count).Select(_ => new SoakItem { UID = soak.nextUid++, ID = "bought" }).ToList();
				if (before) AddItems(items);
				if (!Debit(price))
				{
					// The game would not have sold them.
					if (before) foreach (var item in items) RemoveItem(item);
					return;
				}
				if (!before) AddItems(items);
				foreach (var item in items) soak.purchasedItems[item.UID] = (Slot, Economy.LastNumber);
				soak.result.ItemsBought += count;
			}
			else if (roll < 0.75)
			{
				if (!Debit(price)) return;
				var color = soak.nextPaint++;
				var car = Slot;
				soak.paints[color] = (Slot, Economy.LastNumber);
				Paints[car] = color;
				deferred.Add(txn =>
				{
					using var packet = new Packet((int)PacketTypes.carPaint);
					packet.Write(car);
					packet.Write(color);
					packet.Write(txn);
					client.Send(packet.FrameBuffer, packet.FrameLength);
				});
				soak.result.Paints++;
			}
			else if (roll < 0.92)
			{
				if (!Debit(price)) return;
				var car = Slot * 100_000 + ++nextCar;
				var txn = Economy.LastNumber;
				Parking[car] = txn;
				CarsBought[car] = txn;
				Linker.OnRefused(txn, () => Parking.Remove(car));
				soak.result.Cars++;
			}
			else
			{
				var id = UpgradeIds[soak.random.Next(UpgradeIds.Length)];
				if (Upgrades.Contains(id) || asks.Values.Any(a => a.id == id)) return;
				var request = ++nextRequest;
				asks[request] = (id, price);
				soak.result.UpgradeAsks++;
				using var packet = new Packet((int)PacketTypes.purchaseAsk);
				PurchaseWire.WriteAsk(packet, request, PurchaseKind.Upgrade, id, 1, price);
				client.Send(packet.FrameBuffer, packet.FrameLength);
			}
		}

		private void RemoveItem(SoakItem item)
		{
			Game.Remove(item.UID);
			Inventory.Local(InventoryOp.Remove, item);
		}

		private void AddItems(List<SoakItem> items)
		{
			foreach (var item in items)
			{
				Game[item.UID] = item;
				Inventory.Local(InventoryOp.Add, item);
			}
		}
	}

	private sealed class Sink : ILogSink
	{
		private readonly PurchaseSoakResult result;
		private readonly ILogSink next;

		public Sink(PurchaseSoakResult result, ILogSink next)
		{
			this.result = result;
			this.next = next;
		}

		public void Write(LogLevel level, string message)
		{
			if (level >= LogLevel.Error)
			{
				result.LogErrors++;
				result.Fail("Logged error: " + message);
			}
			next?.Write(level, message);
		}
	}
}