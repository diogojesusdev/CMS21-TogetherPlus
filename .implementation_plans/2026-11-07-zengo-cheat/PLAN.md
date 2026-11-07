# "zengo" cheat: +500 money

## Goal
User: "add a cheat code where if any user is pressing the 5 keys "z" "e" "n" "g" "o" at the same time, it 500€ to the
account".

## Findings
- Money is host-authoritative: `EconomyServer.Grant(slot, amount)` credits the ledger and broadcasts the balance to
  every player. A client must never credit itself (the wallet would be reverted by the host).
- Per-frame input is polled from `MainMod.OnUpdate` (see `Recovery.Pump`). New packet types go at the end of
  `PacketTypes` and in the pinned table of `Tests/PacketTypesTests.cs`.

## Approach
- `ClientSide/Data/Cheats.cs`: every frame, when connected and the game is ready, detect Z+E+N+G+O all held. Fire once
  when the chord completes (rising edge), then require a 1 s pause. Sends the new `cheatMoney` packet (no amount: the
  host decides it) and shows a toast.
- Host handler `ServerHandle.CheatMoneyPacket`: `Economy.Grant(slot, 500)`, per-slot cooldown of 1 s, Info log line
  `[Cheat] #N used zengo: +500`.
- Applies to "any user": host and clients alike (the host is also a client through the loopback).
- Alternatives rejected: client-side only credit (reverted by the host); amount in the packet (any value could be
  claimed).

## Files
`ClientSide/Data/Cheats.cs` (new), `CMS21-Together.csproj`, `Shared/PacketTypes.cs`, `Tests/PacketTypesTests.cs`,
`ServerSide/Server.cs`, `ServerSide/Data/ServerHandle.cs`, `ClientSide/Data/Handle/ClientSend.cs`, `MainMod.cs`.

## Testing
- `.\build.ps1 -Package` (PacketTypes pin test covers the new type).
- In game: hold Z+E+N+G+O together; money goes up by 500 for both players and a toast shows. Holding longer does not
  repeat; releasing and pressing again after 1 s does.

## Status
Done in 0.5.0-rc42.
