# Late joiner cannot see cars / garage upgrades (resync storm)

**Goal:** "A 3rd player just joined and it can't see our cars, and the garage expansion they can also not see. They see the school bus instead of the new lifter."

**Findings:** Joiner log (#3): while the host refills brake fluid (a `carFluid` per frame), every fluid packet for a car not yet loaded was dropped with a Warn and `ClientSend.ResyncCar` was called per packet. The host answered each with the whole car (loadCar 33 KB, ~600 body parts, ~4000 part scripts, lifter): the host log shows `resync from #3 (+68 similar skipped)`, the joiner `loadCar (+14)` every 250 ms and thousands of `partScript`/`bodyPart` per 5 s. The main thread drowned in re-applying the same car, so the other cars and the garage upgrades (resync queued behind) never arrived.

**Approach:** throttle `ClientSend.ResyncCar` per car (3 s), drop duplicate per-player/per-car resync requests on the server (2 s), and make the "fluid for an unloaded car" drop a throttled breadcrumb instead of a Warn. Resync of all cars (-1) is untouched.

**Files:** `ClientSend.cs`, `ServerHandle.cs`, `PartsUpdater.cs`, `MainMod.cs`.

**Testing:** build + tests pass. In game: a 3rd player joins while a fluid is being refilled; all cars, the lifter and the expansion must show, and `resync from #N` must not repeat.

**Status:** done in 0.5.0-rc48. Not proven: only the first 20 s of the joiner log were available; follow up with the full log if cars/upgrades are still missing. Also seen: `GroupItem is null in ModGroupItem constructor` on join (not investigated).
