using UnityEngine;

namespace Delphi.Simulation
{
    /// <summary>
    /// Kinematic base for a vehicle on the Track. Single lane, no traffic —
    /// just route-space position and jerk-limited speed control. Owns
    /// MECHANICS only:
    ///
    ///   - route-space state: S (arc-length position, metres), Speed (m/s),
    ///     jerk-limited Acceleration,
    ///   - placement: transform position/heading from route space.
    ///
    /// Deliberately NOT here: target-speed computation, red-light/corner
    /// logic. The subclass (CarDriver) owns the brain.
    /// </summary>
    public abstract class RouteVehicle : MonoBehaviour
    {
        [Header("Links (auto-found if left empty)")]
        public Track track;

        [Header("Body")]
        [Tooltip("Bumper-to-bumper length, metres.")]
        public float lengthMeters = 4.5f;

        // ── Route-space state ───────────────────────────────────────────
        public float S { get; protected set; }
        public float Speed { get; protected set; }

        protected float Acceleration;

        // Heading support
        private Vector3 _lastPos;
        private bool _hasLastPos;

        protected virtual void Awake()
        {
            if (track == null) track = FindFirstObjectByType<Track>();
            if (track == null)
            {
                Debug.LogError($"[{GetType().Name}] No Track in the scene.");
                enabled = false;
            }
        }

        /// <summary>Drop the vehicle at a route position, facing along the
        /// road, with clean state.</summary>
        public virtual void PlaceAt(float s, float speed)
        {
            S = Mathf.Clamp(s, 0f, track.TotalLength);
            Speed = Mathf.Max(0f, speed);
            Acceleration = 0f;
            _hasLastPos = false;
            transform.position = track.EvaluatePosition(S);
            Vector3 fwd = track.EvaluateTangent(S);
            fwd.y = 0f;
            if (fwd.sqrMagnitude > 1e-6f)
                transform.rotation = Quaternion.LookRotation(fwd.normalized, Vector3.up);
        }

        // ── Speed control ───────────────────────────────────────────────
        /// <summary>Jerk-limited acceleration toward a target acceleration —
        /// the abruptness knob. Acceleration RAMPS toward the target, capped
        /// by a rate-of-change (jerk) limit, so accel/brake style parameters
        /// control how sudden speed changes FEEL, not just their magnitude.</summary>
        protected void StepAccel(float targetAccel, float accelJerk, float brakeJerk,
                                 float maxAccel, float maxDecel, float dt)
        {
            float desired = Mathf.Clamp(targetAccel, -maxDecel, maxAccel);
            float jerkLimit = desired >= Acceleration ? accelJerk : brakeJerk;
            Acceleration = Mathf.MoveTowards(Acceleration, desired, jerkLimit * dt);
            Speed = Mathf.Max(0f, Speed + Acceleration * dt);
        }

        /// <summary>Jerk-limited approach to a target SPEED.</summary>
        protected void StepSpeed(float targetSpeed, float accelJerk, float brakeJerk,
                                 float maxAccel, float maxDecel, float dt)
        {
            float desiredAccel = (targetSpeed - Speed) / Mathf.Max(dt, 0.0001f);
            StepAccel(desiredAccel, accelJerk, brakeJerk, maxAccel, maxDecel, dt);
        }

        /// <summary>Hard-set speed (red-light waits etc.).</summary>
        protected void HoldStopped()
        {
            Speed = 0f;
            Acceleration = 0f;
        }

        // ── Placement ───────────────────────────────────────────────────
        /// <summary>Put the transform where route space says it should be.
        /// Heading comes from the frame-to-frame position delta, with the
        /// road tangent as the near-stationary fallback.</summary>
        protected void PlaceOnRoute(float dt)
        {
            Vector3 pos = track.EvaluatePosition(S);
            transform.position = pos;

            Vector3 fwd = _hasLastPos ? pos - _lastPos : Vector3.zero;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-6f)
            {
                fwd = track.EvaluateTangent(S);
                fwd.y = 0f;
            }
            if (fwd.sqrMagnitude > 1e-6f)
                transform.rotation = Quaternion.Slerp(transform.rotation,
                    Quaternion.LookRotation(fwd.normalized, Vector3.up), 10f * dt);

            _lastPos = pos;
            _hasLastPos = true;
        }
    }
}
