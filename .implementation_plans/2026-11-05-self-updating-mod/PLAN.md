# Self-updating mod

**Status:** done in 0.5.0-rc13. End-to-end tested against a real release (see Testing).

## Goal

TODO item: "MelonLoader can't swap a mod while the game is running, but it can update between launches."

- A small `TogetherPlus.Updater.dll` in the game's `Plugins` folder, loaded before any mod.
- At game start it checks this repository's GitHub releases for a newer version.
- If one exists it downloads the zip, checks the SHA-256 published with the release, and replaces the mod and
  `UserLibs` files (safe: the mod is not loaded yet), so the game starts on the new version with no extra restart.
- Extras: match the host's version when a join is refused, a channel setting (pre-releases / stable / off), rollback
  of an update that fails to start, HTTPS plus hash verification only.

## Findings

- MelonLoader 0.5.7 load order: plugins (`OnEarlyInitializeMelon`), then the start screen, the assembly generator,
  `OnPreModsLoaded`, then mods. A plugin can therefore replace `Mods\CMS21-Together.dll` and `UserLibs\*.dll` before
  they are loaded.
- Plugins that are already loaded (`TogetherPlus.GcFix.dll`, the updater itself) are file-locked, so an automatic
  update cannot replace them; the installer must (documented as a limit).
- The mod version (`MainMod.PLUS_VERSION`) lives in the DLL that must not be loaded before replacing it, so the
  installed version is recorded in `Mods\CMS21-Together.version` (installer and `build.ps1 -Deploy` write it; the mod
  refreshes it on every successful start).
- Release tags are `v0.5.0-rcN`; the RC number must compare numerically (`rc9 < rc10 < rc12`) and a full release must
  compare after its pre-releases.

## Approach

1. `Shared/Updates/VersionText.cs`: parse tags, compare versions, and read the host version out of the transport's
   rejection message. Pure .NET, unit tested.
2. `Shared/Updates/UpdateFiles.cs`: paths and marker files (installed version, pending, request, started, failed).
   Pure .NET, unit tested.
3. `Plugin/Updater/TogetherPlus.Updater.csproj` + `Updater.cs`, a MelonPlugin:
   - settings in `MelonPreferences.cfg` under `[TogetherPlusUpdater]`: `Channel`
     (`prerelease`/`stable`/`off`) and `CheckOnStartup`;
   - roll back an update that never started (backup in `TogetherPlus\Updates\backup`, `failed.txt` prevents a loop);
   - newest release for the channel, or the exact version in `request.txt` when a host refused a join;
   - download the zip and its `.zip.sha256` over HTTPS, verify, extract, back up the current files, then replace the
     mod, `UserLibs`, and docs; write `pending.txt` so the next launch can roll back if the mod never starts;
   - `DataContractJsonSerializer` for the GitHub JSON (no extra dependency), 15 s API timeout, 10 min download.
4. `MainMod`: on a successful init, write the installed version and the started marker. `Client`: on a version
   mismatch, write `request.txt` with the host's version and log "restart to update".
5. `build.ps1 -Package` writes `dist\TogetherPlus-<version>.zip.sha256` (published as a release asset);
   `install.ps1`/`uninstall.ps1` copy/remove both plugins and the version sidecar.
6. Alternatives rejected: MelonPreferences-only config (no, prefs are used; a JSON file would duplicate it); an
   external launcher (worse UX); updating from the mod itself (the DLL is loaded and locked); trusting GitHub's order
   of releases (versions are compared instead).

## Files

- `Shared/Updates/VersionText.cs`, `Shared/Updates/UpdateFiles.cs` (+ `CMS21-Together.csproj`, `Tests/Tests.csproj`)
- `Plugin/Updater/Updater.cs`, `Plugin/Updater/TogetherPlus.Updater.csproj`
- `MainMod.cs`, `ClientSide/Client.cs`
- `build.ps1`, `install.ps1`, `uninstall.ps1`, `README.md`
- `Tests/UpdateFilesTests.cs`

## Testing

- Unit tests: tag parsing, numeric RC ordering, release after pre-release, host-version extraction, marker files
  (2574 tests pass).
- In game: install a sidecar version older than the newest release and start the game; the log must show the
  download, the checksum check and `[Update] Together+ <version> installed`, and the mod must load.
- Safety checks to try once: a release without a `.sha256` must be refused; a pending marker without a started
  marker must roll back.
- Offline / GitHub blocked: `[Update] The update check failed: ...` in the log and the installed version keeps working.

## Follow-ups

- The updater and the GC plugin are only replaced by the installer (they are loaded when a check runs).
- A future protocol change can use `request.txt` to pin clients to the host's version; the server already sends its
  version in the rejection.

## End-to-end test (2026-11-05, rc12 release, rc13 fixes)

Installed rc11 (sidecar) with the rc12 updater, started the game: the updater downloaded rc12, the SHA-256 matched,
the mod was replaced and MelonLoader loaded the new DLL in the same launch; the log showed
`[Update] Together+ 0.5.0-rc12 installed (was 0.5.0-rc11)` and the mod reached the menu.

That test found two real bugs, fixed in rc13:

1. The installed version was read before the rollback check, so after a rollback the same launch still compared against
   the new version and skipped re-installing. The version is now read after the recovery step.
2. A game that quits before the mod loads (on this launcher, the extra instance Steam starts) was treated as "the new
   version does not start": it rolled back and put the version on the failed list, where it would never be offered
   again. Now a version is only blamed when another version had already started on this machine (a started marker
   exists and differs); otherwise the update is kept until a start succeeds. A successful start also clears its own
   failed entry (`UpdateFiles.ClearFailed`).
