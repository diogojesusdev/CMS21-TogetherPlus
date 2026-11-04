# CMS21 Together+ — Implementation Plan

Fork of [Fozkais/CMS21-Together](https://github.com/Fozkais/CMS21-Together) (MIT) that integrates the
"Together Fixes" improvements, then goes further: host-authoritative state, per-component locking,
a shared money ledger, crash-safe saves and support for **8 players**.

**Decisions so far**

| Topic | Decision |
|---|---|
| Base version | Tag `Release-0_4_16hf3`, plus selected parts of 0.4.17 (Steam relay, secure game-load detection) |
| Max players | 8 (configurable, hard cap 8 for now) |
| Topology | Listen server: the host plays and is the single source of truth |
| Money/scrap | Global and shared, owned by the server ledger |
| XP / level / skills | Stay per-player (current behaviour, confirmed: least sync surface) |
| Minigames | Run locally for the lock holder; only the final result is synced (TogetherFixer model) |
| Interaction rule | One player per component at a time, enforced by server locks |
| Compatibility | Protocol break: only Together+ clients can join a Together+ host |

---

## 1. Sources we build on

| Source | License | How we use it |
|---|---|---|
| `Fozkais/CMS21-Together` @ `Release-0_4_16hf3` | MIT | Base code |
| `Fozkais/CMS21-Together` 0.4.17 commits (`97b7d7d` GameLoadHook, Steam transport changes, DirectIP rename) | MIT | Cherry-pick selectively. **Do not** take the 0.4.17 inventory rework or new SaveSystem (issue #105: saves deleted, no migration) |
| `Fozkais/CMS21-Together` `Dev` branch | MIT | Ideas only: PacketRouter, server-side Stats/Shop handlers, PricingCalculator, logging gateway |
| [`Stevii001100/Fix-For-CMS21-Together`](https://github.com/Stevii001100/Fix-For-CMS21-Together) (published source of the Nexus "Together Fixes" mod #286, targets 0.4.16hf3) | Custom, **non-commercial**. Credit "TogetherFixer", link the original repo, mark changes as modified, never sell | Port features and code with attribution |
| [`bellaouzo/CMS2021_Enhanced`](https://github.com/bellaouzo/CMS2021_Enhanced) | MIT | Server fluid tracking, late-join live balance, dyno/alignment/headlamp/car-wash sync, door/crouch sync |

> Licensing consequence: once TogetherFixer code is merged, the combined distribution must stay
> non-commercial. Add `NOTICE.md` and `LICENSES/` (MIT + TogetherFixer) in Phase 0 and keep
> per-file attribution headers on ported files.

---

## 2. Audit of the current code (what breaks today)

Verified against the source. Phase 0 re-checked every row on `plus/main` (hf3 + the Phase 0 ports). All of them hold, except A15, which behaves differently on hf3 (see the row). A21–A23 were found during Phase 0.

| # | Where | Problem | Impact |
|---|---|---|---|
| A1 | `Shared/Packet.cs` | Every payload is serialized with `BinaryFormatter` | Slow and bloated, plus **remote code execution** risk: a malicious peer can send a gadget payload |
| A2 | `Shared/Packet.cs` | `List<byte>` buffer, `InsertRange(0, …)` to prepend the length, new allocations per packet | CPU/GC churn that grows with player count |
| A3 | `ServerSide/Transports/TCPConnection.cs`, `ClientSide/Transports/ClientTCP.cs` | `stream.BeginWrite` can be called again before the previous write completes; there is no send queue | Frames can interleave and corrupt the TCP stream, causing "random" desyncs under load |
| A4 | TCP receive path | Fixed 4 KB buffer, fragile `Reset`/`_packetLenght <= 1` logic, no maximum packet size | Mis-framed packets; memory DoS |
| A5 | `ServerSide/Server.cs` UDP | Client id is read from the payload; `clients[_clientId]` throws on invalid ids (caught on hf3), and the first datagram for a slot binds its UDP endpoint | Spoofing: any peer can claim or hijack another slot's UDP endpoint; log spam |
| A6 | Whole server | The server mostly relays: clients decide part, car and inventory state, and the last writer wins | Two players editing the same thing corrupts state |
| A7 | `ClientSide/Data/Player/Stats.cs` | Money synced as deltas, with echo suppression via the `listentoAddMoney` flag | Drift and double-counting (issues #94, #38) |
| A8 | Server disconnect | `connectedClients` entries are never removed and no "player left" event is sent | Ghost avatars; "user with same name already connected" on rejoin |
| A9 | `ServerHandle.ReadyPacket` | Trusts the player id inside the payload | Any client can toggle other players' ready state |
| A10 | `Shared/SavesManager.cs` (hf3) | Vanilla profile and `save_N.cms21mp` JSON are written separately with `File.WriteAllText`: no temp file, no backup, no checksum | Torn or mismatched saves (issues #40, #52, #92, #105) |
| A11 | e.g. `PartUpdateHooks.HandleDoMount` | Sync relies on timers (`WaitForSeconds` polling, `counter <= 16`) | Together Fixes' author names this as the root cause of most desyncs |
| A12 | Many hooks | `carLoaderID = gameObject.name[10] - '0' - 1` | Fragile; breaks if names change or exceed 9 |
| A13 | `ServerSend.SendDataToAll` | Iterates every slot, including disconnected ones | Wasted work; errors on stale sockets |
| A14 | `MainMod.MAX_PLAYER = 4`, `UILobby` | Player cap is a compile-time constant and the lobby has 4 hard-coded slots | Blocks 8 players |
| A15 | `ServerHandle.ItemPacket`/`GroupItemPacket` (`resync`) | On hf3 the reply goes through `SendDataToAll(fromClient, …)`, which sends to everyone **except** the requester, and clients have no `resync` case in `HandleItem`/`HandleGroupItem`. (0.4.17's `InventoryResyncRequest` sends to all instead.) | The resync after `Inventory.Load` never reaches the requester (stale inventory), and every other client receives item-count × packets for nothing |
| A16 | `TCPConnection.Disconnect` | The host's own connection (id 1) dropping shuts down the whole server | A transient error ends the session |
| A17 | `SavesManager.SaveModSave` | The player loop does `break` on the first slot without a connected client, instead of `continue` | Other players' XP, level and position are silently **not saved**. On hf3 this is mostly masked by A8: entries are never removed and slots fill lowest-first, so gaps are rare. Fixing A8 (Phase 1) unmasks it, so the A17 fix lands in Phase 1, in the same change as the A8 fix |
| A18 | `PlayerInfo.UpdateSkill` | Every skill unlock triggers a full, non-atomic save write | Extra write load, and more chances of a torn save |
| A19 | `UserData.playerGUID`, `ServerHandle` | Player identity is a random GUID made on each install, sent by the client and trusted by the server | Progress is lost on reinstall; one client can claim another's progress |
| A20 | `SavesManager.SAVE_FOLDER_PATH` | The sidecar lives in `<game>\Mods\togetherMod\saves`, while the vanilla profile is in `%USERPROFILE%\AppData\LocalLow\...\Save` | Reinstalling or verifying the game can delete one file without the other |
| A21 | `SavesManager.SaveModSave(saveIndex)` | Player stats are written into `ModSaves[currentSaveIndex]`, but the file written is `ModSaves[saveIndex]` | Latent: hf3's call paths keep the two equal while hosting (`LoadSave` sets `currentSaveIndex` right after `StartServer`). Any new caller that saves another slot while hosting (e.g. autosave or slot management) would put progress in the wrong slot |
| A22 | `ServerSide/SteamSocket.cs`, `SteamworksUtils.GetClientFromConnection` | `OnConnectionChanged` overrides the base and ignores `ClosedByPeer`/`ProblemDetectedLocally`, so `OnDisconnected` never runs, the connection is never closed and the slot stays `isConnected`. `GetClientFromConnection` uses `First()`, which throws for unknown connections (its null check is dead code) | A Steam client that crashes or times out leaks its slot until the host restarts; an unknown connection throws in `OnMessage` |
| A23 | `ClientSide/Data/Player/Inventory.cs` (`HandleItem`/`HandleGroupItem` remove) | `modItems.Remove(item)` is called with the deserialized copy, and `ModItem`/`ModGroupItem` don't override `Equals`, so nothing is removed | The client's tracking list keeps stale UIDs; a later add of the same UID is skipped as a "duplicate" |

Invariant to keep: server handlers already run on Unity's main thread (`ThreadManager`), so `ServerData`
is effectively single-threaded. All new server services keep that rule; only I/O runs off-thread.

---

## 3. Target architecture

```
Client (Harmony hooks)                     Host / Server (main thread)
 ├─ ActionGate ──Request(lockToken)──────▶ ├─ PacketRouter (validate sender, bounds, schema)
 │                                         ├─ ResourceLockManager  (leases, fencing tokens)
 │                                         ├─ EconomyLedger        (money/scrap, idempotent txns)
 │                                         ├─ WorldState           (cars/parts/inventory/tools/jobs + versions)
 │◀──Event(seq, absolute state)─────────── ├─ EventLog / Broadcaster (monotonic seq)
 ├─ StateApplier (ApplyingServerState scope)├─ PersistenceService  (atomic, versioned, backups)
 └─ DesyncDetector (periodic state hash) ─▶└─ SessionManager       (join snapshot, heartbeat, cleanup)
```

**Core rule:** any change to shared state is a **request**. The server validates it (sender, lock
held, funds, entity version), commits it to `WorldState`, then broadcasts an **event** carrying a
monotonic `seq`. Every client, including the requester, applies the event. Clients never mutate
shared state locally; a short visual prediction is allowed only if it can be rolled back.

Server services contain no Unity or IL2CPP types, so they can be unit-tested headlessly.

### 3.1 Network layer (Phase 1)
- **Serializer:** hand-written `Write(NetWriter)` / `Read(NetReader)` on each DTO (no reflection, no
  `BinaryFormatter`). Strings are length-prefixed UTF-8; collections are count-prefixed with hard limits.
- **Frame:** `len:u32 | type:u16 | flags:u8 | seq:u32 | payload`. Max frame 1 MB; anything larger
  disconnects the sender. Large blobs (join snapshot) go out in 64 KB chunks with gzip and CRC32.
- **Pooled buffers** (`ArrayPool`-style), with space for the length reserved up front (no `InsertRange`).
- **Per-connection send queue** with exactly one write in flight (fixes A3).
- **Channels:** reliable-ordered (TCP / Steam reliable) for state; unreliable for movement.
  Movement at 20 Hz with dead-band, position quantized, rotation compressed with smallest-three;
  clients interpolate through a 100 ms buffer. Movement is only sent to players in the same scene.
- **Handshake:** protocol version + mod version + game version + DLC content hash. The server
  assigns the player id; payload ids are never trusted (fixes A5, A9).
- **Heartbeat:** every 1 s, timeout 10 s. On timeout or disconnect: release locks, remove from
  `connectedClients`, broadcast `PlayerLeft`, despawn avatar (fixes A8; port of TogetherFixer
  `ConnectionHeartbeat`, `PlayerLeavePacket`, `ClientDisconnectFix`, `LobbyLeaveCleanup`).
- **Handler isolation:** each handler is wrapped in try/catch, so a bad packet is logged and dropped.
  Malformed *framing* disconnects the offender only, never the server (fixes A16).
- Steam relay keeps the `SocketManager`; slot checks use the configurable max.

**As built (Phase 1), where it differs from the design above:**
- **Serializer:** a whitelisted reflection serializer (`NetSerializer`) instead of hand-written methods
  per DTO. Only registered DTO types are allowed, no type names on the wire, max depth 32, and every
  count is bounded by the bytes that remain. Round-trip and hostile-bytes tests cover every DTO.
- **Framing:** the frame header is as designed, with no CRC (TCP and Steam already check integrity).
  Messages over 64 KB are gzipped and split into chunks; max message 64 MB, max wire frame 1 MB.
  Transport-level frames (ping, chunk, handshake) use type ids `0xFFF0` and up.
- **Transports:** the old UDP socket is gone. Direct IP uses `TcpLink` (reader and writer threads, so
  frames are still received while the main thread is busy). Steam uses `SteamTransport` with manual
  callbacks. The host's own player uses an in-process loopback link that always gets slot 1 (it is
  authenticated by a random host key). Steam hosting never opens a TCP port. Send queues have a
  Normal and a rate-limited Bulk priority (2 MB/s); unreliable frames are dropped when a link backs up.
- **Handshake:** `Hello`/`Welcome` with magic, protocol version and mod version; it replaces the old
  greeting. Game version and DLC content hash move to Phase 5 (join snapshot).
- **Heartbeat:** ping every 1 s, timeout 15 s, only on links without their own liveness. Steam
  liveness comes from Steam itself. Pings are sent from the main thread, so after a local stall of
  more than 3 s (loading a save or a scene) silence is measured from when the stall ended. Otherwise
  everyone loading together would drop each other. Load-aware "busy" frames come in Phase 5.
- **Movement quantization and interest management** are deferred to Phase 7.
- **A19:** direct-IP players get a **client-generated** per-save secret token, and the host stores
  only its SHA-256. A host-issued token would orphan the profile if the reply was lost or the host
  crashed before saving. Known limits: an unclaimed hf3 GUID profile is claimed on first use (TOFU),
  and a malicious host that knows another save's id could ask for that save's token. The client
  answers only one identity request per connection.
- **A22:** covered by `SteamTransport`. Closed or broken Steam connections go through the normal
  disconnect path, and messages from unknown connections are logged and dropped.

### 3.2 Resource locking (Phase 2) — "one player per component"
**Resource keys** (hierarchical):

| Key | Covers |
|---|---|
| `car/{L}` | Whole-car operations: move, delete, lift, wash, paint, sell, test drive, park |
| `car/{L}/part/{type}/{key}/{idx}` | Mechanical part (engine, suspension, other, driveshaft) |
| `car/{L}/body/{id}` | Body part |
| `stand/{1\|2}/part/{key}` | Engine-stand parts |
| `tool/{ModIOSpecialType}` | Station: engine crane, stand 1/2, spring clamp, tire changer, wheel balancer, welder, paint shop, dyno, battery charger, brake lathe, alignment, oil bin… |
| `lifter/{L}`, `park/{slot}`, `job/{id}` | Lift, parking slot, job |
| `item/{UID}`, `group/{UID}` | Inventory item being used or mounted |

**As built (Phase 2).** Code: `Shared\Locks\` (headless, unit-tested), `ServerSide\Server.cs` /
`ServerHandle.cs` (host), `ClientSide\Data\Garage\ActionGate.cs` / `LockGates.cs` (client).
Phase 2 uses the `car`, `part`, `body`, `stand`, `stand/part` and `lifter` keys. The `tool`, `park`, `job`,
`item` and `group` keys are defined but not gated yet.

- **Modes:** exclusive per key. A key conflicts with itself, its parent and its children, so
  `car/0` and `car/0/part/…` exclude each other. A request carries up to 8 keys, granted all or
  nothing. Everything runs on the host main thread, so it cannot deadlock.
- **Policy:** component keys (part, body, item, group) are one set per player. A newly granted
  component set auto-releases the player's previous one. Each player can hold at most 32 grants.
- **Leases:** 10 s, expired every 250 ms. The client renews every 3 s while the player keeps
  acting and releases after 6 s idle. Locks are freed on disconnect, heartbeat timeout (15 s) and
  scene change: the host calls `PlayerChangedScene`, and the client drops its own entries before
  it sends the scene packet.
- **Protocol:** `lockRequest(token, keys)` → `lockReply(granted|denied, holder, epoch)`, plus
  `lockRenew` and `lockRelease`. The host broadcasts `lockState(key, holder)` and sends a
  `lockSnapshot` on join, so every client knows who holds what.
- **Fencing (the real guarantee):** part and body writes carry the writer's lock token as their
  last field (`ActionGate.Stamp`). The host's `AcceptWrite` rejects a write when:
  - another player holds the key, its parent or a child;
  - the token is stale (< 0);
  - the token is positive but another player wrote the key since.

  Token 0 (no lock taken) is accepted only while the key is free. A rejected write is dropped,
  and the host sends its stored state back with `resync=true` (at most once per key per second),
  so the writer's view converges. An accepted write renews the writer's lease.
- **Client gates are optimistic and deny-only.** The design had them request, cancel and replay,
  but replaying is unsafe for most entry points, so they work like this instead:
  - Each client keeps a lock table from the broadcasts.
  - Prefixes on `MountObject.Action`, `PartScript.ActionUnMount`, `PartScript.ActionMount`,
    `CarLifter.Action`, `CarLoader.TakeOffCarPart` and car move (`ChangePosition`) cancel the
    action when someone else holds the key, its parent or a child. A `CarLoader.CanTakeOffCarPart`
    postfix does the same for body parts. A cancelled action shows "In use by *Name*".
  - Otherwise the action runs, and the lock is requested in parallel.
  - The gates are bypassed while not connected and while host state is being applied.
- **Echo removal:** host state is applied inside `ActionGate.Apply()`, and the send hooks return
  early there, so remote changes are no longer re-broadcast. Async mount callbacks that fire
  outside the scope can still echo, but harmlessly: the host rejects them while someone else
  holds the lock, and resync is idempotent when states match.
- **Not gated yet:**
  - FastMount/FastUnmount, delete car, engine crane, oil, paint, wash, welder and parking:
    these move to Phase 6 with the tool/car port.
  - Station (`tool/*`) locks: Phase 6.
  - Inventory items: Phase 5.

  These paths keep hf3 behaviour, but the host still fences their part and body writes.
- **Residual risks:**
  1. Two players who start on the same free part within about one RTT both act locally. The
     host accepts the first write and rejects and resyncs the second. Host state stays
     consistent, but the loser's local inventory may already have changed, so an item can be
     duplicated or lost. Phase 5 (authoritative inventory) closes this.
  2. Locks on a deleted car linger for up to one lease (10 s).
- **Exit test (done):** `BotHarness --locks` and `Tests\LockSoakTests.cs` run 2–8 bots over the
  real net stack on simulated links with latency, jitter and loss. The bots race for hot keys,
  freeze mid-lock and change scenes. Every tick the soak checks:
  - the host never grants conflicting keys to different players;
  - a client that believes it holds a lock matches the host;
  - no two live bots hold conflicting keys;
  - a write sent under a held lock is never rejected;
  - a frozen player's locks are freed within a lease plus about 1 s.

  The run ends with a leak check. The soak is mutation-checked: disabling conflict detection or
  lease expiry makes it fail.

### 3.3 Global economy (Phase 3)
- **Scope:** money **and** scrap are one shared wallet for the whole session (campaign only; Sandbox
  keeps vanilla behaviour). XP, level and skills stay per-player.
- **Plan vs. as built.** The plan made every debit a client request that the host approves before
  the game changes anything. CMS21 changes money deep inside its own purchase, sell and job code,
  so diverting every call site is fragile. As built, the economy is **predictive**:
  1. The game applies a change locally, as in vanilla. Postfixes on `AddPlayerMoney`/`AddPlayerScraps`
     record it as a transaction `{number, money delta, scrap delta}`. `number` is monotonic per
     connection (starting at 1) instead of a Guid. It is cheaper, and gaps or replays are detectable.
  2. The host's `EconomyLedger` applies transactions in order, **exactly once**: a number at or below
     the last one processed for that slot is ignored and counted as a duplicate.
  3. After every change, each player gets a unicast **absolute** balance
     `{money, scrap, ledgerSeq, processedUpTo, status}`. A client shows host balance + its own
     still-pending transactions (number > processedUpTo), clamped to `[0, int.MaxValue]`.
  4. Rejected transactions (`Insufficient`, `Invalid`, `NotReady`) are reported only to the player who
     sent them, who gets a toast ("Not enough shared money" / "The host refused that transaction",
     at most once per 2 s). The game value then snaps back to the host balance.
- **Wallet reconcile.** `Wallet.Pump` writes the predicted value into `GlobalData` whenever a balance
  arrives, and at least every 1 s. Any game-side change the hooks miss is overwritten rather than
  drifting. It writes inside `ActionGate.Apply()`, so the write is never recorded as a new transaction.
- **Echo suppression (fixes A7).** The `listentoAddMoney` flags and the delta `StatPacket` are gone.
  All client packet dispatch runs inside `ActionGate.Apply()`. Remote-applied coroutine blocks
  (garage upgrade unlock, the spawn/state apply in `SpawnPlayer`) have their own `Apply` scopes and
  never span a `yield`. Remote state is therefore never re-charged or re-credited.
- **Source of truth.** The host initialises the ledger once, from its own `GlobalData`, when its game
  is ready. The session save takes `money` from the ledger. Scrap is persisted through the host's
  vanilla profile, which the wallet keeps equal to the ledger. The spawn packet carries the ledger
  balance, and a joining player gets the live balance as soon as it connects (`PlayerJoined`).
- **Sanity bounds.** Per transaction: |money| ≤ 100 M, |scrap| ≤ 10 M, non-zero; a balance can never
  go below 0 or above `int.MaxValue`. Credits are client-reported (bounded, not recomputed from job
  data); host-side payout computation comes with the job service in Phase 6.
- **Known deviations.**
  - *Optimistic debits (temporary, must not ship).* If two players spend the last money at the same
    time, the host refuses the second transaction. As of Phase 3 that player already has the item,
    uncharged, and sees "Not enough shared money". The ledger stays consistent and never goes
    negative, but the item is "free". **Phase 6 work item:** a refused purchase is rolled back
    locally (remove the item, despawn the car, revert the upgrade); see §3.8 and the Phase 6 row.
  - *Pending transactions are lost on disconnect.* Numbering restarts on reconnect and the host's
    balance wins. A client keeps at most 512 pending transactions; beyond that, changes are dropped
    and overwritten by the next balance.
- **History.** The ledger keeps the last 200 entries in memory (slot, deltas, status, balance after).
  Saving it with the host save and showing it in the debug menu moves to Phase 4/6.
- **Exit test (done).**
  - `Tests\EconomyTests.cs` covers the ledger, the wire format and the client/host protocol.
  - `BotHarness --economy` and `Tests\EconomySoakTests.cs` run bots that spend and earn concurrently
    over the real net stack on simulated links. The bots replay transactions and freeze mid-session.
    Profiles: 2 players for 60 s; 8 players for 180 s at 80 ms + 40 ms jitter with 5% loss; 4 players
    for 90 s at 250 ms + 150 ms jitter with 20% loss and little money. The soak checks that:
    - every transaction is applied exactly once, with no gaps;
    - replays never change the wallet;
    - the ledger equals the sum of applied deltas and is never negative;
    - every balance a bot receives matches the host's expectation;
    - in quiet windows and at the end, every client's economy, game value and pending count match the
      ledger.
  - A typical 8-player run: about 11,000 transactions, of which about 10,500 are applied and 580
    refused; all 331 replays are ignored. The soak is mutation-checked: removing the dedupe, keeping
    acknowledged pending transactions, allowing negative balances, applying replays, reporting a
    wrong processedUpTo or dropping pending transactions early each make it fail.

### 3.4 Authoritative world state (Phase 5)
- `WorldState` holds cars → parts/body/fluids/tuning/paint, inventory (server-issued UIDs only),
  tools/stations, lifts, parking slots, jobs, garage upgrades. **Each entity has a `version`.**
- A mutation must carry `lockEpoch` and `baseVersion`; a mismatch is rejected and the server sends
  that entity's state back to the requester for resync.
- **Event-driven sync** replaces timers (A11): hook concrete state transitions (bolt mount/unmount
  finished, `PartScript` state, `CarLoader` part switch) instead of polling with `WaitForSeconds`.
- `CarLoaderRegistry` maps `CarLoader` instances to stable ids (fixes A12).
- **Join / late join / rejoin:** the server builds a snapshot of `WorldState`, ledger and locks,
  then sends it chunked with checksum and `snapshotSeq`. The client applies it and replays buffered
  events with `seq > snapshotSeq`. This replaces Together Fixes' "host saves and resends" and allows
  rejoin without restarting the game.
- **Scene changes:** while a client is outside the garage, events are queued and replayed on return
  (TogetherFixer `SceneReplayQueue`, `SceneReturnCarSync`).
- **DesyncDetector:** every 10 s clients send per-domain hashes (money, inventory, per-car parts).
  On mismatch the server pushes that domain's authoritative state and logs the diff.

**As built (Phase 5).** Code: `Shared\World\` (headless, unit-tested), the inventory hooks in
`ClientSide\Data\Player\Inventory.cs`, the host side in `ServerSide\`. A general `WorldState` with a
version on every entity was not built. Each domain got the model it needs:
- **Inventory (items and item groups)** is host-authoritative and versioned. Players send numbered
  changes keyed by UID (A23), which the host applies at most once and in order. Every applied
  change gets a version and goes to everyone, with each player's "processed up to" number.
  - A player keeps the host's set as of the last version, plus its own unprocessed changes on top
    (predictive, like the wallet). Once per frame, the game's inventory is brought to that set
    without echoing anything back.
  - A missed version, or a join, asks for a snapshot; the snapshot goes to the requester only (A15).
  - The host's save fills the shared inventory on its first garage load. Every later load, by any
    player, shows the shared set, so removed items don't come back from a profile.
  - Desync detector: every 10 s each player sends its version and set hash. The host compares them
    with its own hash at that version and sends its set back on a mismatch.
  - Exit tests: `InventorySyncTests` (A23 re-add, A15, gap → snapshot, divergence caught) and
    `BotHarness --inventory`. In the soak, 8 bots on 80+40 ms with 5% loss make about 7,900 changes
    in 5 minutes, including late replays and rejoins: every game converges, with 0 desyncs.
- **Lock races (Phase 2 follow-up).** When the host drops a part write that lost the race, it
  undoes the loser's matching inventory change: the unmounted part's new item (within 5 s), or the
  item taken for a mount (within the bolting time, up to 120 s). Matching is by item id and time.
  If both players took the same item for a mount, it stays out. `RaceReconcilerTests` runs 1,500
  interleavings with exactly one copy every time. Body panels are not covered: no reliable link
  between a panel and an inventory item is known.
- **Cars and parts** keep the hf3 event + resync model, now fenced by locks (Phase 2):
  - A write for a car the host doesn't know is ignored, so no ghost car entry is created.
  - A resync for a missing car is answered with nothing.
  - Lifter packets carry the absolute level; players step towards it, and a newer state cancels an
    older move. The host keeps each lifter's level and sends it with a car resync.
- **Join / late join / rejoin.** A lobby join works as before.
  - If a player passes validation while the host's game is running, they get their own start
    packet (with the current parking lot) and load straight in. On their first garage load they ask
    for every car (resync of car −1) and the order board.
  - The inventory snapshot, lock table, wallet and newest save copy come with the validation, and
    the other players spawn the newcomer when they enter the garage. Rejoining mid-game takes the
    same path.
  - Events that arrive while a player loads wait in coroutines until the game is ready. Resync
    answers come later on the same ordered channel, so the host's state wins.
- **Load-aware heartbeat:** already given by Phase 1. Pongs are sent from the link's receive
  thread, and Steam keeps its connections alive itself. `NetLayerTests` freezes a player for 3 s
  over TCP to check this.
- **Moved to Phase 6:** event-driven part sync (A11) and `CarLoaderRegistry` (A12) belong with the
  per-station rework of those hooks. A per-car parts hash built from `ServerData` was left out (the
  host only stores what players wrote, not a full baseline). Phase 6 builds it from the host's live
  `CarLoader` objects instead, and also covers the body-panel race.

### 3.5 Persistence and anti-corruption (Phase 4)
- **Only the host writes the shared save.** Clients never write it, and their own vanilla slots
  are never touched.
- **What is saved:** the vanilla profile (garage, cars, inventory, money, upgrades) plus the mod
  sidecar (per-player progress keyed by player identity, ledger tail, lock-free world extras such
  as engine stand 2). Fixes A18 (no save per skill unlock; mark dirty instead). The A17 loop fix
  lands earlier, in Phase 1, in the same change as A8.
- **Location:** move the sidecar from the game folder to
  `%USERPROFILE%\AppData\LocalLow\Red Dot Games\Car Mechanic Simulator 2021\TogetherPlus\Saves\`,
  next to the vanilla saves, with a one-time migration from `Mods\togetherMod\saves` (fixes A20):
  files are copied, the originals are kept, an existing target is never overwritten, and a marker
  file stops the migration from running twice.
- **One logical transaction** covers the vanilla profile and the mod sidecar. Vanilla writes the
  profile asynchronously *after* our save hook runs, so the commit has two steps:
  1. The save hook captures the players' progress on the main thread and writes the sidecar
     atomically as generation N, with the profile hash marked *pending*.
  2. A finalizer, pumped every frame, waits until vanilla's write has finished
     (`BaseSave.IsSaveInProgress` is false; 30 s timeout), hashes the profile, rewrites the sidecar
     with that hash and writes a backup bundle of generation N.
  The sidecar carries the profile's SHA-256; the bundle carries both files and both hashes.
- **Atomic write:** write `*.tmp`, `Flush(true)`, then `File.Replace(tmp, target, target.bak)`.
  Apply the same to `TogetherModManager` user data.
- **Save bundle (`.tpb`):** magic + version, an uncompressed header (save id, generation, UTC time,
  save name, profile file name/format, schema, the two file hashes), the SHA-256 of everything
  that follows, then the gzip-compressed profile and sidecar. Decompression is size-bounded.
  The save id must be 32 lowercase hex characters, since it is used as a folder name.
- **Rolling backups:** the last 10 generations per save under
  `...\TogetherPlus\Backups\{save id}\gen-{N}.tpb`, plus a copy of the current files before every
  restore.
- **Load validation:** when the host picks a slot, the files are checked: *Ok*, *Pending* (step 2
  never ran: accepted if the profile matches the newest bundle of that generation), *Missing*,
  *Corrupt* (hash mismatch or unreadable), or *NewerSchema*. Anything except Ok shows
  "Restore last good backup?" with the newest valid bundle, and **nothing is overwritten
  automatically**. A restore backs up the current files first.
- **Consistent snapshot:** capture state on the main thread (single-threaded, so it is consistent),
  then compress, hash and write on a background thread. Vanilla `ProfileData` serialization stays
  on the main thread. Saving is skipped during scene transitions or while a join snapshot is being sent.
- **Autosave** every 5 min (Together Fixes), when a player leaves, and when the host quits; debounced.
  Autosaves run on the host only, in the garage, never during a scene load or a join snapshot.
  A player leaving saves at once (sidecar only if the host is away from the garage, with the full
  save deferred). Exit to menu saves everything; quitting the game writes the sidecar only, because
  vanilla's asynchronous profile write can't finish during shutdown.
- **Schema version** and an explicit migration chain (the lesson of #105). Unknown newer versions
  are opened read-only, never overwritten.
- **Client save replicas,** so a session survives the host losing its files:
  - After each successful host save that clients are present for (autosave, player leave), the
    host sends the save bundle to every client: vanilla profile + sidecar, compressed, with the
    save generation, SHA-256 and save id.
  - The bundle goes in low-priority, rate-limited chunks (about 32 KB each, a small ack window,
    about 256 KB/s per client), never during a join snapshot or a scene load: the host pauses its
    sends, and a loading client withholds its acks.
  - Clients verify the hash and keep the last 3 bundles under
    `%USERPROFILE%\AppData\LocalLow\Red Dot Games\Car Mechanic Simulator 2021\TogetherPlus\Replicas\{save id}\`,
    written atomically. They never touch their own saves.
  - **Recovery path 1:** when a client joins, it offers the generation of its newest valid replica
    of the host's save id. If that replica is newer, or the host's file is missing or corrupt, the
    host gets a prompt to restore from it. Nothing is overwritten automatically; on accept the
    client uploads the bundle, the host verifies it, backs up its current files and restores
    (in the lobby; in game the bundle is stored with the host's backups, ready to restore).
  - **Recovery path 2:** "Restore from backup" on an empty save slot lists the bundles under
    `Replicas` and `Backups`; importing one into that slot lets a client host the session if the
    original host is gone.
  - A tampered or truncated bundle fails its hash and is rejected (on receive, upload and import).
  - Limit: the save made on exit to menu or quit can't reach clients, who are already gone, so a
    replica can be up to one autosave interval behind. The host's own backups cover that case.

**As built (Phase 4).** Code: `Shared\Persistence\` (headless, unit-tested), `Shared\SavesManager.cs`
and `Shared\PlusSaves.cs` (game side), the save UI in `ClientSide\Data\NewUI\`. Where it differs
from or adds to the design above:
- **Folders** under `...\TogetherPlus\`: `Saves\` (sidecars), `Backups\{save id}\` (10 per save),
  `Backups\_kept\{save id}\` (copies taken before a restore, an import, a delete or hosting damaged
  files; 20 per save), `Backups\_received\{save id}\` (copies uploaded by players; 3 per save) and
  `Replicas\{save id}\` (3 per save, at most 20 saves). A restore moves newer backups into `_kept`
  instead of deleting them, so the restored generation is the newest again.
- **Atomic writes** use one `MoveFileEx(REPLACE_EXISTING | WRITE_THROUGH)` rename, and the previous
  sidecar is *copied* to `.bak`. `File.Replace` is several steps; the save kill test found that a kill
  between them can leave no file at all. A missing or unreadable sidecar falls back to `.bak`, and
  the damaged file is kept aside. A slot that still can't be read is never written. `userData.ini`
  is written the same way.
- **Session start:** when the host picks a slot it is validated. *Ok*, *Unverified* and *New* go
  ahead. Before the session, the files as they are get a stamp and a backup if they lack one
  (`SessionStart`). Without this step, a crash after the game's write but before step 2, followed by
  a torn first save, fell back two generations in the kill test. The session then loads the checked
  file from disk, not the copy read when the menu opened. *Missing*, *Corrupt* and *Older* ask:
  "Restore backup", "Host anyway" or "Cancel". "Host anyway" keeps a copy of the current files and
  marks the session as played from damaged files: players then keep their good copies (no replicas
  are sent), and a joining player's copy triggers the restore prompt even if its generation is
  older. A save from a newer Together+ can't be hosted.
- **Commit timing:** step 2 waits until the game no longer reports saving and the profile has not
  changed for 0.5 s (or 3 s without a saving signal). It gives up after 60 s, and the generation
  stays unverified. Two hooks of one game save share a generation.
- **Autosave** calls `GarageLoader.Save` every 5 minutes. A player leaving saves after 2 s (players
  leaving together share one save). Within 30 s of a full save, or away from the garage, only the
  sidecar is written, and the full save follows in the garage. Skill unlocks only mark the save
  dirty (A18). Exit to menu starts the commit before the game's own save, and step 2 finishes in the
  menu. Quitting leaves the generation unverified, and the next session start backs it up.
- **Replicas:** 16 KB chunks, at most 4 unacknowledged, 128 KB/s per player, 2 players at a time; a
  newer save replaces one in flight. A player who joins gets the host's newest backup unless they
  already have that generation. The joining player sends its offer right after its validation. On
  "Restore" in the lobby, every player's live progress is reset to the restored values, then the
  players are asked to rejoin, so nothing from the replaced save is written back.
- **Recovery path 2** is "Restore a backup" on the new-save panel. It lists the newest copy of each
  save that isn't in a slot already (backups, session copies, copies from players, kept copies).
- **Ledger history:** the last 200 entries are saved in the sidecar and restored before the wallet
  starts (damaged entries are skipped). A difference from the game's money is only logged.
- **Kill test:** `BotHarness --save-kill 200` (also `build.ps1 -SaveKill`, and 12 kills in
  `SaveKillTests`) passed 200 kills on each of two seeds: 0 errors, 5 and 9 restores, over 400 generations each.

### 3.6 Scaling to 8 players (Phase 7)
- `MAX_PLAYER` becomes `ServerConfig.MaxPlayers` (default 8) in `UserData/TogetherPlus.json`.
- Lobby UI: dynamic list or scroll for up to 8 slots; distinct avatar colours and name billboards.
- Bandwidth estimate: 8 × 20 Hz × ~16 B of movement is about 2.5 KB/s per client before scene
  filtering; state events are small and event-driven.
- Per-frame packet processing budget on the host (e.g. 4 ms), with metrics in the debug overlay.
- Steam rich presence shows `n/8`.

**As built (Phase 7).**
- **Config:** `UserData/TogetherPlus.json` (`ServerConfig`), written with the defaults on first start: `MaxPlayers`
  2–8, default 8 (a bad value falls back to 8, logged). The host's `NetHost` takes it; players learn it from the
  handshake.
- **Lobby:** one row per possible player (4 rows as in hf3, up to 8 packed closer); a joining player's window resizes
  once the handshake tells it the session's size. Avatars get one colour per slot (8-colour palette, textures kept);
  name billboards as before. Steam rich presence shows `Together+ n/8` and the Steam player group.
- **Item UIDs:** the per-slot UID counters (hf3: 4) grow to 8 (`UidRanges`): slots 5–8 start at 40M–70M, or past every
  counter already in use, so no two slots ever issue the same UID; existing counters are kept.
- **Movement:** positions and rotations go out at most 20 times a second (they went out every frame) and are
  **unreliable** (they were reliable, so a slow link queued every old position). On the send queue each player's
  movement has a coalesce key (`CoalesceKeys`): a new frame replaces the one still waiting, in its place, so at most
  one movement frame per player and kind is ever pending, and it is never dropped for backlog (`SendQueue`; TCP and
  Steam both use it). Avatars glide to the newest position every frame (they moved only when a packet arrived) and snap
  on jumps over 5 m. **Interest management:** the host relays movement only to players in the same scene
  (`Interest`; an unknown scene still gets it).
- **Host budget:** `NetHost.Tick` dispatches for at most 4 ms (`BudgetMs`), at least 64 events, and leaves the rest for
  the next frame in order. Last/max tick time, budget hits, backlog and per-player RTT are in the Ctrl+Shift+L report.
- **Bounded-lag soak:** `BotHarness --movement` (`MovementSoak`): 8 players at 20 Hz over the *clumsy* profile (100 ms
  one way + 20 ms jitter, 5% loss) on links with too little bandwidth (8 KB/s, simulated with a real send queue in
  front, `SimOptions.Queued`) plus reliable traffic. Staleness of the newest position each player has of every other:
  p50 300 ms, p99 510 ms, flat over 15 minutes (first quarter p99 515 ms, last 510 ms); with enough bandwidth p50 is
  250 ms = send interval 50 ms + RTT 200 ms. The old path (reliable, uncoalesced) on the same links: p99 grows from
  12.5 s to 44 s in 2 minutes.
- **Tests:** `ScaleTests` (coalescing, never dropped but bounded, keys, bounded-lag soak and the old path's backlog,
  8 players and a 9th turned away, tick budget and order, config, UID ranges, interest).

### 3.8 Gameplay model: items, orders, progress

| Area | Scope | Behaviour in Together+ |
|---|---|---|
| Inventory and warehouse | **Shared** (same as hf3) | One shared pool, so there is nothing to "give": every item is visible to everyone. The server issues item UIDs. While a player holds or mounts an item, `item/{UID}` is locked, so it can't be used twice |
| Shop purchases (parts, tools, paint, tyres) | Shared | Predictive, as in the economy (§3.3): the purchase applies locally at once (vanilla feel) and its debit goes to the host as a numbered transaction that records what was bought (item UIDs, car, upgrade id). The purchase is **not** broadcast to other players until the host has applied the debit. If two purchases race for the last money, the host refuses the second, and that player's client **rolls the purchase back locally**: it removes the bought items (or despawns the car, or reverts the upgrade) and shows "Not enough shared money". Afterwards nobody owns anything the ledger did not charge for. (As built in Phase 6: items and group items carry the debit's txn and the host adds them only if it charged that debit; paint reverts to the stored colour; a car bought into parking is taken back; garage upgrades are pre-authorized, the host charges first. Car-to-parking and paint linking rely on the debit happening in the same frame: on the in-game checklist) |
| Car purchases (auction, barn, junkyard, salon) | Shared | Debit + reserve a free `park/{slot}` in one step, then broadcast the car spawn. No free slot means no charge. A refused debit despawns the car on the buyer's side (same rollback as shop purchases) |
| Customer orders (jobs) | Shared board | Accepting locks `job/{id}`, so only one accept wins. The car spawns for everyone and anyone can work on it, one part at a time. On hand-over the server computes the payout and pays it into shared money. (As built in Phase 6: accept and hand-over are asked of the host, the first ask wins, and the host pays the payout; see §3.7 As built) |
| Garage upgrades and customization | Shared | Bought with shared money; unlocked for everyone once the host applies the debit. A refused debit reverts the upgrade on the buyer's side |
| XP, level, skill points, unlocked skills | **Per-player** | Each player earns XP from their own actions (vanilla), including jobs they hand over. Stored in the host's save and restored when that player rejoins |

**Player identity:** Steam ID taken from the Steam connection (cannot be spoofed). On direct IP it falls back to a GUID plus a per-save secret token that the client generates and keeps; the host stores only its hash (see 3.1, "As built"). This fixes A19.

**Where progress lives:** only on the host. See 3.5. Clients play on a temporary in-memory profile built from the join snapshot; nothing is written to their own saves.

### 3.7 Together Fixes feature port matrix (Phase 6)

| Together Fixes feature (TogetherFixer file) | Together+ approach |
|---|---|
| Late join / start sync (`LateJoin`, `StartSyncClient`, `StartPacketWatcher`, throttles) | Replaced by the join snapshot (3.4); keep the throttling ideas |
| Rejoin, `HostSelfConnectRecovery`, `SaveReloadOnSelectFix` | SessionManager cleanup + snapshot; port the recovery logic |
| Heartbeat, kick, leave/disconnect cleanup | Port into the net layer (3.1) |
| Steam invite send/receive | Port |
| Scene change manager, return car sync, replay queue, whitelist, away car buy | Port onto the event log |
| Parking add/remove/move/rows, ghost cars | Rebuild as authoritative `park/{slot}` with locks |
| Job accept/finish, car sell | Server job service + ledger credits |
| Engine stand 1 **and** 2, crane, spring clamp, tire changer, wheel balancer, welder, battery charger, brake lathe, oil bin, car/interior wash, dyno, headlamp/wheel alignment, tuning (`*Sync.cs`, `desync_*.cs`) | Each becomes a lockable station; port the sync payloads to the new serializer |
| Minigames (headlamp alignment, wheel alignment, wheel balancer, dyno run, ECU/carb/gearbox tuning) | Station lock + local-only minigame + single final-state event; optional `SkipMinigames` config (default **off** = vanilla) |
| Paint, window tint, licence plates, tuning parts, wheel visuals | Port |
| Lift F4 reset, debug/recovery menu | Port; add lock viewer, ledger viewer, forced resync |
| Autosave every 5 min | Part of PersistenceService |
| Log spam reduction | Logging gateway with levels (idea from `Dev`) |
| Bulk sell, warehouse bulk move, examined sync | Port on top of inventory and ledger services |
| Workshop/DLC content sync, QoL mod compatibility (force-disable unsynced extra parking) | Port |
| *(Not in Fixes)* Transmission sync | Backlog |
| *(Enhanced)* Server fluid tracking, door/crouch sync, garage customization | Port (MIT) |
| *(Enhanced)* Salon/barn/junkyard/auction sync | Backlog, after 1.0 |

**As built (Phase 6).** Ported or rebuilt on the Phase 1–5 foundations:
- **Disconnect/rejoin, heartbeat, kick, late join, autosave, log spam:** already covered by Phases 0–5 (net core, join snapshot, PersistenceService, logging gateway). Nothing else was taken from the Fixes code for these.
- **Jobs and car sale ("jobs/sell via the ledger"):** accepting an order, handing it over and selling a car are asked of the host first (`purchaseAsk` kinds `JobAccept`, `JobEnd`, `CarSell`). The host's `Payouts` gate lets the first ask win and refuses later ones, so two players clicking at once never spawn a car twice or get paid twice. The host pays the payout or sale price itself (`EconomyServer.Grant`), and the player's own credit of that amount is not sent again (`Wallet.Expect`). A hand-over claims `car/{id}`, and the host ignores an `EndJob` it did not approve. If the order window closed before the approval arrived, the order is given back (`AcceptCancel`). XP is per player (§3.8), so only the player who hands an order over gets its XP (hf3 gave it to everyone). Tests: `PayoutsTests`.
- **Stations:** one player per station via deny-only gates (`StationGates`): tire changer, wheel balancer, spring clamp, welder, engine crane insert (`tool/{type}`), and per car the oil bin and car wash (exterior and interior). The engine crane pull and car delete claim `car/{id}`. FastMount/FastUnmount are gated like normal mounts; a group mount is never cut in half. **Not gated:** the paint shop (no safe entry point; its debit is purchase-linked instead) and parking (sync off since hf3).
- **Recovery menu:** as hotkeys, no new UI. Ctrl+Shift+R forces a resync (cars checked against the host at once, inventory snapshot, wallet). Ctrl+Shift+L writes the lock holders, the wallet and, on the host, the ledger tail and car-checker counters to the log.
- **Cars (from Phase 5):** `CarLoaderRegistry` (A12), every-frame mount watching (A11), the per-car desync detector, and exact body-panel race linking (see the Phase 6 row).
- **Purchase rollback:** see §3.8, Shop purchases.

**Deferred to Phase 6b** (user decision: ported before Phase 7, each behind its own switch; see the Phase 6b row):
- New station syncs: engine stand 2, battery charger, brake lathe, dyno, headlamp/wheel alignment, tuning and their minigames (`SkipMinigames`).
- Parking add/remove/move/rows as authoritative `park/{slot}`, ghost-car cleanup in parking, QoL extra-parking compatibility.
- Scene-change manager and return-car sync beyond the Phase 5 replay.
- Visuals: window tint, licence plates, tuning parts, wheel visuals, vanilla item paint.
- Bulk sell, warehouse bulk move, examined sync, workshop/DLC content sync, Steam invite send/receive, lift F4 reset.
- Bulk sell will need the same once-only host approval as car sale.

**As built (Phase 6b).** Every deferred feature is ported, each in its own file with the TogetherFixer attribution and
a modification note, each behind its own `[TogetherPlus]` switch (`FeatureCatalog`, 23 switches, all on except
`SkipMinigames`). Commits `d2575f0..48c5080`.
- **Switches:** the host's preferences become the session config, players follow it, and outside a session every
  feature is off. `GameFeature` patches by hand on its own Harmony instance, so a switched-off feature leaves no patch,
  and a missing target is undone, logged and turns off only that feature (`FeatureSwitchboard`). A generic feature
  channel carries feature messages; the host relays them only while the feature is on and keeps the last state per key
  for late joiners.
- **Stations:** battery charger, brake lathe (shared `ItemStationFeature`), dyno, headlamp and wheel alignment, ECU,
  carburettor and gearbox tuning. Minigames run locally; the final result is a per-car feature state; one player per
  station (`tool/batterycharger`, `tool/brakelathe`, `tool/dyno`, `tool/headlamp`, `tool/alignment`; tuning claims the
  car). Another player's action is applied with the inventory frozen (items move only through the shared inventory).
  `SkipMinigames` ends each minigame at its best result. **Engine stand 2** is synced (its unmounts were never sent:
  the part was looked up on stand 1 only); with the switch off it is parked out of reach, as in Together Fixes.
- **Money and items:** bulk sell is settled once by the host: only items still in the shared inventory are removed and
  paid (`BulkSale`, `EconomyServer.Grant`). The **warehouse** follows the host's (hf3 never synced it; a client's moves
  were lost). In-place item changes (paint, plates, wheel data, examined) are versioned inventory updates
  (`InventoryOp.Update`); `ModItem` colour and licence plate data were empty before (painted items arrived blank).
- **Visuals:** item paint, licence plates, wheel visuals, window tint, tuning parts (both ways), examined parts.
- **Garage:** F4 lift reset (cars through the authoritative lifter state). **Parking** is host-approved (`Payouts`
  kinds `ParkIn`/`ParkOut`): one player parks at a time and a space is emptied once; moves and unlocked rows are
  feature messages. **Ghost cars:** car reports list every loaded car; a car only the player has is deleted there, a
  missing one is sent again, after two reports in a row (`CarRoster`). **QoL mod extra parking** (`QolParkingCompat`):
  in a session the QoL mod's extra parking spawn is skipped and its local loaders are removed; spawn and delete hooks
  ignore its places (`placeNo` -10 to -999) even with the switch off. Without the QoL mod the feature does nothing.
- **Scene return** (`SceneReturnSync`): a car bought in the junkyard, auto salon or barn is taken out of the buyer's
  (stale) garage or parking list and handed to the host, which puts it in a free parking space for everyone only after
  it charged the buyer's debit (`AwayCarQueue`); a refused debit never ships the car, an undecided one is dropped after
  60 s, and a full parking lot refunds exactly what that debit charged, once (taken when the debit was applied). During a player's return
  load, cars from its own save that the host doesn't have in that loader are not sent (the hf3 path could bring back a
  sold car or overwrite a car another player put there) and are removed after the load. Fluid and mount hooks no
  longer send from away scenes. Everything else on return is the Phase 5 resync. Not built: Together Fixes' private
  "cut" of the returner's garage traffic and its replay queue (the Phase 5 resync replaces them).
- **Lobby:** Steam invites (rich presence connect string, join requests and `+connect` launch); workshop content sync
  (only workshop cars every player has are offered).
- **Also fixed:** review #1–#4 (§5.1) and `ProgressGuard` (the host never lowers a player's level or XP).
- **Tests:** `FeatureTests` (switch registry, isolation, config, relay, Steam connect, workshop cars, QoL places, away
  cars, refund), `BulkSaleTests`, `InventorySyncTests` (updates), `PayoutsTests` (parking), `CarDesyncTests` (ghost
  cars), `ParkSlotsTests`, `WalletRulesTests`, `ProgressGuardTests`, `SaveCommitTests`. Every feature is on the
  in-game checklist (§5.2).

---

## 4. Roadmap

Each phase ends with a build plus its exit tests. Order matters: locks, ledger and persistence
come **before** porting features, so ported features land on safe foundations.

| Phase | Scope | Exit criteria |
|---|---|---|
| **0. Setup** | Branch `plus/main` from `Release-0_4_16hf3`; cherry-pick 0.4.17 Steam + GameLoadHook; re-verify audit items; `dotnet build` script; NOTICE/LICENSES; logging gateway; `Tests` project (net472, no Unity) | Mod builds and loads in game; 2-player smoke test equals stock hf3 |
| **1. Net core** | Serializer, framing, pooled buffers, send queue, channels, handshake, heartbeat, disconnect cleanup, handler isolation. **A19 (handshake identity):** take the player ID from the Steam connection (never from the client's packet); on direct IP use a GUID plus a host-issued secret token; migrate existing `playerInfos` from GUID to the new ID. **A8 + A17 (one change):** disconnect cleanup removes the `connectedClients` entry, and the same commit changes the `SaveModSave` player loop from `break` to `continue`, so fixing A8 never exposes XP loss. **A21:** `SaveModSave` uses one slot index for both the stats it writes and the file it writes. **A22:** `SteamSocket` handles `ClosedByPeer`/`ProblemDetectedLocally` (runs `OnDisconnected`, closes the connection, frees the slot); `GetClientFromConnection` returns null for unknown connections instead of throwing | 8 bot clients for 1 h: 0 framing errors, 0 exceptions; malformed-packet fuzz never crashes the host. **A19 test:** a client that sends another player's GUID/token gets a fresh profile, not theirs; an hf3 save's `playerInfos` migrate with XP/level intact; rejoining after a reinstall (Steam) restores the same progress. **A8/A17 test:** 3 players, the slot-2 player leaves, save, reload: all 3 players keep their XP/level/skills, and the leaver can rejoin under the same name with no ghost avatar. **A21 test:** an autosave of slot X never writes into slot Y: with slot Y current, saving slot X leaves slot Y's sidecar byte-identical. **A22 test:** killing a Steam client's process frees its slot within the Steam timeout and the player can rejoin; a message from an unknown connection is dropped and logged, never thrown |
| **2. Locks** | ResourceLockManager, fencing, ActionGate hooks for parts/body/lifts/car move, "in use by" toast (as built: see §3.2; tools/stations and parking moved to Phase 6) | Contention bots: 0 double grants; lock released within 10 s of a kill (done: `LockSoakTests`) |
| **3. Economy** | Ledger, txn dedupe, debit/credit routing, absolute balance broadcast (as built: predictive client + exactly-once host ledger, see §3.3) | 10k random concurrent txns: identical balance on every client; never negative (done: `EconomySoakTests`) |
| **4. Persistence** | Atomic dual-file save, generations, hashes, backups, migrations, autosave (A17 moved to Phase 1, together with A8; as built: see §3.5). **A18:** skill unlock marks progress dirty instead of a full save; the autosave/leave/quit path writes it. **A20:** move the sidecar to `...\LocalLow\...\TogetherPlus\` with a one-time migration from `Mods\togetherMod\saves`. **From Phase 3:** persist the ledger history (last 200 entries) with the host save. **Client save replicas (§3.5):** after each host save the bundle is sent to every client in rate-limited chunks (never during a join snapshot or scene load); clients keep the last 3 per save id; recovery path 1 (restore prompt on join) and path 2 (host from a replica) | 200× process kill during save: always loads a valid generation; restore flow works. **A18 test:** unlocking 10 skills causes 0 immediate save writes, and the next autosave persists all 10. **A20 test:** an old sidecar in `Mods\togetherMod\saves` is migrated once (original kept as backup), and deleting/verifying the game folder no longer loses progress. **Replica tests:** delete or corrupt the host save, then restore from a client replica (byte-identical profile and sidecar, current files backed up first); a tampered replica fails its hash and is rejected; a replica transfer over a lossy, laggy link completes and never sends during a scene load (done: `SaveKillTests` and `BotHarness --save-kill 200`, `SaveCommitTests`, `PersistenceTests`, `ReplicaTests`; the in-game restore flow is on the in-game checklist) |
| **5. WorldState** | Authoritative entities + versions, event log, join/late-join/rejoin snapshot, scene replay, desync detector, event-driven part sync, load-aware heartbeat ("busy" frames while a peer loads a scene). **A23:** client inventory tracking removes entries by UID, not by reference. **Found in Phase 2:** a write the host rejects is reconciled into the loser's inventory, so the lock race window can't duplicate or lose an item; lifter packets carry the absolute state (today the client applies a relative ±1 step even when states already match, and `LifterLogic.listen` can stay false); a part write for a deleted car no longer recreates a ghost `CarPartInfo` on the host (as built: see §3.4; A11 and A12 moved to Phase 6) | 2-hour 4-player playtest with 0 unresolved desync alerts; late join mid-job is correct. **A23 test:** on a client, add, remove, then re-add the same UID: the item is present after the re-add, and the client's tracked UIDs match the host's. **Phase 2 follow-ups:** two bots racing a mount/unmount of the same part end with exactly one copy of the item; lifter state converges after lost packets; deleting a car and then writing to one of its parts leaves no car entry on the host (done: `InventorySyncTests` and `BotHarness --inventory`, `RaceReconcilerTests`, `NetLayerTests`; the lifter, ghost car, late join and playtest checks are on the in-game checklist) |
| **6. Fixes port** | Section 3.7, in order: disconnect/rejoin → parking/cars → tools/stations → jobs/sell → visuals → QoL/DLC → recovery menu. **From Phase 5:** event-driven part sync instead of timers (A11) and a `CarLoaderRegistry` instead of name parsing (A12), done while those hooks are reworked. **Per-car desync detector (with A11/A12):** the host computes a per-car parts hash from its live `CarLoader` objects (complete, and what gets saved), and players compute it with the same code; on a mismatch the host builds that car's full state from its live `CarLoader` (the data the car-spawn path sends) and pushes it to that player. **Body-panel race:** check whether two players unmounting the same panel within about one RTT can both get an item; if so, link the panel to the item the game creates on unmount and reconcile it like parts, otherwise document why the lock window makes it acceptable. **Locks (moved from Phase 2):** station locks (`tool/{type}`), plus gates for FastMount/FastUnmount, delete car, engine crane, oil, paint, wash, welder and parking, each landing with its feature. **Purchase rollback (from Phase 3; must not ship without it):** debit transactions record what was bought; a purchase is broadcast only after the host applies its debit; a refused debit rolls the purchase back on the buyer's side (remove items, despawn the car, revert the upgrade) with the "Not enough shared money" toast (§3.8) | Per-tool manual checklist passes with 2 players; two players can never use the same station at the same time. **Purchase rollback test:** 2–8 bots race to spend the last money on shop parts, tools, paint, cars and upgrades, over lossy, laggy links; afterwards no player (host or client) owns anything the ledger didn't charge for, every owned purchase maps to exactly one applied debit, and every refused purchase has been rolled back on the buyer's side and never reached other players. **Per-car detector test:** under the lossy soak, a deliberately diverged car converges within one detector cycle. **Body-panel race test:** two players unmounting the same panel end with exactly one item (or the documented reason it cannot happen) (done: `PurchaseRollbackTests` and `BotHarness --purchases`, `CarDesyncTests` and `BotHarness --cars`, `RaceReconcilerTests` (exact panel linking), `LoaderNamesTests`, `PayoutsTests`; the deferred Fixes features and the per-tool manual checklist are listed in §3.7 As built and on the in-game checklist) |
| **6b. Fixes port, rest** | Everything in §3.7 "Deferred", ported from Together Fixes (its patch targets are known-good in the real game) onto the Phase 1–5 foundations: engine stand 2, battery charger, brake lathe, dyno, headlamp/wheel alignment, tuning and their minigames (`SkipMinigames`, default **off**); authoritative parking `park/{slot}`, ghost-car cleanup, QoL extra-parking compatibility; scene-change manager and return-car sync; visuals (tint, plates, tuning parts, wheel visuals, item paint); bulk sell (approved once by the host, like car sale), warehouse bulk move, examined sync, workshop/DLC content sync, Steam invite, F4 lift reset. **Switches:** one `MelonPreferences` entry per feature under `[TogetherPlus]`, all on by default except `SkipMinigames`; a switched-off feature leaves no Harmony patch in effect; each feature patches inside its own try/catch, so a broken target disables only that feature (logged). The host's switches go into the session config and clients follow them. Money and items go through the ledger/purchase path. TogetherFixer attribution and modification notes in every ported file | Mod builds, all tests pass; headless tests for the switch registry (off = unpatched, a failing feature isolates) and for every money/item path (bulk sell once, no double pay); each feature on the in-game checklist (done: see §3.7 As built (Phase 6b); `FeatureTests`, `BulkSaleTests`, `InventorySyncTests`, `PayoutsTests`, `CarDesyncTests`; 2,491 tests pass; in-game items in §5.2) |
| **7. Scale to 8** | Config, lobby UI, interest management, perf budget. **Movement coalescing:** unreliable movement is coalesced per player on the send queue (latest wins, at most one pending per player), so a slow link (Direct IP now runs over TCP) never builds a backlog of stale positions | 8-player playtest of 1 h: host frame time +<3 ms, no desync. **Bounded-lag test:** under the *clumsy* latency/loss profile, a soak shows movement lag stays bounded at about one send interval plus the RTT (done: see §3.6 As built (Phase 7); `ScaleTests`, `BotHarness --movement`; the 8-player playtest is on the in-game checklist, §5.2) |
| **8. Hardening** | Fault injection (latency/loss with *clumsy*), long soak, docs, release packaging, then the sync bug assessment (§5.1) | Release candidate; every critical/high finding of §5.1 fixed with a regression test (done: see §5.1 Assessment results; release candidate 0.5.0, `build.ps1 -Package`) |

---

## 5. Test strategy
- **Unit (headless):** serializer round-trips and size limits, framing with split or merged reads,
  lock manager (hierarchy, leases, epochs), ledger (dedupe, concurrency), persistence (torn
  writes, hash mismatch, migrations).
- **Bot harness:** console app on the shared net library that simulates N clients doing movement
  spam, lock contention, purchases and joins/leaves. Built as `Tools\BotHarness` (simulated links
  with latency, jitter, loss and bandwidth, or real TCP); `build.ps1 -Soak` runs the 1-hour,
  8-player soak, and shorter soaks plus framing/payload fuzz tests run in the unit tests.
- **Fault injection:** *clumsy* for 200 ms latency, 5 % loss and reorder; kill the host
  mid-save; pull the network cable on a client while it holds a lock.
- **In-game:** debug overlay (players, RTT, locks, ledger seq, desync alerts) plus a manual
  checklist per station and tool.

### 5.1 Sync bug assessment (after Phase 8)
Review every change since `Release-0_4_16hf3` with a code-review sub-agent, and run the bot harness
under latency and loss. Fix every critical or high finding, each with a regression test. Checklist:
- **Desync:** every synced domain converges after loss, reorder and scene changes.
- **Locks:** no leaks (every grant is released or expires), no double grants, no stuck interactions.
- **Economy:** no double grants or double charges; no ledger double-apply or drift between the
  ledger, the clients and the save.
- **Uncharged purchases (from Phase 3):** no player owns anything the ledger didn't charge for. A
  refused purchase is rolled back on the buyer's side (item removed, car despawned, upgrade reverted)
  and never reaches other players.
- **Join, late join, rejoin and scene replay:** no races between the snapshot, buffered events and
  replay.
- **Cars:** the per-car parts hash (built from the live `CarLoader` on host and players) finds every
  diverged car, and the host's push makes it converge within one detector cycle. A body-panel race
  never gives both players an item.
- **Saves:** every path that can lose or corrupt a save (host save, autosave, leave/quit, migration,
  backups, client replicas and restore).
- **Phase 6b features:** each switch on and off; feature states for late joiners; stations; bulk sell, warehouse and
  item updates never duplicate or lose items or money; parking and away purchases never ship uncharged or twice.
**Early review of Phases 1–5** (read-only, run during Phase 6; all fixed right after Phase 6):

| # | Severity | Finding | Fix |
|---|---|---|---|
| 1 | High | After a disconnect the garage-ready flag stayed set in the menu, so on rejoin the spawn data ran in the menu and the player's next XP gain saved level 1 / 0 XP on the host | `471dc48`: reset on every disconnect and scene change (in-game check: a kicked level-5 player rejoins at level 5) |
| 2 | High | Money spent or earned in the junkyard, auto salon or barn was never recorded, so purchases there were free and quitting from there saved stale money | `4d5b9b8`: `WalletRules` (tests); a car bought away whose debit loses a race is rolled back in Phase 6b (away-car buy) |
| 3 | Medium | A late joiner could not load when the parking lot had an empty space (slot-keyed dictionary read as a list) | `07a23a2`: `ParkSlots` (tests) |
| 4 | Low | After a restore, new saves reused generation numbers, and players skipped copies by generation only, so their copies froze | `df23685`: generations count `_kept`; copies matched by SHA-256 (tests) |

**Assessment results (Phase 8).** Four code-review passes over everything since `Release-0_4_16hf3` (about 17,000
added lines: net and session, persistence, economy/inventory/locks, cars and features), plus every bot-harness mode
for 30–60 simulated minutes under the *clumsy* profile (100 ms one way + 20 ms jitter, 5% loss; 8 players). No
critical finding. Every high and medium finding is fixed, and the cheap low ones too:

| # | Severity | Finding | Fix |
|---|---|---|---|
| 5 | High | Per-car feature results (tuning, alignment, dyno, examined) were sent to late joiners before they had the car and never again on a car resync, so they reverted after a scene return or late join | `2bad0ba`: kept car states go with every car resync, wait on the player until the car is loaded, and are forgotten when the car is deleted (`FeatureTests`) |
| 6 | Medium | Kept feature states were evicted oldest-first at 2,048, so warehouse and parking rows were lost for late joiners in long sessions (a stale warehouse could create items) | `2bad0ba`: overwrites refresh the age; per-car states go first (`FeatureTests`) |
| 7 | Medium | Purchase soak (30 min, clumsy): a refused paint job's revert could reach the painter after its next, paid job, so the painter kept the old colour | `bf08e4b`: the paid colour also goes back to the painter, flagged as its own (`PurchaseRollbackTests`, 600 s clumsy case) |
| 8 | Medium | A garage load cut short by a disconnect still set "game ready" later (in the menu), and spawn/resync coroutines of an earlier lobby applied to the next session (the review #1 path) | `e1b6409`: load generations and per-connection fences (in-game checklist 9) |
| 9 | Medium | Item UID counters missed group items and their parts, and a brand-new profile never got its range: UIDs could be reused, and such items vanished | `e8fb245`: `UidRanges.Advance` on every UID in the slot's range; `uidRange` packet for new profiles (`ScaleTests`) |
| 10 | Medium | The reward box of a handed-over order counted as host state: never shared, removed on the next load | `bd19a6f`: the hand-over completes on the next frame as the player's own action (checklist 13) |
| 11 | Medium | An away car's refund was looked up by transaction number, which restarts on reconnect: wrong refund or none | `68c4e07`: the charge is taken when the debit is applied (`FeatureTests`) |
| 12 | Medium | A save copy uploaded during the garage load could be restored mid-load (mixed save); a stale upload could answer a later request | `0e87146`: restore only in the lobby (not started, not loading) and only the copy asked for |
| 13 | Medium | A new game in a slot holding a game profile without a mod file overwrote it with no copy | `a65d96f`: a copy is kept first (or the slot is refused); delete removes `.cms21` too |
| 14 | Medium | One failed read of `credentials.txt` made the next token rewrite it, losing every other host's token (and the progress there) | `a65d96f`: read again; an unreadable file is moved aside (`IdentityTests`) |
| 15 | Low | Kept copies recorded legacy profiles as format 1 | `a65d96f`: real format (`IdentityTests`) |
| 16 | Low | Standing players were never sent again after a lost frame or scene change; `NetHost.Stop` left just-accepted links open; a departed player's workshop list kept hiding cars | `8875915` (`FeatureTests`) |

Follow-up (both lows fixed before the release, as decided with the user):

| # | Severity | Finding | Fix |
|---|---|---|---|
| 17 | Low | Two players removing the same item at once, one into the warehouse or a sale and one onto a car, kept both | `5032788`: the host accepts only the first removal of a UID, and it pays for one thing (`RemovalArbiter`): a mount with a lost removal is undone, a warehouse add only stands if the mover's removal won, single-item sales are host-settled like bulk sales (`ItemRaceTests`: 4,000 interleavings, exactly one copy, paid at most once) |
| 18 | Low | A purchase before the shared wallet arrived (and for ~2 s after the garage was ready, when the wallet read "sandbox") was not charged | `bd9cb9b`: a player shows 0 money/scrap until the host's balance arrives, so the game refuses every purchase ("Syncing money…"); upgrades and order accepts are gated; the mode comes from the loaded profile; the host seeds the wallet before its garage is playable (`ItemRaceTests`) |
Harness after the fixes (all PASS): net 30 min with churn, locks, economy,
purchases, cars, movement (p99 515 ms flat), inventory 15 min, save-kill 200 + 50. Release candidate: 0.5.0
(`build.ps1 -Package` → `dist\TogetherPlus-0.5.0.zip`). Harness after the fixes (all PASS): net 30 min with churn, locks, economy,
purchases, cars, movement (p99 515 ms flat), inventory 15 min, save-kill 200 + 50. Release candidate: 0.5.0
(`build.ps1 -Package` → `dist\TogetherPlus-0.5.0.zip`).

### 5.2 In-game checklist
What the headless tests and the bot harness can't cover. Two players unless noted (host H, player P).

**Phases 2–5**
1. **In use:** H holds a part, wheel or station; P tries the same: "in use by H" toast, nothing happens on P.
2. **Shared money:** P buys parts and H sees the balance drop; P buys in the junkyard, auto salon and barn: money is
   charged there too; quitting from there keeps the right money.
3. **Autosave and restore:** play 6+ min, kill the host's game (Task Manager), restart: the save loads with at most a
   few minutes lost. Corrupt the host's save file: a client offers its copy (restore prompt), the restored game loads.
4. **Replica:** after a host save, P's `TogetherPlus` folder gets a copy of the save (at most 3 per save).
5. **Inventory:** H and P add, move and mount items: both inventories stay equal; P unmounts a part H mounts at the
   same moment: exactly one copy of the item exists.
6. **Lifter:** raise and lower each lifter from both sides, with a car on it: same height on both games.
7. **Ghost car:** H deletes or sells a car while P loads a scene: P never keeps it.
8. **Late join:** P joins while H has cars, orders and an empty parking space between parked cars: P loads, sees the
   same cars, parts, orders and parking.
9. **Rejoin:** kick a level-5 P, P rejoins: still level 5 with its XP and skills, no ghost avatar.

**Phase 6**
10. **Purchase rollback:** with almost no money, H and P buy at once (parts, a tool, paint, a car into parking, a
    garage upgrade): the one refused shows "Not enough shared money" and has nothing (no items, old colour, no car,
    upgrade still locked).
11. **Car detector:** change a car on P's game only (e.g. a mod tool or a lost packet): within ~10 s it matches H's.
12. **Stations:** H and P on the tire changer, wheel balancer, spring clamp, welder, engine crane, oil bin, car wash:
    only one at a time.
13. **Orders:** H and P accept the same order at once: one car spawns. Hand over at once: paid once, XP to the one who
    handed over. Sell a car at once: paid once, the car goes once.
14. **Recovery:** Ctrl+Shift+R resyncs (cars, inventory, money); Ctrl+Shift+L writes locks, wallet (and on the host
    the ledger tail and car-checker counters) to the log.

**Phase 6b** (each switch off as well: the feature does nothing and nothing breaks)
15. **Engine stand 2:** mount/unmount parts on it from both sides; off: it is out of reach.
16. **Battery charger, brake lathe:** put an item on, take it off from the other game; one player at a time; P joins
    late and sees the item on the station.
17. **Dyno, headlamp alignment, wheel alignment, ECU/carb/gearbox tuning:** results show on the other game and for a
    late joiner; one player per station. `SkipMinigames` on: each ends at its best result.
18. **Bulk sell:** H and P sell the same items at once: paid once per item, items gone once.
19. **Warehouse:** P moves items (single and bulk) to and from the warehouse, then H saves and reloads: nothing lost or
    doubled.
20. **Visuals:** paint an item, change a licence plate, wheel visuals, window tint, tuning parts, examine a part: same
    on the other game and after a late join.
21. **F4 lift reset:** with a stuck lifter, F4 resets it on both games.
22. **Parking:** H and P park a car at the same moment: both get a space, never the same; take the same car out at
    once: it leaves once; move cars and unlock a row: same on both.
23. **Ghost cars:** a car only on P's game (e.g. after a desync) disappears there; a missing one comes back.
24. **QoL mod:** with the QoL mod's extra parking on, in a session no extra parking cars appear in the garage; outside
    a session they do.
25. **Scene return:** P buys a car in the junkyard, auto salon and barn while H plays: it appears in a parking space on
    both games, money charged once; with not enough money: no car anywhere; with a full parking lot: refunded. Sell or
    delete a car while P is away: it doesn't come back when P returns.
26. **Steam invites:** H invites P from the Steam friends list; P joins from the main menu and via "Join game".
27. **Workshop content:** a workshop car only H has is not offered while P is in the session.

**Phase 7** (8 players)
28. **8 players, 1 hour:** host frame time at most ~3 ms worse than alone (Ctrl+Shift+L shows the host tick), no
    desync alerts; lobby shows 8 rows; a 9th player is told the session is full; `MaxPlayers` in
    `UserData/TogetherPlus.json` limits the session.
29. **Movement:** other players walk smoothly (no stutter at 20 Hz, no gliding across the room after a teleport), each
    avatar has its own colour and name; with *clumsy* (200 ms, 5% loss) movement lags but never falls further behind.
30. **Item UIDs:** players 5–8 buy and unmount items in an old 4-player save: no item ever replaces another.
31. **Steam:** the friends list shows "Together+ n/8".

**Phase 8 fixes** (assessment)
32. **Scene return keeps results:** tune, align and dyno a car; P goes to the junkyard and back, and a late joiner
    joins: both see the same results.
33. **Hand-over reward box:** the player who hands over an order gets its reward box, everyone sees it, and it is still
    there after a scene change.
34. **Restore timing:** accept a player's copy, then press Start at once: the restore happens only in the lobby, or
    the copy is just stored.
35. **Orphaned profile:** put a vanilla `profileN.cms21b` in an empty slot, create a new game there: the old one is
    listed under "Restore a backup".
36. **Paint:** paint the same car twice quickly with barely enough money: every player ends on the host's colour.
37. **Same item at once:** H drags an item into the warehouse (or sells it) while P mounts the same item: exactly one
    of them succeeds; the other's part comes back off, or the warehouse/sale entry disappears, and a sale is paid once.
38. **Syncing money:** P tries to buy right after joining or loading: the money shows 0 with "Syncing money…" until the
    shared balance appears; nothing is bought for free.

## 6. Risks
| Risk | Mitigation |
|---|---|
| Some interactions lack a clean Harmony entry point for gating | Deny-and-retry fallback; server-side fencing still blocks corrupt commits |
| TogetherFixer code assumes hf3 internals we are replacing (packets, serializer) | Port behaviour and hook points, re-implement transport and payloads |
| Non-commercial licence of ported code | Keep attribution; never monetize; document in NOTICE |
| Game updates or MelonLoader version drift | Pin MelonLoader 0.5.7; startup game-version check |
| Scope size (~13k LOC base + ~1 MB of Fixes source) | Phase gates; each phase is shippable on its own |

## 7. Open questions
1. ~~XP per-player, money and scrap shared?~~ Confirmed.
2. ~~`SkipMinigames` default?~~ Off (vanilla), toggle in config.
3. ~~Refresh `Libs\`?~~ Yes. Game is Unity 2020.3.49f1, Steam build 23963849 (updated 2026-10-05).
   MelonLoader is not installed yet; 0.5.7 must run once to generate the unhollowed assemblies.
