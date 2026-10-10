using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using HarmonyLib;
using Lib.GAB.Tools;
using UnityEngine.SceneManagement;

namespace ValAgentBridge.Tools
{
    // Main menu -> world and back, so a test can run without anyone clicking through the menus.
    // Drives FejdStartup the same way its buttons do (OnStartGame -> OnCharacterStart -> OnWorldStart).
    public class WorldTools
    {
        private static readonly AccessTools.FieldRef<FejdStartup, bool> StartingWorld =
            AccessTools.FieldRefAccess<FejdStartup, bool>("m_startingWorld");
        private static readonly AccessTools.FieldRef<FejdStartup, List<World>> MenuWorlds =
            AccessTools.FieldRefAccess<FejdStartup, List<World>>("m_worlds");
        private static readonly Action<FejdStartup, string> SetSelectedProfile =
            AccessTools.MethodDelegate<Action<FejdStartup, string>>(AccessTools.Method(typeof(FejdStartup), "SetSelectedProfile"));
        private static readonly Action<FejdStartup, int, bool> SetSelectedWorld =
            AccessTools.MethodDelegate<Action<FejdStartup, int, bool>>(AccessTools.Method(typeof(FejdStartup), "SetSelectedWorld"));

        // How long the game has been in the phase world/status last saw.
        private static readonly object PhaseGate = new object();
        private static string _lastPhase = "";
        private static readonly Stopwatch PhaseClock = Stopwatch.StartNew();

        // Set while a world world/enter just created is loading: generating a new world takes far longer.
        private static volatile bool _generating;

        /// <summary>How long without a frame is worth a hint, how long ends a wait as frozen, and how long loading
        /// (frames still drawing) looks stuck. In a world a freeze should be short; loading allows more, and
        /// generating a new world much more.</summary>
        private static (long hintMs, long giveUpMs, double stuckLoadingS) Limits(string phase)
        {
            if (phase == "in_world") return (5000, 90000, 0);
            if (phase == "main_menu") return (10000, 90000, 0);
            return _generating ? (180000, 300000, 300) : (45000, 90000, 60);
        }

        private sealed class Snapshot
        {
            public string phase = "loading";
            public string? character, world, scene, popup;
        }

        [Tool("world/status", Description = "Where the game is: main_menu, loading or in_world (plus character and world), how long it has been in that phase, how long ago the game last drew a frame (frameAgeMs: seconds-long means frozen or a heavy load step), fps, the open popup's text if one is blocking the menu, and a hint when something looks stuck.")]
        public object Status()
        {
            var s = Read();
            double inPhase;
            lock (PhaseGate)
            {
                if (s.phase != _lastPhase) { _lastPhase = s.phase; PhaseClock.Restart(); }
                inPhase = PhaseClock.Elapsed.TotalSeconds;
            }
            var frameAge = MainThreadDispatcher.FrameAgeMs;
            return new
            {
                success = true,
                s.phase,
                s.character,
                s.world,
                s.scene,
                secondsInPhase = Math.Round(inPhase, 1),
                frameAgeMs = frameAge,
                fps = MainThreadDispatcher.FrameMs > 0 ? Math.Round(1000.0 / MainThreadDispatcher.FrameMs) : 0,
                s.popup,
                hint = Hint(s, inPhase, frameAge),
                keepClear = ControlTools.KeepClearState()
            };
        }

        private static string? Hint(Snapshot s, double inPhase, long frameAge)
        {
            if (s.popup != null)
                return "A popup is open and may be waiting for a click: use ui/list and ui/click, or ask the owner.";
            // A hint only, never a failure. Pauses while loading are normal, so loading gets more slack.
            var limits = Limits(s.phase);
            if (frameAge > limits.hintMs)
                return s.phase == "in_world"
                    ? $"No frame drawn for {frameAge / 1000}s while in the world: the game is hitching or frozen. Check LogOutput.log."
                    : $"No frame drawn for {frameAge / 1000}s while {(s.phase == "loading" && _generating ? "generating a new world" : s.phase)}. Pauses here are normal; if this keeps growing, check LogOutput.log and ask the owner to look at the game.";
            if (s.phase == "loading" && inPhase > limits.stuckLoadingS && frameAge >= 0 && frameAge < 2000)
                return $"Loading for {inPhase:0}s while frames still draw: possibly stuck (black screen, hidden prompt). Take screen/capture.";
            return null;
        }

        /// <summary>Phase, names and popup, read on the main thread; while a scene loads it can't answer, which means loading.</summary>
        private static Snapshot Read()
        {
            try
            {
                return MainThreadDispatcher.Run(() =>
                {
                    var s = new Snapshot { scene = SceneManager.GetActiveScene().name, popup = OpenPopup() };
                    if (Player.m_localPlayer != null)
                    {
                        _generating = false;
                        var profile = Game.instance != null ? Game.instance.GetPlayerProfile() : null;
                        s.phase = "in_world";
                        s.character = profile?.GetName();
                        s.world = ZNet.instance != null ? ZNet.instance.GetWorldName() : null;
                    }
                    else
                    {
                        var fs = FejdStartup.instance;
                        if (fs != null && !StartingWorld(fs)) s.phase = "main_menu";
                    }
                    return s;
                }, 3000);
            }
            catch (TimeoutException)
            {
                return new Snapshot();
            }
        }

        private static string? OpenPopup()
        {
            if (!UnifiedPopup.IsVisible()) return null;
            var (header, body) = UiTools.Popup();
            var text = string.Join(": ", new[] { header, body }.Where(t => !string.IsNullOrWhiteSpace(t)));
            return text.Length == 0 ? "(popup open)" : text;
        }

        [Tool("world/list_saves", Description = "List the characters and worlds that can be loaded, and which were used last.")]
        public object ListSaves()
        {
            return MainThreadDispatcher.RunTool(() => new
            {
                success = true,
                lastCharacter = PlatformPrefs.GetString("profile"),
                lastWorld = PlatformPrefs.GetString("world"),
                characters = SaveSystem.GetAllPlayerProfiles().Select(p => new { name = p.GetName(), file = p.GetFilename() }).ToList(),
                worlds = SaveSystem.GetWorldList().Select(w => new { name = w.m_name, problem = w.m_dataError == World.SaveDataError.None ? null : w.m_dataError.ToString() }).ToList()
            });
        }

        [Tool("world/enter", Description = "Get a character into a world in one call, from wherever the game is: waits for the main menu (right after games_start is fine), logs out first if a different world or character is loaded, optionally creates the character and/or world, loads, and returns once the player is in the world. Returns at once if already there. Gives up after `timeout` (default 180 s; 600 s when it creates a world, since generating one takes long), or early if the game draws no frame for 90 s (5 min while generating). Give the GABS call timeout 300, or 700 with create. Until world/keep_clear has been set this session, the answer includes askOwner: offer the owner hostile-creature clearing (off by default).")]
        public object Enter(
            [ToolParameter(Description = "Character name. Empty = the one in use, else the last one used.")] string character = "",
            [ToolParameter(Description = "World name. Empty = the one in use, else the last one used.")] string world = "",
            [ToolParameter(Description = "Create the character and/or world if they don't exist yet (new character: default look and starting items; new world: local save)")] bool create = false,
            [ToolParameter(Description = "Seed for a new world (empty = random)")] string seed = "",
            [ToolParameter(Description = "Give up after this many seconds (0 = 180, or 600 when a new world is created)")] int timeout = 0)
        {
            var clock = Stopwatch.StartNew();
            double limit = timeout > 0 ? timeout : 180;
            double Left() => Math.Max(1, limit - clock.Elapsed.TotalSeconds);
            var created = new List<string>();

            // 1. Wait for the menu or a world (the game may still be starting).
            var s = WaitFor(p => p != "loading", Left(), out var failure);
            if (failure != null) return failure;
            double toMenu = clock.Elapsed.TotalSeconds;

            // 2. Already where we want to be, or log out first.
            if (s.phase == "in_world")
            {
                bool sameChar = character.Length == 0 || string.Equals(character, s.character, StringComparison.OrdinalIgnoreCase);
                bool sameWorld = world.Length == 0 || string.Equals(world, s.world, StringComparison.OrdinalIgnoreCase);
                if (sameChar && sameWorld)
                    return new { success = true, alreadyThere = true, s.character, s.world, seconds = Math.Round(clock.Elapsed.TotalSeconds, 1), askOwner = AskOwner() };

                ValAgentBridgePlugin.ModLogger.LogInfo($"world/enter: logging out of {s.world} ({s.character}) first");
                var logout = MainThreadDispatcher.RunTool(() =>
                {
                    if (Game.instance != null) Game.instance.Logout();
                    return new { success = true };
                });
                s = WaitFor(p => p == "main_menu", Left(), out failure);
                if (failure != null) return failure;
            }

            double loadStart;
            try
            {
                // 3. Create what's missing, if asked.
                if (create)
                {
                    var problem = MainThreadDispatcher.Run(() => CreateMissing(character, world, seed, created), 20000);
                    if (problem != null) return new { success = false, error = problem };
                    if (created.Any(c => c.StartsWith("world")))
                    {
                        _generating = true;
                        if (timeout <= 0) limit = clock.Elapsed.TotalSeconds + 600;
                    }
                }

                // 4. Load and wait until in the world.
                var started = MainThreadDispatcher.Run(() => StartLoad(character, world), 20000);
                if (!started.ok) return new { success = false, error = started.error };
                loadStart = clock.Elapsed.TotalSeconds;
                s = WaitFor(p => p == "in_world", Left(), out failure);
                if (failure != null) return failure;
            }
            catch (Exception ex)
            {
                var e = ex.GetBaseException();
                return new { success = false, error = e is TimeoutException ? "The menu did not answer in time; check world/status." : $"{e.GetType().Name}: {e.Message}" };
            }
            finally
            {
                _generating = false;
            }

            return new
            {
                success = true,
                s.character,
                s.world,
                created,
                secondsToMenu = Math.Round(toMenu, 1),
                secondsLoading = Math.Round(clock.Elapsed.TotalSeconds - loadStart, 1),
                seconds = Math.Round(clock.Elapsed.TotalSeconds, 1),
                askOwner = AskOwner()
            };
        }

        private static string? AskOwner() => ControlTools.KeepClearChosen
            ? null
            : "Hostile creature clearing is off. Creatures can kill the character mid-test: ask the owner whether to turn it on (world/keep_clear on=true; it removes hostile creatures within 50 m every 5 s until turned off), unless the test is about hostile creatures. Call world/keep_clear with their answer (on=true or on=false) so this reminder stops.";

        /// <summary>Polls the phase once a second. On timeout <paramref name="failure"/> is the error answer, with
        /// what the game looked like (popup, frozen, screenshot).</summary>
        private static Snapshot WaitFor(Func<string, bool> done, double seconds, out object? failure)
        {
            failure = null;
            var clock = Stopwatch.StartNew();
            while (true)
            {
                var s = Read();
                if (done(s.phase)) return s;
                if (s.popup != null && s.phase == "main_menu")
                {
                    failure = new { success = false, error = $"A popup is blocking the menu: {s.popup}. Use ui/list and ui/click, or ask the owner.", s.phase };
                    return s;
                }
                var frameAge = MainThreadDispatcher.FrameAgeMs;
                // A long freeze ends the wait early; short ones while loading are normal and just waited out.
                bool frozen = frameAge > Limits(s.phase).giveUpMs;
                if (frozen || clock.Elapsed.TotalSeconds >= seconds)
                {
                    object? shot = null;
                    if (frameAge >= 0 && frameAge < 5000)
                    {
                        try { shot = new ScreenTools().Capture(960); } catch { }
                    }
                    failure = new
                    {
                        success = false,
                        error = frozen
                            ? $"The game has drawn no frame for {frameAge / 1000}s (still '{s.phase}'): frozen. Check LogOutput.log and ask the owner to look at the game."
                            : $"Timed out after {seconds:0}s; the game is still '{s.phase}'.",
                        s.phase,
                        frameAgeMs = frameAge,
                        hint = Hint(s, clock.Elapsed.TotalSeconds, frameAge),
                        screenshot = shot
                    };
                    return s;
                }
                Thread.Sleep(1000);
            }
        }

        /// <summary>Main thread. Creates the named character and/or world through the menu's own handlers.</summary>
        private static string? CreateMissing(string character, string world, string seed, List<string> created)
        {
            var fs = FejdStartup.instance;
            if (fs == null) return "Not at the main menu.";

            if (character.Length > 0 && !SaveSystem.GetAllPlayerProfiles().Any(p =>
                    string.Equals(p.GetName(), character, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(p.GetFilename(), character, StringComparison.OrdinalIgnoreCase)))
            {
                if (character.Length < 3 || character.Length > 15 || !character.All(char.IsLetter))
                    return $"Can't create character '{character}': the game wants 3-15 letters.";
                fs.OnStartGame();
                fs.OnCharacterNew();
                fs.m_csNewCharacterName.text = character;
                fs.OnNewCharacterDone(forceLocal: true);
                if (!PlayerProfile.HaveProfile(character.ToLowerInvariant()))
                    return $"The game did not create character '{character}' (name taken or not allowed?). Take screen/capture.";
                created.Add("character " + character);
                ValAgentBridgePlugin.ModLogger.LogInfo($"world/enter: created character {character}");
            }

            if (world.Length > 0 && !World.HaveWorld(world))
            {
                if (world.Length < 5)
                    return $"Can't create world '{world}': the game wants at least 5 characters.";
                fs.OnStartGame();
                fs.m_newWorldName.text = world;
                fs.m_newWorldSeed.text = seed.Length > 0 ? seed : World.GenerateSeed();
                fs.OnNewWorldDone(forceLocal: true);
                if (!World.HaveWorld(world))
                    return $"The game did not create world '{world}' (a popup may be open). Take screen/capture.";
                created.Add("world " + world);
                ValAgentBridgePlugin.ModLogger.LogInfo($"world/enter: created world {world} (seed {fs.m_newWorldSeed.text})");
            }
            return null;
        }

        [Tool("world/load", Description = "From the main menu, load a character into a world (single player, using the menu's current server options). Returns at once; world/enter does the same and waits until in the world.")]
        public object Load(
            [ToolParameter(Description = "Character name or file name. Empty = the last one used.")] string character = "",
            [ToolParameter(Description = "World name. Empty = the last one used.")] string world = "")
        {
            return MainThreadDispatcher.RunTool(() =>
            {
                var r = StartLoad(character, world);
                if (!r.ok) return new { success = false, error = r.error };
                return new { success = true, character = r.character, world = r.world, loading = true };
            });
        }

        /// <summary>Main thread. Starts loading through the menu's handlers.</summary>
        private static (bool ok, string? error, string? character, string? world) StartLoad(string character, string world)
        {
            var fs = FejdStartup.instance;
            if (Player.m_localPlayer != null || fs == null)
                return (false, "Not at the main menu. Use world/logout first, or world/enter.", null, null);
            if (StartingWorld(fs))
                return (false, "A world is already loading.", null, null);

            var profiles = SaveSystem.GetAllPlayerProfiles();
            var wantChar = string.IsNullOrEmpty(character) ? PlatformPrefs.GetString("profile") : character;
            var profile = profiles.FirstOrDefault(p => string.Equals(p.GetFilename(), wantChar, StringComparison.OrdinalIgnoreCase))
                          ?? profiles.FirstOrDefault(p => string.Equals(p.GetName(), wantChar, StringComparison.OrdinalIgnoreCase));
            if (profile == null)
                return (false, $"No character '{wantChar}'. Have: {string.Join(", ", profiles.Select(p => p.GetName()))} (world/enter with create=true makes one).", null, null);

            var worlds = SaveSystem.GetWorldList();
            var wantWorld = string.IsNullOrEmpty(world) ? PlatformPrefs.GetString("world") : world;
            var target = worlds.FirstOrDefault(w => string.Equals(w.m_name, wantWorld, StringComparison.OrdinalIgnoreCase));
            if (target == null)
                return (false, $"No world '{wantWorld}'. Have: {string.Join(", ", worlds.Select(w => w.m_name))} (world/enter with create=true makes one).", null, null);
            if (target.m_dataError != World.SaveDataError.None)
                return (false, $"World '{target.m_name}' has a save problem ({target.m_dataError}). Open it from the menu by hand.", null, null);

            fs.OnStartGame();
            SetSelectedProfile(fs, profile.GetFilename());
            fs.OnCharacterStart();

            var menuWorlds = MenuWorlds(fs);
            var index = menuWorlds == null ? -1 : menuWorlds.FindIndex(w => w.m_name == target.m_name);
            if (index < 0)
                return (false, $"World '{target.m_name}' is not in the menu's world list.", null, null);
            SetSelectedWorld(fs, index, true);
            fs.OnWorldStart();

            if (!StartingWorld(fs))
                return (false, "The menu did not start the world; a popup (cloud storage warning, login) is probably waiting. Check world/status or take a screenshot.", null, null);

            ValAgentBridgePlugin.ModLogger.LogInfo($"world/load: {profile.GetName()} into {target.m_name}");
            return (true, null, profile.GetName(), target.m_name);
        }

        [Tool("world/logout", Description = "Save and go back to the main menu (like Logout in the game menu).")]
        public object Logout()
        {
            return MainThreadDispatcher.RunTool(() =>
            {
                if (Game.instance == null || Player.m_localPlayer == null)
                    return new { success = false, error = "Not in a world." };
                Game.instance.Logout();
                return new { success = true, loggingOut = true };
            });
        }
    }
}
