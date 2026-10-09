using System;
using System.Linq;
using System.Reflection;
using Lib.GAB.Tools;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ValBridgeServer.Tools
{
    // Writing live state. Dev-profile tool: every change is logged, nothing is saved unless the game saves it.
    public class EditTools
    {
        [Tool("debug/set_value", Description = "Change a live field or property by path (same paths as debug/inspect), e.g. 'Player.m_localPlayer.m_maxCarryWeight' = 600. Lasts until the game or mod changes it back or the game restarts.")]
        public object SetValue(
            [ToolParameter(Description = "Dotted path to the field or property")] string path,
            [ToolParameter(Description = "New value as text: number, true/false, enum name, 'x,y,z' for a Vector3, JSON for anything else")] string value)
        {
            return MainThreadDispatcher.RunTool(() =>
            {
                var end = Inspector.ResolvePath(path, out var error);
                if (end == null) return new { success = false, error };
                if (!Inspector.Write(end, value, out var before, out var after, out error)) return new { success = false, error };
                ValBridgeServerPlugin.ModLogger.LogInfo($"debug/set_value: {path}: {before} -> {after}");
                return new { success = true, path, before = Inspector.Describe(before, 1), after = Inspector.Describe(after, 1) };
            });
        }

        [Tool("debug/call_method", Description = "Run a method on a type (static) or on the object at a path, e.g. target 'Player.m_localPlayer', method 'GetHealth', args '[]'. A Vector3 argument can be [x, y, z] or \"x,y,z\". Returns what it returned.")]
        public object CallMethod(
            [ToolParameter(Description = "A type (for static methods) or a path to an object")] string target,
            [ToolParameter(Description = "Method name")] string method,
            [ToolParameter(Description = "Arguments as a JSON array, e.g. '[5, \"Wood\"]' (default none)")] string args = "[]",
            [ToolParameter(Description = "Levels of fields to show in the result (default 1)")] int depth = 1)
        {
            return MainThreadDispatcher.RunTool(() =>
            {
                JArray argList;
                try { argList = JArray.Parse(string.IsNullOrWhiteSpace(args) ? "[]" : args); }
                catch (Exception ex) { return new { success = false, error = $"args is not a JSON array: {ex.Message}" }; }

                var end = Inspector.ResolvePath(target, out var error);
                if (end == null) return new { success = false, error };
                object? instance = null;
                Type type = end.OwnerType;
                if (!end.IsTypeOnly)
                {
                    if (!Inspector.Read(end, out instance, out error)) return new { success = false, error };
                    if (instance == null || (instance is UnityEngine.Object uo && uo == null))
                        return new { success = false, error = $"'{target}' is null" };
                    type = instance.GetType();
                }

                var candidates = type.GetMethods(Inspector.All | BindingFlags.FlattenHierarchy)
                    .Where(m => m.Name == method && m.GetParameters().Length == argList.Count && !m.ContainsGenericParameters)
                    .Where(m => instance != null || m.IsStatic)
                    .ToList();
                if (candidates.Count == 0)
                {
                    var overloads = type.GetMethods(Inspector.All | BindingFlags.FlattenHierarchy).Where(m => m.Name == method)
                        .Select(m => $"{m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})").Distinct().ToList();
                    return new { success = false, error = overloads.Count == 0 ? $"{type.Name} has no method '{method}'" : $"No overload takes {argList.Count} argument(s). Have: {string.Join("; ", overloads)}" };
                }

                foreach (var m in candidates)
                {
                    object?[] values;
                    try { values = m.GetParameters().Select((p, i) => ConvertArg(argList[i], p.ParameterType)).ToArray(); }
                    catch { continue; }    // try the next overload

                    try
                    {
                        var result = m.Invoke(m.IsStatic ? null : instance, values);
                        ValBridgeServerPlugin.ModLogger.LogInfo($"debug/call_method: {type.Name}.{m.Name}({args})");
                        return new { success = true, method = $"{type.Name}.{m.Name}", returned = m.ReturnType == typeof(void) ? null : Inspector.Describe(result, Mathf.Clamp(depth, 0, 4)) };
                    }
                    catch (Exception ex)
                    {
                        var inner = ex.GetBaseException();
                        return new { success = false, error = $"{type.Name}.{m.Name} threw {inner.GetType().Name}: {inner.Message}" };
                    }
                }
                return new { success = false, error = "The arguments don't fit any overload's parameter types." };
            });
        }

        private static object? ConvertArg(JToken token, Type type)
        {
            if (token.Type == JTokenType.Null) return null;
            if (token.Type == JTokenType.String && type != typeof(string))
                return Inspector.ConvertText(token.Value<string>()!, type);
            if (token is JArray numbers && (type == typeof(Vector3) || type == typeof(Vector2)))
                return Inspector.ConvertText(string.Join(",", numbers.Select(n => n.ToString())), type);
            return token.ToObject(type);
        }
    }
}
