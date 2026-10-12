using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ValAgentBridge
{
    /// <summary>
    /// Turns live objects into JSON-friendly trees for the debug tools. Reads fields only (never runs property
    /// getters, which can have side effects), stops at a depth and node budget, and treats destroyed Unity
    /// objects as such.
    /// </summary>
    public static class Inspector
    {
        public const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private const int MaxItems = 50;

        public static object? Describe(object? value, int depth, int nodeBudget = 3000)
        {
            int budget = nodeBudget;
            return Walk(value, depth, ref budget);
        }

        private static object? Walk(object? value, int depth, ref int budget)
        {
            if (--budget < 0) return "(output limit reached)";
            if (value == null) return null;
            if (value is UnityEngine.Object uo && uo == null) return "(destroyed)";

            var type = value.GetType();
            if (type.IsPrimitive || value is string || value is decimal) return value;
            if (type.IsEnum) return value.ToString();
            if (value is Type t) return t.FullName;
            if (value is Delegate d) return $"(delegate {d.Method.DeclaringType?.Name}.{d.Method.Name})";
            if (value is UnityEngine.Vector2 || value is UnityEngine.Vector3 || value is UnityEngine.Vector4 ||
                value is UnityEngine.Quaternion || value is UnityEngine.Color || value is UnityEngine.Vector2Int ||
                value is UnityEngine.Vector3Int)
                return value.ToString();
            if (value is UnityEngine.Object unityObj && depth <= 0)
                return $"{unityObj.name} ({type.Name})";
            if (depth <= 0) return $"({TypeName(type)})";

            if (value is IDictionary dict)
            {
                var result = new Dictionary<string, object?> { ["$count"] = dict.Count };
                int n = 0;
                foreach (DictionaryEntry e in dict)
                {
                    if (n++ >= MaxItems) { result["$more"] = dict.Count - MaxItems; break; }
                    result[e.Key?.ToString() ?? "null"] = Walk(e.Value, depth - 1, ref budget);
                }
                return result;
            }
            if (value is IEnumerable seq)
            {
                var items = new List<object?>();
                int n = 0;
                foreach (var item in seq)
                {
                    if (n++ >= MaxItems) { items.Add("(more items not shown)"); break; }
                    items.Add(Walk(item, depth - 1, ref budget));
                }
                return items;
            }

            var obj = new Dictionary<string, object?> { ["$type"] = TypeName(type) };
            if (value is UnityEngine.Object named) obj["$name"] = named.name;
            List<FieldInfo> fields;
            try { fields = InstanceFields(type).ToList(); }
            catch (Exception ex)
            {
                // Usually a mod type whose fields need another mod that isn't installed.
                obj["$fields"] = $"(unreadable: {ex.GetBaseException().GetType().Name}: {ex.GetBaseException().Message})";
                return obj;
            }
            foreach (var f in fields)
            {
                object? v;
                try { v = f.GetValue(value); }
                catch (Exception ex) { v = $"(error: {ex.GetType().Name})"; }
                obj[CleanName(f.Name)] = Walk(v, depth - 1, ref budget);
            }
            return obj;
        }

        /// <summary>Instance fields declared by game or mod code (Unity's own base classes are skipped).</summary>
        public static IEnumerable<FieldInfo> InstanceFields(Type type)
        {
            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                if (IsUnityType(t)) yield break;
                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    yield return f;
            }
        }

        public static bool IsUnityType(Type t) =>
            (t.Namespace ?? "").StartsWith("UnityEngine") || t.Assembly.GetName().Name.StartsWith("UnityEngine");

        // Auto-property backing fields read better under the property's name.
        public static string CleanName(string name) =>
            name.StartsWith("<") && name.Contains(">k__BackingField") ? name.Substring(1, name.IndexOf('>') - 1) : name;

        public static string TypeName(Type t) =>
            t.IsGenericType ? $"{t.Name.Split('`')[0]}<{string.Join(",", t.GetGenericArguments().Select(TypeName))}>" : t.Name;

        // Every loaded type, indexed once (rebuilt when an assembly loads): scanning all of them per lookup took
        // a few hundred ms with a big mod list, several times per debug/inspect call.
        private static readonly object IndexLock = new object();
        private static int _indexedAssemblies = -1;
        private static List<Type> _allTypes = new List<Type>();
        private static Dictionary<string, Type> _byFullName = new Dictionary<string, Type>();
        private static Dictionary<string, List<Type>> _byShortName = new Dictionary<string, List<Type>>();

        public static IReadOnlyList<Type> AllTypes()
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            lock (IndexLock)
            {
                if (assemblies.Length == _indexedAssemblies) return _allTypes;
                var all = new List<Type>();
                var byFull = new Dictionary<string, Type>();
                var byShort = new Dictionary<string, List<Type>>();
                foreach (var asm in assemblies)
                {
                    if (asm.IsDynamic) continue;
                    Type?[] types;
                    try { types = asm.GetTypes(); }
                    catch (ReflectionTypeLoadException e) { types = e.Types; }
                    catch { continue; }
                    foreach (var t in types)
                    {
                        if (t == null) continue;
                        all.Add(t);
                        var full = (t.FullName ?? t.Name).Replace('+', '.');
                        if (!byFull.ContainsKey(full)) byFull[full] = t;
                        if (!t.IsNested)
                        {
                            if (!byShort.TryGetValue(t.Name, out var list)) byShort[t.Name] = list = new List<Type>();
                            list.Add(t);
                        }
                    }
                }
                _allTypes = all;
                _byFullName = byFull;
                _byShortName = byShort;
                _indexedAssemblies = assemblies.Length;
                return _allTypes;
            }
        }

        /// <summary>A type by full name ("A.B.C", nested with '+' or '.') or by unique short name ("Player").</summary>
        public static Type? ResolveType(string name, out string? ambiguous)
        {
            ambiguous = null;
            AllTypes(); // make sure the index is current
            lock (IndexLock)
            {
                if (_byFullName.TryGetValue(name.Replace('+', '.'), out var exact)) return exact;
            }
            if (name.Contains('.')) return null;
            List<Type> shortMatches;
            lock (IndexLock)
            {
                shortMatches = _byShortName.TryGetValue(name, out var list) ? list : new List<Type>();
            }
            if (shortMatches.Count == 1) return shortMatches[0];
            if (shortMatches.Count > 1)
            {
                // The game's own type wins over same-named types in Unity or mods.
                var game = shortMatches.Where(t => t.Assembly.GetName().Name == "assembly_valheim").ToList();
                if (game.Count == 1) return game[0];
                ambiguous = string.Join(", ", shortMatches.Select(t => t.FullName).Take(6));
            }
            return null;
        }

        /// <summary>Where a dotted path ends: the object (or type, for statics) holding the last member.</summary>
        public sealed class PathEnd
        {
            public Type OwnerType = typeof(object);
            public object? Owner;          // null for a static member or a bare type
            public string? Member;         // null when the path is just a type
            public int Index = -1;         // last segment was a list index
            public bool IsTypeOnly => Member == null && Index < 0;
        }

        /// <summary>
        /// Resolves "Type.static.field.field" (a number segment indexes a list) down to the owner of its last
        /// segment. Everything before the last segment is read; the last one is left to the caller.
        /// </summary>
        public static PathEnd? ResolvePath(string path, out string? error)
        {
            error = null;
            var parts = path.Split('.');
            Type? type = null;
            int used = 0;
            // Longest prefix that names a type wins ("A.B.C.field" -> type A.B.C).
            for (int n = parts.Length; n >= 1 && type == null; n--)
            {
                var name = string.Join(".", parts.Take(n));
                type = ResolveType(name, out var ambiguous);
                if (ambiguous != null) { error = $"'{name}' matches several types: {ambiguous}. Use the full name."; return null; }
                if (type != null) used = n;
            }
            if (type == null) { error = $"No type found at the start of '{path}'. Try debug/find_types."; return null; }
            if (used == parts.Length) return new PathEnd { OwnerType = type };

            object? current = null;
            var currentType = type;
            for (int i = used; i < parts.Length - 1; i++)
            {
                if (!Step(currentType, current, parts[i], out current, out error)) return null;
                if (current == null || (current is UnityEngine.Object uo && uo == null))
                {
                    error = $"'{string.Join(".", parts.Take(i + 1))}' is null";
                    return null;
                }
                currentType = current.GetType();
            }
            var last = parts[parts.Length - 1];
            if (int.TryParse(last, out var index) && current is IList)
                return new PathEnd { OwnerType = currentType, Owner = current, Index = index };
            return new PathEnd { OwnerType = currentType, Owner = current, Member = last };
        }

        public static bool Read(PathEnd end, out object? value, out string? error)
        {
            value = null;
            error = null;
            if (end.Index >= 0)
            {
                var list = (IList)end.Owner!;
                if (end.Index >= list.Count) { error = $"Index {end.Index} is outside 0..{list.Count - 1}"; return false; }
                value = list[end.Index];
                return true;
            }
            try { return TryGetMember(end.OwnerType, end.Owner, end.Member!, out value, out error); }
            catch (Exception ex) { error = $"Reading '{end.Member}' threw {ex.GetBaseException().GetType().Name}: {ex.GetBaseException().Message}"; return false; }
        }

        /// <summary>Writes the last segment of a path (field, settable property or list slot).</summary>
        public static bool Write(PathEnd end, string text, out object? before, out object? after, out string? error)
        {
            before = after = null;
            error = null;
            if (end.IsTypeOnly) { error = "The path names a type, not a value."; return false; }
            if (end.Owner != null && end.OwnerType.IsValueType)
            {
                error = $"'{end.Member}' belongs to a struct copy ({end.OwnerType.Name}); writing it would change nothing. Set the whole struct one level up.";
                return false;
            }
            try
            {
                if (end.Index >= 0)
                {
                    var list = (IList)end.Owner!;
                    var elemType = end.OwnerType.IsArray ? end.OwnerType.GetElementType()! :
                        end.OwnerType.IsGenericType ? end.OwnerType.GetGenericArguments()[0] : typeof(object);
                    before = list[end.Index];
                    list[end.Index] = ConvertText(text, elemType);
                    after = list[end.Index];
                    return true;
                }
                var flags = All | BindingFlags.FlattenHierarchy;
                for (var t = end.OwnerType; t != null; t = t.BaseType)
                {
                    var f = t.GetField(end.Member!, flags | BindingFlags.DeclaredOnly)
                            ?? t.GetField($"<{end.Member}>k__BackingField", flags | BindingFlags.DeclaredOnly);
                    if (f != null)
                    {
                        if (f.IsLiteral) { error = $"'{end.Member}' is a constant"; return false; }
                        if (!f.IsStatic && end.Owner == null) { error = $"'{end.Member}' needs an instance"; return false; }
                        var target = f.IsStatic ? null : end.Owner;
                        before = f.GetValue(target);
                        f.SetValue(target, ConvertText(text, f.FieldType));
                        after = f.GetValue(target);
                        return true;
                    }
                    var p = t.GetProperty(end.Member!, flags | BindingFlags.DeclaredOnly);
                    if (p != null && p.GetIndexParameters().Length == 0)
                    {
                        var setter = p.GetSetMethod(true);
                        if (setter == null) { error = $"'{end.Member}' is read-only"; return false; }
                        if (!setter.IsStatic && end.Owner == null) { error = $"'{end.Member}' needs an instance"; return false; }
                        var target = setter.IsStatic ? null : end.Owner;
                        before = p.GetGetMethod(true)?.Invoke(target, null);
                        setter.Invoke(target, new[] { ConvertText(text, p.PropertyType) });
                        after = p.GetGetMethod(true)?.Invoke(target, null);
                        return true;
                    }
                }
                error = $"{end.OwnerType.Name} has no field or property '{end.Member}'";
                return false;
            }
            catch (Exception ex)
            {
                error = $"Could not set '{end.Member}': {ex.GetBaseException().Message}";
                return false;
            }
        }

        /// <summary>Text as typed by a person or a script -> a value of the given type.</summary>
        public static object? ConvertText(string text, Type type)
        {
            if (type == typeof(string)) return text;
            if (text == "null" && !type.IsValueType) return null;
            var underlying = Nullable.GetUnderlyingType(type) ?? type;
            if (underlying.IsEnum) return Enum.Parse(underlying, text, ignoreCase: true);
            if (underlying.IsPrimitive || underlying == typeof(decimal))
                return Convert.ChangeType(text, underlying, System.Globalization.CultureInfo.InvariantCulture);
            if (underlying == typeof(UnityEngine.Vector3) || underlying == typeof(UnityEngine.Vector2))
            {
                var v = text.Trim('(', ')', '[', ']', ' ').Split(',').Select(s => float.Parse(s.Trim(), System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                return underlying == typeof(UnityEngine.Vector2)
                    ? (object)new UnityEngine.Vector2(v[0], v[1])
                    : new UnityEngine.Vector3(v[0], v.Length > 1 ? v[1] : 0f, v.Length > 2 ? v[2] : 0f);
            }
            return Newtonsoft.Json.JsonConvert.DeserializeObject(text, underlying);
        }

        private static bool Step(Type type, object? instance, string seg, out object? next, out string? error)
        {
            next = null;
            error = null;
            if (int.TryParse(seg, out var index) && instance is IList list)
            {
                if (index < 0 || index >= list.Count) { error = $"Index {index} is outside 0..{list.Count - 1}"; return false; }
                next = list[index];
                return true;
            }
            try { return TryGetMember(type, instance, seg, out next, out error); }
            catch (Exception ex) { error = $"Reading '{seg}' threw {ex.GetBaseException().GetType().Name}: {ex.GetBaseException().Message}"; return false; }
        }

        /// <summary>Reads a field, auto-property or plain property (by name) on a type or instance.</summary>
        public static bool TryGetMember(Type type, object? instance, string name, out object? value, out string? error)
        {
            value = null;
            error = null;
            var flags = All | BindingFlags.FlattenHierarchy;
            for (var t = type; t != null; t = t.BaseType)
            {
                var f = t.GetField(name, flags | BindingFlags.DeclaredOnly)
                        ?? t.GetField($"<{name}>k__BackingField", flags | BindingFlags.DeclaredOnly);
                if (f != null)
                {
                    if (!f.IsStatic && instance == null) { error = $"'{name}' needs an instance"; return false; }
                    value = f.GetValue(f.IsStatic ? null : instance);
                    return true;
                }
                // A property is only read when the path names it explicitly.
                var p = t.GetProperty(name, flags | BindingFlags.DeclaredOnly);
                if (p != null && p.GetIndexParameters().Length == 0 && p.GetGetMethod(true) != null)
                {
                    var getter = p.GetGetMethod(true);
                    if (!getter.IsStatic && instance == null) { error = $"'{name}' needs an instance"; return false; }
                    value = getter.Invoke(getter.IsStatic ? null : instance, null);
                    return true;
                }
            }
            error = $"{type.Name} has no field or property '{name}'";
            return false;
        }
    }
}
