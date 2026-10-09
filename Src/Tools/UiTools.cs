using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Lib.GAB.Tools;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ValBridgeServer.Tools
{
    // What's on screen as data: popups, buttons, toggles and the messages the game showed.
    public class UiTools
    {
        private static readonly AccessTools.FieldRef<UnifiedPopup> PopupInstance =
            AccessTools.StaticFieldRefAccess<UnifiedPopup>(AccessTools.Field(typeof(UnifiedPopup), "instance"));
        private static readonly AccessTools.FieldRef<UnifiedPopup, TextMeshProUGUI> PopupHeader =
            AccessTools.FieldRefAccess<UnifiedPopup, TextMeshProUGUI>("headerText");
        private static readonly AccessTools.FieldRef<UnifiedPopup, TextMeshProUGUI> PopupBody =
            AccessTools.FieldRefAccess<UnifiedPopup, TextMeshProUGUI>("bodyText");

        [Tool("ui/list", Description = "What can be clicked right now: the open popup (if any) and the visible, clickable buttons and toggles with their labels and ids.")]
        public object List(
            [ToolParameter(Description = "Only controls whose label or name contains this (optional)")] string filter = "",
            [ToolParameter(Description = "Max controls (default 60)")] int limit = 60,
            [ToolParameter(Description = "Also list HUD controls such as hotbar slots (default false)")] bool includeHud = false)
        {
            return MainThreadDispatcher.RunTool(() =>
            {
                var (header, body) = Popup();
                object? popup = header == null && body == null && !UnifiedPopup.IsVisible() ? null : new { header, body };
                var controls = Controls()
                    .Where(c => includeHud || !c.inHud)
                    .Where(c => filter.Length == 0 ||
                                c.label.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                c.path.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                    .Take(Mathf.Clamp(limit, 1, 300))
                    .Select(c => new { id = c.id, kind = c.kind, label = c.label, isOn = c.isOn, path = c.path })
                    .ToList();
                return new { success = true, popup, count = controls.Count, controls };
            });
        }

        [Tool("ui/click", Description = "Click a button (or flip a toggle) by id from ui/list, or by its label.")]
        public object Click(
            [ToolParameter(Description = "Control id from ui/list (0 = use label)")] int id = 0,
            [ToolParameter(Description = "Label text; exact match first, then 'contains'")] string label = "")
        {
            return MainThreadDispatcher.RunTool(() =>
            {
                var all = Controls().ToList();
                List<Control> hits;
                if (id != 0)
                    hits = all.Where(c => c.id == id).ToList();
                else
                {
                    hits = all.Where(c => string.Equals(c.label, label, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (hits.Count == 0)
                        hits = all.Where(c => label.Length > 0 && c.label.IndexOf(label, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                }
                if (hits.Count == 0) return new { success = false, error = "No visible, clickable control matches. Use ui/list." };
                if (hits.Count > 1)
                    return new { success = false, error = "Several controls match: " + string.Join("; ", hits.Take(8).Select(h => $"{h.id} '{h.label}'")) };

                var hit = hits[0];
                if (hit.selectable is Button b) b.onClick.Invoke();
                else if (hit.selectable is Toggle t) t.isOn = !t.isOn;
                ValBridgeServerPlugin.ModLogger.LogInfo($"ui/click: '{hit.label}' ({hit.path})");
                return new { success = true, clicked = hit.label, kind = hit.kind, path = hit.path };
            });
        }

        [Tool("ui/messages", Description = "Messages the game showed on screen (centre and top-left), newest last, e.g. portal hints or 'You can't do that'. Pass 'last' from a previous call as 'since' for only new ones.")]
        public object Messages(
            [ToolParameter(Description = "Only messages after this number (0 = all kept, up to 200)")] long since = 0,
            [ToolParameter(Description = "Max messages (default 30)")] int limit = 30)
        {
            var list = Recorder.MessagesSince(since, Mathf.Clamp(limit, 1, 200));
            return new { success = true, last = Recorder.LastMessageSeq, count = list.Count, messages = list };
        }

        /// <summary>Header and body of the open popup (both null if none). Main thread.</summary>
        public static (string? header, string? body) Popup()
        {
            if (!UnifiedPopup.IsVisible()) return (null, null);
            var p = PopupInstance();
            if (p == null) return (null, null);
            var header = PopupHeader(p);
            var body = PopupBody(p);
            return (header != null ? header.text : null, body != null ? body.text : null);
        }

        private sealed class Control
        {
            public int id;
            public string kind = "";
            public string label = "";
            public bool? isOn;
            public string path = "";
            public bool inHud;
            public Selectable selectable = null!;
        }

        private static IEnumerable<Control> Controls()
        {
            foreach (var s in UnityEngine.Object.FindObjectsByType<Selectable>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (s == null || !s.isActiveAndEnabled || !s.IsInteractable()) continue;
                if (!(s is Button) && !(s is Toggle)) continue;
                // Skip controls under a hidden canvas or a fully faded group.
                var canvas = s.GetComponentInParent<Canvas>();
                if (canvas == null || !canvas.isActiveAndEnabled) continue;
                var group = s.GetComponentInParent<CanvasGroup>();
                if (group != null && (group.alpha <= 0.01f || !group.interactable)) continue;

                var tmp = s.GetComponentInChildren<TMP_Text>();
                var legacy = tmp == null ? s.GetComponentInChildren<Text>() : null;
                var label = tmp != null ? tmp.text : legacy != null ? legacy.text : s.name;
                yield return new Control
                {
                    id = s.GetInstanceID(),
                    kind = s is Button ? "button" : "toggle",
                    label = (label ?? "").Trim(),
                    isOn = s is Toggle tg ? tg.isOn : (bool?)null,
                    path = PathOf(s.transform),
                    inHud = UnderHud(s.transform),
                    selectable = s
                };
            }
        }

        // The in-game HUD (hotbar, minimap, guardian power) lives under "hudroot".
        private static bool UnderHud(Transform t)
        {
            for (var x = t; x != null; x = x.parent)
                if (x.name == "hudroot") return true;
            return false;
        }

        private static string PathOf(Transform t)
        {
            var names = new List<string>();
            for (var x = t; x != null && names.Count < 6; x = x.parent) names.Add(x.name);
            names.Reverse();
            return string.Join("/", names);
        }
    }
}
