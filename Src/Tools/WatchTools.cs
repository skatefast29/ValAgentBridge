using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Lib.GAB.Tools;
using UnityEngine;

namespace ValAgentBridge.Tools
{
    /// <summary>
    /// Watches calls to any game or mod method by patching it at runtime: counts every call and records
    /// the arguments, return value and exceptions of the first N (recording every call would slow hot
    /// methods down). Mono inlines tiny methods, so a watch on one may never fire.
    /// </summary>
    public class WatchTools
    {
        private sealed class Call
        {
            public long n;
            public double atSeconds;
            public string? on;
            public object? args;
            public object? returned;
            public string? threw;
        }

        private sealed class Watch
        {
            public int id;
            public string label = "";
            public MethodBase method = null!;
            public int keep;
            public int argDepth;
            public long calls;
            public long exceptions;
            public readonly List<Call> recorded = new List<Call>();
            public readonly Stopwatch clock = Stopwatch.StartNew();
        }

        private static readonly Harmony WatchHarmony = new Harmony("BarryWhite.ValAgentBridge.watch");
        private static readonly ConcurrentDictionary<MethodBase, Watch> ByMethod = new ConcurrentDictionary<MethodBase, Watch>();
        private static int _nextId;

        [Tool("debug/watch_start", Description = "Start watching a method: every call is counted, the first N are recorded with arguments, return value and exceptions. Overloads are all watched unless argCount is given.")]
        public object Start(
            [ToolParameter(Description = "Type name, full or short (see debug/find_types)")] string type,
            [ToolParameter(Description = "Method name; property getters are 'get_Name'")] string method,
            [ToolParameter(Description = "Only the overload with this many parameters (-1 = all)")] int argCount = -1,
            [ToolParameter(Description = "How many calls to record in detail (default 50, max 500); all calls are still counted")] int keep = 50,
            [ToolParameter(Description = "Levels of fields shown for arguments and results (default 1, max 3)")] int depth = 1)
        {
            return MainThreadDispatcher.RunTool(() =>
            {
                var t = Inspector.ResolveType(type, out var ambiguous);
                if (t == null) return new { success = false, error = ambiguous != null ? $"'{type}' matches several types: {ambiguous}" : $"No type '{type}'. Try debug/find_types." };
                var targets = t.GetMethods(Inspector.All | BindingFlags.DeclaredOnly).Cast<MethodBase>()
                    .Concat(method == ".ctor" ? t.GetConstructors(Inspector.All) : Array.Empty<ConstructorInfo>())
                    .Where(m => m.Name == method && (argCount < 0 || m.GetParameters().Length == argCount))
                    .ToList();
                if (targets.Count == 0) return new { success = false, error = $"{t.Name} declares no method '{method}'" + (argCount >= 0 ? $" with {argCount} parameter(s)" : "") };

                var started = new List<object>();
                foreach (var m in targets)
                {
                    var label = $"{t.FullName}.{m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})";
                    if (m.IsAbstract || m.ContainsGenericParameters)
                    {
                        started.Add(new { method = label, watching = false, note = "abstract or generic; can't be patched" });
                        continue;
                    }
                    if (ByMethod.ContainsKey(m))
                    {
                        started.Add(new { method = label, watching = true, note = "already watched", id = ByMethod[m].id });
                        continue;
                    }
                    var w = new Watch { id = ++_nextId, label = label, method = m, keep = Mathf.Clamp(keep, 0, 500), argDepth = Mathf.Clamp(depth, 0, 3) };
                    ByMethod[m] = w;
                    try
                    {
                        WatchHarmony.Patch(m, postfix: new HarmonyMethod(PostfixFor(m)), finalizer: new HarmonyMethod(typeof(WatchTools), nameof(Finalizer)));
                    }
                    catch (Exception ex)
                    {
                        ByMethod.TryRemove(m, out _);
                        started.Add(new { method = label, watching = false, note = "patch failed: " + ex.GetBaseException().Message });
                        continue;
                    }
                    var il = SafeIlSize(m);
                    started.Add(new
                    {
                        method = label,
                        watching = true,
                        id = w.id,
                        note = il >= 0 && il < 20 ? $"tiny method ({il} bytes of IL): Mono may inline it, so calls from other code may not be seen" : null
                    });
                    ValAgentBridgePlugin.ModLogger.LogInfo($"debug/watch_start #{w.id}: {label}");
                }
                return new { success = true, watches = started };
            });
        }

        [Tool("debug/watch_list", Description = "The active watches with their call counts.")]
        public object List()
        {
            var list = ByMethod.Values.OrderBy(w => w.id).Select(w =>
            {
                lock (w) return new { id = w.id, method = w.label, calls = w.calls, exceptions = w.exceptions, recorded = w.recorded.Count, keep = w.keep, seconds = Math.Round(w.clock.Elapsed.TotalSeconds, 1) };
            }).ToList();
            return new { success = true, count = list.Count, watches = list };
        }

        [Tool("debug/watch_read", Description = "The recorded calls of a watch (arguments, return value, exceptions, seconds since the watch started).")]
        public object Read(
            [ToolParameter(Description = "Watch id from debug/watch_start or debug/watch_list")] int id,
            [ToolParameter(Description = "Only calls after this call number (default 0)")] long since = 0,
            [ToolParameter(Description = "true = clear the recording afterwards so the next N calls get recorded")] bool clear = false)
        {
            var w = ByMethod.Values.FirstOrDefault(x => x.id == id);
            if (w == null) return new { success = false, error = $"No watch #{id}. Use debug/watch_list." };
            lock (w)
            {
                var calls = w.recorded.Where(c => c.n > since).ToList();
                if (clear) w.recorded.Clear();
                return new { success = true, id, method = w.label, totalCalls = w.calls, exceptions = w.exceptions, calls };
            }
        }

        [Tool("debug/watch_stop", Description = "Stop a watch (removes its patch), or all watches with id 0.")]
        public object Stop(
            [ToolParameter(Description = "Watch id, or 0 for all")] int id = 0)
        {
            return MainThreadDispatcher.RunTool(() =>
            {
                var stopping = ByMethod.Values.Where(w => id == 0 || w.id == id).ToList();
                if (stopping.Count == 0) return new { success = false, error = id == 0 ? "No watches running." : $"No watch #{id}." };
                foreach (var w in stopping)
                {
                    WatchHarmony.Unpatch(w.method, HarmonyPatchType.All, WatchHarmony.Id);
                    ByMethod.TryRemove(w.method, out _);
                }
                return new { success = true, stopped = stopping.Select(w => new { id = w.id, method = w.label, calls = w.calls }).ToList() };
            });
        }

        // One postfix per method shape: Harmony can't inject __instance into a static method or __result into a void one.
        private static MethodInfo PostfixFor(MethodBase m)
        {
            bool hasResult = m is MethodInfo mi && mi.ReturnType != typeof(void);
            string name = (m.IsStatic ? "Static" : "Instance") + (hasResult ? "Result" : "Void");
            return AccessTools.Method(typeof(WatchTools), name);
        }

        private static void InstanceResult(object __instance, object[] __args, object __result, MethodBase __originalMethod) =>
            Record(__originalMethod, __instance, __args, __result, null);
        private static void InstanceVoid(object __instance, object[] __args, MethodBase __originalMethod) =>
            Record(__originalMethod, __instance, __args, null, null);
        private static void StaticResult(object[] __args, object __result, MethodBase __originalMethod) =>
            Record(__originalMethod, null, __args, __result, null);
        private static void StaticVoid(object[] __args, MethodBase __originalMethod) =>
            Record(__originalMethod, null, __args, null, null);

        private static void Finalizer(Exception __exception, MethodBase __originalMethod)
        {
            if (__exception != null) Record(__originalMethod, null, null, null, __exception);
        }

        private static void Record(MethodBase method, object? instance, object[]? args, object? result, Exception? ex)
        {
            if (!ByMethod.TryGetValue(method, out var w)) return;
            try
            {
                lock (w)
                {
                    if (ex == null) w.calls++; else w.exceptions++;
                    if (w.recorded.Count >= w.keep) return;
                    w.recorded.Add(new Call
                    {
                        n = w.calls + w.exceptions,
                        atSeconds = Math.Round(w.clock.Elapsed.TotalSeconds, 3),
                        on = instance == null ? null : DescribeOwner(instance),
                        args = args == null ? null : method.GetParameters().Select((p, i) => new { name = p.Name, value = i < args.Length ? Inspector.Describe(args[i], w.argDepth, 300) : null }).ToList(),
                        returned = ex == null ? Inspector.Describe(result, w.argDepth, 300) : null,
                        threw = ex == null ? null : $"{ex.GetType().Name}: {ex.Message}"
                    });
                }
            }
            catch
            {
                // Recording must never break the watched method.
            }
        }

        private static string DescribeOwner(object instance)
        {
            if (instance is UnityEngine.Object uo)
                return uo == null ? "(destroyed)" : $"{uo.name} ({instance.GetType().Name})";
            return instance.GetType().Name;
        }

        private static int SafeIlSize(MethodBase m)
        {
            try { return m.GetMethodBody()?.GetILAsByteArray()?.Length ?? -1; }
            catch { return -1; }
        }
    }
}
