using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR;
using Varjo.XR;

namespace Delphi
{
    /// <summary>
    /// Owns the single eye-tracking stream from the Varjo XR-3 and derives the
    /// three gaze metrics DELPHI records. Mirrors GSRSerialConnection's and
    /// PolarH10OscConnection's role — the only thing that talks to the device —
    /// so EyeBlinkIntervalSensor, GazeDistanceSensor and PupilDiameterSensor
    /// can stay thin readers that just hand over a latched value.
    ///
    /// Only ONE of these should exist in the scene (singleton).
    ///
    /// WHY THE BUFFERED API AND NOT GetGaze()/GetEyeMeasurements():
    ///
    ///   Varjo streams at 200 Hz; Unity renders at 90. GetGaze() returns only
    ///   the LATEST sample, so polling it once per frame silently throws away
    ///   over half the stream — and Varjo's own docs warn that two calls
    ///   within one frame can even return different results. For pupil
    ///   diameter that would merely be lossy. For BLINKS it is fatal: a blink
    ///   lasts 100–400 ms and its onset has to be timed to better than a frame
    ///   for the inter-blink interval to mean anything.
    ///
    ///   GetGazeList(out gaze, out measurements) drains the whole queue since
    ///   last call and is what Varjo's own EyeTrackingExample uses. Every
    ///   sample is processed here, and blink timing comes from each sample's
    ///   own captureTime (nanoseconds, device clock) rather than from Unity's
    ///   frame time — so a frame hitch cannot smear a blink interval.
    ///
    /// WHY THE THREE METRICS ARE REDUCED DIFFERENTLY:
    ///
    ///   Blink  — EVERY sample is fed to the detector; that's the point.
    ///   Pupil  — AVERAGED across the samples in this batch. Pupil diameter is
    ///            a slow signal and averaging is free noise reduction.
    ///   Gaze   — LATEST valid sample only. This one measures how far the eye
    ///            has darted from the neutral direction, so averaging across a batch would
    ///            blur the exact saccade the channel exists to capture.
    ///
    /// "NEUTRAL GAZE DIRECTION" IS NOT THE STUDY BASELINE. Nothing in this
    /// file measures a baseline in DELPHI's sense of the word. SessionController
    /// owns that one: the per-channel mean and SD captured during a window near
    /// the end of the MEDITATION, once per condition, which every channel's
    /// deviation is then scored against. This component only needs a
    /// GEOMETRIC reference — which way the eye points when it is pointing
    /// straight ahead — so that "gaze distance" has an origin to be a distance
    /// FROM. It is captured as soon as the tracker is calibrated, long before
    /// any meditation, and it is not a measurement of the participant at all.
    /// The Gaze channel then flows into the meditation baseline exactly like
    /// every other channel, untouched by any of this.
    ///
    /// NOTHING HERE IS TUNED PER PARTICIPANT, BY DESIGN. The only per-person
    /// step is the gaze calibration done in the Varjo Base app. Everything
    /// this component needs about an individual's eyes it learns at run time:
    /// the blink thresholds are fractions of an open-eye level measured from
    /// that person (UpdateOpenEyeReference), and the neutral gaze direction captures
    /// itself on the first steady fixation after calibration
    /// (TryAutoCaptureNeutralGaze). Both are discarded when the signal drops, on
    /// the assumption that a headset coming off means the next participant is
    /// about to put it on. The inspector values are population defaults, not
    /// starting points to adjust.
    ///
    /// If no headset is running this component does nothing and every sensor
    /// reads NaN (dashboard: NoSignal). The desktop workflow is untouched.
    /// </summary>
    public class VarjoGazeConnection : MonoBehaviour
    {
        public static VarjoGazeConnection Instance { get; private set; }

        [Header("Varjo stream")]
        [Tooltip("200 Hz on the XR-3. MaximumSupported is what you want — the " +
                 "blink detector's timing resolution is one sample, and the " +
                 "other two metrics are averaged/latched so a faster stream " +
                 "costs them nothing.")]
        [SerializeField]
        private VarjoEyeTracking.GazeOutputFrequency outputFrequency =
            VarjoEyeTracking.GazeOutputFrequency.MaximumSupported;

        [Tooltip("Varjo's own smoothing. Standard is their default and steadies " +
                 "the gaze ray at the cost of a little latency. Switch to None " +
                 "if you want the unfiltered signal for offline analysis — but " +
                 "note the blink detector's hysteresis was tuned against " +
                 "Standard, so re-check the thresholds below if you change it.")]
        [SerializeField]
        private VarjoEyeTracking.GazeOutputFilterType outputFilter =
            VarjoEyeTracking.GazeOutputFilterType.Standard;

        [Header("Calibration (researcher-side)")]
        [Tooltip("OPTIONAL convenience. Calibration is normally done per " +
                 "participant in the Varjo Base app before the session; this " +
                 "key just triggers the same sequence without leaving Unity. " +
                 "Either way it takes over the participant's view, so it is " +
                 "NEVER automatic. F11/F12 belong to VrRig.")]
        [SerializeField] private Key calibrationKey = Key.F10;
        [SerializeField]
        private VarjoEyeTracking.GazeCalibrationMode calibrationMode =
            VarjoEyeTracking.GazeCalibrationMode.Fast;

        [Header("Neutral gaze direction")]
        [Tooltip("OPTIONAL. Forces a fresh neutral-gaze capture. Not normally " +
                 "needed — the capture below runs itself. Use it if a " +
                 "participant reseats the headset mid-session.")]
        [SerializeField] private Key neutralGazeKey = Key.F9;
        [Tooltip("Gaze is averaged over this long when capturing the neutral direction. A " +
                 "single sample could land mid-saccade and would pin the " +
                 "reference to wherever the eye happened to be flicking.")]
        [SerializeField] private float neutralGazeWindowSeconds = 1f;
        [Tooltip("Capture the neutral direction automatically, with no key press, once " +
                 "Varjo reports gaze calibrated and the participant holds a " +
                 "steady fixation. Leave this ON — it removes the only " +
                 "per-participant step this component would otherwise need. " +
                 "The key below still forces a fresh capture at any time.")]
        [SerializeField] private bool autoCaptureNeutralGaze = true;
        [Tooltip("A capture window is thrown away if the eye moved more than " +
                 "this much during it. This is the safety catch that lets the " +
                 "capture run unattended: it waits for a genuinely steady " +
                 "fixation instead of averaging across a saccade.")]
        [SerializeField] private float maxNeutralGazeDispersionDegrees = 3f;

        // ── Blink detection ──────────────────────────────────────────────
        //
        // THRESHOLDS ARE RELATIVE TO EACH PARTICIPANT'S OWN EYE, LEARNED AT
        // RUN TIME. Nothing here needs setting per person.
        //
        // Absolute openness thresholds cannot be made to fit everybody, and
        // the way they fail is silent. Varjo's openness is a ratio, but
        // resting openness still varies enormously between people — narrow
        // palpebral fissures, epicanthic folds, hooded or drooping lids,
        // habitual squinting, even where the headset sits on the face. Pick
        // 0.6 as "open" and a participant whose relaxed eye reads 0.55 is
        // scored as permanently mid-blink; pick 0.3 and someone wide-eyed
        // never dips below it and is scored as never blinking at all. Both
        // produce a channel that looks perfectly healthy and is entirely
        // wrong, and the direction of the error correlates with facial
        // anatomy — which is about the worst possible confound to hand a
        // study that is comparing people.
        //
        // So the detector learns each participant's own OPEN level (see
        // UpdateOpenEyeReference) and expresses both thresholds as fractions
        // of it. Convergence takes a second or two of normal wearing, with no
        // researcher action and nothing to configure.
        [Header("Blink detection (self-calibrating — no per-participant tuning)")]
        [Tooltip("Closing starts when openness drops below this FRACTION of " +
                 "the participant's own learned open level. A real blink drives " +
                 "openness to near zero, so half-way down is a safe line that " +
                 "still ignores ordinary lid tremor.")]
        [Range(0.2f, 0.8f)] [SerializeField] private float closeFraction = 0.5f;
        [Tooltip("The eye counts as open again above this fraction of the same " +
                 "learned level. The gap up from Close Fraction is the " +
                 "hysteresis — one threshold alone chatters, because openness " +
                 "dithers across it during the lid's travel and a single blink " +
                 "gets counted several times.")]
        [Range(0.3f, 0.95f)] [SerializeField] private float openFraction = 0.75f;
        [Tooltip("Shorter closures than this are tracker noise, not blinks. " +
                 "Physiological, not personal — safe for everyone.")]
        [SerializeField] private float minBlinkSeconds = 0.04f;
        [Tooltip("Longer closures than this are not blinks either — they are a " +
                 "participant resting their eyes, or the headset sitting on a " +
                 "desk. Rejecting them is what stops 'headset off' from " +
                 "registering as one enormous blink. Also physiological: " +
                 "spontaneous blinks run 100–400 ms in everybody.")]
        [SerializeField] private float maxBlinkSeconds = 0.6f;
        [Tooltip("The learned open level must reach at least this before any " +
                 "blink is reported. Stops the detector arming against a " +
                 "reference learned while the headset sat on a desk with the " +
                 "eye cameras seeing nothing.")]
        [Range(0.05f, 0.5f)] [SerializeField] private float minOpenEyeReference = 0.15f;

        [Header("Signal health")]
        [Tooltip("How long the stream may report nothing valid before all three " +
                 "channels go NaN (dashboard: NoSignal). Must comfortably " +
                 "exceed a blink — gaze legitimately goes invalid every time " +
                 "the participant blinks, and punching a NaN hole in the data " +
                 "several times a minute would be worse than useless.")]
        [SerializeField] private float signalTimeoutSeconds = 1f;

        /// <summary>True once the Varjo stream is actually delivering samples.</summary>
        public bool IsStreaming { get; private set; }

        // ── Latched values ───────────────────────────────────────────────
        // Written here on the Unity main thread, read from DELPHI's sampling
        // thread by the three sensors. Guarded by _lock, same contract as
        // GSRSerialConnection.GetRawValue().
        private readonly object _lock = new object();
        private float _interBlinkIntervalSeconds = float.NaN;
        private float _gazeDistanceDegrees = float.NaN;
        private float _pupilDiameterMm = float.NaN;

        // ── Stream state (main thread only) ──────────────────────────────
        private List<VarjoEyeTracking.GazeData> _gaze;
        private List<VarjoEyeTracking.EyeMeasurements> _measurements;
        private bool _available;          // gaze allowed + XR running
        private double _lastSampleDeviceSeconds = double.NaN; // device clock, last sample of ANY kind

        // Staleness is tracked on DELPHI's WALL clock, not on the device
        // clock, because the failure that matters most — the stream stopping
        // dead — delivers no samples at all, and a clock read from the samples
        // therefore freezes exactly when it needs to keep counting.
        //
        // Three separate stamps rather than one, because the three metrics
        // fail independently: an uncalibrated or degraded tracker can stop
        // producing a valid GAZE RAY while the eye cameras carry on reporting
        // perfectly good pupil size and eyelid openness. Collapsing these
        // would throw away two working channels whenever the third broke.
        private double _lastSampleWall = double.NaN;      // anything arriving
        private double _lastGazeValidWall = double.NaN;   // a Valid gaze ray
        private double _lastPupilWall = double.NaN;       // a non-zero pupil

        // Last good values, held across the brief invalid window every blink
        // produces. Main thread only — the published copies are the latched
        // fields above.
        private float _heldGazeDistance = float.NaN;
        private float _heldPupil = float.NaN;

        // Blink detector.
        private bool _eyesClosed;
        private double _closureStartSeconds;
        private double _lastBlinkOnsetSeconds = double.NaN;
        private float _lastCompletedIbiSeconds = float.NaN;
        private int _blinkCount;

        // The participant's learned open level, and the previous sample's
        // timestamp so the adaptation rates below are per-SECOND rather than
        // per-sample (and so stay correct if the stream runs at 100 Hz, or
        // drops samples under load).
        private float _openEyeReference = float.NaN;
        private double _prevOpennessSeconds = double.NaN;
        private bool _blinkDetectorArmed;

        // Asymmetric adaptation, in seconds. Rising fast and falling slowly
        // makes the reference track the UPPER envelope of openness — i.e. the
        // eye's open level — rather than its mean, which would sit somewhere
        // between open and closed and drift with how often the person blinks.
        private const float ReferenceRiseTau = 0.5f;  // converges in ~2 s from cold
        private const float ReferenceFallTau = 60f;   // only follows real, sustained change

        // Neutral gaze direction. Head-relative, so straight-ahead is a meaningful
        // default rather than a placeholder — see CaptureNeutralGaze().
        private Vector3 _neutralGazeForward = Vector3.forward;
        private bool _neutralGazeCaptured;
        private bool _capturingNeutralGaze;
        private double _neutralGazeCaptureEndsAt;
        private Vector3 _neutralGazeAccumulator;
        private int _neutralGazeSamples;
        private bool _autoCapturePending;   // an unattended capture is retrying until the eye is steady
        private bool _autoCaptureDone;      // fired once per streaming session

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("[VarjoGazeConnection] Duplicate instance found — only one should exist. Destroying duplicate.");
                Destroy(this);
                return;
            }
            Instance = this;
        }

        private void Start()
        {
            // Everything below is a native P/Invoke into the Varjo plugin.
            // Calling it with no XR session loaded is how you get a
            // DllNotFoundException on a workstation with no headset, so the
            // desktop path bails out here and the sensors just read NaN.
            if (!XRSettings.isDeviceActive)
            {
                Debug.Log("[VarjoGazeConnection] No XR device running — gaze channels will read NaN. " +
                          "This is the normal desktop path.", this);
                return;
            }

            try
            {
                if (!VarjoEyeTracking.IsGazeAllowed())
                {
                    Debug.LogWarning("[VarjoGazeConnection] Eye tracking is not permitted for this application. " +
                                     "Enable it in Varjo Base (System > Eye tracking > 'Allow eye tracking') and " +
                                     "restart. All three gaze channels will read NaN until then.", this);
                    return;
                }

                VarjoEyeTracking.SetGazeOutputFrequency(outputFrequency);
                VarjoEyeTracking.SetGazeOutputFilterType(outputFilter);
                _available = true;

                if (!VarjoEyeTracking.IsGazeCalibrated())
                {
                    Debug.LogWarning("[VarjoGazeConnection] Gaze is NOT calibrated. Pupil diameter and blink " +
                                     "detection are unaffected — they come from the eye cameras, not from the " +
                                     "gaze ray — but the Gaze channel is only Varjo's uncalibrated best estimate " +
                                     "until you calibrate in Varjo Base. The automatic neutral-gaze capture deliberately " +
                                     $"waits for calibration before it fires; {calibrationKey} triggers Varjo's " +
                                     "calibration from here if you would rather not leave Unity.", this);
                }

                Debug.Log($"[VarjoGazeConnection] Eye tracking ready — {outputFrequency}, filter {outputFilter}.", this);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[VarjoGazeConnection] Could not start eye tracking: {e.Message}. " +
                                 "Gaze channels will read NaN.", this);
                _available = false;
            }
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        /// <summary>Keeps the hysteresis the right way round. If the open
        /// threshold ever slipped below the close threshold the detector would
        /// not merely misbehave, it would stop detecting anything at all —
        /// silently, with the channel still reporting a plausible held value.</summary>
        private void OnValidate()
        {
            if (openFraction <= closeFraction)
                openFraction = Mathf.Min(0.95f, closeFraction + 0.15f);
            if (maxBlinkSeconds <= minBlinkSeconds)
                maxBlinkSeconds = minBlinkSeconds + 0.1f;
        }

        // ── Per-frame ────────────────────────────────────────────────────

        private void Update()
        {
            HandleKeys();
            if (!_available) return;

            int count;
            try
            {
                count = VarjoEyeTracking.GetGazeList(out _gaze, out _measurements);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[VarjoGazeConnection] Gaze stream failed: {e.Message}. Stopping.", this);
                _available = false;
                IsStreaming = false;
                Publish(float.NaN, float.NaN, float.NaN);
                return;
            }

            // An empty batch is normal on a frame faster than the stream —
            // it is NOT loss of signal, so the timeouts in PublishBatch are
            // what decide health, not this.
            if (count > 0) _lastSampleWall = DelphiClock.Now;
            for (int i = 0; i < count; i++)
            {
                ProcessSample(_gaze[i], _measurements[i]);
            }

            PublishBatch(count);
        }

        /// <summary>Feeds one 200 Hz sample through the blink detector and the
        /// neutral-gaze accumulator. Uses the sample's OWN captureTime, so blink
        /// timing is device-clocked and immune to frame hitches.</summary>
        private void ProcessSample(VarjoEyeTracking.GazeData gaze,
                                   VarjoEyeTracking.EyeMeasurements measurements)
        {
            double t = gaze.captureTime * 1e-9; // ns → s, Varjo's device clock
            _lastSampleDeviceSeconds = t;

            if (gaze.status == VarjoEyeTracking.GazeStatus.Valid)
            {
                _lastGazeValidWall = DelphiClock.Now;
                AccumulateNeutralGaze(gaze, t);
            }

            // The blink detector runs on eye OPENNESS regardless of gaze
            // status, and deliberately so: gaze status is expected to drop to
            // Invalid partway through a blink (both eyes shut = eyes cannot be
            // located), which is precisely the window the detector must see.
            // Gating it on Valid would blind it to the second half of every
            // blink it is supposed to be measuring.
            UpdateBlinkDetector(measurements, t);
        }

        /// <summary>Schmitt-triggered blink detector.
        ///
        /// Openness is the MEAN of both eyes because a spontaneous blink is
        /// bilateral — averaging means a momentary tracking dropout on one eye
        /// alone cannot fake a blink, while a real blink still drives the mean
        /// firmly below threshold.
        ///
        /// The inter-blink interval is measured onset-to-onset rather than
        /// end-to-start: onset is the sharp, well-defined event, whereas the
        /// reopening is a slow lid movement whose timing depends entirely on
        /// where the open threshold sits.</summary>
        private void UpdateBlinkDetector(VarjoEyeTracking.EyeMeasurements m, double t)
        {
            float openness = (m.leftEyeOpenness + m.rightEyeOpenness) * 0.5f;

            UpdateOpenEyeReference(openness, t);
            if (!_blinkDetectorArmed) return;

            float closeThreshold = _openEyeReference * closeFraction;
            float openThreshold = _openEyeReference * openFraction;

            if (!_eyesClosed)
            {
                if (openness >= closeThreshold) return;
                _eyesClosed = true;
                _closureStartSeconds = t;
                return;
            }

            // Closed. Abandon anything that has gone on too long to be a blink
            // — resting eyes, or a headset on the desk — WITHOUT recording an
            // interval, and without waiting for the eyes to reopen to notice.
            if (t - _closureStartSeconds > maxBlinkSeconds)
            {
                if (openness > openThreshold) _eyesClosed = false;
                return;
            }

            if (openness <= openThreshold) return;

            // Reopened inside the plausible window: a blink.
            _eyesClosed = false;
            double duration = t - _closureStartSeconds;
            if (duration < minBlinkSeconds) return; // tracker noise

            if (!double.IsNaN(_lastBlinkOnsetSeconds))
            {
                _lastCompletedIbiSeconds = (float)(_closureStartSeconds - _lastBlinkOnsetSeconds);
            }
            _lastBlinkOnsetSeconds = _closureStartSeconds;
            _blinkCount++;
        }

        /// <summary>Learns this participant's own open-eye level — the thing
        /// both blink thresholds are expressed as a fraction of.
        ///
        /// It is a peak follower, not an average: it rises fast toward any
        /// openness above the current estimate and falls only very slowly.
        /// An average would settle somewhere between open and closed and,
        /// worse, would drift with how often the person blinks — so a
        /// frequent blinker's reference would sag, raising their effective
        /// threshold, making blinks even easier to detect, and feeding back
        /// on the very quantity being measured.
        ///
        /// Adaptation is FROZEN while a closure is in progress, so a blink
        /// contributes nothing at all to the level it is being measured
        /// against. What the slow fall is actually for is real, sustained
        /// change: the headset settling, the participant tiring, a rig
        /// vibration shifting the optics.</summary>
        private void UpdateOpenEyeReference(float openness, double t)
        {
            if (openness <= 0f || float.IsNaN(openness))
            {
                _prevOpennessSeconds = t;
                return;
            }

            if (float.IsNaN(_openEyeReference))
            {
                _openEyeReference = openness;
                _prevOpennessSeconds = t;
                return;
            }

            // Per-second rates, so a 100 Hz stream, a 200 Hz stream and a
            // dropped-sample gap all converge identically.
            double dt = t - _prevOpennessSeconds;
            _prevOpennessSeconds = t;
            if (dt <= 0d || dt > 1d) return; // first sample after a gap — no meaningful rate

            if (!_eyesClosed)
            {
                float tau = openness > _openEyeReference ? ReferenceRiseTau : ReferenceFallTau;
                float alpha = 1f - Mathf.Exp((float)(-dt / tau));
                _openEyeReference += (openness - _openEyeReference) * alpha;
            }

            if (!_blinkDetectorArmed && _openEyeReference >= minOpenEyeReference)
            {
                _blinkDetectorArmed = true;
                Debug.Log($"[VarjoGazeConnection] Blink detector armed — this participant's open-eye level " +
                          $"learned as {_openEyeReference:0.00}, so blinks are counted below " +
                          $"{_openEyeReference * closeFraction:0.00} and reopening above " +
                          $"{_openEyeReference * openFraction:0.00}.", this);
            }
        }

        /// <summary>Sums valid gaze directions while a neutral-gaze capture is
        /// running. Vectors are summed and normalised at the end rather than
        /// averaged component-wise as angles — summing unit vectors and
        /// renormalising is the correct mean direction and has no wraparound
        /// to get wrong.</summary>
        private void AccumulateNeutralGaze(VarjoEyeTracking.GazeData gaze, double t)
        {
            if (!_capturingNeutralGaze) return;

            // The window is anchored on the FIRST sample to arrive, not on
            // whatever the clock read when the key was pressed. Varjo's
            // captureTime is an absolute device timestamp, so anchoring it at
            // request time means guessing a value on that clock — and if no
            // valid gaze had been seen yet there is nothing to guess from, so
            // the window would expire on its own first sample.
            if (double.IsNaN(_neutralGazeCaptureEndsAt))
                _neutralGazeCaptureEndsAt = t + Mathf.Max(0.05f, neutralGazeWindowSeconds);

            _neutralGazeAccumulator += gaze.gaze.forward;
            _neutralGazeSamples++;

            if (t < _neutralGazeCaptureEndsAt) return;

            _capturingNeutralGaze = false;
            if (_neutralGazeSamples == 0 || _neutralGazeAccumulator.sqrMagnitude < 1e-6f)
            {
                Debug.LogWarning("[VarjoGazeConnection] Neutral-gaze capture got no usable gaze — keeping the " +
                                 "previous direction. Is the participant wearing the headset with their eyes open?", this);
                return;
            }

            // Reject a window the eye did not actually hold still through.
            //
            // The resultant length of the summed unit vectors gives the spread
            // in one pass, with nothing stored: tightly clustered directions
            // sum to nearly n, scattered ones partly cancel. For small spreads
            // R ≈ 1 − σ²/2, which inverts to the dispersion below.
            //
            // This gate is what makes an UNATTENDED capture safe. Averaging a
            // window that happened to span a saccade would silently plant the
            // the reference somewhere the participant never rests, and every angle
            // measured afterwards would be wrong by that offset — with no
            // symptom, because the channel would still look perfectly alive.
            float resultant = _neutralGazeAccumulator.magnitude / _neutralGazeSamples;
            float dispersionDeg = Mathf.Rad2Deg * Mathf.Sqrt(Mathf.Max(0f, 2f * (1f - resultant)));

            if (dispersionDeg > maxNeutralGazeDispersionDegrees)
            {
                if (_autoCapturePending)
                {
                    BeginNeutralGazeCapture(); // the eye was moving — quietly wait for a steadier moment
                    return;
                }
                Debug.LogWarning($"[VarjoGazeConnection] Neutral gaze REJECTED — the eye moved {dispersionDeg:0.#}° " +
                                 $"during the window (limit {maxNeutralGazeDispersionDegrees:0.#}°). Keeping the previous " +
                                 "direction. Ask the participant to hold a steady forward fixation and try again.", this);
                return;
            }

            bool wasRecapture = _neutralGazeCaptured;
            float movedBy = wasRecapture
                ? Vector3.Angle(_neutralGazeForward, _neutralGazeAccumulator.normalized)
                : 0f;

            _neutralGazeForward = _neutralGazeAccumulator.normalized;
            _neutralGazeCaptured = true;
            bool wasAuto = _autoCapturePending;
            _autoCapturePending = false;
            Debug.Log($"[VarjoGazeConnection] Neutral gaze direction {(wasAuto ? "auto-captured" : "captured")} from " +
                      $"{_neutralGazeSamples} samples, steady to {dispersionDeg:0.#}° (direction {_neutralGazeForward}). " +
                      "The Gaze channel now reports degrees from here.", this);

            // A RE-capture moves the channel's zero point. Harmless before the
            // meditation; corrupting after it, because SessionController has
            // by then recorded a mean and SD for the Gaze channel measured
            // against the OLD origin, and every deviation scored against that
            // baseline afterwards is offset by however far the origin shifted.
            // Nothing downstream can detect this on its own — the channel goes
            // on producing entirely plausible degrees — so say so here.
            if (wasRecapture && movedBy > 0.5f)
            {
                Debug.LogWarning($"[VarjoGazeConnection] Neutral gaze direction MOVED {movedBy:0.#}° from its " +
                                 "previous value. If the meditation baseline for the Gaze channel has already " +
                                 "been captured in this condition, it was measured from the old origin and no " +
                                 "longer applies — treat that condition's Gaze data as suspect.", this);
            }
        }

        /// <summary>Reduces this frame's batch to the three latched values,
        /// NaN-ing each one independently once its own source has been quiet
        /// for longer than the timeout.</summary>
        private void PublishBatch(int count)
        {
            double now = DelphiClock.Now;
            bool streaming = !double.IsNaN(_lastSampleWall) &&
                             now - _lastSampleWall <= signalTimeoutSeconds;

            if (streaming != IsStreaming)
            {
                IsStreaming = streaming;
                if (streaming)
                    Debug.Log("[VarjoGazeConnection] Eye tracking signal live.", this);
                else
                    Debug.LogWarning("[VarjoGazeConnection] Eye tracking signal lost — gaze channels going " +
                                     "NoSignal. Expected if the headset came off; treat as suspect if not.", this);
            }

            if (!streaming)
            {
                // Nothing is arriving at all. A half-finished blink and a held
                // gaze/pupil value are both meaningless now, and carrying them
                // across a dropout would invent an interval that spans it.
                ResetBlinkState();
                _heldGazeDistance = float.NaN;
                _heldPupil = float.NaN;

                // Re-arm the unattended neutral-gaze capture. Same reasoning as
                // discarding the learned openness: a dropout most likely means
                // the headset changed heads, and the previous person's resting
                // gaze offset must not survive into the next person's data.
                _autoCaptureDone = false;
                _autoCapturePending = false;
                _capturingNeutralGaze = false;

                Publish(float.NaN, float.NaN, float.NaN);
                return;
            }

            TryAutoCaptureNeutralGaze();
            UpdateGazeDistance(count, now);
            UpdatePupilDiameter(count, now);
            Publish(ComputeInterBlinkInterval(), _heldGazeDistance, _heldPupil);
        }

        /// <summary>Starts the unattended neutral-gaze capture once, as soon as the
        /// tracker is calibrated and delivering. It then keeps re-arming
        /// (silently) until a window passes the steadiness gate, so it lands on
        /// a real fixation rather than whenever it happened to be asked.
        ///
        /// Waiting for IsGazeCalibrated matters: calibration is done in Varjo
        /// Base before the session, and a direction captured from Varjo's
        /// uncalibrated "best estimate" would bake that estimate's error into
        /// every angle the channel reports afterwards.</summary>
        private void TryAutoCaptureNeutralGaze()
        {
            if (!autoCaptureNeutralGaze || _autoCaptureDone || _capturingNeutralGaze) return;
            if (!VarjoEyeTracking.IsGazeCalibrated()) return;

            _autoCaptureDone = true;
            _autoCapturePending = true;
            BeginNeutralGazeCapture();
            Debug.Log($"[VarjoGazeConnection] Waiting for a steady fixation to set the neutral gaze direction " +
                      $"automatically (needs {neutralGazeWindowSeconds:0.##}s within " +
                      $"{maxNeutralGazeDispersionDegrees:0.#}°). No action needed.", this);
        }

        /// <summary>Seconds between the last two blink onsets — but never less
        /// than the time actually elapsed since the last blink.
        ///
        /// That second clause matters more than it looks. Reporting a frozen
        /// "2.8 s" while the participant has in fact not blinked for twenty
        /// seconds is not a stale reading, it is a WRONG one, and it is wrong
        /// in the direction that hides exactly the effect this channel exists
        /// to detect — suppressed blinking under high workload. Letting the
        /// value grow keeps it honest between blinks.</summary>
        private float ComputeInterBlinkInterval()
        {
            if (double.IsNaN(_lastBlinkOnsetSeconds)) return float.NaN; // no blink seen yet

            // Clocked off the last sample of ANY kind, not the last VALID one:
            // the detector itself runs on eyelid openness regardless of gaze
            // status, so pinning its elapsed time to the gaze ray would stall
            // the interval on a tracker that still sees the eyes perfectly
            // well but can no longer produce a calibrated direction.
            float sinceLast = (float)(_lastSampleDeviceSeconds - _lastBlinkOnsetSeconds);
            if (float.IsNaN(_lastCompletedIbiSeconds)) return Mathf.Max(0f, sinceLast);
            return Mathf.Max(_lastCompletedIbiSeconds, sinceLast);
        }

        /// <summary>Angular distance in degrees between where the eye is
        /// pointing now and the neutral fixation direction.
        ///
        /// Varjo reports the gaze ray RELATIVE TO HEAD POSE, which is what
        /// makes this measure meaningful in DELPHI: the participant is on a
        /// motion platform, and a world-referenced gaze vector would be
        /// dominated by the YAW3 swinging their head around rather than by
        /// anything their eyes did.
        ///
        /// Newest valid sample only — see the class summary for why this one
        /// is not averaged.</summary>
        private void UpdateGazeDistance(int count, double now)
        {
            for (int i = count - 1; i >= 0; i--)
            {
                if (_gaze[i].status != VarjoEyeTracking.GazeStatus.Valid) continue;
                _heldGazeDistance = Vector3.Angle(_neutralGazeForward, _gaze[i].gaze.forward);
                return;
            }

            // No valid sample this batch. That is the NORMAL state during a
            // blink — the eye is shut, so of course there is no gaze ray — and
            // punching a NaN hole in the channel every few seconds would be
            // worse than briefly holding the last angle. Beyond the timeout it
            // stops being a blink and starts being missing data.
            if (double.IsNaN(_lastGazeValidWall) || now - _lastGazeValidWall > signalTimeoutSeconds)
                _heldGazeDistance = float.NaN;
        }

        /// <summary>Mean pupil diameter across both eyes and across this
        /// batch, in millimetres.
        ///
        /// ZERO MEANS "NOT AVAILABLE", NOT "ZERO MILLIMETRES" — Varjo
        /// documents it that way, and a pupil is never 0 mm anyway. Averaging
        /// a missing eye in as a zero would halve the reading, which lands in
        /// the plausible 2–8 mm range and so would never look like a bug; it
        /// would just quietly corrupt the channel the optimizer is steering
        /// on. Each eye is therefore included only when it reports a real
        /// number.</summary>
        private void UpdatePupilDiameter(int count, double now)
        {
            float sum = 0f;
            int n = 0;

            for (int i = 0; i < count; i++)
            {
                var m = _measurements[i];
                if (m.leftPupilDiameterInMM > 0f)  { sum += m.leftPupilDiameterInMM;  n++; }
                if (m.rightPupilDiameterInMM > 0f) { sum += m.rightPupilDiameterInMM; n++; }
            }

            if (n > 0)
            {
                _heldPupil = sum / n;
                _lastPupilWall = now;
                return;
            }

            // Both pupils occluded — a blink again. Same hold-then-NaN rule.
            if (double.IsNaN(_lastPupilWall) || now - _lastPupilWall > signalTimeoutSeconds)
                _heldPupil = float.NaN;
        }

        private void Publish(float ibi, float gazeDistance, float pupil)
        {
            lock (_lock)
            {
                _interBlinkIntervalSeconds = ibi;
                _gazeDistanceDegrees = gazeDistance;
                _pupilDiameterMm = pupil;
            }
        }

        /// <summary>Called when the stream drops out. The learned open level is
        /// thrown away along with the blink state, because the most likely
        /// reason the signal stopped is the headset coming off — and the most
        /// likely reason it comes off is that the next person is about to put
        /// it on. Relearning costs about two seconds of wearing; carrying the
        /// previous participant's eye shape into the next participant's data
        /// costs the session.</summary>
        private void ResetBlinkState()
        {
            _eyesClosed = false;
            _lastBlinkOnsetSeconds = double.NaN;
            _lastCompletedIbiSeconds = float.NaN;
            _openEyeReference = float.NaN;
            _prevOpennessSeconds = double.NaN;
            _blinkDetectorArmed = false;
        }

        private void HandleKeys()
        {
            var kb = Keyboard.current;
            if (kb == null) return;
            if (kb[calibrationKey].wasPressedThisFrame) RequestCalibration();
            if (kb[neutralGazeKey].wasPressedThisFrame) CaptureNeutralGaze();
        }

        // ── Reader API (called from DELPHI's sampling thread) ────────────

        public float GetInterBlinkIntervalSeconds() { lock (_lock) { return _interBlinkIntervalSeconds; } }
        public float GetGazeDistanceDegrees()       { lock (_lock) { return _gazeDistanceDegrees; } }
        public float GetPupilDiameterMm()           { lock (_lock) { return _pupilDiameterMm; } }

        // ── Researcher API ───────────────────────────────────────────────

        /// <summary>Runs Varjo's gaze calibration. Takes over the
        /// participant's view, so never call this mid-trial.</summary>
        public void RequestCalibration()
        {
            if (!_available)
            {
                Debug.LogWarning("[VarjoGazeConnection] Cannot calibrate — eye tracking is not available.", this);
                return;
            }
            if (VarjoEyeTracking.RequestGazeCalibration(calibrationMode))
                Debug.Log($"[VarjoGazeConnection] Gaze calibration requested ({calibrationMode}). " +
                          "The neutral gaze direction captures itself once this finishes — nothing else to press.", this);
            else
                Debug.LogWarning("[VarjoGazeConnection] Gaze calibration request was refused by Varjo.", this);
        }

        /// <summary>Captures the participant's NEUTRAL GAZE DIRECTION: the
        /// fixation direction, which the Gaze channel measures distance from.
        /// Call with them seated, calibrated and looking down the road.
        ///
        /// Until this is called the reference is straight-ahead — which is a
        /// real answer rather than a placeholder, because Varjo's gaze ray is
        /// head-relative, so "straight ahead" means "aligned with the head".
        /// Capturing is still better: it absorbs the participant's individual
        /// resting eye offset instead of scoring it as a permanent saccade.</summary>
        public void CaptureNeutralGaze()
        {
            if (!_available)
            {
                Debug.LogWarning("[VarjoGazeConnection] Cannot capture a neutral gaze direction — eye tracking is not available.", this);
                return;
            }
            if (!VarjoEyeTracking.IsGazeCalibrated())
            {
                Debug.LogWarning("[VarjoGazeConnection] Capturing a neutral direction against an UNCALIBRATED gaze tracker. " +
                                 $"Run {calibrationKey} first, or the Gaze channel measures degrees from a guess.", this);
            }

            _autoCapturePending = false; // an explicit request is not a retrying background one
            BeginNeutralGazeCapture();
            Debug.Log($"[VarjoGazeConnection] Capturing neutral gaze direction over {neutralGazeWindowSeconds:0.##}s — " +
                      "participant should hold a steady forward fixation.", this);
        }

        /// <summary>Arms one capture window. Separate from CaptureNeutralGaze so
        /// the unattended path can silently re-arm after a window the eye
        /// moved through, without logging a line every second.</summary>
        private void BeginNeutralGazeCapture()
        {
            _capturingNeutralGaze = true;
            _neutralGazeAccumulator = Vector3.zero;
            _neutralGazeSamples = 0;
            _neutralGazeCaptureEndsAt = double.NaN; // anchored on the first sample — see AccumulateNeutralGaze
        }

        /// <summary>Blinks seen this session. Diagnostics only — the recorded
        /// channel is the interval, not the count.</summary>
        public int BlinkCount => _blinkCount;

        /// <summary>Whether a neutral direction has actually been captured, as opposed
        /// to the straight-ahead default.</summary>
        public bool HasCapturedNeutralGaze => _neutralGazeCaptured;
    }
}
