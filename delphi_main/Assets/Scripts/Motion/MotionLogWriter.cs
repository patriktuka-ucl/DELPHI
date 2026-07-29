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
        [Tooltip("Optional — the rumble cue's own output. Absent, the rumble " +
                 "columns log as zeros rather than the file changing shape, " +
                 "so a run with and without rumble stays directly comparable.")]
        public CarRumbleCues rumble;

        [Header("Logging rate — independent of frame rate")]
        public float logRateHz = 50f;

        private StreamWriter _writer;
        private bool _wasRecording;
        private double _nextTick;
        private double _clockStart;

        private void Awake()
        {
            if (recorder == null) recorder = FindAnyObjectByType<SessionRecorder>();
            if (cues == null) cues = FindAnyObjectByType<CarMotionCues>();
            if (rumble == null) rumble = FindAnyObjectByType<CarRumbleCues>();
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

            // Rumble columns log what the MODEL produced, not what the rig was
            // sent: the two differ whenever rumble is switched off at the
            // connection, and knowing the cue was computed-but-not-delivered is
            // what makes a rumble-off condition analysable as a control rather
            // than just a hole in the data. Whether it was actually delivered
            // is the two mode flags on the next columns.
            var conn = YawVR3Connection.Instance;
            _writer.WriteLine(string.Join(",",
                F(t), F(cues.AccelMs2), F(cues.SpeedGapMs), F(cues.YawRateDegPerSec),
                F(cues.PitchDeg), F(cues.RollDeg), F(cues.YawDeg),
                rumble != null ? rumble.MotorRight : 0,
                rumble != null ? rumble.MotorCentre : 0,
                rumble != null ? rumble.MotorLeft : 0,
                rumble != null ? rumble.Hz : 0,
                conn != null && conn.tiltEnabled ? 1 : 0,
                conn != null && conn.rumbleEnabled ? 1 : 0));
        }

        private void Open()
        {
            string dir = recorder.CurrentSessionPath;
            if (string.IsNullOrEmpty(dir)) return;
            _writer = new StreamWriter(Path.Combine(dir, "motion_log.csv"));
            // Schema changed when the cue moved off g-forces onto the car's own
            // commanded motion — logs written before that are NOT comparable.
            // Extended again when rumble became an independent second cue
            // channel: the seven original columns keep their meaning and
            // position, so tilt analysis across the two schemas still lines up.
            _writer.WriteLine("time_s,accel_ms2,speed_gap_ms,yaw_rate_dps,pitch_deg,roll_deg,yaw_deg," +
                              "rumble_right,rumble_centre,rumble_left,rumble_hz," +
                              "tilt_enabled,rumble_enabled");
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
