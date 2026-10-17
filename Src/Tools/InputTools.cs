using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using HarmonyLib;
using Lib.GAB.Tools;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace ValAgentBridge.Tools
{
    /// <summary>
    /// Presses keys as if typed, so mod hotkeys (F11 panels, toggles) and game keys can be tested without anyone at
    /// the keyboard. Reaches two places: the game's Input System (ZInput, menus) through queued keyboard events, and
    /// BepInEx hotkeys (KeyboardShortcut.IsDown, used by most mods) through a patch on BepInEx's input reader,
    /// installed on first use. Mods that call UnityEngine.Input directly are not reached.
    /// </summary>
    public class InputTools
    {
        private sealed class Simulated
        {
            public int down, up;
        }

        private static readonly Dictionary<KeyCode, Simulated> Keys = new Dictionary<KeyCode, Simulated>();
        private static bool _patched;
        private static string? _patchProblem;

        [Tool("input/press_key", Description = "Press a key (with optional modifiers) for a moment, as if typed: F11, Escape, Tab, Alpha1 / 1, LeftShift, ... Reaches the game's controls and mod hotkeys set up through BepInEx (most mods); not mods that read UnityEngine.Input directly. Returns once the key is released.")]
        public object PressKey(
            [ToolParameter(Description = "Key name (Unity KeyCode, e.g. F11, Escape, Return, A, Alpha1 or 1)")] string key,
            [ToolParameter(Description = "Modifiers held with it, comma separated: ctrl, shift, alt (or KeyCode names)")] string modifiers = "",
            [ToolParameter(Description = "Frames to hold the key (default 2)")] int holdFrames = 2)
        {
            if (!TryKey(key, out var main)) return new { success = false, error = $"Unknown key '{key}'. Use Unity KeyCode names, e.g. F11, Escape, Return, A, Alpha1." };
            var mods = new List<KeyCode>();
            foreach (var m in modifiers.Split(new[] { ',', '+', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!TryKey(m, out var mk)) return new { success = false, error = $"Unknown modifier '{m}'. Use ctrl, shift, alt or KeyCode names." };
                mods.Add(mk);
            }
            var all = mods.Concat(new[] { main }).ToList();
            holdFrames = Mathf.Clamp(holdFrames, 1, 600);

            int releaseFrame;
            bool keyboard;
            try
            {
                (releaseFrame, keyboard) = MainThreadDispatcher.Run(() =>
                {
                    EnsurePatched();
                    int down = Time.frameCount + 1, up = down + holdFrames;
                    foreach (var k in all) Keys[k] = new Simulated { down = mods.Contains(k) ? down - 1 : down, up = up };
                    bool kb = Keyboard.current != null;
                    OwnerControl.SimulatingUntilFrame = Math.Max(OwnerControl.SimulatingUntilFrame, up + 3);
                    if (kb) MainThreadDispatcher.Instance.StartCoroutine(PressInputSystem(all, up));
                    return (up, kb);
                }, 5000);
            }
            catch (Exception ex)
            {
                return new { success = false, error = ex.GetBaseException().Message };
            }

            // Wait (real time, at most 30 s) until the release frame has passed.
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                System.Threading.Thread.Sleep(20);
                try { if (MainThreadDispatcher.Run(() => Time.frameCount, 2000) > releaseFrame) break; }
                catch (TimeoutException) { }
            }
            // Left in place if the game is busy: the entries are frame-bounded, so they stop matching anyway.
            try { MainThreadDispatcher.Run(() => { foreach (var k in all) Keys.Remove(k); return 0; }, 5000); }
            catch (Exception) { }

            ValAgentBridgePlugin.ModLogger.LogInfo($"input/press_key: {string.Join("+", all)}");
            return new
            {
                success = true,
                pressed = string.Join("+", all),
                gameControls = keyboard,
                modHotkeys = _patched,
                note = _patchProblem
            };
        }

        // ---- Mouse: queued Input System mouse states (wheel, buttons, position), seen by the game and its menus ----

        private static readonly AccessTools.FieldRef<Minimap, float>? MapZoom =
            AccessTools.Field(typeof(Minimap), "m_largeZoom") is { } zf ? AccessTools.FieldRefAccess<Minimap, float>(zf) : null;
        private static readonly AccessTools.FieldRef<Minimap, Vector3>? MapOffset =
            AccessTools.Field(typeof(Minimap), "m_mapOffset") is { } of ? AccessTools.FieldRefAccess<Minimap, Vector3>(of) : null;

        [Tool("input/scroll", Description = "Turn the mouse wheel, as if by hand: map zoom (positive = up = zoom in), build-menu tabs, lists. Optional x/y put the pointer there first (fractions of the screen, 0,0 = top left). With the large map open the answer gives its zoom before and after. Returns when done.")]
        public object Scroll(
            [ToolParameter(Description = "Wheel notches: positive = up (map zooms in), negative = down")] int notches,
            [ToolParameter(Description = "Pointer x as a fraction of the screen width (0 = left, 1 = right); leave out to keep it where it is")] float x = -1f,
            [ToolParameter(Description = "Pointer y as a fraction of the screen height (0 = top, 1 = bottom)")] float y = -1f,
            [ToolParameter(Description = "Wheel value per notch (default: what the game gets from a real wheel)")] float perNotch = 0f)
        {
            if (notches == 0) return new { success = false, error = "notches must not be 0." };
            notches = Mathf.Clamp(notches, -50, 50);
            return RunMouse(() => ScrollRoutine(notches, x, y, perNotch), Math.Abs(notches) * 3 + 10, "scroll " + notches);
        }

        [Tool("input/drag", Description = "Drag with a mouse button held, as if by hand: pan the large map, move a slider or an item. Points are fractions of the screen (0,0 = top left, 1,1 = bottom right). With the large map open the answer gives how far the map moved. Returns when done.")]
        public object Drag(
            [ToolParameter(Description = "Start x (fraction of screen width)")] float fromX,
            [ToolParameter(Description = "Start y (fraction of screen height, 0 = top)")] float fromY,
            [ToolParameter(Description = "End x")] float toX,
            [ToolParameter(Description = "End y")] float toY,
            [ToolParameter(Description = "left, right or middle (default left)")] string button = "left",
            [ToolParameter(Description = "How long the move takes in seconds (default 0.5)")] float seconds = 0.5f)
        {
            MouseButton which;
            switch (button.Trim().ToLowerInvariant())
            {
                case "left": which = MouseButton.Left; break;
                case "right": which = MouseButton.Right; break;
                case "middle": which = MouseButton.Middle; break;
                default: return new { success = false, error = "button is left, right or middle." };
            }
            seconds = Mathf.Clamp(seconds, 0.05f, 10f);
            return RunMouse(() => DragRoutine(new Vector2(fromX, fromY), new Vector2(toX, toY), which, seconds), seconds + 10, $"drag {button} ({fromX:F2},{fromY:F2}) -> ({toX:F2},{toY:F2})");
        }

        private static object RunMouse(Func<IEnumerator> routine, float maxSeconds, string what)
        {
            var done = new System.Threading.ManualResetEventSlim(false);
            object? map = null;
            string? problem = null;
            try
            {
                problem = MainThreadDispatcher.Run(() =>
                {
                    if (Mouse.current == null) return "No mouse device in the game.";
                    map = MapState();
                    MainThreadDispatcher.Instance.StartCoroutine(Wrap(routine(), done));
                    return (string?)null;
                }, 5000);
            }
            catch (Exception ex) { problem = ex.GetBaseException().Message; }
            if (problem != null) return new { success = false, error = problem };
            if (!done.Wait(TimeSpan.FromSeconds(maxSeconds)))
                return new { success = false, error = "The game did not finish the mouse input in time (paused or loading?)." };
            object? after = null;
            try { after = MainThreadDispatcher.Run(MapState, 3000); } catch (Exception) { }
            ValAgentBridgePlugin.ModLogger.LogInfo($"input/{what}");
            return new { success = true, did = what, mapBefore = map, mapAfter = after };
        }

        private static IEnumerator Wrap(IEnumerator inner, System.Threading.ManualResetEventSlim done)
        {
            try
            {
                while (true)
                {
                    OwnerControl.SimulatingUntilFrame = Time.frameCount + 3;
                    bool more;
                    try { more = inner.MoveNext(); }
                    catch (Exception ex) { ValAgentBridgePlugin.ModLogger.LogWarning("Simulated mouse input failed: " + ex.Message); break; }
                    if (!more) break;
                    yield return inner.Current;
                }
            }
            finally
            {
                OwnerControl.SimulatingUntilFrame = Time.frameCount + 3;
                done.Set();
            }
        }

        /// <summary>Large map zoom and offset when it is open (null otherwise), to show what the input did.</summary>
        private static object? MapState()
        {
            var map = Minimap.instance;
            if (map == null || !Minimap.IsOpen() || MapZoom == null || MapOffset == null) return null;
            var off = MapOffset(map);
            return new { zoom = Math.Round(MapZoom(map), 4), offsetX = Math.Round(off.x), offsetZ = Math.Round(off.z) };
        }

        /// <summary>Screen fractions (0,0 top left) to Input System pixels (0,0 bottom left).</summary>
        private static Vector2 ToScreen(Vector2 fraction) =>
            new Vector2(Mathf.Clamp01(fraction.x) * Screen.width, (1f - Mathf.Clamp01(fraction.y)) * Screen.height);

        private static void QueueMouse(Vector2 position, Vector2 delta, Vector2 scroll, MouseButton? held)
        {
            var mouse = Mouse.current;
            if (mouse == null) return;
            var state = new MouseState { position = position, delta = delta, scroll = scroll };
            if (held.HasValue) state = state.WithButton(held.Value, true);
            InputSystem.QueueStateEvent(mouse, state);
        }

        private static IEnumerator ScrollRoutine(int notches, float x, float y, float perNotch)
        {
            var mouse = Mouse.current;
            var pos = x >= 0f && y >= 0f ? ToScreen(new Vector2(x, y)) : mouse.position.ReadValue();
            if (perNotch <= 0f)
                perNotch = InputSystem.settings.scrollDeltaBehavior == InputSettings.ScrollDeltaBehavior.UniformAcrossAllPlatforms ? 1f : 120f;
            QueueMouse(pos, Vector2.zero, Vector2.zero, null);
            yield return null;
            for (int i = 0; i < Math.Abs(notches); i++)
            {
                QueueMouse(pos, Vector2.zero, new Vector2(0f, Math.Sign(notches) * perNotch), null);
                yield return null;
                QueueMouse(pos, Vector2.zero, Vector2.zero, null);
                yield return null;
                yield return null;
            }
        }

        private static IEnumerator DragRoutine(Vector2 from, Vector2 to, MouseButton button, float seconds)
        {
            Vector2 a = ToScreen(from), b = ToScreen(to), last = a;
            QueueMouse(a, Vector2.zero, Vector2.zero, null);
            yield return null;
            yield return null;
            QueueMouse(a, Vector2.zero, Vector2.zero, button);
            float start = Time.unscaledTime;
            while (Time.unscaledTime - start < 0.1f) yield return null;    // pressed before it moves, like a hand
            start = Time.unscaledTime;
            while (true)
            {
                float t = Mathf.Clamp01((Time.unscaledTime - start) / seconds);
                var p = Vector2.Lerp(a, b, t);
                QueueMouse(p, p - last, Vector2.zero, button);
                last = p;
                yield return null;
                if (t >= 1f) break;
            }
            QueueMouse(b, Vector2.zero, Vector2.zero, button);
            yield return null;
            QueueMouse(b, Vector2.zero, Vector2.zero, null);
            yield return null;
            yield return null;
        }

        private static bool TryKey(string name, out KeyCode key)
        {
            var n = name.Trim();
            switch (n.ToLowerInvariant())
            {
                case "ctrl": case "control": key = KeyCode.LeftControl; return true;
                case "shift": key = KeyCode.LeftShift; return true;
                case "alt": key = KeyCode.LeftAlt; return true;
                case "enter": key = KeyCode.Return; return true;
                case "esc": key = KeyCode.Escape; return true;
            }
            if (n.Length == 1 && char.IsDigit(n[0])) n = "Alpha" + n;
            return Enum.TryParse(n, true, out key) && key != KeyCode.None && !n.StartsWith("Mouse") && !n.StartsWith("Joystick");
        }

        // ---- Game controls: queued Input System keyboard events (press, then release on the release frame) ----

        private static IEnumerator PressInputSystem(List<KeyCode> keys, int releaseFrame)
        {
            var controls = keys.Select(ToInputSystem).Where(k => k != Key.None).ToList();
            Queue(controls, 1f);
            while (Time.frameCount < releaseFrame - 1) yield return null;
            Queue(controls, 0f);
        }

        private static void Queue(List<Key> keys, float value)
        {
            var kb = Keyboard.current;
            if (kb == null || keys.Count == 0) return;
            using (StateEvent.From(kb, out var eventPtr))
            {
                foreach (var k in keys) kb[k].WriteValueIntoEvent(value, eventPtr);
                InputSystem.QueueEvent(eventPtr);
            }
        }

        private static Key ToInputSystem(KeyCode code)
        {
            var name = code.ToString();
            if (name.StartsWith("Alpha")) name = "Digit" + name.Substring(5);
            else if (name.StartsWith("Keypad") && name.Length == 7 && char.IsDigit(name[6])) name = "Numpad" + name.Substring(6);
            name = name switch
            {
                "Return" => "Enter",
                "LeftControl" => "LeftCtrl",
                "RightControl" => "RightCtrl",
                "BackQuote" => "Backquote",
                "KeypadEnter" => "NumpadEnter",
                _ => name
            };
            return Enum.TryParse<Key>(name, true, out var key) ? key : Key.None;
        }

        // ---- Mod hotkeys: BepInEx's input reader (UnityInput.Current) answers true for simulated keys ----

        private static void EnsurePatched()
        {
            if (_patched || _patchProblem != null) return;
            try
            {
                var reader = UnityInput.Current.GetType();
                var harmony = new Harmony("BarryWhite.ValAgentBridge.input");
                foreach (var (method, postfix) in new[] { ("GetKey", nameof(KeyPostfix)), ("GetKeyDown", nameof(KeyDownPostfix)), ("GetKeyUp", nameof(KeyUpPostfix)) })
                {
                    var target = AccessTools.Method(reader, method, new[] { typeof(KeyCode) });
                    if (target == null) throw new MissingMethodException(reader.Name, method);
                    harmony.Patch(target, postfix: new HarmonyMethod(typeof(InputTools), postfix));
                }
                _patched = true;
                ValAgentBridgePlugin.ModLogger.LogInfo($"input/press_key: patched {reader.Name} for simulated mod hotkeys");
            }
            catch (Exception ex)
            {
                _patchProblem = "Mod hotkeys not reached: " + ex.Message;
                ValAgentBridgePlugin.ModLogger.LogWarning(_patchProblem);
            }
        }

        private static void KeyPostfix(KeyCode key, ref bool __result)
        {
            if (!__result && Keys.Count > 0 && Keys.TryGetValue(key, out var s) && Time.frameCount >= s.down && Time.frameCount < s.up) __result = true;
        }

        private static void KeyDownPostfix(KeyCode key, ref bool __result)
        {
            if (!__result && Keys.Count > 0 && Keys.TryGetValue(key, out var s) && Time.frameCount == s.down) __result = true;
        }

        private static void KeyUpPostfix(KeyCode key, ref bool __result)
        {
            if (!__result && Keys.Count > 0 && Keys.TryGetValue(key, out var s) && Time.frameCount == s.up) __result = true;
        }
    }
}
