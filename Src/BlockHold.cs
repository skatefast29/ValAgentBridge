using System;
using HarmonyLib;

namespace ValAgentBridge
{
    /// <summary>
    /// Keeps the player blocking for player/block. The game's own controls call Player.SetControls every physics tick
    /// with the real keyboard state, which would end a block set once on the next tick, so a postfix there keeps
    /// m_blocking on while a hold is active. Patched on first use. Main thread only.
    /// </summary>
    public static class BlockHold
    {
        private static Player? _held;
        private static bool _patched;
        private static AccessTools.FieldRef<Character, bool>? _blocking;

        /// <summary>Starts or ends the hold. Returns a problem, or null.</summary>
        public static string? Set(Player player, bool active)
        {
            if (!active)
            {
                if (_held != null && _held == player && _blocking != null) _blocking(player) = false;
                _held = null;
                return null;
            }
            if (!_patched)
            {
                try
                {
                    _blocking = AccessTools.FieldRefAccess<Character, bool>("m_blocking");
                    var target = AccessTools.DeclaredMethod(typeof(Player), nameof(Player.SetControls));
                    if (target == null) return "Player.SetControls not found; blocking can't be held.";
                    new Harmony("BarryWhite.ValAgentBridge.block")
                        .Patch(target, postfix: new HarmonyMethod(typeof(BlockHold), nameof(SetControlsPostfix)));
                    _patched = true;
                }
                catch (Exception ex)
                {
                    return "Blocking can't be held: " + ex.GetBaseException().Message;
                }
            }
            _held = player;
            _blocking!(player) = true;
            return null;
        }

        private static void SetControlsPostfix(Player __instance)
        {
            if (_held == null) return;
            if (_held != Player.m_localPlayer) { _held = null; return; }    // logged out or died and respawned
            if (__instance == _held) _blocking!(__instance) = true;
        }
    }
}
