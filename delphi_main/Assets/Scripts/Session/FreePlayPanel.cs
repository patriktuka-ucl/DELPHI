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
        public Vector3 localPosition = new Vector3(0f, -0.15f, 0.5f);
        public Vector3 localEulerAngles = Vector3.zero;
        public Vector2 sizeMeters = new Vector2(0.5f, 0.44f);

        private const int PixelWidth = 640;
        private const int PixelHeight = 560;

        private static readonly string[] ParamLabels =
            { "Acceleration", "Braking", "Follow distance", "Cornering speed", "Takeover chance", "Speed vs. limit" };
        private static readonly string[] ParamKeys =
            { "accelerationJerk", "brakingJerk", "followDistance", "corneringSpeed", "takeoverProbability", "speedBelowLimit" };

        private Font _font;
        private GameObject _panelRoot; // toggled on/off with Phase.FreePlay; this component's own GameObject stays active so Update() keeps polling
        private readonly Slider[] _sliders = new Slider[6];
        private readonly Text[] _values = new Text[6];

        private void Awake()
        {
            if (session == null) session = FindFirstObjectByType<SessionController>();
            if (participantCamera == null)
            {
                foreach (var cam in Camera.allCameras)
                    if (cam.targetDisplay == 0) { participantCamera = cam; break; }
            }

            var mount = anchorOverride != null ? anchorOverride : participantCamera != null ? participantCamera.transform : null;
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
                          p.corneringSpeed, p.takeoverProbability, p.speedBelowLimit };
            for (int i = 0; i < 6; i++)
            {
                _sliders[i].SetValueWithoutNotify(v[i]);
                _values[i].text = v[i].ToString("F2");
            }
        }

        // ── UI construction ─────────────────────────────────────────────
        private void BuildUI(Transform mount)
        {
            var canvasGO = new GameObject("FreePlay Panel (World Space)", typeof(Canvas), typeof(GraphicRaycaster));
            canvasGO.transform.SetParent(mount, false);
            canvasGO.transform.localPosition = localPosition;
            canvasGO.transform.localEulerAngles = localEulerAngles;

            var canvas = canvasGO.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = participantCamera;

            var crt = canvasGO.GetComponent<RectTransform>();
            crt.sizeDelta = new Vector2(PixelWidth, PixelHeight);
            float scaleX = sizeMeters.x / PixelWidth, scaleY = sizeMeters.y / PixelHeight;
            canvasGO.transform.localScale = new Vector3(scaleX, scaleY, (scaleX + scaleY) * 0.5f);

            var bg = NewImage(canvasGO.transform, new Color(0.05f, 0.06f, 0.09f, 0.88f));
            Stretch(bg.rectTransform);

            var title = Txt(canvasGO.transform, "Adjust the driving style", 26,
                new Color(0.90f, 0.93f, 0.99f), new Vector2(24, -18), new Vector2(PixelWidth - 48, 34));
            title.fontStyle = FontStyle.Bold;

            for (int i = 0; i < 6; i++)
            {
                float y = -72 - i * 80;
                Txt(canvasGO.transform, ParamLabels[i], 20, new Color(0.82f, 0.86f, 0.94f),
                    new Vector2(24, y), new Vector2(PixelWidth - 48, 28));

                _sliders[i] = BuildSlider(canvasGO.transform, new Vector2(24, y - 34), new Vector2(PixelWidth - 120, 30));
                int idx = i; // capture per-iteration, not the loop variable
                _sliders[i].onValueChanged.AddListener(val => session?.SetFreePlayParameter(ParamKeys[idx], val));

                _values[i] = Txt(canvasGO.transform, "0.50", 20, new Color(0.82f, 0.86f, 0.94f),
                    new Vector2(PixelWidth - 86, y - 32), new Vector2(62, 28));
            }

            _panelRoot = canvasGO;
            _panelRoot.SetActive(false); // hidden until Phase.FreePlay
        }

        private Slider BuildSlider(Transform parent, Vector2 pos, Vector2 size)
        {
            var go = new GameObject("Slider", typeof(RectTransform), typeof(Slider));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0, 1); rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = pos; rt.sizeDelta = size;

            var bg = NewImage(go.transform, new Color(0.20f, 0.22f, 0.28f, 1f));
            Stretch(bg.rectTransform);

            var fillArea = new GameObject("Fill Area", typeof(RectTransform));
            fillArea.transform.SetParent(go.transform, false);
            var fart = fillArea.GetComponent<RectTransform>();
            fart.anchorMin = Vector2.zero; fart.anchorMax = Vector2.one; fart.offsetMin = fart.offsetMax = Vector2.zero;
            var fill = NewImage(fillArea.transform, new Color(0.30f, 0.75f, 0.55f, 1f));
            var frt = fill.rectTransform;
            frt.anchorMin = Vector2.zero; frt.anchorMax = Vector2.one; frt.offsetMin = frt.offsetMax = Vector2.zero;

            var handleArea = new GameObject("Handle Area", typeof(RectTransform));
            handleArea.transform.SetParent(go.transform, false);
            var hart = handleArea.GetComponent<RectTransform>();
            hart.anchorMin = Vector2.zero; hart.anchorMax = Vector2.one; hart.offsetMin = new Vector2(16, 0); hart.offsetMax = new Vector2(-16, 0);
            var handle = NewImage(handleArea.transform, Color.white);
            var hrt = handle.rectTransform; hrt.sizeDelta = new Vector2(32, 0);

            var s = go.GetComponent<Slider>();
            s.fillRect = frt; s.handleRect = hrt; s.targetGraphic = handle;
            s.minValue = 0f; s.maxValue = 1f; s.direction = Slider.Direction.LeftToRight;
            return s;
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
