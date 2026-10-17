using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ValAgentBridge
{
    /// <summary>
    /// Tells when the person at the keyboard is using the character, so tools that move, turn or teleport it don't
    /// take it away from them (a test once teleported the owner off a moving boat). Real keyboard, mouse and gamepad
    /// input is noticed every frame; the bridge's own simulated input (input/press_key, input/scroll, input/drag) is
    /// not counted.
    /// </summary>
    public static class OwnerControl
    {
        /// <summary>Seconds after the owner's last input during which moving tools refuse.</summary>
        public const float RecentInputSeconds = 5f;

        private static float _lastOwnerInput = -1000f;
        private static string _lastOwnerInputKind = "";

        /// <summary>Until this frame, device input is the bridge's own (set by the input tools).</summary>
        public static int SimulatingUntilFrame = -1;

        /// <summary>Main thread, once per frame (MainThreadDispatcher.Update).</summary>
        public static void Watch()
        {
            if (!Application.isFocused || Time.frameCount <= SimulatingUntilFrame) return;
            string? kind = null;
            var kb = Keyboard.current;
            if (kb != null && kb.anyKey.isPressed) kind = "keyboard";
            var mouse = Mouse.current;
            if (kind == null && mouse != null)
            {
                if (mouse.leftButton.isPressed || mouse.rightButton.isPressed || mouse.middleButton.isPressed) kind = "mouse button";
                else if (mouse.scroll.ReadValue().sqrMagnitude > 0f) kind = "mouse wheel";
                else if (mouse.delta.ReadValue().sqrMagnitude > 4f) kind = "mouse";
            }
            var pad = Gamepad.current;
            if (kind == null && pad != null)
            {
                if (pad.leftStick.ReadValue().sqrMagnitude > 0.04f || pad.rightStick.ReadValue().sqrMagnitude > 0.04f) kind = "gamepad";
                else
                    foreach (var control in pad.allControls)
                        if (control is UnityEngine.InputSystem.Controls.ButtonControl b && b.isPressed) { kind = "gamepad"; break; }
            }
            if (kind == null) return;
            _lastOwnerInput = Time.unscaledTime;
            _lastOwnerInputKind = kind;
        }

        /// <summary>Seconds since the owner last touched keyboard, mouse or gamepad (large if never).</summary>
        public static float SecondsSinceInput => Time.unscaledTime - _lastOwnerInput;

        /// <summary>
        /// Main thread. Why the owner is in control right now (steering, in a menu, recent input), or null. With
        /// force the answer is always null.
        /// </summary>
        public static string? Busy(Player player, bool force)
        {
            if (force || player == null) return null;
            if (player.GetControlledShip() != null) return "steering a ship";
            var ship = player.GetStandingOnShip();
            if (ship != null && Mathf.Abs(ship.GetSpeed()) > 0.5f) return $"on a moving ship ({Mathf.Abs(ship.GetSpeed()):F1} m/s)";
            if (player.IsAttached()) return "sitting or attached to something (chair, bed, cart)";
            var menu = OpenMenu();
            if (menu != null) return "in a menu (" + menu + ")";
            var since = SecondsSinceInput;
            if (since < RecentInputSeconds) return $"using the {_lastOwnerInputKind} (last input {since:F1} s ago)";
            return null;
        }

        /// <summary>The menu the game treats as taking the input (same list as the player's own input check), or null.</summary>
        public static string? OpenMenu()
        {
            if (Chat.instance != null && Chat.instance.HasFocus()) return "chat";
            if (Console.IsVisible()) return "console";
            if (TextInput.IsVisible()) return "text input";
            if (StoreGui.IsVisible()) return "trader";
            if (InventoryGui.IsVisible()) return "inventory or crafting";
            if (Menu.IsVisible()) return "game menu";
            if (TextViewer.instance != null && TextViewer.instance.IsVisible()) return "text viewer";
            if (Minimap.IsOpen()) return "map";
            return null;
        }

        /// <summary>The refusal answer for a tool, or null when it may go ahead.</summary>
        public static object? Refusal(Player player, bool force, string what)
        {
            var why = Busy(player, force);
            if (why == null) return null;
            return new
            {
                success = false,
                ownerInControl = why,
                error = $"Not done: the owner is {why}. {what} would take the character away from them. Ask the owner, then pass force=true to do it anyway."
            };
        }
    }
}
