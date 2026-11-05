# Welder, fluids, paint position and part hooks

**Status:** done in 0.5.0-rc15

## Goal
Fix `docs/BUG_AUDIT.md` WL-1/WL-2, FL-1/FL-2/FL-3, CR-1/CR-2, PT-1/PT-2: the welder host handler throws and the
receiver leaks `listen`; the host never stores fluid changes; the wash branches are swapped; `carPosition` is lost on a
host load; the part hooks index `loadedCars` unchecked; a car load runs twice; connected parts with an unresolved id
are sent as panel 0; `unmountWith` is matched positionally without bounds.

## Approach
1. Null-check `SetWelder` (`body`/`details`) and update the stored `ModNewCarData.BodyPartsData` so late resyncs carry
   the repair.
2. Wrap `UseWelder` in `ActionGate.Apply()` and restore `listen` in `finally`.
3. Store every fluid change in `ServerData.CarSpawnDatas[loaderID].FluidsData` (match `FluidType` + `ID`).
4. Swap the interior/exterior branches in `SetCarWash`.
5. Pass `data.carPosition` to the `ModCar` built in `LoadCarFromServer`.
6. `TryGetValue` + garage-scene/QoL guards in `HideHook`, `TakeOffCarPartHook`, `SwitchCarPartHook`.
7. Make `LoadCarFromFileHook` a `bool` prefix returning `false` after the manual load (one load, not two).
8. Only add connected body parts whose lookup succeeded; `TryGetValue` on the receiver.
9. Bound `unmountWith` by both arrays and match by id when possible.

## Files
- `ServerSide/Data/ServerData.cs`
- `ClientSide/Data/Garage/Tools/WelderLogic.cs`
- `ClientSide/Data/Garage/Car/{PartUpdateHooks,PartsUpdater,CarSpawnManager,CarSyncHooks,CarSpawnHooks}.cs`
- `Shared/Data/Vanilla/Cars/ModCarPart.cs`

## Testing
- New pure tests for the fluid merge (`UpdateFluid` is in `ServerData`, which needs the game; test a small pure helper
  instead), plus existing `DtoSerializationTests` / `PartUpdate` coverage.
- Build + suite; in-game: weld a body with two clients (both see it after resync), fill oil and return from the
  junkyard (level stays), wash exterior and resync (interior condition unchanged).

## Deviations / follow-ups

- All listed findings were fixed. The two-fluid-hook echo (report FL-4) and the stale `CarChecks` references after a
  scene change (report CR-4) were left as minor: both need a live game to verify the hook call order.
- `SetWelder` now also updates `ModNewCarData.BodyPartsData`, so a late join carries the weld without waiting for the
  desync detector.
