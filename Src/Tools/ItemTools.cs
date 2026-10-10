using System;
using System.Collections.Generic;
using System.Linq;
using Lib.GAB.Tools;
using UnityEngine;

namespace ValAgentBridge.Tools
{
    // Item stats from the game's item database (vanilla and modded items alike), in short answers.
    public class ItemTools
    {
        [Tool("items/find", Description = "Search all items (vanilla and modded) by name and/or type, optionally sorted by a stat, with short stats per item. E.g. type 'Consumable' sortBy 'health' = best health foods.")]
        public object Find(
            [ToolParameter(Description = "Text in the prefab or display name (optional)")] string query = "",
            [ToolParameter(Description = "Item type, e.g. Consumable, OneHandedWeapon, TwoHandedWeapon, Bow, Shield, Helmet, Chest, Legs, Shoulder, Material, Ammo (optional)")] string type = "",
            [ToolParameter(Description = "Sort by: health, stamina, eitr, food (health+stamina+eitr), damage, armor, block, weight (optional; highest first)")] string sortBy = "",
            [ToolParameter(Description = "Max results (default 20)")] int limit = 20)
        {
            return MainThreadDispatcher.RunTool(() =>
            {
                if (ObjectDB.instance == null) return new { success = false, error = "The item database isn't loaded yet." };
                ItemDrop.ItemData.ItemType? wanted = null;
                if (type.Length > 0)
                {
                    if (!Enum.TryParse<ItemDrop.ItemData.ItemType>(type, true, out var t))
                        return new { success = false, error = $"Unknown type '{type}'. Use one of: {string.Join(", ", Enum.GetNames(typeof(ItemDrop.ItemData.ItemType)))}" };
                    wanted = t;
                }

                var items = AllItems()
                    .Where(x => wanted == null || x.drop.m_itemData.m_shared.m_itemType == wanted)
                    .Where(x => query.Length == 0 ||
                                x.prefab.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                x.display.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();
                var key = SortKey(sortBy);
                if (sortBy.Length > 0 && key == null)
                    return new { success = false, error = $"Unknown sortBy '{sortBy}'. Use health, stamina, eitr, food, damage, armor, block or weight." };
                var ordered = key == null ? items.OrderBy(x => x.prefab) : items.OrderByDescending(x => key(x.drop.m_itemData));
                var shown = ordered.Take(Mathf.Clamp(limit, 1, 200)).Select(x => Summary(x.prefab, x.display, x.drop.m_itemData)).ToList();
                return new { success = true, matches = items.Count, shown = shown.Count, items = shown };
            });
        }

        [Tool("items/info", Description = "Everything useful about one item: stats, food values, damage by type, armor, stack size, weight, teleportable, and how to craft it.")]
        public object Info(
            [ToolParameter(Description = "Item prefab name, e.g. 'MooseKebab' (or its display name)")] string name)
        {
            return MainThreadDispatcher.RunTool(() =>
            {
                if (string.IsNullOrWhiteSpace(name)) return new { success = false, error = "name is required" };
                if (ObjectDB.instance == null) return new { success = false, error = "The item database isn't loaded yet." };
                var all = AllItems().ToList();
                var hit = all.FirstOrDefault(x => string.Equals(x.prefab, name, StringComparison.OrdinalIgnoreCase))
                          ?? all.FirstOrDefault(x => string.Equals(x.display, name, StringComparison.OrdinalIgnoreCase));
                if (hit == null)
                {
                    var close = all.Where(x => x.prefab.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0).Take(8).Select(x => x.prefab).ToList();
                    return new { success = false, error = close.Count == 0 ? $"No item '{name}'." : $"No item '{name}'. Did you mean: {string.Join(", ", close)}" };
                }

                var data = hit.drop.m_itemData;
                var s = data.m_shared;
                var d = s.m_damages;
                var recipe = ObjectDB.instance.GetRecipe(data);
                object? craft = recipe == null ? null : new
                {
                    station = recipe.m_craftingStation != null ? Localize(recipe.m_craftingStation.m_name) : "by hand",
                    stationLevel = recipe.m_minStationLevel,
                    makes = recipe.m_amount,
                    ingredients = recipe.m_resources.Where(r => r.m_resItem != null)
                        .Select(r => new { item = r.m_resItem.name, amount = r.m_amount, perUpgradeLevel = r.m_amountPerLevel }).ToList()
                };
                return new
                {
                    success = true,
                    summary = Summary(hit.prefab, hit.display, data),
                    description = Localize(s.m_description),
                    damageByType = new { d.m_damage, d.m_blunt, d.m_slash, d.m_pierce, d.m_chop, d.m_pickaxe, d.m_fire, d.m_frost, d.m_lightning, d.m_poison, d.m_spirit },
                    maxQuality = s.m_maxQuality,
                    durability = s.m_useDurability ? (float?)s.m_maxDurability : null,
                    teleportable = s.m_teleportable,
                    craft
                };
            });
        }

        private sealed class Entry
        {
            public string prefab = "";
            public string display = "";
            public ItemDrop drop = null!;
        }

        private static IEnumerable<Entry> AllItems()
        {
            foreach (var go in ObjectDB.instance.m_items)
            {
                if (go == null) continue;
                var drop = go.GetComponent<ItemDrop>();
                if (drop == null || drop.m_itemData?.m_shared == null) continue;
                yield return new Entry { prefab = go.name, display = Localize(drop.m_itemData.m_shared.m_name), drop = drop };
            }
        }

        private static object Summary(string prefab, string display, ItemDrop.ItemData data)
        {
            var s = data.m_shared;
            bool isFood = s.m_food > 0 || s.m_foodStamina > 0 || s.m_foodEitr > 0;
            float damage = s.m_damages.GetTotalDamage();
            return new
            {
                name = prefab,
                displayName = display,
                type = s.m_itemType.ToString(),
                food = isFood ? new { health = s.m_food, stamina = s.m_foodStamina, eitr = s.m_foodEitr, regen = s.m_foodRegen, minutes = Math.Round(s.m_foodBurnTime / 60f, 1) } : null,
                damage = damage > 0 ? (float?)damage : null,
                armor = s.m_armor > 0 && IsArmor(s.m_itemType) ? (float?)s.m_armor : null,
                block = s.m_blockPower > 0 && s.m_itemType == ItemDrop.ItemData.ItemType.Shield ? (float?)s.m_blockPower : null,
                maxStack = s.m_maxStackSize,
                weight = s.m_weight
            };
        }

        private static bool IsArmor(ItemDrop.ItemData.ItemType t) =>
            t == ItemDrop.ItemData.ItemType.Helmet || t == ItemDrop.ItemData.ItemType.Chest ||
            t == ItemDrop.ItemData.ItemType.Legs || t == ItemDrop.ItemData.ItemType.Shoulder;

        private static Func<ItemDrop.ItemData, float>? SortKey(string sortBy) => sortBy.ToLowerInvariant() switch
        {
            "health" => d => d.m_shared.m_food,
            "stamina" => d => d.m_shared.m_foodStamina,
            "eitr" => d => d.m_shared.m_foodEitr,
            "food" => d => d.m_shared.m_food + d.m_shared.m_foodStamina + d.m_shared.m_foodEitr,
            "damage" => d => d.m_shared.m_damages.GetTotalDamage(),
            "armor" => d => IsArmor(d.m_shared.m_itemType) ? d.m_shared.m_armor : 0f,
            "block" => d => d.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Shield ? d.m_shared.m_blockPower : 0f,
            "weight" => d => d.m_shared.m_weight,
            _ => null
        };

        private static string Localize(string token) =>
            Localization.instance != null && !string.IsNullOrEmpty(token) ? Localization.instance.Localize(token) : token ?? "";
    }
}
