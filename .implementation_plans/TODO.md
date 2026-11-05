# TODO

Ideas not started yet. To start one, create its plan folder (see `AGENTS.md`) and remove the idea from this list.

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
