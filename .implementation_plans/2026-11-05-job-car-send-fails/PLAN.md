# Job cars are not sent to the other players

**Status:** planned. Not fixed in rc6–rc9.

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

- The rc6 fix (`CopyList` returns an empty list when `source._items == null`) did **not** catch this. `_items` is non-null as seen from Unhollower, yet the game's `List<T>.ToArray()` still throws `ArgumentNullException` natively.
- The failure is deterministic: all 3 attempts fail, 0.5 s apart, each after `SaveCarToFile()`.
- `CopyList` is used for `BodyPartsData` and `PartData`. The log doesn't say which list failed, because the exception escapes `CopyList` before its `name` is logged.
- One failing list loses the whole car: the exception escapes the `ModNewCarData` constructor.
- Hypotheses to check:
  1. Unhollower reads `_items` for the generic `List<T>` instantiation at the wrong field offset, or from a different object than the native code uses.
  2. `ToArray` in this IL2CPP build fails on a null **element** conversion or an inner array, not on `_items` itself.
  3. `carsInGarage[index]` is a stale or half-written copy (wrong index from `GetIndexFromCarLoaderName`, or `SaveCarToFile` hasn't finished).

## Approach (proposed)

1. Stop calling `ToArray()`. Read by index up to `Count`, bounds-checked against `_items.Length`, with a try/catch **per list**. A bad list is then read as empty (with a warning naming it) instead of losing the car.
2. Wrap each list/sub-object copy in the constructor so one bad field never drops the whole car.
3. Add diagnostics on failure: list name, `Count`, `_items` pointer and length, and the loader/index used.
4. If the data really is half-written, read the car from the `CarLoader` directly instead of `carsInGarage`.

## Files

- `Shared/Data/Vanilla/Cars/ModNewCarData.cs` (`CopyList`, constructor)
- `ClientSide/Data/Garage/Car/CarSpawnManager.cs` (`LoadJobCar`)

## Testing

- Unit tests where pure .NET allows it.
- In game: the host accepts a job (e.g. `car_bmwm1procar`). The other player must see the car, and the log must show `[Car] Job car ... sent to the other players.`
