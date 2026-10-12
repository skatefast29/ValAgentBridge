using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using HarmonyLib;

namespace ValAgentBridge
{
    /// <summary>
    /// Keeps the recent game log and on-screen messages in memory, numbered, so tools can ask
    /// "what happened since step N" instead of re-reading LogOutput.log.
    /// </summary>
    public static class Recorder
    {
        public sealed class LogEntry
        {
            public long seq;
            public string time = "";
            public string level = "";
            public string source = "";
            public string text = "";
        }

        public sealed class MessageEntry
        {
            public long seq;
            public string time = "";
            public string type = "";
            public string text = "";
        }

        public sealed class EventEntry
        {
            public long seq;
            public string time = "";
            public string channel = "";
            public object? data;
        }

        private static readonly LinkedList<EventEntry> Events = new LinkedList<EventEntry>();
        private static long _eventSeq;
        public static long LastEventSeq { get { lock (Gate) return _eventSeq; } }

        public static void AddEvent(string channel, object data)
        {
            lock (Gate)
            {
                Events.AddLast(new EventEntry { seq = ++_eventSeq, time = DateTime.Now.ToString("HH:mm:ss.fff"), channel = channel, data = data });
                while (Events.Count > 500) Events.RemoveFirst();
            }
        }

        public static List<EventEntry> EventsSince(long since, string channel, int limit)
        {
            lock (Gate)
                return Events.Where(e => e.seq > since && (channel.Length == 0 || e.channel == channel)).Reverse().Take(limit).Reverse().ToList();
        }

        private const int MaxLog = 3000;
        private const int MaxMessages = 200;
        private static readonly object Gate = new object();
        private static readonly LinkedList<LogEntry> Log = new LinkedList<LogEntry>();
        private static readonly LinkedList<MessageEntry> Messages = new LinkedList<MessageEntry>();
        private static long _logSeq;
        private static long _msgSeq;

        public static long LastLogSeq { get { lock (Gate) return _logSeq; } }
        public static long LastMessageSeq { get { lock (Gate) return _msgSeq; } }

        public static void Install(Harmony harmony)
        {
            Logger.Listeners.Add(new Listener());
            var show = AccessTools.Method(typeof(MessageHud), nameof(MessageHud.ShowMessage));
            if (show != null)
                harmony.Patch(show, postfix: new HarmonyMethod(typeof(Recorder), nameof(ShowMessagePostfix)));
            else
                ValAgentBridgePlugin.ModLogger.LogWarning("MessageHud.ShowMessage not found; ui/messages will stay empty.");
            var addString = AccessTools.Method(typeof(Terminal), nameof(Terminal.AddString), new[] { typeof(string) });
            if (addString != null)
                harmony.Patch(addString, postfix: new HarmonyMethod(typeof(Recorder), nameof(AddStringPostfix)));
        }

        // Console lines printed while console/run_command runs a command (main thread only).
        private static List<string>? _consoleCapture;

        public static List<string> CaptureConsole(Action run)
        {
            var lines = new List<string>();
            _consoleCapture = lines;
            try { run(); }
            finally { _consoleCapture = null; }
            return lines;
        }

        private static void AddStringPostfix(string text) => _consoleCapture?.Add(text);

        /// <summary>Levels at or above <paramref name="minLevel"/>: error, warning, info (default) or all.</summary>
        public static List<LogEntry> LogSince(long since, string minLevel, string filter, int limit)
        {
            var allowed = AllowedLevels(minLevel);
            lock (Gate)
            {
                return Log.Where(e => e.seq > since && allowed.Contains(e.level) &&
                                      (filter.Length == 0 ||
                                       e.text.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                       e.source.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0))
                          .Reverse().Take(limit).Reverse().ToList();
            }
        }

        public static List<MessageEntry> MessagesSince(long since, int limit)
        {
            lock (Gate)
                return Messages.Where(m => m.seq > since).Reverse().Take(limit).Reverse().ToList();
        }

        private static HashSet<string> AllowedLevels(string minLevel)
        {
            var order = new[] { "Fatal", "Error", "Warning", "Message", "Info", "Debug" };
            int cut = (minLevel ?? "").ToLowerInvariant() switch
            {
                "error" => 1,
                "warning" => 2,
                "all" => 5,
                "debug" => 5,
                _ => 4
            };
            return new HashSet<string>(order.Take(cut + 1));
        }

        private static void ShowMessagePostfix(MessageHud.MessageType type, string text)
        {
            try
            {
                var shown = Localization.instance != null ? Localization.instance.Localize(text) : text;
                lock (Gate)
                {
                    Messages.AddLast(new MessageEntry { seq = ++_msgSeq, time = DateTime.Now.ToString("HH:mm:ss.fff"), type = type.ToString(), text = shown });
                    while (Messages.Count > MaxMessages) Messages.RemoveFirst();
                }
            }
            catch
            {
                // Never let a recording problem break the game's message display.
            }
        }

        private sealed class Listener : ILogListener
        {
            public void LogEvent(object sender, LogEventArgs e)
            {
                // Runs inside every mod's logging call: it must never throw into that mod.
                try
                {
                    var level = e.Level.ToString();
                    string text;
                    try { text = e.Data?.ToString() ?? ""; }
                    catch (Exception ex) { text = $"(log line could not be read: {ex.GetType().Name})"; }
                    var entry = new LogEntry
                    {
                        time = DateTime.Now.ToString("HH:mm:ss.fff"),
                        level = level.Contains(",") ? level.Split(',')[0].Trim() : level,
                        source = e.Source?.SourceName ?? "",
                        text = text
                    };
                    lock (Gate)
                    {
                        entry.seq = ++_logSeq;
                        Log.AddLast(entry);
                        while (Log.Count > MaxLog) Log.RemoveFirst();
                    }
                }
                catch
                {
                    // Recording is best effort.
                }
            }

            public void Dispose() { }
        }
    }
}
