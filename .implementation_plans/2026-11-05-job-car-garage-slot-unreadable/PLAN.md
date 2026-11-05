# Job car spawn: every garage slot reads as `<unreadable>`

**Status:** done in 0.5.0-rc26.

## Goal

> "It's failing again when i take a car order and the car spawns. it doesn't crash the game but please investigate
> and fix"

From `MelonLoader_26-11-05_19-24-59.746.log` (rc25), after accepting a job:

```
[19:26:08.936] [Car] Job car car_hinatamoonmk1 loaded on loader 4 (customer=True).
[19:26:10.052] [Car] car_hinatamoonmk1 (loader 4) parts referenced: 29 body, 74 engine, 13 other, 6 suspension.
[19:26:10.238] [ERROR] [CarSpawnManager->LoadJobCar] car_hinatamoonmk1 (loader 4) is not in the garage array;
               it could not be sent. The slots hold: [0]=<unreadable>, [1]=<unreadable>, ... [4]=<unreadable>
```

No crash, but the job car is never sent, so the other player does not get it.

## Findings

- `CarDataReader.Prepare` succeeded: the log has `Reading cars with the 440-byte NewCarData stride`, so the class,
  the stride and the field lookups all resolved. The failure is in the per-slot read.
- Every slot is `<unreadable>`, including the one that certainly holds the freshly spawned car. A systematic
  off-by-a-constant, not a half-written car.
- Root cause: **IL2CPP field offsets for a value type are relative to the boxed object, so they include the
  16-byte `Il2CppObject` header.** `CarDataReader` computes `slot` as the *inline, unboxed* element address inside
  `carsInGarage` and then adds the raw `il2cpp_field_get_offset` value, so every field is read 16 bytes too far.
  `carToLoad` therefore never reads back as a string and every slot is reported unreadable.
- Confirmed against Unhollower 0.4.18 `Il2CppReferenceArray<T>.WrapElement`: for a value-type element it calls
  `il2cpp_value_box(class, memberPointer)` and hands the **boxed** pointer to the wrapper constructor. The generated
  field accessors then add `il2cpp_field_get_offset` to that boxed pointer, which only works because the offsets
  include the header.
- The same mistake is in the success path: `TryRead` did `new NewCarData(slot)` with the inline pointer. Had a read
  ever succeeded, the wrapper would have read every field 16 bytes off, and `Il2CppArrays.Set` (which calls
  `il2cpp_object_unbox`, i.e. +16 again) would have copied garbage into the destination array. So
  `DataHelper.Copy(ProfileData)` and `Minigames` were silently wrong too.

## Approach

1. `CarDataReader` gets `ObjectHeader = 2 * IntPtr.Size` and reads every field at `inlineBase + offset - bias`.
2. The bias is decided once, from the resolved offsets, by a new pure helper `CarFieldLayout.ChooseBias`: if every
   resolved offset is at least the header size and still fits inside `header + elementSize`, the offsets are
   header-inclusive (bias = header). If any offset is below the header, they are already relative to the inline data
   (bias = 0, with a warning). Offsets that fit neither are reported as a problem instead of being read.
   This keeps the reader correct if a future Unhollower changes the convention, and it is unit testable without the
   game.
3. `TryRead` builds the wrapper the way Unhollower does: `new NewCarData(IL2CPP.il2cpp_value_box(klass, slot))`,
   after the existing pointer validation. The boxed copy is a real GC object, so the wrapper and
   `Il2CppArrays.Set`'s `il2cpp_object_unbox` both behave.
4. The nested `BonusPartsData` block is reached the same way: its base is `slot + offBonusPartsData - bias`, and its
   own fields use the bias too (it is a value type as well).
5. Alternatives rejected: hard-coding `-16` without the sanity check (silent breakage if the convention differs);
   reading through the Unhollower indexer (boxes before validating, which is the native crash this reader exists to
   avoid); scanning memory for the name string (no way to map a hit back to a slot).

## Files

- `Shared/Data/Vanilla/Cars/CarFieldLayout.cs` (new, pure)
- `Shared/Data/Vanilla/Cars/CarDataReader.cs`
- `Tests/CarFieldLayoutTests.cs` (new)
- `CMS21-Together.csproj`, `Tests/Tests.csproj`

## Testing

- `.\build.ps1 -Package` (all unit tests, including the new `CarFieldLayoutTests`).
- In game: host a session, accept a car order. The log must show
  `[Car] Job car <name> (loader N) sent to the other players.` and no `is not in the garage array`. The other player
  must see the car spawn.

## Follow-ups

- None known; the two other `CarDataReader.At` callers are fixed by the same change.
