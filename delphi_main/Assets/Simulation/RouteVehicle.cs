using UnityEngine;

namespace Delphi.Simulation
{
    /// <summary>
    /// Kinematic base for a vehicle on the Track. Single lane, no traffic —
    /// just route-space position and speed control. Owns MECHANICS only:
    ///
    ///   - route-space state: S (arc-length position, metres), Speed (m/s),
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
            _hasLastPos = false;
            transform.position = track.EvaluatePosition(S);
            Vector3 fwd = track.EvaluateTangent(S);
            fwd.y = 0f;
            if (fwd.sqrMagnitude > 1e-6f)
                transform.rotation = Quaternion.LookRotation(fwd.normalized, Vector3.up);
        }

        // ── Speed control ───────────────────────────────────────────────
        /// <summary>Move Speed directly toward targetSpeed at a CONSTANT
        /// magnitude — accelMagnitude when speeding up, brakeMagnitude when
        /// slowing down. No separate physical ceiling and no ramping toward
        /// one: the magnitude IS the driving-style parameter (already mapped
        /// from that axis's own Min/Max range), so gentle vs assertive is
        /// visible for the WHOLE speed change, not a brief transient on the
        /// way to some shared cap.</summary>
        protected void StepSpeed(float targetSpeed, float accelMagnitude, float brakeMagnitude, float dt)
        {
            float magnitude = targetSpeed >= Speed ? accelMagnitude : brakeMagnitude;
            Speed = Mathf.MoveTowards(Speed, targetSpeed, Mathf.Max(0.01f, magnitude) * dt);
        }

        /// <summary>Hard-set speed (red-light waits etc.).</summary>
        protected void HoldStopped()
        {
            Speed = 0f;
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
