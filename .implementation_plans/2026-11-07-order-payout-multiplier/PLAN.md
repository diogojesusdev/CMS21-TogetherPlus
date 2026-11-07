# Order payout multiplier (3x)

## Goal
User: "Some jobs are giving 29€ WTF is that?? doesn't seem fair at all. can you balance it?" then chose: "add a host-side payout multiplier of 3x".

## Findings
- Payouts (25, 29, 42, 369, 1428, 1440, 2106) are the game's own `Job.TotalPayout`; the mod only passes the amount to the shared wallet.
  Small orders pay very little in vanilla, and with several players splitting one wallet it feels worse.
- rc43 (breakdown log) was shipped to explain low values; it does not change amounts.

## Approach
- `Payouts.OrderPayoutMultiplier = 3`, applied on the host when a hand-over is approved (`Decide`, JobEnd). The multiplied amount is
  what is range-checked, stored in the approval and granted by `CompleteEnd`. Clients are unaffected (they show the shared balance),
  so only the host's version matters. Car sales and other credits are not multiplied.
- The game's order summary still shows the vanilla payout; the wallet gets 3x. The host log shows `paid <amount>` (already multiplied).
- Rejected: client-side multiplier (every client would need the same value and the shared wallet is host authoritative).

## Files
`Shared/Economy/Payouts.cs`, `Tests/PayoutsTests.cs`, `MainMod.cs`.

## Testing
- Unit: approved payout is multiplied; overflow check uses the multiplied amount.
- In game: hand over an order, wallet rises by 3x the summary payout.

## Status
Done in 0.5.0-rc44.
