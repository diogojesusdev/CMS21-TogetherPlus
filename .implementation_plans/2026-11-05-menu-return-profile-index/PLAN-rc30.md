# Menu return still crashed: the job-car coroutine outlived the garage

**Status:** done in 0.5.0-rc30.

## Goal

> "crashed again I think. ivnestigate"

## Findings

### rc29's fix worked; the crash is a different one

`Crash_2026-11-05_203437221` plus `MelonLoader_26-11-05_20-32-55.753.log`:

- The rc29 reset fired exactly as intended:
  `[20:34:35.882] [SceneManager] Save slot 4 is a Together+ slot; selecting a vanilla slot for the menu.`
- The two `IndexOutOfRangeException ... get_CurrentProfileData` entries in this report are at **lines 159 and 222 of
  581**, i.e. during the game's *startup* menu, not at the crash. They come from the value rc29 now clears
  (`RDGPlayerPrefs["selectedProfile"]` was still 4 from the previous session) and the game survives them. The fatal
  event is `Crash!!!` at line 385.
- What the game logged immediately before `Crash!!!`:
  ```
  [SteamSave] -> SendSave()profile4.cms21b
  GarageLoader:Save(Boolean)
  [Inventory] -> Add() car_luxorbaronne-trunk
  Unloading 4 Unused Serialized files (Serialized files now loaded: 3)
  Crash!!!
  ```

### Root cause

The job-car coroutine was still running. Its timeline in this session:

- `20:34:26` the car finished loading, so `LoadJobCar` began its **20 second** polling window.
- `20:34:28.650` it logged "not in the garage array yet after 2.0s".
- `20:34:35.882` the player asked for the menu - **7 seconds into a 20 second window**.
- `20:34:36.646` the session shut down; the crash follows ~1s later.

Every 0.25s that loop calls into the garage: `carLoader.IsCarLoaded()`, `carLoader.SaveCarToFile(index, false)` and
`GameManager.Instance.GameDataManager.CurrentProfileData`. Its only guards were `carLoader == null` and
`IsCarLoaded()`, which say nothing about the scene being torn down. So while Unity was unloading the garage and the
game was swapping the profile array, the mod kept asking a dying `CarLoader` to save a car - which is the save and
unload visible right before `Crash!!!`.

This window is the one rc27 introduced. Before it, the coroutine ended almost immediately, which is why this exact
shape appeared only now.

### Why the polling could never succeed anyway

Across rc27, rc28 and rc29 every log says the same thing: `[0]=<empty>, [1]=<empty>, ... [4]=<empty>` for the whole
window. `ProfileData.carsInGarage` is simply not where the game puts a job car, so no amount of waiting (or of
calling `SaveCarToFile`) would ever find it. Polling it was the wrong mechanism.

## Approach

1. **The coroutine stops when the garage does.** `StillInThisGarage` is checked before and after every wait, and
   requires `Client.isConnected`, `!SceneManager.IsLoading`, `SceneManager.IsInGarage()`,
   `GameLoadHook.IsGameReady()`, a live loaded `CarLoader`, and the car still being tracked. `GameLoadHook.Reset()`
   already runs at the top of the scene-change hook, so the loop gives up on the very first poll after the player
   asks for the menu.
2. **Catch the car where the game actually writes it.** New `CarSaveHooks` puts a Harmony postfix on
   `GameDataManager.SaveCar` and `SaveCarInGarage` and converts whatever the game saves into `ModNewCarData` right
   there. `LoadJobCar` now takes the car from that capture, with the garage-array read left only as a fallback. No
   guessing about where a job car ends up.
3. The capture is converted immediately, so nothing holds a reference to a game object that could go stale, and it
   is dropped when the session ends or after 60s.
4. The window is also cut from 20s to 12s and the re-save from every 2s to every 3s, so even the fallback pokes the
   game far less.
5. Alternatives rejected: keeping the long poll with only a null check (the crash); stopping the coroutine from the
   scene hook by tracking handles (more state for the same result as checking readiness); reading the car from the
   live `CarLoader` instead (a much larger change, and the game's own save already has the exact data).

## Files

- `ClientSide/Data/Garage/Car/CarSaveHooks.cs` (new)
- `ClientSide/Data/Garage/Car/CarSpawnManager.cs` (`LoadJobCar`, `StillInThisGarage`)
- `Shared/SceneManager.cs` (drop captures when the session ends)
- `CMS21-Together.csproj`

## Testing

- `.\build.ps1 -Package` - all unit tests pass.
- In game: accept a car order and **go back to the menu immediately**, while the car is still settling. The game must
  reach the menu. Then accept a car order and wait: the log should show
  `[Car] <name> ... was caught as the game saved it into garage slot N.` followed by
  `[Car] Job car ... sent to the other players.`

## Follow-ups

- If the capture never fires, `SaveCar`/`SaveCarInGarage` are not the calls a job car goes through; the next step is
  to hook `CarLoader.SaveCarToFile` itself and read the loader's data.
