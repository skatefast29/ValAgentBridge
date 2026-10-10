# ValAgentBridge tool reference

All tools are called through GABS (`games_call_tool`). GABS shows them with a game prefix and underscores, e.g.
`player/get_health` is `valheim_player_get_health`. Every answer has `success`; failures carry an `error` text.
Tools that need Unity run on the game's main thread and give up after 15 s (loading screen, paused game) with an
error instead of hanging. A wrong or missing argument name gets an answer listing the arguments the tool takes.
Not sure of a name: `games_tool_names` (`brief: true`) and `games_tool_detail`.

Cheat-like tools are marked **(cheat)**. They work without `devcommands`, change the live game and are logged.

## Player state

| Tool | What it does |
|---|---|
| `player/get_health` | Health, max health, percentage |
| `player/get_position` | World position |
| `player/get_player_state` | Health, stamina, position, look direction, biome, weapon, status effects, foods |
| `player/get_inventory` | Items (name, amount, slot, equipped, durability), weight vs carry limit, encumbered, and the contents of an equipped backpack (Adventure Backpacks or Backpacks) |
| `player/get_visible_objects` | Objects in the camera view with line of sight, nearest first, with instanceIds |
| `player/find_nearby_prefabs` | Objects whose name contains `prefabName` within `range`, nearest first |

## Items

| Tool | What it does |
|---|---|
| `player/give_item` | **(cheat)** Put items straight into the inventory, marked cheated like the game's spawn |
| `player/pickup_nearby` | Pick up loose drops of one item (`itemName`), or `*` for everything in range |
| `player/equip_item`, `player/unequip_item` | By name; exact prefab name wins over a partial match |
| `player/use_item` | Eat or drink a consumable |
| `player/drop_item` | Drop from the inventory, or from the backpack with `fromBackpack: true` |
| `player/repair_item` | **(cheat)** Restore durability; no station check, no cost |
| `items/find` | Search all items (vanilla and modded) by text and type, sort by health, stamina, eitr, food, damage, armor, block or weight |
| `items/info` | One item: food values, damage by type, armor, stack, weight, teleportable, recipe |

## Moving, looking, fighting

| Tool | What it does |
|---|---|
| `player/move_direction` | Walk/run in a world direction (`x`, `z`) for `duration` seconds |
| `player/navigate_to_position` | Pathfind and walk to `x`, `z`; returns when arrived, stuck or timed out |
| `player/look_at_position`, `player/look_at_object` | Face a point or an object (instanceId) |
| `player/look_direction` | Turn player and camera to a compass `heading` (0 = north/+Z, 90 = east/+X) and `tilt` (up +, down -), e.g. to aim a screenshot |
| `world/clear_hostiles` | **(cheat)** Remove creatures hostile to the player within `radius` (default 50 m), so a test isn't interrupted; the player is untouched. Never players or tamed animals; bosses only with `includeBosses` |
| `player/set_protection` | `god` (no damage) and `ghost` (creatures ignore you), `true`/`false`; switched directly, so the character is not marked as having used cheats. Returns before/after. Not saved across restarts |
| `player/teleport` | **(cheat)** Teleport to `x`, `z`, optional `y` (left out: lands on the ground, or on the water surface at sea); waits until done and returns where the player landed |
| `input/press_key` | Press a key as if typed (`key`, e.g. F11, Escape, Alpha1; `modifiers` ctrl/shift/alt; `holdFrames`). Reaches game controls and mod hotkeys made with BepInEx (most mods), not mods that read `UnityEngine.Input` directly |
| `player/jump`, `player/dodge`, `player/set_crouch` | Single inputs |
| `player/primary_attack`, `player/secondary_attack`, `player/block` | Single attack, or start/stop blocking |
| `player/interact` | Press E on whatever is hovered |

## Crafting and building

| Tool | What it does |
|---|---|
| `player/get_available_recipes` | Recipes craftable here, with materials and whether you have them |
| `player/craft_item` | Craft by name, consuming materials |
| `player/place_piece` | Place a build piece (hammer equipped) at a position and rotation |
| `player/remove_piece` | Demolish a piece by instanceId |

## World and game

| Tool | What it does |
|---|---|
| `world/status` | `main_menu`, `loading` or `in_world`, with character and world; `secondsInPhase`, `frameAgeMs` (time since the game last drew a frame; pauses while loading are normal), `fps`, the text of a popup blocking the menu, and a `hint` when something looks stuck |
| `world/enter` | Get into a world in one call from wherever the game is: waits for the menu (fine right after `games_start`), logs out first if another world or character is loaded, loads, waits until in the world. `character`, `world` (empty = in use / last used), `create` (make a missing character or world), `seed`, `timeout` (default 180 s, 600 s when it creates a world; stops early after 90 s without a frame, 5 min while generating; give GABS `timeout` 300, 700 with `create`) |
| `world/list_saves` | Characters and worlds on disk, and the last-used ones |
| `world/load` | From the main menu, start loading a character into a world and return at once (`world/enter` waits) |
| `world/logout` | Save and return to the main menu |
| `world/save` | Save the world and characters now |
| `world/find_locations` | Nearest generated places by (part of) name: trader `Vendor`, boss altars (`Eikthyrnir`, `GDKing`, ...), dungeons (`Crypt`, `SunkenCrypt`, `TrollCave`), `StartTemple`. No name = list the place types in this world |
| `world/spawn_creature` | **(cheat)** Spawn `amount` creatures (`name`, `level` 1-3 stars+1, `distance`, `tamed`) in front of the player; returns instanceIds |
| `world/remove_objects` | **(cheat)** Remove objects by instanceId (`ids` as a JSON array), e.g. creatures spawned for a test |
| `env/status` | Hour, day, current weather, whether time/weather are pinned, weather names |
| `env/set_time` | **(cheat)** Jump forward to an `hour` (0-24) like sleeping, or `pin` it there for repeatable tests; `hour` -1 releases a pinned time |
| `env/set_weather` | **(cheat)** Force a weather by name (`env/status` lists them); empty = normal weather |
| `time/status`, `time/set_speed`, `time/pause` | Game speed (0.1 to 10x) and pause; single player only |
| `console/run_command` | Run a console command and get the console's output. Cheat commands need `allowCheats: true`, which turns devcommands on for that one command; the game then marks the character as having used cheats. If devcommands is already on (e.g. a mod turns it on automatically), cheat commands run like any other; `cheat` and `cheatsOn` in the answer say so |
| `znetscene/search_prefabs` | World prefabs by part of the name, each with its kind (`creature`, `item`, `piece`, `other`); `kind` filters, e.g. creatures for `world/spawn_creature` |
| `znetscene/get_prefab` | One prefab by exact name: kind, layer, components |

## Screen, UI, logs, events

| Tool | What it does |
|---|---|
| `screen/capture` | Screenshot (JPG, default 1280 wide) to `BepInEx\ValAgentBridge\screenshots\` (last 30 kept); returns the path |
| `ui/list` | The open popup's text and the visible clickable buttons/toggles (`includeHud` adds hotbar slots etc.) |
| `ui/click` | Click a button or flip a toggle by id or label |
| `ui/messages` | Messages the game showed on screen, numbered (`since` = only new ones) |
| `logs/read`, `logs/mark` | The last 3000 BepInEx/Unity log lines, numbered, filter by level and text |
| `events/read` | `player/death` and `player/health_changed` events (also sent on those GABP channels) |

## Mods and live inspection

| Tool | What it does |
|---|---|
| `mods/list` | Loaded BepInEx mods: GUID, name, version, DLL |
| `mods/get_config`, `mods/set_config` | Read a mod's settings, change one live (saved to its .cfg) |
| `mods/list_commands` | Commands other mods added with `[BridgeTool]` (see below) |
| `debug/find_types` | Loaded types whose name contains a text |
| `debug/inspect` | Read live values by path, e.g. `ZoneSystem.instance.m_globalKeys` or `Player.m_localPlayer.m_inventory.m_inventory.0` (a number indexes a list). Below the path only fields are read, never property getters |
| `debug/inspect_object` | Components on a world object (instanceId) and the fields of the game/mod ones |
| `debug/set_value` | **(cheat)** Change a live field or property by path |
| `debug/call_method` | **(cheat)** Run a method on a type or on the object at a path, args as a JSON array (a Vector3 as `[x, y, z]` or `"x,y,z"`) |
| `debug/watch_start`, `debug/watch_list`, `debug/watch_read`, `debug/watch_stop` | Patch a method at runtime: count every call and record the arguments, result and exceptions of the first N. Mono inlines tiny methods (under ~20 bytes of IL); a watch on one may not see every call, and the start answer warns about it |

## Test scripts

`test/run` takes a script inline (a JSON object, or the same as a string), `test/run_file` reads
`BepInEx\ValAgentBridge\tests\<name>.json` (the files in `tests\` are copied there on deploy and also built into the
bridge, so `enter-world`, `quick-check`, `inventory-items-console` and `world-controls` work in any profile), `test/list_files` lists them and the
latest results.

```json
{ "name": "give-and-check", "stopOnFail": true, "steps": [
  { "call": "world/status", "expect": [ { "path": "phase", "equals": "in_world" } ] },
  { "call": "player/give_item", "args": { "itemName": "CopperOre", "amount": 5 } },
  { "wait": 2 },
  { "waitUntil": { "call": "world/status" }, "until": [ { "path": "phase", "equals": "in_world" } ], "timeout": 120, "every": 2 },
  { "waitUntil": "in_world", "timeout": 300 },
  { "call": "player/get_health", "saveAs": "hp", "expect": [ { "path": "health", "greaterThan": 0 } ] },
  { "call": "debug/inspect", "args": { "path": "Player.m_localPlayer.m_name" } },
  { "screenshot": true, "maxWidth": 960 },
  { "note": "free text, shown in the report" }
] }
```

- Checks (`expect`, `until`): `path` (JSON path into the result, e.g. `position.x`, `items[0].name`) plus one of
  `equals`, `notEquals`, `contains`, `greaterThan`, `lessThan`, `exists`.
- Top-level `"clearHostiles": 50` removes hostile creatures within 50 m before the first step (`true` = 50 m).
- Top-level `"keepClear": 50` (or `{ "radius": 50, "every": 10 }`) clears them again every 10 s during the run; the
  answer counts how many (`hostilesClearedDuringRun`). Off by default: avoid it in performance measurements.
- Top-level `"protect": true` turns on god and ghost mode for the run and puts them back as they were afterwards.
- `"waitUntil": "in_world"` (or `"main_menu"`, `"loading"`) is short for waiting on `world/status`'s phase.
- An unknown tool name fails the step and lists the closest names.
- A tool answering `success: false` fails the step unless the step has `"allowFail": true`.
- `"saveAs": "hp"` keeps the step's result; later args and checks can use `${hp}` or `${hp.health}`.
- A failing step gets a screenshot (top-level `"screenshotOnFail": false` turns that off).
- The answer lists every step (ok, failures, seconds, trimmed result), plus a summary of the warnings/errors
  logged, on-screen messages (identical lines once, with a count) and player events (count per channel, deaths,
  the last few). The full report with every line is saved to `BepInEx\ValAgentBridge\test-results\` (`reportFile`).
- Calls with long waits need a longer GABS `timeout`.

## Commands from other mods

A mod adds bridge commands without referencing the bridge: copy this attribute into the mod (any namespace) and
put it on **static** methods. Parameters: strings, numbers, bools (defaults make them optional). Return any object;
it becomes the JSON answer. The bridge finds them at startup and runs them on Unity's main thread.

```csharp
[AttributeUsage(AttributeTargets.Method)]
internal sealed class BridgeToolAttribute : Attribute
{
    public BridgeToolAttribute(string name, string description) { Name = name; Description = description; }
    public string Name { get; }
    public string Description { get; }
}

[BridgeTool("bgp/tier_status", "Locked/unlocked state of each portal tier for the local player")]
private static object TierStatus(bool verbose = false) => new { success = true, /* ... */ };
```

Names must be lowercase with at least one `/` (`mymod/thing`) and must not clash with a bridge command. Calls with
an argument name the method doesn't have are refused with the list of its parameters;
`mods/list_commands` shows what was found and why anything was skipped. The attribute costs nothing when the
bridge isn't installed.
