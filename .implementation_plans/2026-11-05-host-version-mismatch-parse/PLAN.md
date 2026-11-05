# Host-version mismatch: the client cannot auto-target the host's exact build

**Status:** done in 0.5.0-rc17

## Goal

Follow-up agreed with the user after the rc16 release discussion: fix the pre-existing path where a client kicked for a
version mismatch is supposed to remember the host's version and have the updater install exactly that build on the next
launch. Today it silently never does.

## Findings

- The host rejects a mismatched client with (`Shared/Net/NetHost.cs:281`):
  `Mod version mismatch: host has {modVersion}, you have {hello.ModVersion}.`
  `modVersion` is `MainMod.NET_VERSION` = `"TogetherPlus-" + PLUS_VERSION` (`MainMod.cs:31`), so the real text is
  `host has TogetherPlus-0.6.0`.
- `Client.NoteVersionMismatch` (`ClientSide/Client.cs:249-269`) extracts the host version with
  `VersionText.HostVersionIn` and writes `UpdateFiles.WriteRequest(PlusPaths.Root, hostVersion)`.
- `VersionText.HostVersionIn` (`Shared/Updates/VersionText.cs:17`) expects `host has Together+ 0.5.0-rc12` (or a bare
  version) and does **not** accept the `TogetherPlus-` prefix, so it returns null and no request is written.
- `VersionTextTests` only exercised the fabricated `Together+ 0.5.0-rc12` wording, which is why this stayed green.
- Effect: a client on rc16 that is rejected by a host on an older/different version keeps its own build and cannot
  join. A newer published release is still found by the normal "newest release" check, so a plain upgrade is not
  affected — this only breaks the host-specific targeting.

## Approach

1. Broaden the prefix in `HostVersionIn` to accept `Together+ `, `TogetherPlus-`, `TogetherPlus `, `Together ` and
   the bare version; keep the version capture group unchanged.
2. Add tests with the actual `NetHost` wording (`TogetherPlus-0.6.0`), plus the existing `Together+` and bare forms,
   and confirm a protocol-mismatch reason still yields null.
3. Document the wording/parser contract in `AGENTS.md` (release/auto-update section), because a future change to the
   rejection text would silently break the feature again.

## Files

- `Shared/Updates/VersionText.cs`
- `Tests/UpdateFilesTests.cs` (class `VersionTextTests`)
- `AGENTS.md` (contract note)

## Testing

- `dotnet test Tests\Tests.csproj` (new `HostVersionIn` cases).
- `.\build.ps1 -Package` + NUnit suite.
- In game (two clients): host on rc17, client on rc16; the client is rejected, then its next launch installs v0.5.0-rc17
  (the host's version) from the release, not merely the newest release. This requires the host's version to be an
  older/other published tag, which is the scenario the request path exists for.

## Status

Done in 0.5.0-rc17. The regression tests now use the real `TogetherPlus-` wording, `Together+`, `TogetherPlus ` and bare versions; the existing protocol-mismatch case still returns null. Follow-ups: none; the updater and installer already consume the request.