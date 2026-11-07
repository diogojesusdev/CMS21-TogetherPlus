# Wheel size / offset not synced (wheels wrong after a friend rejoins)

## Goal
User: "My friend crashed and when he came back, the wheels were not correctly placed on the car. can you find the cause and fix".

## Findings
- Log (host, rc37): friend #2 dropped (Remote_Timeout) at 18:05:44 while the host had been working on car 4 (rims removed, tire changer,
  wheel balancer, rim mounted). He rejoined at 18:06:24; `[Cars] Player #2's car 4 differed from the host's; sent the host's (264 parts).`
- A car's wheel dimensions (width, rim size, tire profile, ET/offset) live in `CarLoader.WheelsData.Wheels[i]`, not in the parts.
  They travel only in the car's spawn data (`ModNewCarData.wheelsWidth/rimsSize/tiresSize/tiresET`), which the host stores ONCE when the car loads
  and never updates (`ServerResyncs.ResyncCar` resends that stale copy).
- `ModPartScript` carries no wheel data, and `PartsUpdater.UpdatePartScript` re-applies the RECEIVER's own `WheelsData` after a mount.
  `CarChecks.Hash` ignores wheel dimensions, so the periodic desync check can neither see nor repair them.
- Result: any wheel size/offset change made by one player (tire changer, new rims) never reaches the others, and a rejoining player
  gets the car with the dimensions it had when it first loaded, while the parts say the (different) wheels are mounted.
- Not confirmed from the log alone which dimension changed in this session (all rims were `rim_17`); this is the only wheel state path that was missing.

## Approach
New feature `WheelDimensions` on the existing feature channel (no new packet type, wire format unchanged):
- Each client watches the wheel dimensions of its loaded cars once a second. After a LOCAL change it sends a kept state `car/{id}/wheels`
  (width, rim size, tire size, ET per wheel). The host relays it and keeps the last one, and `SendCarStates` already gives a car's kept states to a
  player whose car is sent again (rejoin, scene return, resync), after the car and its parts.
- The receiver waits for the car to be ready (framework), then applies `SetWheelSize` / `SetET` / `SetWheelSizes` like `PartsUpdater` does,
  and remembers the result so it is not echoed back.
- Rejected: patching the game's mount/tire-changer methods (many shared-native-code / value-type pitfalls, see AGENTS.md); adding fields to
  `ModNewCarData` and refreshing the stored copy (the host has no authoritative live copy of what another player changed).

## Files
- `Shared/Features/FeatureCatalog.cs` (new switch, appended last)
- `ClientSide/Features/Visuals/Visuals.cs` (`WheelDimensionsFeature`, in an existing compiled file)
- `ClientSide/Features/FeatureList.cs`
- `Tests/FeatureTests.cs`
- `MainMod.cs` (version)

## Testing
- `.\build.ps1 -Package` (all tests).
- In game (host + friend): change a rim/tire size or offset on a car, friend sees the same; friend leaves and rejoins mid-work, car wheels match the host.

## Status
Done in 0.5.0-rc38. Follow-up: if wheels still look wrong after a rejoin, the rim/tire model ids (`Wheel.Rim/Tire`) are the next thing to check.
