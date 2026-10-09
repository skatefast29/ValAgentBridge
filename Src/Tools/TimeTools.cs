using Lib.GAB.Tools;
using UnityEngine;

namespace ValBridgeServer.Tools
{
    // Single player only: the game refuses to pause or change speed while other players are connected.
    public class TimeTools
    {
        [Tool("time/status", Description = "Game speed and pause state.")]
        public object Status()
        {
            return MainThreadDispatcher.RunTool(() => new
            {
                success = true,
                timeScale = Time.timeScale,
                paused = Game.IsPaused(),
                inWorld = Player.m_localPlayer != null
            });
        }

        [Tool("time/set_speed", Description = "Run the game faster or slower (1 = normal, 0.5 = half, up to 10). Single player only. Physics gets less accurate at high speeds.")]
        public object SetSpeed(
            [ToolParameter(Description = "Speed multiplier, 0.1 to 10")] float speed = 1f)
        {
            return MainThreadDispatcher.RunTool(() =>
            {
                if (Player.m_localPlayer == null) return new { success = false, error = "Not in a world." };
                var clamped = Mathf.Clamp(speed, 0.1f, 10f);
                // Takes effect on the next frame; the game ignores it when it can't pause (other players online).
                Game.FadeTimeScale(clamped, 0f);
                return new { success = true, speed = clamped };
            });
        }

        [Tool("time/pause", Description = "Pause or unpause the game (single player only).")]
        public object Pause(
            [ToolParameter(Description = "true = pause, false = unpause")] bool paused = true)
        {
            return MainThreadDispatcher.RunTool(() =>
            {
                if (Player.m_localPlayer == null) return new { success = false, error = "Not in a world." };
                if (paused) Game.Pause(); else Game.Unpause();
                return new { success = true, paused = Game.IsPaused() };
            });
        }
    }
}
