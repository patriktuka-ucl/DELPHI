using UnityEngine;

namespace Delphi.Motion
{
    /// <summary>
    /// Minimal, isolated test harness for confirming the YAW VR3 socket
    /// connection actually works — completely bypasses CarMotionCues and the
    /// car simulation. Nudge yaw/pitch/roll by a small step and watch the
    /// physical rig respond, before trusting the full physics-driven
    /// pipeline. See YawVR3TesterEditor (Editor/) for the Inspector buttons.
    ///
    /// While ManualModeActive, YawVR3Connection sends THESE angles instead
    /// of reading CarMotionCues — ExitManualMode() hands control straight
    /// back. Nothing is ever sent over the wire unless the rig is actually
    /// Started (see YawVR3Connection.State) — nudging while not started just
    /// updates the pending values harmlessly.
    ///
    /// THE RUMBLE BENCH (second half of this file) is the same idea for the
    /// three seat buzzers, and it exists because CarRumbleCues has two
    /// constants that CANNOT be reasoned out in software:
    ///
    ///   • minEffectiveIntensity — the duty cycle below which the motor
    ///     doesn't actually turn over. Every intensity under it is wasted
    ///     scale, and getting it wrong is the single likeliest reason a cue
    ///     "isn't noticeable".
    ///   • the usable Hz window — which frequencies this hardware renders
    ///     distinguishably, which is what accel-vs-brake is encoded in.
    ///
    /// Both are measured with the sweeps below: run one, press the button the
    /// moment you feel the change, read the recorded number off the Inspector,
    /// type it into CarRumbleCues. Isolate() drives a single pad so you can
    /// also confirm which physical pad r/c/l actually correspond to — that
    /// mapping comes from reading YawVR's SDK, not from the manufacturer.
    /// </summary>
    public class YawVR3Tester : MonoBehaviour
    {
        [Header("Links (auto-found if left empty)")]
        public YawVR3Connection connection;

        [Header("Safety")]
        [Tooltip("Degrees moved per nudge button press.")]
        public float stepDegrees = 5f;
        [Tooltip("Hard ceiling for manual testing — deliberately tighter " +
                 "than CarMotionCues' own 40° production limit.")]
        public float maxTestDegrees = 20f;

        [Header("Rumble bench")]
        [Tooltip("Intensity change per nudge press, and the step the floor " +
                 "sweep climbs by.")]
        [Range(1, 25)] public int rumbleStep = 5;
        [Tooltip("Hz change per nudge press, and the step the Hz sweep " +
                 "climbs by.")]
        [Range(1, 25)] public int hzStep = 5;
        [Tooltip("How long each sweep step is held before moving on. Long " +
                 "enough to actually notice, short enough that a full 0-100 " +
                 "sweep isn't tedious.")]
        [Range(0.2f, 5f)] public float sweepStepSeconds = 1.2f;

        public bool ManualModeActive { get; private set; }
        public float Yaw { get; private set; }
        public float Pitch { get; private set; }
        public float Roll { get; private set; }

        private void Awake()
        {
            if (connection == null) connection = FindAnyObjectByType<YawVR3Connection>();
        }

        public void EnterManualMode()
        {
            ManualModeActive = true;
            Yaw = Pitch = Roll = 0f;
            Push();
        }

        public void ExitManualMode()
        {
            ManualModeActive = false;
            connection?.ClearManualOverride();
        }

        public void NudgeYaw(float deltaDeg)
        {
            if (!ManualModeActive) return;
            Yaw = Mathf.Clamp(Yaw + deltaDeg, -maxTestDegrees, maxTestDegrees);
            Push();
        }

        public void NudgePitch(float deltaDeg)
        {
            if (!ManualModeActive) return;
            Pitch = Mathf.Clamp(Pitch + deltaDeg, -maxTestDegrees, maxTestDegrees);
            Push();
        }

        public void NudgeRoll(float deltaDeg)
        {
            if (!ManualModeActive) return;
            Roll = Mathf.Clamp(Roll + deltaDeg, -maxTestDegrees, maxTestDegrees);
            Push();
        }

        public void ResetToLevel()
        {
            if (!ManualModeActive) return;
            Yaw = Pitch = Roll = 0f;
            Push();
        }

        public void StartMotion() => connection?.StartMotion();
        public void StopMotion() => connection?.StopMotion();

        private void Push() => connection?.SetManualAngles(Yaw, Pitch, Roll);

        // ════════════════════════════════════════════════════════════════
        //  RUMBLE BENCH — independent of manual ANGLE mode above. You can
        //  run either alone; testing vibration does not require putting the
        //  rig into a tilt override, and shouldn't.
        // ════════════════════════════════════════════════════════════════

        /// <summary>Which pads a bench command drives. Named for the wire
        /// order the protocol uses (V[right, centre, left, hz]) — Isolate is
        /// how you verify that order against the physical seat.</summary>
        public enum RumbleChannel { All, Right, Centre, Left }

        public bool RumbleTestActive { get; private set; }
        public int TestIntensity { get; private set; }
        public int TestHz { get; private set; } = 40;
        public RumbleChannel TestChannel { get; private set; } = RumbleChannel.All;

        /// <summary>What kind of sweep, if any, is currently running.</summary>
        public enum SweepMode { None, Intensity, Frequency }
        public SweepMode ActiveSweep { get; private set; }

        /// <summary>Intensity recorded by MarkThreshold() during an intensity
        /// sweep — the value to copy into CarRumbleCues.minEffectiveIntensity.
        /// -1 = nothing recorded yet.</summary>
        public int MeasuredFloorIntensity { get; private set; } = -1;
        /// <summary>Frequency recorded by MarkThreshold() during an Hz sweep.
        /// Run it twice — once for the lowest Hz you can feel and once for the
        /// highest that still feels different — to bracket CarRumbleCues'
        /// minHz/maxHz. -1 = nothing recorded yet.</summary>
        public int MeasuredHz { get; private set; } = -1;

        private float _sweepNextStepTime;

        public void EnterRumbleTest()
        {
            RumbleTestActive = true;
            TestIntensity = 0;
            PushRumble();
        }

        /// <summary>Stop driving the pads directly and hand the vibration
        /// field back to CarRumbleCues. No explicit zero is needed on the way
        /// out: the rig holds the last vibration command it received, but the
        /// very next tick sends a full V field either way — the model's values
        /// if rumble is on, zeros if it isn't — so the bench level is always
        /// replaced rather than left running.</summary>
        public void ExitRumbleTest()
        {
            RumbleTestActive = false;
            ActiveSweep = SweepMode.None;
            connection?.ClearManualRumble();
        }

        public void SetChannel(RumbleChannel channel)
        {
            TestChannel = channel;
            PushRumble();
        }

        public void NudgeIntensity(int delta)
        {
            if (!RumbleTestActive) return;
            ActiveSweep = SweepMode.None;
            TestIntensity = Mathf.Clamp(TestIntensity + delta, 0, 100);
            PushRumble();
        }

        public void NudgeHz(int delta)
        {
            if (!RumbleTestActive) return;
            TestHz = Mathf.Clamp(TestHz + delta, 0, 200);
            PushRumble();
        }

        /// <summary>Climb intensity from 0 in rumbleStep increments, holding
        /// each for sweepStepSeconds. Press MarkThreshold() the instant you
        /// first feel anything: that number is the motor floor.</summary>
        public void StartIntensitySweep()
        {
            if (!RumbleTestActive) EnterRumbleTest();
            ActiveSweep = SweepMode.Intensity;
            TestIntensity = 0;
            _sweepNextStepTime = Time.unscaledTime + sweepStepSeconds;
            PushRumble();
        }

        /// <summary>Climb frequency from 0 at a fixed, comfortably audible
        /// intensity. Mark the lowest Hz you feel and the highest that still
        /// feels DIFFERENT from the one before — those bracket the window
        /// accel-vs-brake can be encoded in.</summary>
        public void StartFrequencySweep()
        {
            if (!RumbleTestActive) EnterRumbleTest();
            ActiveSweep = SweepMode.Frequency;
            TestHz = 0;
            // Mid-scale so the sweep is about frequency alone, not amplitude.
            TestIntensity = Mathf.Max(TestIntensity, 50);
            _sweepNextStepTime = Time.unscaledTime + sweepStepSeconds;
            PushRumble();
        }

        /// <summary>Freeze the running sweep and record where it got to.</summary>
        public void MarkThreshold()
        {
            switch (ActiveSweep)
            {
                case SweepMode.Intensity: MeasuredFloorIntensity = TestIntensity; break;
                case SweepMode.Frequency: MeasuredHz = TestHz; break;
                default: return;
            }
            ActiveSweep = SweepMode.None;
        }

        public void StopSweep() => ActiveSweep = SweepMode.None;

        public void ClearMeasurements()
        {
            MeasuredFloorIntensity = -1;
            MeasuredHz = -1;
        }

        private void Update()
        {
            if (!RumbleTestActive || ActiveSweep == SweepMode.None) return;
            if (Time.unscaledTime < _sweepNextStepTime) return;
            _sweepNextStepTime = Time.unscaledTime + Mathf.Max(0.1f, sweepStepSeconds);

            if (ActiveSweep == SweepMode.Intensity)
            {
                TestIntensity += rumbleStep;
                if (TestIntensity >= 100) { TestIntensity = 100; ActiveSweep = SweepMode.None; }
            }
            else
            {
                TestHz += hzStep;
                if (TestHz >= 120) { TestHz = 120; ActiveSweep = SweepMode.None; }
            }
            PushRumble();
        }

        private void PushRumble()
        {
            if (connection == null || !RumbleTestActive) return;
            int r = TestChannel is RumbleChannel.All or RumbleChannel.Right ? TestIntensity : 0;
            int c = TestChannel is RumbleChannel.All or RumbleChannel.Centre ? TestIntensity : 0;
            int l = TestChannel is RumbleChannel.All or RumbleChannel.Left ? TestIntensity : 0;
            connection.SetManualRumble(r, c, l, TestHz);
        }

        // Never leave the pads buzzing because Play mode ended or the object
        // was disabled mid-test — the rig holds the last command it was sent.
        private void OnDisable()
        {
            if (RumbleTestActive) ExitRumbleTest();
        }
    }
}
