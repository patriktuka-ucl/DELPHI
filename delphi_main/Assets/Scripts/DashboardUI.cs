using System.Collections.Generic;
using UnityEngine;
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

        [Header("Update")]
        [Tooltip("Seconds between redraws. This is a monitoring view, not the " +
                 "participant experience, so it doesn't need to be every frame.")]
        public float updateInterval = 0.1f;

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
        private readonly Color32 _line   = new Color32(70, 220, 160, 255);
        private readonly Color   _noSig  = new Color(0.45f, 0.45f, 0.45f);

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

            int rowHeight = cellHeight + 34; // header row + content
            int cols = Mathf.Max(1, columns);
            const float leftMargin = 30f;
            const float topMargin  = 66f;

            var scalarChannels = DelphiManager.AllChannels;
            var frameChannels  = DelphiManager.AllFrameChannels;
            int totalCells = scalarChannels.Length + frameChannels.Length;

            for (int i = 0; i < totalCells; i++)
            {
                int col = i % cols;
                int row = i / cols;
                float posX = leftMargin + col * (cellWidth  + colSpacing);
                float posY = -topMargin  - row * (rowHeight + rowSpacing);

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
                container.transform.SetParent(canvasGO.transform, false);
                var crt = container.GetComponent<RectTransform>();
                crt.anchorMin = crt.anchorMax = new Vector2(0, 1);
                crt.pivot = new Vector2(0, 1);
                crt.anchoredPosition = new Vector2(posX, posY);
                crt.sizeDelta = new Vector2(cellWidth, rowHeight);

                var titleText = CreateText(container.transform, $"{label}".TrimEnd(' ', '(', ')'),
                    17, TextAnchor.UpperLeft, new Color(0.8f, 0.85f, 0.95f),
                    new Vector2(0, 0), new Vector2(cellWidth * 0.6f, 24));

                var valueText = CreateText(container.transform, "No signal",
                    17, TextAnchor.UpperRight, _noSig,
                    new Vector2(cellWidth * 0.4f, 0), new Vector2(cellWidth * 0.6f, 24));

                var imgGO = new GameObject("Content", typeof(RawImage));
                imgGO.transform.SetParent(container.transform, false);
                var rawImage = imgGO.GetComponent<RawImage>();
                rawImage.color = Color.white; // texture supplies the actual colour
                var irt = imgGO.GetComponent<RectTransform>();
                irt.anchorMin = irt.anchorMax = new Vector2(0, 1);
                irt.pivot = new Vector2(0, 1);
                irt.anchoredPosition = new Vector2(0, -28);
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
            _timer += Time.deltaTime;
            if (_timer < updateInterval) return;
            _timer = 0f;
            RefreshAll();
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
                p.valueText.color = (Color)_line;
            }
            else
            {
                p.valueText.text  = "No signal";
                p.valueText.color = _noSig;
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
                p.valueText.text  = "Live";
                p.valueText.color = (Color)_line;
            }
            else
            {
                p.valueText.text  = "No signal";
                p.valueText.color = _noSig;
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

                int prevY = -1;
                for (int x = 0; x < w; x++)
                {
                    float v = p.history[x];
                    if (float.IsNaN(v)) continue;
                    float t = (v - mn) / range;
                    int yy = Mathf.Clamp(Mathf.RoundToInt(t * (h - 1)), 0, h - 1);
                    if (prevY < 0) prevY = yy;
                    int y0 = Mathf.Min(prevY, yy), y1 = Mathf.Max(prevY, yy);
                    for (int k = y0; k <= y1; k++) p.buffer[k * w + x] = _line;
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