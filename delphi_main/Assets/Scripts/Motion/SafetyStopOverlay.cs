using UnityEngine;
using Delphi.Session;

namespace Delphi.Motion
{
    /// <summary>
    /// Shows/hides a participant-facing world-space overlay only while
    /// SessionController.CurrentPhase == Phase.EmergencyStop.
    ///
    /// Owns none of the visual content on purpose — point overlayRoot at a
    /// world-space Canvas authored directly in the scene so you can add
    /// images, extra text, or restyle it freely in the Editor. This script
    /// just flips it on/off.
    ///
    /// WHERE TO PARENT IT: "[VR] Seat Reference", the driver's eye point
    /// fixed to the car (VrRig creates it above "Person View"). NOT the
    /// camera — in the headset that welds the overlay to the participant's
    /// face, which is both unreadable and nauseating, and this thing shows
    /// up precisely when they are already having a bad time. Sit it around
    /// 1.5–2 m ahead so it converges comfortably in stereo. Without a
    /// headset "Person View" and the seat reference are the same place, so
    /// the desktop setup is unaffected either way.
    /// </summary>
    public class SafetyStopOverlay : MonoBehaviour
    {
        [Header("Links (auto-found if left empty)")]
        public SessionController session;
        [Tooltip("The world-space overlay (Canvas root) to show only during " +
                 "an emergency stop. Author its content freely in the Editor.")]
        public GameObject overlayRoot;

        private void Awake()
        {
            if (session == null) session = FindFirstObjectByType<SessionController>();
            if (overlayRoot != null) overlayRoot.SetActive(false);
        }

        private void Update()
        {
            if (overlayRoot == null || session == null) return;
            bool shouldShow = session.CurrentPhase == SessionController.Phase.EmergencyStop;
            if (overlayRoot.activeSelf != shouldShow) overlayRoot.SetActive(shouldShow);
        }
    }
}
