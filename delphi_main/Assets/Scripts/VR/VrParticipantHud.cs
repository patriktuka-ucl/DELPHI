using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Delphi.VR
{
    /// <summary>
    /// A small debug readout for whoever is wearing the headset — NOT the
    /// experimenter dashboard.
    ///
    /// The full dashboard has no business in a participant's view. It is
    /// dense, it is researcher-facing, and putting it in front of somebody
    /// being measured changes what they are doing. This shows three things
    /// only: live sensor values, which counterbalancing order is loaded, and
    /// what phase the session is in.
    ///
    /// IT IS OFF BY DEFAULT AND IT IS A DEBUG TOOL. During a real session it
    /// should stay hidden — a participant watching their own heart rate is no
    /// longer a naive observer of their own arousal, which is exactly the
    /// thing the study is measuring. It exists so that solo testing does not
    /// require taking the headset off to see whether a sensor is alive.
    ///
    /// Anchored to the SEAT, not the head, for the same reason as everything
    /// else here: a panel welded to the eyes is both unreadable and
    /// nauseating. It sits off to one side so it is glanced at, not stared
    /// through.
    /// </summary>
    [DefaultExecutionOrder(150)] // after DelphiManager (-1000) has sampled this frame
    public class VrParticipantHud : MonoBehaviour
    {
        [Header("Links (auto-found if empty)")]
        public DelphiManager manager;
        public Delphi.Session.SessionController session;

        [Header("Visibility")]
        [Tooltip("Show at startup. Leave OFF for real sessions — see the class " +
                 "summary for why a participant should not be reading their " +
                 "own physiology.")]
        public bool visibleOnStart = false;
        [Tooltip("Toggles the readout. F6 to stay clear of VrRig (F11/F12), " +
                 "VarjoGazeConnection (F9/F10) and the dashboard panel (F7/F8).")]
        public Key toggleKey = Key.F6;

        [Header("Placement (relative to the driver's seat, metres)")]
        [Tooltip("Off to the side by default, so it is glanced at rather than " +
                 "read through while driving.")]
        public Vector3 localPosition = new Vector3(0.45f, -0.10f, 0.75f);
        public Vector3 localEulerAngles = new Vector3(0f, -28f, 0f);
        public Vector2 sizeMeters = new Vector2(0.34f, 0.30f);

        [Header("Refresh")]
        [Tooltip("Redraws per second. The text is rebuilt from scratch each " +
                 "time, so there is no reason to do it every frame.")]
        [Range(1f, 30f)] public float refreshHz = 6f;

        private const int PixelWidth = 480;
        private const int PixelHeight = 424;

        private GameObject _root;
        private Text _body;
        private float _nextRefresh;
        private readonly StringBuilder _sb = new();

        private void Start()
        {
            if (manager == null) manager = FindAnyObjectByType<DelphiManager>();
            if (session == null) session = FindAnyObjectByType<Delphi.Session.SessionController>();

            var rig = VrRig.Instance;
            if (rig == null || !rig.IsActive || rig.SeatReference == null)
            {
                Debug.Log("[VrParticipantHud] No active VR rig — participant HUD not built. " +
                          "Normal on the desktop path, where the dashboard is already on screen.", this);
                enabled = false;
                return;
            }

            Build(rig.SeatReference);
            SetVisible(visibleOnStart);
        }

        private void Update()
        {
            var kb = Keyboard.current;
            if (kb != null && kb[toggleKey].wasPressedThisFrame)
                SetVisible(_root != null && !_root.activeSelf);

            if (_root == null || !_root.activeSelf) return;
            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + 1f / Mathf.Max(1f, refreshHz);
            Refresh();
        }

        public void SetVisible(bool visible)
        {
            if (_root != null) _root.SetActive(visible);
        }

        public bool IsVisible => _root != null && _root.activeSelf;

        // ── Build ────────────────────────────────────────────────────────

        private void Build(Transform seat)
        {
            _root = new GameObject("[VR] Participant HUD");
            _root.transform.SetParent(seat, false);
            _root.transform.localPosition = localPosition;
            _root.transform.localEulerAngles = localEulerAngles;

            var canvasGO = new GameObject("Canvas", typeof(Canvas));
            canvasGO.transform.SetParent(_root.transform, false);
            var canvas = canvasGO.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = VrRig.Instance.participantCamera;

            var crt = canvasGO.GetComponent<RectTransform>();
            crt.sizeDelta = new Vector2(PixelWidth, PixelHeight);
            canvasGO.transform.localScale = new Vector3(sizeMeters.x / PixelWidth,
                                                        sizeMeters.y / PixelHeight, 1f);

            var bgGO = new GameObject("BG", typeof(Image));
            bgGO.transform.SetParent(canvasGO.transform, false);
            var bg = bgGO.GetComponent<Image>();
            bg.color = new Color(0.04f, 0.05f, 0.08f, 0.86f);
            bg.raycastTarget = false; // never intercept a poke meant for something behind it
            var brt = bg.rectTransform;
            brt.anchorMin = Vector2.zero; brt.anchorMax = Vector2.one;
            brt.offsetMin = brt.offsetMax = Vector2.zero;

            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            var bodyGO = new GameObject("Body", typeof(Text));
            bodyGO.transform.SetParent(canvasGO.transform, false);
            _body = bodyGO.GetComponent<Text>();
            _body.font = font;
            _body.fontSize = 18;
            _body.color = new Color(0.86f, 0.90f, 0.97f);
            _body.alignment = TextAnchor.UpperLeft;
            _body.horizontalOverflow = HorizontalWrapMode.Overflow;
            _body.verticalOverflow = VerticalWrapMode.Overflow;
            _body.raycastTarget = false;
            var trt = _body.rectTransform;
            trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
            trt.offsetMin = new Vector2(16, 12); trt.offsetMax = new Vector2(-16, -12);
        }

        // ── Refresh ──────────────────────────────────────────────────────

        /// <summary>Time left in the current phase, as m:ss.
        ///
        /// SessionController.PhaseSecondsRemaining returns 0 for TWO different
        /// situations — a timer that has just run out, and a phase that never
        /// had one. Several phases genuinely have no clock: AwaitingRating ends
        /// when the participant submits, FreeRoam is open-ended by design, and
        /// anything waiting on the researcher waits indefinitely. Printing
        /// "0:00" for those would be a lie that looks like a stuck timer, so
        /// they are named instead.
        ///
        /// A timer expiring also reads as zero for the moment before the phase
        /// changes, which at this refresh rate is a single frame of "—".</summary>
        private string DescribeRemaining()
        {
            if (session == null) return "";

            double left = session.PhaseSecondsRemaining;
            if (left > 0)
            {
                int total = Mathf.CeilToInt((float)left);
                return $"{total / 60:0}:{total % 60:00} left";
            }

            if (session.IsAwaitingResearcher) return "waiting for researcher";
            if (session.AwaitingBreakResume) return "break — waiting to resume";
            if (session.CurrentPhase == Delphi.Session.SessionController.Phase.FreePlay) return "open-ended";
            if (session.CurrentPhase == Delphi.Session.SessionController.Phase.Idle) return "not started";
            return "—";
        }

        private void Refresh()
        {
            _sb.Clear();

            // Experiment identity and state first: when something looks wrong
            // mid-run, "which condition am I even in" is the question that
            // gets asked before any sensor value matters.
            if (session != null)
            {
                _sb.Append("ORDER ").Append(session.orderIndex).Append("  (")
                   .Append(Delphi.Session.SessionController.DescribeOrder(session.orderIndex)).Append(")\n");

                if (session.ConditionCount > 0 && session.ConditionNumber > 0)
                    _sb.Append("COND  ").Append(session.ConditionNumber).Append('/')
                       .Append(session.ConditionCount).Append("  ")
                       .Append(session.CurrentConditionKind).Append('\n');

                _sb.Append("PHASE ").Append(session.CurrentPhase)
                   .Append("   ").Append(DescribeRemaining()).Append('\n');

                // Speed, with what is currently CAPPING it. The number alone
                // invites "why is it doing 30?"; the limiter answers it — a
                // corner, a red light, or simply cruise.
                if (session.carDriver != null)
                    _sb.Append("SPEED ").Append(session.carDriver.CurrentSpeedKmh.ToString("0"))
                       .Append(" km/h  (target ").Append(session.carDriver.TargetSpeedKmh.ToString("0"))
                       .Append(", ").Append(session.carDriver.Limiter).Append(")\n");
            }
            else _sb.Append("no SessionController\n");

            _sb.Append('\n');

            if (manager == null) { _body.text = _sb.Append("no DelphiManager").ToString(); return; }

            foreach (var ch in DelphiManager.AllChannels)
            {
                var status = manager.GetStatus(ch);
                if (status == ChannelStatus.NotAttached) continue; // don't list slots nobody filled

                var (label, unit) = DelphiManager.Meta(ch);
                _sb.Append(label).Append("  ");

                if (status == ChannelStatus.Live && manager.HasData(ch))
                    _sb.Append(manager.GetValue(ch).ToString("0.##")).Append(' ').Append(unit);
                else
                    _sb.Append(status == ChannelStatus.Disabled ? "off" : "no signal");

                _sb.Append('\n');
            }

            _body.text = _sb.ToString();
        }
    }
}
