# Order payout looks too low (42)

## Goal
User: "a friend of mine (client) finished a job but we didn't receive any money ... i dont know but 42 seems very little".

## Findings
- rc42 host log: `#2 JobEnd 27 (42): Applied.` then `#2 ended order 27: paid 42.`; the only hand-over of the session.
  The rc40 fix works mechanically: the host credited 42 into the shared wallet (ledger Applied, balance sent to all).
- The amount is whatever the handing-over player's `Job.TotalPayout` was when the ask was sent (`Handover.AskEnd`).
  Earlier asks were 2106, 25, 369, 1428, so small values are not new, but the logs hold no breakdown of the order
  (TotalPayout vs TaskBonus/JobBonus/MoneySpent, tasks done), so we cannot tell if 42 is the real game reward or a
  stale/partial `TotalPayout`.
- `GameScript.EndJob` is replaced by the mod, so the game's own payout code never runs; we cannot compare against it.

## Approach
- Evidence first: the host logs (Info, once per hand-over, never per frame) the order's payout breakdown from the
  `endJob` packet: TotalPayout, TaskBonus, JobBonus, MoneySpent(+difficulty mod), XP, IsCompleted, and every task
  (type/subtype, Done, moneySpent). The next hand-over shows whether TotalPayout omits a bonus or tasks are not counted.
- No payout formula change until that evidence exists (guessing could over- or under-pay).

## Files
`ServerSide/Data/ServerHandle.cs`, `MainMod.cs`.

## Testing
- Build + tests pass. In game: hand an order over as client, read `[ServerHandle] ... payout breakdown` in the host log.

## Status
Done in 0.5.0-rc43 (diagnostics only). Follow-up: fix the amount once the breakdown shows what the game really pays.
