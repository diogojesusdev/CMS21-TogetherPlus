# Installer installs rc9 instead of the newest release

**Status:** done in 0.5.0-rc34 (script-only fix, deliberately not version-bumped - see Status).

## Goal

> "OK, so push the latest release to the remote so he can download" - with a screenshot of a friend's PowerShell:
> `irm https://raw.githubusercontent.com/diogojesusdev/CMS21-TogetherPlus/main/install.ps1 | iex` printing
> `Release: v0.5.0-rc9`, `Package: TogetherPlus-0.5.0-rc9.zip`, `Together+ updated.`

The friend ran the documented one-liner on 2026-11-06 and got **rc9**, not rc33. Two players installing today end up
on a 25-releases-old build.

## Findings

**The releases API does not return releases in version order.** `repos/.../releases?per_page=10` answers:

```
v0.5.0-rc9   created=2026-11-05T00:10:30Z  id=434608749
v0.5.0-rc8   created=2026-11-05T00:06:02Z  id=434605980
v0.5.0-rc7   created=2026-11-05T00:03:34Z  id=434604479
v0.5.0-rc33  created=2026-11-05T21:31:47Z  id=435767808
v0.5.0-rc32  created=2026-11-05T21:22:32Z  id=435758847
...
```

rc7-rc9 sort ahead of rc10-rc33 by neither `id` nor `created_at` descending; whatever the platform is doing, the
response cannot be trusted to be newest-first.

**The installer trusted that order.** `install.ps1` walked the list and took the first entry with a
`TogetherPlus-*.zip`:

```powershell
foreach ($release in $releases) {
    if ($release.draft) { continue }
    $asset = $release.assets | Where-Object { $_.name -like 'TogetherPlus-*.zip' } | Select-Object -First 1
    if ($asset) { Say "Release: $($release.tag_name)"; break }   # rc9 wins
}
```

So the one-liner in the README has been installing rc9 since rc10 was published. This was invisible in the logs of
auto-updating players, because **the mod's own updater never had the bug**: `Updater.FindNewest` scans every release
and keeps the highest version with `VersionText.IsNewer` (`Plugin/Updater/Updater.cs:359-370`). Only
installer-based installs were affected - which is exactly the population that never gets a version file, so they
also never self-correct.

**Consequence for the friend:** rc9 predates the updater entirely (first shipped in rc12; the rc9 package has no
`Plugins/` folder and no `Mods/CMS21-Together.version`), so he cannot be reached by auto-update and must be
re-installed.

## Approach

Choose the newest release by **version**, not by position:

1. `Get-VersionKey` turns a tag into a sortable string (`0.5.0-rc33` -> `000000.000005.000000.0.000033`); a final
   release ranks above its own prereleases. Unparseable tags (`Release-0_4_17`, the 0.4.x era) yield `$null`.
2. Read one page of `per_page=100` releases, keep non-drafts that have a `TogetherPlus-*.zip`, sort by key descending,
   take the first.
3. If no tag parses (older repo layouts, or a future tag scheme), fall back to the previous behaviour - first
   non-draft release with a zip - so the script never gets *worse* at finding something to install.

Alternatives rejected:

- **Sort by `created_at`.** Simpler, and would fix today's data. Rejected as the rule: creation time is metadata, not
  the version we mean. A backport of an older version published later would win over the current one, and the mod's
  updater compares versions, so the two would disagree about "newest".
- **Pin the version in the script** (`?tag=v0.5.0-rc33`). Guarantees the correct build today and is wrong tomorrow -
  every release would need the installer edited.
- **Use `releases/latest`.** 404s on this repo: every release is a prerelease, so there is no non-prerelease "latest".
- **Fix it only in the mod's updater.** The updater is already correct; the affected population cannot run it.

## Files

- `install.ps1` - `Get-VersionKey` helper and the release-selection block (step 3).
- This plan.

Not touched: `MainMod.cs`, `Shared/`, `Plugin/`. The checkout held another session's unfinished rc34 work, so the
change is confined to the one file that is independent of the mod build.

## Testing

- Ad-hoc harness (temp script, not committed): eval `Get-VersionKey` out of `install.ps1`, feed scrambled release
  lists, assert the pick:
  - `[rc9, rc8, rc7, rc33, rc32]` -> rc33 (the live bug).
  - `[0.5.1 (stable), 0.5.2-rc1, 0.5.2-rc10]` -> `0.5.2-rc10` (prerelease of a newer patch, numeric not lexical).
  - `[rc9, rc10]` -> rc10 (the lexical trap: `"rc10" -lt "rc9"` as strings).
  - unparseable tags only -> falls back, does not throw.
  - entries without a zip asset are skipped.
- Live check after pushing: `gh api repos/.../contents/install.ps1?ref=main` shows the fix, and the selection logic
  run against the real API response picks rc33 (or the newest, once rc34 ships).
- Player check: the friend re-runs the one-liner and the log says `Release: v0.5.0-rc33`.

## Status

**done - fixed on `main` in 0.5.0-rc34, without a version bump of its own.**

Timeline that shaped the fix: while this was being written, a parallel session finished the menu-return work,
committed and pushed it, and published **v0.5.0-rc34** (23:25:48) - whose `install.ps1` asset was built from the tree
*before* this fix, so it still had the bug. The API reflected that too: by the end it answered `rc34, ...` first
instead of `rc9, rc8, rc7, ...`, which is the clearest argument that position in that list means nothing and only
version comparison can be trusted.

Deviations from the usual rules, deliberate and approved in conversation (2026-11-06):

- **No version bump for this change.** It only touches `install.ps1`, which players receive from
  `raw.githubusercontent.com/.../main/install.ps1`. Bumping to rc35 would have forced every player to update the DLL
  and re-handshake for a script edit, and the tree at the time carried another session's unfinished rc34 work that
  this change must not publish.
- **The rc34 release's `install.ps1` asset was replaced** with this version (`gh release upload --clobber`), so the
  release page and the raw URL agree. The already-built rc34 zip still contains the old copy; that is harmless - when
  a package sits next to the script, the installer uses the package and never queries the API (the bug only fires on
  the download path), so `Install.bat` from the zip was never affected.
- Committed only `install.ps1` and this plan.

Follow-ups (not done here):

- `per_page=100` is one page; if the repo ever exceeds 100 releases, page through the `Link` header instead.
- The installer does not verify the downloaded zip against its `.sha256` asset. Worth adding: the updater already
  refuses releases without one, so the installer should prefer releases that have both.
- The README could mention the `-Zip` workaround for anyone stuck on an old build.
