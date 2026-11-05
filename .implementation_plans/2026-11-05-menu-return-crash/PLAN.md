# Crash when returning to the menu

**Status:** done in 0.5.0-rc12. The in-game menu-return retest is still manual (see Testing).

## Goal

TODO item: the host saves in multiplayer, goes back to the menu, and the game crashes. Older logs show no menu return
that ever worked. The chosen fix was to stop the IL2CPP incremental GC, which frees objects that a store without a
write barrier left untracked.

## Findings

- The crash is a use-after-free in the IL2CPP heap (UnityPlayer's reference walk during `UnloadUnusedAssets`, junk
  pointers in object fields). The game runs the incremental, generational Boehm collector
  (`boot.config`: `gc-max-time-slice=3`), so objects stored without a write barrier can be freed while still in use.
  Unhollower's array indexers and struct setters skip that barrier.
- The first plugin build (`Plugins\TogetherPlus.GcFix.dll`, 00:10) only called `Environment.SetEnvironmentVariable`
  and logged nothing, so there was no evidence of what it did; the mod still reported `[GC] Incremental GC is on`.
- Measured on this machine:
  - `GC_DISABLE_INCREMENTAL=1` set **before the process starts** works: the mod logs `[GC] Incremental GC is off.`
  - With the rebuilt plugin, the variable set in `OnEarlyInitializeMelon` also works: the plugin logs
    `[GC] Incremental GC is off.` at `OnPreModsLoaded`. Plugins therefore run before the collector reads the variable.
  - GameAssembly reads the variable through `GetEnvironmentVariableW` (dumpbin import table); the CRT copy is not
    needed, but `_putenv_s` is set as well for safety.
- `il2cpp_gc_is_incremental` (GameAssembly.dll!RVA 0x277250) is `cmp dword ptr [rip+0x5EB3C9], 0; setne al; ret`,
  i.e. it reads a boolean stored at `GameAssembly + 0x2862620`, the collector's own `GC_incremental` flag.
- The runtime flag patch was verified by launching with `TOGETHERPLUS_GC_SKIP_ENV=1` (a diagnostic switch in the
  plugin): the plugin logged `[GC] Incremental GC turned off at runtime.` and the mod then reported the GC off.

## Approach

1. `OnEarlyInitializeMelon` sets `GC_DISABLE_INCREMENTAL` through the Win32 API, `Environment.SetEnvironmentVariable`
   and `_putenv_s`, and logs whether each worked.
2. `OnPreModsLoaded` (IL2CPP is up, mods are not loaded yet) asks `il2cpp_gc_is_incremental()`. If it is still on, the
   plugin finds the flag from the getter's own machine code (RIP-relative `cmp`/`mov`), writes 0 over it, and checks
   the getter again. On any mismatch the original value is written back, so a game update that moves the flag cannot
   leave memory corrupted; the log then points at the Steam launch option
   `set GC_DISABLE_INCREMENTAL=1 && "%command%"`.
3. The mod keeps its `[GC]` log line. `README.md` mentions the plugin and the "Incremental GC is on" warning.
4. Alternatives rejected: relying on `OnEarlyInitializeMelon` alone (one earlier run still reported the GC on, so the
   outcome must not depend on plugin timing); `il2cpp_gc_set_mode`/`GarbageCollector.GCMode` (they toggle the
   collector, not incremental mode); setting a persistent user environment variable (system-wide side effect).

## Files

- `Plugin/GcFix.cs`
- `MainMod.cs` (`LogGcMode` message, unchanged behaviour)
- `README.md` (install, troubleshooting)

## Testing

- `.\build.ps1 -Deploy`, start the game, read `MelonLoader\Last*.log`: the plugin reports the mode one line after
  `Loading Il2CppAssemblyGenerator...`, and the mod reports `[GC] Incremental GC is off.`
- Verified both paths (env variable, runtime patch) on this machine.
- Manual retest still needed: host, accept a job, save, go back to the menu several times.

## Follow-ups

- A different game build moves `0x2862620`; the plugin recovers the flag from the getter every launch, but the
  fallback message must be checked after a CMS21 update.
- Also recheck `CarSpawnManager.LoadJobCar`, which used to fail with the same corruption-like symptom (rc12 reads the
  car list without `List.ToArray`).
