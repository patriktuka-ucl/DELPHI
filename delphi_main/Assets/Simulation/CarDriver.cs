using System.Collections.Generic;
using UnityEngine;

namespace Delphi.Simulation
{
    /// <summary>
    /// The driving-style parameters, normalised 0..1. Convention across EVERY
    /// axis: 0 = gentle, 1 = assertive — the optimiser sees a uniform direction.
    ///
    /// EVERYTHING the car does is DERIVED FROM THESE SIX AXES AND NOTHING ELSE —
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
    /// followDistance and takeoverProbability are part of the thesis parameter
    /// set but currently INERT — they need other traffic to act on, and the
    /// traffic system is deliberately parked while the core drive loop is being
    /// validated step by step.
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
        [Tooltip("Inert until the takeover system returns; kept for symmetry.")]
        public bool takeoverProbabilityOn = true;
        [Tooltip("Off = cruise exactly at the posted limit (zero margin).")]
        public bool speedBelowLimitOn     = true;

        [Header("Values (0 = gentle, 1 = assertive)")]
        [Range(0f, 1f)] public float accelerationJerk    = 0.5f;
        [Range(0f, 1f)] public float brakingJerk         = 0.5f;
        [Range(0f, 1f)] public float followDistance      = 0.5f;
        [Range(0f, 1f)] public float corneringSpeed      = 0.5f;
        [Range(0f, 1f)] public float takeoverProbability = 0.5f;
        [Range(0f, 1f)] public float speedBelowLimit     = 0.5f;

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

        [Header("Speed below limit range (km/h under the posted limit)")]
        [Tooltip("Gentle = a big margin below the limit, so this axis is " +
                 "inverted when mapped: 0 → belowLimitMaxKmh under, 1 → at " +
                 "the limit.")]
        public float belowLimitMinKmh = 0f;   // assertive: at the limit
        public float belowLimitMaxKmh = 15f;  // gentle: well under

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
        public float SpeedBelowLimitKmh => speedBelowLimitOn ? Mathf.Lerp(belowLimitMaxKmh, belowLimitMinKmh, speedBelowLimit) : 0f; // inverted
        public float TakeoverProbability => takeoverProbability;
    }

    /// <summary>
    /// The ego AV. Drives the Track in route space (see RouteVehicle): direct
    /// speed control at each style's own CONSTANT accel/decel magnitude,
    /// continuous curvature-based corner slowdown (the geometry IS the corner —
    /// no corner events), and cruise speed derived from the local posted limit
    /// minus the speedBelowLimit parameter. Every behaviour is derived from the
    /// six DrivingParameters axes and nothing else — no separate physical
    /// ceilings or auxiliary constants live on this class.
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
        [Header("Parameters (normally driven by the optimiser)")]
        public DrivingParameters parameters = new DrivingParameters();

        [Header("Debug")]
        public bool logStateChanges = false;

        // ── Runtime state ───────────────────────────────────────────────
        private bool  _waitingAtRedLight;
        private float _waitTimer;
        private readonly HashSet<TrackEvent> _servedLights = new();
        private bool _finished;

        public float CurrentSpeedKmh => Speed * 3.6f;

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
            LogTightestCurve();
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

        private void Update()
        {
            if (!track.IsReady || _finished) return;
            float dt = Time.deltaTime;

            // ── Holding at a red light ───────────────────────────────
            if (_waitingAtRedLight)
            {
                HoldStopped();
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
            float targetSpeed = ComputeTargetSpeed();
            StepSpeed(targetSpeed, parameters.AccelJerk, parameters.BrakeJerk, dt);

            // ── Advance, but NEVER across an unserved stop line ──────
            // The stop line is exactly where the marker sits. If this
            // frame's motion would carry the car past it, the car lands ON
            // it instead and the wait starts — that's what makes the stop
            // position exact regardless of speed, frame rate, or how gentle
            // the braking parameters are.
            float newS = S + Speed * dt;
            if (track.TryNextStop(_servedLights, out float stopS, out TrackEvent light)
                && newS >= stopS)
            {
                S = stopS;
                _waitingAtRedLight = true;
                _waitTimer = light.waitDuration;
                _servedLights.Add(light);
                HoldStopped();
                if (logStateChanges)
                    Debug.Log($"[CarDriver] Stopped at red light (s={stopS:F0}m), waiting {light.waitDuration}s.");
                PlaceOnRoute(dt);
                return;
            }
            S = newS;

            // ── End of the track ─────────────────────────────────────
            if (S >= track.TotalLength)
            {
                S = track.TotalLength;
                _finished = true;
                if (logStateChanges) Debug.Log("[CarDriver] Reached the end of the track.");
            }

            PlaceOnRoute(dt);
        }

        // ── Target speed from road knowledge ────────────────────────────
        private float ComputeTargetSpeed()
        {
            // Cruise: posted limit minus the style-dependent margin.
            float cruiseKmh = Mathf.Max(5f, track.SpeedLimitAt(S) - parameters.SpeedBelowLimitKmh);
            float target = cruiseKmh / 3.6f;

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
                target = Mathf.Max(0f, target - slowdownKmh / 3.6f);
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
                float distanceRemaining = Mathf.Max(0f, stopS - S);
                float approachLimit = Mathf.Sqrt(2f * parameters.BrakeJerk * distanceRemaining);
                target = Mathf.Min(target, approachLimit);
            }

            return Mathf.Max(0f, target);
        }
    }
}
