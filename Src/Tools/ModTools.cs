using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using Lib.GAB.Tools;
using UnityEngine;

namespace ValBridgeServer.Tools
{
    // Reading other mods: their BepInEx settings and, through reflection, their live state.
    public class ModTools
    {
        [Tool("mods/list", Description = "List loaded BepInEx mods (GUID, name, version, DLL).")]
        public object List(
            [ToolParameter(Description = "Only mods whose GUID or name contains this (optional)")] string filter = "")
        {
            var mods = Chainloader.PluginInfos.Values
                .Where(p => Matches(p, filter))
                .OrderBy(p => p.Metadata.Name)
                .Select(p => new { guid = p.Metadata.GUID, name = p.Metadata.Name, version = p.Metadata.Version.ToString(), dll = Path.GetFileName(p.Location) })
                .ToList();
            return new { success = true, count = mods.Count, mods };
        }

        [Tool("mods/get_config", Description = "Show a mod's BepInEx settings: section, key, current value, default and description.")]
        public object GetConfig(
            [ToolParameter(Description = "Mod GUID or (part of) its name")] string mod,
            [ToolParameter(Description = "Only settings whose section or key contains this (optional)")] string filter = "")
        {
            return MainThreadDispatcher.RunTool(() =>
            {
                var plugin = FindPlugin(mod, out var error);
                if (plugin == null) return new { success = false, error };
                var config = plugin.Instance.Config;
                var entries = config.Keys
                    .Where(k => filter.Length == 0 ||
                                k.Section.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                k.Key.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                    .Select(k => config[k])
                    .Select(e => new
                    {
                        section = e.Definition.Section,
                        key = e.Definition.Key,
                        value = e.GetSerializedValue(),
                        defaultValue = e.DefaultValue?.ToString(),
                        type = e.SettingType.Name,
                        description = e.Description?.Description
                    })
                    .ToList();
                return new { success = true, mod = plugin.Metadata.Name, file = config.ConfigFilePath, count = entries.Count, entries };
            });
        }

        [Tool("mods/set_config", Description = "Change one of a mod's BepInEx settings while the game runs (saved to its .cfg like the config manager does). Server-synced settings may be overridden by the server.")]
        public object SetConfig(
            [ToolParameter(Description = "Mod GUID or (part of) its name")] string mod,
            [ToolParameter(Description = "Section name")] string section,
            [ToolParameter(Description = "Key name")] string key,
            [ToolParameter(Description = "New value as it would be written in the .cfg file")] string value)
        {
            return MainThreadDispatcher.RunTool(() =>
            {
                var plugin = FindPlugin(mod, out var error);
                if (plugin == null) return new { success = false, error };
                var config = plugin.Instance.Config;
                var def = config.Keys.FirstOrDefault(k =>
                    string.Equals(k.Section, section, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(k.Key, key, StringComparison.OrdinalIgnoreCase));
                if (def == null) return new { success = false, error = $"{plugin.Metadata.Name} has no setting [{section}] {key}" };

                var entry = config[def];
                var before = entry.GetSerializedValue();
                entry.SetSerializedValue(value);
                var after = entry.GetSerializedValue();
                ValBridgeServerPlugin.ModLogger.LogInfo($"mods/set_config: {plugin.Metadata.Name} [{def.Section}] {def.Key}: {before} -> {after}");
                return new { success = true, mod = plugin.Metadata.Name, section = def.Section, key = def.Key, before, after, accepted = after != before || after == value };
            });
        }

        [Tool("mods/list_commands", Description = "Bridge commands added by other mods (via their own [BridgeTool] attribute), including ones skipped and why.")]
        public object ListCommands() => new
        {
            success = true,
            count = ModCommands.All.Count,
            commands = ModCommands.All.Select(f => new { f.name, f.mod, f.method, f.description, f.registered, f.problem }).ToList()
        };

        [Tool("debug/find_types", Description = "Find loaded types (game or mods) whose full name contains the text. Use it to find what to pass to debug/inspect.")]
        public object FindTypes(
            [ToolParameter(Description = "Text to look for, e.g. 'Backpack' or 'Config'")] string query,
            [ToolParameter(Description = "Max results (default 40)")] int limit = 40)
        {
            var found = Inspector.AllTypes()
                .Where(t => (t.FullName ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 && !t.Name.StartsWith("<"))
                .Take(limit)
                .Select(t => new { type = t.FullName, assembly = t.Assembly.GetName().Name })
                .ToList();
            return new { success = true, count = found.Count, types = found };
        }

        [Tool("debug/inspect", Description = "Read live values by path: a type, then static fields/properties, then fields of those, e.g. 'Player.m_localPlayer.m_inventory' or 'ZoneSystem.instance.m_globalKeys'. Reads fields only below the path; never changes anything.")]
        public object Inspect(
            [ToolParameter(Description = "Dotted path starting with a type name (full or short), e.g. 'ZoneSystem.instance.m_globalKeys'. A number segment indexes a list.")] string path,
            [ToolParameter(Description = "How many levels of fields to show under the result (default 2, max 5)")] int depth = 2)
        {
            depth = Mathf.Clamp(depth, 0, 5);
            return MainThreadDispatcher.RunTool(() =>
            {
                var end = Inspector.ResolvePath(path, out var error);
                if (end == null) return new { success = false, error };
                if (end.IsTypeOnly)
                    return new { success = true, path, type = end.OwnerType.FullName, value = DescribeStatics(end.OwnerType, depth) };
                if (!Inspector.Read(end, out var value, out error)) return new { success = false, error };
                return new { success = true, path, type = value == null ? null : Inspector.TypeName(value.GetType()), value = Inspector.Describe(value, depth) };
            });
        }

        [Tool("debug/inspect_object", Description = "List the components on a world object (instanceId from player/get_visible_objects or player/find_nearby_prefabs) and the fields of the game/mod ones.")]
        public object InspectObject(
            [ToolParameter(Description = "Instance ID of the object")] int instanceId,
            [ToolParameter(Description = "Only components whose type name contains this (optional)")] string component = "",
            [ToolParameter(Description = "Levels of fields to show per component (default 1, max 4)")] int depth = 1)
        {
            depth = Mathf.Clamp(depth, 0, 4);
            return MainThreadDispatcher.RunTool(() =>
            {
                var found = Resources.InstanceIDToObject(instanceId);
                GameObject? go = found as GameObject;
                if (go == null && found is Component c && c != null) go = c.gameObject;
                if (go == null) return new { success = false, error = $"No live object with instanceId {instanceId}" };

                var comps = go.GetComponentsInChildren<Component>(true)
                    .Where(x => x != null)
                    .Select(x => new
                    {
                        component = x,
                        path = x.transform == go.transform ? "" : RelativePath(go.transform, x.transform),
                        type = x.GetType()
                    })
                    .ToList();
                var shown = comps
                    .Where(x => !Inspector.IsUnityType(x.type))
                    .Where(x => component.Length == 0 || x.type.Name.IndexOf(component, StringComparison.OrdinalIgnoreCase) >= 0)
                    .Select(x => new { type = x.type.FullName, child = x.path, fields = Inspector.Describe(x.component, depth) })
                    .ToList();
                var pos = go.transform.position;
                return new
                {
                    success = true,
                    name = go.name,
                    position = new { x = pos.x, y = pos.y, z = pos.z },
                    allComponents = comps.Select(x => (x.path.Length > 0 ? x.path + ": " : "") + x.type.Name).ToList(),
                    components = shown
                };
            });
        }

        private static object DescribeStatics(Type type, int depth)
        {
            var result = new Dictionary<string, object?> { ["$type"] = type.FullName };
            foreach (var f in type.GetFields(Inspector.All | BindingFlags.DeclaredOnly).Where(f => f.IsStatic && !f.IsLiteral))
            {
                object? v;
                try { v = f.GetValue(null); }
                catch (Exception ex) { v = $"(error: {ex.GetType().Name})"; }
                result[Inspector.CleanName(f.Name)] = Inspector.Describe(v, depth);
            }
            return result;
        }

        private static string RelativePath(Transform root, Transform t)
        {
            var names = new List<string>();
            for (var x = t; x != null && x != root; x = x.parent) names.Add(x.name);
            names.Reverse();
            return string.Join("/", names);
        }

        private static bool Matches(PluginInfo p, string filter) =>
            filter.Length == 0 ||
            p.Metadata.GUID.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
            p.Metadata.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

        private static PluginInfo? FindPlugin(string mod, out string? error)
        {
            error = null;
            var all = Chainloader.PluginInfos.Values.ToList();
            var exact = all.FirstOrDefault(p => string.Equals(p.Metadata.GUID, mod, StringComparison.OrdinalIgnoreCase) ||
                                                string.Equals(p.Metadata.Name, mod, StringComparison.OrdinalIgnoreCase));
            var partial = all.Where(p => Matches(p, mod)).ToList();
            var pick = exact ?? (partial.Count == 1 ? partial[0] : null);
            if (pick != null)
            {
                if (pick.Instance == null) { error = $"{pick.Metadata.Name} has no loaded instance"; return null; }
                return pick;
            }
            error = partial.Count == 0
                ? $"No mod matches '{mod}'. Use mods/list."
                : $"'{mod}' matches several mods: {string.Join(", ", partial.Select(p => p.Metadata.Name))}";
            return null;
        }
    }
}
