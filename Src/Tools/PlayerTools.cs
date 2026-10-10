using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lib.GAB.Tools;
using UnityEngine;

namespace ValAgentBridge.Tools
{
    public class PlayerTools
    {
        private static readonly object NoPlayer = new { success = false, error = "No local player found" };

        /// <summary>Runs a tool body on the main thread with the local player; errors and timeouts become answers.</summary>
        private static object WithPlayer(Func<Player, object> work) =>
            MainThreadDispatcher.RunTool(() =>
            {
                var player = Player.m_localPlayer;
                return player == null ? NoPlayer : work(player);
            });

        private static object Fail(string error) => new { success = false, error };

        private static object Vec(Vector3 v) => new { x = v.x, y = v.y, z = v.z };

        // ---------- State ----------

        [Tool("player/get_health", Description = "Get player's current health statistics")]
        public object GetHealth() => WithPlayer(player => new
        {
            success = true,
            health = player.GetHealth(),
            maxHealth = player.GetMaxHealth(),
            healthPercentage = player.GetHealthPercentage() * 100f
        });

        [Tool("player/get_player_state", Description = "Get a full snapshot of the player: health, stamina, position, biome, equipped weapon, status effects, and food buffs.")]
        public object GetPlayerState() => WithPlayer(player =>
        {
            var effects = player.GetSEMan().GetStatusEffects().Select(se => new
            {
                name = se.m_name,
                timeRemaining = se.m_ttl > 0 ? (float?)Math.Round(se.GetRemaningTime(), 1) : null
            }).ToList();

            var foods = player.GetFoods().Select(f => new
            {
                name = f.m_name,
                health = (float)Math.Round(f.m_health, 1),
                stamina = (float)Math.Round(f.m_stamina, 1),
                timeRemaining = (float)Math.Round(f.m_time, 1)
            }).ToList();

            var weapon = player.GetCurrentWeapon();
            object? weaponInfo = weapon != null && weapon.m_dropPrefab != null
                ? new { name = weapon.m_dropPrefab.name, durability = (float)Math.Round(weapon.GetDurabilityPercentage() * 100f, 1) }
                : null;

            return new
            {
                success = true,
                health = (float)Math.Round(player.GetHealth(), 1),
                maxHealth = (float)Math.Round(player.GetMaxHealth(), 1),
                stamina = (float)Math.Round(player.GetStamina(), 1),
                maxStamina = (float)Math.Round(player.GetMaxStamina(), 1),
                position = Vec(player.transform.position),
                lookDirection = Vec(player.GetLookDir()),
                biome = player.GetCurrentBiome().ToString(),
                weapon = weaponInfo,
                statusEffects = effects,
                foods
            };
        });

        [Tool("player/get_inventory", Description = "List all items in the player's inventory with name, quantity, slot, equipped status and durability, the weight against the carry limit, and the contents of an equipped backpack (Adventure Backpacks or Backpacks).")]
        public object GetInventory() => WithPlayer(player =>
        {
            var inv = player.GetInventory();
            var itemList = DescribeItems(inv);

            // Backpack mods keep the backpack's contents in a separate inventory.
            var backpacks = BackpackCompat.EquippedBackpacks(player).Select(b => new
            {
                mod = b.mod,
                count = b.inventory.GetAllItems().Count,
                totalWeight = (float)Math.Round(b.inventory.GetTotalWeight(), 1),
                emptySlots = b.inventory.GetEmptySlots(),
                items = DescribeItems(b.inventory)
            }).ToList();

            return new
            {
                success = true,
                count = itemList.Count,
                totalWeight = (float)Math.Round(inv.GetTotalWeight(), 1),
                carryLimit = (float)Math.Round(player.GetMaxCarryWeight(), 1),
                encumbered = player.IsEncumbered(),
                emptySlots = inv.GetEmptySlots(),
                items = itemList,
                backpacks
            };
        });

        [Tool("player/get_position", Description = "Get player's world position coordinates")]
        public object GetPosition() => WithPlayer(player => new { success = true, position = Vec(player.transform.position) });

        // ---------- Items ----------

        [Tool("player/give_item", Description = "Cheat: put items straight into the player's inventory (works without devcommands). Fills stacks; returns how many fit. Items are marked as cheated, like the game's spawn command does.")]
        public object GiveItem(
            [ToolParameter(Description = "Item prefab name, e.g. 'MooseKebab', 'IronOre'")] string itemName,
            [ToolParameter(Description = "How many (default 1; a full stack is the item's max stack size)")] int amount = 1)
        {
            if (string.IsNullOrWhiteSpace(itemName)) return Fail("itemName is required");
            return WithPlayer(player =>
            {
                var prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(itemName) : null;
                var drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
                if (prefab == null || drop == null) return Fail($"No item prefab named '{itemName}' (names are case-sensitive, e.g. 'IronOre')");

                var inv = player.GetInventory();
                int maxStack = Math.Max(1, drop.m_itemData.m_shared.m_maxStackSize);
                int given = 0;
                while (given < amount)
                {
                    int chunk = Math.Min(maxStack, amount - given);
                    if (inv.AddItem(prefab.name, chunk, 1, 0, 0L, "", cheated: true) == null) break;
                    given += chunk;
                }
                ValAgentBridgePlugin.ModLogger.LogInfo($"player/give_item: {given}/{amount} {prefab.name}");
                return new
                {
                    success = given > 0,
                    item = prefab.name,
                    given,
                    requested = amount,
                    error = given < amount ? "Inventory full; the rest did not fit" : null
                };
            });
        }

        [Tool("player/pickup_nearby", Description = "Pick up loose item drops of one kind within range of the player. Give itemName; '*' picks up everything in range (careful: other people's or deliberately left items too).")]
        public object PickupNearby(
            [ToolParameter(Description = "Item prefab name to pick up, e.g. 'MooseKebab' (exact, case-insensitive), or '*' for everything")] string itemName,
            [ToolParameter(Description = "Pickup radius in meters (default 5)")] float range = 5f)
        {
            if (string.IsNullOrWhiteSpace(itemName))
                return Fail("Say which item to pick up (itemName), or '*' for everything in range.");
            bool everything = itemName.Trim() == "*";

            return WithPlayer(player =>
            {
                var origin = player.transform.position + Vector3.up;
                var pickedUp = new List<string>();
                foreach (var col in Physics.OverlapSphere(origin, range, LayerMask.GetMask("item")))
                {
                    if (col == null || col.attachedRigidbody == null) continue;
                    var itemDrop = col.attachedRigidbody.GetComponent<ItemDrop>();
                    if (itemDrop == null) continue;
                    var znv = itemDrop.GetComponent<ZNetView>();
                    if (znv == null || !znv.IsValid()) continue;

                    itemDrop.Load();
                    if (!everything &&
                        !string.Equals(Utils.GetPrefabName(itemDrop.gameObject), itemName, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(PrefabOf(itemDrop.m_itemData), itemName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (player.GetInventory().CanAddItem(itemDrop.m_itemData) && player.Pickup(itemDrop.gameObject, true, false))
                        pickedUp.Add(PrefabOf(itemDrop.m_itemData));
                }
                return new
                {
                    success = true,
                    pickedUpCount = pickedUp.Count,
                    items = pickedUp,
                    message = pickedUp.Count > 0 ? $"Picked up {pickedUp.Count} items" : "No items to pick up"
                };
            });
        }

        [Tool("player/equip_item", Description = "Equip an item from inventory by name.")]
        public object EquipItem(
            [ToolParameter(Description = "Item name to equip (prefab name, e.g. 'SwordBronze', 'ShieldWood')")] string itemName) => WithPlayer(player =>
        {
            var item = FindItem(player.GetInventory().GetAllItems(), itemName);
            if (item == null) return Fail($"No item matching '{itemName}' found in inventory");
            var result = player.EquipItem(item);
            return new { success = result, message = result ? $"Equipped {PrefabOf(item)}" : "Cannot equip (in combat, broken, or already equipped)" };
        });

        [Tool("player/unequip_item", Description = "Unequip a currently equipped item by name.")]
        public object UnequipItem(
            [ToolParameter(Description = "Item name to unequip (prefab name, e.g. 'SwordBronze')")] string itemName) => WithPlayer(player =>
        {
            var item = FindItem(player.GetInventory().GetEquippedItems(), itemName);
            if (item == null) return Fail($"No equipped item matching '{itemName}' found");
            player.UnequipItem(item);
            return new { success = true, message = $"Unequipped {PrefabOf(item)}" };
        });

        [Tool("player/use_item", Description = "Consume a food or potion item from inventory by name.")]
        public object UseItem(
            [ToolParameter(Description = "Item name to consume (prefab name, e.g. 'CookedMeat', 'MeadHealthMinor')")] string itemName) => WithPlayer(player =>
        {
            var inv = player.GetInventory();
            var item = FindItem(inv.GetAllItems(), itemName, i => i.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Consumable);
            if (item == null) return Fail($"No consumable matching '{itemName}' found in inventory");
            var result = player.ConsumeItem(inv, item);
            return new { success = result, message = result ? $"Consumed {PrefabOf(item)}" : "Cannot consume (food slots full or already active)" };
        });

        [Tool("player/drop_item", Description = "Drop an item from the inventory (or an equipped backpack from Adventure Backpacks / Backpacks) into the world.")]
        public object DropItem(
            [ToolParameter(Description = "Item name to drop (prefab name)")] string itemName,
            [ToolParameter(Description = "Number to drop (default 1, use -1 for entire stack)")] int amount = 1,
            [ToolParameter(Description = "true = take it from the backpack instead of the main inventory")] bool fromBackpack = false) => WithPlayer(player =>
        {
            Inventory inv = player.GetInventory();
            ItemDrop.ItemData? item = null;
            if (fromBackpack)
            {
                var packs = BackpackCompat.EquippedBackpacks(player);
                if (packs.Count == 0) return Fail("No backpack equipped (Adventure Backpacks or Backpacks)");
                // The first equipped backpack that holds the item.
                foreach (var (_, packInv) in packs)
                {
                    item = FindItem(packInv.GetAllItems(), itemName);
                    if (item != null) { inv = packInv; break; }
                }
            }
            else
            {
                item = FindItem(inv.GetAllItems(), itemName);
            }
            if (item == null) return Fail($"No item matching '{itemName}' found in {(fromBackpack ? "the backpack" : "inventory")}");

            var dropAmount = amount < 0 ? item.m_stack : Math.Min(amount, item.m_stack);
            var result = player.DropItem(inv, item, dropAmount);
            return new { success = result, message = result ? $"Dropped {dropAmount}x {PrefabOf(item)}" : "Cannot drop (quest item)" };
        });

        [Tool("player/repair_item", Description = "Restore an inventory item to full durability. Cheat: it does not check for a crafting station or cost anything.")]
        public object RepairItem(
            [ToolParameter(Description = "Item name to repair (prefab name). If empty, repairs the first damaged item.")] string itemName = "") => WithPlayer(player =>
        {
            var damaged = player.GetInventory().GetAllItems().Where(i => i.m_shared.m_useDurability && i.m_durability < i.GetMaxDurability());
            var item = string.IsNullOrEmpty(itemName) ? damaged.FirstOrDefault() : FindItem(damaged, itemName);
            if (item == null) return Fail("No damaged item found to repair");
            item.m_durability = item.GetMaxDurability();
            return new { success = true, message = $"Repaired {PrefabOf(item)}" };
        });

        // ---------- Perception ----------

        [Tool("player/get_visible_objects", Description = "Get objects visible to the player using camera frustum and line-of-sight raycasting. Returns what the player can actually see.")]
        public object GetVisibleObjects(
            [ToolParameter(Description = "Max distance to check (default 50)")] float range = 50f,
            [ToolParameter(Description = "Max objects to return (default 25)")] int limit = 25) => WithPlayer(player =>
        {
            var cam = Camera.main;
            if (cam == null) return Fail("No main camera found");

            var playerPos = player.transform.position;
            var camPos = cam.transform.position;
            var seen = new HashSet<int>();
            var results = new List<(float dist, object data)>();

            foreach (var col in Physics.OverlapSphere(playerPos, range))
            {
                if (col == null) continue;
                var root = col.transform.root.gameObject;
                var id = root.GetInstanceID();
                if (!seen.Add(id) || root == player.gameObject) continue;

                var objPos = root.transform.position;
                var viewport = cam.WorldToViewportPoint(objPos);
                if (viewport.z < 0f || viewport.x < 0f || viewport.x > 1f || viewport.y < 0f || viewport.y > 1f)
                    continue;

                // Line of sight: the first thing the camera ray hits must be this object.
                var dir = objPos - camPos;
                if (Physics.Raycast(camPos, dir.normalized, out var hit, dir.magnitude) && hit.collider.transform.root.gameObject != root)
                    continue;

                var dist = Vector3.Distance(playerPos, objPos);
                results.Add((dist, new
                {
                    name = root.name,
                    instanceId = id,
                    type = KindOf(root, includeBuildings: true),
                    position = Vec(objPos),
                    distance = (float)Math.Round(dist, 2),
                    screenPosition = new { x = (float)Math.Round(viewport.x, 3), y = (float)Math.Round(viewport.y, 3) }
                }));
            }

            var sorted = results.OrderBy(r => r.dist).Take(limit).Select(r => r.data).ToList();
            return new
            {
                success = true,
                count = sorted.Count,
                playerPosition = Vec(playerPos),
                cameraForward = Vec(cam.transform.forward),
                range,
                objects = sorted
            };
        });

        [Tool("player/find_nearby_prefabs", Description = "Find prefab instances near the player by name. Useful for locating trees, rocks, enemies, and other world objects within range.")]
        public object FindNearbyPrefabs(
            [ToolParameter(Description = "Prefab name to search for (partial match, case-insensitive). E.g. 'Beech', 'Rock', 'Boar'")] string prefabName,
            [ToolParameter(Description = "Search radius in meters (default 30)")] float range = 30f)
        {
            if (string.IsNullOrWhiteSpace(prefabName))
                return Fail("prefabName is required (e.g. 'Beech', 'Rock', 'IronOre')");

            return WithPlayer(player =>
            {
                var playerPos = player.transform.position;
                var seen = new HashSet<int>();
                var results = new List<(float dist, object data)>();

                foreach (var col in Physics.OverlapSphere(playerPos, range))
                {
                    if (col == null) continue;
                    // The root prefab instance, or the hit object itself if only it matches.
                    var go = col.gameObject;
                    var root = go.transform.root.gameObject;
                    GameObject? match = Contains(root.name, prefabName) ? root : Contains(go.name, prefabName) ? go : null;
                    if (match == null || !seen.Add(match.GetInstanceID())) continue;

                    var pos = match.transform.position;
                    var dist = Vector3.Distance(playerPos, pos);
                    results.Add((dist, new
                    {
                        name = match.name,
                        instanceId = match.GetInstanceID(),
                        position = Vec(pos),
                        distance = (float)Math.Round(dist, 2),
                        type = KindOf(match, includeBuildings: false)
                    }));
                }

                var sorted = results.OrderBy(r => r.dist).Select(r => r.data).ToList();
                return new { success = true, count = sorted.Count, playerPosition = Vec(playerPos), range, prefabs = sorted };
            });
        }

        // ---------- Combat and looking ----------

        [Tool("player/primary_attack", Description = "Perform a single primary attack (left click) with the current weapon. Returns immediately after starting the attack.")]
        public object PrimaryAttack() => WithPlayer(player =>
        {
            var result = player.StartAttack(null, false);
            return new { success = result, message = result ? "Primary attack started" : "Cannot attack (no weapon, mid-animation, or stunned)" };
        });

        [Tool("player/secondary_attack", Description = "Perform a single secondary attack (right click) with the current weapon. Alt-attack, parry, or special depending on weapon.")]
        public object SecondaryAttack() => WithPlayer(player =>
        {
            var result = player.StartAttack(null, true);
            return new { success = result, message = result ? "Secondary attack started" : "Cannot attack (no weapon, no secondary attack, mid-animation, or stunned)" };
        });

        [Tool("player/block", Description = "Start or stop blocking with the current shield/weapon. Blocking within 0.25s of an incoming hit triggers a parry.")]
        public object Block(
            [ToolParameter(Description = "true to start blocking, false to stop")] bool active) => WithPlayer(player =>
        {
            player.SetControls(Vector3.zero,
                attack: false, attackHold: false, secondaryAttack: false, secondaryAttackHold: false,
                block: false, blockHold: active, jump: false, crouch: false, run: false, autoRun: false);
            return new { success = true, message = active ? "Blocking started" : "Blocking stopped", isBlocking = player.IsBlocking() };
        });

        [Tool("player/look_at_position", Description = "Face the player toward a world position (XZ plane).")]
        public object LookAtPosition(
            [ToolParameter(Description = "X coordinate to look at")] float x,
            [ToolParameter(Description = "Z coordinate to look at")] float z) => WithPlayer(player =>
        {
            var pos = player.transform.position;
            if (!PlayerLook.Face(player, new Vector3(x - pos.x, 0f, z - pos.z))) return Fail("Target position is too close to player");
            return new { success = true, message = $"Now facing ({x:F1}, {z:F1})" };
        });

        [Tool("player/look_at_object", Description = "Face the player toward a specific object by instanceId.")]
        public object LookAtObject(
            [ToolParameter(Description = "Instance ID of the target object (from find_nearby_prefabs or get_visible_objects)")] int instanceId) => WithPlayer(player =>
        {
            var target = FindObject(instanceId);
            if (target == null) return Fail($"No live object with instanceId {instanceId}");
            if (!PlayerLook.Face(player, target.transform.position - player.transform.position)) return Fail("Target is too close to player");
            return new { success = true, message = $"Now facing {target.name}", targetPosition = Vec(target.transform.position) };
        });

        [Tool("player/interact", Description = "Interact with the object the player is looking at (E key). Opens doors, picks up items, activates crafting stations, etc.")]
        public object Interact(
            [ToolParameter(Description = "Hold interact instead of tap (default false)")] bool hold = false) => WithPlayer(player =>
        {
            var hover = player.GetHoverObject();
            if (hover == null) return Fail("Nothing in range to interact with");
            var interactable = hover.GetComponentInParent<Interactable>();
            if (interactable == null) return Fail($"Object '{hover.name}' is not interactable");
            var result = interactable.Interact(player, hold, false);
            return new { success = result, message = result ? $"Interacted with {hover.name}" : $"Interaction with {hover.name} failed", target = hover.name };
        });

        // ---------- Movement ----------

        [Tool("player/move_direction", Description = "Move the player in a world-space direction for a duration. Use forward/back/left/right relative to current look direction, or provide explicit X/Z.")]
        public object MoveDirection(
            [ToolParameter(Description = "X component of direction (east/west)")] float x = 0f,
            [ToolParameter(Description = "Z component of direction (north/south)")] float z = 0f,
            [ToolParameter(Description = "Duration in seconds (default 1)")] float duration = 1f,
            [ToolParameter(Description = "Run instead of walk (default false)")] bool run = false)
        {
            var dir = new Vector3(x, 0f, z);
            if (dir.magnitude < 0.01f) return Fail("Direction vector is zero");
            // Movement runs in game time; give up in real time so a paused game can't hang the call.
            return AwaitManager(
                () => Player.m_localPlayer == null ? null : MovementManager.Instance.StartMoving(dir, run, duration),
                duration + 10f,
                () => MovementManager.Instance.StopMoving());
        }

        [Tool("player/navigate_to_position", Description = "Walk the player to a world position using pathfinding. Returns when arrived or on failure.")]
        public object NavigateToPosition(
            [ToolParameter(Description = "X coordinate")] float x,
            [ToolParameter(Description = "Z coordinate")] float z,
            [ToolParameter(Description = "Timeout in seconds (default 60)")] float timeout = 60f)
        {
            return AwaitManager(
                () =>
                {
                    var player = Player.m_localPlayer;
                    if (player == null) return null;
                    return NavigationManager.Instance.StartNavigation(new Vector3(x, player.transform.position.y, z), timeout);
                },
                timeout + 10f,
                () => NavigationManager.Instance.StopNavigation("Bridge gave up waiting (game paused or stalled)"));
        }

        [Tool("player/jump", Description = "Make the player jump. Set move direction first for a directional jump.")]
        public object Jump() => WithPlayer(player =>
        {
            player.Jump();
            return new { success = true, message = "Jump initiated" };
        });

        [Tool("player/dodge", Description = "Dodge roll in a direction. Costs stamina and grants brief invincibility.")]
        public object Dodge(
            [ToolParameter(Description = "X component of dodge direction")] float x = 0f,
            [ToolParameter(Description = "Z component of dodge direction")] float z = 0f) => WithPlayer(player =>
        {
            var dir = new Vector3(x, 0f, z);
            if (dir.magnitude < 0.01f) dir = -player.GetLookDir(); // default: dodge backward
            dir.Normalize();
            player.SetMoveDir(dir);
            // A dodge is block + jump in the same frame.
            player.SetControls(dir,
                attack: false, attackHold: false, secondaryAttack: false, secondaryAttackHold: false,
                block: true, blockHold: true, jump: true, crouch: false, run: false, autoRun: false);
            return new { success = true, message = "Dodge initiated" };
        });

        [Tool("player/set_crouch", Description = "Toggle crouching/sneaking on or off.")]
        public object SetCrouch(
            [ToolParameter(Description = "true to crouch, false to stand")] bool active) => WithPlayer(player =>
        {
            // SetControls with crouch=true toggles the state, so only send it when it needs to change.
            if (active != player.IsCrouching())
                player.SetControls(Vector3.zero,
                    attack: false, attackHold: false, secondaryAttack: false, secondaryAttackHold: false,
                    block: false, blockHold: false, jump: false, crouch: true, run: false, autoRun: false);
            return new { success = true, message = active ? "Now crouching" : "Stopped crouching", requested = active };
        });

        // ---------- Crafting and building ----------

        [Tool("player/get_available_recipes", Description = "List recipes the player can craft at their current location. Shows required materials and station.")]
        public object GetAvailableRecipes() => WithPlayer(player =>
        {
            var recipes = new List<Recipe>();
            player.GetAvailableRecipes(ref recipes);
            var station = player.GetCurrentCraftingStation();
            var list = recipes.Where(r => r.m_item != null).Select(r => new
            {
                name = r.m_item.gameObject.name,
                amount = r.m_amount,
                station = r.m_craftingStation != null ? r.m_craftingStation.m_name : null,
                minStationLevel = r.m_minStationLevel,
                resources = r.m_resources.Where(req => req.m_resItem != null)
                    .Select(req => new { item = req.m_resItem.gameObject.name, amount = req.GetAmount(1) }).ToList(),
                canCraft = player.HaveRequirements(r, false, 1)
            }).ToList();
            return new
            {
                success = true,
                currentStation = station != null ? station.m_name : null,
                stationLevel = station != null ? (int?)station.GetLevel() : null,
                count = list.Count,
                recipes = list
            };
        });

        [Tool("player/craft_item", Description = "Craft an item by name at the current crafting station (or by hand if no station needed).")]
        public object CraftItem(
            [ToolParameter(Description = "Item name to craft (prefab name, e.g. 'SwordBronze', 'ArrowWood')")] string itemName,
            [ToolParameter(Description = "Number of times to craft (default 1)")] int count = 1)
        {
            if (string.IsNullOrWhiteSpace(itemName)) return Fail("itemName is required");
            return WithPlayer(player =>
            {
                var recipes = new List<Recipe>();
                player.GetAvailableRecipes(ref recipes);
                var craftable = recipes.Where(r => r.m_item != null).ToList();
                // Exact name first, so "ArrowWood" isn't beaten by an earlier "ArrowWoodFire".
                var recipe = craftable.FirstOrDefault(r => string.Equals(r.m_item.gameObject.name, itemName, StringComparison.OrdinalIgnoreCase))
                             ?? craftable.FirstOrDefault(r => Contains(r.m_item.gameObject.name, itemName));
                if (recipe == null) return Fail($"No available recipe matching '{itemName}'");

                int crafted = 0;
                var inv = player.GetInventory();
                for (int i = 0; i < count && player.HaveRequirements(recipe, false, 1); i++)
                {
                    if (inv.AddItem(recipe.m_item.gameObject.name, recipe.m_amount, 1, 0, player.GetPlayerID(), player.GetPlayerName(), cheated: false) == null)
                        break; // inventory full
                    player.ConsumeResources(recipe.m_resources, 1);
                    crafted++;
                }
                var name = recipe.m_item.gameObject.name;
                return new
                {
                    success = crafted > 0,
                    message = crafted > 0 ? $"Crafted {crafted}x {name} ({recipe.m_amount * crafted} items)" : "Cannot craft (missing materials or inventory full)",
                    crafted
                };
            });
        }

        [Tool("player/place_piece", Description = "Place a building piece at a position. Requires hammer equipped and appropriate crafting station nearby.")]
        public object PlacePiece(
            [ToolParameter(Description = "Piece prefab name (e.g. 'wood_wall', 'wood_floor')")] string pieceName,
            [ToolParameter(Description = "X position")] float x,
            [ToolParameter(Description = "Y position")] float y,
            [ToolParameter(Description = "Z position")] float z,
            [ToolParameter(Description = "Y rotation in degrees (default 0)")] float rotation = 0f)
        {
            if (string.IsNullOrWhiteSpace(pieceName)) return Fail("pieceName is required");
            return WithPlayer(player =>
            {
                var table = player.GetBuildTool(); // m_buildPieces is private in 1.0
                if (table == null) return Fail("No build piece table active (equip a hammer first)");

                var pieces = table.m_pieces.Where(go => go != null).Select(go => go.GetComponent<Piece>()).Where(p => p != null).ToList();
                var piece = pieces.FirstOrDefault(p => string.Equals(p.gameObject.name, pieceName, StringComparison.OrdinalIgnoreCase))
                            ?? pieces.FirstOrDefault(p => Contains(p.gameObject.name, pieceName));
                if (piece == null) return Fail($"No building piece matching '{pieceName}'");

                player.PlacePiece(piece, new Vector3(x, y, z), Quaternion.Euler(0f, rotation, 0f));
                return new { success = true, message = $"Placed {piece.gameObject.name} at ({x:F1}, {y:F1}, {z:F1})" };
            });
        }

        [Tool("player/remove_piece", Description = "Demolish a building piece by instanceId. Returns some materials.")]
        public object RemovePiece(
            [ToolParameter(Description = "Instance ID of the piece to remove")] int instanceId) => WithPlayer(player =>
        {
            var target = FindObject(instanceId);
            if (target == null) return Fail($"No live object with instanceId {instanceId}");
            var piece = target.GetComponent<Piece>();
            if (piece == null) return Fail("Object is not a building piece");
            if (!piece.m_canBeRemoved) return Fail("This piece cannot be removed");

            var wnt = target.GetComponent<WearNTear>();
            if (wnt != null) wnt.Remove();
            else ZNetScene.instance.Destroy(target);
            return new { success = true, message = $"Removed {piece.gameObject.name}" };
        });

        // ---------- Helpers ----------

        private static List<object> DescribeItems(Inventory inv) =>
            inv.GetAllItems().Select(item => (object)new
            {
                name = PrefabOf(item),
                quantity = item.m_stack,
                slot = new { x = item.m_gridPos.x, y = item.m_gridPos.y },
                equipped = item.m_equipped,
                durability = item.m_shared.m_useDurability && item.m_shared.m_maxDurability > 0
                    ? (float?)Math.Round(item.GetDurabilityPercentage() * 100f, 1)
                    : null,
                type = item.m_shared.m_itemType.ToString()
            }).ToList();

        /// <summary>The item's prefab name (e.g. "IronOre"), or its name token if it has no drop prefab.</summary>
        private static string PrefabOf(ItemDrop.ItemData item) =>
            item.m_dropPrefab != null ? item.m_dropPrefab.name : item.m_shared.m_name;

        /// <summary>Exact prefab or name-token match first, then "contains" (so "Stone" doesn't hit "Whetstone" first).</summary>
        private static ItemDrop.ItemData? FindItem(IEnumerable<ItemDrop.ItemData> items, string name, Func<ItemDrop.ItemData, bool>? extra = null)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var list = items.Where(i => extra == null || extra(i)).ToList();
            return list.FirstOrDefault(i => string.Equals(PrefabOf(i), name, StringComparison.OrdinalIgnoreCase) ||
                                            string.Equals(i.m_shared.m_name, name, StringComparison.OrdinalIgnoreCase))
                   ?? list.FirstOrDefault(i => Contains(PrefabOf(i), name) || Contains(i.m_shared.m_name, name));
        }

        private static bool Contains(string text, string part) => text.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>A live game object by instance id (GameObject or component id).</summary>
        private static GameObject? FindObject(int instanceId)
        {
            var obj = Resources.InstanceIDToObject(instanceId);
            if (obj is GameObject go && go != null) return go;
            if (obj is Component c && c != null) return c.gameObject;
            return null;
        }

        private static string? KindOf(GameObject go, bool includeBuildings)
        {
            if (go.GetComponent<TreeBase>() != null) return "TreeBase";
            if (go.GetComponent<Destructible>() != null) return "Destructible";
            if (go.GetComponent<TreeLog>() != null) return "TreeLog";
            if (go.GetComponent<MineRock>() != null) return "MineRock";
            if (go.GetComponent<Character>() != null) return "Character";
            if (includeBuildings && go.GetComponent<Piece>() != null) return "Piece";
            if (includeBuildings && go.GetComponent<ItemDrop>() != null) return "ItemDrop";
            return null;
        }

        /// <summary>
        /// Starts a movement/navigation manager on the main thread and waits for it to finish, at most
        /// <paramref name="maxSeconds"/> of real time; on giving up, <paramref name="stop"/> cancels it.
        /// </summary>
        private static object AwaitManager(Func<Task<object>?> start, float maxSeconds, Action stop)
        {
            Task<object>? task;
            try { task = MainThreadDispatcher.Run(start); }
            catch (Exception ex) { return Fail(ex.GetBaseException().Message); }
            if (task == null) return NoPlayer;
            if (task.Wait(TimeSpan.FromSeconds(maxSeconds))) return task.Result;
            MainThreadDispatcher.Instance.Enqueue(stop);
            return Fail($"Did not finish within {maxSeconds:F0}s of real time (game paused or stalled); stopped.");
        }
    }
}
