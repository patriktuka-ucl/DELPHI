using System.Collections.Concurrent;
using System.Collections.Generic;
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
        TrackOverview,    // bird's-eye scene camera (CameraFeedSensor)
        PlayerView,       // what the participant sees (CameraFeedSensor)
        Panorama360,      // 360° equirect of the environment (Panorama360Sensor)
        EyeCameras        // Varjo XR-3 infrared eye cameras, side by side
                          // (VarjoEyeCameraSensor)
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
        [Tooltip("DEBUG — bypass all real sensors: feed EVERY metric a " +
                 "sine+noise signal centred on the value below, each channel " +
                 "on its own frequency, so a full session / BO loop runs with " +
                 "no hardware. NOT a flat constant, on purpose — a real GP fit " +
                 "needs actual variance to model; feeding it an identical " +
                 "number on every channel is degenerate data that can make " +
                 "BoTorch's model fit stall/retry internally, which looks " +
                 "exactly like a hung optimizer. Turning this ON forces every " +
                 "channel's ON/OFF toggle OFF (their prior states are " +
                 "remembered and restored when you turn it back OFF). Drive " +
                 "it from the checkbox at the top of the Inspector so the " +
                 "save/restore fires.")]
        [SerializeField] private bool debugConstantFeed = false;
        [Tooltip("Centre value each metric's debug signal wobbles around " +
                 "while Debug Constant Feed is on (not the literal value fed — " +
                 "see the toggle's tooltip).")]
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
        // FormerlySerializedAs, or renaming these fields silently empties the
        // slot in every scene that already had a sensor plugged into it — and
        // an empty feed slot looks exactly like a feed that stopped working.
        [FormerlySerializedAs("sceneOverviewOn")]
        [SerializeField] private bool trackOverviewOn = true;
        [FormerlySerializedAs("sceneOverview")]
        [SerializeField] private FrameSensor trackOverview;
        [FormerlySerializedAs("sceneOverviewFps")]
        [SerializeField] private float trackOverviewFps = 30f;
        [SerializeField] private bool playerViewOn = true;
        [SerializeField] private FrameSensor playerView;
        [SerializeField] private float playerViewFps = 30f;
        [SerializeField] private bool panorama360On = true;
        [SerializeField] private FrameSensor panorama360;
        [Tooltip("Six cube faces are rendered per frame here, so this is the " +
                 "most expensive feed — keep it well below the others.")]
        [SerializeField] private float panorama360Fps = 10f;
        [SerializeField] private bool eyeCamerasOn = true;
        [SerializeField] private FrameSensor eyeCameras;
        [Tooltip("Varjo delivers the eye cameras at 200 Hz, which is far more " +
                 "than any recording needs — two 640x480 frames that often is " +
                 "~123 MB/s. This is the rate frames are actually composed " +
                 "at; everything in between is dropped inside the sensor for " +
                 "the cost of a flag check, so raising this costs real work.")]
        [SerializeField] private float eyeCamerasFps = 15f;

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

        // Tracks each frame channel's on/off state as of the LAST Update, so a
        // toggle flip can be edge-detected and turned into sensor.enabled =
        // on/off (see the comment on that line in Update() for why this
        // exists at all). Set to true for every slot in OnEnable — every
        // FrameSensor component starts enabled via Unity's own lifecycle
        // regardless of what the inspector checkbox says, so seeding it that
        // way makes an off-from-the-start channel register as a flip on
        // frame 0 and get disabled immediately, while an on-from-the-start
        // channel matches and is correctly left alone.
        private readonly bool[] _frameOnPrev = new bool[AllFrameChannels.Length];

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
            FrameChannel.Webcam, FrameChannel.TrackOverview, FrameChannel.PlayerView,
            FrameChannel.Panorama360, FrameChannel.EyeCameras
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
            // Read the SAME per-channel varying debug sensor the acquisition
            // pipeline samples from (see Slot/EnsureDebugSensors) — not a flat
            // scalar — so the dashboard shows real variation instead of an
            // identical number on every tile.
            if (debugConstantFeed) return _debugSensors.TryGetValue(ch, out var ds) ? ds.Current : debugValue;
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
            FrameChannel.TrackOverview => ("Track overview", ""),
            FrameChannel.PlayerView    => ("Player view", ""),
            FrameChannel.Panorama360   => ("360° environment", ""),
            FrameChannel.EyeCameras    => ("Eye cameras", ""),
            _                          => (ch.ToString(), "")
        };

        // ── Core lifecycle ──────────────────────────────────────────────
        // Scalar sampling and csv recording live on DelphiCore's dedicated
        // thread with its own DelphiClock schedule — Unity's frame loop
        // cannot touch their cadence. Started here, torn down on disable.
        private DelphiCore.Group[] _coreGroups;

        private void OnEnable()
        {
            if (debugConstantFeed) EnsureDebugSensors();
            _coreGroups = new[]
            {
                new DelphiCore.Group { channels = ContactChannels, rateHz = contactRateHz },
                new DelphiCore.Group { channels = GazeChannels,    rateHz = gazeRateHz },
                new DelphiCore.Group { channels = ImuChannels,     rateHz = imuRateHz },
            };
            _core = new DelphiCore(_coreGroups, AllChannels, Slot, IsOn);
            _core.Start();

            // See _frameOnPrev's declaration for why this is seeded to true
            // rather than left at the array's default false.
            for (int i = 0; i < _frameOnPrev.Length; i++) _frameOnPrev[i] = true;
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
                bool on = IsOn(fc);
                var s = FrameSlot(fc);

                // The "…On" checkboxes used to only gate whether ReadFrame()
                // got CALLED below — they never touched the sensor component
                // itself. For WebcamSensor that left the physical camera
                // capturing continuously (WebCamTexture.Play(), started in
                // OnEnable) for the rest of the session no matter what the
                // checkbox said, because only the component's own OnDisable
                // stops it and nothing was calling that. Toggling the
                // component's enabled state here — instead of leaving this a
                // pure "skip ReadFrame" gate — routes through Unity's normal
                // OnEnable/OnDisable, so turning a feed off actually stops
                // whatever hardware or render-to-texture work it was doing,
                // for every FrameSensor, not just this one.
                if (s != null && on != _frameOnPrev[i])
                {
                    s.enabled = on;
                    _frameOnPrev[i] = on;
                }

                if (!on) continue;
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

            // Keep every debug sensor's centre in sync with live edits to the
            // slider — their frequency/amplitude/noise stay whatever
            // EnsureDebugSensors set, only the offset moves.
            if (debugConstantFeed) SyncDebugSensorOffsets();
        }

        // ── Debug constant feed ─────────────────────────────────────────
        // Public read for the pipeline (SessionController/UI) and control for
        // the custom Inspector checkbox.
        public bool DebugConstantFeed => debugConstantFeed;
        public float DebugValue => debugValue;

        // One hidden MockSensor_Scalar PER CHANNEL, spawned in Play mode,
        // each on its own frequency so channels don't move in lockstep.
        //
        // A single shared ConstantSensor used to sit here instead — literally
        // one identical, unchanging number fed to every channel. That's
        // degenerate data: every baseline-vs-window deviation comes out to
        // exactly 0 forever, which starves BoTorch's GP fit of any real
        // variance to model. In practice that's what was behind a very slow,
        // seemingly-hung optimizer step (GPyTorch's fit retries internally on
        // certain numerical failures, and constant targets are a good way to
        // trigger them) — swapping to genuinely varying mock sensors made it
        // fit normally. This exists so flipping Debug Constant Feed on gets
        // that same good behaviour by default, without hand-wiring
        // MockSensor_Scalar into every slot first.
        // Concurrent, not plain Dictionary: the sampling thread reads this via
        // Slot()/GetValue() while the main thread can be mid-insert in
        // EnsureDebugSensors() — a plain Dictionary is not safe for
        // concurrent read-during-write and can throw or hand back a corrupt
        // read in that window.
        private readonly ConcurrentDictionary<Channel, MockSensor_Scalar> _debugSensors = new();

        private void EnsureDebugSensors()
        {
            if (!Application.isPlaying) return;
            if (_debugSensors.Count > 0) { SyncDebugSensorOffsets(); return; }

            var go = new GameObject("[DebugVaryingSensors]") { hideFlags = HideFlags.HideAndDontSave };
            go.transform.SetParent(transform, false);

            for (int i = 0; i < AllChannels.Length; i++)
            {
                var s = go.AddComponent<MockSensor_Scalar>();
                s.offset = debugValue;
                // Spread frequencies out so no two channels are anywhere near
                // in phase with each other — the actual decorrelation lever;
                // the noise term alone wouldn't reliably prevent channels
                // from tracking together.
                s.frequency = 0.03f + 0.011f * i;
                s.amplitude = Mathf.Max(0.5f, Mathf.Abs(debugValue) * 0.15f);
                s.noise = s.amplitude * 0.3f;
                _debugSensors[AllChannels[i]] = s;
            }
        }

        private void SyncDebugSensorOffsets()
        {
            foreach (var s in _debugSensors.Values)
                if (s != null) s.offset = debugValue;
        }

        /// <summary>Turn the debug constant feed on/off. Enabling snapshots then
        /// clears every channel's ON toggle; disabling restores the snapshot.
        /// Called by the custom Inspector; safe to call from code too.</summary>
        public void SetDebugConstantFeed(bool on)
        {
            if (on == debugConstantFeed)
            {
                SyncDebugSensorOffsets();
                return;
            }
            if (on)
            {
                SnapshotAndClearToggles();
                // Populate _debugSensors BEFORE the sampling thread can see
                // debugConstantFeed = true — Slot()/GetValue() start reading
                // this dictionary the instant the flag flips, so the flag
                // must flip only once every entry is already in place.
                EnsureDebugSensors();
                debugConstantFeed = true;
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
            FrameChannel.TrackOverview => trackOverviewFps,
            FrameChannel.PlayerView    => playerViewFps,
            FrameChannel.Panorama360   => panorama360Fps,
            FrameChannel.EyeCameras    => eyeCamerasFps,
            _                          => 30f
        });

        // Map a channel to its slot. In debug, every channel resolves to ITS
        // OWN varying mock sensor (not a shared one — see EnsureDebugSensors)
        // so DelphiCore samples independently-moving, non-degenerate data
        // into the accumulator exactly like real sensors would.
        private ScalarSensor Slot(Channel ch) =>
            debugConstantFeed && _debugSensors.TryGetValue(ch, out var s) ? s : SlotRaw(ch);

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

        /// <summary>For hardware bridges that serve several channels at once
        /// (PolarH10OscConnection: HR/RMSSD/Acc; GSRSerialConnection: GSR) —
        /// lets them skip connecting entirely (no Python process, no serial
        /// port) when EVERY channel they'd feed is disabled here, rather than
        /// opening a real connection nothing downstream will ever read.
        /// Deliberately reads the RAW toggle, not IsOn's debug-feed override —
        /// Debug Constant Feed already forces every real toggle off (see
        /// SetDebugConstantFeed), and a bridge should stay disconnected during
        /// debug mode precisely because nothing needs real hardware then.</summary>
        public bool IsAnyChannelOn(params Channel[] channels)
        {
            foreach (var ch in channels)
                if (IsOnRaw(ch)) return true;
            return false;
        }

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
            FrameChannel.TrackOverview => trackOverview,
            FrameChannel.PlayerView    => playerView,
            FrameChannel.Panorama360   => panorama360,
            FrameChannel.EyeCameras    => eyeCameras,
            _                          => null
        };

        private bool IsOn(FrameChannel ch) => ch switch
        {
            FrameChannel.Webcam        => webcamOn,
            FrameChannel.TrackOverview => trackOverviewOn,
            FrameChannel.PlayerView    => playerViewOn,
            FrameChannel.Panorama360   => panorama360On,
            FrameChannel.EyeCameras    => eyeCamerasOn,
            _                          => true
        };
    }
}