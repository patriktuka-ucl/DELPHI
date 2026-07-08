using UnityEngine;
using UnityEngine.Serialization;

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
    /// The recording pipeline writes one mp4 per connected frame channel.
    /// </summary>
    public enum FrameChannel
    {
        Webcam,           // participant-facing physical camera (WebcamSensor)
        SceneOverview,    // bird's-eye scene camera (CameraFeedSensor)
        PlayerView,       // what the participant sees (CameraFeedSensor)
        DashboardDisplay  // display 2 itself — dashboard + controls (CameraFeedSensor, auto-wired by DashboardUI)
    }

    /// <summary>
    /// Tri-state (plus Live) status for a slot, driven by the dashboard to
    /// colour each cell: NotAttached (gray) = no sensor plugged in,
    /// Disabled (yellow) = a sensor is plugged in but its …On toggle is off,
    /// NoSignal (red) = plugged in + on, but not currently producing data,
    /// Live (green) = producing data right now.
    /// </summary>
    public enum ChannelStatus { NotAttached, Disabled, NoSignal, Live }

    /// <summary>
    /// The patch bay. Each input has a slot — drag any ScalarSensor (or, for
    /// video, any FrameSensor) into it to connect that input. Empty slot = no
    /// data. The manager only polls whatever is plugged in; it doesn't
    /// generate anything itself.
    /// </summary>
    public class DelphiManager : MonoBehaviour
    {
        [Header("Gold-standard inputs")]
        [SerializeField] private bool heartRateOn = true;
        [SerializeField] private ScalarSensor heartRate;
        [SerializeField] private bool hrvRmssdOn = true;
        [SerializeField] private ScalarSensor hrvRmssd;
        [SerializeField] private bool respRateOn = true;
        [SerializeField] private ScalarSensor respRate;
        [SerializeField] private bool gsrOn = true;
        [SerializeField] private ScalarSensor gsr;

        [Header("Good additions")]
        [SerializeField] private bool blinkRateOn = true;
        [SerializeField] private ScalarSensor blinkRate;
        [SerializeField] private bool gazeOn = true;
        [SerializeField] private ScalarSensor gaze;
        [SerializeField] private bool pupilDiameterOn = true;
        [SerializeField] private ScalarSensor pupilDiameter;

        [Header("Experimental")]
        [SerializeField] private bool eegOn = true;
        [SerializeField] private ScalarSensor eeg;
        [SerializeField] private bool facialOn = true;
        [SerializeField] private ScalarSensor facial;

        [Header("Video / frame inputs")]
        [SerializeField] private bool webcamOn = true;
        [FormerlySerializedAs("camera")]
        [SerializeField] private FrameSensor webcam;
        [SerializeField] private bool sceneOverviewOn = true;
        [SerializeField] private FrameSensor sceneOverview;
        [SerializeField] private bool playerViewOn = true;
        [SerializeField] private FrameSensor playerView;
        [SerializeField] private bool dashboardDisplayOn = true;
        [SerializeField] private FrameSensor dashboardDisplay;

        // Canonical display order for the dashboard.
        public static readonly Channel[] AllChannels =
        {
            Channel.HeartRate, Channel.RMSSD, Channel.RespRate, Channel.GSR,
            Channel.BlinkRate, Channel.Gaze, Channel.PupilDiameter,
            Channel.EEG, Channel.Facial
        };

        public static readonly FrameChannel[] AllFrameChannels =
        {
            FrameChannel.Webcam, FrameChannel.SceneOverview, FrameChannel.PlayerView,
            FrameChannel.DashboardDisplay
        };

        // ── Playback override ───────────────────────────────────────────
        // While a recorded session is loaded (SessionPlayer.Load sets this),
        // every consumer of the public API — the dashboard above all — is
        // transparently fed the RECORDED data instead of the live sensors.
        public SessionPlayer Playback { get; set; }
        public bool IsInPlayback => Playback != null && Playback.IsLoaded;

        // ── Public API — scalar channels ────────────────────────────────
        public bool HasData(Channel ch) =>
            IsInPlayback ? Playback.HasData(ch)
                         : IsOn(ch) && Slot(ch) != null && !float.IsNaN(Slot(ch).Current);

        public float GetValue(Channel ch)
        {
            if (IsInPlayback) return Playback.GetValue(ch);
            if (!IsOn(ch)) return float.NaN;
            var s = Slot(ch);
            return s != null ? s.Current : float.NaN;
        }

        public ChannelStatus GetStatus(Channel ch)
        {
            if (IsInPlayback) return Playback.HasData(ch) ? ChannelStatus.Live : ChannelStatus.NotAttached;
            var s = Slot(ch);
            if (s == null) return ChannelStatus.NotAttached;
            if (!IsOn(ch)) return ChannelStatus.Disabled;
            return float.IsNaN(s.Current) ? ChannelStatus.NoSignal : ChannelStatus.Live;
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
        public bool HasFrame(FrameChannel ch) =>
            IsInPlayback ? Playback.HasFrame(ch)
                         : IsOn(ch) && FrameSlot(ch) != null && FrameSlot(ch).CurrentFrame != null;

        public Texture GetFrame(FrameChannel ch)
        {
            if (IsInPlayback) return Playback.GetFrame(ch);
            if (!IsOn(ch)) return null;
            var s = FrameSlot(ch);
            return s != null ? s.CurrentFrame : null;
        }

        public ChannelStatus GetStatus(FrameChannel ch)
        {
            if (IsInPlayback) return Playback.HasFrame(ch) ? ChannelStatus.Live : ChannelStatus.NotAttached;
            var s = FrameSlot(ch);
            if (s == null) return ChannelStatus.NotAttached;
            if (!IsOn(ch)) return ChannelStatus.Disabled;
            return s.CurrentFrame == null ? ChannelStatus.NoSignal : ChannelStatus.Live;
        }

        public static (string label, string unit) FrameMeta(FrameChannel ch) => ch switch
        {
            FrameChannel.Webcam           => ("Webcam", ""),
            FrameChannel.SceneOverview    => ("Scene overview", ""),
            FrameChannel.PlayerView       => ("Player view", ""),
            FrameChannel.DashboardDisplay => ("Dashboard display", ""),
            _                             => (ch.ToString(), "")
        };

        // ── Frame slot auto-wiring ───────────────────────────────────────
        // DashboardUI creates its display-capture camera at runtime, so it
        // can't be dragged into the Inspector ahead of time — it wires
        // itself in here, but only if nothing was already assigned by hand.
        public void AutoWireFrameSlot(FrameChannel ch, FrameSensor sensor)
        {
            if (FrameSlot(ch) != null) return;
            switch (ch)
            {
                case FrameChannel.DashboardDisplay: dashboardDisplay = sensor; break;
                case FrameChannel.Webcam:           webcam = sensor; break;
                case FrameChannel.SceneOverview:    sceneOverview = sensor; break;
                case FrameChannel.PlayerView:       playerView = sensor; break;
            }
        }

        // ── Sampling ───────────────────────────────────────────────────
        private void Update()
        {
            foreach (var ch in AllChannels)
            {
                if (!IsOn(ch)) continue;
                var s = Slot(ch);
                if (s != null) s.ReadValue();
            }

            foreach (var fc in AllFrameChannels)
            {
                if (!IsOn(fc)) continue;
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

        private bool IsOn(Channel ch) => ch switch
        {
            Channel.HeartRate     => heartRateOn,
            Channel.RMSSD         => hrvRmssdOn,
            Channel.RespRate      => respRateOn,
            Channel.GSR           => gsrOn,
            Channel.BlinkRate     => blinkRateOn,
            Channel.Gaze          => gazeOn,
            Channel.PupilDiameter => pupilDiameterOn,
            Channel.EEG           => eegOn,
            Channel.Facial        => facialOn,
            _                     => true
        };

        private FrameSensor FrameSlot(FrameChannel ch) => ch switch
        {
            FrameChannel.Webcam           => webcam,
            FrameChannel.SceneOverview    => sceneOverview,
            FrameChannel.PlayerView       => playerView,
            FrameChannel.DashboardDisplay => dashboardDisplay,
            _                             => null
        };

        private bool IsOn(FrameChannel ch) => ch switch
        {
            FrameChannel.Webcam           => webcamOn,
            FrameChannel.SceneOverview    => sceneOverviewOn,
            FrameChannel.PlayerView       => playerViewOn,
            FrameChannel.DashboardDisplay => dashboardDisplayOn,
            _                             => true
        };
    }
}