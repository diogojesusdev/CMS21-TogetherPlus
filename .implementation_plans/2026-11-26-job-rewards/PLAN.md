# More money and skill points per order

## Goal

User: "jobs are paying too little. I want to receive more per job. figure out why", then:
"I would also like to get skill points more easily. 1 per job at least. more complex jobs should give more".

Two rewards per handed-over order: a bigger wallet credit, and skill points that scale with how complex the
order is (never fewer than one).

## Findings

- The host already pays `3 x Job.TotalPayout` (`Payouts.OrderPayoutMultiplier`, rc44). The logs of 2026-11-26 show
  158 -> 474, 163 -> 489 and 337 -> 1011, and `save_4.cms21mp` has the matching `+474`, `+489`, `+1011` ledger entries.
- The game pays exactly `Job.TotalPayout` once (`GameScript.EndJobCoroutine` calls `GlobalData.AddPlayerMoney`,
  RVA 0xD7B580), so the mod already pays 3x vanilla. The small numbers are the game's own: `TotalPayout` is
  `TaskBonus + JobBonus + MoneySpent`, where `MoneySpent` is the value of the parts installed (27-50% of the total is
  the actual fee; the rest is the parts refund). Small orders therefore stay small even at 3x (11 -> 33).
- Skill points today only come from levels: `UpgradeSystem.AddPoints()` adds `pointsPerLvl[playerLevel]` to the
  `availablePoints` field, and the mod's `Stats.AddPointsHook` sends that field to the host. `JobHooks.CompleteHandover`
  gives the order's XP (`GlobalData.AddPlayerExp`) but no points.
- Points are per player and already synced: `PointPacket` stores `playerSkillPoints` on the host, the spawn packet
  restores it (`ClientData.SpawnPlayer` sets `availablePoints`), and the sidecar save keeps it (`SavesManager`,
  `PlayerInfo`). The mod already writes `availablePoints` directly when a player joins, so adding to it is safe.

## Approach

Money: keep the 3x multiplier and add a flat `Payouts.OrderPayoutFlatBonus = 500` per handed-over order
(`Decide`, `PurchaseKind.JobEnd`), included in the range check and the stored approval. This lifts every order and
fixes the tiny ones the user complained about (11 -> 533, 158 -> 974, 337 -> 1511) without changing the meaning of
the existing multiplier. Rejected: a bigger multiplier alone (still leaves 11 -> 55), and a wage-only formula such as
`MoneySpent + K x (TaskBonus + JobBonus)` (at K=5 it would *lower* some current payouts, e.g. 4140 -> 3380).

Points: a pure, tested `JobRewards.SkillPoints(tasks, parts, mission)`:
`1 + max(0, tasks - 1) + max(0, parts) / 4 + (mission ? 2 : 0)`, capped at 10. One per order, one per extra task,
one per four parts, two more for a story mission. `CompleteHandover` adds the result to
`UpgradeSystem.AvailablePoints` after the XP and sends `ClientSend.PointPacket` so the host stores it for rejoin.
Rejected: giving the points to every player in the session (XP is per hand-over player, §3.8), and paying a point
per part (missions would give 20).

## Files

- `Shared/Economy/Payouts.cs` (flat bonus), `Shared/Economy/JobRewards.cs` (new pure formula)
- `ClientSide/Data/Garage/Campaign/JobHooks.cs` (grant + sync on hand-over)
- `CMS21-Together.csproj` (explicit compile entry for the new Shared file)
- `Tests/PayoutsTests.cs` (bonus in the payout assertions), `Tests/JobRewardsTests.cs` (new)
- `MainMod.cs` (version), `dist/0.5.0-rc52-notes.md`

## Testing

- `.\build.ps1 -Package`: all tests pass; `PayoutsTests` checks the bonus, the multiplied-plus-bonus range check and
  the sandbox path; `JobRewardsTests` covers 1 point for the smallest order, extra tasks/parts, the mission bonus and
  the cap.
- In game: hand an order over; the wallet rises by `3 x TotalPayout + 500` (host log `paid <amount>`), the handing-over
  player's skill window shows the extra points, and they survive a rejoin (spawn packet) and a save/reload.

## Status

Done in 0.5.0-rc52. Follow-up: if a flat 500 is not enough, raise `Payouts.OrderPayoutFlatBonus` (or the
multiplier) and re-run the in-game check; the skill-point curve lives in `JobRewards.SkillPoints`.
