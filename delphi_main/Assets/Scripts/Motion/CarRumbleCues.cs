using UnityEngine;
using Delphi.Simulation;

namespace Delphi.Motion
{
    /// <summary>
    /// Turns the car's motion into VIBRATION cues for the YAW VR3's three seat
    /// buzzers — the second, fully independent cue channel alongside the seat
    /// tilt that CarMotionCues drives.
    ///
    /// WHY THIS EXISTS. Tilt says "the car is doing something" by moving your
    /// body. Rumble says it by changing what your skin feels, with the seat
    /// essentially still. Run alone it gives motion cues at near-zero physical
    /// displacement; run alongside tilt it accents it. Which of the two is
    /// live is decided in YawVR3Connection (tiltEnabled / rumbleEnabled) and
    /// neither knows or cares about the other.
    ///
    /// WHAT WAS THERE BEFORE, AND WHY IT COULDN'T BE FELT. The old rumble was
    /// four fields on YawVR3Connection computing `base + scale × km/h`, sent
    /// identically to all three motors at a fixed 45 Hz. Three separate
    /// problems, all fatal:
    ///
    ///   1. It encoded SPEED, not events. Cruising at 50 km/h and braking hard
    ///      from 50 km/h produced the same buzz. There was no acceleration,
    ///      braking or cornering information in the signal at all — nothing to
    ///      notice, because nothing changed.
    ///   2. It was CONSTANT. These motors adapt out of awareness in a second
    ///      or two; a level hum is gone from perception long before the first
    ///      corner. Amplitude only registers as an EVENT when it changes.
    ///   3. It was BELOW THE FLOOR. 4 + 0.15 × 50 ≈ 11 out of 100. An
    ///      eccentric-mass motor at 11% duty barely turns over.
    ///
    /// THE MODEL. Three layers, each independently switchable, mixed into the
    /// three channels the protocol carries (V[right, centre, left, hz]):
    ///
    ///   ROAD BED   ← speed. The quiet background the other two read AGAINST.
    ///                A rise from a live floor is far more detectable than the
    ///                same rise out of silence, and it keeps the seat feeling
    ///                like a running car rather than a dead chair.
    ///
    ///   LONGITUDINAL ← acceleration and braking. The channel layout is
    ///                LATERAL (right/centre/left) — there is no front/back
    ///                pair — so accelerating and braking cannot be told apart
    ///                by WHERE the buzz is. They are told apart by TEXTURE
    ///                instead, on three axes at once:
    ///
    ///                  frequency    accel = low and throaty (engine under
    ///                               load); brake = high and harsh (judder)
    ///                  envelope     accel = swells in; brake = sharp attack
    ///                               then decay
    ///                  spread       accel = centre-weighted; brake = pushed
    ///                               out to the two side pads
    ///
    ///                Three coincident differences are discriminable where two
    ///                amplitudes of the same buzz are not.
    ///
    ///   LATERAL    ← turn rate, as a straight left/right pan. This one DOES
    ///                have a spatial axis, and it is the cheapest, clearest
    ///                cue available here: in a right-hand bend your body loads
    ///                the left bolster, so the left pad drives harder. Sign is
    ///                a convention (invertLateralPan) — settle it on the rig.
    ///
    /// THE ENVELOPE IS THE POINT. Every layer's magnitude runs through an
    /// attack/release follower plus a slow ADAPTATION follower, and what
    /// actually gets sent is a decayed sustain plus a transient proportional
    /// to how far the signal has run ahead of what you've adapted to. So the
    /// onset of a brake punches, and holding that brake settles back rather
    /// than droning. This, not raw intensity, is what makes an event land.
    ///
    /// THE FLOOR IS REAL. Normalised 0–1 cue strength maps onto
    /// [minEffectiveIntensity … maxIntensity], not [0 … max], with anything
    /// under silenceThreshold going hard to 0. Below the floor the motor draws
    /// current and does nothing, so linear-from-zero throws away the entire
    /// bottom of the range. minEffectiveIntensity is a MEASURED quantity —
    /// find it with YawVR3Tester's rumble bench, don't guess it.
    ///
    /// Like CarMotionCues, nothing here is differentiated: every input comes
    /// from CarDriver's own published intent, and the longitudinal magnitude
    /// prefers CarMotionCues.ShapedAccelMs2 so rumble and tilt always agree
    /// about what the car is doing.
    /// </summary>
    [DefaultExecutionOrder(110)] // after CarMotionCues (100) has shaped this frame's acceleration
    public class CarRumbleCues : MonoBehaviour
    {
        [Header("Links (auto-found if left empty)")]
        public CarDriver car;
        [Tooltip("Optional. Used for its jerk-shaped acceleration (so rumble " +
                 "and tilt agree on what the car is doing) and for its freeze/" +
                 "return-to-neutral state. Without it the raw commanded " +
                 "acceleration is used instead and the session mutes below " +
                 "have nothing to follow.")]
        public CarMotionCues cues;

        // ── Master ───────────────────────────────────────────────────────
        [Header("Master")]
        [Tooltip("Overall strength of everything below. The one knob to reach " +
                 "for first when the whole effect is too weak or too strong.")]
        [Range(0f, 2f)] public float masterGain = 1f;
        [Tooltip("Extra gain applied when rumble is running WITHOUT tilt " +
                 "(YawVR3Connection.tiltEnabled off). In that mode rumble is " +
                 "carrying the entire cue rather than accenting a lean, so it " +
                 "should work harder. 1 = no difference between the modes.")]
        [Range(1f, 2.5f)] public float soloGain = 1.4f;

        [Header("Output range — the motors' real working window, NOT 0-100")]
        [Tooltip("The lowest intensity that actually turns the motor over. " +
                 "Everything nonzero is mapped into [this … max], because " +
                 "commanding 8/100 produces no sensation at all — it just " +
                 "wastes the bottom third of the scale. MEASURE THIS on the " +
                 "rig with YawVR3Tester's floor sweep rather than guessing.")]
        [Range(0, 100)] public int minEffectiveIntensity = 18;
        [Tooltip("Ceiling for any single motor. Well below 100 by default — " +
                 "a buzzer at full duty is loud, fatiguing over a session, and " +
                 "leaves no headroom for a transient to stand out against.")]
        [Range(0, 100)] public int maxIntensity = 85;
        [Tooltip("Normalised cue strength below which a channel is switched " +
                 "fully OFF rather than mapped to the floor. Without this, an " +
                 "idle car would sit permanently at minEffectiveIntensity.")]
        [Range(0f, 0.2f)] public float silenceThreshold = 0.02f;
        [Tooltip("Frequency limits clamped onto every layer's Hz. The rig's " +
                 "usable range is hardware, not protocol — confirm it with " +
                 "the tester's Hz sweep and tighten these to what you can " +
                 "actually feel a difference across.")]
        public int minHz = 10;
        public int maxHz = 100;

        // ── Layer A: road bed ────────────────────────────────────────────
        [Header("Layer A — road bed (speed). The background the event layers " +
                "are heard against; also what stops the seat feeling dead.")]
        public bool roadBedEnabled = true;
        [Tooltip("Bed strength at roadBedReferenceKmh, as a fraction of full " +
                 "scale. Keep it low — this is a floor, not a cue.")]
        [Range(0f, 1f)] public float roadBedStrength = 0.18f;
        [Tooltip("Speed at which the bed reaches roadBedStrength. It scales " +
                 "linearly below this and keeps climbing above it (clamped by " +
                 "the ceiling), so a faster road is a busier road.")]
        public float roadBedReferenceKmh = 50f;
        [Tooltip("Bed frequency. LOW reads as a soft, distant hum even at a " +
                 "given intensity — the cheapest way to make the bed quieter " +
                 "without dropping it under the motor floor and losing it.")]
        public int roadBedHz = 40;

        // ── Layer B: longitudinal ────────────────────────────────────────
        [Header("Layer B — longitudinal (accelerating / braking). The core " +
                "cue. Direction is carried by texture, not position — there " +
                "is no front/back pair of motors to use.")]
        public bool longitudinalEnabled = true;
        [Tooltip("The acceleration (m/s²) that drives this layer to full " +
                 "scale. 3 puts a mid-assertive brake near the top of the " +
                 "range and leaves the hardest 5 m/s² stop clipped there — " +
                 "deliberate: past this point the DIFFERENCE is carried by " +
                 "the transient and the frequency, not by more amplitude.")]
        [Range(0.5f, 10f)] public float referenceAccelMs2 = 3f;
        [Tooltip("Curve applied to normalised magnitude. Below 1 lifts small " +
                 "events so a gentle manoeuvre still clears the motor floor " +
                 "instead of vanishing; 1 = linear.")]
        [Range(0.3f, 2f)] public float longitudinalGamma = 0.7f;
        [Range(0f, 2f)] public float longitudinalGain = 1f;

        [Header("Layer B envelope — where 'noticeable' actually comes from")]
        [Tooltip("How fast the buzz rises when a manoeuvre starts. Short = " +
                 "the onset hits as an event rather than a swell.")]
        [Range(0.01f, 1f)] public float attackSeconds = 0.06f;
        [Tooltip("How fast it falls when the manoeuvre ends. Longer than the " +
                 "attack, so events feel like they decay rather than being " +
                 "cut off.")]
        [Range(0.05f, 2f)] public float releaseSeconds = 0.35f;
        [Tooltip("How quickly the body is modelled as adapting to a sustained " +
                 "buzz. The transient below is measured against this slow " +
                 "follower, so a signal that has been steady for longer than " +
                 "this produces no punch — exactly like real skin.")]
        [Range(0.2f, 6f)] public float adaptationSeconds = 1.6f;
        [Tooltip("How much of full magnitude survives once you HAVE adapted. " +
                 "The plateau a long, steady brake settles to. 1 = no decay, " +
                 "back to the old drone.")]
        [Range(0f, 1f)] public float sustainLevel = 0.55f;
        [Tooltip("Size of the onset punch, as a multiple of how far the signal " +
                 "has run ahead of the adaptation follower. 0 = no transient, " +
                 "sustain only.")]
        [Range(0f, 3f)] public float transientGain = 1f;

        [Header("Layer B texture — how accelerating and braking differ")]
        [Tooltip("Accelerating: centre pad share. Centre-weighted reads as a " +
                 "single push from behind you.")]
        [Range(0f, 1f)] public float accelCentreWeight = 1f;
        [Range(0f, 1f)] public float accelSideWeight = 0.45f;
        [Tooltip("Braking: centre pad share. Pushed out to the sides, which " +
                 "reads as wider and more urgent than the same energy in the " +
                 "middle.")]
        [Range(0f, 1f)] public float brakeCentreWeight = 0.5f;
        [Range(0f, 1f)] public float brakeSideWeight = 1f;
        [Tooltip("Frequency while speeding up — low and throaty.")]
        public int accelHz = 32;
        [Tooltip("Frequency while slowing down — high and harsh. The gap " +
                 "between this and accelHz is what makes the two readable as " +
                 "different EVENTS rather than as more or less of one thing, " +
                 "so widen it before reaching for more intensity.")]
        public int brakeHz = 72;
        [Tooltip("Acceleration (m/s²) over which the accel/brake blend " +
                 "crosses over. Small, but nonzero — a hard switch at exactly " +
                 "zero would make the frequency snap every time the car trims " +
                 "its speed.")]
        [Range(0.05f, 2f)] public float accelBrakeBlendMs2 = 0.3f;

        // ── Layer C: lateral ─────────────────────────────────────────────
        [Header("Layer C — lateral (cornering). A left/right pan, which is " +
                "what the three-channel layout is actually for.")]
        public bool lateralEnabled = true;
        [Tooltip("Turn rate (deg/s) that drives this layer to full scale. A " +
                 "50 m bend at 12 m/s is about 14°/s, so 15 puts a normal " +
                 "corner near the top.")]
        [Range(1f, 60f)] public float referenceYawRateDegPerSec = 15f;
        [Range(0.3f, 2f)] public float lateralGamma = 0.75f;
        [Range(0f, 2f)] public float lateralGain = 1f;
        [Tooltip("What the INSIDE pad gets while the outside pad is at full. " +
                 "Above 0 so the corner reads as a pan across the seat rather " +
                 "than one pad switching on — a pan is localisable, a lone " +
                 "buzz mostly isn't.")]
        [Range(0f, 1f)] public float lateralInnerFraction = 0.25f;
        [Tooltip("Centre pad share during a corner. Small: the centre sits on " +
                 "the axis the pan is swinging around, so driving it hard just " +
                 "smears out the left/right information.")]
        [Range(0f, 1f)] public float lateralCentreFraction = 0.15f;
        [Tooltip("Mid frequency — deliberately between accelHz and brakeHz so " +
                 "a corner is texturally distinct from both.")]
        public int lateralHz = 55;
        [Tooltip("Which pad leads in a bend. Default: turning RIGHT drives the " +
                 "LEFT pad, matching the bolster your body actually loads. " +
                 "Tick if it reads backwards on the rig.")]
        public bool invertLateralPan = false;

        // ── Session behaviour ────────────────────────────────────────────
        [Header("Session behaviour — follows CarMotionCues' own state")]
        [Tooltip("Silence rumble while the seat is frozen for the per-" +
                 "iteration rating. The tilt deliberately HOLDS there so the " +
                 "live force stays felt, and holding the buzz would be the " +
                 "consistent choice — but a continuous vibration underneath a " +
                 "rating task is a confound in a way a static lean is not. " +
                 "Default off; untick for strict consistency with the tilt.")]
        public bool muteWhileFrozen = true;
        [Tooltip("Silence rumble while the seat is easing back to level — " +
                 "park, questionnaire reset, and emergency stop all route " +
                 "through that path, so this is also what kills the buzz on " +
                 "an e-stop.")]
        public bool muteWhileReturningToNeutral = true;
        [Tooltip("Fade time for those mutes, in and out. Short, but not " +
                 "instant — a hard cut is itself a noticeable event.")]
        [Range(0.05f, 2f)] public float muteFadeSeconds = 0.4f;

        // ── Published state (read by YawVR3Connection, the UI, the log) ──
        /// <summary>Right seat pad intensity, 0-100, ready for the wire.</summary>
        public int MotorRight { get; private set; }
        /// <summary>Centre seat pad intensity, 0-100.</summary>
        public int MotorCentre { get; private set; }
        /// <summary>Left seat pad intensity, 0-100.</summary>
        public int MotorLeft { get; private set; }
        /// <summary>Commanded frequency, Hz. One value for all three pads —
        /// the protocol carries a single frequency per packet — so this is a
        /// level-weighted blend of whichever layers are currently loud.</summary>
        public int Hz { get; private set; }

        /// <summary>True when every pad is off. Distinct from "quiet": the
        /// floor mapping means a channel is either silent or at least
        /// minEffectiveIntensity.</summary>
        public bool IsSilent => MotorRight == 0 && MotorCentre == 0 && MotorLeft == 0;

        /// <summary>Set by YawVR3Connection each tick: true when rumble is
        /// running without tilt, which applies soloGain. Not serialized —
        /// it's a live mode flag, not a setting.</summary>
        public bool SoloMode { get; set; }

        // Normalised 0-1 diagnostics, for the custom inspector and the log.
        /// <summary>Road bed contribution this frame, 0-1.</summary>
        public float RoadBedLevel { get; private set; }
        /// <summary>Longitudinal contribution AFTER the envelope, 0-1 — the
        /// number the punch-then-settle shape actually lives in.</summary>
        public float LongitudinalLevel { get; private set; }
        /// <summary>Longitudinal magnitude BEFORE the envelope, 0-1. Watching
        /// this against LongitudinalLevel is how you see the transient and the
        /// adaptation decay doing their jobs.</summary>
        public float LongitudinalRaw { get; private set; }
        /// <summary>Lateral contribution, 0-1.</summary>
        public float LateralLevel { get; private set; }
        /// <summary>0 = pure acceleration, 1 = pure braking. Drives both the
        /// frequency blend and the centre/side spread.</summary>
        public float Brakeness { get; private set; }
        /// <summary>Current mute fade, 1 = fully live, 0 = fully muted.</summary>
        public float MuteBlend { get; private set; } = 1f;

        // Envelope followers — see the class comment. _fast tracks the signal
        // with attack/release; _slow tracks _fast on the adaptation constant,
        // and the gap between them IS the transient.
        private float _fast, _slow;
        private float _signedAccel;

        private void Awake()
        {
            if (car == null) car = FindAnyObjectByType<CarDriver>();
            if (cues == null) cues = FindAnyObjectByType<CarMotionCues>();
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            if (car == null)
            {
                MotorRight = MotorCentre = MotorLeft = 0;
                Hz = Mathf.Clamp(roadBedHz, minHz, maxHz);
                return;
            }

            // ── Mute fade ────────────────────────────────────────────────
            bool muted = cues != null &&
                         ((muteWhileFrozen && cues.IsFrozen) ||
                          (muteWhileReturningToNeutral && cues.IsReturningToNeutral));
            MuteBlend = Mathf.MoveTowards(MuteBlend, muted ? 0f : 1f,
                                          dt / Mathf.Max(0.05f, muteFadeSeconds));

            // ── Layer A: road bed ────────────────────────────────────────
            // Deliberately not clamped at roadBedStrength: above the reference
            // speed the bed keeps climbing, so a faster road feels busier.
            // The output ceiling is the only limit.
            RoadBedLevel = roadBedEnabled
                ? Mathf.Max(0f, car.CurrentSpeedKmh) / Mathf.Max(1f, roadBedReferenceKmh) * roadBedStrength
                : 0f;

            // ── Layer B: longitudinal ────────────────────────────────────
            // Prefer the tilt's jerk-shaped acceleration so the two cues can
            // never disagree about the onset of a manoeuvre; fall back to the
            // car's raw commanded value if there is no CarMotionCues.
            float accel = cues != null ? cues.ShapedAccelMs2 : car.CommandedAccel;
            _signedAccel = ExpTo(_signedAccel, accel, attackSeconds, dt);

            float rawMagnitude = longitudinalEnabled
                ? Mathf.Clamp01(Mathf.Abs(accel) / Mathf.Max(0.1f, referenceAccelMs2))
                : 0f;
            rawMagnitude = Mathf.Pow(rawMagnitude, Mathf.Max(0.01f, longitudinalGamma));
            LongitudinalRaw = rawMagnitude;

            // Attack on the way up, release on the way down.
            _fast = ExpTo(_fast, rawMagnitude,
                          rawMagnitude > _fast ? attackSeconds : releaseSeconds, dt);
            // What the body has adapted to, always chasing _fast from behind.
            _slow = ExpTo(_slow, _fast, adaptationSeconds, dt);
            // Steady signal => _slow catches _fast => transient collapses to 0
            // and only the decayed sustain remains. A fresh change outruns it
            // and punches through.
            float transient = Mathf.Max(0f, _fast - _slow);
            LongitudinalLevel = Mathf.Clamp01(
                (_fast * sustainLevel + transient * transientGain) * longitudinalGain);

            // Brakeness: 0 while speeding up, 1 while slowing down, blended
            // across a narrow band around zero so trimming speed doesn't make
            // the frequency chatter.
            Brakeness = Mathf.Clamp01(Mathf.InverseLerp(accelBrakeBlendMs2, -accelBrakeBlendMs2, _signedAccel));

            // ── Layer C: lateral ─────────────────────────────────────────
            float yawRateDeg = car.YawRateRadPerSec * Mathf.Rad2Deg;
            float latSigned = Mathf.Clamp(yawRateDeg / Mathf.Max(0.1f, referenceYawRateDegPerSec), -1f, 1f);
            LateralLevel = lateralEnabled
                ? Mathf.Pow(Mathf.Abs(latSigned), Mathf.Max(0.01f, lateralGamma)) * lateralGain
                : 0f;
            LateralLevel = Mathf.Clamp01(LateralLevel);

            // Pan: +1 = turning right, which loads the LEFT bolster, so the
            // left pad leads. Blended rather than switched, so a bend that
            // eases through straight doesn't jump across the seat.
            float panRight = Mathf.InverseLerp(-1f, 1f, invertLateralPan ? -latSigned : latSigned);
            float latLeftW = Mathf.Lerp(lateralInnerFraction, 1f, panRight);
            float latRightW = Mathf.Lerp(1f, lateralInnerFraction, panRight);

            // ── Mix ──────────────────────────────────────────────────────
            float longCentreW = Mathf.Lerp(accelCentreWeight, brakeCentreWeight, Brakeness);
            float longSideW = Mathf.Lerp(accelSideWeight, brakeSideWeight, Brakeness);

            float gain = masterGain * (SoloMode ? soloGain : 1f) * MuteBlend;

            float right = Combine(RoadBedLevel,
                                  LongitudinalLevel * longSideW,
                                  LateralLevel * latRightW) * gain;
            float centre = Combine(RoadBedLevel,
                                   LongitudinalLevel * longCentreW,
                                   LateralLevel * lateralCentreFraction) * gain;
            float left = Combine(RoadBedLevel,
                                 LongitudinalLevel * longSideW,
                                 LateralLevel * latLeftW) * gain;

            MotorRight = MapToMotor(right);
            MotorCentre = MapToMotor(centre);
            MotorLeft = MapToMotor(left);

            // ── Frequency ────────────────────────────────────────────────
            // One frequency for all three pads, so it belongs to whichever
            // layer is currently loudest — weighting by level means the bed's
            // hum owns it while nothing is happening and an event takes it
            // over the moment one starts.
            float longHz = Mathf.Lerp(accelHz, brakeHz, Brakeness);
            float wRoad = RoadBedLevel, wLong = LongitudinalLevel, wLat = LateralLevel;
            float wSum = wRoad + wLong + wLat;
            float hz = wSum > 0.0001f
                ? (roadBedHz * wRoad + longHz * wLong + lateralHz * wLat) / wSum
                : roadBedHz;
            Hz = Mathf.Clamp(Mathf.RoundToInt(hz), Mathf.Min(minHz, maxHz), Mathf.Max(minHz, maxHz));
        }

        /// <summary>Probabilistic OR — 1-(1-a)(1-b)(1-c). Layers ADD without
        /// ever exceeding full scale and without a clamp flattening the top of
        /// the range: a loud layer plus a quiet one still reads louder than the
        /// loud one alone, which plain saturation-by-clamping destroys exactly
        /// when the most is happening.</summary>
        private static float Combine(float a, float b, float c)
        {
            a = Mathf.Clamp01(a); b = Mathf.Clamp01(b); c = Mathf.Clamp01(c);
            return 1f - (1f - a) * (1f - b) * (1f - c);
        }

        /// <summary>Normalised strength to a wire intensity, respecting the
        /// motor's dead zone: silent below the threshold, otherwise mapped
        /// into the range the hardware can actually render.</summary>
        private int MapToMotor(float v)
        {
            v = Mathf.Clamp01(v);
            if (v < silenceThreshold) return 0;
            int lo = Mathf.Min(minEffectiveIntensity, maxIntensity);
            int hi = Mathf.Max(minEffectiveIntensity, maxIntensity);
            return Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(lo, hi, v)), 0, 100);
        }

        /// <summary>Frame-rate-independent exponential approach — the same
        /// shape as a first-order lag with time constant `tau`, so the
        /// followers behave identically at 60 and at 144 fps.</summary>
        private static float ExpTo(float current, float target, float tau, float dt)
        {
            if (tau <= 0.0001f) return target;
            return current + (target - current) * (1f - Mathf.Exp(-dt / tau));
        }
    }
}
