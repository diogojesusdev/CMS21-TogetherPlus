# Menu return: stop chasing single causes, let go of the garage instead

**Status:** done in 0.5.0-rc31.

## Goal

> "Just fucking crashed again. Why is it so hard for you to fix this?? Try to anticipate future crashes when going
> back to menu and just find a way to avoid game crashes from it"

## Findings

### The menu crash has been one bug all along

Three crash reports, read side by side:

| Report | Last game lines before `Crash!!!` |
|---|---|
| `Crash_2026-11-05_201604946` | `GarageLoader:Save` -> `Inventory.Add(car_bmw2002turbo-trunk)` -> `Unloading 4 Unused Serialized files` |
| `Crash_2026-11-05_203437221` | `GarageLoader:Save` -> `Inventory.Add(car_luxorbaronne-trunk)` -> `Unloading 4 Unused Serialized files` |
| `Crash_2026-11-05_204310457` | `GarageLoader:Save` -> inventory add/delete -> `SendSave profile4.cms21b` -> `Unloading 4 Unused Serialized files` |

Identical every time. The crash is **inside Unity's scene unload**, not in any single mod call.

Two earlier diagnoses were wrong and are corrected here:
- rc29 blamed `IndexOutOfRangeException` in `get_CurrentProfileData`. That exception is real but is logged at lines
  159/222 of ~577 - during the game's *startup* menu - and the game carries on. The fatal `Crash!!!` is at line ~381.
  Worse, resetting `ProfileManager.selectedProfile` before the scene change also changed the slot
  `SavesManager.SaveHook` writes to, because the game's `GarageLoader.Save` runs *after* that hook. **Reverted.**
- rc30 blamed the job-car polling coroutine. It fits the 20:34 report, but in the 20:16 one the coroutine had already
  finished 3.2 seconds *before* the menu was requested. The coroutine guards are still right and stay, but they were
  not the cause.

### What it actually is

A use-after-free during Unity's `UnloadUnusedAssets` reference walk - the same class of failure the original
`2026-11-05-menu-return-crash` plan described and tried to fix by turning the incremental GC off. That helped but
could not end it, because the mod is what keeps the dead objects reachable:

- `ModCarPartInfo` holds a `CarPart`/`PartScript` wrapper for **every part of every loaded car**. This session's log
  says `31 body, 114 engine, 10 other, 6 suspension` - over 160 wrappers for one car.
- `GameData` holds `carLoaders`, `localPlayer`, `localInventory`, `orderGenerator` and every tool logic object.
- `ClientData` holds `playerPrefab` and each player's avatar `GameObject`.

Every Unhollower wrapper owns an il2cpp GC handle. When the garage is destroyed those handles still refer to freed
objects, and the unload walks them.

That is why fixing individual callers never ended it: the problem is not *which* call we make during teardown, it is
that the mod is still holding the scene when Unity frees it.

## Approach

Stop guessing which call is unsafe, and make the whole teardown safe:

1. **A single gate.** `Shutdown.Begin` is set the moment the Menu scene is requested and cleared when the next scene
   is loaded. `Shutdown.Leaving` makes the mod stand back: `OnUpdate` skips the feature pump and the client update,
   `SavesManager.SaveHook` and `SavePatch` let the game save on its own, and the job-car coroutine stops.
2. **Let go of the garage.** After the session is saved and closed, `Shutdown.ReleaseGameReferences()` clears every
   car's part references, destroys the player avatars, drops `playerPrefab`, releases all of `GameData`'s cached
   objects and clears the saved-car captures. Each step is guarded separately, so one failure cannot keep the rest
   alive.
3. **Actually free the handles.** Dropping the references is not enough on its own: Unhollower frees an object's il2cpp
   GC handle in the wrapper's finaliser, so the release ends with `GC.Collect()` / `WaitForPendingFinalizers()` /
   `GC.Collect()`. A scene change already stalls for seconds, so this costs nothing noticeable.
4. The rc29 profile-index reset is removed (kept as a documented, unused method explaining why).
5. Alternatives rejected: continuing to patch individual call sites (three releases of evidence say that does not
   converge); turning the GC off harder (already done, insufficient - the handles are live roots, not a GC mode
   problem); destroying the mod's objects in `OnSceneWasLoaded` (far too late - the unload has already run).

## Files

- `Shared/Shutdown.cs` (new)
- `Shared/SceneManager.cs` (gate + release on the way to the menu, rc29 reset removed)
- `Shared/SavesManager.cs` (`SaveHook`, `SavePatch` stand back while leaving)
- `Shared/Data/ModCar.cs` (`ModCarPartInfo.Clear`)
- `ClientSide/Data/GameData.cs` (`ReleaseReferences`)
- `ClientSide/Data/Garage/Car/CarSpawnManager.cs` (gate)
- `MainMod.cs` (gate per-frame work, clear on scene loaded)
- `CMS21-Together.csproj`

## Testing

- `.\build.ps1 -Package` - all unit tests pass.
- In game, the cases that crashed: host a session, accept a car order, take a body part off, then go back to the main
  menu. Expect `[Shutdown] Leaving to the menu` and `[Shutdown] Released the mod's references to the garage` in the
  log, the menu to load, and the save slots to be intact. Repeat several times, and rejoin a session afterwards to
  confirm the gate is cleared (`[Shutdown] The new scene is up`).

## Follow-ups

- If a crash survives this, the remaining mod-held references are the ones to look for next: anything caching an
  IL2CPP object outside `GameData`/`ModCarPartInfo`/`ClientData`.
- The harmless startup `IndexOutOfRangeException` is still logged; it needs a fix that does not disturb
  `GarageLoader.Save` (for example restoring the index after the save rather than before the scene change).
