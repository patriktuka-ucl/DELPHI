using UnityEngine;
using UnityEngine.UI;
using Delphi.Session;

namespace Delphi.Motion
{
    /// <summary>
    /// The message the participant sees during Phase.EmergencyStop: a sign in
    /// front of them, or the same phrase wrapped right around them.
    ///
    /// GENERATED IN CODE, not authored in the scene, because it has to be
    /// rebuilt whenever the message, size or placement changes, and because it
    /// is the one piece of UI in this project that must be correct on a day
    /// when everything else has gone wrong. It has no scene dependencies that
    /// an unrelated edit can break.
    ///
    /// WHERE TO PUT THIS COMPONENT: anywhere. It anchors to "[VR] Seat
    /// Reference" (the driver's eye point, fixed to the car) and falls back to
    /// the participant camera outside VR. NOT the head — a message welded to
    /// the face is unreadable and nauseating, and this appears precisely when
    /// the participant is already having a bad time.
    /// </summary>
    public class SafetyStopOverlay : MonoBehaviour
    {
        /// <summary>How the message is presented.</summary>
        public enum BannerStyle
        {
            /// <summary>One flat sign straight ahead. Reads instantly, and the
            /// participant does not have to be facing any particular way for it
            /// to make sense.</summary>
            SignInFront = 0,
            /// <summary>The phrase bent into a closed circle all the way
            /// around.</summary>
            Ring360 = 1,
        }

        [Header("Links (auto-found if left empty)")]
        public SessionController session;
        [Tooltip("The camera the participant looks through. Auto-found from " +
                 "VrRig, then by name, then Display 0.")]
        public Camera participantCamera;

        [Header("Message")]
        [Tooltip("A single sign ahead, or the phrase wrapped 360° around the " +
                 "participant.")]
        public BannerStyle bannerStyle = BannerStyle.SignInFront;
        [Tooltip("What the sign says.")]
        [TextArea(1, 3)] public string bannerText = "EMERGENCY STOP";
        [Tooltip("Ring style only — placed between repeats so the ring reads as " +
                 "one continuous line rather than words butted together.")]
        public string separator = "   —   ";
        [Tooltip("Ring style only — how many times the phrase repeats around " +
                 "the full 360°.")]
        [Range(1, 20)] public int loopCount = 4;
        public Color textColor = new Color(1f, 0.23f, 0.23f, 1f);

        [Header("Sign placement (style = Sign In Front)")]
        [Tooltip("How far ahead the sign sits, in metres. Beyond about 1.5 m it " +
                 "converges comfortably in stereo; much closer and it cannot be " +
                 "focused on.")]
        [Range(0.5f, 6f)] public float signDistanceMeters = 2f;
        [Tooltip("Width of the whole sign in metres. The lettering is sized to " +
                 "fit, so this is the only size control you need.")]
        [Range(0.2f, 4f)] public float signWidthMeters = 1.2f;
        [Tooltip("Height relative to the eye line, in metres. Positive is up.")]
        [Range(-2f, 2f)] public float signHeightMeters = 0.2f;

        [Header("Ring placement (style = Ring 360)")]
        [Tooltip("Distance from the participant to the banner, in metres.")]
        [Range(1f, 8f)] public float ringRadiusMeters = 2.5f;
        [Tooltip("Height relative to the eye line, in metres.")]
        [Range(-2f, 2f)] public float ringHeightMeters = -0.15f;
        [Tooltip("Cap height of the lettering, in metres.")]
        [Range(0.05f, 1.5f)] public float textHeightMeters = 0.28f;

        [Header("Screen tint")]
        [Tooltip("Wash the whole view with colour while the stop is active, so " +
                 "the state is unmistakable even if the participant happens to " +
                 "be looking away from the sign.")]
        public bool useTint = true;
        [Tooltip("Tint hue. The alpha of this colour is ignored — use Strength.")]
        public Color tintColor = new Color(0.85f, 0.06f, 0.06f);
        [Tooltip("How strong the tint gets. Keep it low: this sits over the " +
                 "whole world for as long as the stop lasts, and anything heavy " +
                 "enough to read as 'red' at a glance is also heavy enough to " +
                 "be unpleasant after thirty seconds.")]
        [Range(0f, 0.6f)] public float tintStrength = 0.18f;
        [Tooltip("Seconds to fade the tint in. A snap to red is a startle on " +
                 "top of whatever already went wrong.")]
        [Range(0f, 5f)] public float tintFadeInSeconds = 1f;
        [Tooltip("Seconds to fade it out on resume. Quicker than in — coming " +
                 "back is a deliberate act the participant has agreed to.")]
        [Range(0f, 5f)] public float tintFadeOutSeconds = 0.5f;

        [Header("Legacy")]
        [Tooltip("The hand-authored overlay this component used to toggle. " +
                 "Everything is generated now, so this is only kept so an " +
                 "existing scene object stays HIDDEN rather than becoming " +
                 "permanently visible. Safe to clear once you delete it.")]
        public GameObject overlayRoot;

        private GameObject _bannerRoot;
        private Font _font;
        private bool _stopped;

        // Signature of everything the banner's geometry depends on, so an edit
        // in the Inspector rebuilds it in Play mode instead of needing a
        // restart.
        private string _builtSig;

        /// <summary>Font size the banner is always laid out at, in canvas
        /// pixels. Arbitrary — the canvas is then scaled to the requested size
        /// in metres — but FIXED, so the glyph mesh has the same resolution
        /// whatever size the banner is asked to be.</summary>
        private const int BannerFontSize = 100;

        /// <summary>Cap height as a fraction of the requested font size for the
        /// built-in legacy font. Only used to turn metres into a canvas
        /// scale.</summary>
        private const float CapHeightRatio = 0.72f;

        private void Awake()
        {
            if (session == null) session = FindAnyObjectByType<SessionController>();
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

            _stopped = session.CurrentPhase == SessionController.Phase.EmergencyStop;

            // Rebuilt live WHILE stopped so the message, size and placement can
            // be dialled in against the headset instead of by re-triggering an
            // emergency stop for every adjustment. EnsureBanner returns
            // immediately unless something actually changed.
            if (_stopped) EnsureBanner();

            if (_bannerRoot != null && _bannerRoot.activeSelf != _stopped)
                _bannerRoot.SetActive(_stopped);

            TickTint();
        }

        // ── Screen tint ─────────────────────────────────────────────────

        private Image _tintImg;
        private float _tintAlpha;

        /// <summary>Ramps the full-view colour wash.
        ///
        /// Driven from the phase every frame rather than switched on and off at
        /// the transitions, so a stop that ends while the tint is still fading
        /// in simply turns around from wherever it got to — no state to get
        /// stuck, and no way to leave the participant looking at a red world
        /// after the session resumed.</summary>
        private void TickTint()
        {
            if (!useTint)
            {
                if (_tintImg != null && _tintImg.gameObject.activeSelf)
                    _tintImg.gameObject.SetActive(false);
                return;
            }

            float target = _stopped ? Mathf.Clamp01(tintStrength) : 0f;
            float seconds = _stopped ? tintFadeInSeconds : tintFadeOutSeconds;

            _tintAlpha = seconds <= 0.001f
                ? target
                : Mathf.MoveTowards(_tintAlpha, target,
                                    Mathf.Max(0.0001f, tintStrength) * Time.unscaledDeltaTime / seconds);

            if (_tintAlpha <= 0.0005f && !_stopped)
            {
                // Fully clear: take it out of the render entirely rather than
                // leaving a transparent full-screen quad drawing every frame
                // for the whole session.
                if (_tintImg != null && _tintImg.gameObject.activeSelf)
                    _tintImg.gameObject.SetActive(false);
                return;
            }

            EnsureTint();
            if (_tintImg == null) return;

            if (!_tintImg.gameObject.activeSelf) _tintImg.gameObject.SetActive(true);
            _tintImg.color = new Color(tintColor.r, tintColor.g, tintColor.b, _tintAlpha);
        }

        /// <summary>A plain coloured canvas pinned just past the camera's near
        /// plane.
        ///
        /// HEAD-LOCKED ON PURPOSE, which is the opposite of the rule the sign
        /// follows. A head-locked IMAGE is uncomfortable because it has detail
        /// the eyes try to fix on while the world moves behind it; a uniform
        /// wash has no detail and no disparity cues, so there is nothing to
        /// converge on and nothing to fight. It also has to be head-locked to
        /// do its job — a tint you can look away from is not a tint.
        ///
        /// Sat just past the near plane so no cockpit geometry can poke through
        /// it, and oversized because the per-eye FOV in the headset is the XR
        /// system's, not Camera.fieldOfView's.</summary>
        private void EnsureTint()
        {
            if (_tintImg != null || participantCamera == null) return;

            float d = Mathf.Max(0.05f, participantCamera.nearClipPlane * 1.2f);

            var go = new GameObject("Safety Stop Tint (generated)", typeof(Canvas));
            go.transform.SetParent(participantCamera.transform, false);
            go.transform.localPosition = new Vector3(0f, 0f, d);
            go.transform.localRotation = Quaternion.identity;

            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = participantCamera;
            // Above the sign's canvas, so the wash sits over the message too —
            // it is a tint on the whole view, not on the world behind the sign.
            canvas.sortingOrder = 32000;

            const float Px = 100f;
            var crt = go.GetComponent<RectTransform>();
            crt.sizeDelta = new Vector2(Px, Px);
            // Six near-plane distances across covers about ±71°, comfortably
            // past any headset FOV.
            go.transform.localScale = Vector3.one * (d * 6f / Px);

            var imgGO = new GameObject("Tint", typeof(Image));
            imgGO.transform.SetParent(go.transform, false);
            _tintImg = imgGO.GetComponent<Image>();
            _tintImg.raycastTarget = false;
            _tintImg.color = new Color(tintColor.r, tintColor.g, tintColor.b, _tintAlpha);

            var irt = _tintImg.rectTransform;
            irt.anchorMin = Vector2.zero; irt.anchorMax = Vector2.one;
            irt.offsetMin = irt.offsetMax = Vector2.zero;
        }

        // ── Construction ────────────────────────────────────────────────

        private void EnsureBanner()
        {
            string sig = $"{bannerStyle}|{bannerText}|{separator}|{loopCount}|" +
                         $"{signDistanceMeters}|{signWidthMeters}|{signHeightMeters}|" +
                         $"{ringRadiusMeters}|{ringHeightMeters}|{textHeightMeters}|" +
                         $"{ColorUtility.ToHtmlStringRGBA(textColor)}";
            if (_bannerRoot != null && sig == _builtSig) return;
            _builtSig = sig;

            // Hidden before Destroy, which is deferred to the end of the frame:
            // otherwise the outgoing banner draws over the incoming one for a
            // frame every time a field is nudged.
            if (_bannerRoot != null) { _bannerRoot.SetActive(false); Destroy(_bannerRoot); }

            var anchor = ResolveAnchor();
            if (anchor == null)
            {
                Debug.LogWarning("[SafetyStopOverlay] No seat reference or participant camera — the stop " +
                                 "banner has nowhere to anchor and will not appear.", this);
                return;
            }

            bool sign = bannerStyle == BannerStyle.SignInFront;

            _bannerRoot = new GameObject("Safety Stop Banner (generated)");
            _bannerRoot.transform.SetParent(anchor, false);
            _bannerRoot.transform.localPosition =
                new Vector3(0f, sign ? signHeightMeters : ringHeightMeters, 0f);
            _bannerRoot.transform.localRotation = Quaternion.identity;

            if (sign) BuildSign(); else BuildRing();

            _bannerRoot.SetActive(_stopped);
        }

        /// <summary>One flat sign straight ahead, on a dark plate with a
        /// coloured rule under the text.
        ///
        /// The plate matters more than it looks. Unbacked coloured text over
        /// whatever happens to be behind it — a bright sky, a pale wall — is
        /// the one case where the message becomes illegible, and it is not
        /// predictable from the Editor because it depends where the car
        /// stopped. A dark panel makes the contrast the sign's own property
        /// rather than the scene's.</summary>
        private void BuildSign()
        {
            string phrase = string.IsNullOrEmpty(bannerText) ? "EMERGENCY STOP" : bannerText;

            var canvasGO = NewCanvas("Sign");
            canvasGO.transform.localRotation = Quaternion.identity;

            var txt = NewBannerText(canvasGO.transform, phrase);

            // THE PLATE IS SIZED TO THE MESSAGE, and the whole thing is then
            // scaled to signWidthMeters. Authoring a width in metres and
            // letting the lettering follow is the right way round: a fixed
            // pixel plate with metre-sized type meant a longer message silently
            // grew a four-metre sign that ran past the edge of vision.
            float plateW = txt.preferredWidth + BannerFontSize * 1.4f;
            float plateH = BannerFontSize * 2.1f;
            float scale = Mathf.Max(0.05f, signWidthMeters) / Mathf.Max(1f, plateW);

            canvasGO.transform.localPosition = new Vector3(0f, 0f, Mathf.Max(0.2f, signDistanceMeters));
            canvasGO.transform.localScale = Vector3.one * scale;
            ((RectTransform)canvasGO.transform).sizeDelta = new Vector2(plateW, plateH);

            var plate = NewSignImage(canvasGO.transform, new Color(0.03f, 0.04f, 0.06f, 0.88f));
            var prt = plate.rectTransform;
            prt.anchorMin = Vector2.zero; prt.anchorMax = Vector2.one;
            prt.offsetMin = prt.offsetMax = Vector2.zero;
            plate.transform.SetAsFirstSibling();          // behind the lettering

            var rule = NewSignImage(canvasGO.transform, textColor);
            var rrt = rule.rectTransform;
            rrt.anchorMin = rrt.anchorMax = new Vector2(0.5f, 0f);
            rrt.pivot = new Vector2(0.5f, 0f);
            rrt.anchoredPosition = new Vector2(0f, plateH * 0.16f);
            rrt.sizeDelta = new Vector2(plateW - BannerFontSize, BannerFontSize * 0.07f);

            var trt = txt.rectTransform;
            trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
            trt.offsetMin = new Vector2(0f, plateH * 0.12f); trt.offsetMax = Vector2.zero;
            txt.transform.SetAsLastSibling();
        }

        /// <summary>The phrase repeated and bent into a closed circle by
        /// RingTextWrap — ONE string on ONE canvas centred on the participant,
        /// not a carousel of panels each holding its own copy.</summary>
        private void BuildRing()
        {
            string phrase = string.IsNullOrEmpty(bannerText) ? "EMERGENCY STOP" : bannerText;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < Mathf.Clamp(loopCount, 1, 20); i++) sb.Append(phrase).Append(separator);

            // METRES → CANVAS SCALE, from the cap height alone. The font size is
            // fixed and the canvas is scaled to suit, rather than the other way
            // round, so changing the radius moves the ring without
            // re-rasterising the font atlas.
            float scale = Mathf.Max(1e-6f, textHeightMeters) / (BannerFontSize * CapHeightRatio);
            float radiusPixels = Mathf.Max(0.3f, ringRadiusMeters) / scale;

            var canvasGO = NewCanvas("Ring");
            canvasGO.transform.localPosition = Vector3.zero;   // centred ON the participant
            canvasGO.transform.localRotation = Quaternion.identity;
            canvasGO.transform.localScale = Vector3.one * scale;

            // Sized to enclose the whole ring even though nothing is drawn on
            // it: the canvas rect is what Unity culls against, and a small rect
            // around geometry pushed out to the radius can vanish when the
            // participant looks away from the origin.
            ((RectTransform)canvasGO.transform).sizeDelta =
                new Vector2(radiusPixels * 2f, radiusPixels * 2f);

            var txt = NewBannerText(canvasGO.transform, sb.ToString());
            var trt = txt.rectTransform;
            trt.anchorMin = trt.anchorMax = trt.pivot = new Vector2(0.5f, 0.5f);
            trt.anchoredPosition = Vector2.zero;
            trt.sizeDelta = new Vector2(radiusPixels * 2f, BannerFontSize * 2f);

            txt.gameObject.AddComponent<RingTextWrap>().radiusPixels = radiusPixels;

            WarnIfCrowded(txt, radiusPixels);
        }

        /// <summary>Says out loud how hard the letters are being squeezed or
        /// stretched to close the ring.
        ///
        /// Loop count, radius and cap height are three independent fields that
        /// jointly decide one thing — how much text has to fit into a fixed
        /// circumference — so any two can silently make the third unreadable.
        /// The wrap always closes, which is why the problem is invisible until
        /// someone is wearing the headset: it never looks broken, it just looks
        /// wrong.</summary>
        private static void WarnIfCrowded(Text txt, float radiusPixels)
        {
            float fit = RingTextWrap.FitFactor(2f * Mathf.PI * radiusPixels, txt.preferredWidth);
            if (fit >= 0.6f && fit <= 1.8f) return;

            string advice = fit < 0.6f
                ? "letters are crowded — lower Loop Count, lower Text Height, or raise Ring Radius"
                : "letters are stretched thin — raise Loop Count, raise Text Height, or lower Ring Radius";
            Debug.LogWarning($"[SafetyStopOverlay] Ring letter spacing is {fit:0.00}x natural: {advice}.");
        }

        // ── Helpers ─────────────────────────────────────────────────────

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

        private GameObject NewCanvas(string name)
        {
            var go = new GameObject(name, typeof(Canvas));
            go.transform.SetParent(_bannerRoot.transform, false);
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = participantCamera;
            return go;
        }

        private Text NewBannerText(Transform parent, string content)
        {
            var go = new GameObject("Text", typeof(Text));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<Text>();
            t.text = content;
            t.font = _font;
            t.fontSize = BannerFontSize;
            t.fontStyle = FontStyle.Bold;
            t.color = textColor;
            t.alignment = TextAnchor.MiddleCenter;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.raycastTarget = false;
            return t;
        }

        /// <summary>Plain image on the sign. Sibling ORDER decides what covers
        /// what inside a canvas — not depth — so the plate is made the first
        /// child and the text the last, and no material trickery is needed to
        /// keep the backing behind the lettering.</summary>
        private static Image NewSignImage(Transform parent, Color c)
        {
            var go = new GameObject("Img", typeof(Image));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.color = c;
            img.raycastTarget = false;
            return img;
        }
    }
}
