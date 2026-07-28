using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Delphi.Session
{
    /// <summary>
    /// The PARTICIPANT-facing free-play control surface — a world-space
    /// canvas mounted in front of the driver (parented to the participant's
    /// own camera by default, so it stays in front of them as the car
    /// moves), with one live slider per driving-style parameter. Only
    /// visible during Phase.FreePlay.
    ///
    /// This is a pure view, same as ExperimentUI: it reads SessionController
    /// and CarDriver's live state and writes back through
    /// SessionController.SetFreePlayParameter — no logging or phase logic
    /// lives here, that's all already on SessionController and applies
    /// identically regardless of which UI called it.
    ///
    /// Positioning is exposed in the Inspector because it genuinely needs a
    /// human's eyes in the Editor (and ideally the actual seat) to get right
    /// — there's no way to derive "in front of the driver" from code alone.
    /// </summary>
    public class FreePlayPanel : MonoBehaviour
    {
        [Header("Links (auto-found if left empty)")]
        public SessionController session;
        [Tooltip("Overrides participantCamera as the mount point, if you'd " +
                 "rather anchor to something else (e.g. a dashboard mesh " +
                 "transform already positioned where you want the panel).")]
        public Transform anchorOverride;
        [Tooltip("The camera the participant actually looks through. " +
                 "Auto-found: the first enabled camera with Target Display " +
                 "0 (Display 1, the participant screen) — in the sample " +
                 "scene that's 'Person View', a child of the car.")]
        public Camera participantCamera;

        [Header("Placement (local to the anchor, world units = metres)")]
        [Tooltip("Desktop placement. In VR the panel uses the VR placement " +
                 "below instead, because it has to be within arm's reach.")]
        public Vector3 localPosition = new Vector3(0f, -0.15f, 0.5f);
        public Vector3 localEulerAngles = Vector3.zero;
        public Vector2 sizeMeters = new Vector2(0.5f, 0.44f);

        [Header("VR — physical touch")]
        [Tooltip("In VR, replace the mouse-driven uGUI sliders with Ultraleap " +
                 "sliders the participant pushes with a finger. Needs hand " +
                 "tracking and a Physical Hands Manager; falls back to the " +
                 "uGUI sliders automatically when either is missing, so the " +
                 "desktop workflow is untouched.")]
        public bool usePhysicalSlidersInVr = true;
        [Tooltip("Ultraleap's 'Physical Hands Slider' prefab.")]
        public GameObject physicalSliderPrefab;
        [Tooltip("Where the panel sits in VR, relative to the DRIVER'S SEAT " +
                 "(not the head). Close enough to touch without leaning: " +
                 "roughly lap height and a comfortable forearm away.")]
        public Vector3 vrLocalPosition = new Vector3(0f, -0.34f, 0.42f);
        [Tooltip("Tilted back toward the participant so it faces the eyes " +
                 "while sitting low enough to reach — like a car centre " +
                 "console rather than a wall.")]
        public Vector3 vrLocalEulerAngles = new Vector3(38f, 0f, 0f);
        [Tooltip("How far each physical slider travels, in metres.")]
        public float sliderTravelMeters = 0.16f;
        [Tooltip("Vertical gap between physical sliders, in metres. Too tight " +
                 "and a finger reaching for one nudges its neighbour.")]
        public float sliderSpacingMeters = 0.055f;

        private const int PixelWidth = 640;
        private const int PixelHeight = 660;

        private static readonly string[] ParamLabels =
            { "Acceleration", "Braking", "Follow distance", "Cornering speed" };
        private static readonly string[] ParamKeys =
            { "accelerationJerk", "brakingJerk", "followDistance", "corneringSpeed" };

        private Font _font;
        private GameObject _panelRoot; // toggled on/off with Phase.FreePlay; this component's own GameObject stays active so Update() keeps polling
        private readonly Text[] _values = new Text[ParamKeys.Length];

        private bool _mountedToSeat;
        private bool _physical;   // seat-mounted VR layout in use
        private readonly VR.VrTouchSlider[] _touchSliders = new VR.VrTouchSlider[ParamKeys.Length];

        private void Awake()
        {
            if (session == null) session = FindFirstObjectByType<SessionController>();
            if (participantCamera == null)
            {
                // Name first, Display 0 only as a fallback: the researcher's
                // "Track Overview Camera" also sits on Display 0 at the same depth,
                // so "first camera on Display 0" is a coin toss that lands on
                // a camera 150 m above the track about half the time.
                foreach (var cam in Camera.allCameras)
                    if (cam.name == "Person View") { participantCamera = cam; break; }
                if (participantCamera == null)
                    foreach (var cam in Camera.allCameras)
                        if (cam.targetDisplay == 0) { participantCamera = cam; break; }
            }

            // ANCHOR TO THE SEAT IN VR, NEVER THE HEAD.
            //
            // The desktop panel hangs off the camera, which is fine for a
            // thing you click with a mouse. It is useless for a thing you
            // TOUCH: a panel welded to the head retreats exactly as fast as
            // the finger approaches, so the slider can never be reached. It is
            // also the arrangement VrRig's notes warn about on comfort
            // grounds. The seat reference is fixed to the car, so it moves
            // with the drive and holds still relative to the hands.
            var seat = VR.VrRig.Instance != null && VR.VrRig.Instance.IsActive
                ? VR.VrRig.Instance.SeatReference
                : null;

            var mount = anchorOverride != null ? anchorOverride
                      : seat != null ? seat
                      : participantCamera != null ? participantCamera.transform : null;

            _mountedToSeat = anchorOverride == null && seat != null;

            if (mount == null)
            {
                Debug.LogWarning("[FreePlayPanel] No participant camera (Target Display 0) found and no " +
                                  "anchorOverride set — the panel has nowhere to mount and will not appear. " +
                                  "Assign one manually if auto-find can't find it.");
                enabled = false;
                return;
            }

            if (FindFirstObjectByType<EventSystem>() == null)
                Debug.LogWarning("[FreePlayPanel] No EventSystem in the scene — the panel will render but " +
                                  "won't respond to clicks/taps. Add one (GameObject > UI > Event System).");

            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            BuildUI(mount);
        }

        private void Update()
        {
            if (session == null || _panelRoot == null) return;

            bool active = session.CurrentPhase == SessionController.Phase.FreePlay;
            if (_panelRoot.activeSelf != active) _panelRoot.SetActive(active);
            if (!active || session.carDriver == null) return;

            var p = session.carDriver.parameters;
            float[] v = { p.accelerationJerk, p.brakingJerk, p.followDistance,
                          p.corneringSpeed };

            for (int i = 0; i < ParamKeys.Length; i++)
            {
                // The slider owns its own readout, so this only has to keep
                // it in step with the car when the participant is NOT touching
                // it — notify:false so echoing a value back cannot loop into
                // SetFreePlayParameter and fight the finger.
                var ts = _touchSliders[i];
                if (ts != null && !ts.IsEngaged)
                    ts.SetValue(v[i], notify: false);
            }
        }

        // ── UI construction ─────────────────────────────────────────────
        private void BuildUI(Transform mount)
        {
            _physical = usePhysicalSlidersInVr && _mountedToSeat && physicalSliderPrefab != null;

            // A plain holder at identity scale, so ONE SetActive hides the
            // whole control surface. The canvas cannot serve as that root: it
            // carries a non-uniform metres-per-pixel scale, and parenting rigid
            // bodies under it would scale their colliders differently on each
            // axis. Physical sliders are siblings of the canvas, not children.
            var holder = new GameObject("FreePlay Panel");
            holder.transform.SetParent(mount, false);

            var canvasGO = new GameObject("FreePlay Panel (World Space)", typeof(Canvas), typeof(GraphicRaycaster));
            canvasGO.transform.SetParent(holder.transform, false);
            canvasGO.transform.localPosition = _physical ? vrLocalPosition : localPosition;
            canvasGO.transform.localEulerAngles = _physical ? vrLocalEulerAngles : localEulerAngles;

            var canvas = canvasGO.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = participantCamera;

            var crt = canvasGO.GetComponent<RectTransform>();
            crt.sizeDelta = new Vector2(PixelWidth, PixelHeight);
            float scaleX = sizeMeters.x / PixelWidth, scaleY = sizeMeters.y / PixelHeight;
            canvasGO.transform.localScale = new Vector3(scaleX, scaleY, (scaleX + scaleY) * 0.5f);

            // Panel plate. Rounded via Unity's own UISprite so it reads as a
            // designed surface rather than a raw quad — the same trick the
            // slider uses, and the reason none of this needs imported art.
            var bg = NewImage(canvasGO.transform, new Color(0.055f, 0.065f, 0.085f, 0.94f));
            Stretch(bg.rectTransform);
            var bgSprite = Resources.GetBuiltinResource<Sprite>("UI/Skin/UISprite.psd");
            if (bgSprite != null) { bg.sprite = bgSprite; bg.type = Image.Type.Sliced; }

            // Accent rule under the title — the one bit of colour that tells
            // you which surface you are looking at without reading it.
            var rule = NewImage(canvasGO.transform, new Color(0.24f, 0.83f, 0.76f, 0.85f));
            rule.rectTransform.anchoredPosition = new Vector2(28, -74);
            rule.rectTransform.sizeDelta = new Vector2(PixelWidth - 56, 3);

            var title = Txt(canvasGO.transform, "DRIVING STYLE", 26,
                new Color(0.92f, 0.95f, 1f), new Vector2(28, -22), new Vector2(PixelWidth - 56, 36));
            title.fontStyle = FontStyle.Bold;

            const float RowH = 132f;
            const float RowTop = -100f;

            for (int i = 0; i < ParamKeys.Length; i++)
            {
                float y = RowTop - i * RowH;
                int idx = i; // capture per-iteration, not the loop variable

                Txt(canvasGO.transform, ParamLabels[i].ToUpperInvariant(), 19,
                    new Color(0.74f, 0.79f, 0.88f), new Vector2(28, y), new Vector2(PixelWidth - 160, 26));

                _values[i] = Txt(canvasGO.transform, "0.50", 22,
                    new Color(0.24f, 0.83f, 0.76f), new Vector2(PixelWidth - 118, y - 2),
                    new Vector2(90, 28));
                _values[i].alignment = TextAnchor.UpperRight;

                // The interactive row is deliberately TALLER than the visible
                // track. Hand tracking has centimetres of jitter, and a target
                // only as tall as an 14 px bar would be missed constantly.
                var rowGO = new GameObject($"Row_{ParamLabels[i]}", typeof(RectTransform));
                rowGO.transform.SetParent(canvasGO.transform, false);
                var row = rowGO.GetComponent<RectTransform>();
                row.anchorMin = row.anchorMax = new Vector2(0, 1);
                row.pivot = new Vector2(0, 1);
                row.anchoredPosition = new Vector2(28, y - 38);
                row.sizeDelta = new Vector2(PixelWidth - 56, 88);

                var slider = rowGO.AddComponent<VR.VrTouchSlider>();
                slider.Build(row, _font);
                slider.BindValueLabel(_values[i]);
                slider.onValueChanged.AddListener(val => session?.SetFreePlayParameter(ParamKeys[idx], val));
                _touchSliders[i] = slider;
            }

            _panelRoot = holder;
            _panelRoot.SetActive(false); // hidden until Phase.FreePlay — canvas AND sliders
        }

        private Image NewImage(Transform parent, Color c)
        {
            var go = new GameObject("Img", typeof(Image));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.color = c;
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0, 1); rt.pivot = new Vector2(0, 1);
            return img;
        }

        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = rt.offsetMax = Vector2.zero;
        }

        private Text Txt(Transform parent, string content, int size, Color color, Vector2 pos, Vector2 sizeDelta)
        {
            var go = new GameObject("Text", typeof(Text));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<Text>();
            t.text = content; t.font = _font; t.fontSize = size; t.color = color;
            t.alignment = TextAnchor.UpperLeft;
            t.horizontalOverflow = HorizontalWrapMode.Overflow; t.verticalOverflow = VerticalWrapMode.Overflow;
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0, 1); rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = pos; rt.sizeDelta = sizeDelta;
            return t;
        }
    }
}
