using HarmonyLib;
using UnityEngine;

namespace ValAgentBridge
{
    /// <summary>
    /// Turning the player and camera. The camera follows Character.m_lookYaw and Player.m_lookPitch (both private);
    /// Character.SetLookDir alone is overwritten by the player's own look on the next frame. Main thread only.
    /// </summary>
    public static class PlayerLook
    {
        private static readonly AccessTools.FieldRef<Character, Quaternion> LookYaw = AccessTools.FieldRefAccess<Character, Quaternion>("m_lookYaw");
        private static readonly AccessTools.FieldRef<Player, float> LookPitch = AccessTools.FieldRefAccess<Player, float>("m_lookPitch");

        /// <summary>Heading in degrees (0 = +Z north, 90 = +X east); tilt in degrees, up positive.</summary>
        public static void Set(Player player, float heading, float tilt)
        {
            tilt = Mathf.Clamp(tilt, -80f, 80f);
            LookYaw(player) = Quaternion.Euler(0f, heading, 0f);
            LookPitch(player) = -tilt;
            player.SetLookDir(Quaternion.Euler(-tilt, heading, 0f) * Vector3.forward);
            player.FaceLookDirection();
        }

        /// <summary>Face a direction on the ground plane, camera level. False if the direction is (almost) zero.</summary>
        public static bool Face(Player player, Vector3 direction)
        {
            direction.y = 0f;
            if (direction.magnitude < 0.01f) return false;
            Set(player, Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg, 0f);
            return true;
        }
    }
}
