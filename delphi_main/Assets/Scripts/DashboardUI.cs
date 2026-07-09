using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UI;

namespace Delphi
{
    /// <summary>
    /// The researcher dashboard. Lists every channel DelphiManager knows
    /// about — both scalar (DelphiManager.AllChannels) and frame/video
    /// (DelphiManager.AllFrameChannels) — laid out in a top-left grid.
    ///
    /// Each cell shows either:
    ///   - a live scrolling waveform (scalar channels with data), or
    ///   - a live video preview (frame channels with data), or
    ///   - "No signal", if that slot is empty / disconnected.
    ///
    /// Driven entirely through the manager's public API (HasData / GetValue /
    /// Meta for scalars, HasFrame / GetFrame / FrameMeta for video) — the
    /// dashboard never holds a direct sensor reference.
    ///
    /// Renders on its own display with its own clearing camera, so it never
    /// overlays the simulator/participant view.
    /// </summary>
    public class DashboardUI : MonoBehaviour
    {
        [Header("Link (auto-found if left empty)")]
        public DelphiManager manager;

        [Header("Display")]
        [Tooltip("0 = Display 1, 1 = Display 2. Keep the simulator on Display 1.")]
        public int dashboardDisplay = 1;

        [Tooltip(
            "EDITOR-ONLY WORKAROUND: when running inside the Unity Editor (no " +
            "real second monitor), the Game view's 'Display 2' preview is often " +
            "letterboxed/centered inside the panel rather than filling it, which " +
            "makes the dashboard appear off-center even though its anchors are " +
            "correct. If that happens, nudge this until it lines up in the top " +
            "-left — e.g. (-350, 200) — and it'll be applied automatically every " +
            "time you press Play. Set back to (0,0) once you're running an " +
            "actual build with a real second monitor.")]
        public Vector2 editorPreviewOffset = Vector2.zero;

        [Header("Redraw (view only — does NOT affect sampling or recording)")]
        [Tooltip("Seconds between dashboard redraws. This only throttles how " +
                 "often this monitoring view repaints; it has no effect on how " +
                 "often sensors are sampled or what ends up in a recording — " +
                 "that is entirely controlled by DelphiManager's own sample-rate settings.")]
        [FormerlySerializedAs("updateInterval")]
        public float redrawInterval = 0.1f;

        [Header("Grid layout")]
        [Tooltip("Also doubles as the number of waveform samples kept per channel.")]
        public int cellWidth   = 240;
        public int cellHeight  = 60;
        public int columns     = 3;
        public int colSpacing  = 30;
        public int rowSpacing  = 20;

        private readonly Color  _bgColor = new Color(0.06f, 0.07f, 0.10f, 1f);
        private readonly Color32 _bg     = new Color32(18, 20, 28, 255);
        private readonly Color32 _grid   = new Color32(38, 42, 54, 255);
        private readonly Color32 _line          = new Color32(70, 220, 160, 255); // live — green
        private readonly Color32 _linePlayback   = new Color32(80, 160, 235, 255); // playback — blue
        // Status colours — must match SessionControlsUI's legend if one exists.
        private readonly Color _notAttached = new Color(0.45f, 0.45f, 0.45f); // gray  — no sensor plugged in
        private readonly Color _noSignal    = new Color(0.85f, 0.25f, 0.25f); // red   — plugged in, nothing coming through
        private readonly Color _disabled    = new Color(0.85f, 0.75f, 0.25f); // yellow — plugged in but toggled off

        // One of these per cell — scalar cells use tex/buffer/history for a
        // waveform; frame cells just point the RawImage at the sensor's
        // live texture directly.
        private class Panel
        {
            public bool       isFrame;
            public Channel    channel;       // valid when !isFrame
            public FrameChannel frameChannel; // valid when isFrame
            public Text       titleText;
            public Text       valueText;
            public RawImage   image;

            // Scalar-only:
            public Texture2D  tex;
            public Color32[]  buffer;
            public float[]    history;
        }

        private readonly List<Panel> _panels = new();
        private Font _font;
        private float _timer;
        private string _lastPlaybackSignature; // detects Load / Eject / switch-session

        private Color32 CurrentLineColor => manager.IsInPlayback ? _linePlayback : _line;

        private void Start()
        {
            if (manager == null) manager = FindFirstObjectByType<DelphiManager>();
            if (manager == null)
            {
                Debug.LogError("[DashboardUI] No DelphiManager found in the scene.");
                enabled = false;
                return;
            }

            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

#if !UNITY_EDITOR
            if (dashboardDisplay >= 0 && dashboardDisplay < Display.displays.Length)
                Display.displays[dashboardDisplay].Activate();
#endif

            BuildDashboardCamera();
            BuildUI();
        }

        // A dedicated camera whose only job is to clear the display every
        // frame — stops smearing, keeps this off the simulator's view.
        private void BuildDashboardCamera()
        {
            var camGO = new GameObject("Dashboard Camera", typeof(Camera));
            camGO.transform.SetParent(transform, false);
            var cam = camGO.GetComponent<Camera>();
            cam.clearFlags      = CameraClearFlags.SolidColor;
            cam.backgroundColor = _bgColor;
            cam.cullingMask     = 0;
            cam.targetDisplay   = dashboardDisplay;
            cam.depth           = 100;
        }

        private void BuildUI()
        {
            var canvasGO = new GameObject("DELPHI Canvas",
                typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGO.transform.SetParent(transform, false);
            var canvas = canvasGO.GetComponent<Canvas>();
            canvas.renderMode    = RenderMode.ScreenSpaceOverlay;
            canvas.targetDisplay = dashboardDisplay;
            var scaler = canvasGO.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;

            // Editor-only compensation for the Game view's letterboxed
            // preview of a non-primary display.
            var canvasRT = canvasGO.GetComponent<RectTransform>();
            canvasRT.anchoredPosition = editorPreviewOffset;

            // Opaque background (belt-and-braces with the clearing camera).
            var bgGO = new GameObject("Background", typeof(Image));
            bgGO.transform.SetParent(canvasGO.transform, false);
            bgGO.GetComponent<Image>().color = _bgColor;
            var bgrt = bgGO.GetComponent<RectTransform>();
            bgrt.anchorMin = Vector2.zero; bgrt.anchorMax = Vector2.one;
            bgrt.offsetMin = bgrt.offsetMax = Vector2.zero;

            CreateText(canvasGO.transform, "DELPHI — live sensor dashboard",
                26, TextAnchor.UpperLeft, new Color(0.85f, 0.9f, 1f),
                new Vector2(30, -20), new Vector2(800, 34));

            // Two text lines (title, then status/value) stacked above the
            // content so long labels ("Gaze / Saccade rate", "Not attached")
            // never overlap each other regardless of length.
            const float titleLineHeight = 22f;
            const float valueLineHeight = 20f;
            int rowHeight = cellHeight + (int)(titleLineHeight + valueLineHeight);
            int cols = Mathf.Max(1, columns);
            const float topMargin = 66f;

            var scalarChannels = DelphiManager.AllChannels;
            var frameChannels  = DelphiManager.AllFrameChannels;
            int totalCells = scalarChannels.Length + frameChannels.Length;
            int rows = Mathf.CeilToInt(totalCells / (float)cols);

            // Grid container is anchored to the top-center of the canvas so
            // the whole panel grid stays horizontally centered regardless of
            // the display's resolution/aspect ratio.
            float gridWidth  = cols * cellWidth + (cols - 1) * colSpacing;
            float gridHeight = rows * rowHeight + (rows - 1) * rowSpacing;
            var gridGO = new GameObject("Grid", typeof(RectTransform));
            gridGO.transform.SetParent(canvasGO.transform, false);
            var gridRT = gridGO.GetComponent<RectTransform>();
            gridRT.anchorMin = gridRT.anchorMax = new Vector2(0.5f, 1f);
            gridRT.pivot = new Vector2(0.5f, 1f);
            gridRT.anchoredPosition = new Vector2(0f, -topMargin);
            gridRT.sizeDelta = new Vector2(gridWidth, gridHeight);

            for (int i = 0; i < totalCells; i++)
            {
                int col = i % cols;
                int row = i / cols;
                float posX = col * (cellWidth  + colSpacing);
                float posY = -row * (rowHeight + rowSpacing);

                bool isFrame = i >= scalarChannels.Length;
                string label, unit, cellName;

                if (isFrame)
                {
                    var fc = frameChannels[i - scalarChannels.Length];
                    (label, unit) = DelphiManager.FrameMeta(fc);
                    cellName = $"Panel_{fc}";
                }
                else
                {
                    var ch = scalarChannels[i];
                    (label, unit) = DelphiManager.Meta(ch);
                    cellName = $"Panel_{ch}";
                }

                var container = new GameObject(cellName, typeof(RectTransform));
                container.transform.SetParent(gridGO.transform, false);
                var crt = container.GetComponent<RectTransform>();
                crt.anchorMin = crt.anchorMax = new Vector2(0, 1);
                crt.pivot = new Vector2(0, 1);
                crt.anchoredPosition = new Vector2(posX, posY);
                crt.sizeDelta = new Vector2(cellWidth, rowHeight);

                // Title and status/value each get their own full-width line —
                // stacked, not side-by-side, so long text never overlaps.
                var titleText = CreateText(container.transform, $"{label}".TrimEnd(' ', '(', ')'),
                    16, TextAnchor.UpperLeft, new Color(0.8f, 0.85f, 0.95f),
                    new Vector2(0, 0), new Vector2(cellWidth, titleLineHeight));

                var valueText = CreateText(container.transform, "Not attached",
                    15, TextAnchor.UpperRight, _notAttached,
                    new Vector2(0, -titleLineHeight), new Vector2(cellWidth, valueLineHeight));

                var imgGO = new GameObject("Content", typeof(RawImage));
                imgGO.transform.SetParent(container.transform, false);
                var rawImage = imgGO.GetComponent<RawImage>();
                rawImage.color = Color.white; // texture supplies the actual colour
                var irt = imgGO.GetComponent<RectTransform>();
                irt.anchorMin = irt.anchorMax = new Vector2(0, 1);
                irt.pivot = new Vector2(0, 1);
                irt.anchoredPosition = new Vector2(0, -(titleLineHeight + valueLineHeight));
                irt.sizeDelta = new Vector2(cellWidth, cellHeight);

                var panel = new Panel
                {
                    isFrame      = isFrame,
                    titleText    = titleText,
                    valueText    = valueText,
                    image        = rawImage
                };

                if (isFrame)
                {
                    panel.frameChannel = frameChannels[i - scalarChannels.Length];
                    // Blank placeholder texture until a real frame arrives.
                    var placeholder = new Texture2D(2, 2);
                    placeholder.SetPixels(new[] { (Color)_bg, (Color)_bg, (Color)_bg, (Color)_bg });
                    placeholder.Apply();
                    rawImage.texture = placeholder;
                }
                else
                {
                    panel.channel = scalarChannels[i];
                    panel.tex     = new Texture2D(cellWidth, cellHeight, TextureFormat.RGBA32, false)
                        { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                    panel.buffer  = new Color32[cellWidth * cellHeight];
                    panel.history = new float[cellWidth];
                    for (int x = 0; x < cellWidth; x++) panel.history[x] = float.NaN;
                    rawImage.texture = panel.tex;
                    ClearTexture(panel.tex, panel.buffer);
                }

                _panels.Add(panel);
            }
        }

        private void Update()
        {
            string sig = manager.IsInPlayback ? manager.Playback.LoadedPath : null;
            if (sig != _lastPlaybackSignature)
            {
                _lastPlaybackSignature = sig;
                ResetWaveforms();
            }

            _timer += Time.deltaTime;
            if (_timer < redrawInterval) return;
            _timer = 0f;
            RefreshAll();
        }

        // Called whenever playback is loaded, ejected, or switched to a
        // different session — old graphs from a different source (live vs.
        // a previous recording) would otherwise linger and mislead.
        private void ResetWaveforms()
        {
            foreach (var p in _panels)
            {
                if (p.isFrame) continue;
                for (int x = 0; x < p.history.Length; x++) p.history[x] = float.NaN;
                RedrawWaveform(p);
            }
        }

        private void RefreshAll()
        {
            foreach (var p in _panels)
            {
                if (p.isFrame) RefreshFramePanel(p);
                else            RefreshScalarPanel(p);
            }
        }

        private void RefreshScalarPanel(Panel p)
        {
            bool hasData = manager.HasData(p.channel);
            float value  = manager.GetValue(p.channel);

            System.Array.Copy(p.history, 1, p.history, 0, cellWidth - 1);
            p.history[cellWidth - 1] = hasData ? value : float.NaN;

            RedrawWaveform(p);

            var (_, unit) = DelphiManager.Meta(p.channel);
            if (hasData)
            {
                p.valueText.text  = $"{value:F1} {unit}".TrimEnd();
                p.valueText.color = (Color)CurrentLineColor;
            }
            else
            {
                var status = manager.GetStatus(p.channel);
                (p.valueText.text, p.valueText.color) = status switch
                {
                    ChannelStatus.Disabled => ("DISABLED", _disabled),
                    ChannelStatus.NoSignal => ("No signal", _noSignal),
                    _                      => ("Not attached", _notAttached)
                };
            }
        }

        private void RefreshFramePanel(Panel p)
        {
            bool hasFrame = manager.HasFrame(p.frameChannel);

            if (hasFrame)
            {
                var tex = manager.GetFrame(p.frameChannel);
                if (tex != null && tex.width > 0)
                {
                    p.image.texture = tex;

                    // Keep width fixed at cellWidth; derive height from the
                    // texture's own aspect ratio so the video isn't stretched.
                    float aspect = (float)tex.height / tex.width;
                    p.image.rectTransform.sizeDelta = new Vector2(cellWidth, cellWidth * aspect);
                }
                p.valueText.text  = manager.IsInPlayback ? "Playback" : "Live";
                p.valueText.color = (Color)CurrentLineColor;
            }
            else
            {
                var status = manager.GetStatus(p.frameChannel);
                (p.valueText.text, p.valueText.color) = status switch
                {
                    ChannelStatus.Disabled => ("DISABLED", _disabled),
                    ChannelStatus.NoSignal => ("No signal", _noSignal),
                    _                      => ("Not attached", _notAttached)
                };
            }
        }

        private void RedrawWaveform(Panel p)
        {
            int w = cellWidth, h = cellHeight;

            for (int i = 0; i < p.buffer.Length; i++) p.buffer[i] = _bg;
            int midY = h / 2;
            for (int x = 0; x < w; x++) p.buffer[midY * w + x] = _grid;

            float mn = float.MaxValue, mx = float.MinValue;
            bool any = false;
            for (int x = 0; x < w; x++)
            {
                float v = p.history[x];
                if (float.IsNaN(v)) continue;
                any = true;
                if (v < mn) mn = v;
                if (v > mx) mx = v;
            }

            if (any)
            {
                float range = Mathf.Max(mx - mn, 1e-3f);
                float pad = range * 0.15f;
                mn -= pad; range += pad * 2f;
                Color32 lineColor = CurrentLineColor;

                int prevY = -1;
                for (int x = 0; x < w; x++)
                {
                    float v = p.history[x];
                    if (float.IsNaN(v)) continue;
                    float t = (v - mn) / range;
                    int yy = Mathf.Clamp(Mathf.RoundToInt(t * (h - 1)), 0, h - 1);
                    if (prevY < 0) prevY = yy;
                    int y0 = Mathf.Min(prevY, yy), y1 = Mathf.Max(prevY, yy);
                    for (int k = y0; k <= y1; k++) p.buffer[k * w + x] = lineColor;
                    prevY = yy;
                }
            }

            p.tex.SetPixels32(p.buffer);
            p.tex.Apply(false);
        }

        private static void ClearTexture(Texture2D tex, Color32[] buffer)
        {
            tex.SetPixels32(buffer);
            tex.Apply(false);
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