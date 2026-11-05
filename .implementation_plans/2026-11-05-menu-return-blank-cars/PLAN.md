# Crash when returning from a multiplayer session to the main menu

**Status:** done in 0.5.0-rc28.

## Goal

> "Also, the game crashed when I went from a multiplayer game back to the main menu!! Fix that"

## Findings

### Evidence

- `MelonLoader_26-11-05_19-48-32.149.log` (rc26) stops dead at `19:50:48.442`; the file's last write is `19:52:08`.
  `Player.log` stops at the same moment. Nothing logged the scene change, so MelonLoader lost the tail to a native
  crash (the repo notes this can happen).
- `TogetherPlus/diagnostics/session.running` from `19:49:02` was still there, so the session never ended cleanly.
  This was a hard native crash, not a managed exception.

### Root cause

`MainMod.OnSceneWasLoaded` calls `SavesManager.Initialize()` whenever the **Menu** scene loads, which is exactly what
returning from a session does. For every mod save with `alreadyLoaded`, `Initialize` does:

```csharp
Singleton<GameManager>.Instance.GameDataManager.ReloadProfiles(tempSaveArray);
var copiedData = DataHelper.Copy(Singleton<GameManager>.Instance.GameDataManager.ProfileData[3]);
Il2CppArrays.Set(profileData, modSave.saveIndex, copiedData);   // handed back to the game
```

`DataHelper.Copy(ProfileData)` built each car with `Copy(CarDataReader.At(cars, i))`, and **`At()` substitutes a
default `new NewCarData()` whenever a slot cannot be read**. `Copy(NewCarData)` then copies that default field by
field, producing a car whose `carToLoad`, `color`, `PartData`, `BodyPartsData` ... are all null.

The same log shows that `At()` was failing for *every* slot - 5 garage and 94 parking warnings at `19:49:12`, all
"carToLoad is not a readable string". So the profile written back into `GameDataManager.ProfileData` had **99 blank
cars with null fields**, and the menu then renders its save slots from it (garage car names, thumbnails). The game
dereferences those nulls in native code and dies, with no managed exception to catch.

That also explains the shape of the bug: it only happens on the way back to the menu *from a session*, because a
save only takes the `alreadyLoaded` branch once the session has loaded it.

Two things made `At()` fail for every slot, both already fixed:
- rc26: the field offsets were being added to the inline element address without removing the object header.
- rc27: a valid but empty `carToLoad` was misreported as unreadable.

But the real defect is older and independent of both: **a copy must never invent a default car.** Any future read
failure silently blanks the player's garage and can crash the game the same way.

### Second, related defect

`Il2CppArrays.Set` stores a value-type element with Unhollower's indexer (`array[index] = value`) and then runs the
write barrier over a span it sizes itself with `il2cpp_class_value_size`. Those two can disagree:
`Il2CppReferenceArray<T>` caches `ourElementTypeSize` in its own static constructor, and that is `IntPtr.Size` (8)
with `ourElementIsValueType == false` when the array type is touched before `Il2CppClassPointerStore<T>` is
populated. The store then writes a *pointer* 8 bytes into the element while the barrier loop walks 440 bytes - which
is exactly the "array class caches the wrong stride" hazard `CarDataReader` already documents, still live on the
write side.

## Approach

1. `DataHelper.Copy(ProfileData)` copies the car arrays **verbatim**, byte for byte, through
   `CarDataReader.CopySlot`. A slot is reproduced exactly as the game had it, so a copy can neither invent a blank
   car nor lose one, and the 99 warnings per menu load disappear. Nothing is interpreted, so no read can fail.
2. `Il2CppArrays` no longer uses Unhollower's indexer for value types: `CopyInline` writes the element with the size
   taken from `il2cpp_class_value_size` and then runs `il2cpp_gc_wbarrier_set_field` over the same span, so the
   store and the barrier can never disagree.
3. `SavesManager.Initialize` gets Info breadcrumbs around `ReloadProfiles` and the copy, so the last line before a
   native crash names the save slot being processed.
4. `CarDataReader.At` keeps its default-car fallback (callers that poke one field rely on it) but now says in its
   summary that it must not be used to copy a car.
5. Alternatives rejected: keeping `At()` in the copy and skipping unreadable slots (still loses cars); deep-copying
   each field through the generated properties (that is what produced the nulls, and it is slower); not writing the
   copy back to the game at all (the menu needs the mod's save contents to list the slots).

## Files

- `Shared/DataHelper.cs` (`Copy(ProfileData)`)
- `Shared/Data/Vanilla/Cars/CarDataReader.cs` (`CopySlot`, `ElementAddress`)
- `Shared/Il2CppArrays.cs` (`CopyInline`, value-type `Set`)
- `Shared/SavesManager.cs` (breadcrumbs)

## Testing

- `.\build.ps1 -Package` - all unit tests pass.
- In game: host a session, accept a car order, save, go back to the main menu. The game must reach the menu without
  crashing, the save slots must still list the right cars, and the log must not show the
  "carToLoad is not a readable string" warnings on the menu load. Repeat a few times.

## Follow-ups

- `DataHelper.Copy(ProfileData)` still shares every other reference with the original (`inventoryData`, `jobsData`,
  ...). That is pre-existing and unchanged here.
