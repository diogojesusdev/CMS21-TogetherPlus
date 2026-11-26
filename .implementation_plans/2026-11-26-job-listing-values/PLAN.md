# Show the real job payout in the order summary

## Goal

User: "Even though I receive more money on jobs that money doesn't appear on the job listing, please show the
correct value there, same for XP if its wrong (i didnt check)".

The order summary must show the amount the host actually pays (3x the order payout + the flat 500), not the game's
base `TotalPayout`; same check for XP.

## Findings

- The order summary tab (`CMS.UI.Logic.Tabs.CarInfo.OrderSummaryTab`) builds its three texts in `SetupJobs(CarLoader, Job)`:
  `TaskBonus` from `Job.TaskBonus` (0x94), `JobBonus` from `Job.JobBonus` (0x98) and `TotalPayout` from `Job.TotalPayout`
  (0x9C), each formatted by the game and written to the tab's `Text` fields.
- Since rc52 the host pays `3 x TotalPayout + 500` (`Payouts`), so the summary has shown the base amount (e.g. 158)
  while the wallet got 974.
- XP: scanning every order/job/car-info UI method in `Assembly-CSharp-firstpass` for reads of `Job.XP` (0xA8) found
  no display of the job's XP. The order board shows car, timer and category icons, the order summary shows task
  bonus / job bonus / total payout, and the order tab only shows the "+25% XP" bonus marker (`Job.BonusToExp`).
  The XP bar itself is updated by `GlobalData.AddPlayerExp` when the order is handed over.

## Approach

- `JobRewards` gets `DisplayedPayout`, `DisplayedTaskBonus` and `DisplayedJobBonus`, using the same `Payouts`
  constants as the host, so display and payment can never drift.
- A Harmony prefix on `OrderSummaryTab.SetupJobs` swaps the three `Job` fields for the displayed values, lets the
  game format and write them, and a postfix puts the base values back. The hand-over (`Handover.AskEnd`) therefore
  still sends the game's `TotalPayout` and the host does not multiply twice.
- Campaign only: in sandbox the host pays nothing, so the vanilla numbers stay.
- Rejected: rewriting the `Text` values after the fact (would have to redo the game's currency/localisation
  formatting), and changing the `Job` fields permanently (would double-multiply the payout on hand-over).

## Files

- `Shared/Economy/JobRewards.cs`, `Tests/JobRewardsTests.cs` (display helpers)
- `ClientSide/Data/Garage/Campaign/JobHooks.cs` (prefix/postfix on `OrderSummaryTab.SetupJobs`)
- `MainMod.cs` (version), `dist/0.5.0-rc54-notes.md`

## Testing

- `.\build.ps1 -Package`: display helpers match the payout formula (`0 -> 500`, `158 -> 974`, `337 -> 1511`, and
  the components times three); all existing payout tests still pass.
- In game: open an order's summary tab; Tasks Bonus / Job Bonus / Total Payout show the multiplied values, the
  hand-over pays exactly that total, and the order's own stored `TotalPayout` is unchanged (the host log still
  shows the base payout and pays `3x + 500` once).

## Status

Done in 0.5.0-rc54. The job XP is not shown on the order board, order tab or order summary in the game (checked
every order/job/car-info UI method that reads `Job.XP`), so only the payout needed the correction; the XP bar
itself updates on hand-over.
