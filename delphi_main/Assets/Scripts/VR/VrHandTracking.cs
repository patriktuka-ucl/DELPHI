using Leap;
using Leap.PhysicalHands; // PhysicalHandsManager lives here, not in Leap itself
using UnityEngine;

namespace Delphi.VR
{
    /// <summary>
    /// Brings up Ultraleap hand tracking on the Varjo XR-3 and hands it the
    /// right camera and mounting offsets, so the participant's real hands
    /// appear where their real hands are.
    ///
    /// NO DRIVER INSTALL IS NEEDED. The Ultraleap (Gemini) runtime ships
    /// inside Varjo Base — the XR-3 has the sensor built into the headset —
    /// so only the com.ultraleap.tracking Unity package is required. Do not
    /// install Ultraleap's standalone drivers alongside Varjo Base.
    ///
    /// WHY THE OFFSETS ARE NOT OPTIONAL:
    ///
    ///   Ultraleap reports hand positions relative to the TRACKING SENSOR,
    ///   which on an XR-3 is not where the headset's tracking origin is. Left
    ///   at the plugin's generic defaults the hands are consistently displaced
    ///   from where the participant's actual hands are — a few centimetres, so
    ///   it looks plausible rather than broken, and it quietly ruins any reach
    ///   or touch interaction. Varjo publish the XR-3 numbers; they are baked
    ///   in below.
    ///
    /// WHY IT SWITCHES OFF THE PLUGIN'S POSE DRIVER:
    ///
    ///   LeapXRServiceProvider.OnEnable adds a TrackedPoseDriver to the main
    ///   camera unless told not to. VrRig already drives that camera itself,
    ///   from the centre-eye node on Application.onBeforeRender. Two things
    ///   writing one transform in the same frame is not a small problem: the
    ///   head pose ends up applied twice or fights frame to frame, which in a
    ///   headset is immediately sickening. So _autoCreateTrackedPoseDriver is
    ///   cleared BEFORE the provider is enabled — after would be too late,
    ///   because OnEnable has already run.
    ///
    /// If no headset is running this does nothing, like the rest of the VR
    /// rig, and the desktop workflow is untouched.
    /// </summary>
    [DefaultExecutionOrder(-150)] // after VrRig (-200) has built the hierarchy
    public class VrHandTracking : MonoBehaviour
    {
        // Varjo's published mounting offsets for the XR-3's Ultraleap sensor.
        private const float Xr3DeviceOffsetY = -0.0112f;
        private const float Xr3DeviceOffsetZ = 0.0999f;
        private const float Xr3DeviceTiltX = 0f;

        [Header("Latency")]
        [Tooltip("Ultraleap's latency compensation. Auto is the setting that " +
                 "makes the hands feel attached rather than trailing — leave " +
                 "it there. Off only exists to prove whether a positioning " +
                 "problem is warping or something else; it always feels laggier.")]
        public LeapXRServiceProvider.TemporalWarpingMode temporalWarping =
            LeapXRServiceProvider.TemporalWarpingMode.Auto;

        [Header("Physical hands")]
        [Tooltip("Ultraleap's 'Physical Hands Manager' prefab. Required for " +
                 "anything the participant physically TOUCHES — it builds the " +
                 "collider hands that push buttons and sliders. Without it the " +
                 "hands are visible but pass straight through everything.")]
        public GameObject physicalHandsManagerPrefab;

        [Header("Hand visuals")]
        [Tooltip("Hand model prefabs to spawn, e.g. Ultraleap's 'Capsule " +
                 "Hands'. Leave empty to run tracking with no visible hands — " +
                 "useful when something else is drawing them.")]
        public GameObject[] handModelPrefabs;

        [Header("Mounting offsets (Varjo's published XR-3 values)")]
        [Tooltip("Leave ON. Off uses Ultraleap's generic defaults, which put " +
                 "the hands a few centimetres from where they really are — " +
                 "close enough to look right and wrong enough to break " +
                 "touching anything.")]
        public bool applyXr3Offsets = true;

        /// <summary>The provider actually driving the hands.</summary>
        public LeapXRServiceProvider Provider { get; private set; }

        /// <summary>The collider hands, once built. Null when physical hands
        /// are not in use — check before assuming anything is touchable.</summary>
        public PhysicalHandsManager PhysicalHands { get; private set; }

        /// <summary>Reports whether hand DATA is arriving, separately from
        /// whether hands are DRAWN.
        ///
        /// "The hands are gone" has two completely different causes and they
        /// look identical from inside the headset: either the tracker is
        /// giving us nothing (Varjo Base has hand tracking off, or the sensor
        /// cannot see them), or the data is fine and the models are not
        /// rendering (wrong render pipeline variant, wrong layer, culled).
        /// Nothing else in the pipeline distinguishes those, so this does.</summary>
        private void Update()
        {
            if (Provider == null) return;
            if (Time.unscaledTime < _nextHandReport) return;
            _nextHandReport = Time.unscaledTime + 3f;

            var frame = Provider.CurrentFrame;
            int hands = frame?.Hands?.Count ?? -1;

            if (hands == _lastHandCount) return;
            _lastHandCount = hands;

            // Report the physical side too. Hands being tracked and hands
            // being able to PUSH things are separate, and only this line
            // distinguishes "I can see my hands but cannot touch anything".
            if (hands > 0 && PhysicalHands != null)
            {
                bool contact = PhysicalHands.ContactParent != null;
                Debug.Log($"[VrHandTracking] Physical hands: contact rig {(contact ? "BUILT" : "MISSING")}" +
                          $", left={(PhysicalHands.LeftHand != null)}, right={(PhysicalHands.RightHand != null)}. " +
                          "Contact rig missing means nothing can be pressed, however good the tracking looks.", this);
            }

            if (hands <= 0)
                Debug.LogWarning("[VrHandTracking] Tracker is reporting NO HANDS. The data is missing, not the " +
                                 "models — check hand tracking is enabled in Varjo Base and that your hands are " +
                                 "in front of the headset. Nothing in Unity can draw hands that were never sent.", this);
            else
                Debug.Log($"[VrHandTracking] Tracker is reporting {hands} hand(s) — data IS arriving. If nothing " +
                          "is visible, the problem is the hand MODELS (render pipeline variant, layer or culling), " +
                          "not the tracking.", this);
        }

        private float _nextHandReport;
        private int _lastHandCount = int.MinValue;

        // The participant camera everything hand-related is parented under, so
        // the whole rig inherits the car's motion.
        private Camera _cam;

        private void Start()
        {
            var rig = VrRig.Instance;
            if (rig == null || !rig.IsActive)
            {
                Debug.Log("[VrHandTracking] VrRig is not active — no headset, so no hand tracking. " +
                          "This is the normal desktop path.", this);
                enabled = false;
                return;
            }

            var cam = rig.participantCamera;
            if (cam == null)
            {
                Debug.LogError("[VrHandTracking] VrRig has no participant camera — cannot place hands.", this);
                enabled = false;
                return;
            }

            _cam = cam;
            BuildProvider(cam);
            BuildPhysicalHands();
            SpawnHands();

            Debug.Log($"[VrHandTracking] Ultraleap hand tracking up on '{cam.name}'" +
                      (applyXr3Offsets
                          ? $", XR-3 offsets applied (Y {Xr3DeviceOffsetY}, Z {Xr3DeviceOffsetZ}, tilt {Xr3DeviceTiltX})."
                          : ", using Ultraleap's GENERIC offsets — hands will be displaced on an XR-3.") +
                      " If no hands appear, check Varjo Base has hand tracking enabled.", this);
        }

        /// <summary>Creates the provider with its pose driver disabled from the
        /// outset.
        ///
        /// The component is added to a GameObject that starts INACTIVE, so its
        /// OnEnable cannot run until after the flag is cleared. Adding it to a
        /// live object and fixing the flag afterwards would leave a
        /// TrackedPoseDriver already bolted to the participant's camera.</summary>
        private void BuildProvider(Camera cam)
        {
            // PARENTED TO THE CAMERA, NOT TO THIS RIG OBJECT.
            //
            // Ultraleap places hands relative to the PROVIDER'S transform.
            // This component lives on a GameObject at the scene root that
            // never moves, so hanging the provider off it pinned the hands to
            // the world origin area: the car drives off and the hands stay
            // behind at the start line. From inside the headset that reads as
            // "my hands vanish the moment I start moving", which is what it
            // was.
            //
            // Under the camera the whole hand rig inherits the car's motion
            // through the transform hierarchy, which is also what lets
            // temporal warping go back on — see BuildProvider's warping call.
            var go = new GameObject("[VR] Leap Service Provider");
            go.transform.SetParent(cam.transform, false);
            go.SetActive(false);

            Provider = go.AddComponent<LeapXRServiceProvider>();
            Provider._autoCreateTrackedPoseDriver = false; // VrRig owns the camera pose
            Provider.mainCamera = cam;
            SetTemporalWarping(Provider, temporalWarping);

            if (applyXr3Offsets)
            {
                Provider.deviceOffsetMode = LeapXRServiceProvider.DeviceOffsetMode.ManualHeadOffset;
                Provider.deviceOffsetYAxis = Xr3DeviceOffsetY;
                Provider.deviceOffsetZAxis = Xr3DeviceOffsetZ;
                Provider.deviceTiltXAxis = Xr3DeviceTiltX;
            }

            go.SetActive(true); // now OnEnable runs, with the flag already off
        }

        /// <summary>Sets Ultraleap's temporal warping mode.
        ///
        /// Warping is LATENCY COMPENSATION: it places the hands using a
        /// slightly historical head pose so they line up with what the eyes
        /// are seeing rather than trailing the tracking delay. With it Off the
        /// hands are correct in space but visibly lag the real ones — the
        /// "still a bit laggy" complaint.
        ///
        /// It was Off as a workaround for hands being left behind while
        /// driving. That turned out to be the wrong fix for the wrong cause:
        /// the hands were left behind because the provider was parented to a
        /// static object, not because of warping. With the provider under the
        /// camera, the car's motion is carried by the transform hierarchy and
        /// warping only does the job it was designed for — so it goes back on.
        ///
        /// Reflection because the field is private and serialized, with no
        /// public setter. Set BEFORE the provider is enabled — the value is
        /// read during its first update.</summary>
        private static void SetTemporalWarping(LeapXRServiceProvider provider,
                                               LeapXRServiceProvider.TemporalWarpingMode mode)
        {
            const string field = "_temporalWarpingMode";
            var f = typeof(LeapXRServiceProvider).GetField(field,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

            if (f == null)
            {
                Debug.LogWarning($"[VrHandTracking] Could not find '{field}' on LeapXRServiceProvider — the " +
                                 "Ultraleap version may have renamed it. Leaving warping at its default.");
                return;
            }

            f.SetValue(provider, mode);
        }

        /// <summary>Builds the collider hands that can actually push things.
        ///
        /// Plain tracked hands are only a picture: they have no colliders, so
        /// they pass through every surface. PhysicalHandsManager is what turns
        /// them into bodies the physics engine knows about, which is the whole
        /// basis of a slider you push with a finger rather than point at.
        ///
        /// It is a LeapProvider in its own right, wrapping ours as its input —
        /// so it goes downstream of the XR provider, not instead of it.</summary>
        private void BuildPhysicalHands()
        {
            if (physicalHandsManagerPrefab == null)
            {
                Debug.LogWarning("[VrHandTracking] No Physical Hands Manager prefab assigned — hands will be " +
                                 "visible but will pass through everything. Assign Ultraleap's " +
                                 "'Physical Hands Manager' prefab to make surfaces touchable.", this);
                return;
            }

            // Under the camera, for the same reason as the provider: the
            // collider hands must travel with the car, not stay at the origin.
            var go = Instantiate(physicalHandsManagerPrefab, _cam.transform);
            go.name = "[VR] Physical Hands Manager";

            PhysicalHands = go.GetComponent<PhysicalHandsManager>();
            if (PhysicalHands == null)
            {
                Debug.LogError("[VrHandTracking] The assigned prefab has no PhysicalHandsManager on its root.", this);
                Destroy(go);
                return;
            }

            // ASSIGNED WHILE ACTIVE, and that is the whole point.
            // PhysicalHandsManager's InputProvider setter does two things:
            // it subscribes to the provider's frame events, and it starts the
            // PostFixedUpdate coroutine that drives contact resolution. A
            // coroutine cannot be started on an inactive GameObject, so
            // assigning the provider before activating logged
            // "Coroutine couldn't be started because the the game object
            // '[VR] Physical Hands Manager' is inactive!" and lost that half
            // of the wiring until something re-ran the setter.
            //
            // Nothing is lost by assigning after activation: the manager's
            // Awake only finds its authored ContactParent and generates the
            // physics layers, and the collider hands themselves are built in
            // ContactParent.Start — none of which reads InputProvider. The
            // provider is in place well before the first frame is dispatched.
            PhysicalHands.InputProvider = Provider;
        }

        /// <summary>Spawns the hand models under this rig and points them at
        /// the provider. Ultraleap's prefabs default to finding a provider
        /// themselves, but binding explicitly means a second provider
        /// appearing in the scene later cannot silently steal them.</summary>
        private void SpawnHands()
        {
            if (handModelPrefabs == null) return;

            foreach (var prefab in handModelPrefabs)
            {
                if (prefab == null) continue;
                // Inactive first, for the same reason as the manager above:
                // HandModelBase binds to its provider on Awake, and binding
                // after that has already run leaves the model listening to
                // nothing.
                var hands = Instantiate(prefab, _cam.transform);
                hands.SetActive(false);
                hands.name = prefab.name;

                foreach (var model in hands.GetComponentsInChildren<HandModelBase>(true))
                    model.leapProvider = Provider;

                hands.SetActive(true);
            }
        }
    }
}
