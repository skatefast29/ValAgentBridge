using System;
using HarmonyLib;
using Lib.GAB.Events;
using UnityEngine;

namespace ValAgentBridge
{
    /// <summary>
    /// Fills the "player/death" and "player/health_changed" event channels (they existed but nothing sent them),
    /// and keeps the same events in Recorder for events/read and test reports.
    /// Health changes are sampled twice a second and only reported when they move by at least 1 point.
    /// </summary>
    public static class PlayerEvents
    {
        private static IEventManager? _events;
        private static float _lastHealth = -1f;
        private static float _nextSample;

        public static void Install(Harmony harmony, IEventManager events)
        {
            _events = events;
            var onDeath = AccessTools.Method(typeof(Player), nameof(Player.OnDeath));
            if (onDeath != null)
                harmony.Patch(onDeath, postfix: new HarmonyMethod(typeof(PlayerEvents), nameof(OnDeathPostfix)));
            else
                ValAgentBridgePlugin.ModLogger.LogWarning("Player.OnDeath not found; player/death events won't be sent.");
        }

        /// <summary>Called from the plugin's Update (main thread).</summary>
        public static void Tick()
        {
            if (Time.unscaledTime < _nextSample) return;
            _nextSample = Time.unscaledTime + 0.5f;
            var player = Player.m_localPlayer;
            if (player == null) { _lastHealth = -1f; return; }

            var health = player.GetHealth();
            if (_lastHealth < 0f) { _lastHealth = health; return; }
            var delta = health - _lastHealth;
            if (Mathf.Abs(delta) < 1f) return;
            _lastHealth = health;
            Send("player/health_changed", new
            {
                health = (float)Math.Round(health, 1),
                maxHealth = (float)Math.Round(player.GetMaxHealth(), 1),
                change = (float)Math.Round(delta, 1)
            });
        }

        private static void OnDeathPostfix(Player __instance)
        {
            if (__instance != Player.m_localPlayer) return;
            var pos = __instance.transform.position;
            Send("player/death", new
            {
                position = new { x = (float)Math.Round(pos.x, 1), y = (float)Math.Round(pos.y, 1), z = (float)Math.Round(pos.z, 1) },
                biome = __instance.GetCurrentBiome().ToString()
            });
            _lastHealth = -1f;
        }

        private static void Send(string channel, object payload)
        {
            Recorder.AddEvent(channel, payload);
            try
            {
                _events?.EmitEventAsync(channel, payload).ContinueWith(t => { _ = t.Exception; });
            }
            catch (Exception)
            {
                // Nobody listening, or the connection dropped: the event is still in Recorder.
            }
        }
    }
}
