using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BepInEx;
using Lib.GAB.Tools;
using UnityEngine;

namespace ValAgentBridge.Tools
{
    public class ScreenTools
    {
        private const int KeepFiles = 30;
        private static string Folder => Path.Combine(Paths.BepInExRootPath, "ValAgentBridge", "screenshots");

        [Tool("screen/capture", Description = "Take a screenshot of the game window (menus and HUD included). Saves a JPG and returns its file path so it can be opened and viewed.")]
        public object Capture(
            [ToolParameter(Description = "Shrink to at most this width in pixels (default 1280; 0 = full size)")] int maxWidth = 1280,
            [ToolParameter(Description = "JPG quality 1-100 (default 80)")] int quality = 80)
        {
            var tcs = new TaskCompletionSource<object>();
            MainThreadDispatcher.Instance.Enqueue(() =>
                MainThreadDispatcher.Instance.StartCoroutine(CaptureAtEndOfFrame(maxWidth, Mathf.Clamp(quality, 1, 100), tcs)));
            if (!tcs.Task.Wait(10000))
                return new { success = false, error = "The game did not render a frame in time (loading screen or minimised?)" };
            return tcs.Task.Result;
        }

        private static IEnumerator CaptureAtEndOfFrame(int maxWidth, int quality, TaskCompletionSource<object> tcs)
        {
            // The back buffer is only complete once the frame (UI included) has rendered.
            yield return new WaitForEndOfFrame();
            Texture2D? shot = null;
            Texture2D? small = null;
            try
            {
                shot = ScreenCapture.CaptureScreenshotAsTexture();
                int fullW = shot.width, fullH = shot.height;
                var output = shot;
                if (maxWidth > 0 && shot.width > maxWidth)
                {
                    int w = maxWidth, h = Mathf.Max(1, Mathf.RoundToInt(shot.height * (maxWidth / (float)shot.width)));
                    var rt = RenderTexture.GetTemporary(w, h, 0);
                    var previous = RenderTexture.active;
                    Graphics.Blit(shot, rt);
                    RenderTexture.active = rt;
                    small = new Texture2D(w, h, TextureFormat.RGB24, false);
                    small.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                    small.Apply();
                    RenderTexture.active = previous;
                    RenderTexture.ReleaseTemporary(rt);
                    output = small;
                }

                Directory.CreateDirectory(Folder);
                var path = Path.Combine(Folder, $"shot-{DateTime.Now:yyyyMMdd-HHmmss-fff}.jpg");
                File.WriteAllBytes(path, output.EncodeToJPG(quality));
                PruneOldShots();
                tcs.TrySetResult(new { success = true, path, width = output.width, height = output.height, screenWidth = fullW, screenHeight = fullH });
            }
            catch (Exception ex)
            {
                tcs.TrySetResult(new { success = false, error = ex.Message });
            }
            finally
            {
                if (shot != null) UnityEngine.Object.Destroy(shot);
                if (small != null) UnityEngine.Object.Destroy(small);
            }
        }

        private static void PruneOldShots()
        {
            foreach (var old in new DirectoryInfo(Folder).GetFiles("shot-*.jpg").OrderByDescending(f => f.Name).Skip(KeepFiles))
            {
                try { old.Delete(); } catch { /* in use; next time */ }
            }
        }
    }
}
