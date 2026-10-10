# ValAgentBridge

ValAgentBridge was made to help with modding Valheim: it lets an AI agent automate and monitor the testing of mods.
Instead of launching the game, loading a world and checking things by hand after every change, you ask the agent
(Claude Code, through the GABS MCP server) and it does it in the running game.

> **For mod developers.** Needs [GABS](https://github.com/pardeike/GABS) (not on Thunderstore) and an AI agent such
> as Claude Code. Setup, the full tool list and test scripts: [GitHub](https://github.com/skatefast29/ValAgentBridge).

## Features

- **Game:** enter a world in one call, create characters and worlds, load, save, log out
- **Movement:** walk, pathfind, jump, look, teleport
- **Player actions:** attack, block, interact, pick up, drop, equip, eat, craft, repair, build, press keys, click menus
- **Console commands:** any game or mod command, with its output
- **Test setup:** give items, spawn creatures, clear enemies, time, weather, health, god/ghost mode, game speed, mod
  settings, live values
- **Lookups:** player state, inventory and backpacks, nearby objects, locations, items and recipes
- **Monitoring:** screenshots, log, on-screen messages, player events, live code inspection, method watches
- **Test scripts:** JSON steps and checks with pass/fail reports
- **Stuck detection:** no frames, waiting popups, stalled loads
- **Other mods:** list mods and settings, use commands they add

Cheat console commands run only with `allowCheats` or when devcommands is already on.

Clearing hostile creatures is off by default. When testing starts the agent offers to turn it on, so a creature
can't kill the character mid-test; say no if you are testing creatures.

## What you need

- [Claude Code](https://claude.com/claude-code) (or another MCP client)
- [GABS](https://github.com/pardeike/GABS) 1.1 or newer (download from its releases page), which starts the game and
  connects the agent to ValAgentBridge

Started without GABS, ValAgentBridge does nothing: no patches, no listener, no per-frame work.

## Setup

1. Install ValAgentBridge in a profile with your mod manager.
2. Register GABS as an MCP server: `claude mcp add --scope user gabs -- <path>\gabs.exe server`.
3. GABS game config, `%USERPROFILE%\.gabs\config.json`: a game `valheim` that starts `valheim.exe` with
   `--doorstop-enabled true --doorstop-target-assembly <profile>\BepInEx\core\BepInEx.Preloader.dll`.
4. Ask the agent to start Valheim (`games_start valheim`), then `world/enter` to load into a world. It finds every
   tool and its arguments itself.

The full tool list is also in `TOOLS.md` in this package.

Works well with [ValPerfMonitor](https://github.com/skatefast29/ValPerfMonitor) (optional), which adds performance
measurements the agent can run through ValAgentBridge.
