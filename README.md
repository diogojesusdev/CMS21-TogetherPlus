# CMS21 Together+

Multiplayer for **Car Mechanic Simulator 2021**: up to 8 friends in one garage, sharing cars, parts, money and
progress. Together+ is a free, non-commercial, modified version of
[CMS21-Together](https://github.com/Fozkais/CMS21-Together) by Fozkais, with parts of
[FixForTogether](https://github.com/Stevii001100/Fix-For-CMS21-Together) by TogetherFixer (see [Credits](#credits-and-licences)).

> **0.5.0 is a release candidate.** Every automated test and network soak passes, but it has not been play-tested in
> the real game yet. Keep a copy of your saves, and please report problems on the
> [issues page](https://github.com/diogojesusdev/CMS21-TogetherPlus/issues).

## What it is

- Up to **8 players** (Steam or Direct IP), with late join and rejoin.
- **Shared money**: one wallet, kept by the host. Purchases are charged once; a purchase the wallet can't pay is undone.
- **Shared inventory and warehouse**: everyone sees the same items; an item is never duplicated or lost.
- **XP, level and skills are per player**, saved in the host's save and restored when you rejoin.
- **One player per part, tool and station**: if someone is using it, you get an "in use" message instead of a broken car.
- **Saves live on the host**, with autosave every 5 minutes and backups. Every player keeps a copy of the host's save,
  so a lost save can be restored from a friend.
- The Together Fixes features (stations, minigames, visuals, parking, bulk sell, Steam invites…), each with an on/off
  switch.

## Install

Everyone who plays needs the mod, in the same version.

**Easiest:** open PowerShell and run:

```powershell
irm https://raw.githubusercontent.com/diogojesusdev/CMS21-TogetherPlus/main/install.ps1 | iex
```

**Or:** download `TogetherPlus-<version>.zip` from the [latest release](https://github.com/diogojesusdev/CMS21-TogetherPlus/releases),
unzip it and double-click **`Install.bat`**.

The installer finds the game through Steam, installs MelonLoader 0.5.7 if needed (official download, checked),
copies the mod and moves an older Together or Together Fixes DLL to `Mods\_disabled`. Run it again to update. It never
touches your saves. To remove the mod, run `uninstall.ps1`.

**Manual install:**
1. Install [MelonLoader 0.5.7](https://github.com/LavaGang/MelonLoader/releases/tag/v0.5.7) (x64) into the game folder.
2. From the zip, copy `Mods\CMS21-Together.dll` into `<game>\Mods` and `UserLibs\Facepunch.Steamworks.Win64.dll`
   into `<game>\UserLibs`.
3. Copy `<game>\Car Mechanic Simulator 2021_Data\Plugins\x86_64\steam_api64.dll` into `<game>\UserLibs`.
4. Start the game. **The first launch takes a few minutes** while MelonLoader prepares the game.

## Play with friends

In the main menu, open the multiplayer button.

**Over Steam (recommended, no port forwarding):**
1. The host picks **Steam**, chooses a save slot and opens the lobby.
2. Friends join from the Steam friends list (**Join game**, or accept an invite sent with **Invite via Steam**), or
   with the lobby code the host copies with **Copy server ID** in the lobby.
3. Everyone clicks **Ready Up**; the host clicks **Start Game**. Friends can also join a game that is already running.

**Over Direct IP (TCP):**
1. The host picks **TCP** and opens the lobby. The host must let in **TCP port 7777**: forward it on the router to
   the host's PC (and allow it in Windows Firewall), or play on the same LAN or VPN.
2. Friends pick TCP and enter the host's IP address.

The host's save is the game everyone plays. Each player's own saves are not used or changed.

## How it works

- **The host decides.** Money, inventory, cars and orders are checked by the host, so two players can't buy, sell or
  hand over the same thing twice. Spending is instant on your screen; if the host refuses it (not enough money), it is
  undone with a message.
- **Locks.** Working on a part, panel, lifter or station locks it for a moment; others see "in use".
- **Saves.** The host saves every 5 minutes, when leaving and when quitting, with backups. After each save every
  player receives a copy (the last 3 are kept). Saves and copies are in
  `%USERPROFILE%\AppData\LocalLow\Red Dot Games\Car Mechanic Simulator 2021\TogetherPlus\`.
- **Recovery keys:** `Ctrl+Shift+R` resyncs with the host, `Ctrl+Shift+L` writes diagnostics to the log, `F4` resets a
  stuck lifter.
- **Settings:** feature switches are in `<game>\UserData\MelonPreferences.cfg` under `[TogetherPlus]` (the host's
  switches apply to everyone). The player limit is `"MaxPlayers"` (2 to 8) in `<game>\UserData\TogetherPlus.json`.

## Troubleshooting

- **Logs:** `<game>\MelonLoader\Latest.log`. Attach it (and the host's) to bug reports, after pressing `Ctrl+Shift+L`.
- **Black screen or long first start:** the first launch after installing MelonLoader takes several minutes. Wait
  until the console window shows the game started. Check that MelonLoader is version 0.5.7.
- **Friends can't connect over Direct IP:** check the port forward of TCP 7777 and Windows Firewall, or use Steam.
- **"Incompatible Together+ version" when joining:** everyone must run the installer again to get the same version.
- **The host lost or broke its save:** the host chooses the save slot and **Host anyway**; when a player who has a copy
  joins, the host is asked whether to restore it (the damaged files are kept as a backup). A copy can also be loaded
  into an empty slot with **Restore a backup**.

## Building from source

Needs Visual Studio 2022 (or Build Tools) with the .NET desktop workload, the .NET SDK, and the game with MelonLoader
0.5.7 started once (the build uses the game's `MelonLoader\Managed` assemblies; none are included here).

```powershell
.\build.ps1                     # build (game found through Steam) and run the tests
.\build.ps1 -GamePath 'E:\Games\Steam\steamapps\common\Car Mechanic Simulator 2021'
.\build.ps1 -Package            # also dist\TogetherPlus-<version>.zip
```

The design and test notes are in [docs/IMPLEMENTATION_PLAN.md](docs/IMPLEMENTATION_PLAN.md).

## Credits and licences

- [CMS21-Together](https://github.com/Fozkais/CMS21-Together) by **Fozkais** (MIT): the base of this project, from
  commit `52bacb8` (tag `Release-0_4_16hf3`).
- [FixForTogether](https://github.com/Stevii001100/Fix-For-CMS21-Together) by **TogetherFixer**: fixes and features,
  ported and modified (each ported file says so). Its licence is **non-commercial**, so **Together+ as a whole is
  free and must not be sold or distributed commercially.**
- [CMS2021_Enhanced](https://github.com/bellaouzo/CMS2021_Enhanced) by bellaouzo (MIT): selected features.
- Bundled libraries: HarmonyX, Newtonsoft.Json, Facepunch.Steamworks (MIT).

Full texts are in [NOTICE.md](NOTICE.md) and [LICENSES/](LICENSES/). Car Mechanic Simulator 2021 belongs to Red Dot
Games and PlayWay; this project is not affiliated with them or endorsed by them.
