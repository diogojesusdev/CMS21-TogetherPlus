# Test hardening: contract guards, uncovered codecs, source invariants, decoder fuzz

**Status:** in progress

## Goal

The request: "Do you think this mod would benefit from having even more tests? would that help catch bugs initially
as well as avoid regressions?" — followed by "just implement all batches sequentially".

The suite is already large (2,589 cases, ~42 s). This is not a coverage push for its own sake: it adds the tests
that fail cheaply when a **documented rule** is broken, plus the codecs that today have no direct test, plus a
source scan for the IL2CPP rules that unit tests cannot execute.

## Findings

- The test project (`Tests\Tests.csproj`) links only `Shared\**` subsets plus the bot harness. `ClientSide\` and
  `ServerSide\` (~570 KB) are not compiled into it, so most findings in `docs/BUG_AUDIT.md` were never testable
  as written. A regression test per *extracted* rule is the pattern already used (`WalletRules`, `Payouts`,
  `BulkSale`, `RecoveryRules`, `ParkSlots`, `RaceReconciler`).
- `PacketTypes` has one weak guard (`GameIds_StayBelowTransportFrames`) but nothing pins the **ordinal** of each
  value, while `AGENTS.md` says new types are appended and never reordered. Reordering silently breaks the wire
  between two builds.
- `PacketRouter.Quiet` (the per-frame log suppression list) is private and untested.
- `NetProtocol` (handshake codecs, `StallClock`, `KeyEquals`) has one indirect reference; `ReplicaWire` and
  `PlusPaths` have none.
- The IL2CPP rule "never write an `Il2Cpp*Array` through its indexer" is only enforced by review. A scan shows
  the only such write in the tree is inside `Shared\Il2CppArrays.cs` itself, so the rule is guardable.
- The mismatch wording contract (`NetHost` -> `VersionText.HostVersionIn`) is guarded on the parser side only,
  not on the producer side, although `AGENTS.md` calls it part of the auto-update contract.
- `PurchaseLinker` has one test; `Reset`, exception handling in undo, and `Processed` boundaries are untested.

## Approach

Four batches, each landed, versioned and released on its own (per `AGENTS.md`):

1. **Protocol contract guards** — a frozen ordinal table for every `PacketTypes` value; `PacketRouter.IsQuiet` as a
   public accessor used by `Dispatch`, with a test pinning the documented per-frame set and a behavioural check
   that quiet types emit no Info breadcrumb; release-catalog asset edge cases.
2. **Untested codecs** — new fixtures for `NetProtocol` (hello/welcome/reason/timestamp/`KeyEquals`/`StallClock`),
   `ReplicaWire` (every layout round-trip + every rejection), `PurchaseLinker` gaps, `PlusPaths`.
3. **Source invariants** — a test that locates the repo root and asserts: no `Il2Cpp*Array` indexer writes outside
   `Il2CppArrays.cs`; no `MelonLogger` outside the log facade; the installer scripts stay BOM-free ASCII (a shipped
   bug in rc1); the handler-mismatch sentence in `ServerSide` source still parses with `VersionText.HostVersionIn`.
4. **Wire decoder fuzz** — every `Read*` decoder in `Shared\Net`, `Shared\Locks`, `Shared\Economy`,
   `Shared\Persistence`, `Shared\Features` and `Shared\World` is fed empty, truncated and random payloads; it must
   either parse or throw `NetFormatException` (what `PacketRouter` expects). Any other exception is fixed in the
   decoder, not worked around in the test.

Alternatives rejected: adding `ClientSide`/`ServerSide` to the test project (they need IL2CPP/MelonLoader types);
fixing the audited bugs themselves (out of scope for a test pass, and each deserves its own plan); a blanket
coverage target (it would spend the budget on serializers instead of rules).

## Files

- `Tests\PacketTypesTests.cs` (new)
- `Tests\NetProtocolTests.cs` (new)
- `Tests\ReplicaWireTests.cs` (new)
- `Tests\PlusPathsTests.cs` (new)
- `Tests\SourceInvariantTests.cs` (new)
- `Tests\WireFuzzTests.cs` (new)
- `Tests\PacketRouterTests.cs`, `Tests\ReleaseCatalogTests.cs`, `Tests\PurchaseRollbackTests.cs` (extended)
- `Shared\Net\PacketRouter.cs` (`IsQuiet` accessor)
- `MainMod.cs` (version bump per batch)
- `dist\rc19-notes.md` ... `dist\rc22-notes.md` (release notes)
- decoder fixes in `Shared\**` only if batch 4 finds a decoder throwing the wrong exception type

## Testing

- `dotnet test Tests\Tests.csproj -c Release` after every batch (all green before the version bump is committed).
- `.\build.ps1 -Package` per batch, with every release keeping the five assets and the `.sha256` matching.
- In game: nothing changes for the player, so only the startup check (mod loads, version shows the new rc) applies.

## Status

In progress. Batch results and deviations are recorded at the end of this file.
## Batch log

- **Batch 1 (rc19):** pinned `PacketTypes` ordinals (new `PacketTypesTests`), added `PacketRouter.IsQuiet` and a
  behavioural breadcrumb test, covered release-catalog assets that are incomplete or from another version.
  2,597 tests green.
- **Batch 2 (rc20):** new `NetProtocolTests` (hello/welcome/reason/timestamp/key comparison/stall clock),
  `ReplicaWireTests` (every layout and every rejection), `PlusPathsTests`, and the `PurchaseLinker` gaps
  (`Reset`, ignored transactions, one undo failing). 2,676 tests green.
- **Batch 3 (rc21):** new `SourceInvariantTests`: IL2CPP indexer writes, direct `MelonLogger` use, installer scripts
  BOM/ASCII, installer still ships both plugins, build still writes the `.sha256`, the `NetHost` rejection still
  parses through `VersionText.HostVersionIn`, and `PacketTypes` has no explicit numbers. 2,683 tests green.
- **Batch 4 (rc22):** new `WireFuzzTests` over all 25 wire decoders (handshake, replica, locks, economy, purchases,
  inventory, feature messages) with empty, constant and 128 random payloads each; every decoder must parse or throw
  `NetFormatException`. No decoder needed a fix. 2,709 tests green.
