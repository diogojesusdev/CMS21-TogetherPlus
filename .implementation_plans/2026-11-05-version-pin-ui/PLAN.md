# Version pinning UI in the multiplayer menu

**Status:** done in 0.5.0-rc18

## Goal

User request: "I would like the user to be able to pin a version via a new in-game UI, inside the multiplayer
sub-menu if possible. latest, and a list of all other available versions. is that possible? the user sets the version
but if a restart is needed the user should be warned about that".

## Findings

- The updater already has a forced path for a single version (`Updates/request.txt`, written when a host rejects the
  client for a version mismatch), but there is no user-facing pin: after the forced install the request is cleared and
  the next launch returns to the newest release.
- The multiplayer menu (`UIMenu.SetupMultiplayerMenu`, `UICore.MP_Main`) has room for one more button and the project
  already has a `StringSelector` (`UIElements.CreateSelector`) and a modal panel pattern (`UICustomPanel`).
- The version list can come from the GitHub releases API; the updater already reads it in `Updater.cs` (private JSON
  classes). The mod can parse the same payload in a shared, testable helper.
- A version cannot be swapped while the game runs: the updater only runs before the mod loads, so a pin takes effect on
  the next launch and the UI must say so.

## Approach

1. `Shared/Updates/ReleaseCatalog.cs`: parse a GitHub releases payload into versions that have both the zip and
   `.sha256` asset, sorted newest first; keep the pre-release flag. Pure .NET, unit tested.
2. `UpdateFiles`: add `pinned.txt` read/write/clear.
3. `Updater`: apply the pin before the mismatch request and the "newest release" checks; the pin is forced (older pins
   install too), persists across launches and is not cleared after install. A pinned version that failed to start is
   not retried (`failed.txt`); the player can pick another in the UI.
4. UI: a "Version: …" button in the multiplayer menu opens a modal with a `StringSelector`:
   "Latest (auto-update)" plus every published version (pre-release/installed/pinned markers). Apply writes the pin,
   clears any stale request, and shows a "restart required" question when the chosen version differs from the installed
   one. "Undo pin" clears it.
5. `Client.NoteVersionMismatch`: while a pin is set, do not overwrite it with a mismatch request; log that the pin must
   be changed in the UI to join that host.
6. Document the pin in `AGENTS.md` and `README.md`.

## Files

- `Shared/Updates/ReleaseCatalog.cs` (new)
- `Shared/Updates/UpdateFiles.cs`
- `Plugin/Updater/Updater.cs`
- `ClientSide/Data/NewUI/VersionPanel.cs` (new), `UICustomPanel.cs`, `UIMenu.cs`
- `ClientSide/Client.cs`
- `Tests/ReleaseCatalogTests.cs` (new), `Tests/UpdateFilesTests.cs`
- `CMS21-Together.csproj`, `AGENTS.md`, `README.md`

## Testing

- Unit tests: release JSON parsing (draft skipped, zip+sha256 required, versions sorted), pin file read/write/clear.
- `.\build.ps1 -Package` (full NUnit suite).
- In game: Multiplayer → Version; latest list loads; pin an older release → restart warning → restart installs it and
  the menu shows the pin; Latest clears it; a pinned older host can be joined without the mismatch overwriting the pin.

## Status

Done in 0.5.0-rc18. Unit tests cover the catalog filter and the pin file; the selector/panel needs the in-game check (Multiplayer -> Version, pin an older release, restart, verify the version and that the pin survives, then Latest). Follow-ups: none.