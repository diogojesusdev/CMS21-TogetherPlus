# Windshield vanished from the inventory

## Goal
User: "right now something happened and we lost a windshield from the inventory please investigate". Detail: "I took
off the windshield, we tried submitting the car, got the warning, I tried putting back in and it no longer existed, so
i had to buy a new one" (session of 17:47-18:49, rc37, host #1 and friend #2).

## Findings
- The log has no inventory change that removes the windshield: the shared (host) inventory saw `invPush` only for
  item 89 (window_front taken off, 18:43:27), 90/91/92 (other panels) and the purchase of item 93 (18:46:02, the new
  windshield that was mounted at 18:46:10). No `[Locks] ... lost` for a window, no `[Items] ... undone`, no snapshot
  mismatch (`differ from the host's`), no `Applying the shared inventory failed`.
- Between 18:44:27 and 18:46:02 there was no inventory traffic at all, yet the player found no windshield. So the item
  was missing from the player's *game* inventory while the shared inventory (and its hash check) still had it.
- `Inventory.Reconcile` diffs the shared inventory against `gameItems` (what the mod *believes* the game holds), not
  against the game's real list. A game-side delete that the mod ignored (any change made while applying another
  player's state is ignored on purpose) leaves `gameItems` and the game different forever: nothing re-adds the item
  until the next load. That fits "no log line, item gone".
- The exact game call that deleted it cannot be proven from the logs.

## Approach
- Every 10 s (the existing check tick) compare the game's real item list with the shared inventory. A difference that
  is the same on two consecutive checks (so a purchase or mount in flight is never undone) is logged as a warning that
  names the items, then repaired: missing items are re-added, extra ones removed. The shared inventory stays the truth.
- Alternatives rejected: freezing the inventory during every remote body-part apply (unproven that the apply touches
  the inventory, and it could hide real changes); a full resync every tick (heavy, races with in-flight actions).

## Files
- `ClientSide/Data/Player/Inventory.cs`
- `MainMod.cs` (version)

## Testing
- `.\build.ps1 -Package` (all tests).
- In game: take a windshield off, wait; it must stay in the inventory. If it ever drifts the log shows
  `[Inventory] The game's items differ from the shared inventory ...`.

## Status
Done in 0.5.0-rc41. Root cause not proven; this is a self-healing safety net plus diagnostics.
