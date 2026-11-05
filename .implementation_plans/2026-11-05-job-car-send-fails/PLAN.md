# Job cars are not sent to the other players

**Status:** done in 0.5.0-rc12. In-game check on a real job accept still to do (see Testing).

## Goal

The user reported "It failed again" with a screenshot of the rc9 console (`rc9-log.png`).

When the host accepts a job, the job car loads locally but is never sent to the other players:

```
[Car] Job car car_bmwm1procar loaded on loader 4 (customer=True).
[Car] car_bmwm1procar (loader 4) parts referenced: 29 body, 83 engine, 9 other, 5 suspension.
[WARNING] [CarSpawnManager->LoadJobCar] Reading car_bmwm1procar (loader 4) failed, attempt 1/3: System.ArgumentNullException: Value cannot be null.
... attempt 2/3, 3/3 (same) ...
  at Il2CppSystem.Collections.Generic.List`1[T].ToArray ()
  at ModNewCarData.CopyList[TIn,TOut] (List`1 source, Func`2 convert, String name)
  at ModNewCarData..ctor (NewCarData newCarData, Int32 placeNo, Int32 _jobID)
  at CarSpawnManager+<LoadJobCar>d__1.MoveNext ()
[ERROR] [CarSpawnManager->LoadJobCar] car_bmwm1procar (loader 4) could not be sent to the other players.
```

## Findings

- The rc6 guard (`CopyList` returns an empty list when `source._items == null`) could never fire. Unhollower's
  generated `Il2CppSystem.Collections.Generic.List<T>._items` getter wraps **the address of the field** with
  `Il2CppArrayBase<T>.WrapNativeGenericArrayPointer`, so the wrapper is never null even when the game list has no
  backing array. Its `Length` would read the neighbouring fields, so it must not be used either.
- The native exception is real: `List<T>.ToArray()` throws `ArgumentNullException` when the list's `_items` array is
  null. rc3 had already seen that indexing the same list crashes native code.
- One failing list escapes `ModNewCarData`'s constructor and loses the whole car (the log never said which list).
- The garage slot came from `Helper.GetIndexFromCarLoaderName(carLoader.name)`; if that lookup is wrong, a different
  (or empty) profile entry is read, so the reported data could be for the wrong car.
- `CarLoader.SaveCarToFile()` does not write a file: it writes the live car into the profile's `carsInGarage`, which is
  why the read happens after it.

## Approach

1. `CopyList` reads the real array pointer from the list field's slot (`Marshal.ReadIntPtr(source._items.Pointer)`), then
   copies by index from an `Il2CppReferenceArray<T>` with `Math.Min(Count, Length)` bounds. It never calls `ToArray`.
   A list with no storage is copied as empty with a warning naming it, so the rest of the car still syncs.
2. Each element conversion is guarded separately, so one unreadable panel or part does not drop the list.
3. `LoadJobCar` finds the slot by car name: the loader-name lookup when it holds that car, otherwise a scan of
   `carsInGarage`, with a log line when they differ. Out-of-range lookups are reported instead of throwing.
4. Diagnostics name the list, the count, the slot and the loader on failure.
5. Alternatives rejected: `List<T>.ToArray()` (throws), `source[i]` on the raw list (native crash on a null array),
   `DataHelper.Copy` (shares the same lists), and rebuilding the car from the live parts (large protocol change; part
   packets already sync the contents).

## Files

- `Shared/Data/Vanilla/Cars/ModNewCarData.cs` (`CopyList`)
- `ClientSide/Data/Garage/Car/CarSpawnManager.cs` (`LoadJobCar`, `FindGarageIndex`)

## Testing

- Unit tests still pass (2574); the changed code needs the game runtime (Il2Cpp lists).
- In game: host, accept a job (e.g. `car_bmwm1procar`); the other player must see the car and the log must show
  `[Car] Job car ... sent to the other players.` If a list is still empty-storage, the warning names it and the car
  must arrive anyway.

## Follow-ups

- Play-test verification needs a manual job accept on two clients.
