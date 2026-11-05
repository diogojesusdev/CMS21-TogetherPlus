# Agent instructions: Together+ (CMS21 multiplayer mod)

## MANDATORY: write an implementation plan before implementing

Before implementing **any** feature or fix, create a new folder under `.implementation_plans/` and write the plan there. No exceptions, even for small fixes.

- **Folder name:** `.implementation_plans/<yyyy-mm-dd>-<short-kebab-name>/`, e.g. `2026-11-05-hood-mount-crash/`.
- **Required file:** `PLAN.md` in that folder, containing:
  - **Goal:** the request or bug, quoting the user/issue where possible.
  - **Findings:** root cause or the relevant code (files, logs, evidence).
  - **Approach:** what will change and why, plus alternatives rejected.
  - **Files:** the files expected to change.
  - **Testing:** the tests to add or run, and the in-game checks for the player.
  - **Status:** planned / in progress / done (with version, e.g. `done in 0.5.0-rc9`), plus any follow-ups.
- **No approval needed:** go from plan to implementation straight away. The plan **must** still be written first, then kept up to date (set the status, note deviations) and committed with the change.
- **Ideas backlog:** ideas not being worked on yet go in `.implementation_plans/TODO.md`. When starting one, move it into its own plan folder and remove it from `TODO.md`.
- **Plan-only changes are not released:** when you only write or update a plan or `TODO.md` (no implementation), do **not** bump the version, commit, push, release or deploy. Leave the files uncommitted; they get committed together with the implementation that uses them.

## MANDATORY: version, commit and push after EVERY change

This is not optional. After **every** change or batch of changes, however small (code, logging, docs, build scripts). The only exception is plan-only changes in `.implementation_plans/` (see above):

1. **Bump the mod version** in `MainMod.cs`:
   `public const string PLUS_VERSION = "0.5.0-rcN";` → `rcN+1`. On a stable version (`0.5.1`), bump the patch number.
   - Keep the file's UTF-8 BOM. In PowerShell:
     `[IO.File]::WriteAllText('MainMod.cs', $text, (New-Object Text.UTF8Encoding $true))`.
   - `NET_VERSION` derives from `PLUS_VERSION`, so every bump means every player needs the new build. That is intended: two different DLLs must never connect to each other.
   - Never change `ASSEMBLY_MOD_VERSION`/`ASSEMBLY_HOTFIX_VERSION` (upstream Together version) unless told to.
2. **Build and test**: `.\build.ps1 -Package` (all tests must pass; it also writes `dist\TogetherPlus-<version>.zip`).
3. **Commit** with a clear message (what changed and why, version in the subject, e.g. `fix: hood mount crash (0.5.0-rc8)`), ending with:
   `Co-authored-by: Copilot App <223556219+Copilot@users.noreply.github.com>`
4. **Push** to `main` on `diogojesusdev/CMS21-TogetherPlus` (see the branch workflow below).
5. **Publish a GitHub pre-release** for the new version (`v<PLUS_VERSION>`) with the zip, `Install.bat`, `install.ps1` and `uninstall.ps1` as assets. In the notes, say "Every player needs rcN" and list the changes in player language.
6. **Deploy to the local game** when the maintainer plays on this machine: `.\build.ps1 -Deploy -SkipTests`. Then check that the hash of `<game>\Mods\CMS21-Together.dll` matches `bin\Release\CMS21-Together.dll`.

Do not finish a task with uncommitted, unpushed or unversioned work. If a push fails, fix it (see Troubleshooting) and retry. Do not leave it for the user.

## Branch workflow

- `origin` is the upstream project (Fozkais/CMS21-Together). **Never push to `origin`.**
- If the checkout has `plus/main` (private working branch) and `public/main` (mirror of GitHub `main`), use this sequence:
  1. Commit on `plus/main`.
  2. `git checkout public/main`, then `git cherry-pick <sha>`.
  3. Push: `git -c credential.helper= -c 'credential.helper=!gh auth git-credential' push https://github.com/diogojesusdev/CMS21-TogetherPlus.git public/main:main`.
  4. `git checkout plus/main`.
- The checkout may be owned by another user. Use `git -c safe.directory=* ...` if git complains.

## Project facts

- **Stack:**
  - Car Mechanic Simulator 2021 (Il2Cpp), MelonLoader **0.5.7**, Unhollower **0.4.18**, Harmony.
  - Target: .NET Framework 4.7.2.
- **Build:**
  - Use MSBuild from Visual Studio, which `build.ps1` finds via vswhere. `dotnet build` of the mod project fails with bogus missing-namespace errors.
  - The game's `MelonLoader\Managed` assemblies are compiled against. `build.ps1` finds the game via Steam, or takes `-GamePath`.
  - If a build fails with "file is locked by testhost (PID)", stop that PID and rebuild.
- **Tests:**
  - `Tests\` (NUnit) link pure .NET files from `Shared\`.
  - A new `Shared\` file that tests or `Tools\BotHarness` need must be added to `CMS21-Together.csproj`, `Tests\Tests.csproj` **and** `Tools\BotHarness\BotHarness.csproj`. Files under `Shared\Net\**` are globbed.
- **Logs:**
  - Location: `<game>\MelonLoader\Logs\Latest.log`, plus timestamped older logs.
  - Crash dumps are next to the game exe. MelonLoader may not flush on a native crash.
- **Saves:**
  - Game: `%USERPROFILE%\AppData\LocalLow\Red Dot Games\Car Mechanic Simulator 2021\Save`.
  - Mod sidecars: `<game>\TogetherPlus\Saves`.

## Architecture in one minute

- `ClientSide/`: the game-facing client.
  - `Client.cs` is the connection and packet router registration.
  - `ClientHandle`/`ClientSend` receive and send packets.
  - Harmony hooks in `Data/Garage/**` detect local actions and send them.
- `ServerSide/`: host logic, `ServerHandle`/`ServerSend`, authoritative state.
- `Shared/`: transport (`Net/`), locks (`Locks/`), economy, persistence. Mostly pure .NET and unit tested.
- **Packet dispatch:**
  - Every received packet goes through `Shared/Net/PacketRouter.Dispatch`.
  - New packet types are appended to the **end** of `Shared/PacketTypes.cs`. Never reorder it.
  - Register the handler in `Client.CreateRouter` / `Server`'s router.
- **Echo suppression:** game changes made while applying remote state run inside `ActionGate.Apply()`. Hooks must check `ActionGate.ApplyingServerState` (or the local `listen` flags) so they don't send the change back.
- **Locks:** `ActionGate.TryBegin(LockKeys.…)` claims a car, panel or tool before a local action is sent. Denials show "In use by X".

## IL2CPP pitfalls (these have caused real crashes)

- **Array writes need the GC write barrier.**
  - Never assign into an `Il2CppReferenceArray`/`Il2CppStringArray` with the indexer, and never implicitly convert a managed `T[]` into one.
  - Use `Shared/Il2CppArrays.Set(...)` / `Il2CppArrays.Of(...)`.
  - Without the barrier, the game's incremental GC frees live objects and the game crashes later in `Component.get_gameObject`.
- **Some game types are value types stored inline in arrays**: `NewCarData`, `CustomColor`, `SaveData`. `Il2CppArrays` handles them; don't hand-roll pointer writes.
- **Game `List<T>` can have `_items == null`.** `ToArray()` then throws `ArgumentNullException`. Check `_items` first (see `ModNewCarData.CopyList`).
- **Stored references to game objects can go stale.**
  - Compare by `Pointer` (`PartUpdateHooks.Same`), not the game's `==`.
  - Re-fetch after scene or car reloads.
- **Telling exception sources apart:** an exception message starting with `System.XxxException:` came from the game (Il2CppException). A plain managed message came from our code.
- **Coroutines:** use `MelonCoroutines.Start` for mod coroutines and `MainMod.StartCoroutine` for game `IEnumerator`s. Wait for `GameLoadHook.IsGameReady()` before touching the garage.

## Logging rules

- Use `Shared/Log.cs` (`Log.Info/Warn/Error/Debug`), never `MelonLogger` directly.
  - Use `Log.Error(msg, ex)` for full stack traces.
  - Users set the level in `UserData\MelonPreferences.cfg`, under `[TogetherPlus] LogLevel`.
- **Never log in per-frame paths:**
  - `OnUpdate`/`OnLateUpdate`, movement, rotation, car position, tool movement, engine sound.
  - Send queues and serializers.
  - Anything called every frame.
- Guard Debug/Trace messages with `Log.IsEnabled(LogLevel.Debug)` so their strings are never built.
- For bursty events, use `LogThrottle` (see `PartsUpdater.Breadcrumb`).
- Put an Info breadcrumb **before** risky native game calls (mount/unmount, car load/delete, engine crane/stand). The last line before a native crash then names the culprit.
- Add packet types that are sent every frame to `PacketRouter.Quiet`.

## Debugging crashes

1. Read the end of the newest log in `MelonLoader\Logs`. Check that the session timestamp matches the reported crash.
2. Check the mod version line in the log matches the expected rc. Players running different rcs can't connect.
3. Native crashes: get the faulting call from the crash dump or the log stack. Then look at the last `[Apply]`/`[Local]`/`<-` breadcrumbs.
4. Reproduce with the unit tests or the bot harness where possible (`.\build.ps1 -Soak`).

## Repo and community

- Issues and PRs are open to the public on `diogojesusdev/CMS21-TogetherPlus`.
- When fixing a reported issue:
  - Reference it in the commit (`Fixes #N`).
  - Comment on the issue with what was fixed and in which release.
  - Thank the reporter in the comment.
- Use `gh` for GitHub operations. If auth fails, clear `GH_TOKEN`/`GH_HOST` from the environment first, then retry.

## Troubleshooting

- **Push says "Invalid username or token":** `Remove-Item Env:GH_TOKEN, Env:GH_HOST -EA 0`, then push again with the credential helper shown above.
- **Mod doesn't load / no multiplayer menu:** look for exceptions in `OnSceneWasLoaded`/`SavesManager` in the log.
- **Tests crash with `TypeInitializationException`:** check static field initialisation order. Static fields initialise top to bottom.
