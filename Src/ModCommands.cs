using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BepInEx.Bootstrap;
using Lib.GAB.Tools;
using Newtonsoft.Json.Linq;

namespace ValAgentBridge
{
    /// <summary>
    /// Lets any mod add bridge commands without referencing the bridge: the mod declares its own tiny
    /// attribute class named BridgeToolAttribute (any namespace) with a constructor (string name, string
    /// description) and puts it on static methods. The bridge finds them by name at startup. See TOOLS.md.
    /// </summary>
    public static class ModCommands
    {
        public sealed class Found
        {
            public string name = "";
            public string mod = "";
            public string method = "";
            public string? description;
            public bool registered;
            public string? problem;
        }

        public static readonly List<Found> All = new List<Found>();

        public static void RegisterAll(IToolRegistry registry)
        {
            var assemblies = Chainloader.PluginInfos.Values
                .Where(p => p.Instance != null)
                .Select(p => (mod: p.Metadata.Name, asm: p.Instance.GetType().Assembly))
                .Where(x => x.asm != typeof(ModCommands).Assembly)
                .GroupBy(x => x.asm).Select(g => g.First());

            foreach (var (mod, asm) in assemblies)
            {
                Type?[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types; }
                catch { continue; }

                foreach (var type in types.Where(t => t != null))
                {
                    MethodInfo[] methods;
                    try { methods = type!.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly); }
                    catch { continue; }
                    foreach (var method in methods)
                    {
                        var attr = method.GetCustomAttributes(false).FirstOrDefault(a => a.GetType().Name == "BridgeToolAttribute");
                        if (attr != null) Register(registry, mod, method, attr);
                    }
                }
            }
            if (All.Count > 0)
                ValAgentBridgePlugin.ModLogger.LogInfo($"Mod commands: {All.Count(f => f.registered)} registered, {All.Count(f => !f.registered)} skipped");
        }

        private static void Register(IToolRegistry registry, string mod, MethodInfo method, object attr)
        {
            var found = new Found
            {
                mod = mod,
                method = $"{method.DeclaringType?.FullName}.{method.Name}",
                name = Read(attr, "Name") ?? "",
                description = Read(attr, "Description")
            };
            All.Add(found);

            if (!ToolNameValidator.IsValid(found.name))
            {
                found.problem = $"name '{found.name}' must look like 'mymod/do_thing' (lowercase, at least one '/')";
                ValAgentBridgePlugin.ModLogger.LogWarning($"Mod command from {mod} skipped: {found.problem}");
                return;
            }
            if (registry.HasTool(found.name))
            {
                found.problem = "a command with this name already exists";
                ValAgentBridgePlugin.ModLogger.LogWarning($"Mod command {found.name} from {mod} skipped: {found.problem}");
                return;
            }

            var parameters = method.GetParameters();
            var info = new ToolInfo
            {
                Name = found.name,
                Description = $"[{mod}] {found.description}",
                Parameters = parameters.Select(p => new ToolParameterInfo
                {
                    Name = p.Name ?? "",
                    Type = p.ParameterType,
                    Required = !p.HasDefaultValue,
                    DefaultValue = p.HasDefaultValue ? p.DefaultValue : null
                }).ToList()
            };

            registry.RegisterTool(found.name, input =>
            {
                var problem = ToolGuard.CheckArgs(info, input);
                return Task.FromResult<object>(problem != null ? new { success = false, error = problem } : Invoke(method, parameters, input));
            }, info);
            found.registered = true;
        }

        private static object Invoke(MethodInfo method, ParameterInfo[] parameters, object input)
        {
            JObject args;
            try
            {
                args = input == null ? new JObject() : input is string s ? JObject.Parse(s) : JObject.FromObject(input);
            }
            catch (Exception ex)
            {
                return new { success = false, error = "Bad arguments: " + ex.Message };
            }

            object?[] values;
            try
            {
                values = parameters.Select(p =>
                {
                    var token = args.Properties().FirstOrDefault(x => string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase))?.Value;
                    if (token == null || token.Type == JTokenType.Null)
                        return p.HasDefaultValue ? p.DefaultValue : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null;
                    return token.ToObject(p.ParameterType);
                }).ToArray();
            }
            catch (Exception ex)
            {
                return new { success = false, error = "Bad arguments: " + ex.Message };
            }

            // Mod code expects Unity's main thread.
            return MainThreadDispatcher.RunTool(() => method.Invoke(null, values) ?? new { success = true }, 30000);
        }

        private static string? Read(object attr, string property)
        {
            var p = attr.GetType().GetProperty(property);
            if (p != null) return p.GetValue(attr) as string;
            var f = attr.GetType().GetField(property);
            return f?.GetValue(attr) as string;
        }
    }
}
