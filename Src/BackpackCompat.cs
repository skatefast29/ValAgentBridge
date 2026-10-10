using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace ValAgentBridge
{
    /// <summary>
    /// Inventories of equipped backpacks from backpack mods, through their public APIs, by reflection so the bridge
    /// doesn't depend on them:
    ///  - Adventure Backpacks (vapok): AdventureBackpacks.API.ABAPI.GetEquippedBackpackInventory(Player)
    ///  - Backpacks (Smoothbrain): Backpacks.API.GetEquippedBackpackInventory()
    /// A mod whose API is missing or changed is skipped with one warning.
    /// </summary>
    public static class BackpackCompat
    {
        private sealed class Source
        {
            public string mod = "";
            public MethodInfo method = null!;
            public bool takesPlayer;
        }

        private static List<Source>? _sources;

        public static List<(string mod, Inventory inventory)> EquippedBackpacks(Player player)
        {
            var result = new List<(string, Inventory)>();
            if (player == null) return result;
            foreach (var s in Sources())
            {
                try
                {
                    if (s.method.Invoke(null, s.takesPlayer ? new object[] { player } : Array.Empty<object>()) is Inventory inv)
                        result.Add((s.mod, inv));
                }
                catch (Exception)
                {
                    // No backpack equipped or the mod is mid-update: nothing to show.
                }
            }
            return result;
        }

        private static List<Source> Sources()
        {
            if (_sources != null) return _sources;
            _sources = new List<Source>();
            Add("Adventure Backpacks", "AdventureBackpacks.API.ABAPI", new[] { typeof(Player) });
            Add("Backpacks", "Backpacks.API", Type.EmptyTypes);
            return _sources;
        }

        private static void Add(string mod, string typeName, Type[] args)
        {
            var api = AccessTools.TypeByName(typeName);
            if (api == null) return;   // mod not installed
            var method = AccessTools.Method(api, "GetEquippedBackpackInventory", args);
            if (method == null || !method.IsStatic || method.ReturnType != typeof(Inventory))
            {
                ValAgentBridgePlugin.ModLogger.LogWarning($"{mod} API changed (GetEquippedBackpackInventory); its backpack contents won't be shown.");
                return;
            }
            _sources!.Add(new Source { mod = mod, method = method, takesPlayer = args.Length == 1 });
        }
    }
}
