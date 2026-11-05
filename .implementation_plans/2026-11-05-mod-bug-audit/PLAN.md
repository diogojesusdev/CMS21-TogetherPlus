# Audit: potential mod bugs in the most common gameplay features

**Status:** done (report written on 2026-11-05, build 0.5.0-rc14 / `30413e8`). No code changes in this change.

## Goal

The request: "go through the mod code and try to find potential bugs (mod-related only) in the most common
features of the game. identify them and write them to an md file on the repo".

Scope: only defects caused by the Together+ code (not vanilla-game bugs, not the already-fixed items listed in
`docs/IMPLEMENTATION_PLAN.md` §2 and §5.1). Focus on the features every session uses: order hand-over and job cars,
mounting/unmounting parts, the welder, fluids/wash/paint, car load/resync/parking, engine stand/crane, the shared
inventory and economy, and the host/join/lobby flow.

## Findings

Collected in `docs/BUG_AUDIT.md`, ranked by severity and grouped by feature. The two most serious clusters are:

1. **Handing over an order leaves the car behind in every game's `loadedCars`, and in the host's `CarSpawnDatas`.**
   The delete that cleans up the customer car runs with echo suppression on, and suppression returns before the
   `loadedCars.Remove`. A later job car on the same loader is therefore never sent (`CarSpawnManager.LoadJobCar`
   early-returns), and a late joiner is re-sent the handed-over car by `ResyncCar`.
2. **The welder packet always throws on the host.** `ServerData.SetWelder` dereferences `GetCarPart(loaderID, "body")`
   and `"details"` without a null check; the host's `BodyPartsReferences` only contains panels a client actually
   changed, so `body` is absent for a normal car. `PacketRouter` catches the exception, so the weld is never relayed
   and the error count moves toward the 50-error peer kick.

## Approach

Read-only audit. I read the client hooks/handlers, the server handlers, the shared locks/economy/persistence helpers
and the feature classes, then ran seven parallel sub-audits (cars, parts, tools/stations, economy/inventory, jobs/
upgrades, net/locks, player/UI). Every reported item was re-read in the working tree and is quoted with its current
line numbers. Vanilla-game behaviour is inferred from call sites, upstream history (`git log`/`git show`) and the
Il2Cpp interop stubs; items that depend on unverified vanilla behaviour carry a confidence note.

Alternatives rejected: fixing the bugs in this change (the request was to identify them and write the report), and
filing them as GitHub issues (the user asked for an md file in the repo).

## Files

- `docs/BUG_AUDIT.md` (new; the report)
- `.implementation_plans/2026-11-05-mod-bug-audit/PLAN.md` (this file)

## Testing

No code changed. The report lists the in-game reproduction for each finding and a suggested fix order; the existing
unit tests and bot harness cannot cover these paths (they need the real game).

## Follow-ups

- Fix the high/medium items in the suggested order, each with a regression check and a plan folder of its own.
- Play-test the two high clusters on two clients (hand over an order, then accept another on the same loader; weld a
  body with two clients).
- Treat the "hardening" section (host trusting client prices/UIDs/handler indexers) as a separate security pass.