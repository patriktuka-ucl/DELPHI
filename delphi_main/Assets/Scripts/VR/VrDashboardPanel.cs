using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.XR;

namespace Delphi.VR
{
    /// <summary>
    /// Puts the ExperimentUI dashboard — normally a second monitor — on a
    /// floating panel inside the headset, positioned in front of the wearer
    /// and following them.
    ///
    /// WHY THE DASHBOARD CANNOT SIMPLY BE "SHOWN IN VR":
    ///
    ///   ExperimentUI builds its dashboard on a SCREEN SPACE OVERLAY canvas.
    ///   Overlay canvases are composited straight into the display's
    ///   framebuffer after every camera has finished, which is exactly why
    ///   they are cheap and always on top — and exactly why no camera can
    ///   capture one. There is no RenderTexture you can point at an overlay,
    ///   and there is no camera whose output contains it. Once the headset is
    ///   on, a second monitor is invisible to the person who needs it.
    ///
    ///   So this component re-routes the dashboard rather than mirroring it:
    ///   the canvas is switched to SCREEN SPACE CAMERA, its Dashboard Camera
    ///   is given a RenderTexture, and that texture is drawn on a quad in the
    ///   scene. Layout is untouched — ScreenSpaceCamera drives CanvasScaler
    ///   from the camera's pixel rect, which is now the texture's size, so the
    ///   1920×1080 the dashboard was authored against still holds.
    ///
    /// WHY IT NEEDS ITS OWN LAYER:
    ///
    ///   A ScreenSpaceCamera canvas is REAL GEOMETRY sitting a metre in front
    ///   of its camera, not a screen overlay. Person View renders everything
    ///   (culling mask FFFFFFFF), so without intervention the participant
    ///   would find the entire dashboard hanging in the sky wherever the
    ///   Dashboard Camera happens to be. The canvas therefore goes on the
    ///   DashboardUI layer, the Dashboard Camera renders ONLY that layer, and
    ///   every other camera in the scene has that layer removed at startup.
    ///
    /// THE MONITOR STILL WORKS, AND STAYS CLICKABLE. The dashboard camera
    /// keeps driving Display 2 exactly as before; a second camera renders the
    /// same world-space canvas into the texture for the headset. See
    /// RedirectDashboardToTexture for why this is done in world space rather
    /// than by pointing the one camera at a RenderTexture.
    ///
    /// ON HEAD-LOCKING: VrRig's notes warn — correctly — against welding
    /// participant-facing UI to a tracked head. This panel is anchored to the
    /// camera anyway, because it is a RESEARCHER instrument that has to stay
    /// readable while looking around, not something a participant is subjected
    /// to for a whole trial. The smoothing below is the mitigation: the panel
    /// eases toward its resting place instead of being rigidly nailed to the
    /// eyes, which is what makes head-locked UI uncomfortable. Set Anchor to
    /// Seat for a genuinely body-locked panel that stays with the car.
    /// </summary>
    [DefaultExecutionOrder(200)] // after ExperimentUI (100) has built its canvas
    public class VrDashboardPanel : MonoBehaviour
    {
        public enum Anchor
        {
            /// <summary>Follows the head. Readable wherever they look.</summary>
            Head,
            /// <summary>Fixed to the driver's eye point, so it stays put in
            /// the car and the wearer looks over at it.</summary>
            Seat
        }

        [Header("Source")]
        [Tooltip("Leave empty to find the ExperimentUI in the scene.")]
        public ExperimentUI experimentUI;
        [Tooltip("Resolution of the texture the dashboard is rendered into. " +
                 "The dashboard is authored against 1920x1080; going lower " +
                 "makes its text mushy long before it saves any real frame " +
                 "time, because this canvas only redraws at ExperimentUI's " +
                 "Redraw Fps, not every frame.")]
        public Vector2Int resolution = new Vector2Int(1920, 1080);
        // The monitor is no longer an opt-in mirror: the dashboard camera
        // still drives Display 2 directly, and a second camera fills the
        // headset's texture. Nothing to toggle — both always run.

        [Header("Placement")]
        public Anchor anchorTo = Anchor.Head;
        [Tooltip("Metres in front of the wearer. Below about 0.6 m the eyes " +
                 "have to converge hard to read it, which gets tiring fast.")]
        [Range(0.4f, 5f)] public float distanceMeters = 1.4f;
        [Tooltip("Panel width in metres. Height follows from the resolution's " +
                 "aspect ratio.")]
        [Range(0.2f, 4f)] public float widthMeters = 1.6f;
        [Tooltip("Degrees right(+)/left(−) of straight ahead.")]
        [Range(-90f, 90f)] public float yawOffsetDegrees = 0f;
        [Tooltip("Degrees up(+)/down(−) from straight ahead. A little below " +
                 "eye level is the restful place for a panel you glance at.")]
        [Range(-60f, 60f)] public float pitchOffsetDegrees = -12f;

        [Header("Follow")]
        [Tooltip("How lazily the panel catches up, in seconds to close most " +
                 "of the gap. 0 = rigidly welded to the head. A little lag is " +
                 "what stops a head-locked panel feeling nauseating — the " +
                 "panel drifts to rest instead of the world sliding under a " +
                 "fixed rectangle.")]
        [Range(0f, 1f)] public float followLagSeconds = 0.18f;
        [Tooltip("Don't chase movements smaller than this many degrees. Keeps " +
                 "the panel dead still during ordinary micro-movements of the " +
                 "head instead of permanently creeping.")]
        [Range(0f, 20f)] public float deadzoneDegrees = 6f;

        [Header("Controls")]
        [Tooltip("Show/hide the panel. F8 to stay clear of VrRig (F11/F12) " +
                 "and VarjoGazeConnection (F9/F10).")]
        public Key toggleKey = Key.F8;
        [Tooltip("Snap the panel straight back in front of the wearer.")]
        public Key recenterKey = Key.F7;
        public bool visibleOnStart = true;

        private const string DashboardLayerName = "DashboardUI";

        private RenderTexture _rt;
        private Transform _panel;
        private Transform _anchor;
        private Camera _dashCam;   // drives the researcher's monitor
        private Camera _vrCam;     // fills the RenderTexture for the headset
        private bool _visible;
        private int _dashboardLayer = -1;

        private void Start()
        {
            if (!XRSettings.isDeviceActive)
            {
                // No headset: the dashboard's own monitor output is the right
                // answer and there is nothing to fix. Leaving ExperimentUI
                // completely alone here is what keeps the desktop workflow
                // working, so don't "helpfully" build the panel anyway.
                Debug.Log("[VrDashboardPanel] No XR device running — leaving the dashboard on its " +
                          "display untouched. This is the normal desktop path.", this);
                enabled = false;
                return;
            }

            if (experimentUI == null) experimentUI = FindFirstObjectByType<ExperimentUI>();
            if (experimentUI == null)
            {
                Debug.LogError("[VrDashboardPanel] No ExperimentUI in the scene — nothing to show.", this);
                enabled = false;
                return;
            }

            _dashboardLayer = LayerMask.NameToLayer(DashboardLayerName);
            if (_dashboardLayer < 0)
            {
                Debug.LogError($"[VrDashboardPanel] Layer '{DashboardLayerName}' does not exist. Add it in " +
                               "Project Settings > Tags and Layers, or the dashboard will hang in the " +
                               "participant's sky.", this);
                enabled = false;
                return;
            }

            if (!ResolveAnchor()) { enabled = false; return; }
            if (!RedirectDashboardToTexture()) { enabled = false; return; }

            BuildPanel();

            SetVisible(visibleOnStart);
            SnapToRest();

            Debug.Log($"[VrDashboardPanel] Dashboard on a {widthMeters:0.##} m panel at " +
                      $"{distanceMeters:0.##} m, anchored to {anchorTo}. {toggleKey} hides it, " +
                      $"{recenterKey} recentres it.", this);
        }

        private void OnDestroy()
        {
            if (_rt != null) { _rt.Release(); Destroy(_rt); }
        }

        // ── Setup ────────────────────────────────────────────────────────

        private bool ResolveAnchor()
        {
            var rig = VrRig.Instance;
            if (rig != null && rig.IsActive)
            {
                _anchor = anchorTo == Anchor.Seat ? rig.SeatReference : rig.participantCamera.transform;
            }
            else
            {
                var cam = Camera.main;
                if (cam == null)
                {
                    Debug.LogError("[VrDashboardPanel] No active VrRig and no Camera.main — nothing to " +
                                   "anchor the panel to.", this);
                    return false;
                }
                _anchor = cam.transform;
            }
            return _anchor != null;
        }

        /// <summary>Moves the dashboard into WORLD SPACE and points two cameras
        /// at it: one still driving the researcher's monitor, one filling the
        /// RenderTexture for the headset.
        ///
        /// THIS REPLACED A SCREEN-SPACE-CAMERA VERSION THAT BROKE THE MONITOR.
        /// Pointing the dashboard camera at a RenderTexture takes it off the
        /// display, which left Display 2 with no camera at all — Unity draws
        /// its own "No cameras rendering" notice over the top — and it broke
        /// input as well, because a GraphicRaycaster on a camera that renders
        /// to a texture has no way to map a mouse position on the monitor into
        /// that texture. The dashboard still appeared, and nothing on it could
        /// be clicked.
        ///
        /// A world-space canvas has no such coupling. It is ordinary geometry,
        /// so any number of cameras may look at it, and the one nominated as
        /// the canvas's worldCamera keeps mouse picking working exactly as
        /// before. The canvas keeps its authored 1920x1080 RectTransform, so
        /// every anchored position in ExperimentUI still lands where it did.
        ///
        /// It is parked far from the scene and confined to its own layer, so
        /// the only two cameras that can see it are the two created here.</summary>
        private bool RedirectDashboardToTexture()
        {
            _dashCam = experimentUI.DashboardCamera;
            var canvas = experimentUI.DashboardCanvas;
            if (_dashCam == null || canvas == null)
            {
                Debug.LogError("[VrDashboardPanel] ExperimentUI has not built its dashboard yet. This " +
                               "component must run AFTER it — check the execution order.", this);
                return false;
            }

            _rt = new RenderTexture(Mathf.Max(256, resolution.x), Mathf.Max(256, resolution.y), 24)
            {
                name = "DELPHI Dashboard RT",
                antiAliasing = 1,
                filterMode = FilterMode.Bilinear
            };
            _rt.Create();

            SetLayerRecursive(canvas.gameObject, _dashboardLayer);

            // Park it well away from anything the participant can reach. One
            // world unit per UI pixel keeps the maths trivial: the cameras'
            // orthographic size is then just half the canvas height.
            const float halfH = 1080f / 2f;
            var origin = new Vector3(0f, -10000f, 0f);

            canvas.renderMode = RenderMode.WorldSpace;
            var crt = canvas.GetComponent<RectTransform>();

            // Anchors and pivot are set BEFORE sizeDelta, and explicitly.
            // While a canvas is in Overlay mode Unity drives its RectTransform
            // itself and leaves whatever it last wrote behind on the way out —
            // often stretched anchors, under which sizeDelta is an inset from
            // the parent rather than a size, so asking for 1920x1080 quietly
            // gets you something else entirely.
            crt.anchorMin = crt.anchorMax = crt.pivot = new Vector2(0.5f, 0.5f);
            crt.sizeDelta = new Vector2(1920f, 1080f);
            crt.localScale = Vector3.one;
            crt.position = origin;
            crt.rotation = Quaternion.identity;

            // The monitor camera. Still on the dashboard display, still with
            // no target texture — which is what keeps Display 2 alive and
            // clickable.
            ConfigureDashboardCamera(_dashCam, origin, halfH);
            _dashCam.targetTexture = null;
            _dashCam.targetDisplay = experimentUI.dashboardDisplay;

            // Nominating it as the canvas's camera is what restores picking:
            // the GraphicRaycaster converts monitor mouse positions through
            // this camera onto the canvas plane.
            canvas.worldCamera = _dashCam;

            // The headset camera — same view, into the texture instead.
            var vrCamGO = new GameObject("Dashboard VR Camera");
            vrCamGO.transform.SetParent(transform, false);
            _vrCam = vrCamGO.AddComponent<Camera>();
            ConfigureDashboardCamera(_vrCam, origin, halfH);
            _vrCam.targetTexture = _rt;
            _vrCam.targetDisplay = 7;   // never a real display
            _vrCam.depth = _dashCam.depth - 1;

            HideDashboardLayerFromEveryOtherCamera();

            Debug.Log($"[VrDashboardPanel] Dashboard moved to world space at {origin}. " +
                      $"Monitor camera '{_dashCam.name}' on display {_dashCam.targetDisplay + 1} " +
                      $"(cull 0x{_dashCam.cullingMask:X}), headset camera into {_rt.width}x{_rt.height} " +
                      $"(cull 0x{_vrCam.cullingMask:X}). Both masks must contain bit {_dashboardLayer} " +
                      "— a zero there is what a black panel looks like.", this);
            return true;
        }

        /// <summary>Frames the world-space dashboard head-on. Orthographic, so
        /// the panel shows the same undistorted rectangle the monitor does
        /// rather than a perspective view of a billboard.</summary>
        private void ConfigureDashboardCamera(Camera cam, Vector3 origin, float halfHeight)
        {
            cam.transform.SetPositionAndRotation(origin + new Vector3(0f, 0f, -1000f), Quaternion.identity);
            cam.orthographic = true;
            cam.orthographicSize = halfHeight;
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 2000f;
            cam.cullingMask = 1 << _dashboardLayer;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.055f, 0.065f, 0.09f, 1f); // ExperimentUI's own background
            cam.stereoTargetEye = StereoTargetEyeMask.None;             // never draws into the headset directly
            cam.allowHDR = false;
            cam.allowMSAA = false;
        }

        /// <summary>Removes the dashboard layer from every other camera.
        ///
        /// Done defensively at run time rather than trusting the culling masks
        /// authored in the scene, because the failure mode is so bad and so
        /// confusing: a huge dark-blue rectangle of experiment controls
        /// hanging in mid-air in the participant's view, which looks like a
        /// rendering bug rather than a culling-mask setting, and which would
        /// also land in the PlayerView recording.</summary>
        private void HideDashboardLayerFromEveryOtherCamera()
        {
            // FindObjectsByType, NOT Camera.allCameras: that property lists
            // only ENABLED cameras, and DELPHI's frame sensors deliberately
            // keep their cameras disabled and drive them by hand with
            // Render() (CameraFeedSensor, Panorama360Sensor). Those are
            // precisely the cameras that write to disk, so missing them would
            // put the dashboard in the recordings and nowhere else.
            int mask = 1 << _dashboardLayer;
            var cameras = FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var cam in cameras)
            {
                // BOTH dashboard cameras are exempt. Missing _vrCam here is
                // what turned the headset panel into a black rectangle: it was
                // created before this ran, so it got the dashboard layer
                // stripped along with everything else and was left culling the
                // one object it exists to render. The panel still drew — just
                // the camera's clear colour, which looks exactly like a dead
                // RenderTexture rather than a culling mistake.
                if (cam == _dashCam || cam == _vrCam) continue;
                if ((cam.cullingMask & mask) == 0) continue;
                cam.cullingMask &= ~mask;
            }
        }

        private void BuildPanel()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = "[VR] Dashboard Panel";
            Destroy(go.GetComponent<Collider>()); // must never block the participant's raycasts

            _panel = go.transform;
            _panel.SetParent(_anchor, false);

            // Unlit, because the dashboard is already a finished image — any
            // scene lighting on it would just make the researcher's readout
            // dim when they drive into shadow.
            var shader = Shader.Find("Unlit/Texture");
            var mat = new Material(shader) { mainTexture = _rt };
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;

            ApplyScale();
        }

        private void ApplyScale()
        {
            float aspect = (float)_rt.height / Mathf.Max(1, _rt.width);
            _panel.localScale = new Vector3(widthMeters, widthMeters * aspect, 1f);
        }

        private static void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform child in go.transform) SetLayerRecursive(child.gameObject, layer);
        }

        // ── Per-frame ────────────────────────────────────────────────────

        private void Update()
        {
            var kb = Keyboard.current;
            if (kb == null) return;
            if (kb[toggleKey].wasPressedThisFrame) SetVisible(!_visible);
            if (kb[recenterKey].wasPressedThisFrame) SnapToRest();
        }

        /// <summary>LateUpdate, so the panel eases toward a head pose that has
        /// already been written this frame rather than last frame's.</summary>
        private void LateUpdate()
        {
            if (!_visible || _panel == null) return;

            GetRestPose(out var targetLocalPos, out var targetLocalRot);

            // RESCUE THE PANEL IF IT IS SITTING ON THE EYES.
            //
            // The deadzone below compares DIRECTIONS. A panel at the local
            // origin has no direction: localPosition.normalized is the zero
            // vector, Vector3.Angle returns 0, that reads as "already in
            // place", and the easing never runs — so a panel that reaches the
            // origin stays there forever. And at the origin it is a 1.6 m
            // opaque quad exactly at the eye point, which fills the entire
            // view. That is indistinguishable from the headset going black,
            // and it depends on startup timing, which is what makes it come
            // and go for no apparent reason.
            //
            // Press the toggle key to tell the two apart by hand: if hiding
            // the panel brings the world back, it was never the headset.
            if (_panel.localPosition.sqrMagnitude < 0.01f)
            {
                SnapToRest();
                Debug.LogWarning("[VrDashboardPanel] Panel was at the eye point and filling the view — " +
                                 "snapped it back to its resting place. If the headset looked black, " +
                                 "this was why.", this);
                return;
            }

            // Below the deadzone the panel is simply left alone. Without this
            // an exponential ease never actually arrives, so the panel creeps
            // perpetually against every small head movement — which reads as
            // drift and is far more distracting than a panel that is slightly
            // off-centre and still.
            float offBy = Vector3.Angle(_panel.localPosition.normalized, targetLocalPos.normalized);
            if (offBy > deadzoneDegrees || followLagSeconds <= 0f)
            {
                float t = followLagSeconds <= 0f
                    ? 1f
                    : 1f - Mathf.Exp(-Time.deltaTime / followLagSeconds);
                _panel.localPosition = Vector3.Lerp(_panel.localPosition, targetLocalPos, t);
                _panel.localRotation = Quaternion.Slerp(_panel.localRotation, targetLocalRot, t);
            }
        }

        private void GetRestPose(out Vector3 localPos, out Quaternion localRot)
        {
            localRot = Quaternion.Euler(-pitchOffsetDegrees, yawOffsetDegrees, 0f);
            localPos = localRot * Vector3.forward * distanceMeters;
        }

        // ── Public API ───────────────────────────────────────────────────

        /// <summary>Drops the panel straight back to its resting place with no
        /// easing. What to press when it has been left behind — the smoothing
        /// only chases rotation, so a big translation can strand it.</summary>
        public void SnapToRest()
        {
            if (_panel == null) return;
            GetRestPose(out var p, out var r);
            _panel.localPosition = p;
            _panel.localRotation = r;
        }

        public void SetVisible(bool visible)
        {
            _visible = visible;
            if (_panel != null) _panel.gameObject.SetActive(visible);
        }

        public bool IsVisible => _visible;

        /// <summary>Lets size and placement be tweaked from the inspector
        /// while running, which is the only practical way to find a
        /// comfortable spot — it has to be judged from inside the headset.</summary>
        private void OnValidate()
        {
            if (!Application.isPlaying || _panel == null || _rt == null) return;
            ApplyScale();
            SnapToRest();
        }
    }
}
