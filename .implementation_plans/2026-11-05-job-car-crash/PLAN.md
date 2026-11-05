# Crash when a car order spawns its car

**Status:** done in 0.5.0-rc24

## Goal

> "game crashed just when I was in a multiplayuer game but alone and I picked up a car order. the car spwned and then
> game crashed and exited!"

Host, solo session, `JavaleidaShop` save, rc23. The customer car `car_katagiritamago` spawned on car loader 4, the mod
read its data to send it to the other players, and the game died with a native access violation.

## Findings

**The crash** (`%LOCALAPPDATA%\CrashDumps\...20388.dmp`, analysed with cdb):

- `Car Mechanic Simulator 2021.exe` faulted with `0xc0000005` on the main thread, `GameAssembly.dll+0x872E0`.
- That address is `mov rax,qword ptr [rcx]` / `ret` (the folded "return this->field0" getter that the export table
  also names `il2cpp_object_get_class`). `rcx` was `0x00000000411d3e4c`: an unmapped, 32-bit looking value, so the
  instruction could not read the object it was given.
- The return address on the stack (`0x1d5e8bdc4ba`) is Mono JIT code, not a module. So the caller is **managed** code.
- The only managed caller of that getter is `UnhollowerBaseLib.Il2CppObjectBase::.ctor(IntPtr)`:
  `il2cpp_object_get_class(pointer)` is the first thing it does to every pointer it is handed (verified by reading the
  IL of `Libs\UnhollowerBaseLib.dll`). **Read: managed code handed Unhollower a pointer it read out of game memory
  without checking it.** Uncatchable, hence the silent exit.

**Where in the log** (`MelonLoader\Logs\MelonLoader_26-11-05_18-26-43.901.log`):

```
[18:29:05.595] [Car] Local job car load of car_katagiritamago on loader 4 (place 0).
[18:29:07.819] [Car] Job car car_katagiritamago loaded on loader 4 (customer=True).
[18:29:08.941] [Car] car_katagiritamago (loader 4) parts referenced: 29 body, 74 engine, 12 other, 6 suspension.
[18:29:09.127] [WARNING] [ModNewCarData] BodyPartsData reports 4 item(s) but has no storage; left empty so the car still syncs.
```
then nothing: `LoadJobCar`'s next `Log.Info("... sent to the other players.")` never ran and the retry warning never
ran either, so the game died **inside `new ModNewCarData(carData, ...)`**, after the `BodyPartsData` read.

**Why the car data was garbage.** `carsInGarage[4]` was read through `Il2CppReferenceArray<NewCarData>`, and that is
unsafe for this struct:

- `ProfileData.carsInGarage` is `Il2CppReferenceArray<NewCarData>`; `NewCarData` is an inline il2cpp *value type*, so
  the element stride must be `il2cpp_class_value_size(NewCarData)` (hundreds of bytes), not `IntPtr.Size`.
- `Il2CppReferenceArray<T>` caches that stride in its **static constructor**:
  `ourElementTypeSize = IntPtr.Size;` then, only `if (Il2CppClassPointerStore<T>.NativeClassPtr != 0)`, it asks
  `il2cpp_class_is_valuetype` / `il2cpp_class_value_size`. `Il2CppClassPointerStore<NewCarData>.NativeClassPtr` is
  only set by `NewCarData`'s own static constructor (see the generated `.cctor`).
- `Il2CppClassPointerStore<NewCarData>` is zero until something *creates* a `NewCarData` (`DataHelper.Copy`,
  `ModNewCarData.ToGame`, `SceneReturnFeature`). In this session the first use of a `NewCarData` **array** came from
  the job car flow itself, so the array class's cctor ran with the class pointer still zero: stride `8`,
  `ourElementIsValueType = false`, and the wrong stride is cached for the rest of the process.
- With stride 8, `cars[4]` lands 32 bytes into the first car's struct: `carToLoad` is not our car (so the name match
  fails and `FindGarageIndex` silently falls back to the preferred slot), `BodyPartsData` is some other object or
  field (the "4 item(s) but has no storage" list), and the conversion walks garbage until it wraps a stale pointer.

`FindGarageIndex` returning `preferred` when no slot matched is what let this garbage through at all: it is documented
as "one bad lookup must not lose it", but it also hides a wrong read.

**Second, separate defect seen in the same session (not this crash):** the garage load aborted at 18:28:34 — our
`GameLoadHook.VanillaLoad` copy calls the parameterless `CarLoader.LoadCarFromFile()`, which threw
`Il2CppException: NullReferenceException: routine is null`, after `car.DeleteCar()` had already emptied every loader.
That is a real bug (garage cars deleted, not reloaded) but it is orthogonal to this crash and is left as a follow-up
below; it needs its own root cause (the vanilla overload vs. our patched `(string)` overload).

## Approach

Read a profile car **deterministically and defensively**, and never convert one that fails the checks.

1. `Shared/SafeMemory.cs`: `VirtualQuery`-based `IsReadable(address, size)` and `LooksLikeObject(pointer)` (the first
   word, the Il2CppClass*, must be readable too). Pure, no game types, Windows-only, unit-testable.
2. `Shared/Data/Vanilla/Cars/CarDataReader.cs`:
   - resolve the **real** element stride once: create a throwaway `NewCarData` (runs its `.cctor`, sets
     `Il2CppClassPointerStore<NewCarData>.NativeClassPtr`), then `il2cpp_class_value_size`; the pointer is
     `array + 4*IntPtr.Size + index * stride`, never Unhollower's cached stride.
   - resolve field offsets by name (`il2cpp_class_get_field_from_name` + `il2cpp_field_get_offset`).
   - `TryReadName(cars, index)` / `FindSlot(cars, name, preferred)`: reads `carToLoad` through the raw slot and only
     converts it when the memory passes `LooksLikeObject` plus a sane string length; logs what each slot held when no
     slot matches (this is also the diagnosis for the report).
   - `TryRead(cars, index, out NewCarData, out problem)`: readability of the slot, then every reference field
     (`carToLoad`, `UId`, `engineSwap`, `color`, `factoryColor`, `gearRatio`, `tiresET`, `wheelsWidth`, `rimsSize`,
     `tiresSize`, `BodyPartsData`, `PartData`) and every reference inside the inline `BonusPartsData` struct
     (`IDs`, `IsPainted`, `Color`, `PaintType`, `PaintData`, `IdFromConfig`); `BodyPartsData`/`PartData` must also have
     storage when they report items (the `_size > 0` with `_items == null` state that preceded this crash). Only when
     all pass does it hand the slot to a `NewCarData` wrapper.
3. `CarSpawnManager.LoadJobCar`: use `FindSlot` (log the names when the slot has to be found by name) and `TryRead`
   in the existing 3-attempt retry loop; on "not readable yet" retry after `SaveCarToFile()` as the code already does;
   when it never becomes readable, log the problem and skip — no crash, and the log names the field next time.
4. `DataHelper.Copy(ProfileData)` and `Minigames` read `carsInGarage[i]` through `CarDataReader.At` so the wrong-stride
   bug cannot bite anywhere else.
5. `Log.Debug` breadcrumbs per conversion stage in `ModNewCarData` (guarded by `Log.IsEnabled`), so a future crash in a
   nested field still names the stage that was running.

Rejected: (a) validating `ToArray()` catches — the crash is native and never throws; (b) reading the car through the
game's own `NewCarData.Serialize(BinaryWriter)` (safe to *write*, but it would carry the corrupt data to every client);
(c) only "hard-failing" on the inconsistent `BodyPartsData` list — that is the symptom, not the wrong-stride cause, and
it would still crash on a car whose first corrupt field comes earlier.

## Files

- `Shared/SafeMemory.cs` (new)
- `Shared/Data/Vanilla/Cars/CarDataReader.cs` (new)
- `Shared/Data/Vanilla/Cars/ModNewCarData.cs` (debug breadcrumbs)
- `Shared/DataHelper.cs` (`Copy(ProfileData)` through the reader)
- `ClientSide/Data/Garage/Car/CarSpawnManager.cs` (slot lookup + validated read)
- `ClientSide/Features/Stations/Minigames.cs` (reader)
- `Tests/SafeMemoryTests.cs` (new), `Tests/Tests.csproj`, `CMS21-Together.csproj`, `MainMod.cs` (version)

## Testing

- New NUnit tests for `SafeMemory`: `IsReadable` false for `IntPtr.Zero` and for `0x411d3e4c` (the crash's pointer),
  true for a pinned array, and `LooksLikeObject` false when the first word is not mapped / when it is garbage.
- Existing suite + build (`.\build.ps1 -Package`); `SourceInvariantTests` still pass (no `MelonLogger`, no array
  indexer writes).
- In-game (maintainer): host a multiplayer session from the menu, accept a car order, the car spawns and stays; the log
  shows `[Car] Job car ... sent to the other players.` and no crash. With the fix the log also prints the resolved
  element size once, so the next log proves which stride was in use.

## Status

done in 0.5.0-rc24: `CarDataReader` reads with `il2cpp_class_value_size` and validates every reference before wrapping it;`FindSlot` never falls back to a slot that does not hold the car; `LoadJobCar` retries and then logs instead of converting. 6 new `SafeMemory` tests; the full suite (2716) passes.

## Deviations / follow-ups

- The aborted garage load (`CarLoader.LoadCarFromFile()` → `routine is null`, 18:28:34) is **not** fixed here; it is
  independent of this crash. Follow-up: find out why the parameterless overload fails from managed code and route
  `VanillaLoad` through the path that works.
- Nested part data (`PartData[i].MountObjectData`, `TuningData.Values`, part strings) is not validated field by field;
  the arrays themselves are checked, and Debug breadcrumbs name the stage if a nested pointer is still corrupt.
