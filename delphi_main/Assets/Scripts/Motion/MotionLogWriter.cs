using System.Globalization;
using System.IO;
using UnityEngine;

namespace Delphi.Motion
{
    /// <summary>
    /// Logs CarMotionCues' output to its own motion_log.csv, separate from
    /// sensors.csv — mirrors SessionController's _trialLog in shape (a plain
    /// StreamWriter dropped into recorder.CurrentSessionPath) but ticks at
    /// its own configurable rate against DelphiClock rather than once per
    /// Unity frame, same reasoning as every other DELPHI log: rate is
    /// explicit and decoupled from frame rate. Opens/closes itself by
    /// watching SessionRecorder.IsRecording, so nothing else needs to know
    /// this exists.
    /// </summary>
    [DefaultExecutionOrder(150)] // after CarMotionCues (100) has computed this frame's values
    public class MotionLogWriter : MonoBehaviour
    {
        [Header("Links (auto-found if left empty)")]
        public SessionRecorder recorder;
        public CarMotionCues cues;

        [Header("Logging rate — independent of frame rate")]
        public float logRateHz = 50f;

        private StreamWriter _writer;
        private bool _wasRecording;
        private double _nextTick;
        private double _clockStart;

        private void Awake()
        {
            if (recorder == null) recorder = FindFirstObjectByType<SessionRecorder>();
            if (cues == null) cues = FindFirstObjectByType<CarMotionCues>();
        }

        private void Update()
        {
            if (recorder == null || cues == null) return;

            if (recorder.IsRecording && !_wasRecording) Open();
            else if (!recorder.IsRecording && _wasRecording) Close();
            _wasRecording = recorder.IsRecording;

            if (_writer == null) return;

            double t = DelphiClock.Now - _clockStart;
            if (t < _nextTick) return;
            _nextTick += 1.0 / Mathf.Max(1f, logRateHz);

            _writer.WriteLine(string.Join(",",
                F(t), F(cues.SurgeG), F(cues.LateralG), F(cues.PitchDeg), F(cues.RollDeg)));
        }

        private void Open()
        {
            string dir = recorder.CurrentSessionPath;
            if (string.IsNullOrEmpty(dir)) return;
            _writer = new StreamWriter(Path.Combine(dir, "motion_log.csv"));
            _writer.WriteLine("time_s,surge_g,lateral_g,pitch_deg,roll_deg");
            _clockStart = DelphiClock.Now;
            _nextTick = 0;
        }

        private void Close()
        {
            _writer?.Flush();
            _writer?.Close();
            _writer = null;
        }

        private static string F(double v) => v.ToString("F4", CultureInfo.InvariantCulture);

        private void OnDestroy() => Close();
        private void OnApplicationQuit() => Close();
    }
}
