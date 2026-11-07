# Engine oil refill never reaches other players

## Goal
User: "when my friend filled the oil and then I checked it, it wasn't full for me but was for him. can you investigate and fix?"

## Findings
- Host log (rc37): every oil refill ends with one `carFluid` packet of **18 B**, while all other fluid packets are 27 B (17:53:01 #2, 17:54:53 #1,
  18:16:05 #2, 18:17:59 #1). The 9 missing bytes are the `ModCarFluid` (type, id, reservoir): `FluidsData.Oil` has no `CarFluid`,
  so `OilRefillDoneHook` sent a `ModFluidData` with `CarFluid == null`.
- Everything downstream drops such a packet: `ServerData.UpdateFluid` and `PartsUpdater.UpdateFluid` both return on `fluid?.CarFluid == null`.
  The server still relays it, so it looks sent in the log but nobody applies it, and the stored car keeps the old oil (a resync reverts it too).
- Other fluids (brake, coolant, steering, washer) go through `FluidData.SetLevel`, which has a `CarFluid`, so they sync.

## Approach
- Sender: the oil packet carries a `ModCarFluid` of type `EngineOil` (`ModFluidData.ForOil`).
- Host store: keeps the oil without a `CarFluid` (`WithoutCarFluid`), as a car load stores it.
- Receiver: engine oil is written into `FluidsData.Oil.Level/Condition` (how the game's refill does it) instead of `SetLevelAndCondition`.
- Rejected: patching `FluidsData.SetLevel` (game value type, see AGENTS.md).

## Files
- `Shared/Data/Vanilla/Cars/ModCarFluid.cs`, `ModFluidData.cs`
- `ClientSide/Data/Garage/Car/PartUpdateHooks.cs`, `PartsUpdater.cs`
- `ServerSide/Data/ServerData.cs`
- `MainMod.cs` (version)

## Testing
- `.\build.ps1 -Package` (the touched types depend on game assemblies, so no new unit test).
- In game: one player refills engine oil; the other checks the level and sees the same; a player who rejoins sees it too.

## Status
Done in 0.5.0-rc39 (untested in game).
