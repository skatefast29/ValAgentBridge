using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using HarmonyLib;
using Lib.GAB.Tools;
using UnityEngine;

namespace ValAgentBridge.Tools
{
    /// <summary>
    /// Working with one world object by instanceId: use it without aiming at it (a busy room makes aiming hit the
    /// wrong collider), and spawning any prefab the way the game's spawn command does, at a chosen spot.
    /// </summary>
    public class ObjectTools
    {
        private static void Log(string text) => ValAgentBridgePlugin.ModLogger.LogInfo(text);

        private static object Fail(string error) => new { success = false, error };

        private static object Vec(Vector3 v) => new { x = Math.Round(v.x, 1), y = Math.Round(v.y, 1), z = Math.Round(v.z, 1) };

        private static readonly AccessTools.FieldRef<InventoryGui, Container>? CurrentContainer =
            AccessTools.Field(typeof(InventoryGui), "m_currentContainer") is { } f ? AccessTools.FieldRefAccess<InventoryGui, Container>(f) : null;

        /// <summary>A live game object by instance id (GameObject or component id).</summary>
        private static GameObject? FindObject(int instanceId)
        {
            var obj = Resources.InstanceIDToObject(instanceId);
            if (obj is GameObject go && go != null) return go;
            if (obj is Component c && c != null) return c.gameObject;
            return null;
        }

        // ---------- Use an object by instanceId ----------

        [Tool("player/interact_object", Description = "Use a world object by instanceId, like pressing E on it, without aiming: opens a crafting station, a chest, a portal's name box, a door, a bed, a ship's controls... Returns what opened (station, container, text box) or what changed. The player must be near it (stations need about 2-3 m, chests close beyond 4 m). travel=true takes a connected portal like walking into it. Refuses while the owner is steering, in a menu or just used keyboard/mouse unless force=true.")]
        public object InteractObject(
            [ToolParameter(Description = "instanceId of the object (player/find_nearby_prefabs, player/get_visible_objects)")] int instanceId,
            [ToolParameter(Description = "When the object has several usable parts (a ship: controls, storage, ladder), part of the component name to use, e.g. Container, ShipControlls, Ladder")] string component = "",
            [ToolParameter(Description = "Hold E instead of a tap (default false)")] bool hold = false,
            [ToolParameter(Description = "Alternative use (shift+E) (default false)")] bool alt = false,
            [ToolParameter(Description = "Portal only: travel through it instead of opening its name box (default false)")] bool travel = false,
            [ToolParameter(Description = "Seconds to wait for something to open (default 2; a server's chest answer can take a moment)")] float wait = 2f,
            [ToolParameter(Description = "Do it even while the owner is steering, in a menu or just used keyboard/mouse (ask them first)")] bool force = false)
        {
            long msgMark = Recorder.LastMessageSeq;
            Before? before = null;
            var started = MainThreadDispatcher.RunTool(() =>
            {
                var player = Player.m_localPlayer;
                if (player == null) return Fail("Not in a world.");
                if (OwnerControl.Refusal(player, force, "Opening something") is object refused) return refused;
                var go = FindObject(instanceId);
                if (go == null) return Fail($"No live object with instanceId {instanceId} (it may have unloaded; look it up again).");

                var parts = Usable(go);
                if (parts.Count == 0) return Fail($"'{go.name}' has nothing to use (no Interactable on it, its parents or children).");
                var names = parts.Select(p => p.GetType().Name).Distinct().ToList();
                var chosen = component.Length == 0 ? parts[0]
                    : parts.FirstOrDefault(p => p.GetType().Name.IndexOf(component, StringComparison.OrdinalIgnoreCase) >= 0);
                if (chosen == null) return Fail($"'{go.name}' has no usable part matching '{component}'. It has: {string.Join(", ", names)}.");
                var target = (Component)chosen;

                float distance = Vector3.Distance(player.transform.position, target.transform.position);
                if (chosen is CraftingStation station && !station.InUseDistance(player))
                    return Fail($"Too far from {Name(target)}: {distance:F1} m, the station works within {station.m_useDistance:F1} m. Walk closer (player/navigate_to_position) and try again.");
                if (chosen is Container && InventoryGui.instance != null && distance > InventoryGui.instance.m_autoCloseDistance)
                    return Fail($"Too far from {Name(target)}: {distance:F1} m; the game closes a chest beyond {InventoryGui.instance.m_autoCloseDistance:F0} m. Walk closer and try again.");

                before = Snapshot(player, target);
                bool returned;
                string how;
                if (travel)
                {
                    if (!(chosen is TeleportWorld portal)) return Fail($"travel works on portals; {Name(target)} is a {chosen.GetType().Name}.");
                    portal.Teleport(player);
                    returned = player.IsTeleporting();
                    how = "travel";
                }
                else
                {
                    returned = chosen.Interact(player, hold, alt);
                    how = hold ? "hold" : alt ? "alt" : "use";
                }
                Log($"player/interact_object: {how} {Name(target)} ({chosen.GetType().Name}, instanceId {instanceId}) at {distance:F1} m -> {returned}");
                return new { success = true, target = Name(target), component = chosen.GetType().Name, parts = names, distance = Math.Round(distance, 1), returned };
            });
            if (before == null) return started;    // refused or failed before using it

            // Wait for the result to show: menus open on the next frames, a chest only once the owner answers.
            object? opened = null;
            var deadline = DateTime.UtcNow.AddSeconds(Mathf.Clamp(wait, 0f, 15f));
            do
            {
                Thread.Sleep(100);
                try { opened = MainThreadDispatcher.Run(() => Changed(before), 3000); }
                catch (Exception) { }
            } while (opened == null && DateTime.UtcNow < deadline);

            var messages = Recorder.MessagesSince(msgMark, 5).Select(m => m.text).ToList();
            var s = Newtonsoft.Json.Linq.JObject.FromObject(started);
            s["opened"] = opened == null ? null : Newtonsoft.Json.Linq.JToken.FromObject(opened);
            if (opened == null)
                s["note"] = messages.Count > 0 ? "Nothing opened; the game said: " + string.Join(" / ", messages)
                    : s.Value<bool>("returned") ? "Used; nothing opened (fine for a door, a lever or a pickup)." : "Nothing opened and the game refused quietly (out of reach, in use by someone else, or not usable now).";
            if (messages.Count > 0) s["messages"] = Newtonsoft.Json.Linq.JToken.FromObject(messages);
            return s;
        }

        /// <summary>Interactables on the object first, then its parents, then its active children.</summary>
        private static List<Interactable> Usable(GameObject go)
        {
            var list = new List<Interactable>();
            void Add(IEnumerable<Interactable> found)
            {
                foreach (var i in found)
                    if (i is Component c && c != null && c.gameObject.activeInHierarchy && !list.Contains(i)) list.Add(i);
            }
            Add(go.GetComponents<Interactable>());
            if (go.transform.parent != null) Add(go.transform.parent.GetComponentsInParent<Interactable>());
            Add(go.GetComponentsInChildren<Interactable>());
            return list;
        }

        private static string Name(Component c)
        {
            var hover = c as Hoverable ?? c.GetComponentInParent<Hoverable>();
            string? name = null;
            try { name = hover?.GetHoverName(); } catch (Exception) { }
            name = string.IsNullOrEmpty(name) ? Utils.GetPrefabName(c.gameObject) : Localization.instance != null ? Localization.instance.Localize(name) : name;
            return name ?? c.name;
        }

        private sealed class Before
        {
            public bool inventory, text, map;
            public Container? container;
            public CraftingStation? station;
            public int? doorState;
            public bool attached, teleporting;
            public ZNetView? view;
        }

        private static Before Snapshot(Player player, Component target)
        {
            var view = target.GetComponentInParent<ZNetView>();
            return new Before
            {
                inventory = InventoryGui.IsVisible(),
                text = TextInput.IsVisible(),
                map = Minimap.IsOpen(),
                container = InventoryGui.instance != null && CurrentContainer != null ? CurrentContainer(InventoryGui.instance) : null,
                station = player.GetCurrentCraftingStation(),
                doorState = target is Door && view != null && view.IsValid() ? view.GetZDO().GetInt(ZDOVars.s_state) : (int?)null,
                attached = player.IsAttached(),
                teleporting = player.IsTeleporting(),
                view = view
            };
        }

        /// <summary>Main thread: what opened or changed since the snapshot, or null while nothing has.</summary>
        private static object? Changed(Before b)
        {
            var player = Player.m_localPlayer;
            if (player == null) return null;
            var container = InventoryGui.instance != null && CurrentContainer != null ? CurrentContainer(InventoryGui.instance) : null;
            if (container != null && container != b.container)
                return new { kind = "container", name = Name(container), instanceId = container.gameObject.GetInstanceID(), items = container.GetInventory()?.NrOfItems() };
            var station = player.GetCurrentCraftingStation();
            if (InventoryGui.IsVisible() && station != null && (station != b.station || !b.inventory))
                return new { kind = "crafting station", name = Name(station), level = station.GetLevel() };
            if (TextInput.IsVisible() && !b.text) return new { kind = "text input", note = "a name/text box is open (e.g. a portal tag); ui/list shows it, Escape closes it" };
            if (InventoryGui.IsVisible() && !b.inventory) return new { kind = "inventory" };
            if (Minimap.IsOpen() && !b.map) return new { kind = "map" };
            if (b.doorState.HasValue && b.view != null && b.view.IsValid())
            {
                int state = b.view.GetZDO().GetInt(ZDOVars.s_state);
                if (state != b.doorState.Value) return new { kind = "door", state = state == 0 ? "closed" : "open" };
            }
            if (player.IsAttached() && !b.attached) return new { kind = "attached", note = "sitting, lying down or holding the ship's controls" };
            if (player.IsTeleporting() && !b.teleporting) return new { kind = "teleporting", note = "travelling through the portal" };
            return null;
        }

        // ---------- Spawn a prefab ----------

        [Tool("world/spawn_object", Description = "(cheat) Spawn any world prefab (a boat such as Karve, VikingShip, Raft; a cart; a rock; a creature...) the way the game's spawn command does, at a position or an offset from the player. Boats go on the water surface pointing the way the player faces; other things on the ground. Refuses a spot that overlaps a ship or (for boats) a player unless force=true. Returns the instanceId; remove it with world/remove_objects. For building pieces use player/place_piece.")]
        public object SpawnObject(
            [ToolParameter(Description = "Prefab name (znetscene/search_prefabs), e.g. Karve")] string name,
            [ToolParameter(Description = "World X; leave x and z out to place it in front of the player")] float x = float.NaN,
            [ToolParameter(Description = "World Z")] float z = float.NaN,
            [ToolParameter(Description = "Height; leave out for the water surface (boats) or the ground")] float y = float.NaN,
            [ToolParameter(Description = "Without x/z: metres in front of the player (default 15 for boats, 3 otherwise)")] float forward = float.NaN,
            [ToolParameter(Description = "Without x/z: metres to the player's right (negative = left)")] float right = 0f,
            [ToolParameter(Description = "Compass heading the object faces (0 = north, 90 = east); default the player's heading")] float heading = float.NaN,
            [ToolParameter(Description = "Spawn even if the spot overlaps a ship or a player")] bool force = false)
        {
            return MainThreadDispatcher.RunTool(() =>
            {
                var player = Player.m_localPlayer;
                if (player == null || ZNetScene.instance == null || ZoneSystem.instance == null) return Fail("Not in a world.");
                var prefab = ZNetScene.instance.GetPrefab(name);
                if (prefab == null) return Fail($"No prefab '{name}'. Use znetscene/search_prefabs.");
                var shipPrefab = prefab.GetComponent<Ship>();
                bool isShip = shipPrefab != null;

                Vector3 pos;
                var look = player.transform.forward;
                look.y = 0f;
                look = look.sqrMagnitude < 0.01f ? Vector3.forward : look.normalized;
                if (float.IsNaN(x) != float.IsNaN(z)) return Fail("Give both x and z, or neither (then forward/right place it near the player).");
                if (float.IsNaN(x))
                {
                    float ahead = float.IsNaN(forward) ? (isShip ? 15f : 3f) : forward;
                    pos = player.transform.position + look * ahead + Vector3.Cross(Vector3.up, look) * right;
                }
                else pos = new Vector3(x, player.transform.position.y, z);

                float waterLevel = ZoneSystem.instance.m_waterLevel;
                float ground = ZoneSystem.instance.GetSolidHeight(pos, out float solid) ? solid : ZoneSystem.instance.GetGroundHeight(pos);
                float liquid = Floating.GetLiquidLevel(new Vector3(pos.x, Mathf.Max(ground, waterLevel), pos.z), 1f, LiquidType.Water);
                float water = liquid > -1000f ? liquid : ground < waterLevel ? waterLevel : float.NaN;
                bool onWater = !float.IsNaN(water) && water > ground;
                string? note = null;
                if (!float.IsNaN(y)) pos.y = y;
                else if (isShip && onWater) pos.y = water;
                else
                {
                    pos.y = ground + (isShip ? 0.5f : 0.2f);
                    if (isShip) note = "No water here: the boat sits on the ground.";
                }

                float radius = isShip ? Footprint(shipPrefab!.m_floatCollider, prefab.transform.localScale) : 0.5f;
                if (!force)
                {
                    foreach (var other in Ship.Instances.OfType<Ship>())
                    {
                        if (other == null) continue;
                        float otherRadius = Footprint(other.m_floatCollider, other.transform.lossyScale);
                        float gap = Flat((other.m_floatCollider != null ? other.m_floatCollider.bounds.center : other.transform.position) - pos);
                        if (gap < radius + otherRadius + 1f)
                            return Fail($"That spot overlaps a {Utils.GetPrefabName(other.gameObject)} {gap:F0} m away (needs {radius + otherRadius + 1f:F0} m clear). Pick another spot, or force=true.");
                    }
                    if (isShip)
                        foreach (var p in Player.GetAllPlayers())
                            if (p != null && Flat(p.transform.position - pos) < radius + 1f && Mathf.Abs(p.transform.position.y - pos.y) < 6f)
                                return Fail($"That spot is on top of {(p == player ? "the player" : p.GetPlayerName())}. Pick another spot (forward 15+), or force=true.");
                }

                float face = float.IsNaN(heading) ? Mathf.Atan2(look.x, look.z) * Mathf.Rad2Deg : heading;
                var go = UnityEngine.Object.Instantiate(prefab, pos, Quaternion.Euler(0f, face, 0f));
                Log($"world/spawn_object: {name} at ({pos.x:F1}, {pos.y:F1}, {pos.z:F1}) heading {Mathf.Repeat(face, 360f):F0}{(force ? " (forced)" : "")}");
                return new
                {
                    success = true,
                    name,
                    instanceId = go.GetInstanceID(),
                    position = Vec(pos),
                    heading = Math.Round(Mathf.Repeat(face, 360f)),
                    ship = isShip,
                    onWater,
                    note
                };
            });
        }

        /// <summary>Half the diagonal of a ship's float box in the XZ plane: a circle around the whole hull.</summary>
        private static float Footprint(BoxCollider? box, Vector3 scale)
        {
            if (box == null) return 4f;
            var size = Vector3.Scale(box.size, scale);
            return 0.5f * Mathf.Sqrt(size.x * size.x + size.z * size.z);
        }

        private static float Flat(Vector3 v) => new Vector2(v.x, v.z).magnitude;
    }
}
