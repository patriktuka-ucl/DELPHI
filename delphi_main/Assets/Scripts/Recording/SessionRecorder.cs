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
    /// sensors.csv. The csv is written by DelphiCore ON ITS OWN SAMPLING
    /// THREAD, with DelphiClock timestamps — this component merely tells the
    /// core when to start/stop and where. Video capture stays here on the
    /// main thread (camera/texture APIs are main-thread-only Unity), but
    /// ticks on the same DelphiClock time base with per-feed clocks, so csv
    /// and mp4 times share one zero. Playback syncs by time, not row index.
    /// A meta.json manifest ties it together.
    ///
    /// Session folder: {persistentDataPath}/Sessions/yyyyMMdd_HHmmss/
    ///
    /// Video path per feed: source texture → Blit into a fixed-size RT →
    /// AsyncGPUReadback (processed strictly in submission order) → byte[] →
    /// FfmpegVideoWriter's background thread → mp4. Nothing blocks the main
    /// thread except the final drain in StopRecording.
    ///
    /// If the sim ever runs slower than a feed's rate, the latest frame is
    /// written multiple times so wall-clock sync is preserved instead of
    /// the video silently shortening.
    ///
    /// This has NO rate fields of its own: DelphiManager is the single
    /// owner of every rate in DELPHI. Each feed's mp4 is captured and
    /// encoded at DelphiManager.FrameRate(channel), snapshotted at record
    /// start.
    /// </summary>
    public class SessionRecorder : MonoBehaviour
    {
        [Header("Links (auto-found if left empty)")]
        public DelphiManager manager;
        [Tooltip("Optional — when present, ego telemetry (s, speed) is " +
                 "logged alongside the sensors.")]
        public CarDriver egoCar;

        [Header("Capture")]
        [Tooltip("Feeds wider than this are scaled down (aspect kept). All " +
                 "capture RATES are configured on DelphiManager, not here.")]
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
        public float ElapsedSeconds => IsRecording ? (float)(DelphiClock.Now - _clockStart) : 0f;
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
            public int fps;            // DelphiManager.FrameRate at record start
            public double nextTick;    // this feed's own capture clock (session-relative, DelphiClock base)
            public Texture2D syncReadbackTex; // only on hardware without async readback
        }

        private readonly List<Feed> _feeds = new();
        private string _sessionPath;
        private double _clockStart;    // DelphiClock.Now at REC — shared zero for csv AND video
        private bool _readbackSupported;
        private float _csvRate;        // nominal (MaxScalarRateHz at start) — recorded in meta.json

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
        /// <summary>Starts a new session. `sessionName`, if given, becomes the
        /// session's folder name (sanitized; a timestamp is appended instead
        /// of clobbering if that name is already taken) — leave it null/empty
        /// for the old auto-timestamp behaviour.</summary>
        public bool StartRecording(string sessionName = null)
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
            if (manager.Core == null)
            {
                Debug.LogError("[SessionRecorder] DelphiManager's core isn't running.");
                return false;
            }

            string ffmpeg = ResolveFfmpeg();
            if (ffmpeg == null)
            {
                Debug.LogError("[SessionRecorder] ffmpeg not found — install it (brew " +
                               "install ffmpeg) or set ffmpegPath explicitly.");
                return false;
            }

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string folderName = string.IsNullOrWhiteSpace(sessionName)
                ? timestamp
                : SanitizeFolderName(sessionName);
            _sessionPath = Path.Combine(SessionsRoot, folderName);
            if (Directory.Exists(_sessionPath))
                _sessionPath = Path.Combine(SessionsRoot, $"{folderName}_{timestamp}");
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

                int feedFps = Mathf.Max(1, Mathf.RoundToInt(manager.FrameRate(ch)));
                var feed = new Feed
                {
                    channel = ch,
                    width = w,
                    height = h,
                    fps = feedFps,
                    nextTick = 0f,
                    rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32),
                    writer = new FfmpegVideoWriter(ffmpeg,
                        Path.Combine(_sessionPath, ch + ".mp4"),
                        w, h, feedFps, flipVideoVertically)
                };
                feed.rt.Create();
                _feeds.Add(feed);
            }

            // Shared session zero for BOTH the core's csv thread and the
            // video clocks below — one time base, one origin.
            _clockStart = DelphiClock.Now;
            _csvRate = manager.MaxScalarRateHz;

            // The csv is written by DelphiCore on its sampling thread.
            manager.Core.EgoS = float.NaN;
            manager.Core.EgoSpeedKmh = float.NaN;
            manager.Core.StartCsv(Path.Combine(_sessionPath, "sensors.csv"), _clockStart);

            IsRecording = true;
            Debug.Log($"[SessionRecorder] Recording {_feeds.Count} video feed(s) (rates from " +
                      $"DelphiManager) + sensor log at {_csvRate:0.#} Hz on the core's thread " +
                      $"→ {_sessionPath}");
            return true;
        }

        public void StopRecording()
        {
            if (!IsRecording) return;
            IsRecording = false;
            float duration = (float)(DelphiClock.Now - _clockStart);

            // Stop the core's csv thread writing first.
            if (manager != null && manager.Core != null) manager.Core.StopCsv();

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

            // Top-level fps = fastest feed (playback's frame-step size);
            // each feed also records its own rate.
            int maxFeedFps = Mathf.Max(1, Mathf.RoundToInt(_csvRate));
            foreach (var f in _feeds) maxFeedFps = Mathf.Max(maxFeedFps, f.fps);

            var meta = new SessionMeta
            {
                started = DateTime.Now.AddSeconds(-duration).ToString("o"),
                fps = maxFeedFps,
                csvRateHz = _csvRate,
                duration = duration,
                scalarChannels = Array.ConvertAll(DelphiManager.AllChannels, c => c.ToString()),
                feeds = _feeds.ConvertAll(f => new SessionFeedMeta
                {
                    channel = f.channel.ToString(),
                    file = f.channel + ".mp4",
                    width = f.width,
                    height = f.height,
                    fps = f.fps
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

        // ── Capture loop (video only — csv lives on the core's thread) ──
        // LateUpdate so DelphiManager.Update has already ticked the frame
        // feeds this frame. Also publishes ego telemetry into the core's
        // latch here, since transforms can only be read on the main thread.
        private void LateUpdate()
        {
            if (!IsRecording) return;

            if (egoCar != null && manager.Core != null)
            {
                manager.Core.EgoS = egoCar.S;
                manager.Core.EgoSpeedKmh = egoCar.CurrentSpeedKmh;
            }

            foreach (var feed in _feeds) ProcessReadbacks(feed, blocking: false);

            double t = DelphiClock.Now - _clockStart;

            // Per-feed video clocks — each feed captures at its own
            // DelphiManager rate. Capture once per frame, repeat `due` times
            // if the sim ran slower than the feed's rate.
            foreach (var feed in _feeds)
            {
                int due = 0;
                while (feed.nextTick <= t)
                {
                    due++;
                    feed.nextTick += 1.0 / feed.fps;
                }
                if (due == 0) continue;

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

        private static string SanitizeFolderName(string name)
        {
            name = name.Trim();
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
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
