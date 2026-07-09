using System;
using UnityEngine;
using UnityEngine.UI;

namespace Delphi.Trial
{
    /// <summary>
    /// Small trial-control panel for the researcher dashboard, built
    /// programmatically in the same style as SessionControlsUI: a START
    /// TRIAL / ABORT button plus live status (phase, iteration i/N,
    /// countdown, hypervolume coverage), and a read-only meter for each of
    /// the six driving parameters — non-interactable sliders that just
    /// reflect carDriver.parameters every frame, so it's visually obvious
    /// the optimizer is actually driving the car (values move each
    /// iteration) rather than only trusting the console log. Anchored
    /// top-right of the dashboard display.
    /// </summary>
    public class TrialControlsUI : MonoBehaviour
    {
        [Header("Links (auto-found if left empty)")]
        public TrialManager trial;

        [Header("Display")]
        [Tooltip("Must match DashboardUI.dashboardDisplay.")]
        public int dashboardDisplay = 1;

        private readonly Color _panelColor  = new Color(0.10f, 0.12f, 0.17f, 1f);
        private readonly Color _buttonColor = new Color(0.16f, 0.19f, 0.26f, 1f);
        private readonly Color _accent      = new Color32(70, 220, 160, 255);
        private readonly Color _running     = new Color32(80, 160, 235, 255);
        private readonly Color _error       = new Color(0.85f, 0.25f, 0.25f);
        private readonly Color _warn        = new Color(0.85f, 0.75f, 0.25f);
        private readonly Color _dim         = new Color(0.55f, 0.58f, 0.65f);
        private readonly Color _notAttached = new Color(0.45f, 0.45f, 0.45f);

        private Font _font;
        private Text _buttonText, _stateLine, _iterationLine, _detailLine, _optimizerLabel, _hvText;
        private Image _buttonImage, _optimizerDot, _hvBox;

        // Driving-parameter meters — order matches CarDriver.DrivingParameters.
        private static readonly string[] ParamLabels =
        {
            "Accel jerk", "Braking jerk", "Follow distance",
            "Cornering speed", "Takeover prob.", "Speed below limit"
        };
        private Slider[] _paramSliders;
        private Text[] _paramValueTexts;

        private void Start()
        {
            if (trial == null) trial = FindFirstObjectByType<TrialManager>();
            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            BuildUI();
        }

        private void ToggleTrial()
        {
            if (trial == null) return;
            if (trial.State == TrialManager.TrialState.Idle ||
                trial.State == TrialManager.TrialState.Finished ||
                trial.State == TrialManager.TrialState.Error)
                trial.StartTrial();
            else
                trial.AbortTrial();
        }

        private void Update()
        {
            if (trial == null) return;
            var s = trial.State;
            bool running = s != TrialManager.TrialState.Idle &&
                           s != TrialManager.TrialState.Finished &&
                           s != TrialManager.TrialState.Error;

            _buttonText.text = running ? "ABORT" : "START TRIAL";
            _buttonImage.color = running ? _error : _buttonColor;

            _stateLine.text = s.ToString().ToUpperInvariant();
            _stateLine.color = s switch
            {
                TrialManager.TrialState.Error    => _error,
                TrialManager.TrialState.Finished => _accent,
                TrialManager.TrialState.Idle     => _dim,
                _                                => _running
            };

            // Always-visible trial counter — separate from the status line
            // so it doesn't get lost inside a longer sentence.
            _iterationLine.text = s == TrialManager.TrialState.Idle
                ? "—"
                : $"Trial {trial.Iteration} / {trial.TotalIterations}";
            _iterationLine.color = s == TrialManager.TrialState.Finished ? _accent : _dim;

            double remain = trial.PhaseSecondsRemaining;
            string countdown = remain > 0 ? $"  {TimeSpan.FromSeconds(remain):mm\\:ss}" : "";
            _detailLine.text = $"{trial.StatusLine}{countdown}";
            _detailLine.color = s == TrialManager.TrialState.Error ? _error : _dim;

            // Hypervolume coverage — its own boxed line, not buried in the
            // status sentence.
            bool haveHv = !float.IsNaN(trial.LastCoverage);
            _hvText.text = haveHv ? $"HV (hypervolume)   {trial.LastCoverage:F3}" : "HV (hypervolume)   —";
            _hvText.color = haveHv ? _accent : _dim;

            // Optimizer connection indicator — dot + label, same visual
            // language as the DelphiManager inspector's status dots.
            var (dotColor, label) = trial.Optimizer switch
            {
                TrialManager.OptimizerStatus.Connected    => (_accent, "Optimizer: connected"),
                TrialManager.OptimizerStatus.Starting     => (_warn, "Optimizer: starting…"),
                TrialManager.OptimizerStatus.Disconnected => (_error, "Optimizer: disconnected"),
                _                                         => (_notAttached, "Optimizer: not started")
            };
            _optimizerDot.color = dotColor;
            _optimizerLabel.text = label;
            _optimizerLabel.color = dotColor;

            RefreshParamMeters();
        }

        private void RefreshParamMeters()
        {
            if (trial.carDriver == null) return;
            var p = trial.carDriver.parameters;
            float[] values = { p.accelerationJerk, p.brakingJerk, p.followDistance,
                              p.corneringSpeed, p.takeoverProbability, p.speedBelowLimit };
            for (int i = 0; i < _paramSliders.Length; i++)
            {
                _paramSliders[i].SetValueWithoutNotify(values[i]);
                _paramValueTexts[i].text = values[i].ToString("F2");
            }
        }

        // ── UI construction ─────────────────────────────────────────────
        private void BuildUI()
        {
            var canvasGO = new GameObject("Trial Controls Canvas",
                typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGO.transform.SetParent(transform, false);
            var canvas = canvasGO.GetComponent<Canvas>();
            canvas.renderMode    = RenderMode.ScreenSpaceOverlay;
            canvas.targetDisplay = dashboardDisplay;
            canvas.sortingOrder  = 11; // above dashboard + transport bar
            var scaler = canvasGO.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;

            // Panel, top-right.
            var panel = new GameObject("Trial Panel", typeof(Image));
            panel.transform.SetParent(canvasGO.transform, false);
            panel.GetComponent<Image>().color = _panelColor;
            var prt = panel.GetComponent<RectTransform>();
            prt.anchorMin = prt.anchorMax = new Vector2(1f, 1f);
            prt.pivot = new Vector2(1f, 1f);
            prt.anchoredPosition = new Vector2(-20, -20);
            prt.sizeDelta = new Vector2(460, 194);

            (_buttonImage, _buttonText) = CreateButton(panel.transform,
                "START TRIAL", new Vector2(14, -14), new Vector2(140, 64), ToggleTrial);

            _stateLine = CreateText(panel.transform, "IDLE", 18, _dim,
                new Vector2(168, -14), new Vector2(278, 24));
            _iterationLine = CreateText(panel.transform, "—", 16, _dim,
                new Vector2(168, -40), new Vector2(278, 22));
            _detailLine = CreateText(panel.transform, "", 14, _dim,
                new Vector2(168, -64), new Vector2(278, 22));

            // HV — its own boxed row, separate from the status line.
            var hvGO = new GameObject("HV Box", typeof(Image));
            hvGO.transform.SetParent(panel.transform, false);
            _hvBox = hvGO.GetComponent<Image>();
            _hvBox.color = new Color(0.06f, 0.07f, 0.10f, 1f);
            var hvRt = hvGO.GetComponent<RectTransform>();
            hvRt.anchorMin = hvRt.anchorMax = new Vector2(0, 1);
            hvRt.pivot = new Vector2(0, 1);
            hvRt.anchoredPosition = new Vector2(14, -92);
            hvRt.sizeDelta = new Vector2(432, 28);
            _hvText = CreateText(hvGO.transform, "HV (hypervolume)   —", 15, _dim,
                Vector2.zero, Vector2.zero);
            _hvText.alignment = TextAnchor.MiddleLeft;
            _hvText.fontStyle = FontStyle.Bold;
            var hvTextRt = _hvText.rectTransform;
            hvTextRt.anchorMin = Vector2.zero; hvTextRt.anchorMax = Vector2.one;
            hvTextRt.offsetMin = new Vector2(10, 0); hvTextRt.offsetMax = new Vector2(-10, 0);

            _optimizerDot = CreateDot(panel.transform, new Vector2(14, -132), _notAttached);
            _optimizerLabel = CreateText(panel.transform, "Optimizer: not started", 14, _notAttached,
                new Vector2(34, -128), new Vector2(412, 22));

            BuildParamMeters(canvasGO.transform, prt.sizeDelta.y);
        }

        // Read-only meter panel, directly below the Trial Panel — one row
        // per driving parameter: label, a disabled (drag-proof) Slider, and
        // its numeric value, refreshed every frame from carDriver.parameters.
        private void BuildParamMeters(Transform canvasParent, float trialPanelHeight)
        {
            const int rows = 6;
            const float rowHeight = 30f;
            float panelHeight = 16 + rows * rowHeight;

            var panel = new GameObject("Trial Params Panel", typeof(Image));
            panel.transform.SetParent(canvasParent, false);
            panel.GetComponent<Image>().color = _panelColor;
            var prt = panel.GetComponent<RectTransform>();
            prt.anchorMin = prt.anchorMax = new Vector2(1f, 1f);
            prt.pivot = new Vector2(1f, 1f);
            prt.anchoredPosition = new Vector2(-20, -20 - trialPanelHeight - 10);
            prt.sizeDelta = new Vector2(460, panelHeight);

            _paramSliders = new Slider[ParamLabels.Length];
            _paramValueTexts = new Text[ParamLabels.Length];
            for (int i = 0; i < ParamLabels.Length; i++)
            {
                float y = -8 - i * rowHeight;
                CreateText(panel.transform, ParamLabels[i], 13, _dim,
                    new Vector2(14, y), new Vector2(126, 24));
                _paramSliders[i] = CreateReadOnlySlider(panel.transform,
                    new Vector2(146, y - 3), new Vector2(250, 18));
                _paramValueTexts[i] = CreateText(panel.transform, "0.50", 13, _dim,
                    new Vector2(404, y), new Vector2(42, 24));
            }
        }

        private Slider CreateReadOnlySlider(Transform parent, Vector2 pos, Vector2 size)
        {
            var go = new GameObject("Meter", typeof(RectTransform), typeof(Image), typeof(Slider));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            // Root Image doubles as the slider's background AND its
            // targetGraphic — Selectable expects a non-null targetGraphic
            // even when interactable is false.
            var rootImg = go.GetComponent<Image>();
            rootImg.color = new Color(0.06f, 0.07f, 0.10f, 1f);
            // Never a raycast target: it's a display-only meter, must not
            // intercept clicks meant for whatever's underneath/around it.
            rootImg.raycastTarget = false;

            var fillArea = new GameObject("Fill Area", typeof(RectTransform));
            fillArea.transform.SetParent(go.transform, false);
            var fart = fillArea.GetComponent<RectTransform>();
            fart.anchorMin = Vector2.zero; fart.anchorMax = Vector2.one;
            fart.offsetMin = new Vector2(2, 0); fart.offsetMax = new Vector2(-2, 0);

            var fill = new GameObject("Fill", typeof(Image));
            fill.transform.SetParent(fillArea.transform, false);
            var fillImg = fill.GetComponent<Image>();
            fillImg.color = _running;
            fillImg.raycastTarget = false;
            var frt = fill.GetComponent<RectTransform>();
            frt.anchorMin = Vector2.zero; frt.anchorMax = Vector2.one;
            frt.offsetMin = frt.offsetMax = Vector2.zero;

            // A REAL handle, matching the pattern already proven to work in
            // SessionControlsUI's scrubber — a null handleRect on a Slider
            // is a deviation the EventSystem's per-frame processing (hover/
            // navigation-focus handling) can choke on, which can silently
            // break click processing for every OTHER button on screen too,
            // since that processing is one shared pass across all canvases.
            // Shrunk to zero width so it's visually invisible without being
            // structurally absent.
            var handle = new GameObject("Handle", typeof(Image));
            handle.transform.SetParent(go.transform, false);
            var handleImg = handle.GetComponent<Image>();
            handleImg.color = Color.clear;
            handleImg.raycastTarget = false;
            var hrt = handle.GetComponent<RectTransform>();
            hrt.anchorMin = new Vector2(0, 0); hrt.anchorMax = new Vector2(0, 1);
            hrt.sizeDelta = new Vector2(0, 0);

            var slider = go.GetComponent<Slider>();
            slider.targetGraphic = rootImg;
            slider.fillRect = frt;
            slider.handleRect = hrt;
            slider.minValue = 0f;
            slider.maxValue = 1f;
            slider.direction = Slider.Direction.LeftToRight;
            slider.interactable = false; // read-only: reflects the optimizer's choice, not user input
            slider.transition = Selectable.Transition.None;
            // Never selectable via Tab/arrow-key UI navigation — it's a
            // pure display widget, not a control.
            slider.navigation = new Navigation { mode = Navigation.Mode.None };
            return slider;
        }

        private Image CreateDot(Transform parent, Vector2 pos, Color color)
        {
            var go = new GameObject("Dot", typeof(Image));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.color = color;
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = pos;
            rt.sizeDelta = new Vector2(12, 12);
            return img;
        }

        private (Image, Text) CreateButton(Transform parent, string label,
                                           Vector2 pos, Vector2 size, Action onClick)
        {
            var go = new GameObject($"Button {label}", typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.color = _buttonColor;
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;

            var btn = go.GetComponent<Button>();
            btn.targetGraphic = img;
            btn.onClick.AddListener(() => onClick());

            var text = CreateText(go.transform, label, 16, new Color(0.85f, 0.9f, 1f),
                Vector2.zero, size);
            text.alignment = TextAnchor.MiddleCenter;
            var trt = text.rectTransform;
            trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
            trt.offsetMin = trt.offsetMax = Vector2.zero;
            return (img, text);
        }

        private Text CreateText(Transform parent, string content, int size, Color color,
                                Vector2 anchoredPos, Vector2 sizeDelta)
        {
            var go = new GameObject("Text", typeof(Text));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<Text>();
            t.text = content; t.font = _font; t.fontSize = size;
            t.alignment = TextAnchor.UpperLeft; t.color = color;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = anchoredPos;
            rt.sizeDelta = sizeDelta;
            return t;
        }
    }
}
