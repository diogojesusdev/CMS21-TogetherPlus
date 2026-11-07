# Harmony patches on game value types corrupt car data (job car never sent + menu-return GC crash)

**Status:** done in 0.5.0-rc35. In-game check (job car on the client, menu return on the host) still to do by the
players; see Testing.

## Goal

> "Yesterday this multiplayer mod crashed again. [...] the car order that the host picked up wasn't visible to me
> (client). the crash happened when trying to leave the game for me but the host did something else and the game also
> crashed for him. [...] How can we tackle this without having to constantly test the game, find new crash, fix,
> repeat"

The player picked option 3 of the proposed approach ("checks right after the mod writes game data, so damage is
reported where it happens instead of crashing at the menu later") and asked to fix the latest problems now. An
in-game autotest was declined (not convenient).

## Findings

### The logs

- **Client (this machine, rc34, 2026-11-06 23:36):** the host's job car sent `jobAction`, `carFluid` x4 and a second
  `jobAction`, but never a `loadCar`; the first body part then hit `[Apply] Body part #18 dropped: car 4 is not loaded
  here`. At 23:39:05 `Steam connection lost (Remote_Timeout)`: the host died. This client did **not** crash: the
  Unity log ends with a normal menu load and `Preferences Saved!`, and there is no crash folder or WER event. The
  host's own log is on the other PC and was not available.
- **Every host session on this machine since 2026-11-05** logs the same job car result:
  `is not in the garage array yet ... [0]=<empty>, [1]=<empty>, [2]=<empty>, [3]=<empty>, [4]=<empty>` and, when it
  waited long enough, `never turned up in the garage array within 12s and was not sent to the other players`. Not a
  single `sent to the other players` or `caught as the game saved it` line exists. The rc12, rc24, rc26/27 and rc30
  fixes for this were never seen working in a log.
- **Every crash folder on this machine** (11-05 20:38, 20:43, 21:17, 21:26, 23:55; 11-06 22:54) follows a job car
  load in the same session, seconds to minutes earlier. The rc33/rc34 dumps fault in the GC reading a reference
  field that holds `0x000000004117b940` / `0x00000000410dd6c8`: a 32-bit **float** (9.48 / 8.86) with a zero upper
  half, not a pointer.

### The game code (disassembled from `GameAssembly.dll` with the Cpp2IL RVAs)

- `CarLoader.SaveCarToFile()` computes `Helper.GetIndexFromCarLoaderName(name)` and tail-jumps to
  `SaveCarToFile(index, false)`.
- `SaveCarToFile(int index, bool toParking)` builds the 440-byte `NewCarData` on its stack and calls
  `GameDataManager.SaveCar(carData, index, toParking)` **synchronously**, then sets `SaveComplete`. It is the only
  caller of `SaveCar`.
- `SaveCar` forwards to `SaveCarInGarage` / `SaveCarInParking`, which store the struct into
  `CurrentProfileData.carsInGarage[index]` / `carsOnParking[index]` (index 0..4 for the garage).
- `CarDataReader`'s layout matches the Cpp2IL offsets exactly (`carToLoad` 0x0, `index` 0x10, `color` 0x18,
  `gearRatio` 0x108, `CarInfoData` 0x114, stride 0x1B8), so the reader is right and the slot really is "empty".

### The root cause: MelonLoader 0.5.7 cannot patch methods that take a game struct

`MelonLoader.Support.HarmonyMethodPatcher` (decompiled from `Dependencies\SupportModules\Il2Cpp.dll`) builds a native
shim for every patched method. `ConvertArgument` has no case for IL2CPP value types: `NewCarData` inherits
`Il2CppObjectBase` (through `Il2CppSystem.ValueType`), so the shim wraps the **raw pointer to the unboxed struct** as
if it were a boxed object (`new NewCarData(ptr)`). When the patched method then runs the original, Unhollower's own
wrapper passes `il2cpp_object_unbox(carData)` = that pointer **+ 16**. The game therefore copies a car record shifted
by 16 bytes into the profile:

- `carToLoad` (0x0) receives the old `index` field (0x10) = 0 -> every slot reads `<empty>`: the job car is never
  found and never sent.
- Every reference field receives whatever lay 16 bytes later, e.g. `gearRatio` (0x108) <- `CarInfoData` (0x118), a
  float: the GC later dereferences `0x4117b940` and the game dies, typically in the unload after `GarageLoader.Save`
  on the way to the menu. The same garbage is what the game wrote to disk ("Slot 4: Corrupt").

This happens for **any** patch on such a method - even an empty prefix - because the shim converts every argument.
The same mistake was behind rc33 ("a bool is an object at address 1"). It also explains why rc34's shutdown check
could not see it: `CarDataReader.TryRead` treats a null `carToLoad` as an empty slot.

Patches in this build on methods that take, return or are declared on an IL2CPP value type:

| Patch | Target | Since |
| --- | --- | --- |
| `SceneReturnFeature` prefix + finalizer | `GameDataManager.SaveCarInGarage/SaveCarInParking/SaveCar(NewCarData, ...)` | 11-04 |
| `CarSaveHooks` postfixes | `GameDataManager.SaveCar/SaveCarInGarage(NewCarData, ...)` | rc30 |
| `ParkHook.SaveCarInParkingPurchaseHook` | `GameDataManager.SaveCarInParking(NewCarData, int)` | Phase 6 |
| `PartUpdateHooks.SetLevelAltHook` | `FluidsData.SetLevel` (instance method **of a struct**: `this` is shifted) | rc15 |

## Approach

1. **Remove every unsafe patch**, keeping what each did through a safe target:
   - `CarSaveHooks`: deleted. `CarSpawnManager.LoadJobCar` reads the slot right after `SaveCarToFile(index, false)`
     (the write is synchronous), keeping the poll loop only as a fallback.
   - `SceneReturnFeature`: the before/after snapshot moves to `CarLoader.SaveCarToFile(int, bool)` (`int`/`bool`
     parameters; every away purchase reaches the profile through it, `SaveCar` has no other caller).
   - `ParkHook`: the purchase-refund prefix moves to `CarLoader.SaveCarToFile(int, bool)` with `toParking`; the
     `listen` flag goes (the mod's own `SaveCarInParking` calls no longer pass through a hook).
   - `PartUpdateHooks.SetLevelAltHook`: deleted. For the brake, coolant, steering and washer lists
     `FluidsData.SetLevel` forwards to `FluidData.SetLevel` (the safe `SetLevelHook`), but for engine oil it writes
     `Oil.Level` directly (`GameAssembly+0x128B7A1`), so a new postfix on `FluidRefillLogic.OnDisable` sends the oil
     level once a refill ends. (The old hook never really synced oil: with `this` shifted it wrote the float into the
     wrong object.)
2. **Refuse such patches from now on** (`Shared/Safety/PatchSafety.cs`):
   - `GameFeature.Target` throws for an unsafe target, so a feature fails on its own with the reason instead of
     corrupting memory.
   - At startup every patched method in the process (ours and other mods') is audited and an unsafe one is logged as
     an error with its owners. Harmony cannot undo it (unpatching rebuilds the same shim), so this is a report.
   - `Tests/HarmonyTargetTests.cs` reads the built DLL and `Libs\` (metadata only, `MetadataLoadContext`: .NET
     Framework cannot load the unhollowed `UnityEngine.Object`) and fails the build when a `[HarmonyPatch]` attribute
     targets such a method. Against the rc34 DLL it listed exactly the four attribute patches above.
3. **Option 3: check the cars where they are written** (`Shared/Safety/HeapCheck.cs`,
   `Shared/Data/Vanilla/Cars/CarSlotAudit.cs`): walk every reference field the collector will follow in a car slot
   (all of `NewCarData`, nested inline structs included, from il2cpp's own field list), and report a field that holds
   something that is not an object, naming who wrote it. The bad field is cleared so the collector cannot crash on it.
   Run after the game's `SaveCarToFile` (postfix), after the mod's own writes (job car read, parking apply, away car
   placement and hand-over, menu profile rebuild) and at shutdown (replacing rc34's `TryRead` check).

Alternatives rejected:

- Fixing MelonLoader's shim from the mod (box the struct before wrapping it): patching the loader's code generator is
  invasive, must run before `PatchAll`, and would hide the rule instead of enforcing it.
- Unpatching unsafe targets at runtime: HarmonyX's unpatch keeps a wrapper and MelonLoader re-creates the same
  converting shim, so it would not help.
- Another in-game autotest harness: declined by the player.

## Files

- `ClientSide/Data/Garage/Car/CarSaveHooks.cs` (deleted)
- `ClientSide/Data/Garage/Car/CarSpawnManager.cs`
- `ClientSide/Data/Garage/Car/ParkHook.cs`
- `ClientSide/Data/Garage/Car/PartUpdateHooks.cs`
- `ClientSide/Features/Garage/SceneReturnFeature.cs`
- `ClientSide/Features/GameFeature.cs`
- `Shared/Safety/PatchSafety.cs` (new)
- `Shared/Safety/HeapCheck.cs` (new)
- `Shared/Data/Vanilla/Cars/CarSlotAudit.cs` (new)
- `Shared/Data/Vanilla/Cars/CarDataReader.cs` (slot address for the audit)
- `Shared/Shutdown.cs`, `Shared/SavesManager.cs`, `MainMod.cs`
- `Tests/HarmonyTargetTests.cs` (new), `Tests/Tests.csproj` (MetadataLoadContext, linked `PatchRules.cs`)
- `AGENTS.md` (the rule, under IL2CPP pitfalls)

## Deviations

- The test uses `System.Reflection.MetadataLoadContext` instead of Mono.Cecil, so it shares `PatchRules` with the
  runtime guard.
- The guard checks in `GameFeature.Prefix/Postfix/Finalizer` (not only `Target`), so a feature passing a
  `MethodInfo` directly is covered too.
- Added `PartUpdateHooks.OilRefillDoneHook` (found by review: engine oil does not go through `FluidData.SetLevel`).
- `HeapCheck` refuses to check (and so to clear) anything when a reference offset falls outside the struct or is not
  pointer-aligned, so a misread layout cannot make the audit itself damage a car.

## Testing

- `.\build.ps1 -Package`: all tests, including the new Harmony target test (it must list no unsafe target; it was
  checked to flag the old `CarSaveHooks` targets before they were removed).
- In game (host + client): accept a car order on the host. The host log must show
  `[Car] Job car <car> (loader N) sent to the other players.` and the client must see the car. Then go back to the
  menu on the host: no crash, and no `[CarAudit]` error in either log. A `[CarAudit]` error names the writer and the
  field if anything still damages a car.
- Startup log: no `[Patches]` error.
