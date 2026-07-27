using UnityEngine;
using Delphi.Simulation;

namespace Delphi.Motion
{
    /// <summary>
    /// Converts CarDriver's motion into seat-motion cues for the YAW VR3 rig:
    /// surge (longitudinal g, from Speed's frame-to-frame change) mapped to
    /// pitch, and cornering mapped to EITHER roll (lateral g × scalar — a
    /// gravity trick faking the sideways push a real corner gives your body)
    /// OR yaw (the car's actual heading change, scaled — the rig's yaw axis
    /// supports continuous rotation, so this can just match reality
    /// directly) — independently toggleable (useRollForCornering /
    /// useYawForCornering) so you can compare which feels right. Written
    /// onto a reference Transform; YawVR3Connection reads that transform —
    /// this class has no dependency on the SDK itself, so it can be built
    /// and eyeballed in the Scene view before the rig or its package are
    /// even in the project.
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

        [Header("Mapping — degrees of tilt per g of accel. Slide toward 0 for " +
                 "barely-there motion, toward the top end to exaggerate it. " +
                 "Range goes well past what maxPitchDeg/maxRollDeg allow " +
                 "through — that's deliberate headroom, since typical car " +
                 "g-forces (0.3-0.8g) need a fairly steep slope to actually " +
                 "reach a 40° ceiling.")]
        [Tooltip("Nose pitch per g of surge (braking/accelerating). Sign is a " +
                 "first guess — flip once you can feel the rig respond.")]
        [Range(0f, 60f)] public float degreesPerSurgeG = 6f;
        [Tooltip("Roll per g of lateral (cornering) accel.")]
        [Range(0f, 60f)] public float degreesPerLateralG = 6f;
        [Tooltip("Hard ceiling regardless of the sliders above.")]
        public float maxPitchDeg = 40f;
        public float maxRollDeg = 40f;

        [Header("Cornering — which axis (or both) represents turning, for " +
                 "comparing feel: roll fakes lateral g via gravity (what a " +
                 "real body feels in a corner); yaw matches the car's actual " +
                 "heading change (what the rig's own continuous-yaw axis is " +
                 "built for). Independent toggles — try one, the other, " +
                 "both, or neither.")]
        public bool useRollForCornering = true;
        public bool useYawForCornering = false;
        [Tooltip("Scales the accumulated heading change before it's sent as " +
                 "yaw. 1 = matches the car's actual turning exactly.")]
        public float yawRotationMultiplier = 1f;

        [Header("Washout — caps how fast commanded tilt can change, so an " +
                 "instant Speed snap (red light stop, park, e-stop) ramps " +
                 "instead of jolting the rig")]
        public float maxDegreesPerSecond = 30f;

        [Header("Smoothing — averages out frame-to-frame noise in the raw " +
                 "g-force signal before it's turned into a tilt target. " +
                 "CarDriver recomputes its target speed continuously (road " +
                 "curvature, follow distance, red-light approach), so the " +
                 "raw accel/brake magnitude can flicker even under steady " +
                 "driving — this is what turns that into one clean lean " +
                 "instead of a jutter. Higher = smoother but slower to " +
                 "respond; 0 = no smoothing at all.")]
        [Range(0f, 0.95f)] public float surgeSmoothing = 0.85f;
        [Range(0f, 0.95f)] public float lateralSmoothing = 0.85f;

        /// <summary>Raw (unsmoothed) longitudinal g this frame — what actually
        /// happened physically, for logging.</summary>
        public float SurgeG { get; private set; }
        /// <summary>Raw (unsmoothed) lateral g this frame.</summary>
        public float LateralG { get; private set; }
        /// <summary>Smoothed pitch actually written to the reference transform.</summary>
        public float PitchDeg { get; private set; }
        /// <summary>Smoothed roll actually written to the reference transform.</summary>
        public float RollDeg { get; private set; }
        /// <summary>Accumulated heading change actually written to the
        /// reference transform, when useYawForCornering is on. Unlike
        /// pitch/roll this isn't clamped to a max angle — the rig's yaw
        /// axis is built for continuous rotation.</summary>
        public float YawDeg { get; private set; }
        public bool IsReturningToNeutral { get; private set; }
        /// <summary>True while FreezeInPlace() is holding the seat exactly
        /// where it was — the explicit per-iteration rating questionnaire,
        /// where the car itself is also frozen (CarDriver.FreezeInPlace),
        /// not parked-and-reset. Real forces stay exactly as they were, not
        /// neutralized.</summary>
        public bool IsFrozen { get; private set; }

        private const float GravityMs2 = 9.81f;

        private float _prevSpeed;
        private Quaternion _prevRot;
        private bool _hasPrev;
        private float _smoothedSurgeG, _smoothedLateralG;

        private float _returnDuration;
        private float _returnTimer;
        private float _returnFromPitch, _returnFromRoll;

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

        /// <summary>Hard override: eases pitch/roll back to level over
        /// `seconds`, ignoring whatever the physics-driven cue is doing.
        /// Deliberately does NOT touch yaw — yaw represents the car's actual
        /// heading, not a g-force illusion, so there's no "neutral" to
        /// return to; forcing it to 0 here would desync the accumulator
        /// from the car's real orientation for the rest of the drive. Used
        /// for idle/parked/questionnaire-reset/emergency-stop — takes
        /// priority over an in-flight Freeze (an emergency must always be
        /// able to override a frozen seat).</summary>
        public void ReturnToNeutral(float seconds)
        {
            IsFrozen = false;
            IsReturningToNeutral = true;
            _returnDuration = Mathf.Max(0.01f, seconds);
            _returnTimer = 0f;
            _returnFromPitch = PitchDeg;
            _returnFromRoll = RollDeg;
        }

        /// <summary>Hand control back to the live physics-driven cue immediately.</summary>
        public void CancelReturnToNeutral() => IsReturningToNeutral = false;

        /// <summary>Hold the seat exactly where it currently is, ignoring the
        /// physics-driven cue, until Unfreeze() is called. For the explicit
        /// per-iteration rating questionnaire — the car itself is frozen in
        /// place (not parked/reset), so the seat should match: whatever
        /// force was live when the freeze hit is what stays felt.</summary>
        public void FreezeInPlace()
        {
            IsReturningToNeutral = false;
            IsFrozen = true;
        }

        /// <summary>Hand control back to the live physics-driven cue. Safe to
        /// call even when not frozen (idempotent) — every ResumeDriving()
        /// call site pairs with this.</summary>
        public void Unfreeze() => IsFrozen = false;

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
            float yawDeltaDeg = Mathf.DeltaAngle(_prevRot.eulerAngles.y, car.transform.rotation.eulerAngles.y);
            LateralG = (yawDeltaDeg * Mathf.Deg2Rad / dt) * car.Speed / GravityMs2;
            _prevSpeed = car.Speed;
            _prevRot = car.transform.rotation;

            // Smoothed separately from the raw values above — SurgeG/
            // LateralG stay true-to-what-happened for logging; these feed
            // the actual tilt target so brief flickers (CarDriver
            // recomputing its target speed continuously) don't jutter it.
            _smoothedSurgeG = Mathf.Lerp(SurgeG, _smoothedSurgeG, surgeSmoothing);
            _smoothedLateralG = Mathf.Lerp(LateralG, _smoothedLateralG, lateralSmoothing);

            // Frozen: leave PitchDeg/RollDeg/YawDeg/referenceTransform
            // exactly as they were the instant FreezeInPlace() was called —
            // SurgeG/LateralG above still update for the log, but nothing
            // writes to the transform.
            if (IsFrozen) return;

            if (IsReturningToNeutral)
            {
                _returnTimer += dt;
                float t = Mathf.Clamp01(_returnTimer / _returnDuration);
                PitchDeg = Mathf.Lerp(_returnFromPitch, 0f, t);
                RollDeg = Mathf.Lerp(_returnFromRoll, 0f, t);
                referenceTransform.localRotation = Quaternion.Euler(PitchDeg, YawDeg, -RollDeg);
                if (t >= 1f) IsReturningToNeutral = false;
                return;
            }

            float targetPitch = Mathf.Clamp(-_smoothedSurgeG * degreesPerSurgeG, -maxPitchDeg, maxPitchDeg);
            float targetRoll = useRollForCornering
                ? Mathf.Clamp(_smoothedLateralG * degreesPerLateralG, -maxRollDeg, maxRollDeg)
                : 0f;
            float maxStep = maxDegreesPerSecond * dt;
            PitchDeg = Mathf.MoveTowards(PitchDeg, targetPitch, maxStep);
            RollDeg = Mathf.MoveTowards(RollDeg, targetRoll, maxStep);

            // Yaw is an accumulator, not a target-and-limit like pitch/roll —
            // each frame adds the car's ACTUAL heading delta (scaled), so it
            // tracks the true turning smoothly with no extra washout needed.
            // When switched off it eases back to 0 using the same rate limit
            // pitch/roll use, rather than snapping.
            if (useYawForCornering) YawDeg += yawDeltaDeg * yawRotationMultiplier;
            else if (YawDeg != 0f) YawDeg = Mathf.MoveTowards(YawDeg, 0f, maxStep);

            referenceTransform.localRotation = Quaternion.Euler(PitchDeg, YawDeg, -RollDeg);
        }
    }
}
