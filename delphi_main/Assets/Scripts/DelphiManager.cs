using UnityEngine;

namespace Delphi
{
    /// <summary>
    /// Every possible input signal. The dashboard always lists all of these.
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
    /// The patch bay. Each input has a slot — drag any ScalarSensor (MockSensor,
    /// GSRSensorSerial, future real drivers) into it to connect that input.
    /// Empty slot = no data. The manager only polls whatever is plugged in; it
    /// doesn't generate anything itself.
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

        // Canonical display order for the dashboard.
        public static readonly Channel[] AllChannels =
        {
            Channel.HeartRate, Channel.RMSSD, Channel.RespRate, Channel.GSR,
            Channel.BlinkRate, Channel.Gaze, Channel.PupilDiameter,
            Channel.EEG, Channel.Facial
        };

        // ── Public API for the dashboard ───────────────────────────────
        public bool HasData(Channel ch) => Slot(ch) != null && !float.IsNaN(Slot(ch).Current);

        public float GetValue(Channel ch)
        {
            var s = Slot(ch);
            return s != null ? s.Current : float.NaN;
        }

        public static (string label, string unit) Meta(Channel ch) => ch switch
        {
            Channel.HeartRate     => ("HR",             "bpm"),
            Channel.RMSSD         => ("HRV (RMSSD)",    "ms"),
            Channel.RespRate      => ("Resp rate",      "br/m"),
            Channel.GSR           => ("GSR",            "raw"),
            Channel.BlinkRate     => ("Blink rate",     "bl/m"),
            Channel.Gaze          => ("Gaze (x)",       ""),
            Channel.PupilDiameter => ("Pupil diameter", "mm"),
            Channel.EEG           => ("EEG",            "µV"),
            Channel.Facial        => ("Facial affect",  ""),
            _                     => (ch.ToString(),    "")
        };

        // ── Sampling ───────────────────────────────────────────────────
        private void Update()
        {
            foreach (var ch in AllChannels)
            {
                var s = Slot(ch);
                if (s != null) s.ReadValue();
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
    }
}