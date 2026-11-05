# Bug audit cleanup: close the remaining minor findings, then drop the report

**Status:** done in 0.5.0-rc23

## Goal

The request: "arent the bug audit bugs fixed already? if not, fix them and then delete the file".

`docs/BUG_AUDIT.md` says the §1–§9 findings were fixed in **0.5.0-rc15**; the §10 table and the §9 "minor UI items"
were partly left, on purpose, by the five rc15 plans. This change re-reads every item against the current tree, fixes
what is safe to fix, records what is deliberately accepted, and removes the stale report so nobody re-fixes it.

## Findings (verified against the current tree, 0.5.0-rc22)

Already fixed in rc15 or later, re-checked here:

- §1–§9 (hand-over, welder, fluids/wash/paint, parts, crane, enter/exit, economy, lobby): fixed.
- §9 minor: the lobby **Kick** button now gets a listener when the row may be kicked (`UILobby.cs:187-191`); the menu
  version check is async and once per process (`UICore.cs:83-120`); the inventory search copies game lists without
  `ToArray()` and without the `ConcurrentBag` (`Inventory.cs:365`).
- EC-3/H-1 (payout): `ServerHandle.EndJobPacket` now grants from the host's own stored order, not from the ask.

Still open (this change):

1. **UI-3** `ServerSend.WheelBalancePacket(int, ModGroupItem)` writes only the item while the only reader expects an
   action first. Dead today, a trap for the next caller.
2. **UI-4** `ServerHandle.ResyncPacket` routes `playerInCar`/`carEngineSound` to handlers that read fields a resync
   request never carries: any such packet throws.
3. **CE-3** the engine-sound sync is dead (`enginePlaying` is hardcoded false), and both ends only log.
4. **UI-5** `ClientSide/Data/Garage/Tools/WheelBalancerLogic.cs` is not in the csproj and references senders that no
   longer exist; adding it to the build would double-patch the wheel balancer.
5. **H-2** `ServerHandle.RotationPacket` indexes `connectedClients[fromClient]` without a check (the sibling position
   handler uses `TryGetValue`).
6. **CR-4** `CarChecks.Hash` still hashes a car flagged `needResync`, whose `PartScript`s belong to the destroyed scene.
7. **ES-4** `EngineStand.HandleEngineStand` reads the static `useAlt` up to five seconds after the action.
8. **FL-4** the two fluid postfixes share one one-shot `listen` flag: the second hook of one apply re-sends the applied
   value, and a throw after the flag is consumed swallows the next real change.

Accepted, not fixed (recorded here because the report is being deleted):

- **EC-4** two debits in one frame: the item op carries a single transaction number, and nothing at send time knows
  which of the two debits paid for which item. Fixing it needs per-item attribution on the wire; the window is one
  frame and needs two money actions in it.
- **EC-6** the host's `gamemode` is set ~2 s after the garage is ready, so a hand-over in that window is treated as
  sandbox (no payout). The correct source, `SavesManager.currentSave.Difficulty`, is only valid once the save is
  loaded; guessing it earlier risks paying into a sandbox session, so this stays until it can be checked in game.
- **LK-1** moving a tool is not locked: two players can move the same tool and the last write wins. A lock needs a
  release edge (tools are moved once, not held) that cannot be verified without the game; the visible result is a
  tool that jumps, not lost progress.
- **H-1** the host still credits sale prices and upgrade costs sent by the client. Verifying them needs the host's own
  price tables from the game; a modified client is needed to exploit it (accepted since rc15).

## Approach

1. Delete the mismatched `WheelBalancePacket` overload and the two resync cases.
2. Remove the dead engine-sound path end to end (sender, client handler and its registration, host handler, relay, send
   helper), keeping `PacketTypes.carEngineSound` in the enum so the pinned wire numbers in `PacketTypesTests` stay
   valid and an older build's packet is simply unknown.
3. Delete the uncompiled `WheelBalancerLogic.cs`.
4. Use a single `TryGetValue` in the rotation and in-car handlers.
5. Skip a car marked `needResync` in `CarChecks.Hash`.
6. Capture the stand in `HandleEngineStand` before the waits.
7. Replace the fluid one-shot flag with a frame-window suppression (`PartUpdateHooks.Suppress()`), which survives both
   postfixes of one apply and cannot be left consumed by a throw.
8. Add a source invariant: no `connectedClients[...]` member read outside an assignment, so H-2 cannot come back.
9. Delete `docs/BUG_AUDIT.md`.

## Files

- `ServerSide/Data/ServerSend.cs`, `ServerSide/Data/ServerHandle.cs`
- `ClientSide/Client.cs`, `ClientSide/Data/Handle/ClientHandle.cs`, `ClientSide/Data/Player/CarEnterExit.cs`
- `ClientSide/Data/Garage/Car/CarChecks.cs`, `ClientSide/Data/Garage/Car/PartUpdateHooks.cs`,
  `ClientSide/Data/Garage/Car/PartsUpdater.cs`, `ClientSide/Data/Garage/Tools/EngineStand.cs`
- delete `ClientSide/Data/Garage/Tools/WheelBalancerLogic.cs`
- `Tests/SourceInvariantTests.cs`
- delete `docs/BUG_AUDIT.md`
- `MainMod.cs` (version), `dist/rc23-notes.md`

## Testing

- `.\build.ps1 -Package` (all tests, including the new invariant and the existing 2,709 cases).
- In game (host + client): wheel balancer still syncs; a car rotation still follows the other player; a fluid change
  is sent once and not echoed; the engine stand announces the right stand; the kick button still works; no engine-sound
  log lines.

## Status

In progress.
## Outcome

All eight items above were fixed; the four accepted items stay documented here now that `docs/BUG_AUDIT.md` is
deleted. Verification: the mod builds clean (0 warnings), 2,710 tests pass including the new
`TheHostNeverReadsAClientSlotItDidNotCheck` invariant. The in-game checks listed under Testing were not possible
here (no game install on this machine).
