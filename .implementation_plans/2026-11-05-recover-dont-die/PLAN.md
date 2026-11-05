# Recoverable failures must not end the session

**Status:** done in 0.5.0-rc25

## Goal

> "can't we avoid crashing the entire fucking game after these stupid errors that feel recoverable?"

The maintainer is right about the principle: a bad car, a bad packet or a car that fails to load from its file should
cost **that car**, not the whole session. This plan covers the two recoverable failures we have evidence for, and
states the limit clearly.

## Findings

**What can be recovered, and what cannot.** A managed exception is recoverable and the mod already has the machinery
(`Shared/Safety/Guard.cs`, `PacketRouter.Dispatch`, `ClientSend.ResyncCar`). A native access violation
(`0xc0000005`) is *not*: it happens inside `GameAssembly.dll`, .NET has no frame there, and the process state after it
is undefined — resuming is not a real option. The only defence is to **never make the call**: check the pointer or the
payload first, and skip the unit that fails. The 2026-11-05 car order crash (fixed in rc24) was exactly a call that
was made without that check.

**Two recoverable failures that were still fatal in effect:**

1. *A car packet the client cannot trust.* `LoadCarFromServer` takes the host's `ModNewCarData` and hands it straight
   to `data.ToGame()` → `carLoader.LoadCarFromFile(carData)`. Nothing checks the payload: a car with no name, absurd
   counts or null entries builds a `NewCarData` the game then walks. This is the same "trusted data used to build
   game objects" shape as the rc24 crash, only on the receive side.
2. *One car failing to load from its file aborts the whole garage load.* In the 2026-11-05 18:26 log:
   `CarLoader.LoadCarFromFile()` threw `Il2CppException: NullReferenceException: routine is null` at 18:28:34 *inside*
   `VenillaLoad`'s car loop — after `car.DeleteCar()` had already emptied that loader. The exception killed the
   coroutine: every remaining car was left deleted, the rest of the load never ran, and the only trace was one
   MelonLoader line. A load that fails for one car should fail for one car.

## Approach

1. `Shared/Data/Vanilla/Cars/CarPayloadRules.cs` (new, pure): the limits a received car must pass — usable name,
   sane counts, no null entries, sane wheel/gear arrays.
2. `ModNewCarData.IfBroken(out reason)` uses those rules. `CarSpawnManager.LoadCarFromServer` checks it **before**
   touching `GameData`; an unusable payload logs the reason and sends `ResyncCar(carLoaderID)` (the rollback the packet
   handler already uses) instead of applying it. The loader id is bounds-checked, and `ToGame()` runs under `Guard` so
   a managed failure also resyncs instead of half-applying.
3. `GameLoadHook.VanillaLoad`: load each car in its own `Guard.Run`, so a throw names the car, skips its wait loop and
   lets the remaining cars load (`TryStartCarLoad`). `GameSettings` flags are still restored afterwards.
4. Tests for the pure rules (they are the only linkable part; `ModNewCarData` needs Unhollower).

Rejected: (a) catching `0xc0000005` with a vectored exception handler and resuming — undefined state, would turn a
clean exit into corrupted saves; (b) blanket try/catch around whole apply coroutines without validation — it cannot
prevent a native fault, only hide the managed trace; (c) kicking a peer on its first bad car — too harsh, and the
resync already repairs it.

## Files

- `Shared/Data/Vanilla/Cars/CarPayloadRules.cs` (new)
- `Shared/Data/Vanilla/Cars/ModNewCarData.cs` (`IfBroken`)
- `ClientSide/Data/Garage/Car/CarSpawnManager.cs` (validate + resync before applying)
- `ClientSide/Data/GameLoadHook.cs` (one car's failure no longer aborts the load)
- `Tests/CarPayloadRulesTests.cs` (new), `CMS21-Together.csproj`, `Tests/Tests.csproj`, `MainMod.cs` (version)

## Testing

- New NUnit tests: a usable car passes; empty/oversized name, absurd counts, a null part entry and oversized wheel
  arrays are rejected, each with a reason.
- Full suite (must stay green: 2716 + new).
- In-game: accept a car order with a valid save (car syncs as before); a broken car now logs
  "The car for loader N was not applied (...) asking for it again" and the session keeps running. Garage load: with the
  `routine is null` failure the log now names the loader and the remaining cars still load.

## Status

done in 0.5.0-rc25: `CarPayloadRules` + `ModNewCarData.IfBroken`, the receiver validates and resyncs, and each car load in the garage loop is its own `Guard.Run`. 6 new tests, full suite 2722.

## Deviations / follow-ups

- The `routine is null` **root cause** is not fixed here; the loop now survives it and names the car. Root cause remains
  in `TODO.md`.
- Other apply paths (profile snapshot, inventory, jobs) still trust their payloads; they are class factories over
  network data with no raw pointers, so they are a smaller risk and are left for a later pass.
