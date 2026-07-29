using System.Collections.Generic;
using Delphi.Session;
using UnityEngine;
using UnityEngine.UI;

namespace Delphi.VR
{
    /// <summary>
    /// Shows a DelphiQuestionnaire on a world-space panel the participant
    /// answers with their fingertip, styled to match the driving-style panel.
    ///
    /// ONE SCALE PER QUESTION, SNAPPED. Each row is a VrTouchSlider in stepped
    /// mode, so a 21-point scale is a full-width track with 21 detents rather
    /// than 21 dot targets — the same ordinal answer, on a target big enough to
    /// hit from a moving seat.
    ///
    /// UNANSWERED IS A REAL STATE. Rows start with no knob showing and the
    /// Submit button disabled, because a scale pre-set to its midpoint is
    /// indistinguishable from a participant who deliberately chose the middle.
    /// The one thing this panel must never do is invent an answer.
    ///
    /// Anchored to the SEAT, like everything else the participant touches: a
    /// panel on a tracked head cannot be reached, and moves when they lean in.
    /// </summary>
    public class VrQuestionnairePanel : MonoBehaviour
    {
        [Header("Content")]
        [Tooltip("The questionnaire this panel presents. Auto-found on this " +
                 "GameObject if left empty.")]
        public DelphiQuestionnaire questionnaire;
        [Tooltip("Heading shown at the top.")]
        public string title = "How was that drive?";

        [Header("Placement (relative to the driver's seat, metres)")]
        [Tooltip("Offset from the driver's eye point. +Z is forward, +Y is up. " +
                 "Drag it in the Scene view with the handle on this component.")]
        public Vector3 localPosition = new Vector3(0f, 0.05f, 0.46f);
        [Tooltip("Panel tilt. X tips the top away from the participant, like a " +
                 "lectern; 0 stands it perfectly upright.")]
        public Vector3 localEulerAngles = new Vector3(16f, 0f, 0f);
        [Tooltip("Panel width in metres. Height follows from the fixed page " +
                 "aspect, so text is never stretched — see HeightMeters.")]
        public float widthMeters = 0.62f;

        [Header("Behaviour")]
        [Tooltip("Hide the panel automatically once submitted. Off leaves it " +
                 "up so the researcher can read the answers back.")]
        public bool hideOnSubmit = true;

        // BUTTON GEOMETRY IS EXPOSED, the rest of the layout is not.
        //
        // The panel is built in code, so nothing in it can be nudged in the
        // inspector the way an authored prefab could be. That is a fair
        // complaint, and these are the numbers it actually bites on: how big
        // the two press targets are and how forgiving they are to hit. Getting
        // them right is a matter of trying a value in the headset, so they had
        // better not need a code change each time.
        [Header("Buttons (canvas pixels; the page is 900 × 620)")]
        [Tooltip("Height of BACK and NEXT. They sit against the bottom margin, " +
                 "so a taller button grows upward and stays on the page.")]
        public float buttonHeightPixels = 80f;
        [Tooltip("Width of the BACK button.")]
        public float backWidthPixels = 240f;
        [Tooltip("Width of the NEXT / SUBMIT button.")]
        public float nextWidthPixels = 380f;
        [Tooltip("Invisible hit area around each button. The target a finger " +
                 "must hit should be bigger than the shape the eye reads.")]
        public float buttonHitPaddingPixels = 24f;
        [Tooltip("How near the panel a fingertip must come to press a button, " +
                 "in metres.")]
        public float buttonTouchDepthMeters = 0.035f;
        [Tooltip("Dead time after any button fires, in seconds. Buttons act on " +
                 "RELEASE, and this stops one poke from also catching the other " +
                 "button on the way back out. Raise it if a single poke ever " +
                 "moves two questions.")]
        public float buttonRepeatLockSeconds = 0.45f;

        // ONE QUESTION PER PAGE, so the panel is a FIXED size no matter how
        // many questions there are.
        //
        // The seven-item version as a single scrolling wall measured 0.92 m
        // tall at 0.46 m — 90 degrees of vertical view, with the top and
        // bottom rows past a seated arm's reach. Question count must not be
        // able to push a control out of reach, and paginating is the only
        // layout where it cannot.
        //
        // Public so the inspector can draw the panel at its true proportions
        // rather than at a guessed aspect — a placement preview that lies about
        // the shape is worse than none.
        public const int PixelWidth  = 900;
        public const int PixelHeight = 620;

        /// <summary>Panel height in metres, implied by the width. The page is a
        /// fixed shape, scaled uniformly, so this is not a free parameter.</summary>
        public float HeightMeters => widthMeters * PixelHeight / PixelWidth;

        /// <summary>Floor on the width, and the only one. Not a judgement about
        /// how small a panel should be — a zero or negative width collapses or
        /// mirrors the canvas, and the panel would disappear with nothing in the
        /// console to say why.</summary>
        public const float MinWidthMeters = 0.001f;

        private GameObject _root;
        private Transform _canvasT;
        private Vector3 _appliedPos, _appliedEuler;
        private float _appliedWidth = -1f;
        private Font _font;
        private VrTouchSlider _slider;
        private Text _questionTxt, _valueTxt, _lowTxt, _highTxt, _progressTxt;
        private VrTouchButton _submitBtn, _backBtn;
        private bool _submitted;
        private int _page;

        private static readonly Color Accent   = new Color(0.24f, 0.83f, 0.76f);
        private static readonly Color Muted    = new Color(0.72f, 0.77f, 0.86f);
        private static readonly Color Disabled = new Color(0.30f, 0.33f, 0.38f);

        /// <summary>True once the participant has submitted this panel.</summary>
        public bool IsSubmitted => _submitted;

        private void Awake()
        {
            if (questionnaire == null) questionnaire = GetComponent<DelphiQuestionnaire>();
            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        }

        /// <summary>Builds and shows the panel, clearing previous answers and
        /// returning to the first question.</summary>
        public void Show()
        {
            if (questionnaire == null)
            {
                Debug.LogError("[VrQuestionnairePanel] No DelphiQuestionnaire assigned.", this);
                return;
            }
            if (questionnaire.questions.Count == 0)
            {
                Debug.LogError("[VrQuestionnairePanel] Questionnaire has no questions.", this);
                return;
            }

            if (_root == null) Build();
            if (_root == null) return;

            _submitted = false;
            _page = 0;
            questionnaire.ClearResponses();
            _root.SetActive(true);
            ShowPage(0);
        }

        public void Hide() { if (_root != null) _root.SetActive(false); }

        /// <summary>The transform this panel's placement is measured from.
        ///
        /// In a running session that is the rig's seat reference. OUTSIDE play
        /// mode the seat node does not exist yet — VrRig builds it in Awake —
        /// so this falls back to the participant camera, which is where the
        /// seat node will be created and therefore the honest edit-time stand-in.
        /// Returns null when there is no rig at all, and callers must cope:
        /// the panel simply is not built on desktop.</summary>
        public Transform ResolveAnchor()
        {
            var rig = VrRig.Instance != null ? VrRig.Instance : FindAnyObjectByType<VrRig>();
            if (rig == null) return null;
            if (rig.IsActive && rig.SeatReference != null) return rig.SeatReference;
            return rig.participantCamera != null ? rig.participantCamera.transform : null;
        }

        private void Update()
        {
            if (_root == null || !_root.activeSelf) return;
            ApplyPlacement();
            if (!_submitted) RefreshButtons();
        }

        /// <summary>Pushes the placement fields onto the built hierarchy.
        ///
        /// Re-applied every frame (guarded by a change check) rather than only
        /// at Build, so the Scene-view handle and the inspector move the real
        /// panel while a session is running. Getting a panel to a reachable,
        /// readable spot is a matter of centimetres, and finding those
        /// centimetres by editing numbers, restarting, and looking again is
        /// how you end up shipping whatever was tolerable on the third try.
        /// </summary>
        private void ApplyPlacement()
        {
            if (_appliedPos == localPosition && _appliedEuler == localEulerAngles &&
                Mathf.Approximately(_appliedWidth, widthMeters)) return;

            _root.transform.localPosition = localPosition;
            _root.transform.localEulerAngles = localEulerAngles;
            if (_canvasT != null)
            {
                float s = Mathf.Max(MinWidthMeters, widthMeters) / PixelWidth;
                _canvasT.localScale = new Vector3(s, s, s);
            }

            _appliedPos = localPosition;
            _appliedEuler = localEulerAngles;
            _appliedWidth = widthMeters;
        }

        private void Build()
        {
            var rig = VrRig.Instance;
            Transform mount = rig != null && rig.IsActive && rig.SeatReference != null ? rig.SeatReference : null;
            if (mount == null)
            {
                Debug.Log("[VrQuestionnairePanel] No active VR rig - panel not built. Normal on desktop.", this);
                return;
            }

            _root = new GameObject("[VR] Questionnaire - " + questionnaire.name);
            _root.transform.SetParent(mount, false);
            _root.transform.localPosition = localPosition;
            _root.transform.localEulerAngles = localEulerAngles;

            var canvasGO = new GameObject("Canvas", typeof(Canvas), typeof(GraphicRaycaster));
            canvasGO.transform.SetParent(_root.transform, false);
            _canvasT = canvasGO.transform;
            var canvas = canvasGO.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = rig.participantCamera;

            var crt = canvasGO.GetComponent<RectTransform>();
            crt.sizeDelta = new Vector2(PixelWidth, PixelHeight);
            float sc = Mathf.Max(MinWidthMeters, widthMeters) / PixelWidth;   // uniform, so nothing is squashed
            canvasGO.transform.localScale = new Vector3(sc, sc, sc);
            _appliedPos = localPosition; _appliedEuler = localEulerAngles; _appliedWidth = widthMeters;
            var root = canvasGO.transform;

            var plate = Img(root, new Color(0.055f, 0.065f, 0.085f, 0.96f));
            Stretch(plate.rectTransform);

            Txt(root, title.ToUpperInvariant(), 30, new Color(0.93f, 0.96f, 1f),
                new Vector2(44, -30), new Vector2(PixelWidth - 260, 40)).fontStyle = FontStyle.Bold;

            _progressTxt = Txt(root, "", 26, Muted, new Vector2(PixelWidth - 200, -30), new Vector2(160, 36));
            _progressTxt.alignment = TextAnchor.UpperRight;

            var rule = Img(root, new Color(Accent.r, Accent.g, Accent.b, 0.85f));
            rule.rectTransform.anchoredPosition = new Vector2(44, -84);
            rule.rectTransform.sizeDelta = new Vector2(PixelWidth - 88, 3);

            // Three lines of room, so a long item wraps instead of clipping -
            // the participant has to be able to read all of it before answering.
            _questionTxt = Txt(root, "", 34, new Color(0.93f, 0.96f, 1f),
                new Vector2(44, -120), new Vector2(PixelWidth - 88, 140));

            _valueTxt = Txt(root, "-", 44, Accent, new Vector2(44, -270), new Vector2(PixelWidth - 88, 56));
            _valueTxt.alignment = TextAnchor.MiddleCenter;

            var rowGO = new GameObject("Scale", typeof(RectTransform));
            rowGO.transform.SetParent(root, false);
            var row = rowGO.GetComponent<RectTransform>();
            row.anchorMin = row.anchorMax = new Vector2(0, 1);
            row.pivot = new Vector2(0, 1);
            row.anchoredPosition = new Vector2(44, -336);
            row.sizeDelta = new Vector2(PixelWidth - 88, 96);

            _slider = rowGO.AddComponent<VrTouchSlider>();
            _slider.steps = 21;                      // replaced per page in ShowPage
            _slider.Build(row, _font);
            _slider.BindValueLabel(_valueTxt);
            _slider.onValueChanged.AddListener(OnScaleChanged);

            _lowTxt = Txt(root, "", 22, Muted, new Vector2(44, -444), new Vector2((PixelWidth - 88) * 0.5f, 30));
            _highTxt = Txt(root, "", 22, Muted, new Vector2(44 + (PixelWidth - 88) * 0.5f, -444), new Vector2((PixelWidth - 88) * 0.5f, 30));
            _highTxt.alignment = TextAnchor.UpperRight;

            // Both buttons sit against the bottom margin, so changing their
            // height in the inspector grows them upward instead of pushing
            // them off the page.
            float bh = Mathf.Max(24f, buttonHeightPixels);
            float by = -(PixelHeight - 30f - bh);
            _backBtn = BuildButton(root, new Vector2(44, by), Mathf.Max(60f, backWidthPixels), bh,
                                   "BACK", primary: false, GoBack);
            _submitBtn = BuildButton(root, new Vector2(PixelWidth - 44 - Mathf.Max(60f, nextWidthPixels), by),
                                     Mathf.Max(60f, nextWidthPixels), bh,
                                     "NEXT", primary: true, GoNext);

            _root.SetActive(false);
        }

        private VrTouchButton BuildButton(Transform parent, Vector2 pos, float width, float height,
                                          string text, bool primary, UnityEngine.Events.UnityAction onPress)
        {
            var go = new GameObject("Btn_" + text, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = pos;
            rt.sizeDelta = new Vector2(width, height);

            var bg = Img(go.transform, Disabled);
            Stretch(bg.rectTransform);
            var label = Txt(go.transform, text, 28, new Color(0.80f, 0.84f, 0.90f), Vector2.zero,
                            new Vector2(width, height));
            label.alignment = TextAnchor.MiddleCenter;
            Stretch(label.rectTransform);

            // A real press control, NOT a two-step slider. The slider version
            // only fired on a value CHANGE, which made the left half of every
            // button inert and the right half work every other touch — see
            // VrTouchButton for the full account.
            var btn = go.AddComponent<VrTouchButton>();
            btn.hitPaddingPixels = buttonHitPaddingPixels;
            btn.touchDepthMeters = buttonTouchDepthMeters;
            btn.releaseDepthMeters = Mathf.Max(buttonTouchDepthMeters * 2f, 0.08f);
            btn.repeatLockSeconds = buttonRepeatLockSeconds;
            btn.Build(rt, bg, label, primary);
            btn.onPressed.AddListener(onPress);
            return btn;
        }

        private void ShowPage(int index)
        {
            var qs = questionnaire.questions;
            _page = Mathf.Clamp(index, 0, qs.Count - 1);
            var q = qs[_page];

            _questionTxt.text = q.text;
            _lowTxt.text = q.lowLabel;
            _highTxt.text = q.highLabel;
            _progressTxt.text = (_page + 1) + " / " + qs.Count;

            // The finger that turned this page is still in front of the panel.
            // Without this it counts as a drag already in progress and answers
            // the question the participant has not read yet.
            _slider.ForceRelease();

            _slider.steps = Mathf.Max(2, q.steps);
            // Restore an answer when paging back; otherwise show nothing, so an
            // untouched scale can never look like a deliberate midpoint.
            if (q.Answered) _slider.SetValue((q.response - 1f) / (q.steps - 1f), false);
            else _slider.SetUnanswered();

            RefreshButtons();
        }

        private void OnScaleChanged(float v)
        {
            var q = questionnaire.questions[_page];
            q.response = Mathf.RoundToInt(v * (q.steps - 1)) + 1;
            RefreshButtons();
        }

        private void GoNext()
        {
            if (!questionnaire.questions[_page].Answered) return;
            if (_page < questionnaire.questions.Count - 1) ShowPage(_page + 1);
            else TrySubmit();
        }

        private void GoBack()
        {
            if (_page > 0) ShowPage(_page - 1);
        }

        /// <summary>Keeps the two buttons' live/dead state in step with the
        /// page. The COLOURS are the button's own business — it has states this
        /// does not know about (hover, press flash), and a panel that also
        /// wrote colours would fight it. All this decides is what is pressable:
        /// NEXT is dead, and stays grey even under a finger, until this page
        /// actually holds an answer.</summary>
        private void RefreshButtons()
        {
            bool answered = questionnaire.questions[_page].Answered;
            bool last = _page == questionnaire.questions.Count - 1;

            _submitBtn.SetLabel(last ? "SUBMIT" : "NEXT");
            _submitBtn.interactable = answered;
            _backBtn.interactable = _page > 0;
        }

        private void TrySubmit()
        {
            if (_submitted || questionnaire == null) return;
            if (!questionnaire.AllAnswered)
            {
                // Only reachable if a page was somehow skipped. Jump to the
                // offender rather than submitting a midpoint on their behalf.
                for (int i = 0; i < questionnaire.questions.Count; i++)
                    if (!questionnaire.questions[i].Answered) { ShowPage(i); return; }
                return;
            }

            _submitted = true;
            var values = questionnaire.Submit();
            Debug.Log("[VrQuestionnairePanel] Submitted " + values.Count + " answers from '"
                      + questionnaire.name + "'.", this);
            if (hideOnSubmit) Hide();
        }

        private static Image Img(Transform parent, Color c)
        {
            var go = new GameObject("Img", typeof(Image));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.color = c;
            img.raycastTarget = false;
            var rt = img.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            return img;
        }

        private Text Txt(Transform parent, string content, int size, Color color, Vector2 pos, Vector2 sizeDelta)
        {
            var go = new GameObject("Text", typeof(Text));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<Text>();
            t.font = _font; t.fontSize = size; t.color = color; t.text = content;
            t.alignment = TextAnchor.UpperLeft;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.raycastTarget = false;
            var rt = t.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = pos; rt.sizeDelta = sizeDelta;
            return t;
        }

        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
        }
    }
}
