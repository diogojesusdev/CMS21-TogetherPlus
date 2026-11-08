# Fluids revert when another player walks back into the garage

## Goal
User: "I was draining and filling all fluids, when i started draining the brake booster fluid, both the power
steering and windshield fluids got reset back. fix ig"

## Findings
Host log `MelonLoader_26-11-07_23-52-35.362.log`. Every time M4R3C0 (#3) comes **back** into the garage, 7-8 s
later #3 sends the car's *load-time* fluid values and they overwrite the player who is working on the car:

```
23:55:31.923 [Scene] M4R3C0 (#3) is now in garage.
23:55:34.7-.8 [Server] <- resync from #3 (+68 similar skipped)
23:55:39.415 [Fluid] #1 car 4 Brake -> level 0.60, condition 1.00   <- #1 filling the brake fluid
23:55:39.417 [Fluid] #3 car 4 Brake -> level 0.24, condition 0.24   <- #3's car load, the car's original values
23:55:39.418 [Fluid] #3 car 4 WindscreenWash -> level 0.41, condition 0.91
23:55:39.479 [Fluid] #1 car 4 Brake -> level 0.26, condition 0.24   <- #1's game reverted
```

The same thing at 00:01:25 (#3 enters) -> 00:01:29, which is the screenshot: `PowerSteering 0.51` and
`WindscreenWash 0.41/0.91`. Only the fluids the working player had changed come back, because
`PartUpdateHooks.Changed` filters the rest as unchanged. Draining the brake booster is a coincidence of timing, not
the trigger - the trigger is another player's garage (re)load.

Root cause: `PartUpdateHooks.SetLevelHook` (postfix on `FluidData.SetLevel`) sends **any** level the game writes.
Loading a car writes every fluid level through that method, so a car being rebuilt locally - the garage reloading it
from the local save on a scene return, or `CarSpawnManager.LoadCarFromServer` applying the host's copy - broadcasts
its stale levels as if the player had just poured them. Unlike every other hook in the file, `SetLevelHook` checks
neither `ActionGate.ApplyingServerState` nor `GameLoadHook.IsGameReady()` nor the car's own load state. The
frame-wide `Suppress()` window only covers `PartsUpdater.UpdateFluid`, which is a single applied packet, not a car
load that spans many frames.

Nothing is lost by not sending during a load: a car's fluids travel inside the car payload itself
(`ModNewCarData.FluidsData`, sent by `LoadCarPacket` / the host's resync).

## Approach
A fluid level is only sent when it is this player's own action on a car that is finished loading here:

- new `Shared/Data/Vanilla/Cars/FluidSendRules.ShouldSend(...)` - a pure predicate (connected, not applying host
  state, not inside the apply frame, game ready, in the garage, car tracked, car ready, car not waiting for a
  resync), unit tested;
- `PartUpdateHooks` gets a `LocalFluidChange(carLoaderID)` helper that feeds it, used by `SetLevelHook` and
  `OilRefillDoneHook`;
- a level that is *not* sent still updates the `LastFluid` baseline (`Remember`), so the loaded value is the
  reference for the player's next real change instead of a value from before the reload.

The two states that identify a car load are already tracked: `ModCar.isReady` (false until `PartsReferencer`
finished, so it covers the host's copy arriving) and `ModCar.needResync` (set by `SceneManager` when the player
leaves the garage and cleared only by a fresh `ModCar`, so it covers the stale entry the garage's own car loop sees
on a scene return).

Rejected: having the host ignore fluids from a client that recently changed scene (the host cannot know when a
client's car finished loading); rejected: widening `Suppress()` to the whole load (a load has no single owner and a
missed `Unsuppress` would silence fluids for good).

## Files
- `Shared/Data/Vanilla/Cars/FluidSendRules.cs` (new)
- `Tests/FluidSendRulesTests.cs` (new)
- `ClientSide/Data/Garage/Car/PartUpdateHooks.cs`
- `CMS21-Together.csproj`, `Tests/Tests.csproj`, `Tools/BotHarness/BotHarness.csproj`
- `MainMod.cs` (version)

## Testing
- `.\build.ps1 -Package` (new NUnit cases for `FluidSendRules`).
- In game: player A drains and refills brake/power steering/washer fluid on a car while player B walks out of the
  garage and back in. A's levels must not jump back, and the host log must show no `[Fluid] #B ...` line right after
  `[Scene] ... is now in garage`.
- Still works: a player's own drain/refill is seen by the others; a joiner sees the host's levels.

## Status
Done in 0.5.0-rc49.
