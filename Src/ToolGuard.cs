using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lib.GAB.Tools;
using Newtonsoft.Json.Linq;

namespace ValAgentBridge
{
    /// <summary>
    /// Lib.GAB silently drops arguments with a name the tool doesn't have, so a guessed name ("path" for "target")
    /// ran the tool with a default instead. Our tools are registered through here: an unknown or missing argument
    /// answers { success: false, error } naming the arguments the tool takes.
    /// </summary>
    public static class ToolGuard
    {
        public static void RegisterChecked(IToolRegistry target, object instance)
        {
            var inner = new ToolRegistry();
            inner.RegisterToolsFromInstance(instance);
            foreach (var info in inner.GetTools())
            {
                var name = info.Name;
                target.RegisterTool(name, input =>
                {
                    var problem = CheckArgs(info, input);
                    return problem != null
                        ? Task.FromResult<object>(new { success = false, error = problem })
                        : inner.CallToolAsync(name, ListsFromStrings(info, input));
                }, info);
            }
        }

        public static string? CheckArgs(ToolInfo info, object? input)
        {
            JObject? args;
            try { args = input == null ? null : input as JObject ?? (input is string s ? JObject.Parse(s) : JObject.FromObject(input)); }
            catch { return null; }    // let Lib.GAB report what it can't read

            var known = info.Parameters;
            var given = args?.Properties().Select(p => p.Name).ToList() ?? new List<string>();
            var unknown = given.Where(g => !known.Any(p => string.Equals(p.Name, g, StringComparison.OrdinalIgnoreCase))).ToList();
            var missing = known.Where(p => p.Required && !given.Any(g => string.Equals(p.Name, g, StringComparison.OrdinalIgnoreCase)))
                .Select(p => p.Name).ToList();
            if (unknown.Count == 0 && missing.Count == 0) return null;

            var parts = new List<string>();
            if (unknown.Count > 0) parts.Add($"unknown argument{(unknown.Count > 1 ? "s" : "")} {string.Join(", ", unknown.Select(u => $"'{u}'"))}");
            if (missing.Count > 0) parts.Add($"missing {string.Join(", ", missing.Select(m => $"'{m}'"))}");
            return $"{info.Name}: {string.Join("; ", parts)}. It takes: {Describe(known)}.";
        }

        /// <summary>A list argument (e.g. ids) sent as a JSON string ("[1, 2]") becomes the list itself, so both work.</summary>
        private static object? ListsFromStrings(ToolInfo info, object? input)
        {
            JObject? args;
            try { args = input == null ? null : input as JObject ?? (input is string s ? JObject.Parse(s) : JObject.FromObject(input)); }
            catch { return input; }
            if (args == null) return input;
            JObject? copy = null;
            foreach (var p in info.Parameters)
            {
                if (p.Type == typeof(string) || !typeof(System.Collections.IEnumerable).IsAssignableFrom(p.Type)) continue;
                var prop = args.Properties().FirstOrDefault(a => string.Equals(a.Name, p.Name, StringComparison.OrdinalIgnoreCase));
                if (prop == null || prop.Value.Type != JTokenType.String) continue;
                var text = prop.Value.Value<string>()!.Trim();
                JToken parsed;
                try { parsed = text.StartsWith("[") ? JToken.Parse(text) : new JArray(text.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)); }
                catch (Exception) { continue; }    // Lib.GAB reports the bad value
                copy ??= (JObject)args.DeepClone();
                copy[prop.Name] = parsed;
            }
            return copy ?? input;
        }

        private static string Describe(List<ToolParameterInfo> parameters)
        {
            if (parameters.Count == 0) return "no arguments";
            return string.Join(", ", parameters.Select(p => p.Required ? $"{p.Name} (required)" : p.Name));
        }

        /// <summary>Closest tool names to a name that doesn't exist, best first.</summary>
        public static List<string> Suggest(IToolRegistry registry, string wanted, int max = 3)
        {
            wanted = wanted.Replace('.', '/').ToLowerInvariant();
            var area = wanted.Contains('/') ? wanted.Substring(0, wanted.IndexOf('/')) : "";
            var verb = wanted.Substring(wanted.IndexOf('/') + 1);
            return registry.GetTools().Select(t => t.Name)
                .Select(n =>
                {
                    var lower = n.ToLowerInvariant();
                    var nArea = lower.Substring(0, Math.Max(0, lower.IndexOf('/')));
                    var nVerb = lower.Substring(lower.IndexOf('/') + 1);
                    // Same area counts for a lot; then how alike the rest is (shared words help too).
                    int score = Distance(verb, nVerb) + (nArea == area ? 0 : 6);
                    if (verb.Split('_').Any(w => w.Length > 2 && nVerb.Contains(w))) score -= 3;
                    return (n, score);
                })
                .OrderBy(x => x.score).ThenBy(x => x.n).Take(max).Select(x => x.n).ToList();
        }

        private static int Distance(string a, string b)
        {
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            return d[a.Length, b.Length];
        }
    }
}
