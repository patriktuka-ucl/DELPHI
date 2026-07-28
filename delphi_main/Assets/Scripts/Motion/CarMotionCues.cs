using UnityEngine;
using Delphi.Simulation;

namespace Delphi.Motion
{
    /// <summary>
    /// Turns the car's motion into seat-motion cues for the YAW VR3 rig.
    ///
    /// THE MODEL, in one line each:
    ///
    ///   PITCH ← the car is changing SPEED:
    ///
    ///               lean = acceleration × (speed change left / reference)
    ///
    ///           One product, one constant. Lean scales with the acceleration
    ///           actually being applied AND with how much of the speed change
    ///           is still ahead, so a big manoeuvre leans hard and a small
    ///           trim barely registers. Pull away from a stop and the seat
    ///           leans back immediately; as the speed closes on cruise the
    ///           lean decays, reaching level exactly as the car settles. No
    ///           step, no snap, no plateau — a smooth arc over the whole
    ///           manoeuvre, and braking is the mirror image. A jerk limit
    ///           (maxJerkMs3) shapes the ONSET of that arc, since the car
    ///           itself applies its full braking magnitude in a single frame.
    ///
    ///   ROLL  ← the car is changing HEADING. Bank scales with turn rate
    ///           (how tight the bend is × how fast you're taking it). Rolls in
    ///           as the bend tightens, holds while you're going round it, rolls
    ///           back out as the road straightens — zero on a straight, because
    ///           the road's curvature there is zero.
    ///
    ///   YAW   ← the same turn rate, integrated, at a fraction of reality
    ///           (yawFollowFraction), washed out on a long time constant so a
    ///           whole drive's worth of turning doesn't leave the rig facing
    ///           sideways.
    ///
    /// NOTHING HERE IS DIFFERENTIATED. Every input is read straight from
    /// CarDriver, which publishes its own intent in plain physical units
    /// (CommandedAccel, SpeedGap, YawRateRadPerSec). That is the entire reason
    /// this is smooth: the previous version recovered acceleration by dividing
    /// a frame-to-frame Speed delta by dt, and cornering by differencing the
    /// car's transform rotation — both of which alternate between a full value
    /// and zero on consecutive frames under perfectly steady driving, which the
    /// rig then rendered as a jerk. The old smoothing/direction-hold knobs
    /// existed only to paper over that noise and are gone with it.
    ///
    /// Output is a critically damped servo (SmoothDamp) with a hard degrees-
    /// per-second ceiling, so the seat always converges without overshoot and
    /// never chatters. Angles are published as PitchDeg/RollDeg/YawDeg —
    /// YawVR3Connection reads THOSE directly. The reference Transform is still
    /// written, but only so the Scene view and the researcher UI have something
    /// to visualise; it is deliberately no longer the transport to the rig
    /// (a Quaternion.Euler → quaternion → euler round trip is not the identity
    /// once two axes are non-zero, and that ambiguity used to reach the rig).
    ///
    /// Signs are a convention, not a truth — accelerating leans back, turning
    /// right banks right. If either feels backwards on the physical rig, tick
    /// invertPitch / invertRoll in the Inspector; no recompile needed.
    /// </summary>
    [DefaultExecutionOrder(100)] // after CarDriver's Update (default order 0)
    public class CarMotionCues : MonoBehaviour
    {
        [Header("Links (auto-found if left empty)")]
        public CarDriver car;
        [Tooltip("Empty GameObject whose local rotation this drives, for the " +
                 "Scene view and the researcher UI. NOT what's sent to the rig " +
                 "— that reads PitchDeg/RollDeg/YawDeg directly.")]
        public Transform referenceTransform;

        [Header("Pitch — how hard the seat leans when the car changes speed")]
        [Tooltip("The speed change (m/s) that scales the lean to one degree " +
                 "per m/s² of acceleration. This is the ONLY depth knob for " +
                 "pitch — lean = acceleration × (remaining speed change / this). " +
                 "Lower it for a deeper seat, raise it for a shallower one.\n\n" +
                 "2 is set so a full pull-away or stop at this track's 50 km/h " +
                 "limit lands where the previous tuning did: a mid 2.5 m/s² " +
                 "pull-away ≈ 15°, a mid 3 m/s² stop ≈ 18°, and the most " +
                 "assertive 5 m/s² stop from top speed ≈ 35°, still inside the " +
                 "40° ceiling.\n\n" +
                 "RE-CHECK THIS IF THE POSTED SPEED LIMIT GOES UP. The lean now " +
                 "scales with the size of the manoeuvre and has no plateau, so " +
                 "a faster road means bigger speed changes and deeper leans: at " +
                 "65 km/h the hardest stop would clip against maxPitchDeg, and " +
                 "once two different assertive styles both clip they feel " +
                 "identical — which is exactly the distinction the optimiser is " +
                 "trying to measure.")]
        [Range(0.5f, 20f)] public float speedGapReferenceMs = 2f;
        [Tooltip("Jerk limit (m/s³) — how fast the acceleration the seat is " +
                 "rendering may itself change. This is the ONSET knob, and it " +
                 "matters most under braking. CarDriver applies its whole " +
                 "braking magnitude the instant it decides to slow down: the " +
                 "acceleration signal steps from 0 to the full value in ONE " +
                 "frame, so the lean it asks for is a step too, and the servo " +
                 "then chases that step as fast as its degrees-per-second " +
                 "ceiling allows. Real brakes don't bite instantly. Limiting " +
                 "jerk ramps the lean in (and back out) over a sensible time " +
                 "instead, WITHOUT touching the plateau — so gentle vs " +
                 "assertive stays exactly as distinguishable as before, it " +
                 "just stops arriving as a slam. At 4, a 3 m/s² brake takes " +
                 "0.75 s to build. Lower = softer and later; 0 = off, back to " +
                 "an instant step.")]
        [Range(0f, 20f)] public float maxJerkMs3 = 4f;
        public bool invertPitch = false;

        [Header("Roll — how hard the seat banks when the car changes heading")]
        [Tooltip("Degrees of bank per rad/s of turn rate. A 50 m-radius bend " +
                 "taken at 12 m/s is 0.24 rad/s, so 60 banks it about 14°.")]
        [Range(0f, 200f)] public float rollDegPerYawRate = 60f;
        public bool invertRoll = false;

        [Header("Ceilings — hard limits regardless of the sliders above")]
        public float maxPitchDeg = 40f;
        public float maxRollDeg = 40f;

        [Header("Cornering axes — roll banks the seat (what your body feels in " +
                 "a corner); yaw rotates the whole rig to follow the car's " +
                 "actual heading. Independent toggles: try one, the other, or " +
                 "both. With both on, keep yawFollowFraction well below 1 or " +
                 "the same corner reads twice.")]
        public bool useRollForCornering = true;
        public bool useYawForCornering = true;
        [Tooltip("Fraction of the car's real heading change the rig follows. " +
                 "1 = matches reality exactly; 0.25 = a quarter of it, a hint " +
                 "of rotation rather than the full turn.")]
        [Range(0f, 1f)] public float yawFollowFraction = 0.25f;
        [Tooltip("Time constant (s) for yaw drifting back toward centre. " +
                 "Without it, yaw accumulates over a whole drive and the rig " +
                 "can end a long run facing well off-centre. Deliberately a " +
                 "PROPORTIONAL decay, not a fixed degrees-per-second bleed: a " +
                 "fixed bleed would fight the accrual and, in a gentle corner " +
                 "that turns slower than the bleed rate, actually rotate the " +
                 "rig the WRONG WAY. This scales with how far off centre yaw " +
                 "already is — zero pull at centre — so it can never reverse " +
                 "a turn; it just bounds how far a long bend can wind the rig " +
                 "up, and unwinds it on the straight after. Set 0 to follow " +
                 "the car's heading indefinitely.")]
        public float yawReturnSeconds = 20f;

        [Header("Servo response — how the commanded angle actually gets there")]
        [Tooltip("Roughly how long the seat takes to reach a new target. " +
                 "Critically damped, so it converges without overshoot and " +
                 "without chattering if the target wobbles. Lower = crisper " +
                 "and more immediate; higher = softer and more delayed.")]
        [Range(0.05f, 2f)] public float responseSeconds = 0.35f;
        [Tooltip("Hard ceiling on how fast the commanded angle may change, so " +
                 "an instant Speed snap (red light, park, e-stop) ramps rather " +
                 "than jolting the rig.")]
        public float maxDegreesPerSecond = 30f;

        // ── Published state (read by YawVR3Connection, the UI, the log) ──
        /// <summary>Commanded seat pitch, degrees. Negative = nose up = leaning
        /// back, which is what accelerating produces.</summary>
        public float PitchDeg { get; private set; }
        /// <summary>Commanded seat roll, degrees.</summary>
        public float RollDeg { get; private set; }
        /// <summary>Commanded yaw, degrees. Unlike pitch/roll this isn't
        /// clamped to a max angle — the rig's yaw axis is built for continuous
        /// rotation — but it is scaled and bled back toward centre.</summary>
        public float YawDeg { get; private set; }

        /// <summary>The acceleration the CAR is actually applying (m/s²,
        /// signed), straight from CarDriver. Logged and shown in the UI so the
        /// seat's behaviour can be read against the car's actual intent.</summary>
        public float AccelMs2 { get; private set; }

        /// <summary>The same acceleration after jerk limiting — what the lean
        /// is actually built from. Differs from AccelMs2 only during an onset
        /// or release; watching the two diverge is how you see maxJerkMs3
        /// doing its job.</summary>
        public float ShapedAccelMs2 { get; private set; }
        /// <summary>The turn rate this cue is currently rendering (deg/s,
        /// signed; + = right).</summary>
        public float YawRateDegPerSec { get; private set; }
        /// <summary>How much speed change this manoeuvre still has left to do
        /// (m/s, signed) — measured to where it ENDS, not to this frame's
        /// moving target. See CarDriver.SpeedGap.</summary>
        public float SpeedGapMs { get; private set; }

        public bool IsReturningToNeutral { get; private set; }
        /// <summary>True while FreezeInPlace() is holding the seat exactly
        /// where it was — the explicit per-iteration rating questionnaire,
        /// where the car itself is also frozen (CarDriver.FreezeInPlace),
        /// not parked-and-reset. Real forces stay exactly as they were, not
        /// neutralized.</summary>
        public bool IsFrozen { get; private set; }

        // SmoothDamp's per-axis velocity state. Must be reset alongside any
        // hard override of the angle it belongs to, or the servo carries stale
        // momentum into the handover and flings.
        private float _pitchVel, _rollVel;

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
        /// heading, not a lean, so there's no "neutral" to return to; forcing
        /// it to 0 here would desync the accumulator from the car's real
        /// orientation for the rest of the drive. Used for idle/parked/
        /// questionnaire-reset/emergency-stop — takes priority over an
        /// in-flight Freeze (an emergency must always be able to override a
        /// frozen seat).</summary>
        public void ReturnToNeutral(float seconds)
        {
            IsFrozen = false;
            IsReturningToNeutral = true;
            _returnDuration = Mathf.Max(0.01f, seconds);
            _returnTimer = 0f;
            _returnFromPitch = PitchDeg;
            _returnFromRoll = RollDeg;
            _pitchVel = _rollVel = 0f;
            // Zeroed alongside the seat: the jerk limiter is frozen for the
            // duration of the return, so leaving it holding a live value would
            // make the lean jump straight back to that depth the moment
            // control is handed back, undoing the whole point of the return.
            ShapedAccelMs2 = 0f;
        }

        /// <summary>Hand control back to the live physics-driven cue immediately.</summary>
        public void CancelReturnToNeutral()
        {
            IsReturningToNeutral = false;
            _pitchVel = _rollVel = 0f;
        }

        /// <summary>Hold the seat exactly where it currently is, ignoring the
        /// physics-driven cue, until Unfreeze() is called. For the explicit
        /// per-iteration rating questionnaire — the car itself is frozen in
        /// place (not parked/reset), so the seat should match: whatever
        /// force was live when the freeze hit is what stays felt.</summary>
        public void FreezeInPlace()
        {
            IsReturningToNeutral = false;
            IsFrozen = true;
            _pitchVel = _rollVel = 0f;
        }

        /// <summary>Hand control back to the live physics-driven cue. Safe to
        /// call even when not frozen (idempotent) — every ResumeDriving()
        /// call site pairs with this.</summary>
        public void Unfreeze()
        {
            IsFrozen = false;
            _pitchVel = _rollVel = 0f;
        }

        private void Update()
        {
            if (car == null || referenceTransform == null) return;
            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            // Raw inputs — always tracked, even mid return-to-neutral or
            // frozen, so the log and the UI reflect what the car is actually
            // doing rather than what the seat is being allowed to show.
            AccelMs2 = car.CommandedAccel;
            SpeedGapMs = car.SpeedGap;
            YawRateDegPerSec = car.YawRateRadPerSec * Mathf.Rad2Deg;

            // Frozen: leave PitchDeg/RollDeg/YawDeg and the reference transform
            // exactly as they were the instant FreezeInPlace() was called.
            if (IsFrozen) return;

            if (IsReturningToNeutral)
            {
                _returnTimer += dt;
                float t = Mathf.Clamp01(_returnTimer / _returnDuration);
                PitchDeg = Mathf.Lerp(_returnFromPitch, 0f, t);
                RollDeg = Mathf.Lerp(_returnFromRoll, 0f, t);
                WriteReferenceTransform();
                if (t >= 1f)
                {
                    IsReturningToNeutral = false;
                    _pitchVel = _rollVel = 0f;
                }
                return;
            }

            // ── PITCH: acceleration, tapered by how much of the speed
            // change is still ahead ─────────────────────────────────────
            // `intensity` is the whole idea. At 1 the car has plenty of
            // speed change left to do, so the lean sits at full depth. As
            // the gap closes it falls in proportion, and it is exactly 0
            // when the manoeuvre completes — so the seat arrives level at
            // the same moment the car arrives at cruise, instead of holding
            // a fixed tilt and then dropping it all at once. The same shape
            // runs in reverse on the way down: full forward lean while
            // there's still speed to shed, easing to level as the car
            // actually comes to rest.
            //
            // The jerk limit shapes the ONSET. Without it, AccelMs2 steps from
            // 0 to the style's full braking magnitude in a single frame, so
            // targetPitch does too and the servo chases a step — which is
            // precisely what "abrupt braking" feels like on the rig. Limiting
            // how fast the rendered acceleration may change ramps the lean in
            // and back out over a real duration, and deliberately does NOT
            // touch the plateau, so the gentle-vs-assertive difference the
            // optimiser is manipulating survives intact.
            ShapedAccelMs2 = maxJerkMs3 > 0f
                ? Mathf.MoveTowards(ShapedAccelMs2, AccelMs2, maxJerkMs3 * dt)
                : AccelMs2;

            // Deliberately NOT clamped to 1. Letting intensity run above 1 is
            // what removes the need for a separate degrees-per-acceleration
            // gain: the two constants collapse into this one, and the lean
            // becomes cleanly proportional to (acceleration × size of the
            // manoeuvre). It also removes the plateau the clamp used to create
            // — the lean now peaks at the very start of a manoeuvre and decays
            // for its entire length, rather than holding flat and only tapering
            // near the end. maxPitchDeg is still the hard ceiling.
            float intensity = Mathf.Abs(SpeedGapMs) / Mathf.Max(0.1f, speedGapReferenceMs);
            float targetPitch = Mathf.Clamp(-ShapedAccelMs2 * intensity,
                                            -maxPitchDeg, maxPitchDeg);
            if (invertPitch) targetPitch = -targetPitch;

            // ── ROLL: turn rate, sustained for as long as the bend lasts ──
            float targetRoll = useRollForCornering
                ? Mathf.Clamp(car.YawRateRadPerSec * rollDegPerYawRate, -maxRollDeg, maxRollDeg)
                : 0f;
            if (invertRoll) targetRoll = -targetRoll;

            // ── Servo: critically damped, velocity-capped ────────────────
            PitchDeg = Mathf.SmoothDamp(PitchDeg, targetPitch, ref _pitchVel,
                                        responseSeconds, maxDegreesPerSecond, dt);
            RollDeg = Mathf.SmoothDamp(RollDeg, targetRoll, ref _rollVel,
                                       responseSeconds, maxDegreesPerSecond, dt);

            // ── YAW: an accumulator, not a target ────────────────────────
            // Integrated from the same clean turn rate the roll cue uses, at
            // a fraction of reality, with a slow bleed back toward centre.
            // When switched off it eases back to 0 at the same rate ceiling
            // pitch/roll obey, rather than snapping.
            if (useYawForCornering)
            {
                YawDeg += YawRateDegPerSec * yawFollowFraction * dt;
                if (yawReturnSeconds > 0f)
                    YawDeg *= Mathf.Exp(-dt / yawReturnSeconds);
            }
            else if (YawDeg != 0f)
            {
                YawDeg = Mathf.MoveTowards(YawDeg, 0f, maxDegreesPerSecond * dt);
            }

            WriteReferenceTransform();
        }

        /// <summary>Visualisation only — the Scene view and the researcher UI's
        /// forces gizmo. The rig is driven from PitchDeg/RollDeg/YawDeg
        /// directly, so nothing depends on this round-tripping cleanly.</summary>
        private void WriteReferenceTransform() =>
            referenceTransform.localRotation = Quaternion.Euler(PitchDeg, YawDeg, -RollDeg);
    }
}
