using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using HarmonyLib;
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

        // The game keeps the height it is given (it only checks for a floor somewhere below), so without a height
        // the player would land at their old height: inside a hill (the game then gives up), or high above a valley.
        // Loaded ground is looked up first; far targets go in from high up and are put on the ground once their area
        // has loaded.
        private const float HighUp = 800f;
        // Below the dungeon interiors (around y 5000 above their entrances) and above any terrain.
        private const float RayStart = 1000f;
        private static readonly int SolidMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain");
        private static AccessTools.FieldRef<Character, float>? _maxAirAltitude;

        /// <summary>Main thread. The highest surface to stand on at x/z: ground, a rock, a roof or floor (not tree
        /// tops, creatures or loose items), or the water surface. False while nothing there is loaded.</summary>
        private static bool TryLandingHeight(float x, float z, out float y)
        {
            y = 0f;
            var zones = ZoneSystem.instance;
            if (zones == null) return false;
            var hits = Physics.RaycastAll(new Vector3(x, RayStart, z), Vector3.down, RayStart + 100f, SolidMask, QueryTriggerInteraction.Ignore);
            foreach (var hit in hits.OrderBy(h => h.distance))
            {
                if (hit.collider.attachedRigidbody != null || hit.collider.GetComponentInParent<TreeBase>() != null) continue;
                y = Mathf.Max(hit.point.y, zones.m_waterLevel) + 0.5f;
                return true;
            }
            return false;
        }

        [Tool("player/teleport", Description = "(cheat) Teleport the player to a world position. Without a height the player lands on the highest surface there: ground, a rock, a roof (not tree tops), or the water surface; give the height to land inside a building. Waits until the teleport has finished (far ones load the area first) and returns where the player ended up. Refuses while the owner is steering or on a moving ship, in a menu or just used keyboard/mouse, unless force=true.")]
        public object Teleport(
            [ToolParameter(Description = "X coordinate")] float x,
            [ToolParameter(Description = "Z coordinate")] float z,
            [ToolParameter(Description = "Height; leave out to land on top of whatever is there")] float y = float.NaN,
            [ToolParameter(Description = "Do it even while the owner is steering, in a menu or just used keyboard/mouse (ask them first)")] bool force = false)
        {
            string? problem;
            bool landLater = false;
            int abandoned = 0;    // set when the call gave up waiting: the queued teleport must not run later
            try
            {
                problem = MainThreadDispatcher.Run(() =>
                {
                    if (Interlocked.CompareExchange(ref abandoned, 0, 0) != 0) return "abandoned";
                    var player = Player.m_localPlayer;
                    if (player == null) return "No local player (not in a world).";
                    if (OwnerControl.Busy(player, force) is string busy)
                        return $"Not done: the owner is {busy}. Teleporting would take the character away from them. Ask the owner, then pass force=true to do it anyway.";
                    var from = player.transform.position;
                    float targetY = y;
                    if (float.IsNaN(y) && !TryLandingHeight(x, z, out targetY))
                    {
                        targetY = HighUp;
                        landLater = true;
                    }
                    var target = new Vector3(x, targetY, z);
                    bool distant = landLater || Vector3.Distance(new Vector3(from.x, 0, from.z), new Vector3(x, 0, z)) > 64f;
                    if (!player.TeleportTo(target, player.transform.rotation, distant))
                        return "The game refused (already teleporting, or within 2 s of the last teleport). Try again in a moment.";
                    Log($"player/teleport: ({from.x:F0}, {from.y:F0}, {from.z:F0}) -> ({x:F0}, {(landLater ? "ground" : targetY.ToString("F0"))}, {z:F0})");
                    return null;
                }, 15000);
            }
            catch (TimeoutException)
            {
                Interlocked.Exchange(ref abandoned, 1);
                problem = "The game's main thread did not answer in time (loading screen or paused?); the teleport was cancelled.";
            }
            catch (Exception ex) { problem = ex.GetBaseException().Message; }
            if (problem != null) return new { success = false, error = problem };

            var clock = Stopwatch.StartNew();
            int landings = 0;
            while (clock.Elapsed.TotalSeconds < 40)
            {
                Thread.Sleep(500);
                object? done;
                try
                {
                    done = MainThreadDispatcher.Run(() =>
                    {
                        var player = Player.m_localPlayer;
                        if (player != null && player.IsTeleporting()) return null;
                        // Put the player on the ground, then check on the next round that they stayed there.
                        if (player != null && landLater && landings < 3 && TryLandingHeight(x, z, out var groundY)
                            && player.transform.position.y - groundY > 2f)
                        {
                            // Looked up first: if the game renamed it, nothing has moved yet.
                            _maxAirAltitude ??= AccessTools.FieldRefAccess<Character, float>("m_maxAirAltitude");
                            var landing = new Vector3(x, groundY, z);
                            player.transform.position = landing;
                            var body = player.GetComponent<Rigidbody>();
                            if (body != null)
                            {
                                body.position = landing;    // otherwise physics puts the player back up high
                                body.linearVelocity = Vector3.zero;
                            }
                            _maxAirAltitude(player) = groundY;    // no fall damage from the drop-in height
                            landings++;
                            return null;
                        }
                        return Pos(player) ?? "no player";
                    }, 5000);
                }
                catch (TimeoutException) { continue; }    // area loading: keep waiting
                catch (Exception ex)
                {
                    return new { success = false, error = "Teleported, but landing failed: " + ex.GetBaseException().Message, position = (object?)null };
                }
                if (done != null)
                    return new { success = true, position = done, seconds = Math.Round(clock.Elapsed.TotalSeconds, 1) };
            }
            return new { success = false, error = "Still teleporting after 40 s (area slow to load). Check world/status." };
        }

        [Tool("world/clear_hostiles", Description = "(cheat) Remove creatures hostile to the player within a radius, so a test isn't interrupted by attacks. Leaves the player untouched (no god mode). Never removes players or tamed animals; bosses only with includeBosses. Returns what was removed.")]
        public object ClearHostiles(
            [ToolParameter(Description = "Radius in metres around the player (default 50, max 200)")] float radius = 50f,
            [ToolParameter(Description = "Also remove bosses (default false)")] bool includeBosses = false) =>
            ClearNow(radius, includeBosses);

        // Clearing that keeps going between calls (world/keep_clear). Runs on a timer thread, clears on the main thread.
        private static readonly object KeepClearGate = new object();
        private static System.Threading.Timer? _keepClear;
        private static float _keepClearRadius;
        private static double _keepClearEvery;
        private static bool _keepClearBosses;
        private static int _keepClearRemoved;

        public static bool KeepClearOn { get; private set; }

        /// <summary>True once world/keep_clear has been called this session; until then world/enter reminds the agent
        /// to offer it to the owner.</summary>
        public static bool KeepClearChosen { get; private set; }

        public static object KeepClearState()
        {
            lock (KeepClearGate)
                return KeepClearOn
                    ? new { on = true, radius = _keepClearRadius, everySeconds = _keepClearEvery, includeBosses = _keepClearBosses, removedSoFar = _keepClearRemoved }
                    : (object)new { on = false, radius = 0f, everySeconds = 0.0, includeBosses = false, removedSoFar = _keepClearRemoved };
        }

        [Tool("world/keep_clear", Description = "(cheat) Removes creatures hostile to the player every few seconds, between calls and test scripts, so one can't kill the character mid-test. Off by default. At the start of testing, offer it to the owner and turn it on only if they want it; leave it off when the test is about hostile creatures (spawning, combat, AI, raids) or measures performance. Same rules as world/clear_hostiles. Returns the state.")]
        public object KeepClear(
            [ToolParameter(Description = "true = keep clearing, false = stop")] bool on,
            [ToolParameter(Description = "Radius in metres around the player (default 50, max 200)")] float radius = 50f,
            [ToolParameter(Description = "Seconds between clearings (default 5, 2 to 600)")] double every = 5,
            [ToolParameter(Description = "Also remove bosses (default false)")] bool includeBosses = false)
        {
            lock (KeepClearGate)
            {
                KeepClearChosen = true;
                Switch(on, radius, every, includeBosses);
            }
            return new { success = true, keepClear = KeepClearState() };
        }

        // Under KeepClearGate.
        private static void Switch(bool on, float radius, double every, bool includeBosses)
        {
            {
                _keepClear?.Dispose();
                _keepClear = null;
                KeepClearOn = on;
                if (on)
                {
                    _keepClearRadius = Mathf.Clamp(radius, 1f, 200f);
                    _keepClearEvery = Math.Max(2, Math.Min(600, every));
                    _keepClearBosses = includeBosses;
                    var period = TimeSpan.FromSeconds(_keepClearEvery);
                    _keepClear = new System.Threading.Timer(_ => KeepClearTick(), null, TimeSpan.Zero, period);
                }
                Log(on ? $"world/keep_clear: on, hostile creatures within {_keepClearRadius:F0} m are removed every {_keepClearEvery:F0} s" : "world/keep_clear: off");
            }
        }

        private static int _keepClearBusy;

        private static void KeepClearTick()
        {
            // While the game is busy (loading) a tick can wait up to 5 s; the next one is skipped, not stacked.
            if (Interlocked.Exchange(ref _keepClearBusy, 1) == 1) return;
            try { KeepClearOnce(); }
            finally { Interlocked.Exchange(ref _keepClearBusy, 0); }
        }

        private static void KeepClearOnce()
        {
            float radius;
            bool bosses;
            lock (KeepClearGate)
            {
                if (!KeepClearOn) return;
                radius = _keepClearRadius;
                bosses = _keepClearBosses;
            }
            // Quietly skipped while not in a world or while the main thread is busy (loading).
            try
            {
                var removed = MainThreadDispatcher.Run(() => Player.m_localPlayer == null ? 0 : ClearOnMainThread(radius, bosses).total, 5000);
                if (removed > 0) lock (KeepClearGate) _keepClearRemoved += removed;
            }
            catch (Exception) { }
        }

        private static object ClearNow(float radius, bool includeBosses)
        {
            return MainThreadDispatcher.RunTool(() =>
            {
                if (Player.m_localPlayer == null || ZNetScene.instance == null) return new { success = false, error = "Not in a world." };
                var (total, kinds, bossesLeft) = ClearOnMainThread(radius, includeBosses);
                return new { success = true, removed = total, kinds, bossesLeft };
            });
        }

        private static (int total, Dictionary<string, int> kinds, int bossesLeft) ClearOnMainThread(float radius, bool includeBosses)
        {
            var removed = new Dictionary<string, int>();
            int bossesLeft = 0;
            var player = Player.m_localPlayer;
            if (player == null || ZNetScene.instance == null) return (0, removed, 0);
            var near = new List<Character>();
            Character.GetCharactersInRange(player.transform.position, Mathf.Clamp(radius, 1f, 200f), near);
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
            return (total, removed, bossesLeft);
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
            [ToolParameter(Description = "instanceIds, e.g. [123, 456] (the same as a JSON string also works)")] int[] ids)
        {
            return MainThreadDispatcher.RunTool(() =>
            {
                var list = (ids ?? new int[0]).ToList();
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

        [Tool("player/look_direction", Description = "Turn the player and camera to a compass heading and tilt, e.g. to aim a screenshot. Heading: 0 = north (+Z), 90 = east (+X). Tilt: 0 = level, positive = up, negative = down. Refuses while the owner is in control unless force=true.")]
        public object LookDirection(
            [ToolParameter(Description = "Heading in degrees (0 = north, 90 = east)")] float heading,
            [ToolParameter(Description = "Tilt in degrees (-80 to 80, default 0)")] float tilt = 0f,
            [ToolParameter(Description = "Do it even while the owner is steering, in a menu or just used keyboard/mouse (ask them first)")] bool force = false)
        {
            return MainThreadDispatcher.RunTool(() =>
            {
                var player = Player.m_localPlayer;
                if (player == null) return new { success = false, error = "Not in a world." };
                if (OwnerControl.Refusal(player, force, "Turning") is object refused) return refused;
                PlayerLook.Set(player, heading, tilt);
                return new { success = true, heading, tilt = Mathf.Clamp(tilt, -80f, 80f) };
            });
        }

        private static object? Pos(Player? player) => player == null ? null : Vec(player.transform.position);

        private static object Vec(Vector3 v) => new { x = Math.Round(v.x, 1), y = Math.Round(v.y, 1), z = Math.Round(v.z, 1) };
    }
}
