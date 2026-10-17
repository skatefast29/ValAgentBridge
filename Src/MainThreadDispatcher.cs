using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace ValAgentBridge
{
    /// <summary>
    /// GABP requests arrive on a network thread, but Unity only allows game objects, transforms and Time on the
    /// main thread. Work is queued here and run in Update.
    /// </summary>
    public class MainThreadDispatcher : MonoBehaviour
    {
        private static MainThreadDispatcher? _instance;
        private static readonly ConcurrentQueue<Action> ExecutionQueue = new ConcurrentQueue<Action>();
        private static int _mainThreadId = -1;

        public static MainThreadDispatcher Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("ValAgentBridge_MainThreadDispatcher");
                    _instance = go.AddComponent<MainThreadDispatcher>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        /// <summary>Call once from the main thread (Plugin.Start) before the server starts.</summary>
        public static void Init()
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            _ = Instance;
        }

        public void Enqueue(Action action) => ExecutionQueue.Enqueue(action);

        /// <summary>
        /// Runs <paramref name="work"/> on the main thread and waits for the result (exceptions are rethrown here,
        /// wrapped in an AggregateException). Don't call it from inside queued work (that would wait on itself).
        /// </summary>
        public static T Run<T>(Func<T> work, int timeoutMs = 10000)
        {
            if (Thread.CurrentThread.ManagedThreadId == _mainThreadId)
                return work();
            var tcs = new TaskCompletionSource<T>();
            Instance.Enqueue(() =>
            {
                try { tcs.SetResult(work()); }
                catch (Exception e) { tcs.SetException(e); }
            });
            if (!tcs.Task.Wait(timeoutMs))
                throw new TimeoutException("The game's main thread did not answer in time (loading screen or paused?)");
            return tcs.Task.Result;
        }

        /// <summary>
        /// Run for tool handlers: a timeout or an exception becomes a { success = false, error } answer instead of
        /// a protocol error. On a timeout the queued work may still run later, once the game catches up.
        /// </summary>
        public static object RunTool(Func<object> work, int timeoutMs = 15000)
        {
            try
            {
                return Run(work, timeoutMs);
            }
            catch (TimeoutException)
            {
                return new { success = false, error = "The game's main thread did not answer in time (loading screen or paused?). The action may still happen when the game catches up." };
            }
            catch (Exception ex)
            {
                var e = ex.GetBaseException();
                return new { success = false, error = $"{e.GetType().Name}: {e.Message}" };
            }
        }

        private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
        private static long _lastFrameMs = -1;
        private static float _smoothedFrameMs;

        /// <summary>Milliseconds since the main thread last ran a frame (-1 before the first). Readable from any thread:
        /// a large value means the game is frozen or inside a long load step.</summary>
        public static long FrameAgeMs => Interlocked.Read(ref _lastFrameMs) < 0 ? -1 : Clock.ElapsedMilliseconds - Interlocked.Read(ref _lastFrameMs);

        /// <summary>Recent frame time in ms (smoothed), as last seen by the main thread.</summary>
        public static float FrameMs => _smoothedFrameMs;

        // ---- Frames with bridge work, for ValPerfMonitor (read by reflection; keep these names) ----
        // Tool calls run here on the main thread and can take tens of ms; a measurement should leave those frames out.

        private static readonly int[] WorkRing = new int[256];

        /// <summary>Last Time.frameCount in which queued bridge work ran (-1 = none yet).</summary>
        public static int LastWorkFrame = -1;

        /// <summary>Milliseconds the bridge's work took in <see cref="LastWorkFrame"/>.</summary>
        public static double LastWorkMs;

        /// <summary>Frames with bridge work since the game started.</summary>
        public static long WorkFrames;

        /// <summary>True if the bridge ran queued work in that frame (the last 256 frames are remembered). Main thread.</summary>
        public static bool WorkedInFrame(int frame) => frame >= 0 && WorkRing[frame & 255] == frame + 1;

        private void Update()
        {
            Interlocked.Exchange(ref _lastFrameMs, Clock.ElapsedMilliseconds);
            var dt = Time.unscaledDeltaTime * 1000f;
            _smoothedFrameMs = _smoothedFrameMs <= 0 ? dt : _smoothedFrameMs * 0.9f + dt * 0.1f;
            try { OwnerControl.Watch(); }
            catch (Exception) { }
            if (ExecutionQueue.IsEmpty) return;
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            while (ExecutionQueue.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception e) { ValAgentBridgePlugin.ModLogger.LogError($"Queued bridge work failed: {e}"); }
            }
            int frame = Time.frameCount;
            WorkRing[frame & 255] = frame + 1;
            LastWorkMs = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            LastWorkFrame = frame;
            WorkFrames++;
        }
    }
}
