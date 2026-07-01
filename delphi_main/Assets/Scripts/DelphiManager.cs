using UnityEngine;

namespace Delphi
{
    /// <summary>
    /// Every possible SCALAR input signal. The dashboard always lists all of these.
    /// </summary>
    public enum Channel
    {
        HeartRate,
        RMSSD,          // HRV
        RespRate,
        GSR,
        BlinkRate,
        Gaze,
        PupilDiameter,
        EEG,
        Facial
    }

    /// <summary>
    /// Every possible FRAME (video/texture) input. Separate from Channel
    /// because these need a Texture accessor, not a float — see FrameSensor.
    /// Just one slot for now (a generic camera feed); add more here later
    /// (e.g. FaceCamera, ScreenCapture) as real sources come online.
    /// </summary>
    public enum FrameChannel
    {
        Camera
    }

    /// <summary>
    /// The patch bay. Each input has a slot — drag any ScalarSensor (or, for
    /// video, any FrameSensor) into it to connect that input. Empty slot = no
    /// data. The manager only polls whatever is plugged in; it doesn't
    /// generate anything itself.
    /// </summary>
    public class DelphiManager : MonoBehaviour
    {
        [Header("Gold-standard inputs")]
        [SerializeField] private ScalarSensor heartRate;
        [SerializeField] private ScalarSensor hrvRmssd;
        [SerializeField] private ScalarSensor respRate;
        [SerializeField] private ScalarSensor gsr;

        [Header("Good additions")]
        [SerializeField] private ScalarSensor blinkRate;
        [SerializeField] private ScalarSensor gaze;
        [SerializeField] private ScalarSensor pupilDiameter;

        [Header("Experimental")]
        [SerializeField] private ScalarSensor eeg;
        [SerializeField] private ScalarSensor facial;

        [Header("Video / frame inputs")]
        [SerializeField] private FrameSensor camera;

        // Canonical display order for the dashboard.
        public static readonly Channel[] AllChannels =
        {
            Channel.HeartRate, Channel.RMSSD, Channel.RespRate, Channel.GSR,
            Channel.BlinkRate, Channel.Gaze, Channel.PupilDiameter,
            Channel.EEG, Channel.Facial
        };

        public static readonly FrameChannel[] AllFrameChannels =
        {
            FrameChannel.Camera
        };

        // ── Public API — scalar channels ────────────────────────────────
        public bool HasData(Channel ch) => Slot(ch) != null && !float.IsNaN(Slot(ch).Current);

        public float GetValue(Channel ch)
        {
            var s = Slot(ch);
            return s != null ? s.Current : float.NaN;
        }

        public static (string label, string unit) Meta(Channel ch) => ch switch
        {
            Channel.HeartRate     => ("HR",                    "bpm"),
            Channel.RMSSD         => ("HRV-RMSSD",             "ms"),
            Channel.RespRate      => ("Resp. rate",            "br/m"),
            Channel.GSR           => ("GSR",                   "raw10bit"),
            Channel.BlinkRate     => ("Blink rate",            "bl/m"),
            Channel.Gaze          => ("Gaze / Saccade rate",   ""),
            Channel.PupilDiameter => ("Pupil diameter",        "mm"),
            Channel.EEG           => ("EEG",                   "µV"),
            Channel.Facial        => ("Facial affect",         ""),
            _                     => (ch.ToString(),           "")
        };

        // ── Public API — frame channels ─────────────────────────────────
        public bool HasFrame(FrameChannel ch) => FrameSlot(ch) != null;

        public Texture GetFrame(FrameChannel ch)
        {
            var s = FrameSlot(ch);
            return s != null ? s.CurrentFrame : null;
        }

        public static (string label, string unit) FrameMeta(FrameChannel ch) => ch switch
        {
            FrameChannel.Camera => ("Camera", ""),
            _                   => (ch.ToString(), "")
        };

        // ── Sampling ───────────────────────────────────────────────────
        private void Update()
        {
            foreach (var ch in AllChannels)
            {
                var s = Slot(ch);
                if (s != null) s.ReadValue();
            }

            foreach (var fc in AllFrameChannels)
            {
                var s = FrameSlot(fc);
                if (s != null) s.ReadFrame();
            }
        }

        // Map a channel to its serialized slot.
        private ScalarSensor Slot(Channel ch) => ch switch
        {
            Channel.HeartRate     => heartRate,
            Channel.RMSSD         => hrvRmssd,
            Channel.RespRate      => respRate,
            Channel.GSR           => gsr,
            Channel.BlinkRate     => blinkRate,
            Channel.Gaze          => gaze,
            Channel.PupilDiameter => pupilDiameter,
            Channel.EEG           => eeg,
            Channel.Facial        => facial,
            _                     => null
        };

        private FrameSensor FrameSlot(FrameChannel ch) => ch switch
        {
            FrameChannel.Camera => camera,
            _                   => null
        };
    }
}