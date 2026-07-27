using UnityEngine;
using Delphi.Simulation;

namespace Delphi.Motion
{
    /// <summary>
    /// Converts CarDriver's motion into seat-motion cues for the YAW VR3 rig:
    /// surge (longitudinal g, from Speed's frame-to-frame change) and lateral
    /// g (from yaw rate × Speed), mapped to pitch/roll degrees and written
    /// onto a reference Transform. YawVR3Connection points the YawVR SDK at
    /// that transform — this class has no dependency on the SDK itself, so
    /// it can be built and eyeballed in the Scene view before the rig or its
    /// package are even in the project.
    ///
    /// Deliberately reads Speed/rotation deltas rather than CarDriver's
    /// internal AccelJerk/BrakeJerk magnitudes — that way EVERY instant Speed
    /// snap (HoldStopped at a red light, RequestPark, EmergencyHalt) shows up
    /// as an ordinary large computed acceleration and passes through the SAME
    /// rate limiter as normal driving, instead of needing special-case
    /// handling at each call site.
    ///
    /// Sign/scale conventions (degreesPerSurgeG etc.) are first-guess
    /// defaults — flip the sign or retune once the physical rig is connected
    /// and you can feel which direction is actually correct.
    /// </summary>
    [DefaultExecutionOrder(100)] // after CarDriver's Update (default order 0)
    public class CarMotionCues : MonoBehaviour
    {
        [Header("Links (auto-found if left empty)")]
        public CarDriver car;
        [Tooltip("Empty GameObject whose local rotation this drives — point " +
                 "YawVR3Connection's reference transform at this same object.")]
        public Transform referenceTransform;

        [Header("Mapping — degrees of tilt per g of accel")]
        [Tooltip("Nose pitch per g of surge (braking/accelerating). Sign is a " +
                 "first guess — flip once you can feel the rig respond.")]
        public float degreesPerSurgeG = 6f;
        [Tooltip("Roll per g of lateral (cornering) accel.")]
        public float degreesPerLateralG = 6f;
        public float maxPitchDeg = 15f;
        public float maxRollDeg = 15f;

        [Header("Washout — caps how fast commanded tilt can change, so an " +
                 "instant Speed snap (red light stop, park, e-stop) ramps " +
                 "instead of jolting the rig")]
        public float maxDegreesPerSecond = 30f;

        /// <summary>Raw (unsmoothed) longitudinal g this frame — what actually
        /// happened physically, for logging.</summary>
        public float SurgeG { get; private set; }
        /// <summary>Raw (unsmoothed) lateral g this frame.</summary>
        public float LateralG { get; private set; }
        /// <summary>Smoothed pitch actually written to the reference transform.</summary>
        public float PitchDeg { get; private set; }
        /// <summary>Smoothed roll actually written to the reference transform.</summary>
        public float RollDeg { get; private set; }
        public bool IsReturningToNeutral { get; private set; }

        private const float GravityMs2 = 9.81f;

        private float _prevSpeed;
        private Quaternion _prevRot;
        private bool _hasPrev;

        private float _returnDuration;
        private float _returnTimer;
        private Quaternion _returnFrom;

        private const string AutoChildName = "YAW Reference Transform";

        private void Awake()
        {
            if (car == null) car = FindFirstObjectByType<CarDriver>();
            EnsureReferenceTransform();
        }

        /// <summary>Idempotent: reuses an existing child (by name) if one's
        /// already there — including one created in the Editor ahead of time
        /// so it shows up in the Inspector/Hierarchy without entering Play —
        /// otherwise creates it. Safe to call from Awake or the Editor.</summary>
        [ContextMenu("Create Reference Transform Now")]
        public void EnsureReferenceTransform()
        {
            if (referenceTransform != null) return;
            var existing = transform.Find(AutoChildName);
            if (existing != null)
            {
                referenceTransform = existing;
                return;
            }
            var go = new GameObject(AutoChildName);
            go.transform.SetParent(transform, false);
            referenceTransform = go.transform;
        }

        /// <summary>Hard override: slerp the reference transform back to level
        /// over `seconds`, ignoring whatever the physics-driven cue is doing.
        /// For SessionController.EmergencyStop — guarantees a bounded return
        /// regardless of what motion was mid-flight when it was pressed.</summary>
        public void ReturnToNeutral(float seconds)
        {
            IsReturningToNeutral = true;
            _returnDuration = Mathf.Max(0.01f, seconds);
            _returnTimer = 0f;
            _returnFrom = referenceTransform != null ? referenceTransform.localRotation : Quaternion.identity;
        }

        /// <summary>Hand control back to the live physics-driven cue immediately.</summary>
        public void CancelReturnToNeutral() => IsReturningToNeutral = false;

        private void Update()
        {
            if (car == null || referenceTransform == null) return;
            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            if (!_hasPrev)
            {
                _prevSpeed = car.Speed;
                _prevRot = car.transform.rotation;
                _hasPrev = true;
                return;
            }

            // Raw physical signal — always tracked, even mid return-to-neutral,
            // so SurgeG/LateralG in the log reflect what actually happened.
            SurgeG = (car.Speed - _prevSpeed) / dt / GravityMs2;
            float yawRateRad = Mathf.DeltaAngle(_prevRot.eulerAngles.y, car.transform.rotation.eulerAngles.y)
                                * Mathf.Deg2Rad / dt;
            LateralG = yawRateRad * car.Speed / GravityMs2;
            _prevSpeed = car.Speed;
            _prevRot = car.transform.rotation;

            if (IsReturningToNeutral)
            {
                _returnTimer += dt;
                float t = Mathf.Clamp01(_returnTimer / _returnDuration);
                referenceTransform.localRotation = Quaternion.Slerp(_returnFrom, Quaternion.identity, t);
                PitchDeg = 0f;
                RollDeg = 0f;
                if (t >= 1f) IsReturningToNeutral = false;
                return;
            }

            float targetPitch = Mathf.Clamp(-SurgeG * degreesPerSurgeG, -maxPitchDeg, maxPitchDeg);
            float targetRoll = Mathf.Clamp(LateralG * degreesPerLateralG, -maxRollDeg, maxRollDeg);
            float maxStep = maxDegreesPerSecond * dt;
            PitchDeg = Mathf.MoveTowards(PitchDeg, targetPitch, maxStep);
            RollDeg = Mathf.MoveTowards(RollDeg, targetRoll, maxStep);

            referenceTransform.localRotation = Quaternion.Euler(PitchDeg, 0f, -RollDeg);
        }
    }
}
