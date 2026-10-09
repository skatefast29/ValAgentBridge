# Changelog

Fork of [jneb802/ValBridgeServer](https://github.com/jneb802/ValBridgeServer) at commit `eb0fe1b` (2026-02-22).

## Unreleased
- Bundled test scripts named after what they check: `smoke` is now `quick-check`, `bridge-1.3` is
  `inventory-items-console`, `bridge-1.4` is `world-controls` (`enter-world` is unchanged).
- Their checks hold up in any profile: a harmless cheat command (`tod -1`) for the refusal check, and no
  comparison of max health, which drifts as food wears off.
- `console/run_command`: `cheat` is read before the command runs (Server Devcommands clears the flag while it runs, so
  cheats were reported as normal commands); new `cheatsOn` says devcommands was already on, which is why a cheat ran
  without `allowCheats`. The test switches devcommands off for its refusal check and puts it back.

## 1.4.1
- `player/look_at_position` and `player/look_at_object` now really turn the player (they set a look direction the
  game overwrote on the next frame); they share the turning code with `player/look_direction`.
- `znetscene/search_prefabs` returns name and kind (creature, item, piece, other) and can filter by kind;
  `znetscene/get_prefab` is shorter; `znetscene/list_prefab_names` is gone (search does the same).
- Commands added by other mods get the same argument-name check as the bridge's own.
- `debug/call_method` and `debug/set_value` take a Vector3 as `[x, y, z]` and a Vector2 too.
- Test scripts: `${...}` works in checks (`expect`, `until`) as well as in arguments.
- One popup reader instead of two; unused leftovers from upstream removed. README, tool reference and notes
  brought up to date.

## 1.4.0
- `world/enter`: gets into a world in one call from wherever the game is (still starting, main menu, or another
  world): waits for the menu, logs out if needed, can create a missing character or world, loads and returns once
  in the world. Gives up after 3 minutes (10 when it creates a world, since generating one takes long), or after
  90 s without a single frame (5 minutes while generating).
- `world/status` also tells how long the game has been in its phase, how long ago it drew a frame, fps, the text of
  a popup blocking the menu, and a hint when something looks stuck (no frame for 5 s in a world, 45 s while loading,
  3 minutes while generating a new world; or loading for over a minute while frames still draw). Short pauses
  while loading are normal.
- A wrong or missing argument name gets an error listing the arguments the tool takes (they were ignored silently,
  so the tool ran with a default).
- Test scripts: `test/run` accepts the script as a JSON object as well as a string; `"waitUntil": "in_world"`
  shorthand; an unknown tool name lists the closest names; the bundled scripts are built into the bridge, so they
  work in any profile.
- `world/clear_hostiles`: removes hostile creatures around the player (not tamed ones, bosses only if asked);
  test scripts can start with `"clearHostiles": 50`, and `"keepClear": 50` repeats it every 10 s during the run.
- `player/set_protection`: god mode (no damage) and ghost mode (creatures ignore the player), without marking the
  character as having used cheats; test scripts can add `"protect": true` to have it on for the run only.
- New controls: `player/teleport`, `player/look_direction` (aim the camera), `input/press_key` (game keys and mod
  hotkeys), `world/find_locations` (trader, boss altars, dungeons), `world/spawn_creature` and
  `world/remove_objects`, `env/status`, `env/set_time` and `env/set_weather` (jump or pin time, force weather),
  `world/save`.
- Test answers are shorter: repeated warnings and messages are counted once, events are summarised. The saved
  report file keeps every line.

## 1.3.2
- The bridge now stays fully off (no patches, no listener, no per-frame work) unless GABS started the game. It used
  to also switch on whenever `~/.gabs/valheim/bridge.json` existed, and that file stays behind after every GABS
  launch, so normal launches from the mod manager turned it on too.
- `dotnet build -t:Undeploy -p:GaleProfile=<profile dir>` takes the bridge back out of a profile.

## 1.3.1
- Every player tool now does all its work on Unity's main thread (some read the player and transform from the
  network thread) and shares one error path: failures and timeouts come back as `{ success: false, error }`.
- `move_direction` and `navigate_to_position` give up after their time plus 10 s of real time, so a paused game
  can't hang the call; the movement is stopped.
- `look_at_object` and `remove_piece` find the object by id directly instead of scanning everything within
  50-100 m.
- `craft_item`, `place_piece`, `unequip_item` and `repair_item` prefer an exact name over a partial match.
- `debug/inspect`, `debug/watch_start` and friends: loaded types are indexed once instead of scanned on every
  lookup (inspect calls went from ~0.5 s to milliseconds).
- `ui/list` hides HUD controls (hotbar slots) unless `includeHud` is set.
- Unity objects are null-checked with `==`, never `?.` (destroyed objects).
- Removed the unused upstream `AttackManager`. Assembly version now matches the plugin version.

## 1.3.0
- `items/find`, `items/info`; `events/read` (death and health events, also sent on their GABP channels).
- 15 s timeout on the older player tools; Smoothbrain Backpacks support; screenshot on a failing test step.

## 1.2.1
- `console/run_command` runs synchronously, returns the console output and refuses cheat commands unless
  `allowCheats` (they used to report success while doing nothing).
- `player/give_item`; `pickup_nearby` needs an item name; inventory shows Adventure Backpacks contents.

## 1.2.0
- `logs/*`, `ui/*`, `time/*`, `debug/set_value`, `debug/call_method`, `debug/watch_*`, `test/*`, and commands
  declared by other mods with their own `[BridgeTool]` attribute.

## 1.1.0
- `world/*` (load a world from the main menu, logout), `mods/*` (read and change settings), `debug/find_types`,
  `debug/inspect`, `debug/inspect_object`, `screen/capture`.

## 1.0.0 (fork baseline)
- Builds on Windows against the real Valheim 1.0 DLLs (upstream used publicized DLLs and macOS paths).
- 1.0 API fixes: `Player.m_buildPieces` is private -> `GetBuildTool()`; `Inventory.AddItem(name, ...)` gained
  `cheated`. `Gabp.CreateServerWithExternalConfig` -> `Lib.GAB.Gabp...`.
- Tools that touch Unity run on the main thread. Stays off when the game wasn't started by GABS.
- Canonical GABP tool names (`player/get_health`): Lib.GAB 1.0.5 refuses the old flat names.
