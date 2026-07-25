using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.Video;

namespace Delphi
{
    /// <summary>
    /// Replays a recorded session THROUGH the live dashboard: on Load it
    /// registers itself as DelphiManager.Playback, so every dashboard cell
    /// transparently shows recorded data instead of live sensors (Unload
    /// hands control back).
    ///
    /// Owns the playback clock. Videos (one VideoPlayer → RenderTexture per
    /// feed) chase the clock and are re-synced whenever they drift; scalar
    /// values come from sensors.csv by hold-last-sample lookup at the clock
    /// time. Supports play/pause, seek, frame-step (1/fps) and speed
    /// multipliers — the whole transport the researcher UI exposes.
    /// </summary>
    public class SessionPlayer : MonoBehaviour
    {
        [Header("Links (auto-found if left empty)")]
        public DelphiManager manager;

        [Tooltip("Max seconds a video may drift from the master clock " +
                 "before being snapped back.")]
        public float maxVideoDrift = 0.15f;

        public bool IsLoaded { get; private set; }
        public bool IsPlaying { get; private set; }
        public float TimeSec { get; private set; }
        public float Duration { get; private set; }
        public float PlaybackSpeed { get; private set; } = 1f;
        public string LoadedPath { get; private set; }
        public SessionMeta Meta { get; private set; }

        // ── Internals ───────────────────────────────────────────────────
        private class FeedPlayback
        {
            public FrameChannel channel;
            public VideoPlayer vp;
            public RenderTexture rt;
        }

        private float[] _times;
        private readonly Dictionary<Channel, float[]> _values = new();
        private readonly List<FeedPlayback> _feeds = new();
        private float _driftCheckTimer;

        private void Awake()
        {
            if (manager == null) manager = FindFirstObjectByType<DelphiManager>();
        }

        // ── Sessions on disk ────────────────────────────────────────────
        /// <summary>Session folders under root (those with a meta.json),
        /// oldest → newest.</summary>
        public static string[] ListSessions(string root)
        {
            if (!Directory.Exists(root)) return Array.Empty<string>();
            var dirs = new List<string>();
            foreach (var d in Directory.GetDirectories(root))
                if (File.Exists(Path.Combine(d, "meta.json")))
                    dirs.Add(d);
            dirs.Sort(StringComparer.Ordinal);
            return dirs.ToArray();
        }

        // ── Load / unload ───────────────────────────────────────────────
        public bool Load(string sessionPath)
        {
            if (IsLoaded) Unload();
            if (manager == null)
            {
                Debug.LogError("[SessionPlayer] No DelphiManager to play back into.");
                return false;
            }

            string metaPath = Path.Combine(sessionPath, "meta.json");
            if (!File.Exists(metaPath))
            {
                Debug.LogError($"[SessionPlayer] Not a session folder (no meta.json): {sessionPath}");
                return false;
            }

            try
            {
                Meta = JsonUtility.FromJson<SessionMeta>(File.ReadAllText(metaPath));
                ParseCsv(Path.Combine(sessionPath, "sensors.csv"));
            }
            catch (Exception e)
            {
                Debug.LogError($"[SessionPlayer] Failed to load {sessionPath}: {e.Message}");
                return false;
            }

            foreach (var fm in Meta.feeds)
            {
                if (!Enum.TryParse(fm.channel, out FrameChannel ch)) continue;
                string file = Path.Combine(sessionPath, fm.file);
                if (!File.Exists(file)) continue;

                var go = new GameObject($"[playback] {fm.channel}");
                go.transform.SetParent(transform, false);
                var vp = go.AddComponent<VideoPlayer>();
                var rt = new RenderTexture(fm.width, fm.height, 0, RenderTextureFormat.ARGB32);
                rt.Create();

                vp.playOnAwake      = false;
                vp.isLooping        = false;
                vp.renderMode       = VideoRenderMode.RenderTexture;
                vp.targetTexture    = rt;
                vp.audioOutputMode  = VideoAudioOutputMode.None;
                vp.source           = VideoSource.Url;
                vp.url              = file; // plain absolute path — avoids URL-escaping issues (persistentDataPath contains spaces on macOS)
                vp.playbackSpeed    = PlaybackSpeed;
                vp.skipOnDrop       = true;
                vp.Prepare();

                _feeds.Add(new FeedPlayback { channel = ch, vp = vp, rt = rt });
            }

            Duration   = Meta.duration;
            TimeSec    = 0f;
            IsPlaying  = false;
            IsLoaded   = true;
            LoadedPath = sessionPath;
            manager.Playback = this;
            Debug.Log($"[SessionPlayer] Loaded {Path.GetFileName(sessionPath)} — " +
                      $"{Duration:F1}s, {_feeds.Count} video feed(s), " +
                      $"{_values.Count} scalar channel(s).");
            return true;
        }

        public void Unload()
        {
            if (!IsLoaded) return;
            IsPlaying = false;
            foreach (var f in _feeds)
            {
                f.vp.Stop();
                Destroy(f.vp.gameObject);
                f.rt.Release();
                Destroy(f.rt);
            }
            _feeds.Clear();
            _values.Clear();
            _times = null;
            IsLoaded = false;
            LoadedPath = null;
            if (manager != null && ReferenceEquals(manager.Playback, this))
                manager.Playback = null;
        }

        private void OnDestroy() => Unload();

        // ── Transport ───────────────────────────────────────────────────
        public void Play()
        {
            if (!IsLoaded) return;
            if (TimeSec >= Duration) Seek(0f); // replay from the top
            foreach (var f in _feeds) f.vp.Play();
            IsPlaying = true;
        }

        public void Pause()
        {
            if (!IsLoaded) return;
            foreach (var f in _feeds) f.vp.Pause();
            IsPlaying = false;
        }

        public void TogglePlay() { if (IsPlaying) Pause(); else Play(); }

        public void Seek(float t)
        {
            if (!IsLoaded) return;
            TimeSec = Mathf.Clamp(t, 0f, Duration);
            foreach (var f in _feeds)
                if (f.vp.isPrepared) f.vp.time = TimeSec;
        }

        /// <summary>Step by whole capture frames (1/fps). Pauses first.</summary>
        public void StepFrames(int n)
        {
            if (!IsLoaded || Meta.fps <= 0) return;
            Pause();
            Seek(TimeSec + n / (float)Meta.fps);
        }

        /// <summary>Coarse skip by a fixed number of seconds (not frame-
        /// accurate) — for quickly jumping around a long recording. Pauses
        /// first, same as StepFrames.</summary>
        public void StepSeconds(float seconds)
        {
            if (!IsLoaded) return;
            Pause();
            Seek(TimeSec + seconds);
        }

        public void SetSpeed(float speed)
        {
            PlaybackSpeed = Mathf.Clamp(speed, 0.1f, 8f);
            foreach (var f in _feeds) f.vp.playbackSpeed = PlaybackSpeed;
        }

        private void Update()
        {
            if (!IsLoaded || !IsPlaying) return;

            TimeSec += Time.deltaTime * PlaybackSpeed;
            if (TimeSec >= Duration)
            {
                TimeSec = Duration;
                Pause();
                return;
            }

            // Videos chase the master clock; snap back any that drift.
            _driftCheckTimer += Time.deltaTime;
            if (_driftCheckTimer >= 0.5f)
            {
                _driftCheckTimer = 0f;
                foreach (var f in _feeds)
                    if (f.vp.isPrepared && Mathf.Abs((float)f.vp.time - TimeSec) > maxVideoDrift)
                        f.vp.time = TimeSec;
            }
        }

        // ── Data access (routed through DelphiManager) ──────────────────
        /// <summary>Whether the loaded recording contains this channel AT ALL —
        /// stable across the whole recording, unlike <see cref="HasData"/>,
        /// which is per-instant. Used to decide which dashboard cells to show
        /// during playback (so a momentarily-NaN sample doesn't flicker a cell
        /// out of the grid).</summary>
        public bool HasChannel(Channel ch) => IsLoaded && _values.ContainsKey(ch);

        public bool HasData(Channel ch) =>
            IsLoaded && _values.ContainsKey(ch) && !float.IsNaN(GetValue(ch));

        public float GetValue(Channel ch)
        {
            if (!IsLoaded || !_values.TryGetValue(ch, out var vals) ||
                _times == null || _times.Length == 0)
                return float.NaN;
            return vals[SampleIndexAt(TimeSec)];
        }

        /// <summary>Fills `outBuffer` with `outBuffer.Length` samples of `ch`,
        /// evenly spaced across the `windowSeconds` ending AT the current
        /// playback time (hold-last-sample lookup per point, same rule as
        /// GetValue) — a real historical window, not a live-style scrolling
        /// buffer. Points before recording start (t &lt; 0) are NaN, so a
        /// window near the beginning of the session correctly shows a
        /// partially-empty graph rather than fabricated data. Used by the
        /// dashboard so pausing/scrubbing shows the graph AS OF that moment
        /// instead of continuing to shift as if time were still advancing.</summary>
        public void FillHistory(Channel ch, float[] outBuffer, float windowSeconds)
        {
            int n = outBuffer.Length;
            if (n == 0) return;
            if (!IsLoaded || !_values.TryGetValue(ch, out var vals) || _times == null || _times.Length == 0)
            {
                for (int i = 0; i < n; i++) outBuffer[i] = float.NaN;
                return;
            }

            float windowStart = TimeSec - windowSeconds;
            for (int i = 0; i < n; i++)
            {
                // Rightmost sample (i == n-1) lands exactly on TimeSec.
                float t = n == 1 ? TimeSec : windowStart + windowSeconds * i / (n - 1);
                outBuffer[i] = t < 0f ? float.NaN : vals[SampleIndexAt(t)];
            }
        }

        public bool HasFrame(FrameChannel ch) => IsLoaded && FindFeed(ch) != null;

        public Texture GetFrame(FrameChannel ch) => FindFeed(ch)?.rt;

        private FeedPlayback FindFeed(FrameChannel ch)
        {
            foreach (var f in _feeds) if (f.channel == ch) return f;
            return null;
        }

        /// <summary>Hold-last-sample: index of the last csv row at or before t.</summary>
        private int SampleIndexAt(float t)
        {
            int lo = 0, hi = _times.Length - 1;
            if (t <= _times[0]) return 0;
            if (t >= _times[hi]) return hi;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (_times[mid] <= t) lo = mid; else hi = mid;
            }
            return lo;
        }

        // ── CSV ─────────────────────────────────────────────────────────
        private void ParseCsv(string path)
        {
            _values.Clear();
            var lines = File.ReadAllLines(path);
            if (lines.Length < 2)
            {
                _times = Array.Empty<float>();
                return;
            }

            string[] header = lines[0].Split(',');
            int rows = lines.Length - 1;
            _times = new float[rows];

            // Column → Channel mapping straight off the header; non-channel
            // columns (time_s, ego telemetry) are simply not mapped.
            var columns = new List<(int col, float[] vals)>();
            for (int c = 1; c < header.Length; c++)
            {
                if (!Enum.TryParse(header[c], out Channel ch)) continue;
                var vals = new float[rows];
                _values[ch] = vals;
                columns.Add((c, vals));
            }

            for (int r = 0; r < rows; r++)
            {
                string[] cells = lines[r + 1].Split(',');
                _times[r] = float.Parse(cells[0], CultureInfo.InvariantCulture);
                foreach (var (col, vals) in columns)
                    vals[r] = col < cells.Length
                        ? float.Parse(cells[col], CultureInfo.InvariantCulture)
                        : float.NaN;
            }
        }
    }
}
