using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace Delphi.Diagnostics
{
    /// <summary>
    /// Self-installing frame-time probe — no scene wiring, no prefab to drag
    /// in. It hooks itself in via RuntimeInitializeOnLoadMethod, which is the
    /// point: a component that has to be placed in the scene can silently
    /// end up not there (see VrDashboardPanel), and the whole reason this
    /// exists is to stop guessing about what's actually running.
    ///
    /// Writes one CSV row per frame to ProfilerCaptures/, flushed to disk
    /// every ~1s so the file is readable WHILE play mode is still running,
    /// not just after it stops. Columns:
    ///   t_s, frame, dtMs, fps, cpuMainMs, cpuRenderMs, gpuMs, presentWaitMs, gc0, gc1, gc2
    ///
    /// cpuMainMs/cpuRenderMs/gpuMs/presentWaitMs come from
    /// UnityEngine.FrameTimingManager. dtMs/fps alone can't distinguish
    /// "game logic is slow" from "GPU can't keep up" from "we're stalled
    /// waiting on the compositor" — these four can.
    ///
    /// gc0/gc1/gc2 are the CUMULATIVE GC.CollectionCount() for each
    /// generation, logged every frame rather than diffed here on purpose —
    /// a diff computed at capture time can't be second-guessed later, but a
    /// running total can always be diffed afterward, and it's the only way
    /// to tell "a collection happened on exactly this frame" from "one
    /// happened sometime in the last second" when hunting a hitch.
    ///
    /// CAVEAT: cpuRenderMs and gpuMs need real GPU-timestamp support from
    /// the graphics API. On D3D11 they sometimes read back as 0 even though
    /// cpuMainMs is populated — that is itself informative (rules out
    /// GPU-bound, or means we need D3D12/Vulkan to see the rest), not a bug
    /// in this logger.
    /// </summary>
    public static class FrameTimingLogger
    {
        private const int MaxFrames = 12000; // ~2-9 min depending on fps — stops itself so it can't run forever unnoticed

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            string dir = Path.Combine(Application.dataPath, "..", "ProfilerCaptures");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"FrameTiming_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            File.WriteAllText(path, "t_s,frame,dtMs,fps,cpuMainMs,cpuRenderMs,gpuMs,presentWaitMs,gc0,gc1,gc2\n");

            var go = new GameObject("[FrameTimingLogger]") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<Pump>().Path = path;

            Debug.Log($"[FrameTimingLogger] Logging every frame to {path} (auto-stops after {MaxFrames} frames).");
        }

        private class Pump : MonoBehaviour
        {
            public string Path;

            private readonly StringBuilder _buffer = new StringBuilder();
            private readonly FrameTiming[] _timings = new FrameTiming[1];
            private int _frame;
            private float _flushTimer;

            private void Update()
            {
                FrameTimingManager.CaptureFrameTimings();
                uint n = FrameTimingManager.GetLatestTimings(1, _timings);

                float dtMs = Time.unscaledDeltaTime * 1000f;
                float fps = Time.unscaledDeltaTime > 0f ? 1f / Time.unscaledDeltaTime : 0f;
                double cpuMain = 0, cpuRender = 0, gpu = 0, present = 0;
                if (n > 0)
                {
                    cpuMain = _timings[0].cpuMainThreadFrameTime;
                    cpuRender = _timings[0].cpuRenderThreadFrameTime;
                    gpu = _timings[0].gpuFrameTime;
                    present = _timings[0].cpuMainThreadPresentWaitTime;
                }

                _buffer.Append(Time.unscaledTime.ToString("F3")).Append(',')
                       .Append(_frame).Append(',')
                       .Append(dtMs.ToString("F2")).Append(',')
                       .Append(fps.ToString("F1")).Append(',')
                       .Append(cpuMain.ToString("F2")).Append(',')
                       .Append(cpuRender.ToString("F2")).Append(',')
                       .Append(gpu.ToString("F2")).Append(',')
                       .Append(present.ToString("F2")).Append(',')
                       .Append(GC.CollectionCount(0)).Append(',')
                       .Append(GC.CollectionCount(1)).Append(',')
                       .Append(GC.CollectionCount(2)).Append('\n');

                _frame++;
                _flushTimer += Time.unscaledDeltaTime;

                bool done = _frame >= MaxFrames;
                if (_flushTimer >= 1f || done)
                {
                    _flushTimer = 0f;
                    File.AppendAllText(Path, _buffer.ToString());
                    _buffer.Clear();
                }

                if (done)
                {
                    Debug.Log($"[FrameTimingLogger] Reached {_frame} frames — stopped automatically. Log at {Path}");
                    Destroy(gameObject);
                }
            }

            private void OnApplicationQuit()
            {
                if (_buffer.Length > 0) File.AppendAllText(Path, _buffer.ToString());
            }
        }
    }
}
