# Economy edge cases and UI

**Status:** done in 0.5.0-rc15

## Goal
Fix `docs/BUG_AUDIT.md` EC-1/EC-2/EC-5, UI-1/UI-2/UI-3/UI-5, H-2: group items are not host-settled in a bulk sale;
an away car with no linked debit is delivered free; a remote garage upgrade is written into the personal skill
profile; the host can start twice; the version check blocks the menu; the Kick button is disabled; the inventory
search uses `ToArray`/unordered results; handlers index `connectedClients` unchecked.

## Approach
1. Capture group removals in `Inventory.RemoveGroupItemHook` and settle a bulk sale against both authorities (item and
   group), with the right lock key.
2. `AwayCarQueue` takes a `requiresDebit` flag (host's campaign mode): with `txn <= 0` it waits, then drops/refunds
   instead of delivering the car. The host derives the flag from its own gamemode; no new client trust.
3. `SkillUpgradeHook` skips while `ActionGate.ApplyingServerState` and only sends for `UpgradeType.Points`.
4. Lobby: `StartGame` sets a launching flag (cleared on failure); the button path cannot start twice.
5. Version check: once per process, run the HTTP call on a `Task` and show the notice on the main thread; remember
   the Dev notice too.
6. Kick: pass the player id to `AddPlayer`, create the button with a listener for the host, disabled for self/others.
7. Inventory search: safe index copy and sorted results instead of `ToArray` + `ConcurrentBag`.
8. `ServerHandle` handlers: `TryGetValue` instead of `connectedClients[...]`.

## Files
- `ClientSide/Features/Inventory/BulkSellFeature.cs`, `ClientSide/Data/Player/Inventory.cs`
- `Shared/Features/AwayCars.cs`, `ClientSide/Features/Garage/SceneReturnFeature.cs`
- `ClientSide/Data/Garage/Campaign/SkillUpgradeHook.cs`
- `ClientSide/Data/NewUI/{UIActions,UILobby,UIElements,UICore}.cs`
- `ClientSide/Data/ContentManager.cs`
- `ServerSide/Data/ServerHandle.cs`

## Testing
- New `BulkSaleTests` case for a group-only sale; `AwayCars` tests for `requiresDebit`; `PayoutsTests` unchanged.
- Build + suite; in-game checks for double Start, Kick, version notice and group bulk sell.

## Deviations / follow-ups

- EC-3 (payout granted before the hand-over) was implemented here as well: the ask only approves for two minutes, the
  payout is granted at `EndJobPacket` from the host's stored order, and `done` is bounded so a failed hand-over can be
  retried.
- EC-4 (one debit per frame) and UI-4 (save-folder rescan on every menu return) were left as minor, as was H-1 (prices
  are still client-supplied; a modified client is needed to exploit it).
- Group bulk sell is settled against both the item and the group authority; `FeatureTests` covers the no-debit away
  car and `PayoutsTests` the approval/expiry path.
