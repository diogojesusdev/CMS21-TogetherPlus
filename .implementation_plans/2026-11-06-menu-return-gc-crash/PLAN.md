# Menu return still crashes: a reference field holds a non-pointer at GC time

**Status:** diagnostic build prepared (0.5.0-rc34). Root cause still open; this build makes the next crash name
the bad car slot instead of guessing.

## Goal

> "Game crashed again when going back to main menu...."

Host, solo session, `JavaleidaShop` on **rc33** (deployed), 2026-11-06 22:53. The garage loads, a job car is taken
(`car_luxorbaronne`), the mod saves generation 23, the player goes to the menu, and the game dies ~10 s later.

## Findings

**The dump** (`%LOCALAPPDATA%\CrashDumps\Car Mechanic Simulator 2021.exe.5360.dmp`):

- `0xC0000005` read of `0x000000004117b940`, at `GameAssembly.dll + 0x2B0DC7` (module base `0x7fffafb30000`).
- The instruction is `mov rax,[rdi]` with `rdi = 0x4117b940`; the code around it matches what rc33 analysed: fetch a
  field value, then `test al,1` / `and rax,~1` on it as an object header.
- The stack is a repeating walk of the runtime's class/field machinery (`+0x2B08FE`, `+0x2B0D7C`, `+0x2B1070`, …,
  nearest export `il2cpp_class_for_each`), entered from `UnityPlayer!UnityMain`.
- **Identical to the 2026-11-05 23:55 dump** (same faulting RVA, same stack, value `0x410dd6c8` there). rc33's
  `CopyInline` barrier fix did not remove it, so something else puts a value that is not a heap pointer into a field
  the GC treats as a reference.

**Unity's own log** (`%LOCALAPPDATA%\..\LocalLow\Red Dot Games\Car Mechanic Simulator 2021\Player.log`) ends with:

```
[SteamSave] -> SendSave()profile4.cms21b
GarageLoader:Save(Boolean)
Unloading 2 Unused Serialized files (Serialized files now loaded: 3)
Crash!!!
```

So the fault happens in the asset unload that `GarageLoader.Save` triggers, ~10 s after the mod's shutdown release
(`[Shutdown] Released the mod's references...`). The mod's own `GC.Collect` + `Resources.UnloadUnusedAssets` in that
release completes without faulting, so the bad reference is either written after it (during the teardown) or is only
reached by the later collection.

**The profile on disk was already corrupt once**: at startup `[Saves] Slot 4: Corrupt.` and the mod restored
generation 21 (2026-11-05 23:54). A corrupt save and a non-pointer in a reference field come from the same place: a
car slot whose bytes do not belong in its reference fields.

**Why the last four fixes missed**: none of them could see *which* object holds the bad reference. The minidump has
registers, stacks and a few pages only; the object being walked (`r13 = 0x19341d22838`, field descriptor
`rbx = 0x192d9f48948`) is heap memory that is not in the file.

## Approach

1. **Name the bad slot in the log**, at the last moment before the crashing unload: in `Shutdown.ReleaseGameReferences`,
   before the collection, run `CarDataReader.TryRead` over every slot of `CurrentProfileData.carsInGarage` and
   `carsOnParking`. That reader validates exactly the reference fields the GC walks (`carToLoad`, `UId`,
   `engineSwap`, `color`, `factoryColor`, `gearRatio`, tires/rims arrays, `BodyPartsData`, `PartData`,
   `BonusPartsData`), so a corrupt slot is reported as `carsInGarage[3] is not a readable car: <field> ...`. Guarded,
   so a failure cannot keep the release from running.
2. **Stop the mod from walking game data during the teardown**, where a wrapper built from a half-destroyed object
   becomes a GC handle: `CarSaveHooks.Record` (postfix on `GameDataManager.SaveCar*`, runs for the game's own save),
   `SceneReturnFeature.Before/After` (same methods) and `CarSpawnHooks.DeleteCarHook` now return immediately while
   `Shutdown.Leaving`.
3. With the slot named, the writer is a short list: `DataHelper.CopyCars`/`CarDataReader.CopySlot`, the game's own
   `SaveCarInGarage` (called by `CarLoader.SaveCarToFile`), or the profile rebuild in `SavesManager`. Fix that path,
   then repeat this build's check to prove the slot stays clean.
4. Full dumps are armed as well (`HKCU\...\LocalDumps\Car Mechanic Simulator 2021.exe`, `DumpType=2`, folder
   `obj\fulldumps`), so the next crash also brings the heap if the log is not enough. Remove the key afterwards.

Alternatives rejected:

- Another speculative caller fix (rc30-rc33 pattern): the crash is byte-identical after each, so the next guess has
  the same odds.
- Turning off the mod's shutdown collection: the fault is in Unity's later collection; removing ours only loses the
  first chance to observe the heap.
- Disabling the GC for the session: unbounded growth, and Unity collects on its own schedule anyway.

## Files

- `Shared/Shutdown.cs` (check the profile cars before the release collection)
- `ClientSide/Data/Garage/Car/CarSaveHooks.cs` (no capture during the teardown)
- `ClientSide/Features/Garage/SceneReturnFeature.cs` (no capture during the teardown)
- `ClientSide/Data/Garage/Car/CarSpawnHooks.cs` (no delete reporting during the teardown)
- `MainMod.cs` (version)

## Testing

- `.\build.ps1 -Package` (full suite) and deploy.
- In game: load `JavaleidaShop`, take a job car, save, go back to the menu. The log must show either
  `[Shutdown] carsInGarage[N] is not a readable car: <detail>` (then that slot's writer is the bug) or no car
  complaint (then the corrupt object is elsewhere and the full dump names it).

## Status

diagnostic build in 0.5.0-rc34.
