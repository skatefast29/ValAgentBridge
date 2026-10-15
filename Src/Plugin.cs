using System;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using Lib.GAB;
using Lib.GAB.Server;
using HarmonyLib;
using Lib.GAB.Tools;
using Newtonsoft.Json;
using ValAgentBridge.Tools;

namespace ValAgentBridge
{
    /// <summary>
    /// GABP bridge configuration from environment variables or bridge.json
    /// </summary>
    public class BridgeConfig
    {
        [JsonProperty("port")]
        public int Port { get; set; }
        
        [JsonProperty("token")]
        public string Token { get; set; } = "";
        
        [JsonProperty("gameId")]
        public string GameId { get; set; } = "valheim";
    }

    [BepInPlugin(ModGUID, ModName, ModVersion)]
    public class ValAgentBridgePlugin : BaseUnityPlugin
    {
        private const string ModName = "ValAgentBridge";
        private const string ModVersion = "1.6.0";
        private const string Author = "BarryWhite";
        private const string ModGUID = Author + "." + ModName;

        public static readonly ManualLogSource ModLogger = BepInEx.Logging.Logger.CreateLogSource(ModName);

        private GabpServer? _server;
        private Harmony? _harmony;
        private bool _bridgeUp;

        /// <summary>The live tool registry, for tools that call other tools (test/run).</summary>
        public static IToolRegistry? Tools { get; private set; }

        public void Start()
        {
            ModLogger.LogInfo($"{ModName} v{ModVersion} is loading...");

            // Launched normally (e.g. from Gale) rather than by GABS: stay off quietly.
            // No patches, no listener, no per-frame work: safe to leave installed in a profile used for measurements.
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GABP_SERVER_PORT")) &&
                string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GABS_BRIDGE_PATH")))
            {
                ModLogger.LogInfo("Not started by GABS, so the bridge stays off.");
                return;
            }

            try
            {
                // Log lines and on-screen messages are kept in memory for logs/read, ui/messages and test reports.
                _harmony = new Harmony(ModGUID);
                var recorderHarmony = _harmony;
                Optional("Log and message recording", () => Recorder.Install(recorderHarmony));

                // Read the bridge config (following GABP docs pattern)
                var config = ReadBridgeConfig();
                ModLogger.LogInfo($"GABP config loaded - GameId: {config.GameId}, Port: {config.Port}");

                // Start GABP server using external config (port/token from GABS)
                _server = Lib.GAB.Gabp.CreateServerWithExternalConfig(
                    "Valheim",
                    ModVersion,
                    config.Port,
                    config.Token,
                    config.GameId
                );

                // Tool classes, one per area (player, world, mods, debug, ...)
                ToolGuard.RegisterChecked(_server.Tools, new PlayerTools());
                ToolGuard.RegisterChecked(_server.Tools, new ZNetSceneTools());
                ToolGuard.RegisterChecked(_server.Tools, new TerminalTools());
                ToolGuard.RegisterChecked(_server.Tools, new WorldTools());
                ToolGuard.RegisterChecked(_server.Tools, new ModTools());
                ToolGuard.RegisterChecked(_server.Tools, new SceneTools());
                ToolGuard.RegisterChecked(_server.Tools, new ScreenTools());
                ToolGuard.RegisterChecked(_server.Tools, new LogTools());
                ToolGuard.RegisterChecked(_server.Tools, new UiTools());
                ToolGuard.RegisterChecked(_server.Tools, new TimeTools());
                ToolGuard.RegisterChecked(_server.Tools, new EditTools());
                ToolGuard.RegisterChecked(_server.Tools, new WatchTools());
                ToolGuard.RegisterChecked(_server.Tools, new TestTools());
                ToolGuard.RegisterChecked(_server.Tools, new ItemTools());
                ToolGuard.RegisterChecked(_server.Tools, new ControlTools());
                ToolGuard.RegisterChecked(_server.Tools, new InputTools());
                // Commands other mods declare with their own [BridgeTool] attribute (after ours, so names can't clash).
                // Optional, like the extras below: a failure is logged and the bridge starts without it.
                Optional("Commands from other mods", () => ModCommands.RegisterAll(_server.Tools));
                Tools = _server.Tools;

                // Register event channels
                _server.Events.RegisterChannel("player/death", "Player death events");
                _server.Events.RegisterChannel("player/health_changed", "Player health change events");
                var harmony = _harmony;
                var events = _server.Events;
                Optional("Player events", () => PlayerEvents.Install(harmony, events));
                _bridgeUp = true;

                // Ensure manager singletons are created on the main thread
                MainThreadDispatcher.Init();
                var _ = NavigationManager.Instance;
                var ___ = MovementManager.Instance;

                // Start listening for GABS connections
                _server.StartAsync().ContinueWith(task =>
                {
                    if (task.IsFaulted)
                    {
                        ModLogger.LogError($"GABP server failed to start: {task.Exception?.InnerException?.Message}");
                    }
                    else
                    {
                        ModLogger.LogInfo($"GABP server listening on 127.0.0.1:{_server.Port}");
                        ModLogger.LogInfo($"{ModName} loaded successfully!");
                    }
                });
            }
            catch (Exception ex)
            {
                ModLogger.LogError($"Failed to initialize GABP server: {ex.Message}");
                ModLogger.LogError($"Stack trace: {ex.StackTrace}");
            }
        }

        /// <summary>Runs a startup step the bridge can do without; a failure is one log line, never the whole bridge.</summary>
        private static void Optional(string what, Action step)
        {
            try { step(); }
            catch (Exception ex)
            {
                var e = ex.GetBaseException();
                ModLogger.LogWarning($"{what} unavailable ({e.GetType().Name}: {e.Message}); the bridge runs without it.");
            }
        }

        /// <summary>
        /// Read GABP server configuration following the priority from GABP docs:
        /// 1. Environment variables (recommended)
        /// 2. Bridge file path from GABS_BRIDGE_PATH env var
        /// </summary>
        private BridgeConfig ReadBridgeConfig()
        {
            // Method 1: Use environment variables (recommended)
            var gameId = Environment.GetEnvironmentVariable("GABS_GAME_ID");
            var portStr = Environment.GetEnvironmentVariable("GABP_SERVER_PORT");
            var token = Environment.GetEnvironmentVariable("GABP_TOKEN");

            if (!string.IsNullOrEmpty(portStr) && !string.IsNullOrEmpty(token) &&
                int.TryParse(portStr, out int port))
            {
                ModLogger.LogInfo("Config loaded from environment variables");
                return new BridgeConfig
                {
                    Port = port,
                    Token = token,
                    GameId = gameId ?? "valheim"
                };
            }

            // Method 2: Use bridge file path (fallback for debugging)
            var bridgePath = Environment.GetEnvironmentVariable("GABS_BRIDGE_PATH");
            if (!string.IsNullOrEmpty(bridgePath) && File.Exists(bridgePath))
            {
                ModLogger.LogInfo($"Config loaded from bridge file: {bridgePath}");
                var json = File.ReadAllText(bridgePath);
                return JsonConvert.DeserializeObject<BridgeConfig>(json)
                    ?? throw new Exception("Failed to parse bridge.json");
            }

            // No ~/.gabs/<game>/bridge.json fallback: that file outlives every GABS launch, so a normal launch from the
            // mod manager would switch the bridge on with a stale port and token.
            throw new Exception(
                "GABP server config not found. Start the game through GABS. " +
                "Expected: GABP_SERVER_PORT and GABP_TOKEN environment variables, or GABS_BRIDGE_PATH.");
        }

        private void Update()
        {
            if (_bridgeUp) PlayerEvents.Tick();
        }

        private void OnDestroy()
        {
            ModLogger.LogInfo($"{ModName} is unloading...");

            if (_server != null)
            {
                try
                {
                    _server.StopAsync().ContinueWith(task =>
                    {
                        if (task.IsFaulted)
                        {
                            ModLogger.LogError($"Error stopping GABP server: {task.Exception?.InnerException?.Message}");
                        }
                        else
                        {
                            ModLogger.LogInfo("GABP server stopped");
                        }
                    });
                    _server.Dispose();
                }
                catch (Exception ex)
                {
                    ModLogger.LogError($"Error disposing GABP server: {ex.Message}");
                }
            }
        }
    }
}
