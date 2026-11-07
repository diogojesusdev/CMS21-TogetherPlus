# Fluids reverting / per-frame carFluid flood

**Goal:** "I handled all fluids on my car (drained + fill). But then it all went back to undone! ... when the new player joined he couldn't see one of the cars."

**Findings:** Latest log: players #2/#3 send `carFluid` on every frame (hundreds per 5 s) after joining, each stored in `ServerData` and relayed to everyone, so stale/default values from a joiner's copy can overwrite the host's. The `[Cars] ... differed from the host's` warnings are only part pushes (the hash has no fluids) and are not the cause. The missing car is possibly the same resync storm; not proven.

**Approach:** the server does not store or relay a fluid equal to the stored one (`ServerData.UpdateFluid` returns bool) and logs real changes (throttled `[Fluid]`); the client `SetLevelHook` only sends when level/condition changed from the last sent/applied value. Rejected: lock-gating fluids (lock coverage unknown).

**Files:** `ServerData.cs`, `ServerHandle.cs`, `PartUpdateHooks.cs`, `PartsUpdater.cs`, `Client.cs`, `MainMod.cs`.

**Testing:** build + tests pass. In game: two players join while the host fills fluids; levels must stay and `[Fluid]` lines show who changes what.

**Status:** done in 0.5.0-rc47 (root cause of the flood not proven; `[Fluid]` log will show if a joiner still sends changed values). Follow-up: missing car on join.
