using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR;

namespace Delphi.VR
{
    /// <summary>
    /// Turns the existing participant camera into a head-tracked stereo
    /// camera for the Varjo XR-3, without moving it in the scene and without
    /// anything else in DELPHI having to know VR exists.
    ///
    /// WHY IT REBUILDS THE HIERARCHY RATHER THAN ASKING FOR ONE:
    ///
    ///   "Person View" already sits exactly where the driver's eyes belong —
    ///   somebody positioned it in the Editor against the actual car model,
    ///   and that judgement is worth keeping. So at Awake this component
    ///   lifts that authored transform onto a new parent and hangs the
    ///   camera underneath it at identity:
    ///
    ///       Car
    ///       └── [VR] Seat Reference     ← Person View's authored local TRS.
    ///       │                             The driver's eye point. Never moves.
    ///           └── [VR] Rig Compensation ← counter-rotation cancelling the
    ///               │                       YAW3's physical seat motion.
    ///               │                       Identity until that's wired up.
    ///                   └── Person View  ← the camera itself, local TRS now
    ///                                      driven purely by head tracking.
    ///
    ///   Reparenting preserves the GameObject, so every scene reference to
    ///   Person View (CameraFeedSensor.sourceCamera, FreePlayPanel's
    ///   auto-find, the AudioListener) keeps resolving. Nothing to re-wire.
    ///
    /// WHY THE COMPENSATION NODE EXISTS ALREADY, EMPTY:
    ///
    ///   The headset tracks against the base stations, i.e. against the ROOM.
    ///   The YAW3 rotates the participant's whole body inside that room, up
    ///   to CarMotionCues' ±40° pitch/roll ceilings plus a yaw follower. Every
    ///   one of those degrees arrives here as apparent HEAD motion, so the
    ///   virtual world swings away from the participant exactly when the seat
    ///   moves — the opposite of the cue we're paying the rig to deliver.
    ///
    ///   SetSeatCompensation() is the hook that cancels it: feed it the rig's
    ///   own reported orientation and this node counter-rotates by its
    ///   inverse, leaving the camera's local pose as head-relative-to-seat,
    ///   which is what the simulation wants.
    ///
    ///   Rotating about the head rather than about the rig's true pivot
    ///   leaves a small residual TRANSLATION (the head swings on an arc the
    ///   seat's centre doesn't). That's second-order next to the orientation
    ///   error this cancels, and correcting it needs the rig's pivot measured
    ///   against the play space — a calibration, not a code change.
    ///
    ///   UNTIL THAT IS WIRED UP, DO NOT START THE RIG WITH THE HEADSET ON.
    ///
    /// RECENTRING is done in software rather than trusting the provider's
    /// own, because it has to be repeatable between participants: it cancels
    /// the head's YAW ONLY and takes the head position as the new seat
    /// origin. Pitch and roll are deliberately left alone — cancelling those
    /// would tilt the horizon and hand the participant a permanent, silent
    /// vestibular conflict.
    ///
    /// If no headset is running this component does NOTHING AT ALL: no
    /// reparenting, no pose driving. The desktop workflow is untouched, so a
    /// scene carrying this component still opens and runs on a workstation
    /// with no Varjo attached.
    /// </summary>
    [DefaultExecutionOrder(-200)] // hierarchy must exist before anything auto-finds the camera
    public class VrRig : MonoBehaviour
    {
        public static VrRig Instance { get; private set; }

        [Header("Links (auto-found if left empty)")]
        [Tooltip("The camera the participant looks through — 'Person View' in " +
                 "the sample scene. ASSIGN IT. Auto-find is a fallback that " +
                 "matches on that name and then refuses to guess between " +
                 "look-alikes, because picking the wrong one puts the " +
                 "researcher's overview camera on the participant's face and " +
                 "the only symptom is an empty sky.")]
        public Camera participantCamera;

        [Tooltip("Force every OTHER camera in the scene to render flat. Unity " +
                 "defaults new cameras to Target Eye = Both, so the overview " +
                 "camera happily draws into the headset alongside the " +
                 "participant's view — same depth, arbitrary winner. Leave " +
                 "this on unless you deliberately want a second stereo camera.")]
        public bool makeOtherCamerasFlat = true;

        [Header("Camera")]
        [Tooltip("Near clip in metres. The desktop default (0.3) slices " +
                 "through the cabin once the camera is at a real head " +
                 "position and the participant can lean forward. 0.03 clears " +
                 "a dashboard without the depth precision getting silly.")]
        public float nearClipMeters = 0.03f;
        [Tooltip("QuestionnaireToolkit mounts its world-space pages and its VR " +
                 "keyboard off Camera.main. Nothing in this scene is tagged " +
                 "MainCamera, so those calls return null and throw the moment " +
                 "a questionnaire opens in VR. Leave this on.")]
        public bool tagAsMainCamera = true;

        [Header("Recentring")]
        [Tooltip("Recentre automatically this many seconds after start. " +
                 "NEGATIVE = off, which is the right answer for this study: " +
                 "the researcher starts the session at the desk and only then " +
                 "hands the headset over, so anything automatic recentres on a " +
                 "headset lying on a table and silently puts the driver's eye " +
                 "point wherever that was. Recentre with the key below once " +
                 "the participant is seated and facing forward.")]
        public float autoRecenterDelaySeconds = -1f;
        [Tooltip("Researcher-side recentre key. F12 to keep clear of " +
                 "ExperimentUI's playback keys.")]
        public Key recenterKey = Key.F12;
        [Tooltip("Dumps the XR subsystem state to the console — what the " +
                 "display and input subsystems think they're doing, and which " +
                 "nodes are actually tracked. Press it when the headset is " +
                 "showing something you can't explain.")]
        public Key diagnosticsKey = Key.F11;

        [Header("Mirror / spectator view")]
        [Tooltip("What Display 1 (the participant monitor, and therefore the " +
                 "PlayerView recording feed's source camera) mirrors.")]
        public GameViewRenderMode mirrorMode = GameViewRenderMode.LeftEye;
        [Tooltip("Render scale for the eye textures. The XR-3 is an expensive " +
                 "target — drop this below 1 before touching anything else if " +
                 "the frame budget is tight.")]
        [Range(0.3f, 2f)] public float eyeTextureResolutionScale = 1f;

        /// <summary>True once the hierarchy is built and poses are being driven.</summary>
        public bool IsActive { get; private set; }

        /// <summary>The driver's eye point, fixed to the car. Anchor
        /// participant-facing world-space UI to THIS, never to the camera —
        /// a panel welded to a tracked head is the classic way to make
        /// somebody ill.</summary>
        public Transform SeatReference { get; private set; }

        /// <summary>The node that cancels the rig's physical seat motion.
        /// Driven through SetSeatCompensation.</summary>
        public Transform Compensation { get; private set; }

        private Camera _cam;
        private Transform _camT;
        private readonly List<XRNodeState> _nodes = new();
        private readonly List<XRInputSubsystem> _inputSubsystems = new();

        // Software recentre state, applied to every raw pose. Identity until
        // the first Recenter().
        private Quaternion _originYawInverse = Quaternion.identity;
        private Vector3 _originPosition = Vector3.zero;

        private float _autoRecenterAt = -1f;
        private bool _autoRecenterDone;
        private bool _loggedFirstPose;
        private bool _positionTracked;
        private bool _rotationTracked;
        private bool _wasTracking;

        private void Awake()
        {
            Instance = this;

            if (participantCamera == null) participantCamera = ResolveParticipantCamera();
            if (participantCamera == null) return; // ResolveParticipantCamera already explained why

            if (!XRSettings.isDeviceActive)
            {
                Debug.Log("[VrRig] No XR device running — leaving the scene exactly as authored " +
                          "and driving nothing. This is the normal desktop path. If a headset IS " +
                          "connected, check Project Settings > XR Plug-in Management: Varjo must " +
                          "be ticked and 'Initialize XR on Startup' left on.", this);
                return;
            }

            _cam = participantCamera;
            _camT = _cam.transform;

            BuildHierarchy();
            ConfigureCamera();
            if (makeOtherCamerasFlat) MakeOtherCamerasFlat();
            ConfigureDisplay();
            ConfigureTrackingOrigin();

            IsActive = true;
            if (autoRecenterDelaySeconds >= 0f) _autoRecenterAt = Time.time + autoRecenterDelaySeconds;

            // Eye texture dimensions are still 0x0 this early — the display
            // subsystem sizes them on its first frame — so they're logged from
            // the first pose instead, where they mean something.
            Debug.Log($"[VrRig] Stereo on '{_cam.name}' via {XRSettings.loadedDeviceName}. " +
                      $"Seat reference at {SeatReference.localPosition}.", this);
        }

        private void OnEnable()  => Application.onBeforeRender += ApplyHeadPose;
        private void OnDisable() => Application.onBeforeRender -= ApplyHeadPose;

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // ── Setup ────────────────────────────────────────────────────────

        /// <summary>Finds the participant camera, or explains itself and gives
        /// up. It does NOT fall back to "first camera on Display 0": this scene
        /// has two cameras on Display 0 at equal depth (Person View and the
        /// researcher's Track Overview Camera), Camera.allCameras' order between
        /// them is arbitrary, and losing that coin toss mounts a camera 150 m
        /// above the track onto the participant's face — which looks exactly
        /// like an empty skybox and nothing like a bug.</summary>
        private Camera ResolveParticipantCamera()
        {
            var candidates = new List<Camera>();
            foreach (var cam in Camera.allCameras)
            {
                if (cam.name == ParticipantCameraName) return cam;
                if (cam.targetDisplay == 0) candidates.Add(cam);
            }

            if (candidates.Count == 1) return candidates[0];

            if (candidates.Count == 0)
            {
                Debug.LogError("[VrRig] No camera assigned and none found on Display 0 — nothing to " +
                               "make stereo. Assign participantCamera in the Inspector.", this);
                return null;
            }

            var names = new string[candidates.Count];
            for (int i = 0; i < candidates.Count; i++) names[i] = $"'{candidates[i].name}'";
            Debug.LogError($"[VrRig] {candidates.Count} cameras share Display 0 ({string.Join(", ", names)}) " +
                           $"and none is named '{ParticipantCameraName}', so there is no honest way to tell " +
                           "which one the participant looks through. Assign participantCamera in the " +
                           "Inspector. Doing nothing rather than guessing.", this);
            return null;
        }

        private const string ParticipantCameraName = "Person View";

        /// <summary>Every camera Unity creates defaults to Target Eye = Both, so
        /// without this the overview camera renders into the headset too — at
        /// the same depth as the participant's view, which resolves to
        /// whichever one Unity feels like drawing last.</summary>
        private void MakeOtherCamerasFlat()
        {
            var demoted = new List<string>();
            foreach (var cam in Camera.allCameras)
            {
                if (cam == _cam || cam.stereoTargetEye == StereoTargetEyeMask.None) continue;
                cam.stereoTargetEye = StereoTargetEyeMask.None;
                demoted.Add($"'{cam.name}'");
            }

            if (demoted.Count > 0)
            {
                Debug.Log($"[VrRig] Kept {string.Join(", ", demoted)} off the headset (Target Eye → None). " +
                          $"Only '{_cam.name}' renders in stereo.", this);
            }
        }

        /// <summary>Inserts Seat Reference + Rig Compensation above the camera,
        /// moving the camera's authored placement onto the seat node so the
        /// camera itself is free for tracking.</summary>
        private void BuildHierarchy()
        {
            var originalParent = _camT.parent;
            int siblingIndex = _camT.GetSiblingIndex();

            SeatReference = new GameObject("[VR] Seat Reference").transform;
            SeatReference.SetParent(originalParent, false);
            SeatReference.localPosition = _camT.localPosition;
            SeatReference.localRotation = _camT.localRotation;
            SeatReference.localScale = Vector3.one;
            if (originalParent != null) SeatReference.SetSiblingIndex(siblingIndex);

            Compensation = new GameObject("[VR] Rig Compensation").transform;
            Compensation.SetParent(SeatReference, false);

            // worldPositionStays: false — we're about to zero the local TRS
            // anyway, and letting Unity back-solve a world pose here would
            // just be a rounding error waiting to happen.
            _camT.SetParent(Compensation, false);
            _camT.localPosition = Vector3.zero;
            _camT.localRotation = Quaternion.identity;
            _camT.localScale = Vector3.one;
        }

        private void ConfigureCamera()
        {
            _cam.nearClipPlane = Mathf.Max(0.01f, nearClipMeters);
            _cam.stereoTargetEye = StereoTargetEyeMask.Both;

            // The XR display owns the projection; a hand-set FOV would be
            // ignored on the headset but still skew the mirror.
            _cam.usePhysicalProperties = false;

            if (tagAsMainCamera && !_cam.CompareTag("MainCamera"))
            {
                _cam.tag = "MainCamera";
            }
        }

        private void ConfigureDisplay()
        {
            XRSettings.gameViewRenderMode = mirrorMode;
            XRSettings.eyeTextureResolutionScale = Mathf.Clamp(eyeTextureResolutionScale, 0.3f, 2f);
        }

        /// <summary>Asks for a seated (device-relative) origin. Base-station
        /// setups default to a floor-relative one, which would drop the
        /// participant's eye point wherever the room's floor happens to be
        /// rather than at the driver's seat. Not every provider honours the
        /// request, which is exactly why Recenter() doesn't depend on it.</summary>
        private void ConfigureTrackingOrigin()
        {
            SubsystemManager.GetSubsystems(_inputSubsystems);
            foreach (var subsystem in _inputSubsystems)
            {
                if (subsystem == null) continue;
                if (!subsystem.TrySetTrackingOriginMode(TrackingOriginModeFlags.Device))
                {
                    // NOT harmless, which is what this used to claim.
                    //
                    // On a FLOOR origin the runtime reports the head's height
                    // above the room floor and its position across the room —
                    // and this component adds that straight onto the driver's
                    // eye point. The camera therefore starts roughly a metre
                    // above the seat and however far sideways the headset
                    // happens to be sitting, which can put it inside the car
                    // body or under the road. The recentre FIXES it, but the
                    // recentre is a key press, so until somebody presses it
                    // the view is wrong — and it is wrong by a different
                    // amount every run, depending only on where the headset
                    // was lying. That is the classic "worked last time,
                    // black this time, nothing changed".
                    Debug.LogWarning($"[VrRig] Provider REFUSED a seated origin and kept " +
                                     $"{subsystem.GetTrackingOriginMode()}. Head position is therefore measured " +
                                     $"from the ROOM FLOOR, so the camera is offset from the driver's eye point " +
                                     $"by wherever the headset physically is — often a metre up, sometimes inside " +
                                     $"the car body, which looks like a black screen. PRESS {recenterKey} with the " +
                                     "headset on and facing forward to zero it. Until then, treat the view as " +
                                     "misplaced rather than broken.", this);
                }
            }
        }

        // ── Per-frame ────────────────────────────────────────────────────

        private void Update()
        {
            if (!IsActive) return;

            if (!_autoRecenterDone && _autoRecenterAt >= 0f && Time.time >= _autoRecenterAt)
            {
                _autoRecenterDone = true;
                Recenter();
            }

            var kb = Keyboard.current;
            if (kb == null) return;
            if (kb[recenterKey].wasPressedThisFrame) Recenter();
            if (kb[diagnosticsKey].wasPressedThisFrame) LogXrDiagnostics();
        }

        /// <summary>Runs on onBeforeRender rather than Update so the pose used
        /// for rendering is the freshest one the runtime has — the whole point
        /// of late latching, and worth more here than anywhere because the
        /// participant's head is on a moving platform.</summary>
        private void ApplyHeadPose()
        {
            if (!IsActive || _camT == null) return;
            bool gotPose = TryGetRawHeadPose(out var position, out var rotation);
            ReportTrackingChanges(gotPose);
            if (!gotPose) return;

            if (!_loggedFirstPose)
            {
                _loggedFirstPose = true;
                Debug.Log($"[VrRig] First pose — position {position} ({(_positionTracked ? "TRACKED" : "NOT TRACKED")}), " +
                          $"rotation {rotation.eulerAngles} ({(_rotationTracked ? "TRACKED" : "NOT TRACKED")}), " +
                          $"eye texture {XRSettings.eyeTextureWidth}x{XRSettings.eyeTextureHeight}.", this);
                LogXrDiagnostics();
            }

            // Rotation-only is a legitimate state to render in (3DoF), but
            // driving localPosition from an UNTRACKED position would yank the
            // participant to the seat origin every frame, so leave it be.
            _camT.localRotation = _originYawInverse * rotation;
            if (_positionTracked)
                _camT.localPosition = _originYawInverse * (position - _originPosition);
        }

        /// <summary>Reads the centre-eye node.
        ///
        /// It reports position and rotation validity SEPARATELY and on purpose.
        /// XRNodeState.TryGetPosition writes default(Vector3) into its out
        /// parameter when it fails, so a rotation-only (3DoF) headset hands
        /// back a perfectly plausible-looking (0, 0, 0) — which reads as "the
        /// participant is sitting exactly at the tracking origin" rather than
        /// "positional tracking is dead". That distinction is the difference
        /// between a working rig and a lost base station, so it must never be
        /// collapsed into one bool again.</summary>
        private bool TryGetRawHeadPose(out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            _positionTracked = false;
            _rotationTracked = false;

            InputTracking.GetNodeStates(_nodes);
            foreach (var node in _nodes)
            {
                if (node.nodeType != XRNode.CenterEye) continue;
                _positionTracked = node.TryGetPosition(out position);
                _rotationTracked = node.TryGetRotation(out rotation);
                if (!_positionTracked) position = Vector3.zero;
                if (!_rotationTracked) rotation = Quaternion.identity;
                return _positionTracked || _rotationTracked;
            }
            return false;
        }

        /// <summary>Says something the FIRST time tracking drops and the first
        /// time it comes back, and never spams in between.
        ///
        /// This exists because of how the headset actually gets used: it goes
        /// on the participant AFTER the session starts, and the display sleeps
        /// whenever it comes off a head. That makes "tracking stopped" a
        /// routine event with two completely different meanings — expected
        /// during handover, and a corrupted trial if it happens mid-drive. The
        /// console line is timestamped, so afterwards there is a record of
        /// which one it was instead of a participant's vague recollection.</summary>
        private void ReportTrackingChanges(bool gotPose)
        {
            bool trackingNow = gotPose && _positionTracked;
            if (trackingNow == _wasTracking) return;
            _wasTracking = trackingNow;

            if (!_loggedFirstPose) return; // startup isn't a "loss"

            if (trackingNow)
            {
                Debug.Log("[VrRig] Head tracking recovered.", this);
            }
            else
            {
                Debug.LogWarning("[VrRig] HEAD TRACKING LOST. Expected if the headset was just taken " +
                                 "off — the proximity sensor puts it into standby and the display " +
                                 "blanks. If nobody touched it, treat this trial as suspect.", this);
            }
        }

        /// <summary>Dumps what the XR subsystems are actually doing. When the
        /// headset shows black there is no way to tell from inside Unity
        /// whether the display subsystem stopped, the session was taken by
        /// another app, or tracking died — unless somebody asks. So ask.</summary>
        public void LogXrDiagnostics()
        {
            var report = new System.Text.StringBuilder("[VrRig] XR diagnostics\n");
            report.AppendLine($"  device active : {XRSettings.isDeviceActive} ('{XRSettings.loadedDeviceName}')");
            report.AppendLine($"  eye texture   : {XRSettings.eyeTextureWidth}x{XRSettings.eyeTextureHeight} " +
                              $"@ scale {XRSettings.eyeTextureResolutionScale}");
            report.AppendLine($"  game view     : {XRSettings.gameViewRenderMode}");

            var displays = new List<XRDisplaySubsystem>();
            SubsystemManager.GetSubsystems(displays);
            if (displays.Count == 0) report.AppendLine("  DISPLAY       : none — nothing is being submitted to the headset");
            foreach (var display in displays)
            {
                report.AppendLine($"  display       : running={display.running}, opaque={display.displayOpaque}, " +
                                  $"renderPasses={display.GetRenderPassCount()}");
            }

            var inputs = new List<XRInputSubsystem>();
            SubsystemManager.GetSubsystems(inputs);
            if (inputs.Count == 0) report.AppendLine("  INPUT         : none — no tracking data can arrive");
            foreach (var input in inputs)
            {
                report.AppendLine($"  input         : running={input.running}, origin={input.GetTrackingOriginMode()}");
            }

            InputTracking.GetNodeStates(_nodes);
            if (_nodes.Count == 0) report.AppendLine("  NODES         : none — the runtime is reporting no tracked devices at all");
            foreach (var node in _nodes)
            {
                report.AppendLine($"  node          : {node.nodeType} tracked={node.tracked}");
            }

            AppendCameraState(report);
            AppendNearbyOccluders(report);
            Debug.Log(report.ToString(), this);
        }

        /// <summary>Reports what the camera IS and where it ACTUALLY ended up.
        ///
        /// Every number logged before this one was local — "seat reference at
        /// (0, 5, 0)" says nothing about where that lands once the car has
        /// driven off, and a camera clearing to black in the middle of nowhere
        /// is indistinguishable from a camera that isn't rendering. The parent
        /// chain is printed too, because the whole rig is three transforms
        /// stacked at Awake and if anything else reparented the camera
        /// afterwards this is the only place that would show it.</summary>
        private void AppendCameraState(System.Text.StringBuilder report)
        {
            if (_cam == null) { report.AppendLine("  CAMERA        : null"); return; }

            report.AppendLine($"  camera        : '{_cam.name}' enabled={_cam.enabled} depth={_cam.depth} " +
                              $"display={_cam.targetDisplay} eye={_cam.stereoTargetEye}");
            report.AppendLine($"  clear         : {_cam.clearFlags}, bg={_cam.backgroundColor}, " +
                              $"skybox={(RenderSettings.skybox != null ? RenderSettings.skybox.name : "<NONE — clears to bg colour>")}");
            report.AppendLine($"  culling mask  : {_cam.cullingMask:X8} (FFFFFFFF = everything, 0 = NOTHING VISIBLE)");
            report.AppendLine($"  clip          : near={_cam.nearClipPlane} far={_cam.farClipPlane}");
            report.AppendLine($"  world pos     : {_camT.position}, forward {_camT.forward}");
            report.AppendLine($"  local pos     : {_camT.localPosition} (under the compensation node)");

            var chain = new List<string>();
            for (var t = _camT; t != null; t = t.parent) chain.Add(t.name);
            report.AppendLine($"  parent chain  : {string.Join(" < ", chain)}");
        }

        /// <summary>Lists everything sitting right in front of the camera.
        ///
        /// A healthy display subsystem submitting four render passes and a
        /// participant reporting "black" are not in contradiction — they are
        /// what a single opaque surface parked in front of the eyes looks
        /// like from the two ends. Nothing in the XR API can see that, because
        /// as far as XR is concerned everything is fine. So look at the scene
        /// instead, and name whatever is close enough to fill the view.
        ///
        /// Canvases first: they're the usual offender here, because
        /// QuestionnaireToolkit mounts its pages off Camera.main and
        /// FreePlayPanel mounts off the participant camera — and both of those
        /// are now a HEAD, not a fixed point in a car.</summary>
        private void AppendNearbyOccluders(System.Text.StringBuilder report)
        {
            if (_camT == null) return;
            const float radius = 5f;

            report.AppendLine($"  --- active canvases within {radius} m of the camera ---");
            bool foundAny = false;
            foreach (var canvas in FindObjectsByType<Canvas>(FindObjectsSortMode.None))
            {
                if (!canvas.isActiveAndEnabled) continue;
                float distance = Vector3.Distance(canvas.transform.position, _camT.position);
                if (distance > radius) continue;
                foundAny = true;
                report.AppendLine($"  canvas        : '{canvas.name}' at {distance:0.00} m, mode={canvas.renderMode}, " +
                                  $"parent='{(canvas.transform.parent != null ? canvas.transform.parent.name : "<root>")}'");
            }
            if (!foundAny) report.AppendLine("  (none — so the black is not a UI surface)");

            if (Physics.Raycast(_camT.position, _camT.forward, out var hit, radius))
            {
                report.AppendLine($"  first collider ahead: '{hit.collider.name}' at {hit.distance:0.00} m");
            }

            // Colliders are not the same set as things you can SEE. A big
            // unlit quad or an inside-out dome with no collider blocks the
            // whole view and the raycast above sails straight through it, so
            // check renderer bounds as well — that's the set the camera
            // actually draws.
            report.AppendLine($"  --- renderers whose bounds contain or nearly touch the camera ---");
            bool foundRenderer = false;
            foreach (var renderer in FindObjectsByType<Renderer>(FindObjectsSortMode.None))
            {
                if (!renderer.isVisible && !renderer.enabled) continue;
                var bounds = renderer.bounds;
                float distance = Vector3.Distance(bounds.ClosestPoint(_camT.position), _camT.position);
                if (distance > 0.5f) continue;

                // Say whether this camera can actually SEE it. Bounds alone
                // are not enough: the researcher's overview instrumentation is
                // hundreds of metres across and its bounds swallow the camera
                // permanently, so without the layer test every dump lists four
                // enormous objects that are culled and innocent — and that is
                // exactly the wrong place to send somebody hunting a black
                // screen.
                bool visibleToCamera = (_cam.cullingMask & (1 << renderer.gameObject.layer)) != 0;
                foundRenderer = true;
                report.AppendLine($"  renderer      : '{renderer.name}' at {distance:0.00} m, " +
                                  $"layer {renderer.gameObject.layer} " +
                                  $"({(visibleToCamera ? "RENDERED BY THIS CAMERA" : "culled — not a suspect")}), " +
                                  $"bounds size {bounds.size}, material='{renderer.sharedMaterial?.name ?? "<none>"}'");
            }
            if (!foundRenderer) report.AppendLine("  (none — nothing is wrapped around the camera)");
        }

        // ── Public API ───────────────────────────────────────────────────

        /// <summary>Puts the participant's current head pose at the driver's
        /// eye point, facing down the road. Yaw only — see the class summary
        /// for why pitch and roll are left in.</summary>
        public void Recenter()
        {
            if (!IsActive) return;
            if (!TryGetRawHeadPose(out var position, out var rotation))
            {
                Debug.LogWarning("[VrRig] Recentre asked for but the headset reported no pose — " +
                                 "is it on the participant's head and visible to the base stations?", this);
                return;
            }

            if (!_positionTracked)
            {
                Debug.LogWarning("[VrRig] Recentring on ROTATION ONLY — the runtime is not reporting a " +
                                 "tracked head position, so the seat origin can't be measured. Yaw will " +
                                 "be right and the participant will be pinned to the seat point. Check " +
                                 "SteamVR: this is what a lost base station looks like from in here.", this);
            }

            float yawDegrees = rotation.eulerAngles.y;
            _originYawInverse = Quaternion.Inverse(Quaternion.Euler(0f, yawDegrees, 0f));
            _originPosition = position;

            ApplyHeadPose();
            Debug.Log($"[VrRig] Recentred: head yaw {yawDegrees:0.#}° is now straight ahead.", this);
        }

        /// <summary>Cancels the rig's physical seat rotation so the camera's
        /// local pose is head-RELATIVE-TO-SEAT again. Pass the orientation the
        /// YAW3 currently has the seat in; pass identity (or just don't call
        /// this) while the rig is parked.
        ///
        /// Nothing calls this yet — wiring it to the rig's reported position
        /// telemetry is the next step, and until then the rig must not run
        /// with the headset on.</summary>
        public void SetSeatCompensation(Quaternion seatRotation)
        {
            if (Compensation == null) return;
            Compensation.localRotation = Quaternion.Inverse(seatRotation);
        }

        /// <summary>Convenience overload matching CarMotionCues' published
        /// PitchDeg / RollDeg / YawDeg, in that component's sign convention.</summary>
        public void SetSeatCompensation(float pitchDeg, float rollDeg, float yawDeg)
        {
            SetSeatCompensation(Quaternion.Euler(pitchDeg, yawDeg, rollDeg));
        }
    }
}
