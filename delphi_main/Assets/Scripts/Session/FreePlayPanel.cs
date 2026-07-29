using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Delphi.Session
{
    /// <summary>
    /// The PARTICIPANT-facing free-play control surface — a world-space
    /// canvas mounted in front of the driver (parented to the participant's
    /// seat in VR, so it stays within reach as the car moves). Only visible
    /// during Phase.FreePlay.
    ///
    /// This is a pure view, same as ExperimentUI: it reads SessionController
    /// and CarDriver's live state and writes back through
    /// SessionController.SetFreePlayParameter / SetFreePlayStyle — no logging
    /// or phase logic lives here, that's all already on SessionController and
    /// applies identically regardless of which UI called it.
    ///
    /// THREE CONTROL MODES, ONE PANEL.
    ///
    ///   The question of how many dials to put in front of a participant is a
    ///   study-design question, not an engineering one, and the answer changes
    ///   between pilots. Four independent sliders ask the participant to have
    ///   an opinion about cornering speed separately from braking, which most
    ///   people do not; one slider asks only "how assertive", which is the
    ///   thing they actually have an opinion about; named presets ask them to
    ///   PICK rather than to dial, which is faster and produces clean,
    ///   comparable levels across participants at the cost of resolution.
    ///
    ///   All three write to the same place and log identically, so switching
    ///   modes between conditions costs nothing downstream — the free-play log
    ///   has the same four columns whichever surface produced them.
    ///
    /// Positioning is exposed in the Inspector because it genuinely needs a
    /// human's eyes in the Editor (and ideally the actual seat) to get right
    /// — there's no way to derive "in front of the driver" from code alone.
    /// </summary>
    public class FreePlayPanel : MonoBehaviour
    {
        /// <summary>Which control surface the participant is given. See the
        /// class summary for why this is a choice rather than a constant.</summary>
        public enum ControlMode
        {
            /// <summary>One slider per driving parameter — the original panel.</summary>
            PerParameterSliders = 0,
            /// <summary>A single defensive→aggressive slider that moves every
            /// parameter together.</summary>
            DrivingStyleSlider = 1,
            /// <summary>Named styles as a toggle group, spread evenly over the
            /// same 0..1 range.</summary>
            StylePresets = 2,
        }

        [Tooltip("Which control surface the participant gets. The other modes' " +
                 "settings are hidden rather than deleted, so switching back " +
                 "and forth between pilots keeps whatever you tuned.")]
        public ControlMode controlMode = ControlMode.PerParameterSliders;

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
        [Tooltip("Panel size for the per-parameter mode. The two single-axis " +
                 "modes are sized by width alone (below) so their type is " +
                 "never stretched.")]
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

        // ── Driving-style slider ────────────────────────────────────────

        [Header("Driving style slider")]
        [Tooltip("Panel width in metres. Height follows the layout, so the " +
                 "panel is scaled uniformly and text is never stretched.")]
        public float styleWidthMeters = 0.5f;
        [Tooltip("Left end of the scale — the value 0 on every parameter.")]
        public string styleLowLabel = "DEFENSIVE";
        [Tooltip("Right end of the scale — the value 1 on every parameter.")]
        public string styleHighLabel = "AGGRESSIVE";
        [Tooltip("Number of detents, or 0 for a continuous slider. Detents " +
                 "make the same setting repeatable between participants; " +
                 "continuous lets them find a value they can't name.")]
        [Min(0)] public int styleSteps;

        // ── Style presets ───────────────────────────────────────────────

        [Header("Style presets")]
        [Tooltip("Panel width in metres. Height follows the number of preset " +
                 "rows, so the panel grows downward as you add options.")]
        public float presetsWidthMeters = 0.56f;
        [Tooltip("The named styles, in order from defensive to aggressive. " +
                 "They are spread evenly over 0..1 — the FIRST is always 0 and " +
                 "the LAST always 1, whatever the count — so adding or removing " +
                 "one re-spaces the rest automatically.")]
        public List<string> stylePresets = new()
            { "SLOTH", "CHILL", "STANDARD", "HURRY", "MAD MAX" };
        [Tooltip("Height of each preset button in canvas pixels.")]
        public float presetButtonHeightPixels = 84f;
        [Tooltip("Narrowest a preset button may get before the row wraps onto " +
                 "a second line. A fingertip lands within about a centimetre " +
                 "of where the tracker says it is, so buttons that shrink " +
                 "indefinitely as options are added become unhittable.")]
        public float presetMinButtonWidthPixels = 110f;
        [Tooltip("Invisible hit area around each preset button, in canvas " +
                 "pixels. Clamped to half the gap between buttons — past that " +
                 "neighbouring targets overlap and the press lands on whichever " +
                 "button happens to be checked first.")]
        public float presetHitPaddingPixels = 12f;
        [Tooltip("How near the panel plane the fingertip must come to fire, " +
                 "in metres. Hand tracking is poor at depth, so this is generous. " +
                 "It is NOT just a tolerance: the finger has to be measured " +
                 "inside this band for at least 'Min press time' below, so " +
                 "shrinking it makes fast pokes stop registering.")]
        public float presetTouchDepthMeters = 0.035f;
        [Tooltip("Shortest press that counts, in seconds. Rejects a fingertip " +
                 "that clipped the button while travelling somewhere else.\n\n" +
                 "This and the touch depth are ONE setting in two fields: a poke " +
                 "faster than (2 × depth ÷ this) crosses the band between frames " +
                 "and is thrown away. The Inspector reports that speed — keep it " +
                 "above about 1.2 m/s or deliberate presses will be ignored.")]
        public float presetMinPressSeconds = 0.05f;
        [Tooltip("Dead time after ANY button fires, in seconds. Shared by every " +
                 "button, so a hand withdrawing through a second one cannot " +
                 "trigger it. Raise it if a single poke ever acts twice.")]
        public float presetRepeatLockSeconds = 0.45f;
        [Tooltip("Caption above the numeric readout under the buttons. Blank " +
                 "for none.")]
        public string presetValueCaption = "STYLE VALUE";

        // ── Layout constants ────────────────────────────────────────────

        /// <summary>Layout width for every mode. Shared so type is the same
        /// physical size on all three panels — the participant should not have
        /// to re-focus because the study switched control surface.</summary>
        public const int PixelWidth = 640;
        private const int PerParameterPixelHeight = 660;
        /// <summary>Gap between preset buttons, in canvas pixels. Public
        /// because it is also the ceiling on hit padding — the Inspector has to
        /// report the same number the layout uses, or its warning about
        /// overlapping targets would be about a different panel.</summary>
        public const float PresetGapPixels = 24f;
        private const float PresetsTopPixels = 104f;

        /// <summary>Layout width available to controls, inside the margins.</summary>
        public const float UsableWidthPixels = PixelWidth - 56f;

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

        // Single-style-slider mode.
        private VR.VrTouchSlider _styleSlider;
        private Text _styleValueTxt;

        // Preset mode.
        private string[] _presetLabels = System.Array.Empty<string>();
        private VR.VrTouchButton[] _presetButtons = System.Array.Empty<VR.VrTouchButton>();
        private Text _presetValueTxt;
        private int _selectedPreset = -1;

        /// <summary>Where a preset sits on the 0..1 style axis. First is always
        /// 0 and last always 1, so the ends of the scale are always reachable
        /// however many options there are; a lone option sits at the midpoint
        /// because there is no scale for it to be an end of.</summary>
        public static float PresetValue(int index, int count) =>
            count <= 1 ? 0.5f : Mathf.Clamp01(index / (float)(count - 1));

        /// <summary>The presets as they will actually be built: blanks dropped,
        /// and a single fallback if that leaves nothing. Shared with the
        /// custom Inspector so the preview it draws is the panel you get.</summary>
        public static string[] SanitisePresets(IReadOnlyList<string> raw)
        {
            var kept = new List<string>();
            if (raw != null)
                foreach (string s in raw)
                    if (!string.IsNullOrWhiteSpace(s)) kept.Add(s.Trim());
            if (kept.Count == 0) kept.Add("STANDARD");
            return kept.ToArray();
        }

        /// <summary>The fastest fingertip, in m/s, that can still register a
        /// press — the single number that decides whether the buttons work.
        ///
        /// A press is only committed once the fingertip has been MEASURED
        /// inside the touch band for minPressSeconds. The band is 2 × depth
        /// thick (unsigned, so a poke that goes through the panel crosses both
        /// halves), and a finger travelling at v is inside it for 2 × depth ÷ v
        /// seconds. Faster than this and every poke is discarded as "clipped
        /// the button on its way somewhere else" — the button still LIGHTS UP
        /// on the way in, so it looks alive and simply never fires.
        ///
        /// A deliberate poke at a small target runs about 1–1.5 m/s, and people
        /// poke small targets faster than large ones, so shrinking the button
        /// and the depth together pushes both sides of this the wrong way.</summary>
        public static float FastestRegisteringPoke(float touchDepthMeters, float minPressSeconds) =>
            minPressSeconds <= 0f ? float.PositiveInfinity : 2f * touchDepthMeters / minPressSeconds;

        /// <summary>The floor the runtime applies to preset button height, in
        /// canvas pixels. Public so the Inspector can say when an authored
        /// value is being clamped instead of silently used.</summary>
        public const float MinPresetButtonHeightPixels = 32f;

        /// <summary>Rows the preset grid will wrap onto, and how many buttons
        /// sit on each. Public for the Inspector preview.</summary>
        public static int PresetColumns(int count, float minButtonWidth)
        {
            // Solve n * minWidth + (n-1) * gap <= usable for n.
            int fit = Mathf.FloorToInt((UsableWidthPixels + PresetGapPixels) /
                                       (Mathf.Max(24f, minButtonWidth) + PresetGapPixels));
            return Mathf.Clamp(fit, 1, Mathf.Max(1, count));
        }

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

            switch (controlMode)
            {
                case ControlMode.DrivingStyleSlider: EchoStyleSlider(); break;
                case ControlMode.StylePresets:       EchoPresets();     break;
                default:                             EchoParameters();  break;
            }
        }

        private void EchoParameters()
        {
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

        private void EchoStyleSlider()
        {
            float style = session.CurrentFreePlayStyle;

            // The readout tracks the car even mid-drag — the finger has already
            // written its value through SetFreePlayStyle, so the two agree, and
            // anything else that moves the parameters shows up immediately
            // rather than after the participant lets go.
            if (_styleValueTxt != null) _styleValueTxt.text = style.ToString("0.00");

            // The KNOB is only moved when nobody is holding it. Writing a value
            // into a control the participant has their finger on fights them and
            // makes the slider feel like it is snapping away.
            if (_styleSlider != null && !_styleSlider.IsEngaged)
                _styleSlider.SetValue(style, notify: false);
        }

        /// <summary>Lights the preset nearest the car's actual style and keeps
        /// the numeric readout live.
        ///
        /// Driven from the CAR rather than from the last press, deliberately.
        /// The two agree while the panel is the only thing writing, and diverge
        /// the moment anything else does — and a group still showing STANDARD
        /// after the parameters moved is worse than one showing nothing, because
        /// it is a confident wrong answer about what the participant is
        /// currently driving.</summary>
        private void EchoPresets()
        {
            if (_presetButtons.Length == 0) return;

            float style = session.CurrentFreePlayStyle;

            int nearest = 0;
            float best = float.MaxValue;
            for (int i = 0; i < _presetButtons.Length; i++)
            {
                float d = Mathf.Abs(PresetValue(i, _presetButtons.Length) - style);
                if (d < best) { best = d; nearest = i; }
            }

            if (nearest != _selectedPreset)
            {
                _selectedPreset = nearest;
                for (int i = 0; i < _presetButtons.Length; i++)
                    if (_presetButtons[i] != null) _presetButtons[i].SetPrimary(i == nearest);
            }

            if (_presetValueTxt != null) _presetValueTxt.text = style.ToString("0.00");
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

            int pixelHeight = LayoutPixelHeight();
            var crt = canvasGO.GetComponent<RectTransform>();
            crt.sizeDelta = new Vector2(PixelWidth, pixelHeight);

            // The per-parameter panel keeps its authored width AND height, so
            // it looks exactly as it always has. The two single-axis modes take
            // a width only and derive the height from the layout, which is the
            // one way to guarantee their type is never stretched — and they are
            // new, so there is no existing look to preserve.
            if (controlMode == ControlMode.PerParameterSliders)
            {
                float sx = sizeMeters.x / PixelWidth, sy = sizeMeters.y / pixelHeight;
                canvasGO.transform.localScale = new Vector3(sx, sy, (sx + sy) * 0.5f);
            }
            else
            {
                float s = Mathf.Max(0.001f, WidthMeters) / PixelWidth;
                canvasGO.transform.localScale = new Vector3(s, s, s);
            }

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
                new Color(0.92f, 0.95f, 1f), new Vector2(28, -22), new Vector2(PixelWidth - 200, 36));
            title.fontStyle = FontStyle.Bold;

            switch (controlMode)
            {
                case ControlMode.DrivingStyleSlider: BuildStyleSlider(canvasGO.transform); break;
                case ControlMode.StylePresets:       BuildPresets(canvasGO.transform);     break;
                default:                             BuildParameterRows(canvasGO.transform); break;
            }

            _panelRoot = holder;
            _panelRoot.SetActive(false); // hidden until Phase.FreePlay — canvas AND controls
        }

        /// <summary>Panel width in metres for the mode in use.</summary>
        public float WidthMeters => controlMode switch
        {
            ControlMode.DrivingStyleSlider => styleWidthMeters,
            ControlMode.StylePresets       => presetsWidthMeters,
            _                              => sizeMeters.x,
        };

        /// <summary>Panel height in metres for the mode in use. Derived from
        /// the layout for the two single-axis modes; authored for the
        /// per-parameter one.</summary>
        public float HeightMeters => controlMode == ControlMode.PerParameterSliders
            ? sizeMeters.y
            : Mathf.Max(0.001f, WidthMeters) * LayoutPixelHeight() / PixelWidth;

        /// <summary>Height of the layout in canvas pixels. Fixed for the two
        /// slider modes; grows with the number of preset rows for the third,
        /// which is what makes "add another option" a one-field change rather
        /// than a re-layout.</summary>
        public int LayoutPixelHeight()
        {
            switch (controlMode)
            {
                case ControlMode.DrivingStyleSlider:
                    return 262;

                case ControlMode.StylePresets:
                {
                    int count = SanitisePresets(stylePresets).Length;
                    int cols = PresetColumns(count, presetMinButtonWidthPixels);
                    int rows = Mathf.CeilToInt(count / (float)cols);
                    float h = Mathf.Max(MinPresetButtonHeightPixels, presetButtonHeightPixels);
                    return Mathf.RoundToInt(PresetsTopPixels
                                            + rows * h + (rows - 1) * PresetGapPixels
                                            + 76f);   // readout + bottom margin
                }

                default:
                    return PerParameterPixelHeight;
            }
        }

        // ── Mode: one slider per parameter (the original) ────────────────

        private void BuildParameterRows(Transform parent)
        {
            const float RowH = 132f;
            const float RowTop = -100f;

            for (int i = 0; i < ParamKeys.Length; i++)
            {
                float y = RowTop - i * RowH;
                int idx = i; // capture per-iteration, not the loop variable

                Txt(parent, ParamLabels[i].ToUpperInvariant(), 19,
                    new Color(0.74f, 0.79f, 0.88f), new Vector2(28, y), new Vector2(PixelWidth - 160, 26));

                _values[i] = Txt(parent, "0.50", 22,
                    new Color(0.24f, 0.83f, 0.76f), new Vector2(PixelWidth - 118, y - 2),
                    new Vector2(90, 28));
                _values[i].alignment = TextAnchor.UpperRight;

                // The interactive row is deliberately TALLER than the visible
                // track. Hand tracking has centimetres of jitter, and a target
                // only as tall as an 14 px bar would be missed constantly.
                var row = NewRow(parent, $"Row_{ParamLabels[i]}", new Vector2(28, y - 38),
                                 new Vector2(PixelWidth - 56, 88));

                var slider = row.gameObject.AddComponent<VR.VrTouchSlider>();
                slider.Build(row, _font);
                slider.BindValueLabel(_values[i]);
                slider.onValueChanged.AddListener(val => session?.SetFreePlayParameter(ParamKeys[idx], val));
                _touchSliders[i] = slider;
            }
        }

        // ── Mode: one defensive→aggressive slider ────────────────────────

        private void BuildStyleSlider(Transform parent)
        {
            // The readout is driven from the CAR in EchoStyleSlider rather than
            // bound to the slider with BindValueLabel. A bound label switches to
            // an ORDINAL ("3 of 5") the moment the slider has detents, which is
            // the right readout for a questionnaire scale and the wrong one
            // here: the number this panel is about is the 0..1 style value that
            // goes into the log, and it must not change meaning because someone
            // added detents.
            _styleValueTxt = Txt(parent, "0.50", 26, new Color(0.24f, 0.83f, 0.76f),
                                 new Vector2(PixelWidth - 168, -22), new Vector2(140, 34));
            _styleValueTxt.alignment = TextAnchor.UpperRight;

            var row = NewRow(parent, "Row_DrivingStyle", new Vector2(28, -108),
                             new Vector2(PixelWidth - 56, 92));

            _styleSlider = row.gameObject.AddComponent<VR.VrTouchSlider>();
            _styleSlider.steps = Mathf.Max(0, styleSteps);   // read by Build for the tick marks
            _styleSlider.Build(row, _font, trackHeight: 26f, knobSize: 68f);

            // ONE CALL, NOT FOUR SetFreePlayParameter CALLS. See
            // SessionController.SetFreePlayStyle: four calls would write four
            // log rows per frame of a drag, three of them describing a car
            // state that existed only between two statements.
            _styleSlider.onValueChanged.AddListener(val => session?.SetFreePlayStyle(val));

            var muted = new Color(0.74f, 0.79f, 0.88f);
            var low = Txt(parent, styleLowLabel, 20, muted, new Vector2(28, -208),
                          new Vector2((PixelWidth - 56) * 0.5f, 28));
            low.alignment = TextAnchor.UpperLeft;

            var high = Txt(parent, styleHighLabel, 20, muted,
                           new Vector2(PixelWidth * 0.5f, -208),
                           new Vector2(PixelWidth * 0.5f - 28, 28));
            high.alignment = TextAnchor.UpperRight;
        }

        // ── Mode: named style presets ───────────────────────────────────

        private void BuildPresets(Transform parent)
        {
            _presetLabels = SanitisePresets(stylePresets);
            _presetButtons = new VR.VrTouchButton[_presetLabels.Length];

            if (stylePresets == null || stylePresets.Count == 0)
                Debug.LogWarning("[FreePlayPanel] Style presets mode with no presets listed — falling back " +
                                 "to a single STANDARD option so the panel is not empty. Add the styles you " +
                                 "want in the Inspector.", this);

            int cols = PresetColumns(_presetLabels.Length, presetMinButtonWidthPixels);
            int rows = Mathf.CeilToInt(_presetLabels.Length / (float)cols);
            float btnH = Mathf.Max(MinPresetButtonHeightPixels, presetButtonHeightPixels);

            // Padding is clamped to half the gap. Past that the invisible hit
            // areas of neighbouring buttons overlap, and since the fingertip is
            // offered to buttons in order, a press in the overlap lands on
            // whichever was registered first — i.e. the participant picks CHILL
            // and gets SLOTH, repeatably, with nothing on screen to explain it.
            float pad = Mathf.Clamp(presetHitPaddingPixels, 0f, PresetGapPixels * 0.5f);

            for (int i = 0; i < _presetLabels.Length; i++)
            {
                int idx = i;
                int r = i / cols;
                int c = i % cols;

                // Every button is the same width — sized for a FULL row — and a
                // short last row is centred instead of stretched. Sizing each
                // row to its own contents would make the buttons on a 3 + 1
                // layout wildly different sizes, and size reads as importance.
                int inRow = Mathf.Min(cols, _presetLabels.Length - r * cols);
                float btnW = (UsableWidthPixels - (cols - 1) * PresetGapPixels) / cols;
                float rowWidth = inRow * btnW + (inRow - 1) * PresetGapPixels;
                float x = 28f + (UsableWidthPixels - rowWidth) * 0.5f + c * (btnW + PresetGapPixels);
                float y = -(PresetsTopPixels + r * (btnH + PresetGapPixels));

                float value = PresetValue(i, _presetLabels.Length);
                var btn = BuildPresetButton(parent, new Vector2(x, y), btnW, btnH, _presetLabels[i], pad);
                btn.onPressed.AddListener(() => session?.SetFreePlayStyle(value));
                _presetButtons[idx] = btn;
            }

            float gridBottom = PresetsTopPixels + rows * btnH + (rows - 1) * PresetGapPixels;

            // The numeric readout. Named levels are easier to choose between
            // and impossible to compare across participants — "HURRY" means
            // nothing next to a per-parameter session's 0.75. Showing the
            // number under the name keeps the two commensurable, and lets a
            // researcher watching the mirrored view read off exactly what the
            // participant selected.
            if (!string.IsNullOrWhiteSpace(presetValueCaption))
            {
                var cap = Txt(parent, presetValueCaption, 15, new Color(0.55f, 0.60f, 0.70f),
                              new Vector2(28, -(gridBottom + 14f)), new Vector2(PixelWidth - 56, 20));
                cap.alignment = TextAnchor.UpperCenter;
            }

            _presetValueTxt = Txt(parent, "0.50", 24, new Color(0.24f, 0.83f, 0.76f),
                                  new Vector2(28, -(gridBottom + 34f)), new Vector2(PixelWidth - 56, 32));
            _presetValueTxt.alignment = TextAnchor.UpperCenter;
        }

        private VR.VrTouchButton BuildPresetButton(Transform parent, Vector2 pos, float width, float height,
                                                   string text, float hitPadding)
        {
            var go = new GameObject("Preset_" + text, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = pos;
            rt.sizeDelta = new Vector2(width, height);

            var bg = NewImage(go.transform, new Color(1f, 1f, 1f, 0.10f));
            Stretch(bg.rectTransform);
            var bgSprite = Resources.GetBuiltinResource<Sprite>("UI/Skin/UISprite.psd");
            if (bgSprite != null) { bg.sprite = bgSprite; bg.type = Image.Type.Sliced; }

            // Font size follows the button, so a long label on a narrow button
            // stays inside it. Ten presets is a legitimate design; ten presets
            // with the text spilling over the neighbours is not.
            int fontSize = Mathf.Clamp(Mathf.RoundToInt(Mathf.Min(width * 0.16f, height * 0.34f)), 11, 26);
            var label = Txt(go.transform, text, fontSize, new Color(0.80f, 0.84f, 0.90f),
                            Vector2.zero, new Vector2(width, height));
            label.alignment = TextAnchor.MiddleCenter;
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            Stretch(label.rectTransform);

            var btn = go.AddComponent<VR.VrTouchButton>();
            btn.hitPaddingPixels = hitPadding;
            btn.touchDepthMeters = presetTouchDepthMeters;
            btn.releaseDepthMeters = Mathf.Max(presetTouchDepthMeters * 2f, 0.08f);
            // Exposed rather than left at VrTouchButton's default, because it is
            // not independent of the touch depth: the two together decide the
            // fastest poke that can register at all (see FastestRegisteringPoke).
            btn.minPressSeconds = Mathf.Max(0f, presetMinPressSeconds);
            btn.repeatLockSeconds = Mathf.Max(0f, presetRepeatLockSeconds);
            // Every button starts quiet; EchoPresets lights the one matching the
            // car on the first frame the panel is visible.
            btn.Build(rt, bg, label, isPrimary: false);
            return btn;
        }

        // ── Helpers ─────────────────────────────────────────────────────

        /// <summary>An interactive row, deliberately taller than the artwork it
        /// contains — see BuildParameterRows.</summary>
        private static RectTransform NewRow(Transform parent, string name, Vector2 pos, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            return rt;
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
