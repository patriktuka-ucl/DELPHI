using UnityEngine;
using UnityEngine.UI;
using Delphi.Session;

namespace Delphi.VR
{
    /// <summary>
    /// Participant-facing world-space HUD — modern, minimal, Tesla-dash
    /// styled: current speed and time remaining in the drive, nothing else.
    ///
    /// Deliberately NOT VrParticipantHud: that component shows raw sensor
    /// values and is hidden by default on purpose (a participant watching
    /// their own heart rate is no longer a naive observer of their own
    /// arousal — see its own class comment). This HUD shows only what a real
    /// car's dashboard would, so it's visible during a real session without
    /// that concern applying.
    ///
    /// Two faces, mutually exclusive:
    ///   DRIVING  — speed + time-left-in-condition, shown whenever the car
    ///              isn't parked.
    ///   MEDITATION — a slow pulsing/breathing disc, shown only during
    ///              Phase.Meditation (the car is stationary and there is
    ///              nothing meaningful to show on a speedometer).
    /// Both hidden the rest of the time (intro/questionnaire/break/etc. —
    /// administrative phases with no driving context to display).
    /// </summary>
    [DefaultExecutionOrder(150)]
    public class VrDriveHud : MonoBehaviour
    {
        [Header("Links (auto-found if empty)")]
        public SessionController session;

        [Header("Placement (relative to the driver's seat, metres)")]
        [Tooltip("Ahead and slightly below eye height — glanced at like a real dash, never blocking the road.")]
        public Vector3 localPosition = new Vector3(0f, -0.22f, 0.9f);
        public Vector3 localEulerAngles = Vector3.zero;
        public Vector2 sizeMeters = new Vector2(0.26f, 0.16f);

        [Header("Meditation pulse")]
        [Tooltip("One full breathe-in-breathe-out cycle, in seconds.")]
        [Range(2f, 12f)] public float pulseCycleSeconds = 8f;
        [Range(0.5f, 1.5f)] public float pulseMinScale = 0.75f;
        [Range(1f, 2.5f)] public float pulseMaxScale = 1.15f;

        [Header("Colours")]
        public Color accent = new Color32(70, 220, 160, 255);
        public Color dim = new Color32(140, 150, 165, 255);
        public Color background = new Color(0.03f, 0.04f, 0.06f, 0.82f);

        private GameObject _root;
        private GameObject _drivingPanel, _meditationPanel;
        private Text _speedTxt, _speedUnitTxt, _timeTxt;
        private Slider _progressBar;
        private Image _pulseDisc;
        private RectTransform _pulseRt;
        private float _pulsePhase;

        private void Start()
        {
            if (session == null) session = FindAnyObjectByType<SessionController>();

            var rig = VrRig.Instance;
            if (rig == null || !rig.IsActive || rig.SeatReference == null)
            {
                Debug.Log("[VrDriveHud] No active VR rig — drive HUD not built (normal on the desktop path).", this);
                enabled = false;
                return;
            }

            Build(rig.SeatReference);
        }

        private void Update()
        {
            if (_root == null || session == null) return;

            bool medPhase = session.CurrentPhase == SessionController.Phase.Meditation;
            bool driving = !medPhase && session.carDriver != null && !session.carDriver.IsParked;

            _drivingPanel.SetActive(driving);
            _meditationPanel.SetActive(medPhase);

            if (driving) RefreshDriving();
            if (medPhase) RefreshMeditationPulse();
        }

        // ── Build ────────────────────────────────────────────────────────
        private void Build(Transform seat)
        {
            _root = new GameObject("[VR] Drive HUD");
            _root.transform.SetParent(seat, false);
            _root.transform.localPosition = localPosition;
            _root.transform.localEulerAngles = localEulerAngles;

            const int pixelW = 420, pixelH = 260;
            var canvasGO = new GameObject("Canvas", typeof(Canvas));
            canvasGO.transform.SetParent(_root.transform, false);
            var canvas = canvasGO.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = VrRig.Instance.participantCamera;

            var crt = canvasGO.GetComponent<RectTransform>();
            crt.sizeDelta = new Vector2(pixelW, pixelH);
            canvasGO.transform.localScale = new Vector3(sizeMeters.x / pixelW, sizeMeters.y / pixelH, 1f);

            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            _drivingPanel = BuildDrivingPanel(canvasGO.transform, font, pixelW, pixelH);
            _meditationPanel = BuildMeditationPanel(canvasGO.transform, pixelW, pixelH);
            _drivingPanel.SetActive(false);
            _meditationPanel.SetActive(false);
        }

        private GameObject BuildDrivingPanel(Transform parent, Font font, int w, int h)
        {
            var panel = new GameObject("Driving");
            panel.transform.SetParent(parent, false);

            var bg = Panel(panel.transform, background, w, h);

            _speedTxt = MakeText(bg.transform, font, 92, accent, TextAnchor.MiddleCenter,
                new Vector2(0, 24), new Vector2(w - 32, 96));
            _speedUnitTxt = MakeText(bg.transform, font, 20, dim, TextAnchor.MiddleCenter,
                new Vector2(0, -34), new Vector2(w - 32, 26));
            _speedUnitTxt.text = "KM/H";

            _timeTxt = MakeText(bg.transform, font, 22, Color.white, TextAnchor.MiddleCenter,
                new Vector2(0, -70), new Vector2(w - 32, 30));

            var barGO = new GameObject("Progress", typeof(RectTransform), typeof(Image), typeof(Slider));
            barGO.transform.SetParent(bg.transform, false);
            var brt = barGO.GetComponent<RectTransform>();
            brt.anchorMin = new Vector2(0.08f, 0f); brt.anchorMax = new Vector2(0.92f, 0f);
            brt.pivot = new Vector2(0.5f, 0f);
            brt.anchoredPosition = new Vector2(0, 18);
            brt.sizeDelta = new Vector2(0, 8);
            var bimg = barGO.GetComponent<Image>(); bimg.color = new Color(1, 1, 1, 0.08f); bimg.raycastTarget = false;
            var fa = new GameObject("Fill Area", typeof(RectTransform)); fa.transform.SetParent(barGO.transform, false);
            var fart = fa.GetComponent<RectTransform>(); fart.anchorMin = Vector2.zero; fart.anchorMax = Vector2.one; fart.offsetMin = fart.offsetMax = Vector2.zero;
            var fill = new GameObject("Fill", typeof(Image)); fill.transform.SetParent(fa.transform, false);
            var fimg = fill.GetComponent<Image>(); fimg.color = accent; fimg.raycastTarget = false;
            var frt = fill.GetComponent<RectTransform>(); frt.anchorMin = Vector2.zero; frt.anchorMax = Vector2.one; frt.offsetMin = frt.offsetMax = Vector2.zero;
            var handle = new GameObject("Handle", typeof(Image)); handle.transform.SetParent(barGO.transform, false);
            handle.GetComponent<Image>().color = Color.clear;
            var hrt = handle.GetComponent<RectTransform>(); hrt.sizeDelta = Vector2.zero;
            _progressBar = barGO.GetComponent<Slider>();
            _progressBar.targetGraphic = bimg; _progressBar.fillRect = frt; _progressBar.handleRect = hrt;
            _progressBar.minValue = 0; _progressBar.maxValue = 1; _progressBar.interactable = false;
            _progressBar.transition = Selectable.Transition.None;
            _progressBar.navigation = new Navigation { mode = Navigation.Mode.None };

            return panel;
        }

        private GameObject BuildMeditationPanel(Transform parent, int w, int h)
        {
            var panel = new GameObject("Meditation");
            panel.transform.SetParent(parent, false);
            Panel(panel.transform, background, w, h);

            var discGO = new GameObject("Pulse", typeof(Image));
            discGO.transform.SetParent(panel.transform, false);
            _pulseDisc = discGO.GetComponent<Image>();
            _pulseDisc.sprite = MakeRadialSprite();
            _pulseDisc.color = accent;
            _pulseDisc.raycastTarget = false;
            _pulseRt = discGO.GetComponent<RectTransform>();
            _pulseRt.anchorMin = _pulseRt.anchorMax = new Vector2(0.5f, 0.5f);
            _pulseRt.anchoredPosition = Vector2.zero;
            float discSize = Mathf.Min(w, h) * 0.55f;
            _pulseRt.sizeDelta = new Vector2(discSize, discSize);

            return panel;
        }

        private GameObject Panel(Transform parent, Color color, int w, int h)
        {
            var go = new GameObject("BG", typeof(Image));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.color = color;
            img.raycastTarget = false;
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            return go;
        }

        private static Text MakeText(Transform parent, Font font, int size, Color color, TextAnchor anchor,
                                      Vector2 pos, Vector2 sizeDelta)
        {
            var go = new GameObject("Text", typeof(Text));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<Text>();
            t.font = font; t.fontSize = size; t.color = color; t.alignment = anchor; t.fontStyle = FontStyle.Bold;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.raycastTarget = false;
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos; rt.sizeDelta = sizeDelta;
            return t;
        }

        /// <summary>A soft radial disc — white centre fading to transparent at
        /// the edge — generated once so the meditation pulse doesn't need a
        /// shipped texture asset. Tinted by Image.color at draw time.</summary>
        private static Sprite MakeRadialSprite()
        {
            const int size = 128;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
            var px = new Color32[size * size];
            Vector2 c = new Vector2(size / 2f, size / 2f);
            float maxDist = size / 2f;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), c) / maxDist;
                float a = 1f - Mathf.SmoothStep(0f, 1f, d);
                px[y * size + x] = new Color(1f, 1f, 1f, a);
            }
            tex.SetPixels32(px);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
        }

        // ── Refresh ──────────────────────────────────────────────────────
        private void RefreshDriving()
        {
            var car = session.carDriver;
            _speedTxt.text = car != null ? Mathf.RoundToInt(car.CurrentSpeedKmh).ToString() : "—";

            float remaining = session.CurrentConditionSecondsRemaining();
            if (session.IsRunningCondition && remaining > 0f)
            {
                int total = Mathf.CeilToInt(remaining);
                _timeTxt.text = $"{total / 60}:{total % 60:00} remaining";
                _progressBar.gameObject.SetActive(true);
                _progressBar.SetValueWithoutNotify(session.CurrentConditionProgress());
            }
            else
            {
                _timeTxt.text = session.CurrentPhase == SessionController.Phase.FreePlay ? "exploring" : "";
                _progressBar.gameObject.SetActive(false);
            }
        }

        private void RefreshMeditationPulse()
        {
            _pulsePhase += Time.deltaTime / Mathf.Max(0.5f, pulseCycleSeconds);
            float breathe = (Mathf.Sin(_pulsePhase * Mathf.PI * 2f) + 1f) * 0.5f; // 0..1
            float scale = Mathf.Lerp(pulseMinScale, pulseMaxScale, breathe);
            _pulseRt.localScale = new Vector3(scale, scale, 1f);
            _pulseDisc.color = new Color(accent.r, accent.g, accent.b, Mathf.Lerp(0.35f, 0.75f, breathe));
        }
    }
}
