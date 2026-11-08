# The ABS module goes back on every time it is unscrewed

## Goal
User: "Now im having issues with an abs module. I bought a new one and am trying to remove the old one but its
strange" - the old module goes straight back on and the new one is taken out of the van.

## Findings
Same host log, car 3, `absModul_1`, key `car/3/part/2/11/0`. Both players hit it, every single attempt:

```
00:30:30.010 [Server] <- lockRequest from #1        <- the player starts unscrewing the module
00:30:36.014 [Server] <- lockRelease from #1        <- 6.004 s later: ClientLockTable.IdleMs
00:30:36.971 [Local] Sending part absModul_1 (other 11/0) car 3, unmounted=True
00:30:36.982 [Locks] #1 lost car/3/part/2/11/0: removing item 277 (absModul_1).
00:30:37.036 [Apply] Part absModul_1 (other 11/0) on car 3 car_ribbsanz530, unmounted=False.
```

and #3 at 00:26:36.180 -> release 00:26:42.189 (6.009 s) -> write 00:26:43.787 -> rejected. #1 retried four times
(items 275, 276, 277, 278 - a fresh module taken from the van and removed again each time).

This is the `2026-11-08-part-write-rejected-after-idle-release` bug: the write lands after the player's own claim
lapsed and is judged stale because somebody else wrote that key earlier. But the ABS module shows the second half of
it: **the unscrew animation takes about 7 s, which is longer than `ClientLockTable.IdleMs` (6 s)**, so the claim is
always gone before the part comes off. It is not a race that sometimes happens - for a slow part it happens every
time, and for the last second of the operation the part is unlocked, so the other player's gate lets them start on
the very part that is being removed (which is exactly what #1 and #3 were doing to each other).

The session in the log is running 0.5.0-rc46, so it has neither fix.

## Approach
On top of the rc50 server fix (a grant records its owner as the key's writer):

- `ActionGate` keeps the claim of the component the player just started working on alive while the game animates it:
  `TryBegin` on a component key arms a hold, `Client.Tick` refreshes that claim every frame, and the hold is dropped
  when the change is stamped for sending or after `HoldSeconds` (25 s) for an operation that never finishes. Idle
  release then means "the player walked away", not "the part is slow".
- A write the host refuses now says why (`RejectHeld` by whom, or `RejectStale`) instead of only naming the key, so
  the next report of a part putting itself back is one log line to diagnose.

Rejected: raising `IdleMs` globally (it would keep every resource claimed longer, including ones nobody is using);
rejected: hooking the game's animation state (no stable per-part signal, and the cap does the same job).

## Files
- `ClientSide/Data/Garage/ActionGate.cs`, `ClientSide/Client.cs`
- `Shared/Locks/LockServer.cs`, `ServerSide/Data/ServerHandle.cs`
- `Tests/LockTests.cs`
- `MainMod.cs` (version)

## Testing
- `.\build.ps1 -Package`.
- In game: two players on the same car. One unscrews the ABS module (about 7 s). It must come off and stay off, the
  new module must mount, and the old one must stay in the van. While it is being unscrewed the other player must get
  "In use by ..." on that module instead of being able to start on it.

## Status
Done in 0.5.0-rc51.
