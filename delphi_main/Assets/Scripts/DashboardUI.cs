using UnityEngine;
using UnityEngine.UI;
using System.Text;

namespace Delphi
{
    /// <summary>
    /// Dead-simple readout: lists every possible input in the top-left. Active
    /// channels show their value; inactive ones show "no data found". Updates a
    /// few times a second. Renders on its own display so it never overlays the
    /// simulator view.
    /// </summary>
    public class DashboardUI : MonoBehaviour
    {
        [Header("Link (auto-found if left empty)")]
        public DelphiManager manager;

        [Header("Display")]
        [Tooltip("0 = Display 1, 1 = Display 2. Keep the simulator on Display 1.")]
        public int dashboardDisplay = 1;

        [Header("Update")]
        [Tooltip("Seconds between text refreshes. Higher = cheaper / lazier.")]
        public float updateInterval = 0.1f;

        private readonly Color _bgColor = new Color(0.06f, 0.07f, 0.10f, 1f);
        private Text _text;
        private float _timer;
        private readonly StringBuilder _sb = new();

        private void Start()
        {
            if (manager == null) manager = FindFirstObjectByType<DelphiManager>();
            if (manager == null)
            {
                Debug.LogError("[DashboardUI] No DelphiManager found in the scene.");
                enabled = false;
                return;
            }

#if !UNITY_EDITOR
            if (dashboardDisplay >= 0 && dashboardDisplay < Display.displays.Length)
                Display.displays[dashboardDisplay].Activate();
#endif

            BuildDashboardCamera();
            BuildUI();
        }

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

            var go = new GameObject("Readout", typeof(Text));
            go.transform.SetParent(canvasGO.transform, false);
            _text = go.GetComponent<Text>();
            _text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _text.fontSize = 24;
            _text.supportRichText = true;
            _text.color = new Color(0.85f, 0.95f, 0.9f);
            _text.alignment = TextAnchor.UpperLeft;
            _text.horizontalOverflow = HorizontalWrapMode.Overflow;
            _text.verticalOverflow = VerticalWrapMode.Overflow;

            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(30, -30);
            rt.sizeDelta = new Vector2(700, 1000);

            Refresh();
        }

        private void Update()
        {
            _timer += Time.deltaTime;
            if (_timer < updateInterval) return;
            _timer = 0f;
            Refresh();
        }

        private void Refresh()
        {
            if (_text == null) return;

            _sb.Clear();
            _sb.AppendLine("DELPHI");
            _sb.AppendLine();

            foreach (var ch in DelphiManager.AllChannels)
            {
                var (label, unit) = DelphiManager.Meta(ch);
                _sb.Append(label).Append(": ");

                if (manager.HasData(ch))
                {
                    _sb.Append(manager.GetValue(ch).ToString("F1"));
                    if (!string.IsNullOrEmpty(unit)) _sb.Append(' ').Append(unit);
                    _sb.AppendLine();
                }
                else
                {
                    _sb.AppendLine("<color=#666666>no data found</color>");
                }
            }

            _text.text = _sb.ToString();
        }
    }
}