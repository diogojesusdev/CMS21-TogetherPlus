# A mounted part is taken back off: "lost car/.../part/..." after the player's own lock lapsed

## Goal
User: "i was trying to put a rear suspension arm and small bushings in a car. after putting them they immediately
disappeared. On the job it shows COMPLETE but when i try to submit it, it says some piece is missing"

## Findings
Host log `MelonLoader_26-11-07_23-52-35.362.log`, car 3 (`car_rinobianco`), part `wahaczTyl_2` = rear suspension arm,
lock key `car/3/part/1/5/7`. It happened twice, with the same shape:

```
00:17:54.626 [Server] <- lockRequest from #1          <- the player claims the arm and starts mounting it
00:18:00.630 [Server] <- lockRelease from #1          <- exactly 6.004 s later: ClientLockTable.IdleMs
00:18:01.977 [Local] Sending part wahaczTyl_2 (suspension 5/7) car 3, unmounted=False
00:18:01.991 [Locks] #1 lost car/3/part/1/5/7: returning item 264 (wahaczTyl_2).
00:18:02.044 [Apply] Part wahaczTyl_2 (suspension 5/7) on car 3 car_rinobianco, unmounted=True.   <- the arm vanishes
00:18:07.442 [Hook->EndJobHook] Car incomplete, missing part id 'wahaczTyl_2'
```

(the first one at 00:16:24 -> 00:16:30.424 release -> 00:16:32.160 send -> 00:16:32.226 `unmounted=True`).

1. The mount is only sent when `HandleDoMount` sees every bolt mounted, which is seconds after the claim. Meanwhile
   `ClientLockTable.Pump` releases a claim nobody touched for `IdleMs` (6 s), so the write goes out with the token of
   a request the client has already released (`TokenFor` still stamps a `Released` request for `RetainMs`).
2. On the host, `ResourceLockManager.CheckWrite` finds no grant for the key and falls through to
   `if (token != 0 && lastWriter[key].owner != owner) return RejectStale`. `lastWriter` had been set by **#2**, who
   had worked on that same arm minutes earlier (`[Apply] wahaczTyl_2` at 00:13:17, 00:14:26, 00:14:53), and is never
   cleared. So the rule meant for "someone else wrote *since* your lease ended" fired on a write by the player who
   had just legitimately held the lock.
3. The rejected write makes the host push its stored state back (`ShouldResync` -> `ServerSend.PartScriptPacket(...,
   true)`) and undo the inventory move, so the arm comes off again and the item returns to the van. The job task list
   still shows the work as done, but `EndJobHook` reads the actual car and refuses to submit it.

The bushings are the same story: they are separate `PartScript`s with their own keys, mounted in the same minute.

## Approach
A grant makes the grantee the recorded writer of that key: `ResourceLockManager.Acquire` calls `Record(key, owner,
token)` for every key it grants. A late write from the player who held the lock then matches `lastWriter.owner` and
is accepted, while the rule keeps doing its job - if somebody else took the key in the meantime they are the recorded
writer (and usually still hold it), so the late write is still rejected.

Also: a dropped part or body write is logged at Info (behind the existing one-per-key-per-second `ShouldResync`
gate), because today the part just silently disappears with nothing in the log to explain it.

Rejected: making the client stop stamping a released request (`RetainMs` exists so a write that crosses an idle
release is still recognised as that player's); rejected: keeping the claim alive for the whole of `HandleDoMount`
(it watches a part for up to 600 s, so a half-bolted part would hold the lock for ten minutes).

## Files
- `Shared/Locks/ResourceLockManager.cs`
- `ServerSide/Data/ServerHandle.cs`
- `Tests/LockTests.cs`
- `MainMod.cs` (version)

## Testing
- `.\build.ps1 -Package`, with new cases in `LockTests`: a late write after the writer's own idle release is accepted
  even when another player wrote that key before the claim; the existing "someone else wrote since" rejection still
  holds.
- In game: two players work on the same car; one mounts a suspension arm and its bushings slowly (more than 6 s
  between starting and the last bolt) on a part the other player had touched earlier. The part must stay on, the item
  must not come back to the van, and the job must submit.

## Status
Done in 0.5.0-rc50.
