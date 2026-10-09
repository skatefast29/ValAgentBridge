using System;
using System.Collections.Generic;
using System.Linq;
using Lib.GAB.Tools;
using UnityEngine;

namespace ValBridgeServer.Tools
{
    // Network prefabs (everything that can exist in the world) by name, e.g. to find a creature for world/spawn_creature.
    public class ZNetSceneTools
    {
        [Tool("znetscene/search_prefabs", Description = "Find world prefabs by (part of) name, with what kind each is: creature, item, piece (buildable) or other. Empty filter lists the first ones alphabetically.")]
        public object SearchPrefabs(
            [ToolParameter(Description = "Part of the name, case-insensitive (empty = all)")] string nameFilter = "",
            [ToolParameter(Description = "Only this kind: creature, item, piece or other (optional)")] string kind = "",
            [ToolParameter(Description = "Max results (default 50)")] int maxResults = 50)
        {
            return MainThreadDispatcher.RunTool(() =>
            {
                if (ZNetScene.instance == null) return new { success = false, error = "Not in a world (or still loading)." };
                var matches = ZNetScene.instance.m_prefabs
                    .Where(p => p != null && (nameFilter.Length == 0 || p.name.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) >= 0))
                    .Select(p => new { name = p.name, kind = KindOf(p) })
                    .Where(p => kind.Length == 0 || string.Equals(p.kind, kind, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(p => p.name)
                    .ToList();
                return new { success = true, matches = matches.Count, prefabs = matches.Take(Mathf.Clamp(maxResults, 1, 500)).ToList() };
            });
        }

        [Tool("znetscene/get_prefab", Description = "One world prefab by exact name: kind, layer and its components.")]
        public object GetPrefab(
            [ToolParameter(Description = "Exact prefab name")] string prefabName)
        {
            return MainThreadDispatcher.RunTool(() =>
            {
                if (ZNetScene.instance == null) return new { success = false, error = "Not in a world (or still loading)." };
                var prefab = ZNetScene.instance.GetPrefab(prefabName);
                if (prefab == null) return new { success = false, error = $"No prefab '{prefabName}'. Use znetscene/search_prefabs." };
                return new
                {
                    success = true,
                    name = prefab.name,
                    kind = KindOf(prefab),
                    layer = LayerMask.LayerToName(prefab.layer),
                    components = prefab.GetComponents<Component>().Where(c => c != null).Select(c => c.GetType().Name).ToList()
                };
            });
        }

        private static string KindOf(GameObject prefab)
        {
            if (prefab.GetComponent<Character>() != null) return "creature";
            if (prefab.GetComponent<ItemDrop>() != null) return "item";
            if (prefab.GetComponent<Piece>() != null) return "piece";
            return "other";
        }
    }
}
