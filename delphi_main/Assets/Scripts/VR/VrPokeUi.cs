using System.Collections.Generic;
using Leap;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Delphi.VR
{
    /// <summary>
    /// Lets the participant press ordinary Unity UI with a fingertip.
    ///
    /// WHY THIS HAS TO EXIST:
    ///
    ///   Ultraleap's Physical Hands can push RIGID BODIES — its own buttons
    ///   and sliders are 3D objects with colliders. It has nothing at all for
    ///   uGUI: the only file in the whole runtime that references UnityEngine.UI
    ///   is an editor settings provider. And QuestionnaireToolkit builds every
    ///   page out of uGUI Buttons and Toggles at run time, so there is no
    ///   prefab to swap and no collider to push. Without something like this a
    ///   participant in the headset can SEE a questionnaire and has no way
    ///   whatsoever to answer it.
    ///
    /// WHAT IT DOES:
    ///
    ///   Each frame it takes both index fingertips from the hand tracker and
    ///   tests them against every active WORLD-SPACE canvas. A fingertip that
    ///   crosses from in front of a canvas to behind it, inside the canvas
    ///   rect, is a press: the crossing point is converted to a screen point
    ///   through that canvas's own camera, run through its GraphicRaycaster,
    ///   and dispatched as the normal pointerDown / pointerUp / click sequence.
    ///   So every existing Button, Toggle and Slider works untouched — this
    ///   drives the standard event system rather than replacing it.
    ///
    /// WHY CROSSING THE PLANE RATHER THAN PROXIMITY:
    ///
    ///   A distance threshold fires the moment a finger drifts near, which in
    ///   a questionnaire means answering a question by reaching past it. The
    ///   plane crossing has an unambiguous moment and a direction, so a finger
    ///   moving parallel to the page — the normal way people gesture while
    ///   reading — never triggers anything.
    ///
    /// Screen-space canvases are ignored on purpose: they are not in the world,
    /// so there is nothing for a finger to be in front of.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public class VrPokeUi : MonoBehaviour
    {
        [Tooltip("Hand data source. Auto-found from VrHandTracking if empty.")]
        public LeapProvider provider;

        [Tooltip("How far BEHIND a canvas the fingertip may go and still count " +
                 "as a press rather than a miss. Also the distance it must " +
                 "come back out through before the same finger can press again.")]
        public float pokeDepthMeters = 0.05f;

        [Tooltip("Padding in canvas pixels around the rect edge. Slightly " +
                 "negative keeps a finger grazing the very edge of a page from " +
                 "counting as a hit on the element nearest the border.")]
        public float edgePaddingPixels = -4f;

        [Tooltip("Ignore fingertips travelling faster than this (m/s). A hand " +
                 "swung across the panel sweeps through several controls in " +
                 "one frame; without this, that reads as pressing whichever " +
                 "one it happened to be nearest on the frame it crossed.")]
        public float maxPokeSpeed = 2.5f;

        [Tooltip("Log every press. Useful while positioning a panel, noisy " +
                 "during a real session.")]
        public bool logPresses;

        // Per-fingertip crossing state. Two entries: left index, right index.
        private readonly bool[] _wasInFront = { true, true };
        private readonly bool[] _armed = { true, true };
        private readonly Vector3[] _lastTip = new Vector3[2];
        private readonly bool[] _hasLastTip = { false, false };

        private readonly List<RaycastResult> _hits = new();
        private PointerEventData _pointer;

        // CACHED TARGETS — rebuilt on a timer, not every frame.
        //
        // This used to call FindObjectsByType<Canvas> once PER HAND PER FRAME.
        // That walks every object in the scene and allocates a fresh array each
        // time: 120 full scene scans a second, for a set that changes maybe
        // twice a session. In VR, where the frame budget is already spent four
        // times over, that is not a micro-optimisation.
        private Canvas[] _targets = System.Array.Empty<Canvas>();
        private GraphicRaycaster[] _raycasters = System.Array.Empty<GraphicRaycaster>();
        private int _targetCount;
        private float _nextScan;

        [Tooltip("How often to re-scan for world-space canvases, in seconds. " +
                 "Questionnaire pages are created mid-session, so this cannot " +
                 "be a one-off — but it does not need to be every frame either.")]
        public float rescanSeconds = 1f;

        /// <summary>Collects the world-space canvases worth testing. Screen-space
        /// ones are skipped because they are not in the world, and nested
        /// canvases because they share their root's plane.</summary>
        private void RescanTargets()
        {
            var all = FindObjectsByType<Canvas>(FindObjectsSortMode.None);
            if (_targets.Length < all.Length)
            {
                _targets = new Canvas[all.Length];
                _raycasters = new GraphicRaycaster[all.Length];
            }

            _targetCount = 0;
            foreach (var c in all)
            {
                if (c.renderMode != RenderMode.WorldSpace) continue;
                if (c.rootCanvas != c) continue;
                var r = c.GetComponent<GraphicRaycaster>();
                if (r == null) continue;
                _targets[_targetCount] = c;
                _raycasters[_targetCount] = r;
                _targetCount++;
            }
        }

        private void Start()
        {
            if (provider == null)
            {
                var ht = FindFirstObjectByType<VrHandTracking>();
                if (ht != null) provider = ht.Provider;
            }

            if (provider == null)
            {
                Debug.Log("[VrPokeUi] No hand provider — UI poking disabled. Normal on the desktop path.", this);
                enabled = false;
                return;
            }

            if (EventSystem.current == null)
            {
                Debug.LogError("[VrPokeUi] No EventSystem in the scene — uGUI cannot receive events from " +
                               "anything, poke included.", this);
                enabled = false;
                return;
            }

            _pointer = new PointerEventData(EventSystem.current);
            Debug.Log("[VrPokeUi] Fingertip UI poking active — world-space canvases are now pressable.", this);
        }

        private void Update()
        {
            if (Time.unscaledTime >= _nextScan)
            {
                _nextScan = Time.unscaledTime + Mathf.Max(0.1f, rescanSeconds);
                RescanTargets();
            }

            var frame = provider != null ? provider.CurrentFrame : null;
            if (frame == null) return;

            // Index 0 = left hand, 1 = right. Missing hands simply re-arm, so
            // a hand leaving mid-press cannot leave a button stuck down.
            ProcessHand(GetIndexTip(frame, isLeft: true), 0);
            ProcessHand(GetIndexTip(frame, isLeft: false), 1);
        }

        private static Vector3? GetIndexTip(Frame frame, bool isLeft)
        {
            foreach (var hand in frame.Hands)
            {
                if (hand.IsLeft != isLeft) continue;
                if (hand.fingers == null || hand.fingers.Length < 2) return null;
                return hand.fingers[1].TipPosition; // 1 = index
            }
            return null;
        }

        private void ProcessHand(Vector3? tipOrNull, int slot)
        {
            if (tipOrNull == null)
            {
                _hasLastTip[slot] = false;
                _armed[slot] = true;
                _wasInFront[slot] = true;
                return;
            }

            Vector3 tip = tipOrNull.Value;

            float speed = 0f;
            if (_hasLastTip[slot] && Time.deltaTime > 0f)
                speed = Vector3.Distance(tip, _lastTip[slot]) / Time.deltaTime;
            _lastTip[slot] = tip;
            _hasLastTip[slot] = true;

            // SLIDERS FIRST, and they get the fingertip exclusively.
            //
            // A slider needs CONTINUOUS input — the value follows the finger
            // for as long as it is near. The poke path below is a discrete
            // click, and letting both see the same fingertip would fire a
            // click into whatever sits behind the slider on every drag.
            for (int i = 0; i < VrTouchSlider.Active.Count; i++)
            {
                var s = VrTouchSlider.Active[i];
                if (s != null && s.isActiveAndEnabled && s.TryDrive(tip)) return;
            }

            for (int i = 0; i < _targetCount; i++)
            {
                var canvas = _targets[i];
                if (canvas == null || !canvas.isActiveAndEnabled) continue;
                if (TryPoke(canvas, _raycasters[i], tip, slot, speed)) return;
            }
        }

        /// <summary>Tests one fingertip against one canvas and fires a click if
        /// this frame is the moment it broke the surface.</summary>
        private bool TryPoke(Canvas canvas, GraphicRaycaster raycaster, Vector3 tip, int slot, float speed)
        {
            var rt = canvas.transform as RectTransform;
            if (rt == null) return false;

            // Canvas-local: +Z is out of the page, so a positive local z means
            // the finger is still in front of it.
            Vector3 local = rt.InverseTransformPoint(tip);
            bool inFront = local.z > 0f;

            var rect = rt.rect;
            bool insideRect =
                local.x >= rect.xMin - edgePaddingPixels && local.x <= rect.xMax + edgePaddingPixels &&
                local.y >= rect.yMin - edgePaddingPixels && local.y <= rect.yMax + edgePaddingPixels;

            // Depth is measured in canvas-local units; the canvas is scaled
            // metres-per-pixel, so convert the metre tolerance through it.
            float scale = Mathf.Abs(rt.lossyScale.z) > 1e-6f ? Mathf.Abs(rt.lossyScale.z) : 1f;
            float depthLocal = pokeDepthMeters / scale;

            bool justCrossed = _wasInFront[slot] && !inFront && insideRect;
            _wasInFront[slot] = inFront;

            // Re-arm once the finger is clearly back out in front, so holding a
            // fingertip inside a panel does not machine-gun the control.
            if (inFront && local.z > depthLocal * 0.5f) _armed[slot] = true;

            if (!justCrossed || !_armed[slot]) return false;
            if (local.z < -depthLocal) return false;          // punched straight through — not a press
            if (speed > maxPokeSpeed) return false;           // swiping past, not pressing

            _armed[slot] = false;
            return Dispatch(canvas, raycaster, tip);
        }

        /// <summary>Converts the fingertip to a screen point through the
        /// canvas's own camera and sends the standard uGUI click sequence, so
        /// existing Buttons and Toggles need no special handling.</summary>
        private bool Dispatch(Canvas canvas, GraphicRaycaster raycaster, Vector3 tip)
        {
            var cam = canvas.worldCamera != null ? canvas.worldCamera : Camera.main;
            if (cam == null) return false;

            _pointer.Reset();
            _pointer.position = cam.WorldToScreenPoint(tip);
            _pointer.button = PointerEventData.InputButton.Left;

            _hits.Clear();
            raycaster.Raycast(_pointer, _hits);
            if (_hits.Count == 0) return false;

            var target = _hits[0].gameObject;
            _pointer.pointerPressRaycast = _hits[0];
            _pointer.pointerCurrentRaycast = _hits[0];

            var handler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(target);
            ExecuteEvents.Execute(target, _pointer, ExecuteEvents.pointerDownHandler);
            ExecuteEvents.Execute(target, _pointer, ExecuteEvents.pointerUpHandler);
            if (handler != null) ExecuteEvents.Execute(handler, _pointer, ExecuteEvents.pointerClickHandler);

            if (logPresses)
                Debug.Log($"[VrPokeUi] Poked '{(handler != null ? handler.name : target.name)}' " +
                          $"on canvas '{canvas.name}'.", this);

            return true;
        }
    }
}
