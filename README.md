# ValBridge

A BepInEx mod that lets an AI agent (through the GABS MCP server) see and drive a running Valheim: get from game
start into a world in one call, read the player's state and inventory, move, fight, teleport, press keys, spawn
and clear creatures, set time of day and weather, take screenshots, read the log and on-screen messages, read and
change other mods' settings, inspect and watch live game and mod code, and run scripted tests with pass/fail
reports. It also reports when the game looks stuck (no frames, a popup waiting, a load that doesn't finish).

It is a **development tool** for testing mods. Several tools are cheats (giving items, spawning, changing live
values), so install it only in a separate mod-manager profile, never in one you play, and use a **test world and
test character**: the agent changes things in the world while it tests. Started any way other than through GABS it
stays completely off, so it can stay installed in a profile used for performance measurements.

Cheat console commands are refused unless the agent passes `allowCheats` or devcommands is already on (some mods,
such as Server Devcommands, can turn it on automatically). If you don't want the agent to use cheats at all, tell
it so.

Fork of [jneb802/ValBridgeServer](https://github.com/jneb802/ValBridgeServer) (MIT, see [LICENSE](LICENSE)),
rebuilt for Valheim 1.0 on Windows and extended. What changed is in [CHANGELOG.md](CHANGELOG.md).

## How it fits together

```
AI agent (Claude Code) --MCP--> GABS --GABP (TCP, localhost)--> ValBridge inside valheim.exe
```

[GABS](https://github.com/pardeike/GABS) starts the game with a port and token in the environment; ValBridge reads
them and listens on `127.0.0.1`. Started any other way (e.g. from the mod manager), ValBridge stays off.

## Requirements

- Valheim 1.0 (Unity 6, Mono), BepInEx 5.4.23
- ValheimModding-JsonDotNET (Newtonsoft.Json) in the profile
- GABS 1.1 with a game entry that starts Valheim with the profile's BepInEx (see below)
- To build: .NET SDK (any recent one; the project targets net48) and Valheim installed

## Build and install

The game must be closed (the DLL is locked while it runs).

```
dotnet build -c Release -p:Deploy=true
```

Builds against the game's real DLLs and copies `ValBridgeServer.dll`, `Lib.GAB.dll` and `Gabp.Runtime.dll` into
`<profile>\BepInEx\plugins\ValBridge\`, and `tests\*.json` into `<profile>\BepInEx\ValBridge\tests\`. Paths are
MSBuild properties in `ValBridge.csproj` (`ValheimDir`, `GaleProfile`; default profile: Gale's `Performance Test`).
The DLLs are also copied into every other Gale profile that already has the bridge installed, so no profile is left
on an old version. BepInEx, Harmony and Json.NET are referenced from the first Gale profile that has them, so
building works even if the deploy profile doesn't exist.

Another profile, and taking it out again (game closed):

```
dotnet build -c Release -p:Deploy=true -p:GaleProfile="%APPDATA%\com.kesomannen.gale\valheim\profiles\Performance Test"
dotnet build -t:Undeploy -p:GaleProfile="%APPDATA%\com.kesomannen.gale\valheim\profiles\Performance Test"
```

Installed but started normally (not by GABS), the bridge does nothing: no patches, no listener, no per-frame work.

## Using it

1. Put GABS in `tools\gabs\` (download from its releases page; the folder is gitignored) and register it as an MCP
   server. `.mcp.json` does that for sessions in this folder; for every project on the machine:
   `claude mcp add --scope user gabs -- <path>\gabs.exe server`.
2. GABS game config, `%USERPROFILE%\.gabs\config.json`: a game `valheim` that starts `valheim.exe` with
   `--doorstop-enabled true`, and one launch profile per mod-manager profile adding
   `--doorstop-target-assembly <profile>\BepInEx\core\BepInEx.Preloader.dll`. Steam must be running.
3. In an agent session: `games_start valheim` (optionally `profile`). In a heavily modded profile the bridge comes
   up only after ~40 s, so `games_start` may answer `started_bridge_pending`: then `games_connect` (`timeout` 120).
   Then `world/enter` (GABS `timeout` 300; 700 with `create`), which waits for the main menu, loads the last world
   (or the `character`/`world` given, creating them with `create`) and returns once the player is in it. Then call
   tools, e.g. `player/get_inventory`, `screen/capture`. Not sure of a name: `games_tool_names` / `games_tool_detail`;
   a wrong argument name gets an answer listing the right ones.

### From another mod's project

Any mod project can use the bridge to test itself: install the bridge in the profile that mod is tested in, start
that profile through GABS, and drive the game with the tools here. A mod can also add its own tools with
`[BridgeTool]` (see [TOOLS.md](TOOLS.md)), without a reference to this project.

### Suggested companion: Mod Performance Monitor (optional)

ValBridge works on its own and doesn't need any other mod. If you also test performance, it pairs well with
[Mod Performance Monitor](https://github.com/skatefast29/ModPerfMonitor), which measures what each installed mod and
each part of Valheim costs: CPU time, memory allocations, load times and network. With both in the same test
profile, the monitor adds its own `perf/*` tools to the bridge, so an agent can run a whole measurement without
anyone at the keyboard: get into the world, clear creatures, run the monitor's test suite or a live A/B test of a change,
and read the results (`perf/start_suite`, `perf/results`, `perf/ab_test`, `perf/load_times`, `perf/objects` and
more; `mods/list_commands` shows them all).

All tools: [TOOLS.md](TOOLS.md).

## Files written in the profile

`<profile>\BepInEx\ValBridge\`: `screenshots\` (last 30), `tests\` (scripts), `test-results\` (reports).

Scripts that come with the bridge (`tests\`, copied on deploy and built into the bridge, so they work in any
profile):

| Script | What |
|---|---|
| `enter-world` | Same as `world/enter`: waits for the main menu, loads the last world, waits until in it |
| `quick-check` | Quick check that the bridge answers in a world; reads only, changes nothing |
| `inventory-items-console` | Inventory and backpacks, refusing cheat console commands, item lookups, errors for missing arguments, health events; sets health to 15 for a second, then restores it (run it somewhere safe) |
| `world-controls` | World and player controls: spawns and removes Greylings, pins and releases time and weather, teleports in place, turns the player, presses Scroll Lock, saves; puts everything back |

## Project layout

| Path | What |
|---|---|
| `Src\Plugin.cs` | BepInEx entry point: config, GABP server, tool registration |
| `Src\Tools\` | One class per tool area: player, world (incl. `world/enter`), control (teleport, creatures, time, weather), input, mods, debug, watch, ui, logs, time, items, test, screen, console, znetscene |
| `Src\ToolGuard.cs` | Registers tools with argument-name checks; closest-name suggestions |
| `Src\MainThreadDispatcher.cs` | Runs tool work on Unity's main thread; frame heartbeat for stuck detection |
| `Src\PlayerLook.cs` | Turning the player and camera |
| `Src\Inspector.cs` | Reflection helpers for the debug tools |
| `Src\Recorder.cs`, `Src\PlayerEvents.cs` | In-memory log, messages and player events |
| `Src\BackpackCompat.cs`, `Src\ModCommands.cs` | Backpack mods; commands declared by other mods |
| `lib\` | `Lib.GAB.dll` (built from pardeike/Lib.GAB 1.0.5) and `Gabp.Runtime.dll` (NuGet 1.0.0) |
| `tests\` | Test scripts for `test/run_file` |
| `tools\decompile.ps1` | Decompiles the game (and chosen mods) into `reference\decompiled\` for local reading |
| `reference\` | Local only, gitignored: decompiled game code and upstream sources |

## License

MIT, as upstream. See [LICENSE](LICENSE).
