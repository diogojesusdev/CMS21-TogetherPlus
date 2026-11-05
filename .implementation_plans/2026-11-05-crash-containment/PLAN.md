# Crash containment and copyable error reports

**Status:** done in 0.5.0-rc15

## Goal
Reduce the number of full crashes and give the player a recoverable path: when a mod action fails, cancel it, warn
the player with a copyable report, and keep the game running. Native access violations cannot be caught in-process;
this targets the managed failures and invalid-state calls that precede most of the mod's crashes.

## Findings
- Apply coroutines (`MelonCoroutines.Start`) that throw die silently and leave half-applied state.
- Some native calls run with stale/missing car/part references (the audit's CR/PT/ES findings); checking first lets
  us cancel instead.
- The router catches handler exceptions but nothing tells the player or copies the error.
- Nothing records an unclean shutdown, so a native crash leaves no user-facing breadcrumb.

## Approach
1. `Shared/Safety/CrashReport.cs`: build a report (version, scene, last actions, exception), write it under
   `TogetherPlus/diagnostics/`, copy it to the clipboard, and show a throttled toast/popup. A ring of breadcrumbs is
   kept in memory.
2. `Shared/Safety/Guard.cs`: `Run`/`Try` for synchronous blocks and `Coroutine` for apply coroutines. A failure logs,
   reports, optionally invalidates the affected car (`onError` → `ClientSend.ResyncCar`), and returns.
3. Use `Guard.Coroutine` for the part/car/tool apply starts and add pre-flight reference checks in `PartsUpdater`:
   a bad car/part is a cancelled action + resync request, not a native call.
4. `Ctrl+Shift+E` copies the last report (Recovery key); `Ctrl+Shift+L` keeps the diagnostics dump.
5. Session marker: write on start, remove on clean quit; if it exists at the next menu load, show "the previous
   session ended unexpectedly" with the report path and copy it.

## Files
- `Shared/Safety/{CrashReport,Guard}.cs` (new; add to `CMS21-Together.csproj`, `Tests.csproj`, `BotHarness.csproj`
  if tests use them)
- `ClientSide/Data/Handle/ClientHandle.cs`, `ClientSide/Data/Garage/Car/PartsUpdater.cs`
- `ClientSide/Data/Recovery.cs`, `ClientSide/Data/NewUI/UICore.cs`, `MainMod.cs`

## Testing
- New `Tests/GuardTests.cs` (report text, ring, coroutine failure path with a fake sink; no Unity UI).
- Build + suite; in-game: force a failing apply (e.g. part for an unloaded car), see the toast and a copied report,
  and the car resyncs; kill the game and restart to see the unexpected-shutdown notice.

## Deviations / follow-ups

- Native access violations still kill the process; this change contains managed failures, validates the risky apply
  paths before calling the game, and leaves a session marker plus `TogetherPlus/diagnostics/crash-*.txt` for the next
  start.
- The report is copied to the clipboard automatically and `Ctrl+Shift+E` copies the last one; the warning reuses the
  existing info panel instead of a new window, to avoid new UI code on the failure path.
- `Tests/CrashReportTests` covers the report, the guard and the coroutine wrapper; the clipboard and UI parts need the
  game.
