using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Lib.GAB.Tools;
using UnityEngine;

namespace ValAgentBridge.Tools
{
    /// <summary>
    /// Test helpers that change the live game: teleport, spawn and remove creatures, time of day, weather, saving,
    /// finding places. The cheat-like ones are marked (cheat) and logged; none of them mark the character as cheated.
    /// </summary>
    public class ControlTools
    {
        private static void Log(string text) => ValAgentBridgePlugin.ModLogger.LogInfo(text);

        [Tool("player/teleport", Description = "(cheat) Teleport the player to a world position; the game finds the ground (height is optional). Waits until the teleport has finished (far ones load the area first) and returns where the player ended up.")]
        public object Teleport(
            [ToolParameter(Description = "X coordinate")] float x,
            [ToolParameter(Description = "Z coordinate")] float z,
            [ToolParameter(Description = "Height; leave out to land on the ground")] float y = float.NaN)
        {
            string? problem;
            try
            {
                problem = MainThreadDispatcher.Run(() =>
                {
                    var player = Player.m_localPlayer;
                    if (player == null) return "No local player (not in a world).";
                    var from = player.transform.position;
                    var target = new Vector3(x, float.IsNaN(y) ? from.y : y, z);
                    bool distant = Vector3.Distance(new Vector3(from.x, 0, from.z), new Vector3(x, 0, z)) > 64f;
                    if (!player.TeleportTo(target, player.transform.rotation, distant))
                        return "The game refused (already teleporting, or within 2 s of the last teleport). Try again in a moment.";
                    Log($"player/teleport: ({from.x:F0}, {from.z:F0}) -> ({x:F0}, {z:F0})");
                    return null;
                }, 15000);
            }
            catch (Exception ex) { problem = ex.GetBaseException().Message; }
            if (problem != null) return new { success = false, error = problem };

            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < 40)
            {
                Thread.Sleep(500);
                object? done;
                try
                {
                    done = MainThreadDispatcher.Run(() =>
                    {
                        var player = Player.m_localPlayer;
                        return player == null || !player.IsTeleporting() ? Pos(player) ?? "no player" : null;
                    }, 5000);
                }
                catch (TimeoutException) { continue; }    // area loading: keep waiting
                if (done != null)
                    return new { success = true, position = done, seconds = Math.Round(clock.Elapsed.TotalSeconds, 1) };
            }
            return new { success = false, error = "Still teleporting after 40 s (area slow to load). Check world/status." };
        }

        [Tool("world/clear_hostiles", Description = "(cheat) Remove creatures hostile to the player within a radius, so a test isn't interrupted by attacks. Leaves the player untouched (no god mode). Never removes players or tamed animals; bosses only with includeBosses. Returns what was removed.")]
        public object ClearHostiles(
            [ToolParameter(Description = "Radius in metres around the player (default 50, max 200)")] float radius = 50f,
            [ToolParameter(Description = "Also remove bosses (default false)")] bool includeBosses = false)
        {
            return MainThreadDispatcher.RunTool(() =>
            {
                var player = Player.m_localPlayer;
                if (player == null || ZNetScene.instance == null) return new { success = false, error = "Not in a world." };
                var near = new List<Character>();
                Character.GetCharactersInRange(player.transform.position, Mathf.Clamp(radius, 1f, 200f), near);
                var removed = new Dictionary<string, int>();
                int bossesLeft = 0;
                foreach (var c in near)
                {
                    if (c == null || c.IsPlayer() || c.IsTamed() || !BaseAI.IsEnemy(player, c)) continue;
                    if (c.IsBoss() && !includeBosses) { bossesLeft++; continue; }
                    var name = Utils.GetPrefabName(c.gameObject);
                    removed[name] = removed.TryGetValue(name, out var n) ? n + 1 : 1;
                    ZNetScene.instance.Destroy(c.gameObject);
                }
                int total = removed.Values.Sum();
                if (total > 0) Log($"world/clear_hostiles: removed {total} within {radius:F0} m ({string.Join(", ", removed.Select(kv => $"{kv.Value} {kv.Key}"))})");
                return new { success = true, removed = total, kinds = removed, bossesLeft };
            });
        }

        [Tool("player/set_protection", Description = "Keep the player safe while a test runs: god = takes no damage, ghost = creatures ignore the player. Switched directly, not through the console, so the character is not marked as having used cheats. Leave an argument out to keep it as it is; returns the state before and after. Not saved: both are off again after a restart.")]
        public object SetProtection(
            [ToolParameter(Description = "No damage (true/false; leave out to keep)")] string god = "",
            [ToolParameter(Description = "Creatures ignore the player (true/false; leave out to keep)")] string ghost = "")
        {
            bool? wantGod = Flag(god), wantGhost = Flag(ghost);
            if (god.Length > 0 && wantGod == null || ghost.Length > 0 && wantGhost == null)
                return new { success = false, error = "god and ghost take true or false." };
            return MainThreadDispatcher.RunTool(() =>
            {
                var player = Player.m_localPlayer;
                if (player == null) return new { success = false, error = "Not in a world." };
                var before = new { god = player.InGodMode(), ghost = player.InGhostMode() };
                if (wantGod.HasValue) player.SetGodMode(wantGod.Value);
                if (wantGhost.HasValue) player.SetGhostMode(wantGhost.Value);
                var after = new { god = player.InGodMode(), ghost = player.InGhostMode() };
                if (wantGod.HasValue || wantGhost.HasValue) Log($"player/set_protection: god {after.god}, ghost {after.ghost}");
                return new { success = true, before, after };
            });
        }

        private static bool? Flag(string text) => text.Trim().ToLowerInvariant() switch
        {
            "true" or "on" or "1" or "yes" => true,
            "false" or "off" or "0" or "no" => false,
            _ => null
        };

        [Tool("world/find_locations", Description = "Find generated places (trader, boss altars, dungeons, villages, ...) nearest the player. With no name: lists the place types in this world and how many of each. Names are prefab names, e.g. Vendor_BlackForest, Eikthyrnir, Crypt2, StartTemple; a part of the name matches.")]
        public object FindLocations(
            [ToolParameter(Description = "Part of the place name (empty = list the types)")] string name = "",
            [ToolParameter(Description = "How many to return (default 5)")] int max = 5)
        {
            return MainThreadDispatcher.RunTool(() =>
            {
                if (ZoneSystem.instance == null) return new { success = false, error = "Not in a world." };
                var all = ZoneSystem.instance.GetLocationList().ToList();
                string NameOf(ZoneSystem.LocationInstance l) => l.m_location?.m_prefabName ?? l.m_location?.m_name ?? "?";
                if (string.IsNullOrWhiteSpace(name))
                {
                    var types = all.GroupBy(NameOf).OrderBy(g => g.Key).Select(g => new { name = g.Key, count = g.Count() }).ToList();
                    return (object)new { success = true, total = all.Count, types };
                }
                var from = Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : Vector3.zero;
                var matches = all.Where(l => NameOf(l).IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                    .Select(l => new { l, dist = Vector2.Distance(new Vector2(l.m_position.x, l.m_position.z), new Vector2(from.x, from.z)) })
                    .OrderBy(m => m.dist).ToList();
                if (matches.Count == 0)
                    return new { success = false, error = $"No place matching '{name}'. Call with no name to list the types." };
                return new
                {
                    success = true,
                    found = matches.Count,
                    nearest = matches.Take(Math.Max(1, max)).Select(m => new
                    {
                        name = NameOf(m.l),
                        position = Vec(m.l.m_position),
                        distance = Math.Round(m.dist),
                        biome = m.l.m_location?.m_biome.ToString(),
                        placed = m.l.m_placed
                    }).ToList()
                };
            });
        }

        [Tool("world/spawn_creature", Description = "(cheat) Spawn creatures (prefab name, e.g. Greyling, Troll, Boar) in front of the player. Returns their instanceIds; remove them again with world/remove_objects.")]
        public object SpawnCreature(
            [ToolParameter(Description = "Creature prefab name (znetscene/search_prefabs to look one up)")] string name,
            [ToolParameter(Description = "How many (default 1, max 20)")] int amount = 1,
            [ToolParameter(Description = "Star level: 1 = normal, 2 = one star, 3 = two stars")] int level = 1,
            [ToolParameter(Description = "Distance in front of the player in metres (default 5)")] float distance = 5f,
            [ToolParameter(Description = "Spawn tamed (default false)")] bool tamed = false)
        {
            return MainThreadDispatcher.RunTool(() =>
            {
                var player = Player.m_localPlayer;
                if (player == null || ZNetScene.instance == null) return new { success = false, error = "Not in a world." };
                var prefab = ZNetScene.instance.GetPrefab(name);
                if (prefab == null) return new { success = false, error = $"No prefab '{name}'. Use znetscene/search_prefabs." };
                if (prefab.GetComponent<Character>() == null)
                    return new { success = false, error = $"'{name}' is not a creature. For items use player/give_item." };

                amount = Mathf.Clamp(amount, 1, 20);
                var center = player.transform.position + player.transform.forward * distance;
                var spawned = new List<object>();
                for (int i = 0; i < amount; i++)
                {
                    var offset = amount == 1 ? Vector3.zero : Quaternion.Euler(0, 360f * i / amount, 0) * Vector3.forward * 1.5f;
                    var pos = center + offset;
                    if (ZoneSystem.instance != null && ZoneSystem.instance.GetSolidHeight(pos, out float h)) pos.y = h + 0.2f;
                    var go = UnityEngine.Object.Instantiate(prefab, pos, Quaternion.LookRotation(-player.transform.forward));
                    var character = go.GetComponent<Character>();
                    if (level > 1) character.SetLevel(Mathf.Clamp(level, 1, 10));
                    if (tamed) character.SetTamed(true);
                    spawned.Add(new { instanceId = go.GetInstanceID(), position = Vec(pos) });
                }
                Log($"world/spawn_creature: {amount} x {name} (level {level}{(tamed ? ", tamed" : "")})");
                return new { success = true, name, level, spawned };
            });
        }

        [Tool("world/remove_objects", Description = "(cheat) Remove objects from the world by instanceId (creatures spawned for a test, dropped items, ...). Never removes the player.")]
        public object RemoveObjects(
            [ToolParameter(Description = "instanceIds as a JSON array, e.g. [123, 456]")] string ids)
        {
            return MainThreadDispatcher.RunTool(() =>
            {
                List<int> list;
                try { list = Newtonsoft.Json.JsonConvert.DeserializeObject<List<int>>(ids) ?? new List<int>(); }
                catch (Exception ex) { return new { success = false, error = "ids is not a JSON array of numbers: " + ex.Message }; }
                var wanted = new HashSet<int>(list);
                int removed = 0;
                foreach (var view in UnityEngine.Object.FindObjectsByType<ZNetView>(FindObjectsSortMode.None))
                {
                    if (view == null || !wanted.Contains(view.gameObject.GetInstanceID())) continue;
                    if (view.GetComponent<Player>() != null) continue;
                    ZNetScene.instance.Destroy(view.gameObject);
                    removed++;
                }
                Log($"world/remove_objects: removed {removed} of {list.Count}");
                return new { success = true, removed, notFound = list.Count - removed };
            });
        }

        [Tool("env/status", Description = "Time of day (hour 0-24, day number), current weather, whether time or weather are pinned, and the weather names that can be set.")]
        public object EnvStatus()
        {
            return MainThreadDispatcher.RunTool(() =>
            {
                var env = EnvMan.instance;
                if (env == null) return new { success = false, error = "Not in a world." };
                var current = env.GetCurrentEnvironment();
                return new
                {
                    success = true,
                    hour = Math.Round(env.GetDayFraction() * 24f, 2),
                    day = env.GetDay(),
                    night = EnvMan.IsNight(),
                    weather = current != null ? current.m_name : null,
                    timePinned = env.m_debugTimeOfDay,
                    weatherPinned = string.IsNullOrEmpty(env.m_debugEnv) ? null : env.m_debugEnv,
                    weathers = env.m_environments.Select(e => e.m_name).Distinct().OrderBy(n => n).ToList()
                };
            });
        }

        [Tool("env/set_time", Description = "(cheat) Set the time of day. By default the clock jumps forward to that hour and keeps running (like sleeping). pin=true holds it there for repeatable tests (shown time only; pin=false with hour -1 releases it).")]
        public object SetTime(
            [ToolParameter(Description = "Hour 0-24 (6 = sunrise, 12 = noon, 22 = night); -1 with pin=false just releases a pinned time")] float hour,
            [ToolParameter(Description = "Hold the time there (default false)")] bool pin = false)
        {
            return MainThreadDispatcher.RunTool(() =>
            {
                var env = EnvMan.instance;
                if (env == null || ZNet.instance == null) return new { success = false, error = "Not in a world." };
                if (hour < 0)
                {
                    env.m_debugTimeOfDay = false;
                    Log("env/set_time: time released");
                    return new { success = true, pinned = false, hour = Math.Round(env.GetDayFraction() * 24f, 2) };
                }
                float shown = Mathf.Repeat(hour, 24f) / 24f;
                if (pin)
                {
                    env.m_debugTimeOfDay = true;
                    env.m_debugTime = shown;
                }
                else
                {
                    env.m_debugTimeOfDay = false;
                    double len = env.m_dayLengthSec;
                    double now = ZNet.instance.GetTimeSeconds();
                    double target = env.GetDay(now) * len + RawFraction(shown) * len;
                    if (target <= now) target += len;    // only forward: going back in time confuses saved timers
                    ZNet.instance.SetNetTime(target);
                }
                Log($"env/set_time: {hour:F1}h{(pin ? " (pinned)" : "")}");
                return new { success = true, pinned = pin, hour = Math.Round(shown * 24f, 2) };
            });
        }

        // The sky clock is rescaled so day and night look right (EnvMan.RescaleDayFraction); this is its inverse.
        private static double RawFraction(float shown)
        {
            if (shown >= 0.25f && shown <= 0.75f) return 0.15 + (shown - 0.25) / 0.5 * 0.7;
            if (shown < 0.25f) return shown / 0.25 * 0.15;
            return 0.85 + (shown - 0.75) / 0.25 * 0.15;
        }

        [Tool("env/set_weather", Description = "(cheat) Force a weather (name from env/status, e.g. Clear, Rain, ThunderStorm, Snow, Misty). Empty name goes back to normal weather.")]
        public object SetWeather(
            [ToolParameter(Description = "Weather name; empty = normal weather again")] string name = "")
        {
            return MainThreadDispatcher.RunTool(() =>
            {
                var env = EnvMan.instance;
                if (env == null) return new { success = false, error = "Not in a world." };
                if (name.Length > 0)
                {
                    var match = env.m_environments.FirstOrDefault(e => string.Equals(e.m_name, name, StringComparison.OrdinalIgnoreCase));
                    if (match == null)
                        return new { success = false, error = $"No weather '{name}'. Have: {string.Join(", ", env.m_environments.Select(e => e.m_name).Distinct())}" };
                    name = match.m_name;
                }
                env.m_debugEnv = name;
                Log($"env/set_weather: {(name.Length == 0 ? "normal" : name)}");
                return new { success = true, weather = name.Length == 0 ? "normal" : name };
            });
        }

        [Tool("world/save", Description = "Save the world and characters now (like the 'save' console command). Returns once the save has been started; the game shows 'World saved' when done.")]
        public object Save()
        {
            return MainThreadDispatcher.RunTool(() =>
            {
                if (ZNet.instance == null || Player.m_localPlayer == null) return new { success = false, error = "Not in a world." };
                ZNet.instance.SaveWorldAndPlayerProfiles();
                Log("world/save");
                return new { success = true };
            });
        }

        [Tool("player/look_direction", Description = "Turn the player and camera to a compass heading and tilt, e.g. to aim a screenshot. Heading: 0 = north (+Z), 90 = east (+X). Tilt: 0 = level, positive = up, negative = down.")]
        public object LookDirection(
            [ToolParameter(Description = "Heading in degrees (0 = north, 90 = east)")] float heading,
            [ToolParameter(Description = "Tilt in degrees (-80 to 80, default 0)")] float tilt = 0f)
        {
            return MainThreadDispatcher.RunTool(() =>
            {
                var player = Player.m_localPlayer;
                if (player == null) return new { success = false, error = "Not in a world." };
                PlayerLook.Set(player, heading, tilt);
                return new { success = true, heading, tilt = Mathf.Clamp(tilt, -80f, 80f) };
            });
        }

        private static object? Pos(Player? player) => player == null ? null : Vec(player.transform.position);

        private static object Vec(Vector3 v) => new { x = Math.Round(v.x, 1), y = Math.Round(v.y, 1), z = Math.Round(v.z, 1) };
    }
}
