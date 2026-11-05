# TODO

Ideas not started yet. To start one, create its plan folder (see `AGENTS.md`) and remove the idea from this list.

## Fix: job cars not sent to other players

Every job car is read with `List.ToArray()`, which throws `ArgumentNullException` even after the rc6 guard, so the car never reaches the other players. Plan: [`2026-11-05-job-car-send-fails/PLAN.md`](2026-11-05-job-car-send-fails/PLAN.md) (status: planned).

## Self-updating mod

MelonLoader can't swap a mod while the game is running, but it can update between launches.

**Approach: an updater plugin**
- A small `TogetherPlus.Updater.dll` in the game's `Plugins` folder. MelonLoader runs plugins before it loads any mods.
- At game start it checks this repo's GitHub releases for a newer version.
- If one exists, it downloads the zip, checks a SHA-256 hash published with the release, and replaces `Mods\CMS21-Together.dll` and `UserLibs` files. This is safe because the mod isn't loaded yet.
- The game then starts on the new version straight away, with no extra restart.

**Extras**
- **Match the host:** when someone joins a host on a different rc, the error can say "Host runs rcN. Update and restart?" and download that exact version.
- **Channel setting** in `MelonPreferences.cfg`: pre-releases, stable only, or off.
- **Safety:**
  - Downloads come only from this repo over HTTPS and must pass the hash check.
  - If the game fails to start after an update, roll back to the previous version.
  - The updater rarely changes, so it can update the mod reliably.

**Limits**
- Players without internet, or with GitHub blocked, stay on their version.
- Updating the updater itself needs an extra step. A one-time manual install of the updater handles this.

## Crash when returning to the menu (IN PROGRESS, paused)

Report: the host saves in multiplayer, goes back to the menu, and the game crashes. Older logs show no menu return that ever worked.

**Diagnosis (done)**
- The latest crash (`%TEMP%\Red Dot Games\Car Mechanic Simulator 2021\Crashes\Crash_2026-11-04_234926525`) is in UnityPlayer's walk over managed references during `UnloadUnusedAssets`. A reference field held junk (`rcx=0x0000000100000001`).
- Earlier crashes (22:32, 23:18, 23:12) show the same thing during TakeJob, SitInside and Inventory.Add. For example, an Object wrapper's `m_CachedPtr` held ASCII text.
- All of these are use-after-free in the IL2CPP heap. The game runs the incremental Boehm GC (`boot.config`: `gc-max-time-slice=3`). That GC is also generational: it only rescans dirty pages, so an object stored without a write barrier gets freed while still in use.
- Unhollower stores skip the barrier in two places:
  - `Il2CppReferenceArray` indexers / implicit `T[]` conversions
  - setters of non-blittable struct fields, which use `Unsafe.CopyBlock`
- `Shared\Il2CppArrays.cs` already covers the array case. No single bad store has been found yet.
- Going to the menu doesn't cause the corruption. It just runs the full walk that exposes it.

**Chosen fix: turn the incremental GC off before IL2CPP starts**
- `GameAssembly.dll` reads the env vars `GC_DISABLE_INCREMENTAL`, `GC_ENABLE_INCREMENTAL` and `GC_PAUSE_TIME_TARGET` through `getenv`, in the GC init routine (0x1802f2420, called from `il2cpp_init` at 0x1802bab04).
- The incremental flag is at 0x182862620, which is what `il2cpp_gc_is_incremental` reads.

**Done so far (built, 2554 tests pass, deployed to the game)**
- `Plugin\TogetherPlus.GcFix.csproj` and `Plugin\GcFix.cs`: a MelonPlugin that sets `GC_DISABLE_INCREMENTAL=1` in `OnEarlyInitializeMelon`.
- `MainMod.cs`: `LogGcMode()` logs at startup whether the incremental GC is on.
- Scripts:
  - `build.ps1` builds the plugin, adds `Plugins\` to the package, and deploys it.
  - `install.ps1` copies `Plugins\TogetherPlus.GcFix.dll`.
  - `uninstall.ps1` removes it.

**Result: the fix does not work yet.** The plugin loads at 00:11:42, before the start screen and the assembly generator, but the mod still logs `[GC] Incremental GC is on`.

**Left to do**
- [ ] Build and run the diagnostic. `Plugin\GcFix.cs` has a temporary `DIAG` line: it logs `il2cpp_gc_is_incremental()` and the env var right after setting it. This shows whether il2cpp is already initialized when the plugin runs (too late) or the variable just isn't seen.
  - Possible cause: .NET `SetEnvironmentVariable` updates the Win32 environment, but the CRT `getenv` reads the CRT's own copy. If so, P/Invoke `_putenv_s` from the CRT that GameAssembly links (ucrtbase/msvcrt) instead.
- [ ] If il2cpp is already initialized, disable it at runtime:
  - `il2cpp_gc_set_mode` / `GarbageCollector.GCMode` won't work: those toggle enable/disable, not incremental.
  - Option A: start a full `il2cpp_gc_collect` and patch the flag at 0x182862620 to 0 (GameAssembly base + RVA 0x2862620; check the bytes / `il2cpp_gc_is_incremental` disasm before writing).
  - Option B: set `GC_DISABLE_INCREMENTAL` for the whole game process, e.g. a launch wrapper or Steam launch options (`cmd /c "set GC_DISABLE_INCREMENTAL=1 && %command%"`), or a user env var written by `install.ps1`.
- [ ] Remove the `DIAG` line, rebuild (`.\build.ps1 -Deploy`), and confirm the log shows `[GC] Incremental GC is off`.
- [ ] Retest: host, take a job, save, go back to the menu (several times).
- [ ] Separately, `CarSpawnManager.LoadJobCar` fails 3/3 with `ArgumentNullException` reading car_katagiritamago (the stack trace isn't logged; only the message). This may be a symptom of the same corruption, so recheck it after the GC fix.
- [ ] Mention the new `Plugins\TogetherPlus.GcFix.dll` in README / docs once it works.
