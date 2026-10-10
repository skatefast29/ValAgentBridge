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
