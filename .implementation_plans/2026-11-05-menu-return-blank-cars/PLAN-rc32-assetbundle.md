# Menu return: the mod's own AssetBundle is the serialized file Unity dies on

**Status:** done in 0.5.0-rc32.

## Goal

> "crashed again!"

## Findings

### The rc31 breadcrumbs did their job

`MelonLoader_26-11-05_21-15-50.459.log`:

```
[21:17:03.020] [Shutdown] Leaving to the menu ...; the mod stops touching the game.
[21:17:03.709] [Server] Server closed: The host returned to the menu.
[21:17:03.734] [Shutdown] Released the mod's references to the garage.
```

The crash is at **21:17:08** - 4.3 seconds *after* the release finished. So rc31's theory is **disproved**: the mod had
already let go of the cars, `GameData` and the avatars, collected, and the game still died. Held references are not
the cause.

### What is left

The crash line is the same as in all four reports: `Unloading 4 Unused Serialized files (Serialized files now
loaded: 3)` then `Crash!!!`. A *serialized file* is an asset bundle. The mod loads exactly one thing of that kind:

```csharp
var playerBundle = AssetBundle.LoadFromStream(... "CMS21Together.Assets.player.assets" ...);
var player       = playerBundle.LoadAsset<GameObject>("playerModel");
var playerInstance = Object.Instantiate(player);
... LoadAsset<Texture>("tex_base") / ("tex_normal") into a new Material ...
playerPrefab = playerInstance;
Object.DontDestroyOnLoad(playerInstance);
playerBundle.Unload(false);
```

Three things line up:

- `DontDestroyOnLoad` means the avatar template **survives the scene change on purpose**, so it is still alive while
  Unity unloads the garage.
- `Unload(false)` releases the bundle but keeps its loaded assets, so the mesh, textures and material still belong to
  a serialized file that Unity is now free to unload.
- The crash only ever happens on the way back **from a garage**. The menu load at game start, before any bundle is
  loaded, never crashes - and the same unload line passes harmlessly there.

The game's own `Player.log` even shows the bundle loading, with broken scripts inside it:
`The referenced script (PlayerAnimController) on this Behaviour is missing! UnityEngine.AssetBundle:LoadAsset(String)`.

So Unity frees the serialized file while the mod's objects still point into it.

### A bug rc31 introduced

`ReleasePlayers` called `UserData.DestroyPlayer()` for every entry in `connectedClients`. For the **local** player
`userObject` is not an avatar at all - `SpawnPlayer` sets `userObject = GameData.Instance.localPlayer`, the game's
own player object. rc31 therefore destroyed the game's player on the way out. Fixed here.

rc31 also set `playerPrefab = null` without destroying it, which leaked the `DontDestroyOnLoad` instance and would
have made the next garage load read the same bundle a second time.

## Approach

1. **Destroy everything that came out of the bundle, before Unity unloads it.** `Shutdown.ReleasePlayers` now
   destroys the avatars and the `DontDestroyOnLoad` avatar template with `DestroyImmediate` - immediately, because a
   deferred `Destroy` would run after the unload has already started - and then clears the field so the next garage
   load builds it again.
2. `Resources.UnloadUnusedAssets()` is called at the end of the release, so the bundle's textures, material and mesh
   (which destroying a GameObject does not free) are gone before Unity's own unload runs.
3. **Never destroy the game's player.** `UserData.DestroyPlayer`/`DestroyPlayerImmediate` check `IsModAvatar()` first
   and only remove an object the mod instantiated.
4. The load path is deliberately left alone: keeping the bundle and using `Unload(true)` instead would also work, but
   it changes how avatars load mid-session, and the evidence does not require it.
5. Alternatives rejected: releasing references only (rc31 - disproved by its own log); leaving the template alive
   because `DontDestroyOnLoad` "should" be safe (it is the thing still pointing into the freed file);
   `Object.Destroy` instead of `DestroyImmediate` (runs too late).

## Files

- `Shared/Shutdown.cs` (`ReleasePlayers`, `ReleaseGameReferences`)
- `Shared/Data/UserData.cs` (`DestroyPlayer`, `DestroyPlayerImmediate`, `IsModAvatar`)

## Testing

- `.\build.ps1 -Package` - all unit tests pass.
- In game, the exact case that crashed: host, accept a car order, go back to the main menu. The log must show
  `[Shutdown] Released the mod's references to the garage, including the avatar template.` and the menu must load.
- Then **start a session again** from the same launch: other players' avatars must still appear, which proves the
  template was rebuilt rather than lost.
- Worth repeating three or four times in one launch, since this is about what survives between scenes.

## Follow-ups

- If it still crashes here, the next thing to try is keeping the bundle and calling `Unload(true)` at shutdown, which
  frees its assets outright instead of relying on `UnloadUnusedAssets`.
