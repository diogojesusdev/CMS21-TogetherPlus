# Faster XP from orders

## Goal

User: "Also, the XP should go up quicker. 2 simple jobs should level up 1 level. 1 hard job should give 1 level".

An order's XP should be a share of the current level's bar: half a level for a simple order, a full level for a hard
one, so the number of orders per level no longer grows with the (now much larger) job list.

## Findings

- `JobHooks.CompleteHandover` gives the order's raw `Job.XP` (2-37 for ordinary orders, 162-202 for a story
  mission) through `GlobalData.AddPlayerExp`. That is the vanilla amount, and it is tiny next to the level bar.
- The game's level bar for the current level is `GlobalData.GetDiffToNextLvl(PlayerLevel)` (public). `GlobalData.AddPlayerExp`
  itself uses it: it fills `PlayerExp` up to that value, calls `UpgradeSystem.AddPoints()` and increments `PlayerLevel`
  (verified in `GameAssembly.dll`, RVA 0xD7BF60-0xD7C08F). `GetCurrentExpToNextLevel` is the UI's view of the curve.
- The curve is `(int(2 * level^1.62221) - int(2 * (level-1)^1.62221)) * 75`: 300 XP for level 1 -> 2, 375 for 2 -> 3,
  525 for 3 -> 4, and it keeps growing. So a fixed XP number can never deliver "2 jobs per level" at every level; a
  share of the current bar can.
- XP is per player and already synced (`AddPlayerExpHook` -> `ExpPacket` -> host stores `playerExp`/`playerLevel`,
  `ProgressGuard` keeps the highest), so no new packet is needed.

## Approach

`JobRewards.ExperienceFraction(xp, mission)`:
- hard (`mission` or `xp >= 100`): **1.0** of the current level bar -> one hard order per level;
- big ordinary order (`xp >= 35`): **0.75**;
- simple: **0.5** -> two simple orders per level.

`CompleteHandover` replaces the raw `job.XP` with `ceil(bar * fraction)` (at least the vanilla `job.XP` when the
bar is 0, e.g. at the level cap). The bar is read with `GlobalData.GetDiffToNextLvl(GlobalData.PlayerLevel)`, the
same value `AddPlayerExp` fills, and the grant goes through `GlobalData.AddPlayerExp` so level-ups, per-level skill
points, the XP bar and the host sync all run the game's own path.

Rejected: multiplying `job.XP` by a constant (the bar grows, so 2 jobs per level would only hold at one level);
and granting a fixed 150/300 XP (same problem).

## Files

- `Shared/Economy/JobRewards.cs` (fraction + constants), `Tests/JobRewardsTests.cs`
- `ClientSide/Data/Garage/Campaign/JobHooks.cs` (`GrantExperience`)
- `MainMod.cs` (version), `dist/0.5.0-rc53-notes.md`

## Testing

- `.\build.ps1 -Package`: tests cover the three tiers and the anchors (two simple fractions reach a full bar; a hard
  one reaches it in one). In game: two ordinary orders level the player once; one story mission levels once; the
  XP bar and the host's stored level follow.

## Status

Done in 0.5.0-rc53. Follow-up: if a simple order should give more than half a level, adjust
`ExperienceFraction` (the two thresholds and the three shares).
