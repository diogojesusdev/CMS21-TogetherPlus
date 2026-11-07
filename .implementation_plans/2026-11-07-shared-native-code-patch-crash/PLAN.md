# Crash when sitting in / driving a car (shared native code patch)

## Goal

User: "It just crashed again when I tried driving a car. investigate and fix"

## Findings

- `Latest.log` (rc35, 15:15) ends at 15:18:14 with nothing unusual. The Unity crash folder
  `%TEMP%\Red Dot Games\Car Mechanic Simulator 2021\Crashes\Crash_2026-11-07_151830049` has the dump.
- Its Player.log: the last game line is `[GameScript] -> SitInside` (the player sat in the car), then `Crash!!!`.
- `cdb` on `crash.dmp`: access violation in `UnityPlayer+0x88b286`, `cmp dword ptr [rdx+20h],2` with
  `rdx = 0x7465670074694674`, which is ASCII `"tFit\0get"`. A Unity icall got a "native object" that is string bytes:
  an object field read at a wrong offset.
- GameAssembly frames, resolved with Cpp2IL's `AddressAttribute` RVAs (base `0x7ffb15240000`):
  `GameScript.Update` -> `UIDescription.HandleInput` -> `ControlDescription.CustomUpdate` ->
  `ControlDescription.InvokeActionInstant` -> `UnityEvent.Invoke` -> delegate -> **managed (Harmony) frames** ->
  `il2cpp_runtime_invoke` -> **managed (Harmony) frames again** -> Unity icall -> crash.
  A key-prompt action (closing the window/description shown inside the car) ran two nested Harmony patches.
- Root cause: IL2CPP folds identical method bodies. `LampAlignmentWindow.HideAction()` and
  `WheelsAlignmentWindow.HideAction()` are the **same native function (RVA 0x4BEED0) as 37 other methods**
  (`CarInfoWindow.HideAction`, `CaseOpeningWindow.Close`, `CreateEngineWindow.HideAction`, ...). Harmony on
  MelonLoader 0.5.7 detours the native code, so:
  - every one of those 39 methods runs the alignment features' `Closing` prefix, with `__instance` being some other
    window; `__instance.carLoader` reads an unrelated field -> garbage pointer -> Unity's `==` dereferences it;
  - the second patch's "original" is the first patch's detour, hence the two nested managed levels in the stack.
- No other patched method shares its native code (offline scan of all patch targets against Cpp2IL RVAs).

## Approach

1. Hook the alignment windows' own, unshared `Hide(bool)` (RVA unique for both windows) instead of `HideAction()`.
   Only send when the window was opened by this player (a flag set by the `Show` gate).
2. Make the mistake impossible to repeat:
   - `Shared/Safety/NativeCode.cs` (game-facing): reads a method's native code pointer (`Il2CppMethodInfo.methodPointer`)
     and, once, indexes the code pointers of every method of every loaded IL2CPP class.
   - `PatchSafety.Require` refuses a target whose native code is shared with another method (the feature then fails on
     its own with the reason, as for game structs), and `PatchSafety.AuditApplied` reports any patched method that
     shares code (attribute patches and other mods), naming a few of the methods that share it.
   - The pure wording/decision lives in `PatchRules` so it is unit tested.

Rejected: keeping `HideAction` and checking `__instance` type in the prefix. Every other window's close would still go
through the detour and the double patch would still chain the two prefixes; the type check itself calls into the game
with a foreign object.

## Files

- `ClientSide/Features/Stations/Minigames.cs`
- `Shared/Safety/NativeCode.cs` (new), `Shared/Safety/PatchSafety.cs`, `Shared/Safety/PatchRules.cs`
- `CMS21-Together.csproj`
- `Tests/HarmonyTargetTests.cs`
- `MainMod.cs` (version)

## Testing

- Unit tests: `PatchRules.SharedCodeReason` wording/threshold.
- `.\build.ps1 -Package` (all tests).
- In game: the log line `[Patches] N patched game methods checked: none takes a game value type or shares its code`
  with no `[Patches]` errors; sit in a car, press the on-screen key actions / close windows: no crash. Headlamp and
  wheel alignment still sync when their windows are closed.

## Status

Done in 0.5.0-rc36. The native-code index runs once at startup (its duration is logged as `[Patches] Indexed ...`);
the game run itself still needs the player's in-game check above.
