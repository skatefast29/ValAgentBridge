using System;
using System.Collections.Generic;
using System.Linq;
using Lib.GAB.Tools;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ValAgentBridge.Tools
{
    // Browsing Unity's object tree: every GameObject in the loaded scenes (world objects, menus, HUD, mod
    // objects kept across scenes), not just the networked world objects the other tools see. Reads only.
    public class SceneTools
    {
        private const int MaxVisited = 300000;

        [Tool("debug/scene_tree", Description = "Browse the tree of Unity objects: without a start, the root objects of every loaded scene (and the ones kept across scenes, where mods and the game keep managers). Each object has its name, instanceId, active state, component types and children. Many same-named children (e.g. thousands of trees) are counted by name instead of listed. Reads only.")]
        public object SceneTree(
            [ToolParameter(Description = "Start at this object: its path of names from a root, e.g. '_GameMain/GUI/PixelFix' (optional)")] string path = "",
            [ToolParameter(Description = "Or start at this instanceId (from this tool, debug/find_objects or player/get_visible_objects)")] int instanceId = 0,
            [ToolParameter(Description = "Levels of children to show (default 2, max 6)")] int depth = 2,
            [ToolParameter(Description = "Most children listed per object; above this they are counted by name (default 40)")] int maxChildren = 40,
            [ToolParameter(Description = "Show inactive (hidden) objects too (default true)")] bool includeInactive = true)
        {
            depth = Mathf.Clamp(depth, 0, 6);
            maxChildren = Mathf.Clamp(maxChildren, 1, 500);
            return MainThreadDispatcher.RunTool(() =>
            {
                int budget = 2000;
                if (path.Length == 0 && instanceId == 0)
                {
                    var scenes = LoadedScenes().Select(s =>
                    {
                        var roots = s.GetRootGameObjects().Where(g => includeInactive || g.activeSelf).ToList();
                        return new
                        {
                            scene = s.name,
                            rootCount = roots.Count,
                            roots = Children(roots.Select(g => g.transform).ToList(), depth, maxChildren, includeInactive, ref budget)
                        };
                    }).ToList();
                    return new { success = true, scenes, truncated = budget < 0 };
                }

                var start = FindStart(path, instanceId, out var error, out var warning);
                if (start == null) return new { success = false, error };
                var node = Node(start, depth, maxChildren, includeInactive, ref budget);
                return new { success = true, path = PathOf(start), scene = start.gameObject.scene.name, warning, node, truncated = budget < 0 };
            });
        }

        [Tool("debug/find_objects", Description = "Search the Unity object tree (all loaded scenes, or below one object) for objects whose name contains a text and/or that have a component whose type name contains a text, e.g. component 'Canvas' or 'Light'. Returns path, instanceId, position and components of each. Reads only.")]
        public object FindObjects(
            [ToolParameter(Description = "Text the object's name contains (optional if component is given)")] string name = "",
            [ToolParameter(Description = "Text a component's type name contains, e.g. 'Canvas', 'ParticleSystem', 'Smelter' (optional)")] string component = "",
            [ToolParameter(Description = "Only below this object: path of names from a root (optional)")] string under = "",
            [ToolParameter(Description = "Or only below this instanceId (optional)")] int underId = 0,
            [ToolParameter(Description = "Include inactive (hidden) objects (default true)")] bool includeInactive = true,
            [ToolParameter(Description = "Max results (default 50)")] int limit = 50)
        {
            if (name.Length == 0 && component.Length == 0)
                return new { success = false, error = "Give name and/or component." };
            limit = Mathf.Clamp(limit, 1, 500);
            return MainThreadDispatcher.RunTool(() =>
            {
                List<Transform> starts;
                string? warning = null;
                if (under.Length > 0 || underId != 0)
                {
                    var start = FindStart(under, underId, out var error, out warning);
                    if (start == null) return new { success = false, error };
                    starts = new List<Transform> { start };
                }
                else
                {
                    starts = LoadedScenes().SelectMany(s => s.GetRootGameObjects()).Select(g => g.transform).ToList();
                }

                var hits = new List<object>();
                int matched = 0, visited = 0;
                var stack = new Stack<Transform>(Enumerable.Reverse(starts));
                while (stack.Count > 0 && visited < MaxVisited)
                {
                    var t = stack.Pop();
                    if (t == null) continue;
                    var go = t.gameObject;
                    if (!includeInactive && !go.activeSelf) continue;
                    visited++;

                    if (Matches(go, name, component))
                    {
                        if (++matched <= limit)
                        {
                            var pos = t.position;
                            hits.Add(new
                            {
                                path = PathOf(t),
                                scene = go.scene.name,
                                instanceId = go.GetInstanceID(),
                                active = go.activeInHierarchy,
                                position = new { x = pos.x, y = pos.y, z = pos.z },
                                components = ComponentNames(go)
                            });
                        }
                    }
                    for (int i = t.childCount - 1; i >= 0; i--) stack.Push(t.GetChild(i));
                }
                return new
                {
                    success = true,
                    warning,
                    matched,
                    shown = hits.Count,
                    visited,
                    stoppedEarly = visited >= MaxVisited,
                    objects = hits
                };
            });
        }

        [Tool("debug/set_active", Description = "(cheat) Switch a Unity object on or off, like hiding it, e.g. a mod's object, a menu part or an effect, to see whether a problem or a frame-time cost goes with it. Its children go with it. Not saved: a world object switched off comes back when it is reloaded. Switch it back afterwards (the answer gives before). Logged.")]
        public object SetActive(
            [ToolParameter(Description = "true = on, false = off")] bool active,
            [ToolParameter(Description = "The object's instanceId (from debug/scene_tree or debug/find_objects)")] int instanceId = 0,
            [ToolParameter(Description = "Or its path of names from a root, e.g. '_GameMain/LoadingGUI'")] string path = "")
        {
            if (instanceId == 0 && path.Length == 0) return new { success = false, error = "Give instanceId or path." };
            return MainThreadDispatcher.RunTool(() =>
            {
                var t = FindStart(path, instanceId, out var error, out var warning);
                if (t == null) return new { success = false, error };
                // A path that matches several objects could switch the wrong one: only an instanceId may then.
                if (warning != null) return new { success = false, error = warning };
                var go = t.gameObject;
                if (Player.m_localPlayer != null && go == Player.m_localPlayer.gameObject)
                    return new { success = false, error = "That is the player character; switching it off breaks the game." };
                // Switching off the bridge's own objects (or BepInEx_Manager, which holds every mod) would stop the
                // bridge, and nothing could switch them back on.
                var ours = typeof(SceneTools).Assembly;
                if (!active && go.GetComponentsInChildren<Component>(true).Any(c => c != null && c.GetType().Assembly == ours))
                    return new { success = false, error = "That object holds part of ValAgentBridge; switching it off would cut the connection." };
                bool before = go.activeSelf;
                go.SetActive(active);
                ValAgentBridgePlugin.ModLogger.LogInfo($"debug/set_active: {PathOf(t)} (instanceId {go.GetInstanceID()}): {before} -> {go.activeSelf}");
                return new
                {
                    success = true,
                    path = PathOf(t),
                    instanceId = go.GetInstanceID(),
                    before,
                    after = go.activeSelf,
                    activeInHierarchy = go.activeInHierarchy,
                    note = go.activeSelf && !go.activeInHierarchy ? "On, but a parent is off, so it still doesn't show." : null
                };
            });
        }

        private static object Node(Transform t, int depth, int maxChildren, bool includeInactive, ref int budget)
        {
            budget--;
            var go = t.gameObject;
            var node = new Dictionary<string, object?>
            {
                ["name"] = go.name,
                ["instanceId"] = go.GetInstanceID(),
                ["active"] = go.activeSelf,
                ["components"] = ComponentNames(go),
                ["childCount"] = t.childCount
            };
            if (go.activeSelf && !go.activeInHierarchy) node["hiddenByParent"] = true;
            if (t.childCount > 0 && depth > 0)
            {
                var kids = new List<Transform>(t.childCount);
                for (int i = 0; i < t.childCount; i++)
                {
                    var c = t.GetChild(i);
                    if (includeInactive || c.gameObject.activeSelf) kids.Add(c);
                }
                node["children"] = Children(kids, depth - 1, maxChildren, includeInactive, ref budget);
            }
            return node;
        }

        // A list of objects, or (when there are too many, or the output budget is used up) a count per name.
        private static object Children(List<Transform> kids, int depth, int maxChildren, bool includeInactive, ref int budget)
        {
            bool tooMany = kids.Count > maxChildren;
            if (!tooMany && budget > kids.Count)
            {
                var list = new List<object>(kids.Count);
                foreach (var k in kids) list.Add(Node(k, depth, maxChildren, includeInactive, ref budget));
                return list;
            }
            // One-off names (managers like _GameMain) are what one usually looks for: listed first, not drowned
            // out by thousands of trees and rocks.
            var byName = kids.GroupBy(k => k.name).ToList();
            var single = byName.Where(g => g.Count() == 1).Select(g => g.First())
                .OrderBy(k => k.name.EndsWith("(Clone)") ? 1 : 0) // spawned copies after the scene's own objects
                .Take(maxChildren).ToList();
            budget -= single.Count + maxChildren;
            var groups = byName.Where(g => g.Count() > 1)
                .Select(g => new { name = g.Key, count = g.Count(), firstInstanceId = g.First().gameObject.GetInstanceID() })
                .OrderByDescending(g => g.count)
                .ToList();
            return new
            {
                note = (tooMany ? $"{kids.Count} objects" : $"{kids.Count} objects, shown in short because the answer reached its size limit (ask for less depth or start lower down)")
                       + "; names that occur once are listed, the rest counted by name. Use debug/find_objects or a firstInstanceId to look at one",
                single = single.Select(k => new
                {
                    name = k.name,
                    instanceId = k.gameObject.GetInstanceID(),
                    active = k.gameObject.activeSelf,
                    childCount = k.childCount,
                    components = ComponentNames(k.gameObject)
                }).ToList(),
                moreSingle = Math.Max(0, byName.Count(g => g.Count() == 1) - single.Count),
                byName = groups.Take(maxChildren).ToList(),
                moreNames = Math.Max(0, groups.Count - maxChildren)
            };
        }

        private static bool Matches(GameObject go, string name, string component)
        {
            if (name.Length > 0 && go.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) return false;
            if (component.Length == 0) return true;
            foreach (var c in go.GetComponents<Component>())
                if (c != null && c.GetType().Name.IndexOf(component, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static List<string> ComponentNames(GameObject go) =>
            go.GetComponents<Component>()
                .Where(c => c != null && !(c is Transform && !(c is RectTransform)))
                .Select(c => c.GetType().Name)
                .ToList();

        private static Transform? FindStart(string path, int instanceId, out string? error, out string? warning)
        {
            error = null;
            warning = null;
            if (instanceId != 0)
            {
                var found = Resources.InstanceIDToObject(instanceId);
                GameObject? go = found as GameObject;
                if (go == null && found is Component c && c != null) go = c.gameObject;
                if (go == null) { error = $"No live object with instanceId {instanceId}"; return null; }
                return go.transform;
            }

            var parts = path.Trim('/').Split('/');
            var roots = LoadedScenes().SelectMany(s => s.GetRootGameObjects()).Where(g => g.name == parts[0]).ToList();
            if (roots.Count == 0) { error = $"No root object named '{parts[0]}'. Call debug/scene_tree without a start to see the roots."; return null; }
            var t = roots[0].transform;
            var shared = new List<string>();
            if (roots.Count > 1) shared.Add($"{parts[0]} ({roots.Count})");
            for (int i = 1; i < parts.Length; i++)
            {
                Transform? next = null;
                int same = 0;
                for (int k = 0; k < t.childCount; k++)
                {
                    if (t.GetChild(k).name != parts[i]) continue;
                    if (next == null) next = t.GetChild(k);
                    same++;
                }
                if (next == null) { error = $"'{string.Join("/", parts.Take(i))}' has no child named '{parts[i]}'"; return null; }
                if (same > 1) shared.Add($"{parts[i]} ({same})");
                t = next;
            }
            // A path of names can't tell same-named objects apart: say so, the instanceId can.
            if (shared.Count > 0)
                warning = $"Several objects share these names, the first was taken: {string.Join(", ", shared)}. Use the instanceId to pick another.";
            return t;
        }

        private static string PathOf(Transform t)
        {
            var names = new List<string>();
            for (var x = t; x != null; x = x.parent) names.Add(x.name);
            names.Reverse();
            return string.Join("/", names);
        }

        // The loaded scenes plus the hidden one holding objects kept across scene loads (DontDestroyOnLoad).
        private static Scene? _keptScene;

        private static List<Scene> LoadedScenes()
        {
            var scenes = new List<Scene>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (s.isLoaded) scenes.Add(s);
            }
            if (_keptScene == null)
            {
                // Unity has no direct way to reach that scene: an object moved into it shows which one it is.
                var probe = new GameObject("ValAgentBridge scene probe");
                UnityEngine.Object.DontDestroyOnLoad(probe);
                _keptScene = probe.scene;
                UnityEngine.Object.DestroyImmediate(probe);
            }
            if (_keptScene.Value.IsValid()) scenes.Add(_keptScene.Value);
            return scenes;
        }
    }
}
