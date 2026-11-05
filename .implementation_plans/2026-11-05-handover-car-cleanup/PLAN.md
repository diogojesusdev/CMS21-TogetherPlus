# Hand-over leaves the car behind (JB-1..JB-4)

**Status:** done in 0.5.0-rc15

## Goal
Fix the order hand-over / job-car findings of `docs/BUG_AUDIT.md` §1: the handed-over car stays in every game's
`loadedCars` and in the host's `CarSpawnDatas`, accepted orders are not recorded in `ServerData.selectedJobs`, and the
server's order list accumulates duplicates.

## Findings
- `JobManager.cs:110-111` and `JobHooks.cs:242-250` delete the car with echo suppression; the suppression returns
  before `loadedCars.Remove` (`CarSpawnHooks.cs:83-87`, removal at `:103`), and `DeleteCar(true)` is an unpatched
  overload, so the entry never goes away. `CarSpawnManager.LoadJobCar` (`:46`) then refuses the next car on that loader.
- `ServerHandle.EndJobPacket` never calls `ServerData.DeleteCar`, so `ResyncCar` re-sends the dead customer car.
- `ServerHandle.JobActionPacket` never calls `UpdateSelectedJobs` for `takeJob == true`.
- `JobHooks.LoadHook` re-sends every order on each garage load; `ServerData.AddJob` / `JobManager.AddJob` append
  without de-duplicating. `JobHooks.GenerateNewJobHook` has no host guard.

## Approach
1. Remove the car from `loadedCars` before every suppressed delete, and guard the loader index.
2. In `ServerHandle.EndJobPacket`, delete the car from `ServerData` (and the lifter state) after the order.
3. Mirror `UpdateSelectedJobs(job, true)` in the server's `JobActionPacket`.
4. De-duplicate by order id in both `AddJob`s; add the host guard to `GenerateNewJobHook`; make `JobHooks.LoadHook`
   send ids the server does not already have (the dedupe makes that cheap and idempotent).

## Files
- `ClientSide/Data/Garage/Campaign/JobManager.cs`, `JobHooks.cs`
- `ServerSide/Data/ServerHandle.cs`, `ServerData.cs`

## Testing
- Unit tests: add/extend `Tests/` coverage for job de-duplication and `ServerData.EndJob` cleanup where the logic is
  headless (new `JobSyncTests`).
- Build + existing NUnit suite.
- In-game (two clients): accept and hand over an order, then accept another order that reuses the loader; the new
  car must appear for both. Join late after a hand-over: no phantom customer car.

## Deviations / follow-ups

- The job payout timing (report EC-3) was fixed in the same batch: `Payouts.Decide(JobEnd, ...)` now only approves for
  a bounded time, and `ServerHandle.EndJobPacket` grants the payout from the host's own order and marks it done.
- `PayoutsTests` and `FeatureTests` were updated for the new approval/completion split.
- In-game verification is still manual (one order handed over, then a second on the same loader; a late join after a
  hand-over).
