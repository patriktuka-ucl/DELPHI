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
        Facial,
        AccX,           // raw Polar H10 PMD accelerometer, milli-g
        AccY,           // raw Polar H10 PMD accelerometer, milli-g
        AccZ            // raw Polar H10 PMD accelerometer, milli-g
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
        PlayerView        // what the participant sees (CameraFeedSensor)
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
    // Runs before every default-order (0) script — dashboard, recorder,
    // CarDriver, etc. — so whatever they read this frame was sampled this
    // frame, not left over from last frame's Update ordering.
    [DefaultExecutionOrder(-1000)]
    public class DelphiManager : MonoBehaviour
    {
        [Header("Gold-standard inputs")]
        [Tooltip("Sample rate for this group of scalar sensors, Hz.")]
        [Range(1f, 240f)]
        public float goldStandardRateHz = 60f;
        [SerializeField] private bool heartRateOn = true;
        [SerializeField] private ScalarSensor heartRate;
        [SerializeField] private bool hrvRmssdOn = true;
        [SerializeField] private ScalarSensor hrvRmssd;
        [SerializeField] private bool respRateOn = true;
        [SerializeField] private ScalarSensor respRate;
        [SerializeField] private bool gsrOn = true;
        [SerializeField] private ScalarSensor gsr;

        [Header("Good additions")]
        [Tooltip("Sample rate for this group of scalar sensors, Hz.")]
        [Range(1f, 240f)]
        public float goodAdditionsRateHz = 60f;
        [SerializeField] private bool blinkRateOn = true;
        [SerializeField] private ScalarSensor blinkRate;
        [SerializeField] private bool gazeOn = true;
        [SerializeField] private ScalarSensor gaze;
        [SerializeField] private bool pupilDiameterOn = true;
        [SerializeField] private ScalarSensor pupilDiameter;

        [Header("Experimental")]
        [Tooltip("Sample rate for this group of scalar sensors, Hz.")]
        [Range(1f, 240f)]
        public float experimentalRateHz = 60f;
        [SerializeField] private bool eegOn = true;
        [SerializeField] private ScalarSensor eeg;
        [SerializeField] private bool facialOn = true;
        [SerializeField] private ScalarSensor facial;

        [Header("IMU / Accelerometer")]
        [Tooltip("Sample rate for this group, Hz. Kept separate from " +
                 "Experimental on purpose: the Polar H10's accelerometer " +
                 "delivers real samples at 200Hz, and sharing a rate with " +
                 "EEG/Facial would either starve this of fidelity or force " +
                 "those onto a rate they don't need. This is the RECORDING " +
                 "rate — the dashboard redraws slower on its own separate " +
                 "throttle (ExperimentUI's Redraw Fps) without dropping any " +
                 "samples from what actually gets written to disk.")]
        [Range(1f, 240f)]
        public float imuRateHz = 200f;
        [SerializeField] private bool accXOn = true;
        [SerializeField] private ScalarSensor accX;
        [SerializeField] private bool accYOn = true;
        [SerializeField] private ScalarSensor accY;
        [SerializeField] private bool accZOn = true;
        [SerializeField] private ScalarSensor accZ;

        [Header("Video / frame inputs")]
        [Tooltip("Per-feed capture FPS. ALL rates in DELPHI live here on the " +
                 "manager — sensors have no clocks of their own, they capture " +
                 "when commanded. Each feed gets its own FPS because their " +
                 "costs differ (a camera feed is a full extra scene render).")]
        [SerializeField] private bool webcamOn = true;
        [FormerlySerializedAs("camera")]
        [SerializeField] private FrameSensor webcam;
        [SerializeField] private float webcamFps = 30f;
        [SerializeField] private bool sceneOverviewOn = true;
        [SerializeField] private FrameSensor sceneOverview;
        [SerializeField] private float sceneOverviewFps = 30f;
        [SerializeField] private bool playerViewOn = true;
        [SerializeField] private FrameSensor playerView;
        [SerializeField] private float playerViewFps = 30f;

        // The acquisition engine — all scalar sampling and csv recording
        // happen on ITS dedicated thread with its own DelphiClock schedule,
        // fully decoupled from Unity's frame loop. This MonoBehaviour is
        // just the facade: configuration, frame feeds (main-thread-only
        // Unity APIs) and the query API the UI/simulator call into.
        private DelphiCore _core;
        public DelphiCore Core => _core;

        // Frame feed next-tick times (frame capture MUST stay on the main
        // thread — camera/texture APIs). Scheduled on DelphiClock, same
        // time base as the core.
        private readonly double[] _frameNext = new double[3]; // indexed by FrameChannel

        private static readonly Channel[] GoldStandardChannels =
            { Channel.HeartRate, Channel.RMSSD, Channel.RespRate, Channel.GSR };
        private static readonly Channel[] GoodAdditionsChannels =
            { Channel.BlinkRate, Channel.Gaze, Channel.PupilDiameter };
        private static readonly Channel[] ExperimentalChannels =
            { Channel.EEG, Channel.Facial };
        private static readonly Channel[] ImuChannels =
            { Channel.AccX, Channel.AccY, Channel.AccZ };

        // Canonical display order for the dashboard.
        public static readonly Channel[] AllChannels =
        {
            Channel.HeartRate, Channel.RMSSD, Channel.RespRate, Channel.GSR,
            Channel.BlinkRate, Channel.Gaze, Channel.PupilDiameter,
            Channel.EEG, Channel.Facial, Channel.AccX, Channel.AccY, Channel.AccZ
        };

        public static readonly FrameChannel[] AllFrameChannels =
        {
            FrameChannel.Webcam, FrameChannel.SceneOverview, FrameChannel.PlayerView
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

        /// <summary>The sensor component plugged into a channel's slot, or
        /// null if empty. For callers that need more than a value/status —
        /// e.g. logging which sensor produced a trial's data.</summary>
        public ScalarSensor GetSensor(Channel ch) => Slot(ch);

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
            Channel.AccX          => ("Acc X",                 "mG"),
            Channel.AccY          => ("Acc Y",                 "mG"),
            Channel.AccZ          => ("Acc Z",                 "mG"),
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
            FrameChannel.Webcam        => ("Webcam", ""),
            FrameChannel.SceneOverview => ("Scene overview", ""),
            FrameChannel.PlayerView    => ("Player view", ""),
            _                          => (ch.ToString(), "")
        };

        // ── Core lifecycle ──────────────────────────────────────────────
        // Scalar sampling and csv recording live on DelphiCore's dedicated
        // thread with its own DelphiClock schedule — Unity's frame loop
        // cannot touch their cadence. Started here, torn down on disable.
        private DelphiCore.Group[] _coreGroups;

        private void OnEnable()
        {
            _coreGroups = new[]
            {
                new DelphiCore.Group { channels = GoldStandardChannels,  rateHz = goldStandardRateHz },
                new DelphiCore.Group { channels = GoodAdditionsChannels, rateHz = goodAdditionsRateHz },
                new DelphiCore.Group { channels = ExperimentalChannels,  rateHz = experimentalRateHz },
                new DelphiCore.Group { channels = ImuChannels,           rateHz = imuRateHz },
            };
            _core = new DelphiCore(_coreGroups, AllChannels, Slot, IsOn);
            _core.Start();
        }

        private void OnDisable()
        {
            _core?.Stop();
            _core = null;
        }

        // ── Main-thread duties only ─────────────────────────────────────
        // 1. Frame feeds — camera/texture APIs are main-thread-only, so
        //    their capture ticks (still on the DelphiClock time base, at
        //    the per-feed FPS above) have to run here.
        // 2. Forward live Inspector rate tweaks to the core's groups.
        // Everything scalar happens on the core's thread.
        private void Update()
        {
            for (int i = 0; i < AllFrameChannels.Length; i++)
            {
                var fc = AllFrameChannels[i];
                if (!IsOn(fc)) continue;
                var s = FrameSlot(fc);
                if (s == null) continue;
                if (DelphiClock.Now < _frameNext[i]) continue;
                _frameNext[i] = DelphiClock.Now + 1.0 / FrameRate(fc);
                s.ReadFrame();
            }

            if (_coreGroups != null)
            {
                _coreGroups[0].rateHz = goldStandardRateHz;
                _coreGroups[1].rateHz = goodAdditionsRateHz;
                _coreGroups[2].rateHz = experimentalRateHz;
                _coreGroups[3].rateHz = imuRateHz;
            }
        }

        /// <summary>Fastest configured scalar rate — the csv row rate.</summary>
        public float MaxScalarRateHz =>
            Mathf.Max(1f, Mathf.Max(goldStandardRateHz,
                      Mathf.Max(goodAdditionsRateHz, Mathf.Max(experimentalRateHz, imuRateHz))));

        /// <summary>The commanded capture rate for a frame feed — the ONLY
        /// place video rates are configured. The recorder encodes each mp4
        /// at this rate too.</summary>
        public float FrameRate(FrameChannel ch) => Mathf.Max(0.1f, ch switch
        {
            FrameChannel.Webcam        => webcamFps,
            FrameChannel.SceneOverview => sceneOverviewFps,
            FrameChannel.PlayerView    => playerViewFps,
            _                          => 30f
        });

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
            Channel.AccX          => accX,
            Channel.AccY          => accY,
            Channel.AccZ          => accZ,
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
            Channel.AccX          => accXOn,
            Channel.AccY          => accYOn,
            Channel.AccZ          => accZOn,
            _                     => true
        };

        private FrameSensor FrameSlot(FrameChannel ch) => ch switch
        {
            FrameChannel.Webcam        => webcam,
            FrameChannel.SceneOverview => sceneOverview,
            FrameChannel.PlayerView    => playerView,
            _                          => null
        };

        private bool IsOn(FrameChannel ch) => ch switch
        {
            FrameChannel.Webcam        => webcamOn,
            FrameChannel.SceneOverview => sceneOverviewOn,
            FrameChannel.PlayerView    => playerViewOn,
            _                          => true
        };
    }
}