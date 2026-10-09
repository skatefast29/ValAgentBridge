using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Lib.GAB.Tools;

namespace ValBridgeServer.Tools
{
    public class TerminalTools
    {
        private static readonly AccessTools.FieldRef<Dictionary<string, Terminal.ConsoleCommand>> Commands =
            AccessTools.StaticFieldRefAccess<Dictionary<string, Terminal.ConsoleCommand>>(AccessTools.Field(typeof(Terminal), "commands"));

        [Tool("console/run_command", Description = "Run a Valheim console command (e.g. 'spawn Boar 1 1', 'tod 0.5') and return what the console printed. Cheat commands (spawn, heal, god...) only run with allowCheats=true, which turns devcommands on just for that command; the game then marks the character as having used cheats (no achievements).")]
        public object RunCommand(
            [ToolParameter(Description = "The console command to execute")] string command,
            [ToolParameter(Description = "Allow a cheat command to run even though devcommands is off (marks the character as cheated)")] bool allowCheats = false)
        {
            if (string.IsNullOrWhiteSpace(command))
                return new { success = false, error = "command is required", output = new List<string>() };

            return MainThreadDispatcher.RunTool(() =>
            {
                if (Console.instance == null)
                    return new { success = false, error = "The console isn't available (still loading?)", output = new List<string>() };

                var name = command.Trim().Split(' ')[0].ToLowerInvariant();
                if (!Commands().TryGetValue(name, out var cmd))
                    return new { success = false, error = $"'{name}' is not a console command", output = new List<string>() };

                bool needCheats = cmd.IsCheat && !Console.instance.IsCheatsEnabled();
                if (needCheats && !allowCheats)
                    return new { success = false, error = $"'{name}' is a cheat command and devcommands is off. Run it with allowCheats=true (marks this character as having used cheats), or use player/give_item for items.", output = new List<string>() };

                // Cheats are switched on for this one command only (needCheats means they were off).
                if (needCheats) Terminal.m_cheat = true;
                List<string> output;
                try { output = Recorder.CaptureConsole(() => Console.instance.TryRunCommand(command, silentFail: false, skipAllowedCheck: true)); }
                finally { if (needCheats) Terminal.m_cheat = false; }

                var ran = !output.Any(l => l.IndexOf("is not valid in the current context", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                          l.IndexOf("is not a recognized command", StringComparison.OrdinalIgnoreCase) >= 0);
                ValBridgeServerPlugin.ModLogger.LogInfo($"console/run_command: {command}{(needCheats ? " (cheats on for this command)" : "")} -> {(ran ? "ran" : "refused")}");
                return new
                {
                    success = ran,
                    command,
                    cheat = cmd.IsCheat,
                    error = ran ? null : (output.FirstOrDefault() ?? $"'{name}' is not valid in the current context."),
                    output
                };
            });
        }
    }
}
