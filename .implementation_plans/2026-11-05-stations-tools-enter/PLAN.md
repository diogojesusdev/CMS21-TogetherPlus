# Stations, crane, engine stand and car enter/exit

**Status:** done in 0.5.0-rc15

## Goal
Fix `docs/BUG_AUDIT.md` ES-1/ES-2/ES-3, ST-1, CE-1/CE-2, CR-3: engine stand 1 take-off is not sent; the crane
dereferences a missing engine and inserts into the local crane's car; stations are not re-sent to joiners; the
"in car" heuristic has a proximity fallback that hides players; the static enter/exit state is not reset; the parking
hold is keyed by space.

## Approach
1. Engine stand: send `TakeOffEnginePacket(useAlt)` for both stands before the vanilla take-off.
2. Crane pull: bounds/`loadedCars`/`GetEngine()` checks; crane insert sends the connected car id and the receiver
   uses it (falling back to its local crane only when -1).
3. Add a station resync (`SetSpringClamp`/`SetTireChanger`/`WheelBalance` to the requester) and store the wheel
   balancer state in `ServerHandle.WheelBalancePacket`.
4. Car enter/exit: drop the unconditional 1.2 m fallback, keep camera-parent + disabled-controller checks, and add a
   `CarEnterExit.Reset()` called on connect/disconnect and every garage load.
5. Parking: key the `ParkOut` hold by the car's UID instead of the space number.

## Files
- `ClientSide/Data/Garage/Tools/{EngineStand,EngineCrane}.cs`
- `ClientSide/Data/Handle/ClientHandle.cs`, `ClientSend.cs`
- `ServerSide/Data/{ServerHandle,ServerSend,ServerResyncs}.cs`
- `ClientSide/Data/Garage/GarageResync.cs`
- `ClientSide/Data/Player/CarEnterExit.cs`
- `ClientSide/Features/Garage/ParkingFeature.cs`

## Testing
- `dotnet`-free NUnit suite (station packet round-trips, parking key), build.
- In-game: put an engine on stand 1 and take it off with a second client watching; join late with a wheel on each
  station; walk next to a car and confirm the avatar is not hidden; park/take out a car twice in 10 s.

## Deviations / follow-ups

- Engine stand 1 take-off is announced for both stands now; this assumes the same pie-menu lambda handles stand 1
  (the `useAlt` branch is the evidence).
- The car-enter heuristic keeps the camera-parent check and the disabled-controller fallback; only the unconditional
  1.2 m proximity fallback was removed, because its trigger could not be verified without the game.
- ES-4 (the static `useAlt` read after a wait) was left as a minor.
