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

        public bool ManualModeActive { get; private set; }
        public float Yaw { get; private set; }
        public float Pitch { get; private set; }
        public float Roll { get; private set; }

        private void Awake()
        {
            if (connection == null) connection = FindFirstObjectByType<YawVR3Connection>();
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
    }
}
