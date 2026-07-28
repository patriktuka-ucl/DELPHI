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
        [Tooltip("Empty = auto-detect ffmpeg in the usual install locations, " +
                 "then on PATH.")]
        public string ffmpegPath = "";
        [Tooltip("GPU readbacks arrive top-down on Metal/DX, so frames need " +
                 "a vertical flip before encoding. Untick only if recordings " +
                 "come out upside-down.")]
        public bool flipVideoVertically = true;

        /// <summary>Which feeds get written to disk, one flag per FrameChannel.
        ///
        /// SEPARATE FROM DelphiManager'S CHANNEL TOGGLES ON PURPOSE. Those
        /// decide whether a feed is CAPTURED at all — turning one off blinds
        /// the dashboard too. These decide only whether a captured feed is
        /// RECORDED, so the researcher can watch a camera live while keeping it
        /// out of the session's files, and can drop an expensive feed from disk
        /// without losing sight of it.
        ///
        /// Sized off AllFrameChannels rather than a fixed count, so adding a
        /// channel cannot silently leave it unrepresented — see
        /// EnsureRecordFlags.</summary>
        // Defaults to ALL ON. `new bool[n]` would be all-false, i.e. a recorder
        // that silently writes nothing — the worst possible default for the
        // component whose entire job is not losing data.
        [SerializeField]
        private bool[] recordFeed = AllOn();

        private static bool[] AllOn()
        {
            var a = new bool[DelphiManager.AllFrameChannels.Length];
            for (int i = 0; i < a.Length; i++) a[i] = true;
            return a;
        }

        /// <summary>Grows the flag array when a FrameChannel is added, defaulting
        /// new channels to ON so a new feed is never silently unrecorded.</summary>
        private void EnsureRecordFlags()
        {
            int n = DelphiManager.AllFrameChannels.Length;
            if (recordFeed != null && recordFeed.Length == n) return;

            var grown = new bool[n];
            for (int i = 0; i < n; i++)
                grown[i] = recordFeed == null || i >= recordFeed.Length || recordFeed[i];
            recordFeed = grown;
        }

        /// <summary>Whether this channel is set to be written to disk.</summary>
        public bool IsFeedRecorded(FrameChannel ch)
        {
            EnsureRecordFlags();
            int i = System.Array.IndexOf(DelphiManager.AllFrameChannels, ch);
            return i >= 0 && i < recordFeed.Length && recordFeed[i];
        }

        public void SetFeedRecorded(FrameChannel ch, bool on)
        {
            EnsureRecordFlags();
            int i = System.Array.IndexOf(DelphiManager.AllFrameChannels, ch);
            if (i >= 0) recordFeed[i] = on;
        }

        private void OnValidate() => EnsureRecordFlags();

        public bool IsRecording { get; private set; }
        public float ElapsedSeconds => IsRecording ? (float)(DelphiClock.Now - _clockStart) : 0f;
        public string LastSessionPath { get; private set; }
        /// <summary>Folder of the session being written right now (null when
        /// idle) — lets the trial layer drop its own logs alongside.</summary>
        public string CurrentSessionPath => IsRecording ? _sessionPath : null;

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
            // macOS/Linux
            "/opt/homebrew/bin/ffmpeg", "/usr/local/bin/ffmpeg", "/usr/bin/ffmpeg",
            // Windows: winget/scoop/chocolatey defaults
            @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
            @"C:\ffmpeg\bin\ffmpeg.exe",
            @"C:\ProgramData\chocolatey\bin\ffmpeg.exe"
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
                Debug.LogError("[SessionRecorder] ffmpeg not found on PATH or in the " +
                               "usual install locations — install it (Windows: winget " +
                               "install Gyan.FFmpeg; macOS: brew install ffmpeg) or set " +
                               "ffmpegPath explicitly.");
                return false;
            }

            string root = ResolveWritableSessionsRoot();
            if (root == null)
            {
                Debug.LogError($"[SessionRecorder] Can't write to the sessions folder " +
                               $"('{SessionsRoot}') or to the fallback ('{DefaultSessionsRoot}'). " +
                               "Set a writable sessionsFolder on the SessionRecorder.");
                return false;
            }

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string folderName = string.IsNullOrWhiteSpace(sessionName)
                ? timestamp
                : SanitizeFolderName(sessionName);
            _sessionPath = Path.Combine(root, folderName);
            if (Directory.Exists(_sessionPath))
                _sessionPath = Path.Combine(root, $"{folderName}_{timestamp}");

            try
            {
                Directory.CreateDirectory(_sessionPath);
            }
            catch (Exception e)
            {
                Debug.LogError($"[SessionRecorder] Could not create '{_sessionPath}': {e.Message}");
                return false;
            }

            // One feed per frame channel that is actually delivering pixels.
            _feeds.Clear();
            EnsureRecordFlags();
            foreach (var ch in DelphiManager.AllFrameChannels)
            {
                // Deselected feeds cost nothing here: no RenderTexture, no
                // readback, no ffmpeg process. This is the toggle that
                // actually removes the per-feed recording load.
                if (!IsFeedRecorded(ch)) continue;

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

        /// <summary>The configured sessions folder if it's actually usable on
        /// THIS machine, otherwise the platform default. A sessionsFolder saved
        /// on another OS doesn't fail loudly — an absolute POSIX path like
        /// "/Users/someone/Desktop" gets silently re-rooted onto the current
        /// drive on Windows ("C:\Users\someone\Desktop") and only blows up at
        /// CreateDirectory. That used to throw partway through starting a
        /// condition, which loses the participant's run; recording into the
        /// default folder with a warning is always the better trade.</summary>
        private string ResolveWritableSessionsRoot()
        {
            string configured = SessionsRoot;
            if (TryEnsureDirectory(configured)) return configured;

            string fallback = DefaultSessionsRoot;
            Debug.LogWarning($"[SessionRecorder] sessionsFolder '{sessionsFolder}' isn't writable on " +
                             $"this machine (a path saved on another OS?) — recording to '{fallback}' " +
                             "instead. Set a valid sessionsFolder on the SessionRecorder to silence this.");
            return TryEnsureDirectory(fallback) ? fallback : null;
        }

        private static bool TryEnsureDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                Directory.CreateDirectory(path);
                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException
                                   || e is ArgumentException || e is NotSupportedException)
            {
                return false;
            }
        }

        private string ResolveFfmpeg()
        {
            if (!string.IsNullOrEmpty(ffmpegPath))
                return File.Exists(ffmpegPath) ? ffmpegPath : null;
            foreach (var p in FfmpegCandidates)
                if (File.Exists(p)) return p;

            // Scoop/winget shims and any manual install land on PATH rather
            // than a fixed prefix, so fall back to walking it.
            string exe = Application.platform == RuntimePlatform.WindowsEditor
                      || Application.platform == RuntimePlatform.WindowsPlayer
                       ? "ffmpeg.exe" : "ffmpeg";
            string pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathVar.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                string candidate;
                try { candidate = Path.Combine(dir.Trim(), exe); }
                catch (ArgumentException) { continue; } // malformed PATH entry
                if (File.Exists(candidate)) return candidate;
            }

            return ResolveWingetFfmpeg(exe);
        }

        /// <summary>winget installs ffmpeg under a VERSION-STAMPED folder and
        /// only puts it on the user PATH — which an already-running Editor
        /// won't have inherited. Search the package tree so a fresh install
        /// works without restarting Unity, and survives version bumps.</summary>
        private static string ResolveWingetFfmpeg(string exe)
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(localAppData)) return null;

            string packages = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
            if (!Directory.Exists(packages)) return null;

            try
            {
                // Only descend into ffmpeg packages — the full tree can be large.
                foreach (var pkgDir in Directory.GetDirectories(packages, "*FFmpeg*"))
                {
                    var hits = Directory.GetFiles(pkgDir, exe, SearchOption.AllDirectories);
                    if (hits.Length > 0)
                    {
                        Array.Sort(hits, StringComparer.OrdinalIgnoreCase);
                        return hits[hits.Length - 1]; // highest version folder
                    }
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                // Unreadable package dir — treat as "not found".
            }
            return null;
        }
    }
}
