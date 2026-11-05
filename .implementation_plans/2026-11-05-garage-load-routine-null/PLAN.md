# Garage load aborts on `NullReferenceException: routine is null`

**Status:** planned (target 0.5.0-rc25)

## Goal

> "game didn't crash but I got this. investigate and fix"

Host, solo multiplayer session on the `JavaleidaShop` save, rc23. Joining the garage logs one
`[ERROR] Exception in IL2CPP-to-Managed trampoline ... System.NullReferenceException: routine is null` and the garage
car load stops.

## Findings

The log (`MelonLoader_26-11-05_18-26-43.901.log`, 18:28:34.998):

```
[ERROR] Exception in IL2CPP-to-Managed trampoline, not passing it to il2cpp: UnhollowerBaseLib.Il2CppException:
System.NullReferenceException: routine is null
  at CarLoader.LoadCarFromFile () [0x0002c] in <c1701c0be20a465788ac15158cc73c50>:0
  at CMS21Together.ClientSide.Data.GameLoadHook+<VanillaLoad>d__6.MoveNext () [0x002f9]
```

`routine is null` is Unity's own text: `MonoBehaviour.StartCoroutine(IEnumerator routine)` throws
`new NullReferenceException("routine is null")` when the enumerator is null
(`UnityCsReference/Runtime/Export/Scripting/MonoBehaviour.bindings.cs`).

What the game's method does (from the Cpp2IL ISIL dump of `Assembly-CSharp-firstpass`, resolved with
`LibCpp2IL.MethodsByPtr`; the shim `StartCoroutine` = `0x18165DAA0`, `Object.get_name` = `0x18165EB80`,
`CarLoader.LoadCarFromFile(string)` = `0x1805077E0`):

```csharp
public void LoadCarFromFile()                    // CarLoader, 0x1805076F0
{
    StartCoroutine(this.LoadCarFromFile(this.name));
}
```

So the parameterless overload takes the enumerator returned by the `(string)` overload and starts it.

Our hook replaced that enumerator:

```csharp
[HarmonyPatch(typeof(CarLoader), nameof(CarLoader.LoadCarFromFile), typeof(string))]
[HarmonyPrefix]
public static bool LoadCarFromFileHook(string file, CarLoader __instance)
{
    ...
    MainMod.StartCoroutine(__instance.LoadCarFromFile(newCarData));
    MelonCoroutines.Start(LoadCarFromFile(newCarData, __instance));
    return false;                      // <- skips the original
}
```

A Harmony prefix that returns `false` on a method with a return value leaves `__result` at its default, so
`LoadCarFromFile(string)` returned **null**. The caller above then called `StartCoroutine(null)` → the exception.
It aborts `GameLoadHook.VanillaLoad` in the middle of its car loop:

```csharp
car.DeleteCar();                       // every loader's car is already deleted here
car.LoadCarFromFile();                 // <- throws on the first loader
while (!car.IsLoadedFromFile()) ...
```

so the saved cars are never loaded, and the rest of the copied vanilla load (player position, lifters, orders)
is skipped too. The game "did not crash" because MelonLoader's IL2CPP-to-managed trampoline logs and swallows the
exception; `CustomLoad` then continues on its own.

Also part of the same fix: `CarSpawnManager.LoadCar` (reached from this hook once it works again) dereferences
`GameData.Instance`, which is only created in `GameLoadHook.CustomLoad` **after** `VanillaLoad` returns, so the
first car of the garage load would replace this exception with another one.

Related but out of scope: `StationGates.InteriorGate` is a `bool` prefix on
`InteriorDetailingToolkitLogic.DoWorkAnim`, which also returns `IEnumerator`. It has the same latent null-result
shape; the caller could not be inspected (Cpp2IL could not dump that part of Assembly-CSharp), so it is left for a
separate check.

## Approach

1. `CarSpawnHooks.LoadCarFromFileHook` becomes an observer: it computes the same `NewCarData`
   (`Helper.GetIndexFromCarLoaderName` + `GameDataManager.LoadCar(index, false)`, which is exactly
   `LoadCarInGarage(index)` — the game's own source for this load) and starts the sync coroutine, then returns
   `true`. The game runs its own load and its own enumerator, so no caller can receive null.
   - The "loaded every car twice" pitfall from the old comment does not come back: the mod no longer starts a
     second load of its own, it only reports the car.
2. `CarSpawnManager.LoadCar` waits for `GameData.Instance`/`carLoaders` before its `IsCarLoaded()` loop (and
   bails if the client disconnects while waiting), so a load that starts before `GameData` exists no longer throws.
3. The sync coroutine keeps using the car name from the data it already read (`carDataCheck.carToLoad`) instead of
   `__instance.carToLoad`, which is only filled in by the load that now runs after the hook.

Alternatives rejected:

- **Return the enumerator via `ref __result` and keep skipping**: it works for today's four callers (all start
  the returned enumerator) but keeps a fragile skip on a value-returning method; a future caller that ignores the
  result would silently not load the car.
- **Set `listenToLoad = false` around the vanilla load**: that only hides the hook for this one caller and leaves
  every other `LoadCarFromFile(string)` caller exposed to the same null result.
- **Waiting for `GameLoadHook.IsGameReady()` in the sync coroutine**: `SceneReturnFeature.HoldEcho` returns
  `false` once the game is ready, so the stale-save protection would stop working.

## Files

- `ClientSide/Data/Garage/Car/CarSpawnHooks.cs` (observe, don't replace; use the data's car name)
- `ClientSide/Data/Garage/Car/CarSpawnManager.cs` (wait for `GameData` before dereferencing it)
- `Tests/SourceInvariantTests.cs` (regression: the hook may not skip)
- `MainMod.cs` (version)

## Testing

- `.\build.ps1 -Package` — the NUnit suite must pass, including the new source invariant.
- In-game (maintainer): host a multiplayer session and load the `JavaleidaShop` save.
  - No `[ERROR] ... routine is null` line.
  - `[Car] Local load of <car> on loader N (place P), sending it.` once per garage car, then the parts-referenced
    line for each.
  - The saved cars are in the garage (all loaders, not just the first).
  - A second client joins and sees the same cars; hand over a job and reload to check the garage still loads.

## Status

done in 0.5.0-rc25.

## Deviations

- The sync coroutine was renamed `LoadCarFromFile` → `SyncLoadedCar` so it is not mistaken for an overload of the
  game method it observes.
- The car-name check moved from `__instance.carToLoad` to the `NewCarData` the hook already read, so it does not
  depend on the load it now only observes.
- `CarSpawnManager.LoadCar` also bounds-checks the loader id once `GameData` exists, instead of indexing past the
  array, because the id can come from the name fallback while `GameData` is still null.

## Follow-ups

- `StationGates.InteriorGate` is a `bool` prefix on `InteriorDetailingToolkitLogic.DoWorkAnim`, which returns
  `IEnumerator`: same latent null-result shape. Cpp2IL could not dump that caller (the Assembly-CSharp analysis
  fails for most types), so whether a denial turns into "routine is null" is unverified. Worth a check with the
  interior detailing toolkit in use by another player.
- In-game verification on two clients is still manual (the checklist under Testing).
