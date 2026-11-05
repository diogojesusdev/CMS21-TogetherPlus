# Job car spawn: the car is never sent to the other players

**Status:** done in 0.5.0-rc27 (rc26 fixed the first half, this fixes the rest).

## Goal

> "It's failing again when i take a car order and the car spawns. it doesn't crash the game but please investigate
> and fix" ... "Dude its still with the same fucking error. FIx it once and for all"

Accepting a car order spawns the car locally, but the other player never gets it:

```
[19:50:47.013] [Car] Job car car_luxorbaronne loaded on loader 4 (customer=True).
[19:50:48.242] [Car] car_luxorbaronne (loader 4) parts referenced: 31 body, 114 engine, 10 other, 6 suspension.
[19:50:48.442] [ERROR] [CarSpawnManager->LoadJobCar] car_luxorbaronne (loader 4) is not in the garage array;
               it could not be sent. The slots hold: [0]=<unreadable>, ... [4]=<empty>
```

## Findings

Two separate bugs, one on top of the other.

### 1. The reader was 16 bytes off (fixed in rc26)

IL2CPP reports a value type's field offsets relative to its *boxed* form, so they include the 16-byte object header,
while `carsInGarage` stores `NewCarData` inline. Confirmed from the real binaries rather than assumed:

- `ProfileData.carsInGarage` really is `Il2CppReferenceArray<NewCarData>`, and `NewCarData` really is a
  `Il2CppSystem.ValueType` (checked in `Assembly-CSharp-firstpass.dll`).
- The generated `NewCarData.get_carToLoad` is `Il2CppObjectBaseToPtrNotNull(this) + il2cpp_field_get_offset(f)`,
  i.e. offsets are added to the **boxed** pointer.
- The shipped UnhollowerBaseLib 0.4.18 indexer is `Pointer + 4*IntPtr.Size + index * il2cpp_class_value_size(...)`
  and boxes the element with `il2cpp_value_box` before wrapping it.

So the inline field address is `slot + offset - 16`. rc26 made that change, and the rc26 log proves it worked:
slot 4 went from `<unreadable>` to `<empty>`, i.e. the reader now reaches a **real, valid, empty** il2cpp string
instead of garbage. The addressing is correct.

### 2. The car is read before the game has saved it (the remaining bug)

With the addressing fixed, the garage array genuinely does not hold the car yet at the moment we look:

- `CarLoader.SaveCarToFile()` does not write the profile synchronously - `CarLoader` carries a public
  `SaveComplete` flag precisely because the write finishes later.
- `LoadJobCar` called `SaveCarToFile()`, waited two frames, called `FindSlot` **once**, and on -1 did a hard
  `yield break` with the "is not in the garage array" error. The 3-attempt retry loop sat *after* the lookup, so it
  only ever retried the field read, never the lookup. This is the regression: before the `FindSlot` lookup was
  introduced, the retry loop did cover the "not written yet" case.
- The log confirms the timing: the error is logged 0.2s after the parts are referenced, far too early.

### 3. Empty slots were reported as corruption

`TryRead` folded "not a readable string" and `string.IsNullOrEmpty(name)` into one branch, so every empty slot was
logged as `carToLoad is not a readable string`. That is what produced 94 bogus warnings from
`DataHelper.Copy(ProfileData)` for the (legitimately empty) parking slots, and it made the real problem much harder
to see. `TryReadName` also returned false for a null name pointer, so `Describe` printed `<unreadable>` for a slot
that was simply unused.

## Approach

1. `LoadJobCar` polls instead of looking once: every 0.25s, up to 20s, re-running `FindSlot` **and** the read each
   time, and asking the game to save again every 2s. It stops the moment the car turns up. The loop bails out early
   if the car or the loader goes away, and logs one breadcrumb (not one per poll) if it is still waiting after 2s.
2. `TryRead` separates "unreadable" from "empty": an empty `carToLoad` now reports `problem == null`, so an empty
   slot is not logged as corruption and `At()` stops warning for every unused parking slot.
3. `TryReadName` reports a null name pointer as an empty name, so `Describe` says `<empty>` for an unused slot and
   keeps `<unreadable>` for memory that really cannot be read.
4. `TryReadString` checks the character data at `2*IntPtr.Size + 4` (after the length field) rather than
   `2*IntPtr.Size`, which is where il2cpp actually puts it.
5. Alternatives rejected: waiting on `SaveComplete` alone (if the game never flips it for this path the wait is
   dead time, and polling the array is the thing we actually care about); using the Unhollower indexer directly (it
   boxes before anything is validated, which is the native crash `CarDataReader` exists to prevent); reading the car
   from the live `CarLoader` instead of the profile (a much larger protocol change).

## Files

- `ClientSide/Data/Garage/Car/CarSpawnManager.cs` (`LoadJobCar`)
- `Shared/Data/Vanilla/Cars/CarDataReader.cs`
- `Shared/Data/Vanilla/Cars/CarFieldLayout.cs` (new in rc26)
- `Tests/CarFieldLayoutTests.cs` (new in rc26)

## Testing

- `.\build.ps1 -Package` - all unit tests pass.
- In game: host, accept a car order. The log must show `[Car] Job car <name> (loader N) sent to the other players.`
  and no `is not in the garage array`. The other player must see the car. The 94 parking-slot warnings on load must
  be gone.

## Follow-ups

- If the 20s window is ever hit, the error now prints what every slot held, which identifies whether the game wrote
  the car somewhere else (e.g. parking) rather than just being slow.
