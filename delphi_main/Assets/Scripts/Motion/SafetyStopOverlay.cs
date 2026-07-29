using UnityEngine;
using UnityEngine.UI;
using Varjo.XR;
using Delphi.Session;

namespace Delphi.Motion
{
    /// <summary>
    /// What the participant sees during Phase.EmergencyStop: the room fades in
    /// through the headset's passthrough cameras, with a warning banner
    /// wrapped right around them so it is readable whichever way they happen to
    /// be facing.
    ///
    /// WHY PASSTHROUGH RATHER THAN JUST A MESSAGE.
    ///
    ///   An emergency stop is triggered when something has gone wrong for the
    ///   person strapped into a moving motion platform. The single most useful
    ///   thing to give them at that moment is their actual surroundings — the
    ///   room, the harness, the researcher walking toward them. A virtual sign
    ///   saying the ride has stopped still leaves them sealed inside a headset
    ///   during the exact minute they most want out of it.
    ///
    /// WHY IT FADES INSTEAD OF CUTTING.
    ///
    ///   A hard cut from a moving virtual world to a static real one is a
    ///   vestibular mismatch delivered instantly, to someone who may already be
    ///   nauseated — which is a plausible reason the stop was pressed at all.
    ///   Three seconds is long enough for the eyes to follow the change and
    ///   short enough not to feel like a malfunction. It is on a slider because
    ///   the right number is a judgement about participants, not about code.
    ///
    /// WHY THE TEXT RING IS BUILT IN CODE.
    ///
    ///   It has to be regenerated whenever the message, the radius or the
    ///   repeat count changes, and a hand-authored ring of a dozen canvases is
    ///   something nobody will re-lay-out correctly under time pressure. It is
    ///   also the one piece of UI in this project that MUST be correct on a day
    ///   when everything else has gone wrong, so it has no scene dependencies
    ///   that can be broken by an unrelated edit.
    ///
    /// WHERE TO PUT THIS COMPONENT: anywhere. It anchors its own ring to
    /// "[VR] Seat Reference" (the driver's eye point, fixed to the car) and
    /// falls back to the participant camera outside VR. NOT the head — a
    /// message welded to the face is unreadable and nauseating, and this thing
    /// appears precisely when the participant is already having a bad time.
    /// </summary>
    public class SafetyStopOverlay : MonoBehaviour
    {
        [Header("Links (auto-found if left empty)")]
        public SessionController session;
        [Tooltip("The camera the participant looks through. Auto-found from " +
                 "VrRig, then by name, then Display 0.")]
        public Camera participantCamera;

        [Header("Message")]
        [Tooltip("The phrase repeated around the ring. Whatever you type here " +
                 "is what a participant reads in every direction.")]
        [TextArea(1, 3)] public string bannerText = "EMERGENCY STOP";
        [Tooltip("Placed between repeats, so the ring reads as one continuous " +
                 "line rather than words butted together.")]
        public string separator = "   —   ";
        [Tooltip("How many times the phrase goes around. Also how many flat " +
                 "segments approximate the circle, so higher is both more " +
                 "repeats AND a rounder ring.")]
        [Range(3, 40)] public int repeatCount = 12;
        public Color textColor = new Color(1f, 0.23f, 0.23f, 1f);

        [Header("Ring placement (around the driver's eye point)")]
        [Tooltip("Distance from the participant to the banner, in metres. " +
                 "Beyond about 1.5 m it converges comfortably in stereo; too " +
                 "close and it cannot be focused on.")]
        [Range(1f, 8f)] public float ringRadiusMeters = 2.5f;
        [Tooltip("Height relative to the eye line, in metres. Slightly below " +
                 "eye level reads as signage rather than a heads-up display.")]
        [Range(-2f, 2f)] public float ringHeightMeters = -0.15f;
        [Tooltip("Cap height of the lettering, in metres.")]
        [Range(0.05f, 1.5f)] public float textHeightMeters = 0.30f;

        [Header("Passthrough")]
        [Tooltip("Fade the Varjo XR-3's video see-through image in over the " +
                 "virtual scene when the stop is triggered. No effect without " +
                 "mixed-reality hardware, so the desktop path is untouched.")]
        public bool usePassthrough = true;
        [Tooltip("Seconds for the real room to fade in. A hard cut is a " +
                 "vestibular mismatch delivered instantly to someone who may " +
                 "already be nauseated; too slow reads as a malfunction.")]
        [Range(0f, 10f)] public float fadeSeconds = 3f;
        [Tooltip("How much of the virtual scene is left at the end of the " +
                 "fade. 0 = the room only. Raise it if the banner needs " +
                 "something behind it to read against.")]
        [Range(0f, 1f)] public float finalSceneOpacity = 0f;
        [Tooltip("Seconds to fade the virtual scene back in when the session " +
                 "resumes. Quicker than the fade out — coming back is a " +
                 "deliberate act the participant has agreed to.")]
        [Range(0f, 10f)] public float restoreSeconds = 1f;

        [Header("Legacy")]
        [Tooltip("The hand-authored overlay this component used to toggle. " +
                 "Everything is generated now, so this is only kept so an " +
                 "existing scene object stays HIDDEN rather than becoming " +
                 "permanently visible. Safe to clear once you delete it.")]
        public GameObject overlayRoot;

        // ── Generated content ───────────────────────────────────────────
        private GameObject _ringRoot;
        private Transform _anchor;
        private Font _font;
        private Material _textMat;

        private GameObject _wipeQuad;
        private Material _wipeMat;
        private static readonly int SceneAlphaId = Shader.PropertyToID("_SceneAlpha");

        // 1 = fully virtual, 0 = fully passthrough. Ramped, never snapped.
        private float _sceneAlpha = 1f;
        private bool _stopped;
        private bool _passthroughOn;
        private bool _mrChecked, _mrAvailable;

        // Signature of everything the ring's geometry depends on, so an edit in
        // the Inspector rebuilds it in Play mode instead of needing a restart.
        private string _builtSig;

        /// <summary>Layout pixels per ring segment. Arbitrary — the segment is
        /// scaled to its arc afterwards — but fixed, so the font size means the
        /// same thing whatever the radius is.</summary>
        private const int SegmentPixelW = 600, SegmentPixelH = 150;

        private void Awake()
        {
            if (session == null) session = FindFirstObjectByType<SessionController>();
            ResolveCamera();

            // Kept hidden rather than merely ignored: with this component no
            // longer driving it, a leftover scene overlay would otherwise sit
            // in front of the participant for the WHOLE session.
            if (overlayRoot != null) overlayRoot.SetActive(false);

            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        }

        private void ResolveCamera()
        {
            if (participantCamera != null) return;
            if (VR.VrRig.Instance != null && VR.VrRig.Instance.participantCamera != null)
            {
                participantCamera = VR.VrRig.Instance.participantCamera;
                return;
            }
            foreach (var cam in Camera.allCameras)
                if (cam.name == "Person View") { participantCamera = cam; return; }
            foreach (var cam in Camera.allCameras)
                if (cam.targetDisplay == 0) { participantCamera = cam; return; }
        }

        private void Update()
        {
            if (session == null) return;

            bool shouldStop = session.CurrentPhase == SessionController.Phase.EmergencyStop;
            if (shouldStop != _stopped)
            {
                _stopped = shouldStop;
                if (_stopped) EnterStop(); else ExitStop();
            }

            // Rebuilt live WHILE stopped so the message, radius and repeat
            // count can be dialled in against the headset instead of by
            // restarting into an emergency stop for every adjustment.
            // EnsureRing returns immediately unless something actually changed.
            if (_stopped) EnsureRing();

            if (_ringRoot != null && _ringRoot.activeSelf != _stopped)
                _ringRoot.SetActive(_stopped);

            TickFade();
        }

        // ── Enter / exit ────────────────────────────────────────────────

        private void EnterStop()
        {
            EnsureRing();
            EnsureWipe();

            // Passthrough comes up BEFORE the alpha starts moving. Starting VST
            // rendering takes the compositor a moment; if the scene alpha were
            // already falling, the participant would get a window of blended
            // nothing — the virtual world half gone and the room not yet there.
            if (usePassthrough && MrAvailable())
            {
                VarjoMixedReality.StartRender();
                VarjoRendering.SetOpaque(false);
                _passthroughOn = true;
            }

            if (_wipeQuad != null) _wipeQuad.SetActive(_passthroughOn);
        }

        private void ExitStop()
        {
            // The scene fades back in first and the compositor is only told to
            // go opaque once it has (see TickFade) — dropping passthrough at
            // alpha 0 would black the headset out for the frames in between.
        }

        private void TickFade()
        {
            float target = _stopped ? Mathf.Clamp01(finalSceneOpacity) : 1f;
            float seconds = _stopped ? fadeSeconds : restoreSeconds;

            _sceneAlpha = seconds <= 0.001f
                ? target
                : Mathf.MoveTowards(_sceneAlpha, target, Time.unscaledDeltaTime / seconds);

            if (_wipeMat != null) _wipeMat.SetFloat(SceneAlphaId, _sceneAlpha);

            // Fully virtual again — hand the compositor back its fast path.
            // Left non-opaque, every frame of the rest of the session would be
            // alpha-blended against the cameras for no reason.
            if (_passthroughOn && !_stopped && _sceneAlpha >= 0.999f)
            {
                VarjoRendering.SetOpaque(true);
                VarjoMixedReality.StopRender();
                _passthroughOn = false;
                if (_wipeQuad != null) _wipeQuad.SetActive(false);
            }
        }

        private bool MrAvailable()
        {
            // Cached: IsMRAvailable is a native call, and the answer cannot
            // change during a session — the headset is not hot-swapped.
            if (_mrChecked) return _mrAvailable;
            _mrChecked = true;
            try { _mrAvailable = VarjoMixedReality.IsMRAvailable(); }
            catch (System.Exception e)
            {
                _mrAvailable = false;
                Debug.LogWarning("[SafetyStopOverlay] Varjo mixed reality unavailable — the stop banner " +
                                 "will show without passthrough. " + e.Message, this);
            }
            if (!_mrAvailable)
                Debug.Log("[SafetyStopOverlay] No mixed-reality hardware; showing the banner only. " +
                          "Normal on the desktop path.", this);
            return _mrAvailable;
        }

        private void OnDisable()
        {
            // Never leave the compositor blending against the cameras because
            // the component happened to be switched off mid-stop.
            if (!_passthroughOn) return;
            VarjoRendering.SetOpaque(true);
            VarjoMixedReality.StopRender();
            _passthroughOn = false;
        }

        // ── The 360° banner ─────────────────────────────────────────────

        /// <summary>Builds (or rebuilds) the ring. Cheap to call — it returns
        /// immediately unless something it depends on actually changed, which
        /// is what lets the Inspector fields be dragged in Play mode.</summary>
        private void EnsureRing()
        {
            string sig = $"{bannerText}|{separator}|{repeatCount}|{ringRadiusMeters}|" +
                         $"{ringHeightMeters}|{textHeightMeters}|{ColorUtility.ToHtmlStringRGBA(textColor)}";
            if (_ringRoot != null && sig == _builtSig) return;
            _builtSig = sig;

            // Hidden before Destroy, which is deferred to the end of the frame:
            // otherwise the outgoing ring draws over the incoming one for a
            // frame every time a field is nudged.
            if (_ringRoot != null) { _ringRoot.SetActive(false); Destroy(_ringRoot); }

            _anchor = ResolveAnchor();
            if (_anchor == null)
            {
                Debug.LogWarning("[SafetyStopOverlay] No seat reference or participant camera — the stop " +
                                 "banner has nowhere to anchor and will not appear.", this);
                return;
            }

            _ringRoot = new GameObject("Safety Stop Banner (generated)");
            _ringRoot.transform.SetParent(_anchor, false);
            _ringRoot.transform.localPosition = new Vector3(0f, ringHeightMeters, 0f);
            _ringRoot.transform.localRotation = Quaternion.identity;

            int n = Mathf.Clamp(repeatCount, 3, 40);
            float radius = Mathf.Max(0.3f, ringRadiusMeters);

            // Segment width comes from the CIRCUMFERENCE, not from the text, so
            // the repeats tile the full 360° exactly however many there are.
            // Sizing segments to their content instead would leave a wedge of
            // empty ring wherever the arithmetic did not come out even — i.e. a
            // direction the participant can face and see no message at all.
            float arcWidth = 2f * Mathf.PI * radius / n;
            float scale = arcWidth / SegmentPixelW;

            string phrase = string.IsNullOrEmpty(bannerText) ? "EMERGENCY STOP" : bannerText;
            string content = phrase + separator;

            // Cap height in metres → font size in layout pixels. Legacy fonts
            // render caps at roughly 0.72 of the requested size.
            int fontSize = Mathf.Clamp(
                Mathf.RoundToInt(Mathf.Max(0.02f, textHeightMeters) / Mathf.Max(scale, 1e-6f) / 0.72f),
                8, 400);

            EnsureTextMaterial();

            for (int i = 0; i < n; i++)
            {
                float deg = 360f * i / n;

                var segment = new GameObject($"Segment_{i}", typeof(Canvas));
                segment.transform.SetParent(_ringRoot.transform, false);

                var canvas = segment.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.WorldSpace;
                canvas.worldCamera = participantCamera;

                var crt = segment.GetComponent<RectTransform>();
                crt.sizeDelta = new Vector2(SegmentPixelW, SegmentPixelH);

                // Face INWARD: the participant is at the centre looking out, so
                // each panel is rotated to present its front to the middle.
                var rot = Quaternion.Euler(0f, deg, 0f);
                segment.transform.localRotation = rot;
                segment.transform.localPosition = rot * new Vector3(0f, 0f, radius);
                segment.transform.localScale = Vector3.one * scale;

                var t = new GameObject("Text", typeof(Text));
                t.transform.SetParent(segment.transform, false);
                var txt = t.GetComponent<Text>();
                txt.text = content;
                txt.font = _font;
                txt.fontSize = fontSize;
                txt.fontStyle = FontStyle.Bold;
                txt.color = textColor;
                txt.alignment = TextAnchor.MiddleCenter;
                txt.horizontalOverflow = HorizontalWrapMode.Overflow;
                txt.verticalOverflow = VerticalWrapMode.Overflow;
                txt.raycastTarget = false;
                // Draws after the passthrough wipe and ignores depth — see the
                // shader's own notes for why neither is optional here.
                txt.material = _textMat;

                var trt = t.GetComponent<RectTransform>();
                trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
                trt.offsetMin = trt.offsetMax = Vector2.zero;
            }

            _ringRoot.SetActive(_stopped);
        }

        /// <summary>The driver's eye point fixed to the car — never the head.
        /// See the class summary.</summary>
        private Transform ResolveAnchor()
        {
            if (VR.VrRig.Instance != null && VR.VrRig.Instance.IsActive &&
                VR.VrRig.Instance.SeatReference != null)
                return VR.VrRig.Instance.SeatReference;

            ResolveCamera();
            return participantCamera != null ? participantCamera.transform : null;
        }

        private void EnsureTextMaterial()
        {
            if (_textMat != null) return;
            var sh = Shader.Find("Delphi/SafetyStopText");
            if (sh == null)
            {
                Debug.LogWarning("[SafetyStopOverlay] Shader 'Delphi/SafetyStopText' not found — the banner " +
                                 "will use the default UI material, so it can be hidden behind the car and " +
                                 "will fade out with the scene. Check the shader is in the project and, for " +
                                 "a build, listed under Project Settings > Graphics > Always Included Shaders.",
                                 this);
                return;
            }
            _textMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
        }

        // ── The passthrough wipe ────────────────────────────────────────

        /// <summary>A quad parented to the camera whose only job is to write
        /// the framebuffer alpha the Varjo compositor blends on.
        ///
        /// Deliberately oversized rather than fitted to the frustum: in VR the
        /// per-eye projection is the XR system's, not Camera.fieldOfView's, so
        /// a quad sized from the Camera would leave an unfaded border in the
        /// headset while looking perfectly correct in the Editor. It writes
        /// alpha only and ignores depth, so the overspill costs nothing.</summary>
        private void EnsureWipe()
        {
            if (_wipeQuad != null || participantCamera == null) return;

            var sh = Shader.Find("Delphi/SafetyStopPassthroughWipe");
            if (sh == null)
            {
                Debug.LogWarning("[SafetyStopOverlay] Shader 'Delphi/SafetyStopPassthroughWipe' not found — " +
                                 "the banner will show but the room will not fade in. For a build, add it " +
                                 "to Project Settings > Graphics > Always Included Shaders.", this);
                return;
            }

            _wipeMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
            _wipeMat.SetFloat(SceneAlphaId, _sceneAlpha);

            _wipeQuad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            _wipeQuad.name = "Passthrough Wipe (generated)";
            var col = _wipeQuad.GetComponent<Collider>();
            if (col != null) Destroy(col);   // a full-screen collider would eat every raycast in the scene

            _wipeQuad.transform.SetParent(participantCamera.transform, false);
            float d = Mathf.Max(0.05f, participantCamera.nearClipPlane * 2f);
            _wipeQuad.transform.localPosition = new Vector3(0f, 0f, d);
            _wipeQuad.transform.localRotation = Quaternion.identity;
            _wipeQuad.transform.localScale = Vector3.one * (d * 12f);   // covers past any plausible FOV

            var mr = _wipeQuad.GetComponent<MeshRenderer>();
            mr.sharedMaterial = _wipeMat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

            _wipeQuad.SetActive(false);
        }
    }
}
