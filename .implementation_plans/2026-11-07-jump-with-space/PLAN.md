# Jump with the space bar

## Goal
"Now add the capability of players to jump using the space bar".

## Findings
- The player is the game's `FPSInputController` + `CharacterMotor` (Unity standard assets, in `Assembly-CSharp-firstpass`).
  `CharacterMotor` already has `inputJump` and a `jumping` block (`enabled`, `baseHeight`, `extraHeight`).
- Other players already see the jump: `Movement.SendPosition` sends the full position including Y, and the avatar glides there.

## Approach
- Harmony postfix on `FPSInputController.Update` (a method of the class's own, plain void, no value types): while
  connected and the cursor is locked (no menu or window open), enable `motor.jumping` (default height if the game
  left it at 0) and set `motor.inputJump` from the space bar.
- Alternative rejected: a bare `OnUpdate` write, because the order against the game's own Update is undefined.
- Only while connected: single-player stays vanilla.

## Files
- `ClientSide/Data/Player/Jump.cs` (new), `MainMod.cs` (version).

## Testing
- No pure logic to unit test; the build passes all tests.
- In game: press space while walking (both players); the other player sees the hop. Space in menus does nothing.

## Status
Done in 0.5.0-rc45. Unverified in game: if the jump is too low/high or does nothing, tune `DefaultHeight` or check
the `[Jump] enabled` log line.
