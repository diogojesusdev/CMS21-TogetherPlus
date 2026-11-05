# Together+ bug audit — common gameplay features

**Build:** 0.5.0-rc14 (`30413e8`) · **Date:** 2026-11-05 · **Scope:** defects introduced by the mod only

**Status:** the findings in §1–§9 were fixed in **0.5.0-rc15** (five fix plans under `.implementation_plans/`:
handover-car-cleanup, welder-fluids-parts, stations-tools-enter, economy-ui, crash-containment). The §10 minor items
are partly fixed; each plan's follow-up section lists what was left, with the reason.

This is a read-only audit of the paths every multiplayer session touches: orders and the job cars, mounting and
unmounting parts, the welder, fluids/wash/paint, car load and resync, the engine stand and crane, the shared
inventory and wallet, and the host/join/menu flow.

Out of scope: vanilla-game bugs, and the issues already listed as fixed in `docs/IMPLEMENTATION_PLAN.md` §2 and
§5.1 (they were re-checked and are not reported again). The vanilla method bodies are not available here (the game
ships Il2Cpp interop stubs), so statements about *what the game does* are inferred from call sites and upstream
history; each finding states what is code-proven and what is assumed.

**Legend.** Severity: how much a normal player loses when the bug fires. Confidence: how sure the code path is
(High = quoted and traced; Medium = one vanilla-behaviour assumption; Low = plausible but unproven).

## Summary

| ID | Feature | Severity | Confidence | One line |
|---|---|---|---|---|
| JB-1 | Hand-over | **High** | High | The handed-over car stays in `loadedCars`, so the next job car on that loader is never sent |
| JB-2 | Hand-over | **High** | High | The host keeps the car in `CarSpawnDatas`, so late joiners get the customer car back |
| WL-1 | Welder | **High** | High | `SetWelder` throws on the host → the weld is never relayed |
| JB-3 | Orders | Medium | High | Accepted orders are not recorded in `ServerData.selectedJobs` → missing for late joiners |
| JB-4 | Orders | Medium | Medium | The server's order list is re-seeded on every garage load and never de-duplicated |
| WL-2 | Welder | Medium | High | A receiver leaves `listen=false`, so its next own weld is not sent |
| FL-1 | Fluids | Medium | High | `ServerData.UpdateFluid` is a no-op → resync/late join reverts oil/coolant levels |
| FL-2 | Wash | Medium | High | The host stores the interior/exterior wash branches swapped |
| FL-3 | Paint | Medium | High | `carPosition` is dropped on a host load, so paint sync can't find the booth car |
| CR-1 | Parts | Medium | High | `Hide`/`TakeOff`/`SwitchCarPart` hooks index `loadedCars` unchecked and skip the scene guard |
| CR-2 | Car load | Medium | Medium | A void prefix cannot skip an iterator → every local car load runs twice |
| CE-1 | Enter/exit | Medium | Medium | Distance heuristics can report "in car" while walking → avatar hidden, position stream stopped |
| ES-1 | Engine stand | Medium | Medium | Taking an engine off stand 1 sends no packet → ghost engine, possible duplication |
| ES-2 | Crane | Medium | High | The crane-pull apply path dereferences a possibly null `GetEngine()` |
| EC-1 | Bulk sell | Medium | High | Group items (wheels, engines) are not host-settled; their money is client-claimed |
| EC-2 | Away purchase | Medium | Medium | A car bought away with no linked debit is treated as already paid |
| EC-3 | Hand-over | Medium | Medium | The payout is granted before the hand-over; a failed hand-over blocks the order forever |
| UI-1 | Lobby | Medium | Medium | The host can start the game twice (no re-entrancy guard) |
| UI-2 | Menu | Low | High | Every return to the menu does a synchronous GitHub request on the main thread |
| — | minor | Low | — | 11 smaller items in §10 |

---

## 1. Orders and hand-over

The order hand-over is the game's main progression loop, and both sides of it keep stale state.

### JB-1 · The handed-over car stays in `loadedCars`; the next job car on that loader is never sent

**Severity:** High · **Confidence:** High

**Where**

- `ClientSide/Data/Garage/Campaign/JobManager.cs:110-111` — on every client that receives the hand-over:
  `CarSpawnHooks.listenToDelete = false; GameData.Instance.carLoaders[job.carLoaderID].DeleteCar();`
- `ClientSide/Data/Garage/Campaign/JobHooks.cs:242-250` — on the player who hands it over: sets `listenToDelete = false`
  and calls `carLoader.DeleteCar(true)`.
- `ClientSide/Data/Garage/Car/CarSpawnHooks.cs:83-87` — the delete hook that would remove the bookkeeping returns
  before it (`if (!Client.Instance.isConnected || !listenToDelete) { listenToDelete = true; return; }`); the removal is
  at `:103` (`ClientData.Instance.loadedCars.Remove(carLoaderID)`).
- `CarSpawnHooks.cs:76` patches `CarLoader.DeleteCar()` **without parameters**; `JobHooks` calls `DeleteCar(true)`,
  which is a different overload and is not patched at all.
- `ClientSide/Data/Garage/Car/CarSpawnManager.cs:46` — the next load is blocked by exactly that entry:
  `if (ClientData.Instance.loadedCars.ContainsKey(carLoaderID)) yield break;`

**What happens.** Handing over an order deletes the customer car on every game with the delete echo suppressed. The
suppression path returns before `loadedCars.Remove`, and the handing player's `DeleteCar(true)` is not intercepted,
so every game keeps a dead `ModCar` for that loader indefinitely.

**Trigger.** Two players, P accepts an order whose car loads on loader X, completes it, hands it over. P accepts
another order and the game loads its car on X (the loader that was just freed).

**Impact.** `LoadJobCar` returns immediately, so no `loadJobCar`/`loadCar` packet is sent for the new car: the host
and all other players never see it. P's part writes for the new car are resolved against the old car's
`CarPartInfo` (or throw), and `CarChecks` keeps hashing a car that no longer exists. The order cannot be completed
together.

**Fix direction.** Remove the loader from `loadedCars` before every suppressed delete (the pattern already used in
`CarSyncManager.cs:36-39`), and/or let the hand-over path do the bookkeeping explicitly in `CompleteHandover` and
`OnJobComplete` before calling `DeleteCar`.

### JB-2 · The host keeps the handed-over car in `CarSpawnDatas`, so late joiners get it back

**Severity:** High · **Confidence:** High

**Where**

- `ServerSide/Data/ServerHandle.cs:591-604` (`EndJobPacket`) only does `ServerData.Instance.EndJob(job);` and
  `ServerSend.EndJobPacket(...)`.
- `ServerSide/Data/ServerData.cs:232-235` — `EndJob` removes the order from `selectedJobs` and nothing else.
- The car entries (`CarSpawnDatas`, `CarPartInfo`) are only removed by `ServerData.DeleteCar` (`ServerData.cs:49-56`),
  reached from the normal `deleteCar` packet (`ServerHandle.cs:430-437`) — never from the hand-over.
- `ServerSide/Data/ServerResyncs.cs:17-26` then re-sends it: it reads `CarSpawnDatas` and `CarPartInfo` with
  `TryGetValue` and calls `ServerSend.LoadCarPacket(...)`.

**Trigger.** P hands over an order; a third player joins late (or P rejoins) while the feature `GhostCarCleanup` is
off, or after any car resync request.

**Impact.** The newcomer loads a customer car that no longer exists on the host (`LoadCarFromServer` even calls
`SetCustomerCar(true, data.jobID)`), with no order for it. The ghost then fights the desync detector and blocks the
loader again (see JB-1).

**Fix direction.** In `EndJobPacket`, after `EndJob`, call `ServerData.Instance.DeleteCar(job.carLoaderID)` (and notify
the car detector), the same way `deleteCar` does — or have the hand-over path send a normal delete to the server.

### JB-3 · Accepted orders are not recorded in the host's `selectedJobs`

**Severity:** Medium · **Confidence:** High

**Where**

- `ServerSide/Data/ServerHandle.cs:559-567` (`JobActionPacket`) removes the order from `jobs` and broadcasts, but never
  calls `ServerData.UpdateSelectedJobs(job, takeJob)`.
- `ServerData.selectedJobs` is only fed by the `selectedJob` packet (`ServerHandle.cs:569-577`), which only the host's
  own `OrderGenerator.Load` sends (`JobHooks.cs:26-46`).
- `ServerResyncs.ResyncJobs` (`ServerResyncs.cs:75-79`) is the late-join replay.

**Trigger.** Host + P accept an order; a third player joins late or P rejoins. `ResyncJobs` sends the host's
load-time selected orders only, while `ResyncAllCars` still sends the accepted order's car.

**Impact.** The joiner receives the customer car but not the "accepted order" state for it; whether the order window
can be used from that client depends on the client's local save, so this ranges from a missing board row to a car
that cannot be handed over from that client.

**Fix direction.** Mirror `UpdateSelectedJobs` in the server's `JobActionPacket` for `takeJob == true`.

### JB-4 · The order list is re-seeded on every garage load and never de-duplicated

**Severity:** Medium · **Confidence:** Medium

**Where**

- `JobHooks.LoadHook` (`JobHooks.cs:26-46`) runs on every `OrderGenerator.Load` and, when host, sends every job and
  selected job again.
- `OrderGenerator.Load` runs on every garage load (`GameLoadHook.cs:183`), including the return from the junkyard,
  barn and auto salon.
- `ServerData.cs:140-143` appends blindly: `public void AddJob(ModJob job) { jobs.Add(job); }`
- Clients mirror the same behaviour: `JobManager.AddJob` (`JobManager.cs:51-65`) does `jobs.Add(newJob)` with no
  duplicate check.
- A second path: `JobHooks.GenerateNewJobHook` (`JobHooks.cs:88-100`) sends the last order without a host check,
  although its prefix (`:79-86`) cancels generation on clients; Harmony still runs the postfix after a skipped
  original, so a client can re-send an existing order as "new".

**Trigger.** Host returns from the junkyard once, then a player joins late (or a client calls `GenerateNewJob` while
connected).

**Impact.** `ServerData.jobs` accumulates duplicates; a late joiner's `ResyncJobs` adds every order again, so the
board shows the same order two or more times. `GlobalData.AddJob(1)` drifts, and the duplicate can never be accepted
(the host remembers `accept/<id>`), so it stays on the board as a dead row. The server packet also grows with the
session.

**Fix direction.** De-duplicate by order id in `ServerData.AddJob`/`JobManager.AddJob`, and either replace the list
from `OrderGenerator.Load` instead of appending or guard the load hook to once per session; add the
`Server.Instance.isRunning` guard to `GenerateNewJobHook` (as its sibling at `:66` has).

---

## 2. Welder

### WL-1 · `SetWelder` throws on the host, so a weld is never relayed

**Severity:** High · **Confidence:** High (the throw), Medium (how often `body` is present)

**Where** — `ServerSide/Data/ServerData.cs:374-384`:

```csharp
public void SetWelder(int loaderID)
{
    if (!CarPartInfo.ContainsKey(loaderID)) return;
    ModCarPart part = GetCarPart(loaderID, "body");
    part.condition = 1;          // null dereference
    part.dent = 1;
    ModCarPart part2 = GetCarPart(loaderID, "details");
    part2.dent = 1;              // null dereference
}
```

`GetCarPart` (`ServerData.cs:368-372`) is `FirstOrDefault` over `BodyPartsReferences`. The only writer of that
dictionary is `UpdateBodyParts` (`ServerData.cs:124-132`), called from the `bodyPart` packet — i.e. only for panels a
player has actually taken off, put on or switched. A car's `body` shell (and `details`) is never sent that way.

Contrast the neighbouring `SetCarWash` (`ServerData.cs:319-366`), which null-checks every part it touches.

**Trigger.** Any player uses the welder on a car (`ClientSend.WelderPacket` → `ServerHandle.WelderPacket` →
`SetWelder`).

**Impact.** `NullReferenceException` → `PacketRouter.Dispatch` catches and logs it, **the weld is not stored or
broadcast**, so other players keep the dents until a car resync; every weld also increments the sender's handler-error
count, and at 50 the peer is kicked (`PacketRouter.cs:13/92`, `Server.cs:120-125`). The host's own welds are affected
too (the host's client is just another peer on the loopback).

**Fix direction.** Null-check both lookups (and skip the fields that are missing), or resolve the panel from the
live `CarLoader`/`CarSpawnDatas` blob instead of the packet-derived map.

### WL-2 · A receiver leaves `WelderLogic.listen` false, so its next own weld is not sent

**Severity:** Medium · **Confidence:** High

**Where**

- `ClientSide/Data/Garage/Tools/WelderLogic.cs:36` — the apply coroutine sets `listen = false` and never restores it.
- The only reset is the early-out of the send hook at `WelderLogic.cs:23`:
  `if (!Client.Instance.isConnected || !listen) { listen = true; return; }`
  which is only reached when the *local* player starts a weld.

**Trigger.** Player A welds; player B receives and applies it (`ClientHandle.WelderPacket` → `UseWelder`). B later
welds a car.

**Impact.** B's first weld after any received weld runs locally (the `StationGates.WelderGate` is bypassed because
the flag is false) but **no `WelderPacket` is sent**, so the host's stored body condition stays unrepaired, other
players keep the dents, and a later resync reverts B's weld.

**Fix direction.** Use `using (ActionGate.Apply())` around the apply body instead of the one-shot flag, or restore
`listen = true` in a `finally`/at the end of `UseWelder`.

---

## 3. Fluids, wash and paint

### FL-1 · `ServerData.UpdateFluid` is a no-op, so resyncs revert fluid levels

**Severity:** Medium · **Confidence:** High

**Where** — `ServerSide/Data/ServerData.cs:237-240`:

```csharp
public void UpdateFluid(ModFluidData fluid, int carLoaderID)
{
    //Log.Info("Not implemented..."); TODO: Implement this
}
```

The live path is fine: the host relays the packet (`ServerHandle.cs:421-428`) and every connected client applies it
(`PartsUpdater.UpdateFluid`). But the host's stored car blob — `CarSpawnDatas[carLoaderID]`, built once at load —
never changes. `ResyncCar` sends that blob (`ServerResyncs.cs:26`) and `LoadCarFromServer` loads it
(`CarSpawnManager.cs:160-165`), including its `FluidsData`.

**Trigger.** Fill oil/coolant/brake fluid, then go to the junkyard/barn/salon and come back (the return marks cars
`needResync` and resyncs them), or have a player join late.

**Impact.** The resynced car's fluid levels revert to the values at load time on that player's game. Orders check
fluids locally (`CheckCarHaveOil`, `CheckCarHaveFluids`), so a player can be unable to hand over an order that is
actually full, or see the wrong level. There is no fluid entry in the desync hash (`CarChecks.cs:51-53`), so nothing
self-heals.

**Fix direction.** Store the fluid in `ServerData.CarSpawnDatas[carLoaderID].FluidsData` (and in `CarPartInfo` if
needed) inside `UpdateFluid`.

### FL-2 · The host stores the interior/exterior wash branches swapped

**Severity:** Medium · **Confidence:** High (branches), Medium (how visible)

**Where**

- Client contract: `CarWashLogic.cs:150-161` — `if (!interior) TweenExteriorDustWash(...)`, `else
  TweenInteriorConditionAndDust(...)`; the script scene sends `interior=false` for an exterior wash (`:49`).
- Host: `ServerData.cs:319-366` — `if (interior) { washFactor = 1; Dust = 0; }` on body panels, `else { ... }` over the
  literal `string[] interiorParts = { "benchFront", "bench", "steeringWheel", ... }` setting `condition = 1`.

So an **exterior** wash (`interior == false`) is the branch that marks the seats/bench/steering wheel pristine on the
host's replica; the exterior dust it removed is not stored.

**Trigger.** Player P washes the car exterior; later the car is resynced to a late joiner or after a scene return.

**Impact.** A wash silently "repairs" the interior condition on the resynced copies (and the exterior dirt comes
back). Cosmetic, but it changes order-check results and condition values.

**Fix direction.** Swap the branches in `SetCarWash` to match the client contract.

### FL-3 · Paint sync depends on `ModCar.carPosition`, which is never set when a car is loaded from the host

**Severity:** Medium · **Confidence:** High

**Where**

- `ClientSide/Data/Garage/Car/CarSpawnManager.cs:172`:
  `var car = new ModCar(carLoaderID, data.carToLoad, data.configVersion);`
  The 4th parameter (`carPosition`) defaults to `-1` (`Shared/Data/ModCar.cs:21`), even though the packet carries
  `data.carPosition` and the loader was just set to it (`:162`).
- `CarPaintLogic.ChangeColor` locates the booth car by that field: it loops `loadedCars.Values` for
  `car.carPosition == 5`, and returns with `Log.Warn("No car found in paintshop position (position 5)")` when none
  matches (`CarPaintLogic.cs:116-132`).

**Trigger.** A car is in the paint booth (place 5) when a player joins late, or when the car is resynced after a
scene return (`ResyncCar` → `LoadCarFromServer`). Another player paints it.

**Impact.** The receiver never finds a car at position 5, so the remote colour is silently not applied (the log line
above); the painter's own colour and other clients that had moved the car to the booth are fine. The car remains the
old colour until it is moved again and repainted.

**Fix direction.** Pass `data.carPosition` to the `ModCar` constructor in `LoadCarFromServer` (and keep it updated on
resync).

---

## 4. Cars: load, resync, parking

### CR-1 · `Hide`, `TakeOffCarPart` and `SwitchCarPart` hooks index `loadedCars` unchecked, with no garage-scene guard

**Severity:** Medium (crash/half-applied action) · **Confidence:** High (missing guard), Medium (frequency)

**Where**

- `ClientSide/Data/Garage/Car/PartUpdateHooks.cs:121-153` (`HideHook`) and `:155-171` (`TakeOffCarPartHook`):
  `var car = ClientData.Instance.loadedCars[carLoaderID];` — both are `[HarmonyPostfix]`, and neither checks
  `SceneManager.CurrentScene() != GameScene.garage`.
- `ClientSide/Data/Garage/Car/CarSyncHooks.cs:16-28` (`SwitchCarPartHook`) has the same indexer at `:21` and reads
  `car.CarPartInfo.BodyPartsReferences[key]` at `:25` (NRE while the car is still loading).
- The fluid hooks in the same file *do* guard this (`PartUpdateHooks.cs:27-28`, with the comment "Cars of the
  junkyard, auto salon or barn are not garage cars (their loader ids would hit garage cars)").

**Trigger.** Unmount a part or take off a panel while the loader id is not tracked: in the junkyard / auto salon /
barn (same `CarLoaderOnCar`/`PartScript` trees), on a QoL extra-parking car (`QolParking.IsExtraPlace`, never
registered), or in the ~1 s window before `PartsReferencer` finishes registering a freshly loaded car.

**Impact.** The `KeyNotFoundException` is thrown out of a postfix into the game's own `PartScript.Hide()` /
`CarLoader.TakeOffCarPart()` call: the local action can be left half-applied and the remote change is never sent.
If a *different* tracked car has the same loader id (the collision the fluid comment warns about), the change is
attributed to that car instead.

**Fix direction.** Use `TryGetValue` and the same garage-scene/QoL guards as the fluid hooks; treat "not tracked" as
"nothing to send".

### CR-2 · A local car load runs twice: a void prefix cannot skip an iterator

**Severity:** Medium (load timing/races) · **Confidence:** Medium (signature verified, impact unproven)

**Where** — `ClientSide/Data/Garage/Car/CarSpawnHooks.cs:27-40`:

```csharp
[HarmonyPatch(typeof(CarLoader), nameof(CarLoader.LoadCarFromFile), typeof(string))]
[HarmonyPrefix]
public static void LoadCarFromFileHook(string file, CarLoader __instance)
{
    ...
    MainMod.StartCoroutine(__instance.LoadCarFromFile(newCarData));
    MelonCoroutines.Start(LoadCarFromFile(newCarData, __instance));
}
```

The patched method returns `IEnumerator` (checked in the interop assembly). A `void` prefix without `ref __result`
cannot skip it, so the manual `LoadCarFromFile(newCarData)` runs **and** the original `LoadCarFromFile(string)` still
runs on the same loader. This happens for every car on every garage entry (`GameLoadHook.cs:153` calls
`car.LoadCarFromFile()` per loader).

**Impact.** Two concurrent loads write `carToLoad`/parts on one loader while `CarSpawnManager.LoadCar` waits for
`IsCarLoaded()`; depending on timing, a load can be announced twice or read a half-written profile. Not proven
in-game, but the pattern is wrong regardless.

**Fix direction.** Make the prefix a `bool` and return `false` after starting the manual load, or patch the
`LoadCarFromFile(NewCarData)` overload instead.

### CR-3 · Parking: a per-space "hold" outlives the car, so a re-filled space cannot be emptied

**Severity:** Low–Medium · **Confidence:** High

**Where** — `ClientSide/Features/Garage/ParkingFeature.cs:141-168` asks `PurchaseKind.ParkOut` with the space number
as the id; `Shared/Economy/Payouts.cs:74` holds `out/<space>` for `SaleHoldMs = 10_000` (`:18`), while a park action
holds only `"park"` for `ParkHoldMs = 5_000` (`:21`). `Payouts.Decide` refuses while the key is held (`:69`).

**Trigger (one player).** Take a car out of space N; park another car at t≈6 s (the game refills the lowest free
space, N); take it out at t≈7 s.

**Impact.** The host refuses with "Another player already took this car out" and the vanilla coroutine is skipped
(`__result = Nothing(); return false;`), so nothing happens and the message is wrong. A second player's park within
5 s is similarly a silent no-op (the toast is rate-limited).

**Fix direction.** Use the car's UID (or a per-player token) as the hold key, or release `out/<space>` when the space
is filled again.

---

## 5. Parts: mounting and panels

### PT-1 · A body panel with an unresolved connected part sends panel id 0, and the receiver rewrites the first panel

**Severity:** Medium · **Confidence:** High (code path), Medium (frequency)

**Where**

- `Shared/Data/Vanilla/Cars/ModCarPart.cs:38` returns **before** `carPartID = _carPartID` (`:45`) when `_part` is
  null, so the object keeps the default `0`.
- `ModCarPart.cs:66-71` ignores the result of `FindBodyPartInDictionary` (which sets `key = 0` on failure,
  `PartUpdateHooks.cs:239`) and adds the connected part with that key anyway.
- Receiver: `PartsUpdater.cs:261-267` uses the serialized id as the dictionary key and calls `UpdateBodyPart` on it
  (`var key = _carPart.carPartID; ... BodyPartsReferences[key]`).

**Trigger.** Take off / put on a body panel whose `ConnectedParts` entry cannot be resolved on the sender (a name
missing from `BodyPartsReferences`, or `GetCarPart` returning null).

**Impact.** Every other client applies an empty `ModCarPart` over body panel `0`: its name becomes `""`, condition,
dent, livery, quality and paint reset to defaults, `TunePart(reference.name, …)` is called with an empty name, and
`TakeOnCarPartFromSave(reference.name)` can re-mount a panel the player had off. Silent corruption of an unrelated
panel.

**Fix direction.** Do not add connected parts whose lookup failed; validate `carPartID` on the receiver and skip
unknown ids.

### PT-2 · `unmountWith` children are matched by position, with no bounds check

**Severity:** Medium · **Confidence:** High (mechanism)

**Where** — `PartsUpdater.cs:174-181`:

```csharp
for (var index = 0; index < part.unmountWith.Count; index++)
{
    var partScript = part.unmountWith[index];
    UpdatePartScript(partScript, reference.unmountWith.ToArray()[index], carLoaderID);
}
```

The loop length comes from the **sender's** list; the target comes from the **receiver's** live `unmountWith`. Nothing
compares lengths or checks that the two parts are the same, and `.ToArray()` is re-allocated per iteration.

**Trigger.** A grouped mount/unmount (engine block + accessories, suspension group) where the receiver's
`unmountWith` is shorter or ordered differently (engine variant, tuned engine, partially mounted group).

**Impact.** `IndexOutOfRangeException` / `NullReferenceException` after the parent was already applied inside
`using (ActionGate.Apply())`, leaving the group half-applied; or the wrong child is updated. The exception escapes the
coroutine, so the change is never re-requested.

**Fix direction.** Match children by part id where possible, bound the loop by both counts, and log a warning for the
unmatched entries.

---

## 6. Engine stand and crane

### ES-1 · Taking an engine off engine stand 1 sends no packet

**Severity:** Medium · **Confidence:** Medium (depends on the vanilla lambda handling both stands; `useAlt` suggests
it does)

**Where** — `ClientSide/Data/Garage/Tools/EngineStand.cs:72-86`:

```csharp
[HarmonyPatch(typeof(PieMenuController), "_GetOnClick_b__72_35")]
[HarmonyPrefix]
public static bool TakeOffEngineFromStandHook()
{
    if (!Client.Instance.isConnected || !listen) { listen = true; return true; }
    if (useAlt)
    {
        ... engineStandLogic2 ... ClientSend.TakeOffEnginePacket(useAlt);
        return false;
    }
    return true;   // stand 1: the vanilla action runs, no packet is sent
}
```

`ServerData.ClearEngineFromStand` is only called from `ServerHandle.EngineStandTakeOffPacket` (`ServerHandle.cs:707-713`),
and `ResyncEngineStand` (`ServerResyncs.cs:99-106`) re-sends the engine to late joiners/scene returns.

**Trigger.** A player takes an engine off the original engine stand (stand 1, the default; `useAlt` is false), then
another player joins or returns from a scene — or puts that engine on a car.

**Impact.** The host still has the engine on stand 1, so the other players see a ghost engine there, and a resync
re-adds it. Meanwhile the taking player's game added the engine group to the shared inventory, so the engine can be
both on the stand and in the inventory/car — a duplication.

**Fix direction.** Send `TakeOffEnginePacket(false)` for the non-alt branch (and clear the server state) before
letting the vanilla action run, or patch `EngineStandLogic.ClearEngineStand` directly for both stands.

### ES-2 · The crane-pull apply path dereferences a possibly null engine

**Severity:** Medium · **Confidence:** High (missing check), Medium (frequency)

**Where** — `ClientSide/Data/Garage/Tools/EngineCrane.cs:52-57`:

```csharp
CarLoader carLoader = GameData.Instance.carLoaders[carLoaderID];   // no bounds check
GameObject engine = carLoader.GetEngine();
InteractiveObject iO = engine.GetComponent<InteractiveObject>();   // NRE when null
if (iO == null) { yield break; }
```

The handler (`ClientHandle.cs:356-368`) has no scene, bounds or `loadedCars` check — unlike `LifterPacket`
(`ClientHandle.cs:143-149`). The mirror path *does* null-check (`EngineCrane.cs:124-127`).

**Trigger.** A pulls an engine; a receiver whose copy of that loader has no engine (late join, that car was deleted
there, or the packet arrives twice).

**Impact.** `NullReferenceException` in the coroutine, so the crane pull is not applied on that client; the host and
the actor diverge until a car resync.

**Fix direction.** Bounds-check the loader, `TryGetValue` into `loadedCars`, and null-check `GetEngine()` before the
dereference.

### ES-3 · The crane-insert packet carries no car id, so the receiver inserts into its own crane's car

**Severity:** Low · **Confidence:** Medium

**Where** — `EngineCrane.cs:29` sends `EngineCraneHandlePacket(1, -1, new ModGroupItem(engine))`; the receiver
(`EngineCrane.cs:110-137`) resolves the target from `ToolsMoveManager.Get().GetConnectedCarLoader(IOSpecialType.EngineCrane)`.

**Trigger.** The crane's connected car differs between clients (a car moved while the crane hangs over it, or the
crane was never connected on the receiver).

**Impact.** `yield break` (engine never appears there) or the engine is inserted into the wrong car and removed from
the receiver's inventory (`EngineCrane.cs:219`).

**Fix direction.** Send the car loader id (the pull path already does).

### ST-1 · Station contents are never re-sent after a late join or a scene return

**Severity:** Medium (an item looks lost) · **Confidence:** High (no resend path), Medium (how visible)

**Where**

- `ClientSide/Data/Garage/GarageResync.cs:59-66` asks only for the tool positions and the engine stands.
- `ServerSide/Data/ServerResyncs.cs:112-118` (`ResyncTools`) replays only `ServerData.toolsPosition`.
- `ServerSide/Data/ServerData.cs:183-230` stores `springClamp`, `tireChanger` and `wheelBalancer`, but nothing ever
  reads them back; `SetWheelBalancerState` has no caller at all, so the wheel balancer's state is not even stored.

**Trigger.** A player puts a wheel on the tire changer, a spring in the clamp or a wheel on the balancer; another
player joins late or returns from the junkyard/barn/salon.

**Impact.** The newcomer's station is empty while the first player's holds the item; the group was removed from the
shared inventory when it was mounted, so the newcomer has it nowhere. That player can then put a second item on the
same station, replacing/losing the first one.

**Fix direction.** Send the three station states on join/scene return (and call `SetWheelBalancerState` from the
wheel-balance handler).

---

## 7. Car enter/exit

### CE-1 · "In car" is inferred from proximity, so walking near a car can hide the avatar and freeze its position

**Severity:** Medium · **Confidence:** Medium (the exact UI trigger for the secondary check is unverified)

**Where** — `ClientSide/Data/Player/CarEnterExit.cs:66-121`:

```csharp
if (!currentlyInCar && fpsController != null && !fpsController.enabled)
{
    ...
    if (distance < closestDistance && distance < 3f) { currentlyInCar = true; ... }   // 3 m fallback
}
if (!currentlyInCar)
{
    ...
    if (distance < 1.2f) { currentlyInCar = true; ... }                                // 1.2 m fallback
}
```

`CheckCarState` runs every 0.5 s (`ClientData.cs:75-79`) and sends `PlayerInCarPacket` on any change. Receivers hide
the avatar (`ClientHandle.cs:460`) and `Movement.UpdatePosition` returns before placing or spawning it while
`player.isInCar` (`Movement.cs:55-64`).

**Trigger.** Stand near a car while `FPSInputController` is disabled (any UI/mode that disables it within 3 m), or
stand against a car within the 1.2 m fallback. The player is not in the car, but the packet says they are.

**Impact.** The avatar disappears for everyone and its whole position stream is dropped; when the state flips back,
the avatar pops to the last known position. With several cars around, this can flicker.

**Fix direction.** Prefer a real enter/exit hook (or a single reliable signal) and treat the distance checks as a
last resort with a smaller radius; at minimum, do not set `isInCar` from proximity alone while the FPS controller is
enabled.

### CE-2 · Static `wasInCar`/`lastCarLoaderID` are never reset, so a seated player can stay invisible after a garage reload

**Severity:** Low–Medium · **Confidence:** High

**Where** — `CarEnterExit.cs:16-17`; there is no `Reset()` and `ClientData`/`Client.Cleanup` reset only
`Movement`/`Rotation` (`Client.cs:133`, `Movement.cs:118-123`).

**Trigger.** A player sits in a car, leaves the garage (junkyard/barn/salon) or the garage scene reloads, and comes
back on foot.

**Impact.** The scene change destroyed the avatar but no client cleared `isInCar`; `Movement.UpdatePosition` keeps
returning for that player, and `SpawnPlayer` is never reached from the position path (`Movement.cs:73`), so the
avatar is missing until `CheckCarState` detects a change (2 s load wait + 0.5 s check).

**Fix direction.** Reset the statics on connect/disconnect and on every garage load.

---

## 8. Inventory and economy

### EC-1 · Bulk sell never settles group items, so their money is client-claimed

**Severity:** Medium · **Confidence:** High (code), Medium (frequency of group bulk sales)

**Where**

- `ClientSide/Features/Inventory/BulkSellFeature.cs:107-115` only asks `server.Inventory.Authority` (single items) and
  never `server.GroupInventory`.
- `ClientSide/Data/Player/Inventory.cs:459-469` (`RemoveGroupItemHook`) never adds group UIDs to the capture list, so
  group UIDs never reach the packet.
- Fallback at `BulkSellFeature.cs:89-93`: when no UIDs were captured, the whole total is sent as an ordinary wallet
  delta (`Wallet.OnLocalChange(money, 0)`).

**Trigger.** Bulk-sell a tab that contains wheels/engines/tyres as groups (the feature patches `SellItem(GroupItem, …)`,
so they are expected).

**Impact.** A group-only sale is paid without any host-side removal check; a wheel another player mounts at the same
time is removed by that mount yet still paid. In mixed sales `BulkSale.Settle` prorates by the seen UID count
(`Shared/Economy/BulkSale.cs:28`), so group money is only paid in full if every single item sells.

**Fix direction.** Capture group removals too and settle them against `GroupInventory` in the host handler.

### EC-2 · A car bought away with no linked debit is treated as already paid

**Severity:** Medium · **Confidence:** Medium (depends on the game's write-vs-charge order, which the code's own
comment acknowledges)

**Where**

- `Shared/Features/AwayCars.cs:86`:
  `pending.Add(new AwayCar<T>(from, txn, uid, car, now) { Charged = txn <= 0 });`
- `ClientSide/Features/Garage/SceneReturnFeature.cs:161-166` looks the debit up **backwards**:
  `Purchases.DebitNear(frame, 3f)`, and `Purchases.cs:60-65` only returns a recorded debit.

**Trigger.** Buy a car in the junkyard/salon/barn when the game takes the money later than the next frame (or after
another debit in the 3 s window), while the host's balance cannot cover it.

**Impact.** `txn == 0` means "charged", so `SceneReturnFeature.Place` parks the car and broadcasts it to everyone
even though the ledger never charged; the later/refused debit just resets the buyer's wallet (`Not enough shared
money`) and the car stays in the shared parking lot.

**Fix direction.** Treat "no debit found" as *unknown*, not paid: let the away-car queue wait for a debit to appear
within a short grace period, and refuse (or roll back) if none does.

### EC-3 · An order's payout is granted before the hand-over, and a failed hand-over blocks the order forever

**Severity:** Medium · **Confidence:** Medium–High

**Where**

- `ClientSide/Data/Garage/Campaign/Handover.cs:119-123` — after the host answers `Applied`, the completion is deferred
  to the next frame (`CompleteNextFrame`), while the payout was already granted when the ask was decided
  (`Shared/Economy/Payouts.cs:70`).
- `ServerSide/Data/ServerHandle.cs:591-599` only accepts the end if `Payouts.Done(JobEnd, id)`; `Payouts.cs:76` stores
  `long.MaxValue` for `JobEnd`, so `Done` never expires.

**Trigger.** Hand over an order and lose the answer or the connection between the ask and `EndJobPacket`, or have
`CompleteHandover` throw.

**Impact.** The shared wallet keeps the payout, the hand-over never happens, the order and car remain in everyone's
lists, and any retry is refused forever ("This order was already handed over"). Job-id reuse in the same session
would also be blocked.

**Fix direction.** Grant the payout when the completion is applied (or add a short completion timeout + rollback), and
store a bounded `done` value for `JobEnd` so a failed attempt can be retried.

### EC-4 (minor) · Purchase-to-item linkage keeps only the last debit of the frame

`Purchases.cs:50-54` overwrites a single `(frame, txn)` pair; `Inventory.Flush(txn)` stamps every item added in the
frame with it (`InventoryEndpoints.cs:134`), and the host refuses adds whose txn was refused (`:43`). Two money
actions in one frame (double-click purchase, buy-all, or a part taken off a car in the same frame as a purchase) can
therefore lose a paid item or let an unpaid one through. Fix: queue per-txn item adds instead of one frame txn.

### EC-5 (minor) · A remote garage upgrade is written into the receiving player's personal skill profile

`SkillUpgradeHook.cs:16-36` posts on `UpgradeSystem.UnlockUpgrade` without an `ActionGate.ApplyingServerState` check;
`GarageUpgradeManager.SetUpgrade` applies another player's upgrade inside `ActionGate.Apply()` (`:51-57`), so the hook
sends `SkillChangePacket` for it. That id ends up in the receiver's `PlayerInfo.skillsInfo` (`ServerHandle.cs:167-181`)
and is replayed at the next join. Fix: skip the hook while applying host state and filter on `UpgradeType` (skills vs
garage upgrades).

---

## 9. Lobby, menu and UI

### UI-1 · The host can start the same game twice

**Severity:** Medium · **Confidence:** Medium

**Where** — `ClientSide/Data/NewUI/UIActions.cs:343-380`:

```csharp
public static void StartGame(int save_index)
{
    ... ready check ...
    MelonCoroutines.Start(GameLaunch(save_index));
}
private static IEnumerator GameLaunch(int save_index)
{
    yield return new WaitForEndOfFrame();
    SavesManager.StartGame(save_index);
    ...
    ServerSend.StartPacket(...);
    Server.Instance.GameStarted = true;
}
```

There is no "already launching" flag; `GameStarted` is set a frame late and never checked. The lobby button stays
enabled until the Menu scene unloads (`UILobby.cs:91-100`).

**Trigger.** Host clicks **Start Game** twice (double-click, or a second click before the scene starts loading).

**Impact.** Two `GameLaunch` coroutines → `SavesManager.StartGame` twice and `StartPacket` twice; clients call
`LoadSave`/`StartGame` twice and two `GarageLoader` flows run on the same loader (`GameLoadHook` has no re-entrancy
guard), so the first one to finish flips the ready flags for both. Symptoms: doubled load work, early "ready",
racing spawn/resync coroutines.

**Fix direction.** Set a launching flag before starting the coroutine (and clear it on failure), and have
`GameLaunch` re-check it; disable the button once clicked.

### UI-2 · Every return to the menu does a synchronous GitHub request on the main thread

**Severity:** Low · **Confidence:** High

**Where** — `MainMod.cs:138-141` calls `SavesManager.Initialize()` and `UICore.InitializeUI(sceneName)` on every Menu
load; `UICore.cs:67` starts `CheckModVersion()` each time, and `ContentManager.IsNewVersionAvailable` does
`client.GetStringAsync(url).Result` with a 5 s timeout (`ContentManager.cs:54-64`). The `update_notice` guard
(`UICore.cs:38, 86-95`) only suppresses the **Outdated** popup; the Dev popup (`:100-102`) is shown again on every
return, and this build compares against upstream 0.4.x, so Dev is the expected case.

**Impact.** A hitch of up to 5 seconds on each return to the menu when GitHub is slow/blocked, plus a repeated popup.
`new HttpClient()` per call and a `.Result` on the main thread are not ideal either.

**Fix direction.** Run the check once per process (or once per n hours), off the main thread, and remember the Dev
notice too.

### Minor UI items

- `UILobby.cs:185` creates the **Kick** button with `onClick == null`, and `UIElements.cs:37-41` disables such a
  button; no listener is ever added, so kick can never work from the lobby row.
- `MainMod.cs:138-141` → `SavesManager.Initialize()` (`SavesManager.cs:55-67`) re-runs `MigrateLegacySaves` +
  `LoadExistingModSaves` on every menu return (no once-per-process guard), re-reading the 23 sidecars each time.
- `Inventory.cs:357-416`: the inventory search hooks call `ToArray()` on game `List<T>`s (the repo itself documents
  that this throws when `_items` is null, see the rc12 plan) and collect matches in a `ConcurrentBag`, so results come
  out in arbitrary order and the list can reshuffle on each refresh.

---

## 10. Other minor findings

These are real but lower impact; they are grouped here to keep the main list actionable.

| ID | Where | Problem |
|---|---|---|
| FL-4 | `PartUpdateHooks.cs:22-51` + `PartsUpdater.cs:283-285` | Two fluid postfixes share one one-shot `listen` flag; if `SetLevelAndCondition` reaches both `FluidsData.SetLevel` and `FluidData.SetLevel`, the second one re-sends the applied value (echo). The flag is also set before an unchecked `carLoaders[carLoaderID]` index in `UpdateFluid`, so a throw leaves it false and swallows the next real change. |
| CR-4 | `Shared/SceneManager.cs:79-86`, `CarChecks.cs:36,52-53` | `loadedCars` is not cleared on a scene change (only `needResync = true`), so between `IsGameReady()` and `ResyncCars` `CarChecks` can hash `PartScript`s destroyed with the old scene. |
| CE-3 | `CarEnterExit.cs:148-179` | Engine-sound sync is dead code (`enginePlaying` is always false), and the handler on the other side only logs; harmless but should not be advertised. |
| ES-4 | `EngineStand.cs:118-137` | `HandleEngineStand` reads the static `useAlt` up to 5 s after the action; if the player hovers the other stand/engine in that window, the engine is announced on the wrong stand. |
| EC-6 | `ClientData.cs:75-79`, `Stats.cs:38-58` | `gamemode` is only set ~2 s after the garage is ready (default = Sandbox), so XP/skill-point/upgrade charges in that window take the sandbox path. |
| LK-1 | `ToolsMoveManager.cs:32-55`, `StationGates.cs` | Moving a tool never claims or checks `tool/*` locks, so two players can move the same tool at once. |
| UI-3 | `ServerSend.cs:300-307` vs `ClientHandle.cs:166-186` | The `WheelBalancePacket(fromClient, item)` overload omits the action field the only reader expects; it has no live caller, but it is a trap for the next one. |
| UI-4 | `ServerHandle.cs:818-823` | The `resync` cases for `playerInCar`/`carEngineSound` call handlers that read fields the resync payload never carries; no client sends them today, but any such packet throws. |
| UI-5 | `ClientSide/Data/Garage/Tools/WheelBalancerLogic.cs` | Dead file, not in `CMS21-Together.csproj`; it references `ClientSend` methods that do not exist. Adding it to the build would double-patch the wheel balancer. |
| H-1 | `ServerHandle.cs:584`, `:499`, `Shared/Economy/Payouts.cs:70`, `BulkSellFeature.cs:107-113` | Hardening: the host credits/prices from client-supplied values (sale price, order payout, upgrade cost, bulk-sell UID list) without checking its own car/order/upgrade state. Normal clients cannot hit it, but a modified client can create money or take items. |
| H-2 | `ServerHandle.cs:185,275,610` | Hardening: some handlers still index `connectedClients[fromClient]` / `carLoaders[id]` directly; a disconnect or a stale packet turns into a handler error instead of a clean ignore (the router catches it, but the peer's error count grows toward the 50-error kick). |

---

## 11. Checked and cleared

These areas were reviewed and no mod-caused defect was found (they are listed so the report does not look like it
stopped at the first file):

- **Locks:** client table and server manager agree on key hierarchy, tokens are monotonic, leases use the injected
  monotonic clock, every grant is released on disconnect or scene change, expiry/deny paths are correct.
- **Packet layer:** no field order/type mismatches between `ClientSend`/`ClientHandle` and `ServerSend`/`ServerHandle`
  for the game packet types; every type is registered exactly once; send-queue coalescing never drops the final
  state of a movement key; the router survives every handler exception and the host's own peer is not kicked.
- **Wallet math:** absolute balance + pending deltas; purchases before the first balance are held at 0; single-item
  sales are host-settled; the ledger is idempotent and clamped; warehouse adds use the removal arbiter (won/lost/
  5 s timeout) and UID dedupe.
- **Progress:** `ProgressGuard` only rejects decreases, XP/level field order matches on both sides, skill points are
  per player.
- **Jobs (other paths):** no double payout/XP for a hand-over, the reward box belongs to the hander, accept-cancel
  returns the order to the board, `GarageUpgradeHooks` replays the `initialSent` marker to late joiners.
- **Cars (other paths):** `CarLoaderRegistry` is pointer-based and `-1` safe; `ParkSlots` is bounds-checked;
  `CarHash`/`CarRoster` have a two-report debounce; `ResyncCar` uses `TryGetValue`; `ParkHook`'s refund path restores
  the previous car; `SceneReturnFeature`'s queue/refund guards are sound.
- **Player/UI:** no position/rotation streaming outside the garage or while not ready; no double hook registration;
  no per-frame logging in the update paths; feature config is host-authoritative; `PlayerLeft`/`Disconnect` teardown
  order is safe; `MainMod`'s per-frame work is bounded.

---

## 12. Suggested fix order

1. **JB-1 + JB-2** (hand-over bookkeeping) — one small change each, and they unblock the core loop and late join.
2. **WL-1 + WL-2** (welder) — null-check the host handler and stop leaking `listen`.
3. **FL-1** (store fluids) and **FL-2** (wash branch swap) — cheap, and they stop resyncs corrupting state.
4. **CR-1** (`TryGetValue` + scene guard in the three part hooks) and **FL-3** (`carPosition` on load).
5. **JB-3 + JB-4** (server order bookkeeping/dedupe) — needed before advertising late join as reliable.
6. **EC-1/EC-2/EC-3** (bulk-sell groups, away-car debit, hand-over payout) — economy integrity.
7. **CE-1/CE-2**, **ES-1/ES-2**, **ST-1**, **UI-1** — visible but non-blocking; each has a clear repro.
8. The remaining minors and the hardening items.

Every fix should get its own `.implementation_plans/<date>-<name>/PLAN.md`, a regression test where the logic is
headless-testable (server handlers, queue/ledger, paintshop/away-car rules), and an in-game two-client check
because most of these paths only exist in the real game.

---

### How this audit was produced

Seven parallel code reviews (cars, parts, tools/stations, economy/inventory, jobs/upgrades, net/locks, player/UI)
over the current working tree, followed by a manual re-read of every finding quoted above. Line numbers refer to
commit `30413e8` (0.5.0-rc14). Findings that depend on the game's own behaviour (rather than the mod's code) say so
in their confidence note. The findings were fixed in 0.5.0-rc15; this document keeps the pre-fix line numbers so the
report stays reproducible against that commit.