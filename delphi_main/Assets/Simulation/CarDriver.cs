using System;
using System.Collections.Generic;
using UnityEngine;

namespace Delphi.Simulation
{
    /// <summary>
    /// The driving-style parameters, normalised 0..1. Convention across EVERY
    /// axis: 0 = gentle, 1 = assertive — the optimiser sees a uniform direction.
    ///
    /// EVERYTHING the car does is DERIVED FROM THESE FOUR AXES AND NOTHING ELSE —
    /// no separate physical ceilings, no auxiliary "how early to brake"
    /// constants. Each axis maps, via ITS OWN Min/Max range below, directly to
    /// the physical quantity that governs its effect. accelerationJerk/
    /// brakingJerk in particular ARE the constant acceleration/deceleration
    /// MAGNITUDE (m/s²) used for the whole speed change — not a rate that ramps
    /// toward some separate shared ceiling, so style stays visible for the
    /// entire transition, not a brief transient on the way to an identical cap.
    ///
    /// All mapped values are computed PROPERTIES read fresh every frame — never
    /// cached — so dragging any slider mid-Play changes behaviour immediately.
    ///
    /// THE FOUR AXES ARE THE WHOLE MODEL:
    ///
    ///   accelerationJerk — m/s² used to speed UP, whatever the target is.
    ///   brakingJerk      — m/s² used to slow DOWN, and the rate the red-light
    ///                      and corner approach curves are derived from, so
    ///                      the car decelerates at exactly the rate its own
    ///                      approach maths assumed.
    ///   followDistance   — time headway to a lead vehicle.
    ///   corneringSpeed   — km/h cut from cruise for a tight curve, i.e. the
    ///                      corner's own speed limit.
    ///
    /// Target speed is chosen from those, and the car is then MOVED TOWARD it
    /// at the accel or brake magnitude — never snapped. Wanting 35 km/h in a
    /// corner while doing 50 does not teleport the speedometer; brakingJerk
    /// decides how long the change takes and is therefore what the change
    /// FEELS like. That easing is the entire behavioural signal this study
    /// manipulates, so nothing may ever set speed directly.
    ///
    /// followDistance is currently INERT — it needs other traffic to act on,
    /// and the traffic system is parked while the core drive loop is validated.
    /// It remains a real axis: the optimizer still searches it and it takes
    /// effect the moment traffic returns.
    /// </summary>
    [System.Serializable]
    public class DrivingParameters
    {
        [Header("Toggles — untick to remove a parameter's influence entirely")]
        [Tooltip("Off = acceleration changes are instant (no shaping on the " +
                 "accelerating side).")]
        public bool accelerationJerkOn    = true;
        [Tooltip("Off = braking changes are instant (no shaping on the " +
                 "braking side).")]
        public bool brakingJerkOn         = true;
        [Tooltip("Off = ignore any lead vehicle (headway control skipped).")]
        public bool followDistanceOn      = true;
        [Tooltip("Off = no curvature slowdown; corners taken at cruise speed.")]
        public bool corneringSpeedOn      = true;

        [Header("Values (0 = gentle, 1 = assertive)")]
        [Range(0f, 1f)] public float accelerationJerk    = 0.5f;
        [Range(0f, 1f)] public float brakingJerk         = 0.5f;
        [Range(0f, 1f)] public float followDistance      = 0.5f;
        [Range(0f, 1f)] public float corneringSpeed      = 0.5f;

        [Header("Acceleration magnitude range (m/s^2)")]
        [Tooltip("The CONSTANT acceleration used for the whole speed-up, not a " +
                 "ramp rate toward a separate ceiling — this range IS the ceiling. " +
                 "Gentle (0) accelerates gently the entire time; assertive (1) " +
                 "accelerates hard the entire time.")]
        public float accelJerkMin = 1.0f;
        public float accelJerkMax = 4.0f;

        [Header("Braking magnitude range (m/s^2)")]
        [Tooltip("The CONSTANT deceleration used for the whole slow-down/stop, " +
                 "not a ramp rate toward a separate ceiling — this range IS the " +
                 "ceiling. Also what the red-light approach curve is derived " +
                 "from directly (v = √(2·thisValue·distance)), so it's exactly " +
                 "self-consistent: the car decelerates at precisely the rate the " +
                 "approach curve assumed, arriving at (very close to) 0 right at " +
                 "the line — no separate 'how early to brake' constant needed.")]
        public float brakeJerkMin = 1.0f;
        public float brakeJerkMax = 5.0f;

        [Header("Follow distance range — time headway (s)")]
        [Tooltip("Gentle = larger gap, so this axis is inverted when mapped.")]
        public float followMin = 0.8f;  // assertive: close
        public float followMax = 2.5f;  // gentle: far

        [Header("Cornering range — km/h shaved off cruise speed in a tight turn")]
        [Tooltip("A plain scalar: how many km/h to cut from the target speed when " +
                 "taking a tight curve (a ~10 m radius bend gets the full cut; " +
                 "wider curves cut proportionally less, scaling to 0 on a straight " +
                 "road). Gentle = a big margin below the limit, so this axis is " +
                 "inverted when mapped, same as the other km/h-based range below.")]
        public float cornerSlowdownMinKmh = 5f;   // assertive: barely slows for curves
        public float cornerSlowdownMaxKmh = 30f;  // gentle: slows a lot for curves

        // "Off" sentinel: an acceleration/deceleration magnitude high enough
        // that MoveTowards reaches any plausible target speed within one frame
        // — i.e. instant, unstyled snapping.
        private const float InstantMagnitude = 1e5f;

        public float AccelJerk          => accelerationJerkOn ? Mathf.Lerp(accelJerkMin, accelJerkMax, accelerationJerk) : InstantMagnitude;
        public float BrakeJerk          => brakingJerkOn ? Mathf.Lerp(brakeJerkMin, brakeJerkMax, brakingJerk) : InstantMagnitude;
        public float FollowHeadway      => Mathf.Lerp(followMax, followMin, followDistance);       // inverted
        /// <summary>km/h to cut from the target speed at a tight curve (see
        /// CarDriver.ComputeTargetSpeed — wider curves cut proportionally less).
        /// 0=gentle → cornerSlowdownMaxKmh (cuts a lot), 1=assertive →
        /// cornerSlowdownMinKmh (barely cuts).</summary>
        public float CornerSlowdownKmh => Mathf.Lerp(cornerSlowdownMaxKmh, cornerSlowdownMinKmh, corneringSpeed); // inverted
    }

    /// <summary>One entry per driving-style axis: key, display labels, on/off
    /// + get/set, and the physical value each end of the 0..1 style axis maps
    /// to (already oriented — PhysicalAtZero/PhysicalAtOne encode any
    /// inversion internally, e.g. follow distance and cornering speed are
    /// both "gentle = larger physical number").</summary>
    public sealed class DrivingParameterInfo
    {
        public readonly string Key;
        public readonly string Label;       // full name, e.g. "Follow distance"
        public readonly string ShortLabel;  // abbreviated, e.g. "Follow dist"
        public readonly string Unit;
        public readonly Func<DrivingParameters, float> Get;
        public readonly Action<DrivingParameters, float> Set;
        public readonly Func<DrivingParameters, bool> IsOn;
        public readonly Func<DrivingParameters, float> PhysicalAtZero;
        public readonly Func<DrivingParameters, float> PhysicalAtOne;

        public DrivingParameterInfo(string key, string label, string shortLabel, string unit,
            Func<DrivingParameters, float> get, Action<DrivingParameters, float> set,
            Func<DrivingParameters, bool> isOn,
            Func<DrivingParameters, float> physicalAtZero, Func<DrivingParameters, float> physicalAtOne)
        {
            Key = key; Label = label; ShortLabel = shortLabel; Unit = unit;
            Get = get; Set = set; IsOn = isOn;
            PhysicalAtZero = physicalAtZero; PhysicalAtOne = physicalAtOne;
        }
    }

    /// <summary>Single source of truth for the driving-parameter axis list —
    /// every consumer (trial bookkeeping, CSV/JSON export, the researcher
    /// dashboard, the FreePlay panel) enumerates this instead of each keeping
    /// its own copy of the four keys/labels.</summary>
    public static class DrivingParameterRegistry
    {
        public static readonly DrivingParameterInfo[] All =
        {
            new DrivingParameterInfo("accelerationJerk", "Acceleration", "Acceleration", "m/s^2",
                p => p.accelerationJerk, (p, v) => p.accelerationJerk = v, p => p.accelerationJerkOn,
                p => p.accelJerkMin, p => p.accelJerkMax),
            new DrivingParameterInfo("brakingJerk", "Braking", "Braking", "m/s^2",
                p => p.brakingJerk, (p, v) => p.brakingJerk = v, p => p.brakingJerkOn,
                p => p.brakeJerkMin, p => p.brakeJerkMax),
            new DrivingParameterInfo("followDistance", "Follow distance", "Follow dist",
                "s headway (inverted: 0=far/gentle, 1=close/assertive)",
                p => p.followDistance, (p, v) => p.followDistance = v, p => p.followDistanceOn,
                p => p.followMax, p => p.followMin),
            new DrivingParameterInfo("corneringSpeed", "Cornering speed", "Corner spd",
                "km/h cut in the tightest realistic turn (inverted: 0=cuts a lot/gentle, 1=barely cuts/assertive)",
                p => p.corneringSpeed, (p, v) => p.corneringSpeed = v, p => p.corneringSpeedOn,
                p => p.cornerSlowdownMaxKmh, p => p.cornerSlowdownMinKmh),
        };

        public static readonly string[] Keys = BuildKeys();

        private static string[] BuildKeys()
        {
            var keys = new string[All.Length];
            for (int i = 0; i < All.Length; i++) keys[i] = All[i].Key;
            return keys;
        }

        public static DrivingParameterInfo ByKey(string key)
        {
            foreach (var info in All)
                if (info.Key == key) return info;
            return null;
        }
    }

    /// <summary>
    /// The ego AV. Drives the Track in route space (see RouteVehicle): direct
    /// speed control at each style's own CONSTANT accel/decel magnitude,
    /// continuous curvature-based corner slowdown (the geometry IS the corner —
    /// no corner events), and cruise speed taken straight from the local posted
    /// limit. Every behaviour is derived from the four DrivingParameters axes
    /// and nothing else — no separate physical ceilings or auxiliary constants
    /// live on this class.
    ///
    /// RED LIGHTS — the stop-line guarantee. Wherever the RedLight marker sits
    /// is treated like the line painted on the pavement:
    ///   1. On approach, target speed is capped by v = √(2·brakingJerk·d) —
    ///      EXACTLY the deceleration the car will actually use (StepSpeed now
    ///      brakes at that same constant rate, no jerk-ramp lag to
    ///      approximate), so it brakes smoothly and arrives at (very close to)
    ///      0 right at the line, regardless of style.
    ///   2. The frame the car's motion WOULD carry it across the line, its
    ///      position is clamped exactly ONTO the line and the wait begins.
    /// </summary>
    public class CarDriver : RouteVehicle
    {
        /// <summary>Which term actually decided this frame's target speed —
        /// i.e. WHAT is holding the car back right now. Purely diagnostic
        /// (nothing in the driving logic reads it), but it's the difference
        /// between seeing "the car won't go above 35" and knowing why.</summary>
        public enum SpeedLimiter { Parked, RedLight, Corner, Cruise }

        [Header("Parameters (normally driven by the optimiser)")]
        public DrivingParameters parameters = new DrivingParameters();

        [Header("Parking")]
        [Tooltip("Car starts parked (stationary) rather than driving off the " +
                 "instant Play begins. SessionController calls ResumeDriving() " +
                 "when the experiment procedure is ready for this condition's " +
                 "drive to start, and RequestPark() to send the car back to the " +
                 "track's Park marker (a TrackEventKind.Park) between " +
                 "conditions. Same mechanism either way — parked is parked.")]
        public bool startParked = true;

        [Header("Debug")]
        public bool logStateChanges = false;

        // ── Runtime state ───────────────────────────────────────────────
        private bool  _waitingAtRedLight;
        private float _waitTimer;
        private readonly HashSet<TrackEvent> _servedLights = new();
        private bool _finished;
        private bool _isParked;
        private bool _headingToPark;
        private bool _parkingInPlace; // braking to a halt where it stands, no marker to aim at
        private TrackEvent _targetPark; // the specific marker this park request is aimed at
        private bool _headingToPullover;
        private float _pulloverTargetS; // computed on demand by RequestPullover, not hand-authored
        private float _settleSpeed;     // where ComputeTargetSpeed says this manoeuvre ENDS

        public float CurrentSpeedKmh => Speed * 3.6f;

        // ── Motion state, published for the seat cue and the researcher UI ──
        // Everything here is what the car ALREADY KNOWS about its own intent,
        // published in plain physical units. Nothing downstream has to
        // differentiate Speed or the transform to recover it — doing that was
        // the source of the frame-to-frame jitter the rig turned into a jerk.

        /// <summary>Speed the car is currently trying to reach (m/s).</summary>
        public float TargetSpeed { get; private set; }
        public float TargetSpeedKmh => TargetSpeed * 3.6f;

        /// <summary>The speed the CURRENT MANOEUVRE ends at (m/s) — not
        /// necessarily this frame's target. On a red-light or park approach
        /// the target follows the √(2·a·d) curve steadily downward, so it is a
        /// moving waypoint, not a destination; the destination is 0. Everywhere
        /// else the two are the same.</summary>
        public float SettleSpeed { get; private set; }

        /// <summary>SettleSpeed − Speed (m/s, signed): how much speed change
        /// this manoeuvre still has left to do. Positive = still speeding up,
        /// negative = still slowing down, ~0 = settled. Deliberately measured
        /// against SettleSpeed and not TargetSpeed: on a red-light approach the
        /// car tracks its moving target so closely that a gap-to-target would
        /// read ~0 for the whole descent and claim nothing is happening, while
        /// the car is in fact braking hard from 40 km/h to a standstill.</summary>
        public float SpeedGap { get; private set; }

        /// <summary>The acceleration actually applied this frame (m/s²,
        /// signed; + = speeding up), from RouteVehicle.AppliedAccel — exact,
        /// measured inside the speed step itself, and bounded by construction
        /// (MoveTowards can never move further than magnitude × dt, so this
        /// can never spike above the style's own rate).</summary>
        public float CommandedAccel { get; private set; }

        /// <summary>How fast the car is changing heading (rad/s, signed;
        /// + = turning right). Derived from road geometry × speed
        /// (Track.SignedCurvatureAt), NOT from the transform's rotation delta
        /// — the transform chases its heading through a Slerp, so differencing
        /// it yields noise rather than turn rate.</summary>
        public float YawRateRadPerSec { get; private set; }

        /// <summary>What's governing the target speed this frame.</summary>
        public SpeedLimiter Limiter { get; private set; } = SpeedLimiter.Parked;

        /// <summary>True whenever the car is sitting stopped and NOT counting
        /// down toward an automatic resume — either it hasn't been told to
        /// start yet, or it arrived at the Park marker. False while merely
        /// waiting out a timed StopAndGo light.</summary>
        public bool IsParked => _isParked;
        /// <summary>True from RequestPark() until the car actually arrives at
        /// the Park marker and IsParked becomes true.</summary>
        public bool IsHeadingToPark => _headingToPark;
        /// <summary>True from RequestPullover() until the car actually comes
        /// to a halt at the computed pullover point.</summary>
        public bool IsHeadingToPullover => _headingToPullover;

        private void Start()
        {
            if (track.IsReady) OnTrackReady();
            else track.OnTrackReady += OnTrackReady;
        }

        private void OnTrackReady()
        {
            PlaceAt(0f, 0f);
            _servedLights.Clear();
            _finished = false;
            _isParked = startParked;
            _headingToPark = false;
            _headingToPullover = false;
            LogTightestCurve();
        }

        /// <summary>Experiment procedure: let the car start/resume driving —
        /// used both for the very first drive and for leaving a Park stop.
        /// No-op if it's already driving.
        ///
        /// Also clears _finished. Without that, a car that ever ran off the
        /// physical end of the track once (see the "reached the END of the
        /// track" branch in Update()) stays permanently inert for the REST OF
        /// THE SESSION: _finished makes Update() a no-op every frame from
        /// then on, and nothing but this method and OnTrackReady() ever
        /// clears it. A short/looping-unaware track WILL hit that during a
        /// real multi-condition session — resuming driving has to actually
        /// mean the car can drive again.</summary>
        public void ResumeDriving()
        {
            _finished = false;
            if (!_isParked) return;
            _isParked = false;
            _headingToPark = false;
            _headingToPullover = false;
            if (logStateChanges) Debug.Log("[CarDriver] Resuming driving.");
        }

        /// <summary>Experiment procedure: send the car to the track's Park
        /// marker — it keeps driving (braking smoothly on approach, honouring
        /// any red light in between) until it reaches that exact line, then
        /// holds there until ResumeDriving() is called. No-op if already
        /// parked/heading there, and warns once if the track has no Park
        /// marker (nothing to head to).
        ///
        /// Also clears _finished — see ResumeDriving's doc. Without this, a
        /// RequestPark() called after the car ran off the end of the track
        /// sets _headingToPark/_parkingInPlace but Update() never processes
        /// either (it no-ops at the top on _finished), so the car sits
        /// forever in "heading to park" with nothing actually happening.</summary>
        public void RequestPark()
        {
            _finished = false;
            if (_isParked || _headingToPark || _parkingInPlace) return;
            if (!track.TryGetParkAhead(S, out _targetPark))
            {
                // No marker to aim at. Stopping WHERE IT STANDS is much safer
                // than carrying on: the experiment procedure parks between
                // conditions so the next baseline is recorded stationary, and
                // a car still driving through that baseline quietly ruins the
                // reference the whole Implicit objective is measured against.
                Debug.LogWarning("[CarDriver] RequestPark(): no Park marker at or ahead of " +
                                 $"s={S:F0}m — braking to a halt in place instead. Add a Park marker " +
                                  "ahead of the car to control WHERE the participant is parked.");
                _parkingInPlace = true;
                return;
            }
            _headingToPark = true;
            if (logStateChanges) Debug.Log($"[CarDriver] Heading to park at s={_targetPark.S:F0}m.");
        }

        /// <summary>Real-time pullover: finds the nearest point ahead that's
        /// safe to stop at (clear of any corner and of every StopAndGo/Park
        /// marker's own meaning — see Track.TryFindSafeStoppingPoint) and
        /// brakes smoothly onto it, using the same approach-curve math as a
        /// red light or a Park marker.
        ///
        /// Unlike RequestPark, the target is COMPUTED, not hand-authored —
        /// this is for stopping wherever the car happens to be when a drive
        /// ends, rather than requiring the route to be pre-timed to land on a
        /// fixed marker. This track has no lane/shoulder geometry, so
        /// "pulling over" means a smooth in-lane stop, never a lateral
        /// curb-side offset — it can never mount a curb because the car
        /// never leaves the line it already drives on.
        ///
        /// No-op if already parked/heading somewhere. Falls back to braking
        /// to a halt in place (same fallback RequestPark uses) if nothing
        /// safe is found within searchAheadMeters.</summary>
        public void RequestPullover(float maxAbsCurvature = 0.02f, float searchAheadMeters = 400f)
        {
            _finished = false;
            if (_isParked || _headingToPark || _headingToPullover || _parkingInPlace) return;

            if (track.TryFindSafeStoppingPoint(S, maxAbsCurvature, searchAheadMeters, out float s))
            {
                _pulloverTargetS = s;
                _headingToPullover = true;
                if (logStateChanges) Debug.Log($"[CarDriver] Pulling over at s={s:F0}m.");
            }
            else
            {
                Debug.LogWarning("[CarDriver] RequestPullover(): no safe stopping point found within " +
                                 $"{searchAheadMeters:F0}m ahead of s={S:F0}m — braking to a halt in " +
                                  "place instead.");
                _parkingInPlace = true;
            }
        }

        /// <summary>Instantly teleport back to the track's start (its first
        /// Park marker, or s=0 if none) and mark it parked there — no
        /// physical drive, no elapsed time. This is what SessionController
        /// calls at the end of every condition's drive: the evaluation
        /// questionnaire covers the whole screen anyway, so resetting the car
        /// in place while it's up is invisible to the participant, and
        /// doesn't depend on the car ever physically reaching a marker
        /// somewhere down the track — the track only needs to be long enough
        /// for ONE drive, not the whole session's cumulative distance.</summary>
        public void ResetToStart()
        {
            float startS = track.TryGetPark(out var startPark) ? startPark.S : 0f;
            PlaceAt(startS, 0f);
            _servedLights.Clear();
            _finished = false;
            _headingToPark = false;
            _headingToPullover = false;
            _parkingInPlace = false;
            _targetPark = null;
            _isParked = true;
            HoldStopped();
            if (logStateChanges) Debug.Log($"[CarDriver] Reset to start (s={startS:F0}m).");
        }

        /// <summary>Immediate halt wherever the car currently is — no travel
        /// to the Park marker, no approach braking curve. For emergencies:
        /// SessionController.EmergencyStop calls this directly.</summary>
        public void EmergencyHalt()
        {
            _waitingAtRedLight = false;
            _headingToPark = false;
            _headingToPullover = false;
            _isParked = true;
            HoldStopped();
            if (logStateChanges) Debug.Log($"[CarDriver] Emergency halt at s={S:F0}m.");
        }

        /// <summary>Freeze in place for a brief in-drive pause (the per-
        /// iteration rating questionnaire) — same instant halt as
        /// EmergencyHalt, just not logged/framed as an emergency. Deliberately
        /// NOT RequestPark(): a Park marker can now be far down the track, and
        /// travelling to it mid-iteration is exactly the "car keeps moving
        /// while I answer" behaviour this exists to avoid.</summary>
        public void FreezeInPlace()
        {
            _waitingAtRedLight = false;
            _headingToPark = false;
            _headingToPullover = false;
            _isParked = true;
            HoldStopped();
            if (logStateChanges) Debug.Log($"[CarDriver] Frozen in place at s={S:F0}m.");
        }

        // A "Turn" TrackEvent is only a LABEL — it has zero effect on physics.
        // The actual curve corneringSpeed reacts to comes ENTIRELY from how
        // sharply the spline itself bends. This one-time scan settles, in the
        // Console, whether there's any real bend for the parameter to act on —
        // rather than leaving that as a guess after a "no visible effect" report.
        private void LogTightestCurve()
        {
            const int samples = 400;
            float maxCurvature = 0f;
            for (int i = 0; i <= samples; i++)
                maxCurvature = Mathf.Max(maxCurvature, track.CurvatureAt(track.TotalLength * i / samples));

            if (maxCurvature < 0.001f) // tighter radius than ~1000m never appears
                Debug.LogWarning("[CarDriver] This road is straight (or only very gently bent) " +
                    "everywhere — corneringSpeed has nothing to act on. A 'Turn' marker only LABELS " +
                    "a stretch for later analysis; the actual bend has to come from dragging the " +
                    "spline's own knots sideways with Unity's Spline tool. Bend the road, then retest.");
            else
                Debug.Log($"[CarDriver] Tightest curve on this road: ~{1f / maxCurvature:F0}m radius.");
        }

        /// <summary>Publish motion state for a frame where the car isn't
        /// driving: nothing to accelerate toward, no heading change. Called on
        /// EVERY non-driving path so the seat cue and the researcher UI can
        /// never read values left over from the last frame the car moved.</summary>
        private void PublishStationary(SpeedLimiter limiter)
        {
            TargetSpeed = 0f;
            SettleSpeed = 0f;
            SpeedGap = 0f;
            CommandedAccel = 0f;
            YawRateRadPerSec = 0f;
            Limiter = limiter;
        }

        private void Update()
        {
            if (!track.IsReady || _finished)
            {
                if (_finished) PublishStationary(SpeedLimiter.Parked);
                return;
            }
            float dt = Time.deltaTime;

            // ── Parked — not driving at all until ResumeDriving() ────
            // Covers BOTH the initial startParked state and having arrived
            // at the Park marker; either way there's nothing to compute.
            if (_isParked)
            {
                HoldStopped();
                PublishStationary(SpeedLimiter.Parked);
                return;
            }

            // ── Holding at a red light ───────────────────────────────
            if (_waitingAtRedLight)
            {
                HoldStopped();
                PublishStationary(SpeedLimiter.RedLight);
                _waitTimer -= dt;
                if (_waitTimer <= 0f)
                {
                    _waitingAtRedLight = false;
                    if (logStateChanges) Debug.Log("[CarDriver] Pulling away from red light.");
                }
                return;
            }

            // ── Target speed this frame, then move toward it at THIS
            // style's own constant accel/decel magnitude ─────────────
            float targetSpeed = ComputeTargetSpeed(dt);  // also sets Limiter and _settleSpeed
            StepSpeed(targetSpeed, parameters.AccelJerk, parameters.BrakeJerk, dt);
            PublishDrivingState(targetSpeed);

            // Parking with nowhere to aim: brake at this style's own rate and
            // latch parked once stopped, so the state the session reads
            // (IsParked) matches what the car is actually doing.
            if (_parkingInPlace && Speed <= 0.01f)
            {
                _parkingInPlace = false;
                _isParked = true;
                HoldStopped();
                PublishStationary(SpeedLimiter.Parked);
                if (logStateChanges) Debug.Log($"[CarDriver] Parked in place (s={S:F0}m).");
                PlaceOnRoute(dt);
                return;
            }

            // ── Advance, but NEVER across an unserved stop line ──────
            // The stop line is exactly where the marker sits. If this
            // frame's motion would carry the car past it, the car lands ON
            // it instead and the wait starts — that's what makes the stop
            // position exact regardless of speed, frame rate, or how gentle
            // the braking parameters are. A red light always takes priority
            // over the park destination if it happens to sit closer.
            float newS = S + Speed * dt;
            if (track.TryNextStop(_servedLights, out float stopS, out TrackEvent light)
                && newS >= stopS)
            {
                S = stopS;
                _waitingAtRedLight = true;
                _waitTimer = light.waitDuration;
                _servedLights.Add(light);
                HoldStopped();
                PublishStationary(SpeedLimiter.RedLight);
                if (logStateChanges)
                    Debug.Log($"[CarDriver] Stopped at red light (s={stopS:F0}m), waiting {light.waitDuration}s.");
                PlaceOnRoute(dt);
                return;
            }

            // ── Arriving at the requested Park marker ────────────────
            // Same exact-line landing as a red light, but this one holds
            // indefinitely (IsParked) instead of counting down a timer.
            if (_headingToPark && _targetPark != null && newS >= _targetPark.S)
            {
                S = _targetPark.S;
                _isParked = true;
                _headingToPark = false;
                HoldStopped();
                PublishStationary(SpeedLimiter.Parked);
                if (logStateChanges) Debug.Log($"[CarDriver] Parked (s={_targetPark.S:F0}m).");
                _targetPark = null;
                PlaceOnRoute(dt);
                return;
            }

            // ── Arriving at the computed pullover point ──────────────
            // Same exact-line landing again, aimed at RequestPullover's
            // on-demand target instead of a hand-authored marker.
            if (_headingToPullover && newS >= _pulloverTargetS)
            {
                S = _pulloverTargetS;
                _isParked = true;
                _headingToPullover = false;
                HoldStopped();
                PublishStationary(SpeedLimiter.Parked);
                if (logStateChanges) Debug.Log($"[CarDriver] Pulled over (s={_pulloverTargetS:F0}m).");
                PlaceOnRoute(dt);
                return;
            }

            S = newS;

            // ── End of the track ─────────────────────────────────────
            // Latch PARKED, not just "finished": parked is a state the rest of
            // the system understands (IsParked, ResumeDriving), whereas the
            // old finished-only flag silently made Update() a no-op — the car
            // froze, and every later parameter set the optimizer applied had
            // no effect on a vehicle that could no longer move.
            if (S >= track.TotalLength)
            {
                S = track.TotalLength;
                _finished = true;
                _headingToPark = false;
                _headingToPullover = false;
                _parkingInPlace = false;
                _targetPark = null;
                _isParked = true;
                HoldStopped();
                PublishStationary(SpeedLimiter.Parked);
                Debug.LogWarning($"[CarDriver] Reached the END of the track ({track.TotalLength:F0} m) and " +
                                  "parked. If a condition is still running, the car will now stay " +
                                  "stationary for the rest of it — extend the track so one pass covers " +
                                  "the full driving phase.");
            }

            PlaceOnRoute(dt);
        }

        /// <summary>Publish motion state for a frame where the car IS driving.
        /// Called AFTER StepSpeed so AppliedAccel and Speed are both this
        /// frame's. CommandedAccel is clamped to the relevant axis's own range
        /// max, so the "jerk off = instant" sentinel (an absurd 1e5 used purely
        /// to make MoveTowards snap in one frame) never leaks out as a physical
        /// acceleration — off reads as "the hardest this style set allows"
        /// rather than as a number that would peg the motion rig.</summary>
        private void PublishDrivingState(float targetSpeed)
        {
            TargetSpeed = targetSpeed;
            SettleSpeed = _settleSpeed;
            SpeedGap = _settleSpeed - Speed;

            float cap = AppliedAccel >= 0f ? parameters.accelJerkMax : parameters.brakeJerkMax;
            CommandedAccel = Mathf.Clamp(AppliedAccel, -cap, cap);

            YawRateRadPerSec = track.SignedCurvatureAt(S) * Speed;
        }

        // ── Target speed from road knowledge ────────────────────────────
        // Also records WHICH term won (Limiter) — every branch that lowers
        // `target` claims it, so the last claim standing is by construction
        // the binding constraint. That readout is the whole answer to "why
        // won't the car go faster than X".
        private float ComputeTargetSpeed(float dt)
        {
            // Cruise: the posted limit. There is no style margin any more —
            // "how far under the limit to sit" was removed as an axis, so the
            // car drives the road's limit and the only things that pull it
            // below are a corner, a red light or a lead vehicle.
            float cruiseKmh = Mathf.Max(5f, track.SpeedLimitAt(S));
            float target = cruiseKmh / 3.6f;
            Limiter = SpeedLimiter.Cruise;
            _settleSpeed = target;

            // Corners: continuous curvature limit AT the car's current position
            // (the geometry IS the corner — no lookahead, no corner events).
            float curvature = parameters.corneringSpeedOn ? track.CurvatureAt(S) : 0f;
            if (curvature > 0.0001f)
            {
                // curvature ≈ 1/radius; a ~10 m-or-tighter bend gets the FULL
                // km/h cut, scaling linearly down to 0 as the curve widens
                // toward straight. A plain scalar subtraction, not a percentage.
                const float tightCornerRadius = 10f;
                float narrowness = Mathf.Clamp01(curvature * tightCornerRadius);
                float slowdownKmh = parameters.CornerSlowdownKmh * narrowness;
                if (slowdownKmh > 0.1f)
                {
                    target = Mathf.Max(0f, target - slowdownKmh / 3.6f);
                    Limiter = SpeedLimiter.Corner;
                    _settleSpeed = target;
                }
            }

            // Parking in place: commanded to zero, braked at this style's own
            // rate by StepSpeed (same as any other slow-down).
            if (_parkingInPlace)
            {
                Limiter = SpeedLimiter.Parked;
                _settleSpeed = 0f;
                return 0f;
            }

            // Red light: anticipatory braking curve toward the stop line —
            // v = √(2·brakingJerk·distanceRemaining). brakingJerk here is the
            // EXACT constant deceleration StepSpeed will actually apply (no
            // ramp-up lag to approximate), so this is precise, self-consistent
            // kinematics: the car reaches (very close to) 0 exactly at the
            // line, and gentle (small brakingJerk) naturally needs — and gets —
            // a proportionally longer runway than assertive. Distance clamps at
            // 0, so even if the line is somehow reached at speed the commanded
            // target is 0 (the hard position clamp in Update is the actual
            // overshoot guarantee).
            if (track.TryNextStop(_servedLights, out float stopS, out _))
            {
                // ONE FRAME OF LOOK-AHEAD, and it is not a fudge factor.
                //
                // The curve answers "how fast may I be going, d metres out".
                // Evaluated at where the car is NOW, the speed it authorises
                // is already too high by the time the car has travelled this
                // frame's Speed*dt — so the car tracks a permanently stale
                // curve and arrives at the line still carrying roughly
                // BrakeJerk*dt of speed. That residual is what the stop-line
                // clamp below then deletes in a single frame, and that delete
                // is the snap. Aiming the curve at next frame's position
                // removes the residual at its source, so the car reaches the
                // line already at zero and the clamp becomes a no-op backstop.
                float distanceRemaining = Mathf.Max(0f, stopS - (S + Speed * dt));
                float approachLimit = Mathf.Sqrt(2f * parameters.BrakeJerk * distanceRemaining);
                if (approachLimit < target)
                {
                    target = approachLimit;
                    Limiter = SpeedLimiter.RedLight;
                    // The approach curve is a moving waypoint; the manoeuvre
                    // ends at the stop line, stationary.
                    _settleSpeed = 0f;
                }
            }

            // Same anticipatory braking curve, aimed at the Park marker
            // instead — only active once RequestPark() has been called.
            if (_headingToPark && _targetPark != null)
            {
                float distanceRemaining = Mathf.Max(0f, _targetPark.S - (S + Speed * dt));
                float approachLimit = Mathf.Sqrt(2f * parameters.BrakeJerk * distanceRemaining);
                if (approachLimit < target)
                {
                    target = approachLimit;
                    Limiter = SpeedLimiter.Parked;
                    _settleSpeed = 0f;
                }
            }

            // Same anticipatory braking curve again, aimed at the on-demand
            // pullover point — only active once RequestPullover() has found one.
            if (_headingToPullover)
            {
                float distanceRemaining = Mathf.Max(0f, _pulloverTargetS - (S + Speed * dt));
                float approachLimit = Mathf.Sqrt(2f * parameters.BrakeJerk * distanceRemaining);
                if (approachLimit < target)
                {
                    target = approachLimit;
                    Limiter = SpeedLimiter.Parked;
                    _settleSpeed = 0f;
                }
            }

            return Mathf.Max(0f, target);
        }
    }
}
