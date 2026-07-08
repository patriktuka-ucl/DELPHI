using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using Delphi.Simulation;

namespace Delphi
{
    /// <summary>
    /// Records a session to disk: one mp4 per connected frame channel plus a
    /// sensors.csv where row N is sampled at the SAME clock tick as video
    /// frame N of every feed — that shared tick (at `fps`) is the sync
    /// contract playback relies on. A meta.json manifest ties it together.
    ///
    /// Session folder: {persistentDataPath}/Sessions/yyyyMMdd_HHmmss/
    ///
    /// Video path per feed: source texture → Blit into a fixed-size RT →
    /// AsyncGPUReadback (processed strictly in submission order) → byte[] →
    /// FfmpegVideoWriter's background thread → mp4. Nothing blocks the main
    /// thread except the final drain in StopRecording.
    ///
    /// If the sim ever runs slower than `fps`, the latest frame is written
    /// multiple times (and the csv row duplicated) so wall-clock sync is
    /// preserved instead of the video silently shortening.
    /// </summary>
    public class SessionRecorder : MonoBehaviour
    {
        [Header("Links (auto-found if left empty)")]
        public DelphiManager manager;
        [Tooltip("Optional — when present, ego telemetry (s, speed) is " +
                 "logged alongside the sensors.")]
        public CarDriver egoCar;

        [Header("Capture")]
        [Tooltip("Video frames AND sensor-log rows per second.")]
        public int fps = 30;
        [Tooltip("Feeds wider than this are scaled down (aspect kept).")]
        public int maxFeedWidth = 1280;

        [Header("Output")]
        [Tooltip("Empty = {persistentDataPath}/Sessions")]
        public string sessionsFolder = "";
        [Tooltip("Empty = auto-detect ffmpeg in the usual macOS locations.")]
        public string ffmpegPath = "";
        [Tooltip("GPU readbacks arrive top-down on Metal/DX, so frames need " +
                 "a vertical flip before encoding. Untick only if recordings " +
                 "come out upside-down.")]
        public bool flipVideoVertically = true;

        public bool IsRecording { get; private set; }
        public float ElapsedSeconds => IsRecording ? Time.time - _startTime : 0f;
        public string LastSessionPath { get; private set; }

        public static string DefaultSessionsRoot =>
            Path.Combine(Application.persistentDataPath, "Sessions");
        public string SessionsRoot =>
            string.IsNullOrEmpty(sessionsFolder) ? DefaultSessionsRoot : sessionsFolder;

        // ── Internals ───────────────────────────────────────────────────
        private class Feed
        {
            public FrameChannel channel;
            public RenderTexture rt;
            public FfmpegVideoWriter writer;
            public readonly Queue<(AsyncGPUReadbackRequest req, int repeats)> pending = new();
            public int width, height;
            public Texture2D syncReadbackTex; // only on hardware without async readback
        }

        private readonly List<Feed> _feeds = new();
        private StreamWriter _csv;
        private string _sessionPath;
        private float _startTime;
        private float _nextTickTime;
        private int _ticksWritten;
        private bool _readbackSupported;

        private static readonly string[] FfmpegCandidates =
        {
            "/opt/homebrew/bin/ffmpeg", "/usr/local/bin/ffmpeg", "/usr/bin/ffmpeg"
        };

        private void Awake()
        {
            if (manager == null) manager = FindFirstObjectByType<DelphiManager>();
            if (egoCar == null) egoCar = FindFirstObjectByType<CarDriver>();
            _readbackSupported = SystemInfo.supportsAsyncGPUReadback;
        }

        // ── Control ─────────────────────────────────────────────────────
        public bool StartRecording()
        {
            if (IsRecording) return false;
            if (manager == null)
            {
                Debug.LogError("[SessionRecorder] No DelphiManager — nothing to record.");
                return false;
            }
            if (manager.IsInPlayback)
            {
                Debug.LogWarning("[SessionRecorder] Refusing to record while a session " +
                                 "is loaded for playback — eject it first.");
                return false;
            }

            string ffmpeg = ResolveFfmpeg();
            if (ffmpeg == null)
            {
                Debug.LogError("[SessionRecorder] ffmpeg not found — install it (brew " +
                               "install ffmpeg) or set ffmpegPath explicitly.");
                return false;
            }

            _sessionPath = Path.Combine(SessionsRoot,
                DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(_sessionPath);

            // One feed per frame channel that is actually delivering pixels.
            _feeds.Clear();
            foreach (var ch in DelphiManager.AllFrameChannels)
            {
                var tex = manager.GetFrame(ch);
                // WebCamTexture reports 16×16 until its first real frame.
                if (tex == null || tex.width <= 32) continue;

                int w = Mathf.Min(maxFeedWidth, tex.width);
                int h = Mathf.RoundToInt((float)w * tex.height / tex.width);
                w &= ~1; h &= ~1; // libx264 yuv420p needs even dimensions

                var feed = new Feed
                {
                    channel = ch,
                    width = w,
                    height = h,
                    rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32),
                    writer = new FfmpegVideoWriter(ffmpeg,
                        Path.Combine(_sessionPath, ch + ".mp4"),
                        w, h, fps, flipVideoVertically)
                };
                feed.rt.Create();
                _feeds.Add(feed);
            }

            // Sensor log — header defines the column order meta.json records.
            _csv = new StreamWriter(Path.Combine(_sessionPath, "sensors.csv"));
            var header = new List<string> { "time_s" };
            foreach (var ch in DelphiManager.AllChannels) header.Add(ch.ToString());
            header.Add("ego_s_m");
            header.Add("ego_speed_kmh");
            _csv.WriteLine(string.Join(",", header));

            _startTime = Time.time;
            _nextTickTime = 0f;
            _ticksWritten = 0;
            IsRecording = true;
            Debug.Log($"[SessionRecorder] Recording {_feeds.Count} video feed(s) + " +
                      $"sensor log at {fps} fps → {_sessionPath}");
            return true;
        }

        public void StopRecording()
        {
            if (!IsRecording) return;
            IsRecording = false;
            float duration = Time.time - _startTime;

            foreach (var feed in _feeds)
            {
                // Drain in order; a blocking wait here is fine — recording is over.
                while (feed.pending.Count > 0)
                {
                    var (req, repeats) = feed.pending.Dequeue();
                    req.WaitForCompletion();
                    PushReadback(feed, req, repeats);
                }
                feed.writer.Finish();
                feed.rt.Release();
                Destroy(feed.rt);
                if (feed.syncReadbackTex != null) Destroy(feed.syncReadbackTex);
            }

            _csv.Flush();
            _csv.Close();
            _csv = null;

            var meta = new SessionMeta
            {
                started = DateTime.Now.AddSeconds(-duration).ToString("o"),
                fps = fps,
                duration = duration,
                scalarChannels = Array.ConvertAll(DelphiManager.AllChannels, c => c.ToString()),
                feeds = _feeds.ConvertAll(f => new SessionFeedMeta
                {
                    channel = f.channel.ToString(),
                    file = f.channel + ".mp4",
                    width = f.width,
                    height = f.height
                }).ToArray()
            };
            File.WriteAllText(Path.Combine(_sessionPath, "meta.json"),
                              JsonUtility.ToJson(meta, prettyPrint: true));

            _feeds.Clear();
            LastSessionPath = _sessionPath;
            Debug.Log($"[SessionRecorder] Saved {duration:F1}s session → {_sessionPath}");
        }

        private void OnDestroy() => StopRecording();
        private void OnApplicationQuit() => StopRecording();

        // ── Capture loop ────────────────────────────────────────────────
        // LateUpdate so DelphiManager.Update has already polled every sensor
        // (and CameraFeedSensor has rendered) this frame.
        private void LateUpdate()
        {
            if (!IsRecording) return;

            foreach (var feed in _feeds) ProcessReadbacks(feed, blocking: false);

            float t = Time.time - _startTime;
            int due = 0;
            while (_nextTickTime <= t)
            {
                due++;
                _nextTickTime += 1f / fps;
            }
            if (due == 0) return;

            // CSV: one row per tick (rows stay 1:1 with video frames).
            for (int i = 0; i < due; i++)
                WriteCsvRow((_ticksWritten + i) / (float)fps);
            _ticksWritten += due;

            // Video: capture once, repeat `due` times if we're behind.
            foreach (var feed in _feeds)
            {
                var src = manager.GetFrame(feed.channel);
                if (src != null) Graphics.Blit(src, feed.rt);
                else Graphics.Blit(Texture2D.blackTexture, feed.rt);

                if (_readbackSupported)
                {
                    var req = AsyncGPUReadback.Request(feed.rt, 0, TextureFormat.RGBA32);
                    feed.pending.Enqueue((req, due));
                }
                else
                {
                    // Sync ReadPixels fallback — slower, but never wrong.
                    if (feed.syncReadbackTex == null)
                        feed.syncReadbackTex = new Texture2D(feed.width, feed.height,
                                                             TextureFormat.RGBA32, false);
                    var prev = RenderTexture.active;
                    RenderTexture.active = feed.rt;
                    feed.syncReadbackTex.ReadPixels(new Rect(0, 0, feed.width, feed.height), 0, 0);
                    RenderTexture.active = prev;
                    byte[] bytes = feed.syncReadbackTex.GetRawTextureData();
                    for (int i = 0; i < due; i++) feed.writer.Push(bytes);
                }
            }
        }

        private void ProcessReadbacks(Feed feed, bool blocking)
        {
            while (feed.pending.Count > 0)
            {
                var (req, repeats) = feed.pending.Peek();
                if (!req.done && !blocking) return;
                if (blocking) req.WaitForCompletion();
                feed.pending.Dequeue();
                PushReadback(feed, req, repeats);
            }
        }

        private void PushReadback(Feed feed, AsyncGPUReadbackRequest req, int repeats)
        {
            if (req.hasError)
            {
                // Keep the frame count intact so A/V sync survives.
                var black = new byte[feed.width * feed.height * 4];
                for (int i = 0; i < repeats; i++) feed.writer.Push(black);
                return;
            }
            byte[] bytes = req.GetData<byte>().ToArray();
            for (int i = 0; i < repeats; i++) feed.writer.Push(bytes);
        }

        private void WriteCsvRow(float t)
        {
            var cells = new List<string>(DelphiManager.AllChannels.Length + 3)
            {
                t.ToString("F4", CultureInfo.InvariantCulture)
            };
            foreach (var ch in DelphiManager.AllChannels)
            {
                float v = manager.GetValue(ch);
                cells.Add(float.IsNaN(v) ? "NaN"
                                         : v.ToString("F4", CultureInfo.InvariantCulture));
            }
            cells.Add(egoCar != null ? egoCar.S.ToString("F2", CultureInfo.InvariantCulture) : "NaN");
            cells.Add(egoCar != null ? egoCar.CurrentSpeedKmh.ToString("F2", CultureInfo.InvariantCulture) : "NaN");
            _csv.WriteLine(string.Join(",", cells));
        }

        private string ResolveFfmpeg()
        {
            if (!string.IsNullOrEmpty(ffmpegPath))
                return File.Exists(ffmpegPath) ? ffmpegPath : null;
            foreach (var p in FfmpegCandidates)
                if (File.Exists(p)) return p;
            return null;
        }
    }
}
