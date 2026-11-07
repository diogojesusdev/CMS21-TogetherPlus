# Empty machine slots log "GroupItem is null" errors

## Goal
User screenshot after loading the garage: `[ERROR] Error: GroupItem is null in ModGroupItem constructor.` (game did not crash).

## Findings
- `ModNewMachines` (built from `ModProfileData` on every profile snapshot) wraps `GroupOnWheelBalancer`, `GroupOnTireChanger`,
  `GroupOnSpringClamp`, `GroupOnEngineStand` and `ItemOnBatteryCharger` unguarded. An empty machine has null slots, so
  `ModGroupItem(GroupItem)` / `ModItem(Item)` log an Error each time.
- Present in rc35 and rc36 logs; unrelated to the driving crash (rc36 fix). Harmless noise, but it hides real errors.
- The null path already yields a default-constructed Mod object (empty `ItemList`), and `ToGame()` turns that into an empty game group.

## Approach
Construct the default Mod object directly in `ModNewMachines` when the slot is null, so state is identical but nothing is logged.
The constructors keep their Error for genuinely unexpected nulls elsewhere. Rejected: downgrading the constructors' log level
(would hide real problems from other callers).

## Files
- `Shared/Data/Vanilla/GarageTool/ModNewMachines.cs`
- `MainMod.cs` (version)

## Testing
- `.\build.ps1 -Package` (all tests).
- In game: load the garage with empty machines; no "GroupItem is null" / "Item is null" error.

## Status
Done in 0.5.0-rc37
