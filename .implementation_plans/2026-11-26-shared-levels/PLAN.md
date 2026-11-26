# Shared level/XP across the session

## Goal

User: "on my friend pc we got to lvl 5 but on mine its still lvl4. please fix that sync issue!".

The host and every player in a session should end up on the same level and XP, so one player's progress is not lost
(or left behind) on another PC.

## Findings

- Levels/XP are per player today: `JobHooks.GrantExperience` calls `GlobalData.AddPlayerExp` on the player who hands
  the order over, `Stats.AddPlayerExpHook` sends `ExpPacket` to the host, and `ServerHandle.ExpPacket` stores it in
  that player's `UserData`. The host's own `GlobalData` is never raised from a client's level, and a client only gets
  its own stored level in `PlayerSpawnPacket`. So a friend who handed over more orders is level 5 while the host is
  still 4 (seen in the 2026-11-26 session: `#2` ended orders 155/159, `#1` ended 151).
- The host already stores every player's `playerLevel`/`playerExp`, so the session-wide maximum is available.
- `ServerSend.StatsFixPacket` can raise a client's level/exp (used by `ProgressGuard`), but it only assigned the
  fields, so a jumped level did not award the per-level skill point the game's `AddPlayerExp` would have.

## Approach

- `LevelSync.IsAhead` (pure, tested) compares two players' level/XP.
- `ServerData.SyncLevels()` finds the session's highest level/XP and:
  - raises the host through `Stats.ApplyLevel` (which calls `UpgradeSystem.AddPoints` per level, exactly like the
    game's own level-up loop, and refreshes the stats UI);
  - updates every lower client's stored level/XP and sends it `StatsFixPacket`; `ClientHandle.StatsFixPacket` uses
    `Stats.ApplyLevel`, so the client gets the per-level skill points too.
- `SyncLevels` runs after every `ExpPacket` (so the host catches up as soon as anyone gains XP) and on join, before
  `PlayerSpawnPacket`, so the joining player starts on the session's level (the joiner's own spawn packet carries the
  maximum).
- The hand-over player still gets the order's XP first; the sync then raises everyone else to the same level/XP.
- Sandbox is skipped. Rejected: only sharing the order XP (the gap would stay), and setting the fields without
  `AddPoints` (players would lose the skill points of the levels they jumped).

## Files

- `Shared/Economy/LevelSync.cs` (new), `Tests/LevelSyncTests.cs`
- `ClientSide/Data/Player/Stats.cs` (`ApplyLevel`), `ClientSide/Data/Handle/ClientHandle.cs`
- `ServerSide/Data/ServerData.cs` (`SessionLevel`, `SyncLevels`), `ServerSide/Data/ServerHandle.cs`
- `CMS21-Together.csproj`, `MainMod.cs`, `dist/0.5.0-rc55-notes.md`

## Testing

- `.\build.ps1 -Package`: `LevelSyncTests` covers the comparison; all existing tests pass.
- In game: the host at level 4 and a friend at 5 come together after the next XP change (or when the friend joins);
  both see the same level and XP, the host's skill points rise by the jumped levels, and a rejoin keeps the level.

## Status

Done in 0.5.0-rc55. The hand-over player still earns the order XP first; the host syncs everyone to the highest
level/XP after every XP packet and every 5 seconds, and on join.
