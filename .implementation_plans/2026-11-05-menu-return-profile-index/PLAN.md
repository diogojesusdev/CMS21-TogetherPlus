# Menu return crashes: the game indexes its profile array with a mod save slot

**Status:** done in 0.5.0-rc29.

## Goal

> "Crashed again when going back to main menu"

This time the Unity crash handler caught it, so there is a real report:
`%LOCALAPPDATA%\Temp\Red Dot Games\Car Mechanic Simulator 2021\Crashes\Crash_2026-11-05_201604946`.

## Findings

### The exception

`Player.log` in the crash folder shows, twice, while the menu is starting up:

```
IndexOutOfRangeException: Index was outside the bounds of the array.
  at GameDataManager.get_CurrentProfileData ()
  at GlobalData.Load ()
  at GameDataManager.LoadProfile ()
  at GameDataManager.Load (CMS.Platforms.PlatformManager platformManager)
  at GameDataManager+<Load>d__13.MoveNext ()
  at MenuPressButton+<LoadScene>d__26.MoveNext ()
```

### Why it is out of bounds

- `GameDataManager.CurrentProfileData` is `ProfileData[ProfileManager.selectedProfile]`.
- Vanilla CMS21 has 4 profile slots (0-3). Together+ adds its own: `SavesManager.profileData` is
  `new Il2CppReferenceArray<ProfileData>(MainMod.MAX_SAVE_COUNT + 1)` (23 entries) and `Initialize()` ends with
  `GameDataManager.ProfileData = profileData`, so while the mod is in charge index 4+ is valid.
- Loading a mod save sets `ProfileManager.selectedProfile = index` (4 here) and also persists it with
  `RDGPlayerPrefs.SetInt("selectedProfile", index)`. **Nothing ever puts it back.**
- Returning to the menu runs the game's own `GameDataManager.Load(platformManager)`, which rebuilds `ProfileData`
  from the platform's saves - a **4-entry** array - and then immediately reads `CurrentProfileData`. With
  `selectedProfile` still 4, that is `ProfileData[4]` on a length-4 array.
- The exception escapes a Unity coroutine during scene load and takes the process down; the crash handler dialog in
  the screenshot is the result.

This matches every detail of the reports:
- It only happens on the way **back** to the menu, never at startup, because at startup `selectedProfile` is a
  vanilla slot and the mod's array is installed before anything indexes it.
- It only happens on a **mod save slot** (the player's save is `profile4.cms21b` / `save_4.cms21mp`, slot 4).
- Nothing is logged by the mod: the crash is inside the game's menu startup, before `OnSceneWasLoaded("Menu")` runs,
  which is why the earlier logs simply stopped.

The mod log confirms the ordering - the last lines are the clean shutdown, and `[Scene] Loaded Menu` never arrives:

```
[20:16:04.345] [Server] Server closed: The host returned to the menu.
```

### Not the earlier theory

rc28 assumed blank cars in the copied profile. That was a genuine bug (a copy must not invent a default car) and the
fix stays, but it was not this crash: the failure is a plain managed `IndexOutOfRangeException` on the array length,
before any car is touched.

## Approach

1. `SceneManager.SelectSceneToLoadHook` points the game back at a vanilla profile slot whenever the **Menu** scene is
   requested: if `ProfileManager.selectedProfile` is a mod slot (>= 4) it is set to 0, and the same value is written
   to `RDGPlayerPrefs` so a later read cannot bring the mod index back. This runs **before** the connected check, so
   a single-player mod save returning to the menu is covered too.
2. The reset is guarded and logged (one Info line), so if the game ever changes this it is visible in the log rather
   than silent.
3. `SavesManager.Initialize()` already reinstalls the extended array on the Menu load, so the mod's save slots still
   appear; only the *index the game dereferences during its own startup* changes.
4. Alternatives rejected: re-extending `ProfileData` from a Harmony patch on `GameDataManager.Load` (the game
   replaces the array inside its own coroutine, so there is no safe point to win the race); patching
   `get_CurrentProfileData` to clamp (hides the problem and would return the wrong profile); leaving
   `selectedProfile` and catching the exception (it is thrown inside the game's coroutine, nothing of ours can catch
   it).

## Files

- `Shared/SceneManager.cs` (`SelectSceneToLoadHook`, `ResetProfileSelection`)

## Testing

- `.\build.ps1 -Package` - all unit tests pass.
- In game: load a Together+ save (slot 4+), host, then go back to the main menu. The menu must load, the save list
  must still show the mod saves, and the log must show `[Saves] Rebuilding the profile of save slot 4 for the menu.`
  Repeat, and also test entering a session again straight after.

## Follow-ups

- The persisted `RDGPlayerPrefs` key is reset along with the field; if a future build is found starting up on a mod
  index anyway, clamp on the first Menu load as well.
