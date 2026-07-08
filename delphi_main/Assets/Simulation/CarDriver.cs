using System.Collections.Generic;
using UnityEngine;

namespace Delphi.Simulation
{
    /// <summary>
    /// The driving-style parameters, normalised 0..1. Convention across
    /// EVERY axis: 0 = gentle, 1 = assertive — the optimiser sees a uniform
    /// direction. Physical ranges grounded in the AV comfort literature we
    /// reviewed; tune the min/max fields during piloting.
    ///
    /// All mapped values are computed PROPERTIES read fresh every frame —
    /// never cached — so dragging any slider mid-Play changes behaviour
    /// immediately.
    ///
    /// followDistance and takeoverProbability are part of the thesis
    /// parameter set but currently INERT — they need other traffic to act
    /// on, and the traffic system is deliberately parked while the core
    /// drive loop is being validated step by step.
    /// </summary>
    [System.Serializable]
    public class DrivingParameters
    {
        [Header("Toggles — untick to remove a parameter's influence entirely")]
        [Tooltip("Off = acceleration changes are instant (no jerk shaping " +
                 "on the accelerating side).")]
        public bool accelerationJerkOn    = true;
        [Tooltip("Off = braking changes are instant (no jerk shaping on the " +
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

        [Header("Acceleration jerk range (m/s^3)")]
        public float accelJerkMin = 0.3f;
        public float accelJerkMax = 2.0f;

        [Header("Braking jerk range (m/s^3)")]
        public float brakeJerkMin = 0.3f;
        public float brakeJerkMax = 2.5f;

        [Header("Follow distance range — time headway (s)")]
        [Tooltip("Gentle = larger gap, so this axis is inverted when mapped.")]
        public float followMin = 0.8f;  // assertive: close
        public float followMax = 2.5f;  // gentle: far

        [Header("Cornering range — fraction of comfortable lateral accel")]
        public float cornerMin = 0.6f;  // gentle: slow in
        public float cornerMax = 1.15f; // assertive: pushes past comfy

        [Header("Speed below limit range (km/h under the posted limit)")]
        [Tooltip("Gentle = a big margin below the limit, so this axis is " +
                 "inverted when mapped: 0 → belowLimitMaxKmh under, 1 → at " +
                 "the limit.")]
        public float belowLimitMinKmh = 0f;   // assertive: at the limit
        public float belowLimitMaxKmh = 15f;  // gentle: well under

        // Effectively "no jerk limit" — high enough that MoveTowards reaches
        // any plausible target acceleration within one frame.
        private const float InstantJerk = 1e5f;

        public bool AnyOn => accelerationJerkOn || brakingJerkOn || followDistanceOn ||
                             corneringSpeedOn || takeoverProbabilityOn || speedBelowLimitOn;

        public float AccelJerk          => accelerationJerkOn ? Mathf.Lerp(accelJerkMin, accelJerkMax, accelerationJerk) : InstantJerk;
        public float BrakeJerk          => brakingJerkOn ? Mathf.Lerp(brakeJerkMin, brakeJerkMax, brakingJerk) : InstantJerk;
        public float FollowHeadway      => Mathf.Lerp(followMax, followMin, followDistance);       // inverted
        public float CornerFactor       => Mathf.Lerp(cornerMin, cornerMax, corneringSpeed);
        public float SpeedBelowLimitKmh => speedBelowLimitOn ? Mathf.Lerp(belowLimitMaxKmh, belowLimitMinKmh, speedBelowLimit) : 0f; // inverted
        public float TakeoverProbability => takeoverProbability;
    }

    /// <summary>
    /// The ego AV. Drives the Track in route space (see RouteVehicle):
    /// jerk-limited speed control, continuous curvature-based corner
    /// slowdown (the geometry IS the corner — no corner events), and cruise
    /// speed derived from the local posted limit minus the speedBelowLimit
    /// parameter.
    ///
    /// RED LIGHTS — the stop-line guarantee. Wherever the RedLight marker
    /// sits is treated like the line painted on the pavement:
    ///   1. On approach, target speed is capped by v = √(2·a·d) toward the
    ///      line, so the car brakes smoothly and arrives slow.
    ///   2. The frame the car's motion WOULD carry it across the line, its
    ///      position is clamped exactly ONTO the line and the wait begins.
    /// The old version instead let the car drive past and only "noticed"
    /// once speed dropped below a threshold, then warped it back — that's
    /// the stop-past-then-snap-back the researcher saw. The clamp makes
    /// overshoot structurally impossible.
    /// </summary>
    public class CarDriver : RouteVehicle
    {
        [Header("Parameters (normally driven by the optimiser)")]
        public DrivingParameters parameters = new DrivingParameters();

        [Header("Physical ceilings (fixed safety bounds, not optimised)")]
        public float maxAccel = 3.0f;  // m/s^2
        public float maxDecel = 4.0f;  // m/s^2

        [Header("Cornering")]
        [Tooltip("Baseline comfortable lateral acceleration (m/s^2) used to " +
                 "derive a safe corner speed from curvature.")]
        public float comfyLateralAccel = 2.0f;
        [Tooltip("How far ahead (m) curvature is sampled so the car slows " +
                 "INTO bends rather than inside them.")]
        public float cornerLookaheadMeters = 15f;

        [Header("Follow distance (inert hook — no traffic system right now)")]
        [Tooltip("Bumper-to-bumper distance to a vehicle ahead. -1 = nothing " +
                 "ahead. Will be fed live again when traffic returns.")]
        public float leadCarGap = -1f;
        public float leadCarSpeedEstimate = 0f; // m/s

        [Header("Red lights")]
        [Tooltip("Fraction of maxDecel used for the anticipatory approach " +
                 "curve — below 1 so planned stops feel calmer than " +
                 "emergency ones.")]
        [Range(0.2f, 1f)] public float planningDecelFactor = 0.6f;

        [Header("Linear mode (all parameter toggles off)")]
        [Tooltip("With every DrivingParameters toggle unticked the brain is " +
                 "bypassed entirely: the car glides through the whole track " +
                 "at this constant speed — no red lights, no corner " +
                 "slowdown, no speed limits. A clean baseline pass, e.g. for " +
                 "testing recording/playback.")]
        public float linearSpeedKmh = 40f;

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
        }

        private void Update()
        {
            if (!track.IsReady || _finished) return;
            float dt = Time.deltaTime;

            // ── Linear mode: no parameters, no brain ──────────────────
            // Every toggle off means "just show me the track": constant
            // speed A→B, ignoring lights, corners and limits.
            if (!parameters.AnyOn)
            {
                _waitingAtRedLight = false;
                Speed = linearSpeedKmh / 3.6f;
                Acceleration = 0f;
                S += Speed * dt;
                if (S >= track.TotalLength)
                {
                    S = track.TotalLength;
                    _finished = true;
                    if (logStateChanges) Debug.Log("[CarDriver] Reached the end of the track (linear mode).");
                }
                PlaceOnRoute(dt);
                return;
            }

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

            // ── Target speed this frame, then jerk-limit toward it ───
            float targetSpeed = ComputeTargetSpeed();
            StepSpeed(targetSpeed, parameters.AccelJerk, parameters.BrakeJerk,
                      maxAccel, maxDecel, dt);

            // ── Advance, but NEVER across an unserved stop line ──────
            // The stop line is exactly where the marker sits. If this
            // frame's motion would carry the car past it, the car lands ON
            // it instead and the wait starts — that's what makes the stop
            // position exact regardless of speed, frame rate, or how gentle
            // the braking parameters are.
            float newS = S + Speed * dt;
            if (track.TryNextRedLight(_servedLights, out float stopS, out TrackEvent light)
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

            // Corners: continuous curvature limit, sampled here and a little
            // ahead so braking starts before the bend.
            float curvature = parameters.corneringSpeedOn
                ? Mathf.Max(track.CurvatureAt(S),
                            track.CurvatureAt(S + cornerLookaheadMeters))
                : 0f;
            if (curvature > 0.0001f)
            {
                float allowedLateral = comfyLateralAccel * parameters.CornerFactor;
                target = Mathf.Min(target, Mathf.Sqrt(allowedLateral / curvature));
            }

            // Red light: anticipatory braking curve toward the stop line —
            // v = √(2 · decel · distanceRemaining), slowing smoothly in
            // advance rather than braking hard at the last second. Distance
            // clamps at 0, so even if the line is somehow reached at speed
            // the commanded target is 0 (the hard position clamp in Update
            // is the actual guarantee).
            if (track.TryNextRedLight(_servedLights, out float stopS, out _))
            {
                float distanceRemaining = Mathf.Max(0f, stopS - S);
                float planningDecel = Mathf.Max(maxDecel * planningDecelFactor, 0.5f);
                float approachLimit = Mathf.Sqrt(2f * planningDecel * distanceRemaining);
                target = Mathf.Min(target, approachLimit);
            }

            // Lead vehicle: soft headway control. Inert until a traffic
            // system feeds leadCarGap again (-1 = nothing ahead).
            if (parameters.followDistanceOn && leadCarGap >= 0f)
            {
                float headway = parameters.FollowHeadway;
                float desiredGap = headway * Mathf.Max(Speed, 1f);
                float followTarget = leadCarSpeedEstimate + (leadCarGap - desiredGap) / headway;
                target = Mathf.Min(target, Mathf.Max(0f, followTarget));
            }

            return Mathf.Max(0f, target);
        }
    }
}
