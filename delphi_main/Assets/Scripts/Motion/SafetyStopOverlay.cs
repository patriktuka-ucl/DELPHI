using UnityEngine;
using Delphi.Session;

namespace Delphi.Motion
{
    /// <summary>
    /// Shows/hides a participant-facing world-space overlay only while
    /// SessionController.CurrentPhase == Phase.EmergencyStop.
    ///
    /// Owns none of the visual content on purpose — point overlayRoot at a
    /// world-space Canvas authored directly in the scene (parented to
    /// "Person View", the participant's viewpoint camera — there's no XR
    /// headset in this project, so that plain desktop Camera parented to
    /// CarDriver is what the participant actually looks at) so you can add
    /// images, extra text, or restyle it freely in the Editor. This script
    /// just flips it on/off.
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
