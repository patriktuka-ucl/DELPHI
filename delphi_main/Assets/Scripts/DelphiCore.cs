using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

namespace Delphi
{
    /// <summary>
    /// DELPHI's own clock — Stopwatch-backed, monotonic, high-resolution,
    /// and completely unrelated to Unity's frame loop, Time.* or timeScale.
    /// Safe to read from any thread. Every timestamp in the acquisition and
    /// recording pipeline comes from here.
    /// </summary>
    public static class DelphiClock
    {
        private static readonly Stopwatch Sw = Stopwatch.StartNew();
        public static double Now => Sw.Elapsed.TotalSeconds;
    }

    /// <summary>
    /// Thread-safe per-channel mean+variance accumulator for a measurement
    /// window. Created by the trial layer, fed by DelphiCore's sampling
    /// thread while installed as DelphiCore.Accumulator, then snapshotted on
    /// the main thread. Means come from the true samples at the true sample
    /// rate — never from frame-rate polling. Variance uses Welford's
    /// single-pass streaming algorithm (stable under lock-per-sample
    /// updates, no need to buffer raw values) — used for baseline standard
    /// deviation, which the trial layer z-scores each window's delta by.
    /// </summary>
    public sealed class WindowAccumulator
    {
        private readonly object _lock = new object();
        private readonly int[] _n = new int[16];
        private readonly double[] _mean = new double[16];
        private readonly double[] _m2 = new double[16]; // sum of squared deviations from the running mean

        public void Add(Channel ch, float v)
        {
            if (float.IsNaN(v)) return;
            int i = (int)ch;
            lock (_lock)
            {
                _n[i]++;
                double delta = v - _mean[i];
                _mean[i] += delta / _n[i];
                _m2[i] += delta * (v - _mean[i]);
            }
        }

        /// <summary>Mean and sample count for a channel; mean is NaN when no
        /// samples landed in the window.</summary>
        public (float mean, int count) Mean(Channel ch)
        {
            lock (_lock)
            {
                int i = (int)ch;
                return _n[i] > 0 ? ((float)_mean[i], _n[i]) : (float.NaN, 0);
            }
        }

        /// <summary>Sample standard deviation; NaN with fewer than 2 samples
        /// (variance undefined for a single point).</summary>
        public float StdDev(Channel ch)
        {
            lock (_lock)
            {
                int i = (int)ch;
                return _n[i] > 1 ? (float)Math.Sqrt(_m2[i] / (_n[i] - 1)) : float.NaN;
            }
        }
    }

    /// <summary>
    /// The acquisition engine. Plain C# — no MonoBehaviour, no Unity APIs —
    /// running a dedicated background thread with its own DelphiClock
    /// schedule:
    ///
    ///   - ticks each scalar sensor group at its configured rate, calling
    ///     ReadValue() on the sensors (which must therefore be thread-safe
    ///     and Unity-API-free — see Sensortypes.cs),
    ///   - when recording, writes sensors.csv rows itself, on this thread,
    ///     each stamped with the true DelphiClock time it was written.
    ///
    /// Unity is a CLIENT of this engine: DelphiManager (main thread) owns
    /// configuration and forwards rate changes; the dashboard/simulator ask
    /// for the latest values whenever they like via the sensors' Current
    /// latches. Rendering hitches, editor stalls and frame drops cannot
    /// affect sampling cadence or recorded timestamps. The only thing that
    /// CANNOT live here is video capture — WebCamTexture / Camera.Render /
    /// Graphics.Blit are main-thread-only Unity engine APIs.
    /// </summary>
    public class DelphiCore
    {
        public class Group
        {
            public Channel[] channels;
            // Written by the main thread (Inspector tweaks), read by the
            // sampling thread — volatile so changes propagate immediately.
            public volatile float rateHz;
            internal double nextTick;
        }

        // Set by the trial layer for the duration of a measurement window;
        // the sampling thread feeds every fresh sample into it. Null when no
        // window is being measured.
        public volatile WindowAccumulator Accumulator;

        private readonly Group[] _groups;
        private readonly Channel[] _csvColumns;
        private readonly Func<Channel, ScalarSensor> _slot;
        private readonly Func<Channel, bool> _isOn;

        private Thread _thread;
        private volatile bool _running;

        // Ego telemetry latch — published by the main thread (the recorder,
        // once per frame; transforms can't be read off-thread), consumed by
        // the sampling thread when writing csv rows.
        public volatile float EgoS = float.NaN;
        public volatile float EgoSpeedKmh = float.NaN;

        // CSV state — owned by the sampling thread; the lock only guards
        // open/close racing against the write in the loop.
        private readonly object _csvLock = new object();
        private StreamWriter _csv;
        private double _csvT0;
        private double _csvNextTick;
        private double _csvInterval;

        public DelphiCore(Group[] groups, Channel[] csvColumns,
                          Func<Channel, ScalarSensor> slot, Func<Channel, bool> isOn)
        {
            _groups = groups;
            _csvColumns = csvColumns;
            _slot = slot;
            _isOn = isOn;
        }

        // ── Lifecycle ───────────────────────────────────────────────────
        public void Start()
        {
            if (_running) return;
            double now = DelphiClock.Now;
            foreach (var g in _groups) g.nextTick = now;
            _running = true;
            _thread = new Thread(SampleLoop)
            {
                IsBackground = true,
                Name = "DELPHI sampling",
                Priority = System.Threading.ThreadPriority.AboveNormal
            };
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            try { _thread?.Join(500); } catch { }
            _thread = null;
            StopCsv();
        }

        // ── Recording ───────────────────────────────────────────────────
        /// <summary>Begin writing sensors.csv on the sampling thread. `t0`
        /// is the shared DelphiClock zero for the whole session (the video
        /// clocks use the same one, so csv and mp4 times line up).</summary>
        public void StartCsv(string path, double t0)
        {
            float maxRate = 1f;
            foreach (var g in _groups) maxRate = Math.Max(maxRate, g.rateHz);

            lock (_csvLock)
            {
                _csv = new StreamWriter(path);
                var header = new System.Text.StringBuilder("time_s");
                foreach (var ch in _csvColumns) header.Append(',').Append(ch);
                header.Append(",ego_s_m,ego_speed_kmh");
                _csv.WriteLine(header.ToString());
                _csvT0 = t0;
                _csvInterval = 1.0 / maxRate;
                _csvNextTick = DelphiClock.Now;
            }
        }

        public void StopCsv()
        {
            lock (_csvLock)
            {
                if (_csv == null) return;
                _csv.Flush();
                _csv.Close();
                _csv = null;
            }
        }

        // ── Sampling thread ─────────────────────────────────────────────
        private void SampleLoop()
        {
            while (_running)
            {
                double now = DelphiClock.Now;
                double next = double.MaxValue;

                foreach (var g in _groups)
                {
                    if (now >= g.nextTick)
                    {
                        SampleGroup(g);
                        g.nextTick = now + 1.0 / Math.Max(1f, g.rateHz);
                    }
                    if (g.nextTick < next) next = g.nextTick;
                }

                lock (_csvLock)
                {
                    if (_csv != null)
                    {
                        if (now >= _csvNextTick)
                        {
                            WriteCsvRow(now);
                            _csvNextTick = now + _csvInterval;
                        }
                        if (_csvNextTick < next) next = _csvNextTick;
                    }
                }

                // Sleep until the earliest upcoming tick. Thread.Sleep has
                // ~1ms granularity, so sleep short and let the loop re-check
                // rather than oversleeping past a tick.
                double wait = next - DelphiClock.Now;
                if (wait > 0.003)      Thread.Sleep((int)((wait - 0.0015) * 1000));
                else if (wait > 0)     Thread.Sleep(0); // yield, re-check immediately
            }
        }

        private void SampleGroup(Group g)
        {
            var acc = Accumulator;
            foreach (var ch in g.channels)
            {
                if (!_isOn(ch)) continue;
                var s = _slot(ch);
                if (s is null) continue; // plain reference check — Unity's overloaded == is main-thread territory
                try
                {
                    float v = s.ReadValue();
                    acc?.Add(ch, v);
                }
                catch (Exception e)
                {
                    // One misbehaving sensor must never kill the whole
                    // acquisition thread. Debug.Log* is thread-safe.
                    UnityEngine.Debug.LogError($"[DelphiCore] {ch} sensor threw: {e.Message}");
                }
            }
        }

        // Called on the sampling thread, immediately after the freshest
        // group ticks — values are read straight from the sensors' Current
        // latches, timestamped with the true write time.
        private void WriteCsvRow(double now)
        {
            var sb = new System.Text.StringBuilder(160);
            sb.Append((now - _csvT0).ToString("F4", CultureInfo.InvariantCulture));

            foreach (var ch in _csvColumns)
            {
                sb.Append(',');
                var s = _isOn(ch) ? _slot(ch) : null;
                float v = s is null ? float.NaN : s.Current;
                sb.Append(float.IsNaN(v) ? "NaN" : v.ToString("F4", CultureInfo.InvariantCulture));
            }

            float egoS = EgoS, egoV = EgoSpeedKmh;
            sb.Append(',').Append(float.IsNaN(egoS) ? "NaN" : egoS.ToString("F2", CultureInfo.InvariantCulture));
            sb.Append(',').Append(float.IsNaN(egoV) ? "NaN" : egoV.ToString("F2", CultureInfo.InvariantCulture));

            _csv.WriteLine(sb.ToString());
        }
    }
}
