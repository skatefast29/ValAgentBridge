using Lib.GAB.Tools;
using UnityEngine;

namespace ValAgentBridge.Tools
{
    public class LogTools
    {
        [Tool("logs/read", Description = "Recent game and mod log lines (everything BepInEx logs, Unity's own included), newest last. Pass the 'last' number from a previous call as 'since' to get only what is new.")]
        public object Read(
            [ToolParameter(Description = "Only lines after this number (0 = all kept, up to 3000)")] long since = 0,
            [ToolParameter(Description = "error, warning, info (default) or all")] string level = "info",
            [ToolParameter(Description = "Only lines whose text or source contains this (optional)")] string filter = "",
            [ToolParameter(Description = "Max lines (default 100)")] int limit = 100)
        {
            var entries = Recorder.LogSince(since, level, filter, Mathf.Clamp(limit, 1, 1000));
            return new { success = true, last = Recorder.LastLogSeq, count = entries.Count, entries };
        }

        [Tool("events/read", Description = "Player events the bridge recorded: player/death (where, biome) and player/health_changed (new health, change). Pass 'last' from a previous call as 'since' for only new ones.")]
        public object ReadEvents(
            [ToolParameter(Description = "Only events after this number (0 = all kept, up to 500)")] long since = 0,
            [ToolParameter(Description = "Only this channel, e.g. 'player/death' (optional)")] string channel = "",
            [ToolParameter(Description = "Max events (default 50)")] int limit = 50)
        {
            var list = Recorder.EventsSince(since, channel ?? "", Mathf.Clamp(limit, 1, 500));
            return new { success = true, last = Recorder.LastEventSeq, count = list.Count, events = list };
        }

        [Tool("logs/mark", Description = "The current log position. Read with since=<this> later to see only what was logged after now.")]
        public object Mark() => new { success = true, last = Recorder.LastLogSeq };
    }
}
