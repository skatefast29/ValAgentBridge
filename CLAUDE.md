# ValBridge

BepInEx mod that lets Claude drive a running Valheim through GABS (MCP `gabs` in `.mcp.json`). Fork of
jneb802/ValBridgeServer (MIT), rebuilt for Valheim 1.0. Tools: `TOOLS.md`. History: `CHANGELOG.md`.
Machine-specific notes (profiles, other projects, the user's preferences) go in `CLAUDE.local.md`, which is not committed.

## Environment
- Valheim 1.0.16, Unity 6000.0.75 on **Mono**, BepInEx 5.4.23.5, HarmonyX 2.9.
- Game: `C:\Program Files (x86)\Steam\steamapps\common\Valheim` (DLLs in `valheim_Data\Managed`).
- Mod manager **Gale**. The bridge goes in a Gale test profile (`-p:GaleProfile=<profile dir>`, log
  `BepInEx\LogOutput.log`); remove it again with `-t:Undeploy`. Never install it in a profile used for normal play.
  Started normally (not by GABS) it does nothing, so it doesn't skew measurements.
- GABS: `tools\gabs\` (gitignored download from github.com/pardeike/GABS/releases), registered as MCP server `gabs`.
  Game config `%USERPROFILE%\.gabs\config.json`: game id `valheim`, with launch profiles pointing at Gale profiles.
  Steam must be running.
- Tools: .NET SDK 10; `ilspycmd` and `ilspy-mcp` (dotnet global tools) for reading game code.

## Build, deploy, test
- **The game must be closed before deploying** (DLL locked). Check: `Get-Process valheim`.
- `dotnet build -c Release -p:Deploy=true` builds and copies into the default profile **and every other Gale profile
  that already has the bridge** (the build lists where it went; a locked DLL means a game is running from one of
  them). `-p:GaleProfile=<profile dir>` adds a new profile. Compile references come from any profile that has BepInEx.
- Bump the plugin's `ModVersion` (`Src\Plugin.cs`) and `Properties\AssemblyInfo.cs` together; add a CHANGELOG entry.
- Test in game: `games_start valheim` (optionally `profile`). In a heavily modded profile the bridge only comes up
  ~40 s after launch, so `games_start` answers `started_bridge_pending`: then call `games_connect` with `timeout` 120.
  Then at once `world/enter` (GABS `timeout` 300, 700 with `create`; `character`/`world`/`create` for a specific or
  new one): it waits for the menu, loads and returns once in the world. Then `test/run_file` (e.g. `bridge-1.4`,
  `bridge-1.3`, `smoke`). Read `logs/read` or `LogOutput.log` afterwards.
- Unsure of a tool's name or arguments: `games_tool_names` (`brief`) / `games_tool_detail`, don't guess. A wrong
  argument name gets an error listing the right ones.
- Something seems stuck: `world/status` (`frameAgeMs`, `secondsInPhase`, `popup`, `hint`). Hints come after no frame
  for 5 s in a world, 45 s loading, 3 min generating a new world; short freezes while loading are never a failure.
- Never wait on the game by sleeping or grepping `LogOutput.log` for a guessed line: the user sits idle while it
  times out. Wait inside the game (`waitUntil` in a test script) or poll `world/status`.
  The user doesn't need to load the world by hand; say so when starting the game.
- Ask before starting, stopping or moving the user's character in their game; restore anything a test changes
  (health, items, settings). In a test script, `saveAs` the value first and set it back from `${...}` in the same
  script (health is absolute: max HP with food can be 250+, not 25). Afterwards re-read inventory and backpack.
- Creatures attacking the character spoil tests: clear them first (`world/clear_hostiles`, or `"clearHostiles": 50`
  in a test script; `"keepClear"` for long runs that aren't performance measurements), not with god/ghost mode,
  which changes how the game behaves and can skew results. Use `player/set_protection` only when the user asks,
  and set it back afterwards (the answer gives `before`).
- A test that spawns creatures removes them again (`world/remove_objects` with the spawned instanceIds); one that
  pins time or weather releases it (`env/set_time` hour -1, `env/set_weather` empty). Teleporting moves the user's
  character: ask first and teleport back afterwards.
- Before reporting a test leftover (an item still on the ground or in the bag), check `events/read` and the log:
  the user may have moved it themselves while the test ran.
- Commit after a change is tested in game, then push.

## Reading game code
- `reference\` is gitignored and local: `decompiled\assembly_valheim`, `decompiled\assembly_utils` (refresh with
  `powershell -ExecutionPolicy Bypass -File tools\decompile.ps1`, `-Mods Name1,Name2` for mods),
  `Lib.GAB` (GABP server source, the library in `lib\`), `ValBridgeServer` (upstream).
- Never copy decompiled game code into the project or publish it.
- For a single method, the `ilspy` MCP tools are quicker. Ask for one type or method, never a whole assembly.

## Rules
- Compile against the **real** game DLLs: a private member must be a compile error, then reached via `AccessTools`.
- Verify every game/mod member name against decompiled code. Never guess.
- Code that reaches into other mods (backpack mods, `[BridgeTool]` discovery) uses reflection and stands down with
  one log line if the target changed.
- Use Unity's null check (`obj == null`), not `?.`/`??`, on Unity objects.
- Tool handlers run on a network thread: anything touching Unity goes through `MainThreadDispatcher.RunTool`
  (errors and timeouts become `{ success: false, error }`). Never block the main thread waiting on itself.
- Tool names are canonical GABP names (`area/verb_thing`); Lib.GAB refuses anything else and the bridge won't start.
- Cheats: say so in the tool description, log every live change, and never mark the character as cheated
  (`console/run_command` with cheats) without the user asking.
- No personal details (names, emails, local user paths) in committed files; author name is `BarryWhite`.
- For multi-line edit scripts, write the script file with the Write tool and run it.

## Traps
- Mono inlines small methods (IL under about 20 bytes): a Harmony patch or `debug/watch_start` on one may not fire.
- Lib.GAB 1.0.5 needs canonical tool names (see above); a bad name stops the whole server at startup.
- `console/run_command`: cheat commands need devcommands; the game's `TryRunCommand` silently does nothing otherwise.
- Adventure Backpacks keeps backpack contents in a separate inventory (`BackpackCompat`).
- Player-dropped items have auto-pickup off; items on the ground otherwise get auto-picked when walking past.
  Picked-up items can land in the backpack instead of the main inventory; check both.
- `debug/watch_*` on a busy method records a lot: keep the record count small and stop the watch when done.
