# Order payout never reaches the wallet

## Goal
User: "I think that after delivering a car we didn't receive any money. please investigate".

## Findings
- Session log (rc37): every hand-over logs `purchaseAsk ... JobEnd <id> (<amount>): Applied` and the `endJob` packet is
  relayed, with no "ended order ... ignored" warning. Asked amounts: 2106 (job 2), 25, 369, 1428.
- The saved ledger (`save_4.cms21mp`, `ledgerHistory`, seq 1-20) holds **only debits**: no order payout was ever granted.
- `ServerHandle.EndJobPacket` took the amount from the host's stored copy of the order
  (`stored?.TotalPayout ?? job.TotalPayout`). The stored order never has `TotalPayout` set (the game fills it in when
  the car is handed over), so the amount was 0. `Payouts.CompleteEnd` treats amount 0 as "nothing to pay" and returns
  `Applied` without touching the wallet, so the order completed and nobody was paid. The host's own hand-over (2106) too.
- The client skips the game's own payout (`GameScript.EndJob` is replaced), so there was no other source of money.

## Approach
- `Payouts` remembers the amount the host approved on the ask (already range-checked) and `CompleteEnd` grants that
  amount, instead of re-deriving it from the stored order.
- `ServerHandle.EndJobPacket` stops reading the stored order for the amount; it logs the paid amount, and warns when a
  non-sandbox hand-over pays 0.
- Rejected: using the stored order when it is non-zero (it is the unfinished order, so it can undercut the real payout).

## Files
`Shared/Economy/Payouts.cs`, `ServerSide/Data/ServerHandle.cs`, `Tests/PayoutsTests.cs`, `MainMod.cs`.

## Testing
- Unit: approved amount is what gets granted; sandbox pays nothing.
- In game: hand over an order as host and as client; the wallet rises for both and the ledger shows a positive entry.
- Money already lost this session (2106 + 25 + 369 + 1428) is not restored by the fix.

## Status
Done in 0.5.0-rc40.
