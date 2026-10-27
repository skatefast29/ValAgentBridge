# Changelog

Fork of [jneb802/ValBridgeServer](https://github.com/jneb802/ValBridgeServer) at commit `eb0fe1b` (2026-02-22).

## 1.8.2
- Fixed: `player/set_protection` answered success while god and ghost mode stayed off. With Server Devcommands
  installed they only work while devcommands are on; the tool now fails with that reason (and leaves them off),
  and a test script with `"protect": true` says so in `protectionProblem` instead of running unprotected silently.
- New `test/wait_until`: wait inside the game until a tool's answer passes checks (e.g. a recording has 60 s),
  without writing a one-step test script.
- `debug/watch_read` with `id` 0 (or none) reads every watch, as `debug/watch_stop` already did.
- `world/remove_objects` also takes `instanceIds`, the name `world/spawn_creature` answers with; no ids is an error.

## 1.8.1
- `world/enter` checks that the character and world exist before logging out of the current world, so a wrong name
  (without `create`) gives the error and leaves you where you were instead of at the main menu.
- Test reports (`reportFile`) keep step results in full (up to 200,000 characters each) instead of cutting them at
  3,000; only the answer shortens them. With `detail: "full"` the answer cuts results over 3,000 characters and says so.
- README: the newer ValPerfMonitor tools, and how their instanceIds work with the bridge's debug tools.

## 1.8.0
- New `player/interact_object`: use a crafting station, chest, portal, door, bed or ship's controls by instanceId,
  without aiming at it, and get back what opened (or the distance when too far, or what the game said).
  Portals: `travel: true` goes through.
- New `input/scroll` and `input/drag`: mouse wheel and drag as if by hand (map zoom and pan, build-menu tabs).
- New `world/spawn_object` (cheat): spawn any prefab like the game's spawn command, at a position or an offset from
  the player. Boats sit on the water facing the player's way; a spot overlapping a ship, or a boat on top of a
  player, is refused unless forced.
- Owner in control: teleport, look, walk, jump, dodge and interact refuse while the owner is steering or on a
  moving ship, sitting, in a menu or just used keyboard/mouse/gamepad; `force: true` overrides.
- Test scripts: `"optional": true` steps (a failure is listed but the run goes on), `repeat` blocks with a round
  counter, and a short summary answer by default (`detail: "full"` for every step; the saved report has all).
- `world/remove_objects` takes `ids` as a real array (a JSON string still works); any list argument does.
- New `console/set_devcommands`: switch devcommands on or off without flipping it by accident;
  `console/run_command` refuses the bare `devcommands` toggle.
- Frames in which the bridge ran tool work are recorded (`MainThreadDispatcher.WorkedInFrame`), so ValPerfMonitor
  can leave them out of measurements.

## 1.7.0
- New `debug/scene_tree`: browse every Unity object in the loaded scenes (world objects, menus, HUD, and the
  objects the game and mods keep across scenes) as a tree with names, instanceIds, active state and components.
  Large groups of same-named objects (thousands of trees) are counted by name, with the one-off objects listed
  first.
- New `debug/find_objects`: search that tree by object name and/or component type, everywhere or below one object.
- New `debug/set_active` (cheat): switch a Unity object on or off, e.g. to see whether a problem or a frame-time
  cost goes with it. Refuses the player character, the bridge's own objects and a path that matches several
  same-named objects; logged.
- A path of names that matches several same-named objects gives a warning (the first is taken; the instanceId
  picks the right one).

## 1.5.5
- Minor README fixes.

## 1.5.4
- Fixed: the bridge didn't start in a profile where another mod carries optional code for a mod that isn't
  installed (e.g. ValPerfPatcher's HearthBelow fix without HearthBelow). The scan for other mods' commands skips
  such code, and the scan, log recording and player events can no longer stop the bridge from starting: a failure
  is one warning and the bridge runs without that part.
- Hardened against other mods: a log line that can't be turned into text no longer throws into the mod that logged
  it; inspecting an object whose code needs a missing mod shows that instead of failing; a walk or move that hits
  an error ends with an answer instead of repeating the error every physics tick; `world/status` answers even when
  it can't read the game; session creature clearing doesn't pile up while the game is loading.
- Build: compile references come from any Gale profile with BepInEx and Json.NET; deploying skips a named profile
  that no longer exists instead of recreating it as an empty folder.

## 1.5.3
- No changes to the mod.

## 1.5.2
- New `world/keep_clear`: keeps removing hostile creatures near the player for the whole session, so one can't kill
  the character mid-test. Off by default; `world/enter` answers with `askOwner` until it is set, so the agent offers
  it to the owner. `world/status` shows its state.
- `player/block` holds the block until `active=false` (the game's own controls ended it on the next physics tick).
- `player/dodge` rolls in the world direction given (it was read relative to the camera, so "backward" went
  sideways unless facing north).
- `player/teleport` without a height lands on the highest surface (ground, rock, roof; not tree tops) instead of the
  terrain under it. A teleport that times out is cancelled instead of happening later; landing errors are answers.
- `ui/click` with neither id nor label asks which control instead of clicking one without a label.
- Test scripts: `"clearHostiles": false` no longer clears.
- `world/enter` and `input/press_key` answer with an error instead of failing the call when the game is busy.
- `player/place_piece` and `player/repair_item` are marked (cheat) and logged; placed pieces count as cheated for
  the build-height stat; the place-piece description no longer claims a crafting station is needed.
- One release zip, `ValAgentBridge-<ver>.zip`, in the Thunderstore layout, for every site.

## 1.5.1
- `player/teleport` without a height lands on the ground (or the water surface at sea). It used to keep the player's
  current height, so the game dropped them from high above lower ground or refused ("blocked") on higher ground.
  Far targets wait for the area to load, then land without fall damage.
- README and Thunderstore page say up front that the mod is for mod developers and needs GABS; shorter wording
  elsewhere. The Thunderstore package now includes `TOOLS.md`.

## 1.5.0
- Available as a Thunderstore package (`thunderstore\`: manifest, icon, README); `tools\package.ps1` builds the
  release zips.
- The bundled `inventory-items-console` test works whatever the character's health is.

## 1.4.2
- README: opens with what the mod is for (automating and monitoring mod testing); how cheat commands are handled;
  install from a release.
- Bundled test scripts named after what they check: `quick-check`, `inventory-items-console`, `world-controls` and
  `enter-world`.
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
