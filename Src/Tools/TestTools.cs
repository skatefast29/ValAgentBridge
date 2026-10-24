using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using BepInEx;
using Lib.GAB.Tools;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ValAgentBridge.Tools
{
    /// <summary>
    /// Runs a whole test as one call: a list of steps (bridge tool calls, waits, checks, screenshots),
    /// then a pass/fail report with the warnings, errors and on-screen messages that appeared meanwhile.
    /// Format: see TOOLS.md ("Test scripts").
    /// </summary>
    public class TestTools
    {
        private static string TestsFolder => Path.Combine(Paths.BepInExRootPath, "ValAgentBridge", "tests");
        private static string ResultsFolder => Path.Combine(Paths.BepInExRootPath, "ValAgentBridge", "test-results");
        private static readonly Regex VarPattern = new Regex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)((?:\.[^}]+)?)\}");

        [Tool("test/run", Description = "Run a test script given as JSON: {\"name\":..., \"steps\":[...]}. Steps: {\"call\":\"tool/name\",\"args\":{}}, {\"wait\":seconds}, {\"waitUntil\":{\"call\":...,\"args\":{}},\"until\":[checks],\"timeout\":60} (or {\"waitUntil\":\"in_world\"} / \"main_menu\"), {\"screenshot\":true}, {\"note\":\"text\"}, {\"repeat\":N,\"steps\":[...],\"as\":\"round\"}; any step may add \"optional\": true (a failure is reported but the run goes on and still passes), \"report\": true (shown in the summary), and \"expect\":[{\"path\":\"health\",\"greaterThan\":10}] and \"saveAs\":\"name\" (use ${name.path} in later args). Returns pass/fail with the failed steps (detail \"full\": every step) plus a summary of warnings, errors, on-screen messages and events from the run (repeats counted, full detail in reportFile); a failing step gets a screenshot (top-level \"screenshotOnFail\": false turns that off). Top-level \"clearHostiles\": 50 removes hostile creatures within 50 m first and \"keepClear\": 50 keeps doing so every 10 s; \"protect\": true keeps the player from taking damage or being noticed by creatures during the run, then restores.")]
        public object Run(
            [ToolParameter(Description = "The script: a JSON object, or the same as a JSON string")] object script,
            [ToolParameter(Description = "summary (default: failed steps, messages, events) or full (every step); the saved report always has everything")] string detail = "")
        {
            JObject parsed;
            try { parsed = script as JObject ?? JObject.Parse(script as string ?? JToken.FromObject(script).ToString()); }
            catch (Exception ex) { return new { success = false, error = "Script is not valid JSON: " + ex.Message }; }
            return Execute(parsed, "inline", detail);
        }

        [Tool("test/run_file", Description = "Run a saved test script from BepInEx\\ValAgentBridge\\tests (name with or without .json), or from a full path. The scripts that come with the bridge (enter-world, quick-check, inventory-items-console, world-controls) work in any profile.")]
        public object RunFile(
            [ToolParameter(Description = "File name in the tests folder, or a full path")] string name,
            [ToolParameter(Description = "summary (default: failed steps, messages, events) or full (every step); the saved report always has everything")] string detail = "")
        {
            var file = name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? name : name + ".json";
            var path = Path.IsPathRooted(name) ? name : Path.Combine(TestsFolder, file);
            string text;
            if (File.Exists(path)) text = File.ReadAllText(path);
            else if (!Path.IsPathRooted(name) && BuiltIn(file) is string builtIn) text = builtIn;
            else return new { success = false, error = $"No test file at {path}. Use test/list_files." };
            JObject parsed;
            try { parsed = JObject.Parse(text); }
            catch (Exception ex) { return new { success = false, error = $"{Path.GetFileName(path)} is not valid JSON: {ex.Message}" }; }
            return Execute(parsed, Path.GetFileNameWithoutExtension(path), detail);
        }

        /// <summary>A test script shipped inside the DLL (tests\*.json at build time), or null.</summary>
        private static string? BuiltIn(string file)
        {
            using var stream = typeof(TestTools).Assembly.GetManifestResourceStream("tests/" + file);
            if (stream == null) return null;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        private static IEnumerable<string> BuiltInNames() => typeof(TestTools).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("tests/")).Select(n => n.Substring("tests/".Length));

        [Tool("test/list_files", Description = "List saved test scripts and the latest result files.")]
        public object ListFiles()
        {
            Directory.CreateDirectory(TestsFolder);
            var tests = Directory.GetFiles(TestsFolder, "*.json").Select(Path.GetFileName).OrderBy(n => n).ToList();
            var results = Directory.Exists(ResultsFolder)
                ? new DirectoryInfo(ResultsFolder).GetFiles("*.json").OrderByDescending(f => f.LastWriteTime).Take(10).Select(f => f.Name).ToList()
                : new List<string>();
            return new { success = true, folder = TestsFolder, tests, builtIn = BuiltInNames().OrderBy(n => n).ToList(), latestResults = results };
        }

        /// <summary>State of one script run, shared by its steps and the steps inside repeat blocks.</summary>
        private sealed class RunState
        {
            public readonly Dictionary<string, JToken> vars = new Dictionary<string, JToken>();
            public readonly JArray report = new JArray();
            public JToken? last;
            public bool passed = true, stopOnFail, screenshotOnFail;
            public int stepsRun, ok, failed, optionalFailed;
        }

        private const int MaxStepsPerRun = 10000;

        private static object Execute(JObject script, string fallbackName, string detail)
        {
            var name = script.Value<string>("name") ?? fallbackName;
            if (!(script["steps"] is JArray steps) || steps.Count == 0)
                return new { success = false, error = "The script needs a non-empty \"steps\" array." };
            if (string.IsNullOrWhiteSpace(detail)) detail = script.Value<string>("detail") ?? "summary";
            detail = detail.Trim().ToLowerInvariant();
            if (detail != "summary" && detail != "full")
                return new { success = false, error = "detail is \"summary\" (failed steps, messages, events) or \"full\" (every step)." };
            var run = new RunState
            {
                stopOnFail = script.Value<bool?>("stopOnFail") ?? true,
                screenshotOnFail = script.Value<bool?>("screenshotOnFail") ?? true
            };
            // "clearHostiles": radius removes hostile creatures near the player before the first step.
            if (script["clearHostiles"] != null)
            {
                var ch = script["clearHostiles"]!;
                var radius = ch.Type == JTokenType.Boolean ? (ch.Value<bool>() ? 50.0 : 0) : ch.Type == JTokenType.Null ? 0 : ch.Value<double>();
                if (radius > 0) Call("world/clear_hostiles", new JObject { ["radius"] = radius });
            }
            // "keepClear": radius (or {"radius":50,"every":10}) repeats that every few seconds during the run. Off by
            // default: the clearing itself costs a little time, which matters for performance measurements.
            System.Threading.Timer? keepClear = null;
            int clearedDuringRun = 0;
            if (script["keepClear"] is JToken kc && kc.Type != JTokenType.Null && !(kc.Type == JTokenType.Boolean && !kc.Value<bool>()))
            {
                double radius = kc is JObject ko ? ko.Value<double?>("radius") ?? 50 : kc.Type == JTokenType.Boolean ? 50 : kc.Value<double>();
                double every = kc is JObject ko2 ? ko2.Value<double?>("every") ?? 10 : 10;
                every = Clamp(every, 2, 600);
                keepClear = new System.Threading.Timer(_ =>
                {
                    try
                    {
                        var r = Call("world/clear_hostiles", new JObject { ["radius"] = radius });
                        Interlocked.Add(ref clearedDuringRun, r.Value<int?>("removed") ?? 0);
                    }
                    catch { }
                }, null, TimeSpan.FromSeconds(every), TimeSpan.FromSeconds(every));
            }
            // "protect": true keeps the player safe for the run (no damage, creatures ignore them), then restores.
            JToken? protectedBefore = null;
            if (script.Value<bool?>("protect") == true)
            {
                var on = Call("player/set_protection", new JObject { ["god"] = "true", ["ghost"] = "true" });
                if (on.Value<bool?>("success") == true) protectedBefore = on["before"];
            }

            long logMark = Recorder.LastLogSeq, msgMark = Recorder.LastMessageSeq, eventMark = Recorder.LastEventSeq;
            var clock = Stopwatch.StartNew();
            ValAgentBridgePlugin.ModLogger.LogInfo($"test/run '{name}': {steps.Count} steps");

            RunSteps(steps, run, "");

            if (keepClear != null)
            {
                using var stopped = new ManualResetEvent(false);
                keepClear.Dispose(stopped);
                stopped.WaitOne(5000);
            }
            if (protectedBefore != null)
            {
                try { Call("player/set_protection", new JObject { ["god"] = protectedBefore.Value<bool>("god").ToString(), ["ghost"] = protectedBefore.Value<bool>("ghost").ToString() }); }
                catch (Exception ex) { ValAgentBridgePlugin.ModLogger.LogWarning($"test/run '{name}': could not restore protection: {ex.Message}"); }
            }

            var result = new JObject
            {
                ["success"] = true,
                ["name"] = name,
                ["passed"] = run.passed,
                ["stepsRun"] = run.stepsRun,
                ["stepsOk"] = run.ok,
                ["stepsFailed"] = run.failed,
                ["optionalFailed"] = run.optionalFailed,
                ["stepsInScript"] = steps.Count,
                ["hostilesClearedDuringRun"] = keepClear != null ? clearedDuringRun : (JToken?)null,
                ["seconds"] = Math.Round(clock.Elapsed.TotalSeconds, 1),
                ["steps"] = run.report,
                ["warningsAndErrors"] = JToken.FromObject(Recorder.LogSince(logMark, "warning", "", 200)),
                ["messages"] = JToken.FromObject(Recorder.MessagesSince(msgMark, 200)),
                ["events"] = JToken.FromObject(Recorder.EventsSince(eventMark, "", 200))
            };
            string reportFile;
            try
            {
                Directory.CreateDirectory(ResultsFolder);
                reportFile = Path.Combine(ResultsFolder, $"{Safe(name)}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
                File.WriteAllText(reportFile, result.ToString(Formatting.Indented));
            }
            catch (Exception ex)
            {
                reportFile = "not saved: " + ex.Message;
            }
            // The answer is a summary (repeats counted once); the file keeps every line and every step.
            result["warningsAndErrors"] = Group((JArray)result["warningsAndErrors"]!, "level", 15);
            result["messages"] = Group((JArray)result["messages"]!, "type", 15);
            result["events"] = SummariseEvents((JArray)result["events"]!);
            result["detail"] = detail;
            if (detail == "summary")
            {
                // Failed steps (optional ones too) and steps marked "report": true; the rest only in reportFile.
                var shown = new JArray(run.report.OfType<JObject>().Where(e => e.Value<bool?>("ok") == false || e.Value<bool?>("report") == true)
                    .Select(e => { var c = (JObject)e.DeepClone(); if (c["result"] != null) c["result"] = Shorten(c["result"], 600); return c; }));
                result["steps"] = shown;
                result["stepsNote"] = $"summary: {shown.Count} of {run.report.Count} steps shown (failed, or marked \"report\": true); every step is in reportFile, or run with detail \"full\"";
            }
            else
            {
                // Every step, but long results only in full in reportFile.
                result["steps"] = new JArray(run.report.OfType<JObject>()
                    .Select(e => { var c = (JObject)e.DeepClone(); if (c["result"] != null) c["result"] = Shorten(c["result"], 3000); return c; }));
                result["stepsNote"] = "step results over 3000 characters are cut here; reportFile has them in full";
            }
            result["reportFile"] = reportFile;
            ValAgentBridgePlugin.ModLogger.LogInfo($"test/run '{name}': {(run.passed ? "PASSED" : "FAILED")} after {run.stepsRun} step(s), {run.failed} failed, {run.optionalFailed} optional failed");
            return result;
        }

        /// <summary>Runs a list of steps (the script's, or a repeat block's). False once the run must stop.</summary>
        private static bool RunSteps(JArray steps, RunState run, string prefix)
        {
            for (int i = 0; i < steps.Count; i++)
            {
                if (run.stepsRun >= MaxStepsPerRun)
                {
                    run.report.Add(new JObject { ["step"] = prefix + (i + 1), ["ok"] = false, ["failures"] = new JArray($"stopped: more than {MaxStepsPerRun} steps in one run") });
                    run.failed++;
                    run.passed = false;
                    return false;
                }
                var step = steps[i] as JObject ?? new JObject();
                var label = prefix + (i + 1);

                // {"repeat": N, "steps": [...], "as": "round"}: the inner steps N times; ${round} is 1..N.
                if (step["repeat"] != null)
                {
                    int times = (int)Clamp(step.Value<double?>("repeat") ?? 0, 0, 1000);
                    if (!(step["steps"] is JArray inner) || inner.Count == 0)
                    {
                        run.report.Add(new JObject { ["step"] = label, ["ok"] = false, ["failures"] = new JArray("repeat needs a non-empty \"steps\" array") });
                        run.failed++;
                        run.passed = false;
                        if (run.stopOnFail) return false;
                        continue;
                    }
                    var counter = step.Value<string>("as");
                    run.report.Add(new JObject { ["step"] = label, ["repeat"] = times, ["ok"] = true });
                    for (int round = 1; round <= times; round++)
                    {
                        if (!string.IsNullOrEmpty(counter)) run.vars[counter!] = round;
                        if (!RunSteps(inner, run, $"{label}.{round}.")) return false;
                    }
                    continue;
                }

                run.stepsRun++;
                var entry = new JObject { ["step"] = prefix.Length == 0 ? (JToken)(i + 1) : label };
                if (step["note"] != null) entry["note"] = step["note"];
                if (step.Value<bool?>("report") == true) entry["report"] = true;
                bool optional = step.Value<bool?>("optional") == true || step.Value<bool?>("continueOnFail") == true;
                var failures = new List<string>();
                var stepClock = Stopwatch.StartNew();
                try
                {
                    if (step["call"] != null)
                    {
                        run.last = Call(step.Value<string>("call")!, Substitute(step["args"], run.vars));
                        entry["call"] = step["call"];
                        entry["result"] = Shorten(run.last, FileMax);
                        if (run.last.Type == JTokenType.Object && run.last.Value<bool?>("success") == false && step.Value<bool?>("allowFail") != true)
                            failures.Add("tool failed: " + (run.last.Value<string>("error") ?? "no reason given"));
                    }
                    else if (step["wait"] != null)
                    {
                        Thread.Sleep(TimeSpan.FromSeconds(Clamp(step.Value<double>("wait"), 0, 600)));
                    }
                    else if (step["waitUntil"] is JObject || step["waitUntil"]?.Type == JTokenType.String)
                    {
                        JObject until;
                        JToken? checks;
                        if (step["waitUntil"] is JObject u) { until = u; checks = Substitute(step["until"] ?? u["until"], run.vars); }
                        else
                        {
                            // Shorthand: a phase name from world/status.
                            until = new JObject { ["call"] = "world/status" };
                            checks = new JArray(new JObject { ["path"] = "phase", ["equals"] = step["waitUntil"] });
                        }
                        double timeout = Clamp(step.Value<double?>("timeout") ?? 60, 1, 1800);
                        double every = Clamp(step.Value<double?>("every") ?? 1, 0.2, 60);
                        var waitClock = Stopwatch.StartNew();
                        while (true)
                        {
                            run.last = Call(until.Value<string>("call")!, Substitute(until["args"], run.vars));
                            if (Check(run.last, checks).Count == 0) break;
                            if (waitClock.Elapsed.TotalSeconds >= timeout)
                            {
                                failures.Add($"waited {timeout}s: " + string.Join("; ", Check(run.last, checks)));
                                break;
                            }
                            Thread.Sleep(TimeSpan.FromSeconds(every));
                        }
                        entry["waitedFor"] = until["call"];
                        entry["result"] = Shorten(run.last, FileMax);
                    }
                    else if (step["screenshot"] != null)
                    {
                        var args = new JObject { ["maxWidth"] = step.Value<int?>("maxWidth") ?? 1280 };
                        run.last = Call("screen/capture", args);
                        entry["screenshot"] = run.last.Value<string>("path");
                    }
                    else if (step["note"] == null)
                    {
                        failures.Add("unknown step: needs call, wait, waitUntil (an object, or \"in_world\" / \"main_menu\"), repeat, screenshot or note");
                    }

                    if (step["expect"] != null)
                        failures.AddRange(Check(run.last, Substitute(step["expect"], run.vars)));
                    if (step["saveAs"] != null && run.last != null)
                        run.vars[step.Value<string>("saveAs")!] = run.last;
                }
                catch (Exception ex)
                {
                    failures.Add("error: " + ex.GetBaseException().Message);
                }

                entry["ok"] = failures.Count == 0;
                if (failures.Count > 0)
                {
                    entry["failures"] = new JArray(failures);
                    if (optional) entry["optional"] = true;
                    // A picture of the moment it went wrong (not for optional steps: they are expected to fail at times).
                    if (run.screenshotOnFail && !optional)
                    {
                        try { entry["failScreenshot"] = Call("screen/capture", new JObject { ["maxWidth"] = 1280 }).Value<string>("path"); }
                        catch (Exception ex) { entry["failScreenshot"] = "not taken: " + ex.GetBaseException().Message; }
                    }
                }
                entry["seconds"] = Math.Round(stepClock.Elapsed.TotalSeconds, 2);
                run.report.Add(entry);
                if (failures.Count == 0) run.ok++;
                else if (optional) run.optionalFailed++;
                else
                {
                    run.failed++;
                    run.passed = false;
                    if (run.stopOnFail) return false;
                }
            }
            return true;
        }

        private static JToken Call(string tool, JToken? args)
        {
            tool = tool.Replace('.', '/');
            if (tool.StartsWith("test/")) throw new InvalidOperationException("a test can't run another test");
            var registry = ValAgentBridgePlugin.Tools ?? throw new InvalidOperationException("bridge not started");
            if (!registry.HasTool(tool))
                throw new InvalidOperationException($"no tool '{tool}'. Closest: {string.Join(", ", ToolGuard.Suggest(registry, tool))}");
            var result = registry.CallToolAsync(tool, args as JObject ?? new JObject()).GetAwaiter().GetResult();
            return result == null ? JValue.CreateNull() : JToken.FromObject(result);
        }

        /// <summary>Each check: {"path": "a.b[0]", one of equals / notEquals / contains / greaterThan / lessThan / exists}.</summary>
        private static List<string> Check(JToken? result, JToken? checks)
        {
            var failures = new List<string>();
            if (checks == null) return failures;
            IEnumerable<JObject> list = checks is JArray arr ? arr.OfType<JObject>()
                : checks is JObject one ? new[] { one } : Enumerable.Empty<JObject>();
            foreach (var c in list)
            {
                var path = c.Value<string>("path") ?? "";
                var actual = result == null ? null : path.Length == 0 ? result : result.SelectToken(path);
                var shown = actual == null ? "missing" : actual.ToString(Formatting.None);
                if (c["exists"] != null && (actual != null) != c.Value<bool>("exists"))
                    failures.Add($"{path}: expected {(c.Value<bool>("exists") ? "to exist" : "to be missing")}, was {shown}");
                if (c["equals"] != null && !Same(actual, c["equals"]!))
                    failures.Add($"{path}: expected {c["equals"]!.ToString(Formatting.None)}, was {shown}");
                if (c["notEquals"] != null && Same(actual, c["notEquals"]!))
                    failures.Add($"{path}: expected anything but {c["notEquals"]!.ToString(Formatting.None)}");
                if (c["contains"] != null && (actual == null || actual.ToString(Formatting.None).IndexOf(c.Value<string>("contains")!, StringComparison.OrdinalIgnoreCase) < 0))
                    failures.Add($"{path}: expected to contain '{c.Value<string>("contains")}', was {shown}");
                if (c["greaterThan"] != null && !(Number(actual) > c.Value<double>("greaterThan")))
                    failures.Add($"{path}: expected more than {c["greaterThan"]}, was {shown}");
                if (c["lessThan"] != null && !(Number(actual) < c.Value<double>("lessThan")))
                    failures.Add($"{path}: expected less than {c["lessThan"]}, was {shown}");
            }
            return failures;
        }

        private static bool Same(JToken? actual, JToken expected)
        {
            if (actual == null) return expected.Type == JTokenType.Null;
            if (JToken.DeepEquals(actual, expected)) return true;
            var na = Number(actual);
            var ne = Number(expected);
            if (!double.IsNaN(na) && !double.IsNaN(ne)) return Math.Abs(na - ne) < 1e-6;
            return string.Equals(actual.ToString(), expected.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        private static double Number(JToken? t)
        {
            if (t == null) return double.NaN;
            if (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) return t.Value<double>();
            return double.TryParse(t.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN;
        }

        /// <summary>Replaces ${var} / ${var.path} in string arguments with values saved by earlier steps.</summary>
        private static JToken? Substitute(JToken? args, Dictionary<string, JToken> vars)
        {
            if (args == null) return null;
            var copy = args.DeepClone();
            foreach (var v in copy.SelectTokens("..*").OfType<JValue>().Where(v => v.Type == JTokenType.String).ToList())
            {
                var text = v.Value<string>()!;
                var whole = VarPattern.Match(text);
                if (whole.Success && whole.Length == text.Length)
                {
                    v.Replace(Lookup(whole, vars) ?? JValue.CreateNull());
                    continue;
                }
                v.Value = VarPattern.Replace(text, m => Lookup(m, vars)?.ToString() ?? m.Value);
            }
            return copy;
        }

        private static JToken? Lookup(Match m, Dictionary<string, JToken> vars)
        {
            if (!vars.TryGetValue(m.Groups[1].Value, out var root)) return null;
            var path = m.Groups[2].Value.TrimStart('.');
            return path.Length == 0 ? root : root.SelectToken(path);
        }

        // The report file keeps step results whole up to this size (a whole scene tree could be megabytes).
        private const int FileMax = 200000;

        // Big results (prefab lists etc.) would swamp the answer.
        private static JToken Shorten(JToken? t, int max)
        {
            if (t == null) return JValue.CreateNull();
            var text = t.ToString(Formatting.None);
            return text.Length <= max ? t : new JObject { ["truncated"] = true, ["start"] = text.Substring(0, max) };
        }

        /// <summary>Identical lines become one entry with a count and first/last time.</summary>
        private static JToken Group(JArray lines, string kindField, int max)
        {
            var groups = lines.OfType<JObject>()
                .GroupBy(l => (kind: l.Value<string>(kindField) ?? "", source: l.Value<string>("source") ?? "", text: (l.Value<string>("text") ?? "").Trim()))
                .Select(g => new JObject
                {
                    [kindField] = g.Key.kind,
                    ["source"] = g.Key.source.Length > 0 ? g.Key.source : null,
                    ["text"] = g.Key.text.Length > 300 ? g.Key.text.Substring(0, 300) + "..." : g.Key.text,
                    ["count"] = g.Count(),
                    ["first"] = g.First().Value<string>("time"),
                    ["last"] = g.Count() > 1 ? g.Last().Value<string>("time") : null
                })
                .ToList();
            foreach (var g in groups)
                foreach (var p in g.Properties().Where(p => p.Value is JValue v && v.Value == null).ToList()) p.Remove();
            var shown = new JArray(groups.Take(max));
            if (groups.Count > max) shown.Add(new JObject { ["more"] = groups.Count - max, ["note"] = "see reportFile" });
            return shown;
        }

        /// <summary>Events: a count per channel plus the deaths and the last few of each channel.</summary>
        private static JToken SummariseEvents(JArray events)
        {
            var all = events.OfType<JObject>().ToList();
            if (all.Count <= 5) return events;
            return new JObject
            {
                ["counts"] = new JObject(all.GroupBy(e => e.Value<string>("channel") ?? "").Select(g => new JProperty(g.Key, g.Count()))),
                ["deaths"] = new JArray(all.Where(e => e.Value<string>("channel") == "player/death")),
                ["last"] = new JArray(all.Skip(Math.Max(0, all.Count - 5)))
            };
        }

        private static double Clamp(double v, double lo, double hi) => Math.Max(lo, Math.Min(hi, v));

        private static string Safe(string name) => string.Concat(name.Select(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '_'));
    }
}
