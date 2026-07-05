using UnityEngine;

namespace Delphi.Simulation
{
    /// <summary>
    /// The four driving-style parameters, normalised 0..1 (gentle..assertive).
    /// Physical ranges are grounded in the AV comfort literature we reviewed —
    /// tune the min/max fields during piloting.
    /// </summary>
    [System.Serializable]
    public class DrivingParameters
    {
        [Range(0f, 1f)] public float accelerationJerk = 0.5f;
        [Range(0f, 1f)] public float brakingJerk      = 0.5f;
        [Range(0f, 1f)] public float followDistance   = 0.5f;
        [Range(0f, 1f)] public float corneringSpeed    = 0.5f;

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

        public float AccelJerk      => Mathf.Lerp(accelJerkMin, accelJerkMax, accelerationJerk);
        public float BrakeJerk      => Mathf.Lerp(brakeJerkMin, brakeJerkMax, brakingJerk);
        public float FollowHeadway  => Mathf.Lerp(followMax, followMin, followDistance); // inverted
        public float CornerFactor   => Mathf.Lerp(cornerMin, cornerMax, corneringSpeed);
    }

    /// <summary>
    /// Drives the car across RouteStitcher's ordered tile sequence.
    ///
    /// Speed is jerk-limited (acceleration RAMPS toward its target, capped by
    /// a rate-of-change limit) rather than set directly — that's what makes
    /// accelerationJerk/brakingJerk control ABRUPTNESS, not just magnitude.
    ///
    /// Corner slowdown and the red-light stop/wait/go both read directly from
    /// the tile data already authored on each RouteTile — no duplicate config.
    ///
    /// Follow distance has a hook (LeadCarGap) but no effect yet, since no
    /// lead-car object exists in the scene yet — that's the next build step,
    /// not this file.
    /// </summary>
    public class CarDriver : MonoBehaviour
    {
        [Header("Links")]
        public RouteStitcher route;
        [Tooltip("Defaults to this GameObject's own transform.")]
        public Transform car;

        [Header("Parameters (normally driven by the optimiser)")]
        public DrivingParameters parameters = new DrivingParameters();

        [Header("Baseline speed")]
        [Tooltip("Cruise speed on open road. All four parameters modulate " +
                 "deviations from this baseline.")]
        public float baselineSpeedKmh = 40f;

        [Header("Physical ceilings (fixed safety bounds, not optimised)")]
        public float maxAccel = 3.0f;  // m/s^2
        public float maxDecel = 4.0f;  // m/s^2

        [Header("Cornering")]
        [Tooltip("Baseline comfortable lateral acceleration (m/s^2) used to " +
                 "derive a safe corner speed from curvature.")]
        public float comfyLateralAccel = 2.0f;

        [Header("Follow distance (hook — inert until a lead car exists)")]
        [Tooltip("Distance in metres to a lead car, fed by a future LeadCar " +
                 "script. -1 = no lead car detected.")]
        public float leadCarGap = -1f;
        public float leadCarSpeedEstimate = 0f; // m/s

        [Header("Debug")]
        public bool logStateChanges = false;

        // ── Runtime state ────────────────────────────────────────────
        private int   _tileIndex;
        private float _t;            // 0..1 within the current tile
        private float _tileLength;   // cached arc length of current tile (m)
        private float _speed;        // m/s
        private float _acceleration; // m/s^2, jerk-limited

        private bool  _waitingAtRedLight;
        private float _waitTimer;

        public float CurrentSpeedKmh => _speed * 3.6f;
        public RouteTile CurrentTile =>
            (route != null && route.OrderedTiles.Count > 0)
                ? route.OrderedTiles[Mathf.Clamp(_tileIndex, 0, route.OrderedTiles.Count - 1)]
                : null;

        private void Awake()
        {
            if (car == null) car = transform;
        }

        private void Start()
        {
            if (route == null)
            {
                Debug.LogError("[CarDriver] No RouteStitcher assigned.");
                enabled = false;
                return;
            }

            // RouteStitcher builds the route in its own Start(); Unity doesn't
            // guarantee which Start() runs first, so defer one frame if the
            // route isn't ready yet rather than assuming ordering.
            if (route.OrderedTiles.Count == 0)
                Invoke(nameof(EnterFirstTile), 0f);
            else
                EnterFirstTile();
        }

        private void EnterFirstTile()
        {
            if (route.OrderedTiles.Count == 0)
            {
                Debug.LogError("[CarDriver] Route still has no tiles — did RouteStitcher.Build() run?");
                return;
            }
            _tileIndex = 0;
            _t = 0f;
            _tileLength = EstimateTileLength(CurrentTile);
            car.position = CurrentTile.EntryPosition;
        }

        private void Update()
        {
            var tile = CurrentTile;
            if (tile == null) return;

            float dt = Time.deltaTime;

            // ── Holding at a red light ───────────────────────────────
            if (_waitingAtRedLight)
            {
                _speed = 0f;
                _waitTimer -= dt;
                if (_waitTimer <= 0f)
                {
                    _waitingAtRedLight = false;
                    if (logStateChanges) Debug.Log($"[CarDriver] Pulling away from {tile.name}.");
                }
                return;
            }

            // ── Target speed this frame, then jerk-limit toward it ───
            float targetSpeed = ComputeTargetSpeed(tile);
            float speedGap = targetSpeed - _speed;
            float desiredAccel = Mathf.Clamp(speedGap / Mathf.Max(dt, 0.0001f), -maxDecel, maxAccel);
            float jerkLimit = desiredAccel >= _acceleration ? parameters.AccelJerk : parameters.BrakeJerk;
            _acceleration = Mathf.MoveTowards(_acceleration, desiredAccel, jerkLimit * dt);
            _speed = Mathf.Max(0f, _speed + _acceleration * dt);

            // ── Advance along the tile by real distance travelled ────
            if (_tileLength > 0.01f)
                _t += (_speed * dt) / _tileLength;

            // ── Reached the red light's stop point? ──────────────────
            if (tile.kind == TileKind.RedLight && _t >= tile.stopPointT && _speed < 0.15f)
            {
                _t = tile.stopPointT;
                _waitingAtRedLight = true;
                _waitTimer = tile.waitDuration;
                if (logStateChanges) Debug.Log($"[CarDriver] Stopped at {tile.name}, waiting {tile.waitDuration}s.");
                PositionCar(tile, _t);
                return;
            }

            if (_t >= 1f) AdvanceToNextTile();
            else PositionCar(tile, _t);
        }

        private void PositionCar(RouteTile tile, float t)
        {
            t = Mathf.Clamp01(t);
            car.position = tile.Evaluate(t);

            float h = 0.01f;
            Vector3 a = tile.Evaluate(Mathf.Clamp01(t - h));
            Vector3 b = tile.Evaluate(Mathf.Clamp01(t + h));
            Vector3 fwd = b - a;
            fwd.y = 0f;
            if (fwd.sqrMagnitude > 1e-6f)
                car.rotation = Quaternion.Slerp(car.rotation, Quaternion.LookRotation(fwd, Vector3.up), 10f * Time.deltaTime);
        }

        private void AdvanceToNextTile()
        {
            _tileIndex++;
            if (_tileIndex >= route.OrderedTiles.Count)
            {
                if (logStateChanges) Debug.Log("[CarDriver] Reached the end of the route.");
                _tileIndex = route.OrderedTiles.Count - 1;
                PositionCar(CurrentTile, 1f);
                enabled = false;
                return;
            }

            _t = 0f;
            _tileLength = EstimateTileLength(CurrentTile);
            PositionCar(CurrentTile, 0f);
            if (logStateChanges) Debug.Log($"[CarDriver] Entering {CurrentTile.name} ({CurrentTile.kind}).");
        }

        // ── Target speed, per event kind ─────────────────────────────
        private float ComputeTargetSpeed(RouteTile tile)
        {
            float cruise = baselineSpeedKmh / 3.6f;
            float target = cruise;

            // Corner: slow within the scored bend range, based on curvature,
            // scaled by the corneringSpeed parameter.
            if (tile.kind == TileKind.Corner && _t >= tile.cornerStartT && _t <= tile.cornerEndT)
            {
                float curvature = SampleCurvature(tile, _t);
                if (curvature > 0.0001f)
                {
                    float allowedLateral = comfyLateralAccel * parameters.CornerFactor;
                    float cornerLimit = Mathf.Sqrt(allowedLateral / curvature);
                    target = Mathf.Min(target, cornerLimit);
                }
            }

            // Red light: anticipatory braking curve toward the stop point —
            // v = sqrt(2 * decel * distanceRemaining), so it slows smoothly
            // in advance rather than braking hard at the last second.
            if (tile.kind == TileKind.RedLight && _t < tile.stopPointT)
            {
                float distanceRemaining = (tile.stopPointT - _t) * _tileLength;
                float planningDecel = Mathf.Max(maxDecel * 0.6f, 0.5f);
                float approachLimit = Mathf.Sqrt(Mathf.Max(0f, 2f * planningDecel * distanceRemaining));
                target = Mathf.Min(target, approachLimit);
            }

            // Follow distance: inert until leadCarGap is actually fed by a
            // lead-car script (next build step).
            if (leadCarGap >= 0f)
            {
                float desiredGap = parameters.FollowHeadway * Mathf.Max(_speed, 1f);
                if (leadCarGap < desiredGap)
                    target = Mathf.Min(target, leadCarSpeedEstimate);
            }

            return Mathf.Max(0f, target);
        }

        // Curvature from three nearby sampled points — no dependency on any
        // specific Splines-package curvature API, so it can't silently break
        // if that surface changes between package versions.
        private float SampleCurvature(RouteTile tile, float t)
        {
            float h = 0.01f;
            Vector3 a = tile.Evaluate(Mathf.Clamp01(t - h));
            Vector3 c = tile.Evaluate(t);
            Vector3 b = tile.Evaluate(Mathf.Clamp01(t + h));
            Vector3 v1 = c - a, v2 = b - c;
            v1.y = v2.y = 0f;
            if (v1.sqrMagnitude < 1e-6f || v2.sqrMagnitude < 1e-6f) return 0f;
            float angle = Vector3.Angle(v1, v2) * Mathf.Deg2Rad;
            float arc = v2.magnitude;
            return arc > 1e-4f ? angle / arc : 0f;
        }

        private float EstimateTileLength(RouteTile tile, int samples = 20)
        {
            if (tile == null) return 1f;
            float length = 0f;
            Vector3 prev = tile.Evaluate(0f);
            for (int i = 1; i <= samples; i++)
            {
                float t = (float)i / samples;
                Vector3 p = tile.Evaluate(t);
                length += Vector3.Distance(prev, p);
                prev = p;
            }
            return Mathf.Max(length, 0.01f);
        }
    }
}