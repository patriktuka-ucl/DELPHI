using System;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;

namespace Delphi
{
    /// <summary>
    /// Record/playback transport bar for the researcher dashboard, built
    /// programmatically (same style as DashboardUI) on its own canvas at the
    /// bottom of the dashboard display.
    ///
    ///   Row 1 — recording: REC/STOP toggle + elapsed/status.
    ///   Row 2 — session browser + transport: prev/next session, Load/Eject,
    ///           Play/Pause, frame-step −1/+1, speed cycle, time readout.
    ///   Row 3 — scrubber (drag anywhere in the session).
    ///
    /// Keyboard (only while a session is loaded, except R):
    ///   R = record toggle, Space = play/pause, ←/→ = step one frame,
    ///   ↑/↓ = cycle playback speed.
    /// </summary>
    public class SessionControlsUI : MonoBehaviour
    {
        [Header("Links (auto-found if left empty)")]
        public SessionRecorder recorder;
        public SessionPlayer player;
        [Tooltip("Auto-found if left empty. Its display camera and UI layer " +
                 "are reused here so this bar renders on the same display and " +
                 "gets swept up by the DashboardDisplay capture feed together " +
                 "with the dashboard.")]
        public DashboardUI dashboard;

        [Header("Display (used only if no DashboardUI is found)")]
        public int dashboardDisplay = 1;

        [Header("Position")]
        [Tooltip("Rect-transform anchors for the transport bar panel. Default " +
                 "is bottom-center, which stays bottom-middle regardless of " +
                 "aspect ratio or screen resolution.")]
        public Vector2 anchorMin = new Vector2(0.5f, 0f);
        public Vector2 anchorMax = new Vector2(0.5f, 0f);
        [Tooltip("Pivot the panel is positioned/sized relative to its anchor.")]
        public Vector2 pivot = new Vector2(0.5f, 0f);
        [Tooltip("Offset from the anchor, in canvas reference units (1920x1080).")]
        public Vector2 anchoredPosition = new Vector2(0f, 20f);
        [Tooltip("Panel size in canvas reference units.")]
        public Vector2 panelSize = new Vector2(1180f, 150f);

        private static readonly float[] Speeds = { 0.25f, 0.5f, 1f, 2f, 4f };

        private readonly Color _bgColor     = new Color(0.06f, 0.07f, 0.10f, 1f);
        private readonly Color _panelColor  = new Color(0.10f, 0.12f, 0.17f, 1f);
        private readonly Color _buttonColor = new Color(0.16f, 0.19f, 0.26f, 1f);
        private readonly Color _accent      = new Color32(70, 220, 160, 255);
        private readonly Color _recRed      = new Color(0.85f, 0.2f, 0.2f);
        private readonly Color _dim         = new Color(0.55f, 0.58f, 0.65f);

        private Font _font;
        private Text _recButtonText, _recStatus;
        private Image _recButtonImage;
        private Text _sessionLabel, _playButtonText, _speedButtonText, _timeLabel;
        private Button _loadButton, _playButton, _stepBackButton, _stepFwdButton, _speedButton;
        private Text _loadButtonText;
        private Slider _scrubber;
        private bool _scrubbing;

        private string[] _sessions = Array.Empty<string>();
        private int _sessionIdx;
        private int _speedIdx = 2; // 1×
        private bool _wasRecording;

        private void Start()
        {
            if (recorder == null)  recorder  = FindFirstObjectByType<SessionRecorder>();
            if (player == null)    player    = FindFirstObjectByType<SessionPlayer>();
            if (dashboard == null) dashboard = FindFirstObjectByType<DashboardUI>();
            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            BuildUI();
            RefreshSessions();
        }

        private void RefreshSessions()
        {
            string root = recorder != null ? recorder.SessionsRoot
                                           : SessionRecorder.DefaultSessionsRoot;
            _sessions = SessionPlayer.ListSessions(root);
            _sessionIdx = Mathf.Clamp(_sessionIdx, 0, Mathf.Max(0, _sessions.Length - 1));
        }

        // ── Actions ─────────────────────────────────────────────────────
        private void ToggleRecord()
        {
            if (recorder == null) return;
            if (recorder.IsRecording) recorder.StopRecording();
            else recorder.StartRecording();
        }

        private void CycleSession(int dir)
        {
            RefreshSessions();
            if (_sessions.Length == 0) return;
            _sessionIdx = (_sessionIdx + dir + _sessions.Length) % _sessions.Length;
        }

        private void ToggleLoad()
        {
            if (player == null) return;
            if (player.IsLoaded) { player.Unload(); return; }
            RefreshSessions();
            if (_sessions.Length == 0) return;
            player.Load(_sessions[_sessionIdx]);
        }

        private void CycleSpeed(int dir)
        {
            if (player == null) return;
            _speedIdx = Mathf.Clamp(_speedIdx + dir, 0, Speeds.Length - 1);
            player.SetSpeed(Speeds[_speedIdx]);
        }

        // ── Per-frame state → widgets ───────────────────────────────────
        private void Update()
        {
            HandleKeyboard();

            // A finished recording should appear in the browser immediately.
            bool rec = recorder != null && recorder.IsRecording;
            if (_wasRecording && !rec) RefreshSessions();
            _wasRecording = rec;

            // Recording row.
            if (recorder != null)
            {
                _recButtonText.text = rec ? "STOP" : "REC";
                _recButtonImage.color = rec ? _recRed : _buttonColor;
                _recStatus.text = rec
                    ? $"RECORDING  {FormatTime(recorder.ElapsedSeconds)}"
                    : $"idle — {_sessions.Length} session(s) on disk";
                _recStatus.color = rec ? _recRed : _dim;
            }

            // Playback row.
            bool loaded = player != null && player.IsLoaded;
            if (player != null)
            {
                if (loaded)
                    _sessionLabel.text = $"{Path.GetFileName(player.LoadedPath)}  [loaded]";
                else
                    _sessionLabel.text = _sessions.Length > 0
                        ? Path.GetFileName(_sessions[_sessionIdx])
                        : "no sessions recorded yet";
                _sessionLabel.color = loaded ? _accent : _dim;

                _loadButtonText.text = loaded ? "Eject" : "Load";
                _playButtonText.text = player.IsPlaying ? "Pause" : "Play";
                _speedButtonText.text = $"{Speeds[_speedIdx]:0.##}x";

                _playButton.interactable     = loaded;
                _stepBackButton.interactable = loaded;
                _stepFwdButton.interactable  = loaded;
                _speedButton.interactable    = loaded;
                _scrubber.interactable       = loaded;

                if (loaded)
                {
                    _timeLabel.text = $"{FormatTime(player.TimeSec)} / {FormatTime(player.Duration)}";
                    if (!_scrubbing && player.Duration > 0f)
                        _scrubber.SetValueWithoutNotify(player.TimeSec / player.Duration);
                }
                else
                {
                    _timeLabel.text = "--:-- / --:--";
                    _scrubber.SetValueWithoutNotify(0f);
                }
            }
        }

        private void HandleKeyboard()
        {
            var kb = Keyboard.current;
            if (kb == null) return;

            if (kb.rKey.wasPressedThisFrame) ToggleRecord();

            if (player == null || !player.IsLoaded) return;
            if (kb.spaceKey.wasPressedThisFrame)      player.TogglePlay();
            if (kb.leftArrowKey.wasPressedThisFrame)  player.StepFrames(-1);
            if (kb.rightArrowKey.wasPressedThisFrame) player.StepFrames(1);
            if (kb.upArrowKey.wasPressedThisFrame)    CycleSpeed(1);
            if (kb.downArrowKey.wasPressedThisFrame)  CycleSpeed(-1);
        }

        private static string FormatTime(float t) =>
            $"{Mathf.FloorToInt(t / 60f):00}:{t % 60f:00.0}";

        // ── UI construction ─────────────────────────────────────────────
        private void BuildUI()
        {
            var canvasGO = new GameObject("Session Controls Canvas",
                typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGO.transform.SetParent(transform, false);
            var canvas = canvasGO.GetComponent<Canvas>();
            if (dashboard != null)
            {
                // Share the dashboard's own camera so this bar renders on the
                // same display AND gets picked up by its display-capture feed.
                canvas.renderMode    = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera   = dashboard.DisplayCamera;
                canvas.planeDistance = 0.5f; // in front of the dashboard's own canvas (1f)
            }
            else
            {
                canvas.renderMode    = RenderMode.ScreenSpaceOverlay;
                canvas.targetDisplay = dashboardDisplay;
            }
            canvas.sortingOrder  = 10; // above the dashboard canvas
            var scaler = canvasGO.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;

            // Bar panel, positioned per the Position fields (bottom-center by default).
            var panel = new GameObject("Transport Bar", typeof(Image));
            panel.transform.SetParent(canvasGO.transform, false);
            panel.GetComponent<Image>().color = _panelColor;
            var prt = panel.GetComponent<RectTransform>();
            prt.anchorMin = anchorMin;
            prt.anchorMax = anchorMax;
            prt.pivot = pivot;
            prt.anchoredPosition = anchoredPosition;
            prt.sizeDelta = panelSize;

            // Row 1 — recording.
            (_, _recButtonImage, _recButtonText) =
                CreateButton(panel.transform, "REC", new Vector2(14, -12), new Vector2(90, 34), ToggleRecord);
            _recStatus = CreateText(panel.transform, "idle", 16, TextAnchor.MiddleLeft, _dim,
                new Vector2(118, -12), new Vector2(900, 34));

            // Row 2 — session browser + transport.
            float y = -56;
            CreateButton(panel.transform, "<", new Vector2(14, y), new Vector2(34, 34), () => CycleSession(-1));
            _sessionLabel = CreateText(panel.transform, "…", 16, TextAnchor.MiddleCenter, _dim,
                new Vector2(52, y), new Vector2(280, 34));
            CreateButton(panel.transform, ">", new Vector2(336, y), new Vector2(34, 34), () => CycleSession(1));
            (_loadButton, _, _loadButtonText) =
                CreateButton(panel.transform, "Load", new Vector2(378, y), new Vector2(80, 34), ToggleLoad);

            (_playButton, _, _playButtonText) =
                CreateButton(panel.transform, "Play", new Vector2(482, y), new Vector2(80, 34),
                             () => player?.TogglePlay());
            (_stepBackButton, _, _) =
                CreateButton(panel.transform, "-1f", new Vector2(566, y), new Vector2(52, 34),
                             () => player?.StepFrames(-1));
            (_stepFwdButton, _, _) =
                CreateButton(panel.transform, "+1f", new Vector2(622, y), new Vector2(52, 34),
                             () => player?.StepFrames(1));
            (_speedButton, _, _speedButtonText) =
                CreateButton(panel.transform, "1x", new Vector2(678, y), new Vector2(60, 34),
                             () => CycleSpeed(_speedIdx >= Speeds.Length - 1 ? -(Speeds.Length - 1) : 1));
            _timeLabel = CreateText(panel.transform, "--:-- / --:--", 16, TextAnchor.MiddleLeft, _dim,
                new Vector2(756, y), new Vector2(260, 34));

            // Row 3 — scrubber.
            _scrubber = CreateSlider(panel.transform, new Vector2(14, -104), new Vector2(1152, 26));
            _scrubber.onValueChanged.AddListener(v =>
            {
                if (player != null && player.IsLoaded)
                {
                    _scrubbing = true;
                    player.Seek(v * player.Duration);
                    _scrubbing = false;
                }
            });

            if (dashboard != null) SetLayerRecursively(canvasGO.transform, dashboard.UiLayer);
        }

        private static void SetLayerRecursively(Transform t, int layer)
        {
            t.gameObject.layer = layer;
            for (int i = 0; i < t.childCount; i++)
                SetLayerRecursively(t.GetChild(i), layer);
        }

        private (Button, Image, Text) CreateButton(Transform parent, string label,
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
            if (onClick != null) btn.onClick.AddListener(() => onClick());

            var text = CreateText(go.transform, label, 16, TextAnchor.MiddleCenter,
                new Color(0.85f, 0.9f, 1f), Vector2.zero, size);
            var trt = text.rectTransform;
            trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
            trt.offsetMin = trt.offsetMax = Vector2.zero;
            return (btn, img, text);
        }

        private Slider CreateSlider(Transform parent, Vector2 pos, Vector2 size)
        {
            var go = new GameObject("Scrubber", typeof(RectTransform), typeof(Slider));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;

            var bg = new GameObject("Background", typeof(Image));
            bg.transform.SetParent(go.transform, false);
            bg.GetComponent<Image>().color = _bgColor;
            var bgrt = bg.GetComponent<RectTransform>();
            bgrt.anchorMin = new Vector2(0, 0.35f); bgrt.anchorMax = new Vector2(1, 0.65f);
            bgrt.offsetMin = bgrt.offsetMax = Vector2.zero;

            var fillArea = new GameObject("Fill Area", typeof(RectTransform));
            fillArea.transform.SetParent(go.transform, false);
            var fart = fillArea.GetComponent<RectTransform>();
            fart.anchorMin = new Vector2(0, 0.35f); fart.anchorMax = new Vector2(1, 0.65f);
            fart.offsetMin = fart.offsetMax = Vector2.zero;

            var fill = new GameObject("Fill", typeof(Image));
            fill.transform.SetParent(fillArea.transform, false);
            fill.GetComponent<Image>().color = _accent;
            var frt = fill.GetComponent<RectTransform>();
            frt.anchorMin = Vector2.zero; frt.anchorMax = Vector2.one;
            frt.offsetMin = frt.offsetMax = Vector2.zero;

            var handleArea = new GameObject("Handle Slide Area", typeof(RectTransform));
            handleArea.transform.SetParent(go.transform, false);
            var hart = handleArea.GetComponent<RectTransform>();
            hart.anchorMin = Vector2.zero; hart.anchorMax = Vector2.one;
            hart.offsetMin = new Vector2(8, 0); hart.offsetMax = new Vector2(-8, 0);

            var handle = new GameObject("Handle", typeof(Image));
            handle.transform.SetParent(handleArea.transform, false);
            handle.GetComponent<Image>().color = new Color(0.85f, 0.9f, 1f);
            var hrt = handle.GetComponent<RectTransform>();
            hrt.sizeDelta = new Vector2(14, 0);

            var slider = go.GetComponent<Slider>();
            slider.fillRect = frt;
            slider.handleRect = hrt;
            slider.targetGraphic = handle.GetComponent<Image>();
            slider.minValue = 0f;
            slider.maxValue = 1f;
            slider.direction = Slider.Direction.LeftToRight;
            return slider;
        }

        private Text CreateText(Transform parent, string content, int size,
                                TextAnchor anchor, Color color,
                                Vector2 anchoredPos, Vector2 sizeDelta)
        {
            var go = new GameObject("Text", typeof(Text));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<Text>();
            t.text = content; t.font = _font; t.fontSize = size;
            t.alignment = anchor; t.color = color;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
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
