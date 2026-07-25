using UnityEngine;
using UnityEngine.Serialization;

namespace Delphi
{
    /// <summary>
    /// Every possible SCALAR input signal. The dashboard always lists all of these.
    /// </summary>
    public enum Channel
    {
        HeartRate,      // 0
        RMSSD,          // 1  HRV
        RespRate,       // 2
        GSR,            // 3  raw, uncalibrated 0–1023 ADC
        InterBlinkInterval, // 4  (was BlinkRate — kept at int 4)
        Gaze,           // 5  gaze distance from baseline fixation point
        PupilDiameter,  // 6
        EEG,            // 7  RETIRED — no longer shown or sampled, but kept
        Facial,         // 8  (with EEG) so enum INT values, and every recorded
                        //    CSV / scene channelConfig keyed by them, stay
                        //    valid. Do not reorder — APPEND ONLY below.
        AccX,           // 9   raw Polar H10 PMD accelerometer, milli-g
        AccY,           // 10  raw Polar H10 PMD accelerometer, milli-g
        AccZ,           // 11  raw Polar H10 PMD accelerometer, milli-g
        GsrTonic,       // 12  slow skin-conductance level: low-pass of raw GSR
        GsrPhasic       // 13  fast skin-conductance response: raw GSR − tonic
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
        Panorama360       // 360° equirect of the environment (Panorama360Sensor)
        // Append only — the value is written into recorded session metadata.
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
        [Header("Contact sensors")]
        [Tooltip("Sample rate for this group of scalar sensors, Hz.")]
        [Range(1f, 240f)]
        [FormerlySerializedAs("goldStandardRateHz")]
        public float contactRateHz = 60f;
        [SerializeField] private bool heartRateOn = true;
        [SerializeField] private ScalarSensor heartRate;
        [SerializeField] private bool hrvRmssdOn = true;
        [SerializeField] private ScalarSensor hrvRmssd;
        [SerializeField] private bool respRateOn = true;
        [SerializeField] private ScalarSensor respRate;
        [SerializeField] private bool gsrOn = true;
        [SerializeField] private ScalarSensor gsr;
        // GsrTonic + GsrPhasic are LIVE-DERIVED sub-signals of the raw GSR
        // above (see GSRTonicSensor / GSRPhasicSensor). Raw is intentionally
        // kept alongside them — we're comparing which drives the optimizer
        // best, so all three are sampled and recorded.
        [SerializeField] private bool gsrTonicOn = true;
        [SerializeField] private ScalarSensor gsrTonic;
        [SerializeField] private bool gsrPhasicOn = true;
        [SerializeField] private ScalarSensor gsrPhasic;

        [Header("Gaze Metrics")]
        [Tooltip("Sample rate for this group of scalar sensors, Hz.")]
        [Range(1f, 240f)]
        [FormerlySerializedAs("goodAdditionsRateHz")]
        public float gazeRateHz = 60f;
        [FormerlySerializedAs("blinkRateOn")]
        [SerializeField] private bool blinkIntervalOn = true;
        [FormerlySerializedAs("blinkRate")]
        [SerializeField] private ScalarSensor blinkInterval;
        [SerializeField] private bool gazeOn = true;
        [SerializeField] private ScalarSensor gaze;
        [SerializeField] private bool pupilDiameterOn = true;
        [SerializeField] private ScalarSensor pupilDiameter;

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

        [Header("Debug")]
        [Tooltip("DEBUG — bypass all real sensors: feed the constant below as " +
                 "EVERY metric's reading, so a full session / BO loop runs with " +
                 "no hardware. Turning this ON forces every channel's ON/OFF " +
                 "toggle OFF (their prior states are remembered and restored " +
                 "when you turn it back OFF). Drive it from the checkbox at the " +
                 "top of the Inspector so the save/restore fires.")]
        [SerializeField] private bool debugConstantFeed = false;
        [Tooltip("Constant fed to every metric while Debug Constant Feed is on.")]
        [SerializeField] private float debugValue = 3f;
        // Snapshot of the per-channel ON toggles taken when debug was enabled,
        // restored when it's disabled. Hidden — managed via SetDebugConstantFeed.
        [SerializeField, HideInInspector] private bool[] _debugSavedOn;

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
        [SerializeField] private bool panorama360On = true;
        [SerializeField] private FrameSensor panorama360;
        [Tooltip("Six cube faces are rendered per frame here, so this is the " +
                 "most expensive feed — keep it well below the others.")]
        [SerializeField] private float panorama360Fps = 10f;

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
        // Sized off AllFrameChannels rather than a hardcoded count, so adding a
        // channel can't silently under-size this and throw/skip in Update().
        private readonly double[] _frameNext = new double[AllFrameChannels.Length];

        private static readonly Channel[] ContactChannels =
            { Channel.HeartRate, Channel.RMSSD, Channel.RespRate,
              Channel.GSR, Channel.GsrTonic, Channel.GsrPhasic };
        private static readonly Channel[] GazeChannels =
            { Channel.InterBlinkInterval, Channel.Gaze, Channel.PupilDiameter };
        private static readonly Channel[] ImuChannels =
            { Channel.AccX, Channel.AccY, Channel.AccZ };

        // Canonical display / CSV-column order. EEG + Facial are intentionally
        // absent (retired); the enum still defines them so old recordings and
        // scene channelConfigs keyed by their int value stay valid.
        public static readonly Channel[] AllChannels =
        {
            Channel.HeartRate, Channel.RMSSD, Channel.RespRate,
            Channel.GSR, Channel.GsrTonic, Channel.GsrPhasic,
            Channel.InterBlinkInterval, Channel.Gaze, Channel.PupilDiameter,
            Channel.AccX, Channel.AccY, Channel.AccZ
        };

        public static readonly FrameChannel[] AllFrameChannels =
        {
            FrameChannel.Webcam, FrameChannel.SceneOverview, FrameChannel.PlayerView,
            FrameChannel.Panorama360
        };

        // ── Playback override ───────────────────────────────────────────
        // While a recorded session is loaded (SessionPlayer.Load sets this),
        // every consumer of the public API — the dashboard above all — is
        // transparently fed the RECORDED data instead of the live sensors.
        public SessionPlayer Playback { get; set; }
        public bool IsInPlayback => Playback != null && Playback.IsLoaded;

        // ── Public API — scalar channels ────────────────────────────────
        // Debug Constant Feed (see the Debug fields) overrides EVERYTHING:
        // every channel reports the constant, is Live, and has data — so the
        // dashboard and the whole BO pipeline run with no hardware attached.
        public bool HasData(Channel ch) =>
            debugConstantFeed ? true :
            IsInPlayback ? Playback.HasData(ch)
                         : IsOn(ch) && Slot(ch) != null && !float.IsNaN(Slot(ch).Current);

        public float GetValue(Channel ch)
        {
            if (debugConstantFeed) return debugValue;
            if (IsInPlayback) return Playback.GetValue(ch);
            if (!IsOn(ch)) return float.NaN;
            var s = Slot(ch);
            return s != null ? s.Current : float.NaN;
        }

        public ChannelStatus GetStatus(Channel ch)
        {
            if (debugConstantFeed) return ChannelStatus.Live;
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
            Channel.HeartRate     => ("HR",             "bpm"),
            Channel.RMSSD         => ("HRV-RMSSD",       "ms"),
            Channel.RespRate      => ("Resp. rate",      "br/m"),
            Channel.GSR           => ("GSR",             "raw10bit"),
            Channel.GsrTonic      => ("GSR tonic",       "raw"),
            Channel.GsrPhasic     => ("GSR phasic",      "raw"),
            Channel.InterBlinkInterval => ("Inter-blink interval",   "s"),
            Channel.Gaze          => ("Gaze distance",         "°"),
            Channel.PupilDiameter => ("Pupil diameter",  "mm"),
            Channel.AccX          => ("Acc X",           "mG"),
            Channel.AccY          => ("Acc Y",           "mG"),
            Channel.AccZ          => ("Acc Z",           "mG"),
            // EEG + Facial are retired; the default handles them.
            _                     => (ch.ToString(),     "")
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
            FrameChannel.Panorama360   => ("360° environment", ""),
            _                          => (ch.ToString(), "")
        };

        // ── Core lifecycle ──────────────────────────────────────────────
        // Scalar sampling and csv recording live on DelphiCore's dedicated
        // thread with its own DelphiClock schedule — Unity's frame loop
        // cannot touch their cadence. Started here, torn down on disable.
        private DelphiCore.Group[] _coreGroups;

        private void OnEnable()
        {
            if (debugConstantFeed) EnsureDebugSensor();
            _coreGroups = new[]
            {
                new DelphiCore.Group { channels = ContactChannels, rateHz = contactRateHz },
                new DelphiCore.Group { channels = GazeChannels,    rateHz = gazeRateHz },
                new DelphiCore.Group { channels = ImuChannels,     rateHz = imuRateHz },
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
                _coreGroups[0].rateHz = contactRateHz;
                _coreGroups[1].rateHz = gazeRateHz;
                _coreGroups[2].rateHz = imuRateHz;
            }

            // Keep the debug source's constant in sync with live edits.
            if (debugConstantFeed && _debugSensor != null) _debugSensor.value = debugValue;
        }

        // ── Debug constant feed ─────────────────────────────────────────
        // Public read for the pipeline (SessionController/UI) and control for
        // the custom Inspector checkbox.
        public bool DebugConstantFeed => debugConstantFeed;
        public float DebugValue => debugValue;

        // A single hidden ConstantSensor, spawned in Play mode, that every
        // channel's slot resolves to while debug is on — so DelphiCore samples
        // the constant into the accumulator (baseline + windows) exactly like a
        // real sensor, and recordings/objectives all see it.
        private ConstantSensor _debugSensor;

        private void EnsureDebugSensor()
        {
            if (!Application.isPlaying) return;
            if (_debugSensor != null) { _debugSensor.value = debugValue; return; }
            var go = new GameObject("[DebugConstantSensor]") { hideFlags = HideFlags.HideAndDontSave };
            go.transform.SetParent(transform, false);
            _debugSensor = go.AddComponent<ConstantSensor>();
            _debugSensor.value = debugValue;
        }

        /// <summary>Turn the debug constant feed on/off. Enabling snapshots then
        /// clears every channel's ON toggle; disabling restores the snapshot.
        /// Called by the custom Inspector; safe to call from code too.</summary>
        public void SetDebugConstantFeed(bool on)
        {
            if (on == debugConstantFeed)
            {
                if (_debugSensor != null) _debugSensor.value = debugValue;
                return;
            }
            if (on)
            {
                SnapshotAndClearToggles();
                debugConstantFeed = true;
                EnsureDebugSensor();
            }
            else
            {
                debugConstantFeed = false;
                RestoreToggles();
            }
        }

        private void SnapshotAndClearToggles()
        {
            _debugSavedOn = new bool[AllChannels.Length];
            for (int i = 0; i < AllChannels.Length; i++)
            {
                _debugSavedOn[i] = IsOnRaw(AllChannels[i]);
                SetOn(AllChannels[i], false);
            }
        }

        private void RestoreToggles()
        {
            if (_debugSavedOn == null || _debugSavedOn.Length != AllChannels.Length) return;
            for (int i = 0; i < AllChannels.Length; i++)
                SetOn(AllChannels[i], _debugSavedOn[i]);
            _debugSavedOn = null;
        }

        private void SetOn(Channel ch, bool v)
        {
            switch (ch)
            {
                case Channel.HeartRate:          heartRateOn = v;     break;
                case Channel.RMSSD:              hrvRmssdOn = v;      break;
                case Channel.RespRate:           respRateOn = v;      break;
                case Channel.GSR:                gsrOn = v;           break;
                case Channel.GsrTonic:           gsrTonicOn = v;      break;
                case Channel.GsrPhasic:          gsrPhasicOn = v;     break;
                case Channel.InterBlinkInterval: blinkIntervalOn = v; break;
                case Channel.Gaze:               gazeOn = v;          break;
                case Channel.PupilDiameter:      pupilDiameterOn = v; break;
                case Channel.AccX:               accXOn = v;          break;
                case Channel.AccY:               accYOn = v;          break;
                case Channel.AccZ:               accZOn = v;          break;
            }
        }

        /// <summary>Fastest configured scalar rate — the csv row rate.</summary>
        public float MaxScalarRateHz =>
            Mathf.Max(1f, Mathf.Max(contactRateHz, Mathf.Max(gazeRateHz, imuRateHz)));

        /// <summary>The commanded capture rate for a frame feed — the ONLY
        /// place video rates are configured. The recorder encodes each mp4
        /// at this rate too.</summary>
        public float FrameRate(FrameChannel ch) => Mathf.Max(0.1f, ch switch
        {
            FrameChannel.Webcam        => webcamFps,
            FrameChannel.SceneOverview => sceneOverviewFps,
            FrameChannel.PlayerView    => playerViewFps,
            FrameChannel.Panorama360   => panorama360Fps,
            _                          => 30f
        });

        // Map a channel to its slot. In debug, every channel resolves to the
        // one constant sensor (so DelphiCore samples the constant everywhere).
        private ScalarSensor Slot(Channel ch) =>
            debugConstantFeed && _debugSensor != null ? _debugSensor : SlotRaw(ch);

        private ScalarSensor SlotRaw(Channel ch) => ch switch
        {
            Channel.HeartRate     => heartRate,
            Channel.RMSSD         => hrvRmssd,
            Channel.RespRate      => respRate,
            Channel.GSR           => gsr,
            Channel.GsrTonic      => gsrTonic,
            Channel.GsrPhasic     => gsrPhasic,
            Channel.InterBlinkInterval => blinkInterval,
            Channel.Gaze          => gaze,
            Channel.PupilDiameter => pupilDiameter,
            Channel.AccX          => accX,
            Channel.AccY          => accY,
            Channel.AccZ          => accZ,
            _                     => null
        };

        // In debug every channel samples (regardless of the cleared toggles).
        private bool IsOn(Channel ch) => debugConstantFeed || IsOnRaw(ch);

        private bool IsOnRaw(Channel ch) => ch switch
        {
            Channel.HeartRate     => heartRateOn,
            Channel.RMSSD         => hrvRmssdOn,
            Channel.RespRate      => respRateOn,
            Channel.GSR           => gsrOn,
            Channel.GsrTonic      => gsrTonicOn,
            Channel.GsrPhasic     => gsrPhasicOn,
            Channel.InterBlinkInterval => blinkIntervalOn,
            Channel.Gaze          => gazeOn,
            Channel.PupilDiameter => pupilDiameterOn,
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
            FrameChannel.Panorama360   => panorama360,
            _                          => null
        };

        private bool IsOn(FrameChannel ch) => ch switch
        {
            FrameChannel.Webcam        => webcamOn,
            FrameChannel.SceneOverview => sceneOverviewOn,
            FrameChannel.PlayerView    => playerViewOn,
            FrameChannel.Panorama360   => panorama360On,
            _                          => true
        };
    }
}