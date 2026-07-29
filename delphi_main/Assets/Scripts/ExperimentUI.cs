using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using Delphi.Session;
using Delphi.Simulation;
using Delphi.Motion;

namespace Delphi
{
    /// <summary>
    /// THE researcher control surface — one component, one canvas, everything.
    /// Replaces the old DashboardUI + trial + session + transport quartet, which
    /// had grown into overlapping panels and (worse) two different "start"
    /// buttons. There is now a SINGLE entry point: START SESSION. The session
    /// owns the trials — starting a session walks the phases and runs each
    /// condition's optimization itself; the trial is shown read-only, never
    /// started separately.
    ///
    /// Layout on the dashboard display:
    ///   • header           — title + one-line session status
    ///   • left column      — SESSION card (start / live control + emergency
    ///                        stop), TRIAL readout (optimizer + params, live),
    ///                        EVENT LOG (phase/iteration/recording feed)
    ///   • centre/right     — SENSOR grid (live waveforms with the trial's
    ///                        baseline±k·SD band + CLIP flag, and video feeds)
    ///   • bottom bar       — RECORD + PLAYBACK transport (browser, scrub, speed)
    ///
    /// Pure view: reads the managers' public state each frame and calls their
    /// public methods on click. All links auto-resolve, so the component can be
    /// dropped on any GameObject with no wiring.
    /// </summary>
    [DefaultExecutionOrder(100)] // after DelphiManager (-1000) has sampled this frame
    public class ExperimentUI : MonoBehaviour
    {
        [Header("Links (all auto-found if empty)")]
        public DelphiManager manager;
        public SessionController session;
        public SessionRecorder recorder;
        public SessionPlayer player;

        [Header("Display")]
        [Tooltip("0 = Display 1 (participant), 1 = Display 2 (dashboard).")]
        public int dashboardDisplay = 1;
        [Tooltip("EDITOR-ONLY nudge if the Display-2 preview is letterboxed and " +
                 "the dashboard looks off-centre. Set back to 0 for a real build.")]
        public Vector2 editorPreviewOffset = Vector2.zero;

        [Header("Redraw (view only; never affects sampling/recording)")]
        [Tooltip("Dashboard repaint rate in frames per second. Purely cosmetic — " +
                 "sensor sampling and recording rates live on DelphiManager.")]
        [Min(1f)] public float redrawFps = 10f;
        private float RedrawInterval => 1f / Mathf.Max(1f, redrawFps);

        [Header("Overhead camera view — live, drag while in Play mode")]
        public OverviewIndicators overviewIndicators;
        [Tooltip("Diameter (m) of each event marker ball in the overhead " +
                 "view. The car's own ball is drawn 1.4× this, same ratio " +
                 "as today's look — so this alone keeps them proportional.")]
        [Range(20f, 300f)] public float overheadBallSize = 100f;
        [Tooltip("Width (m) of the route line in the overhead view.")]
        [Range(1f, 40f)] public float overheadRouteWidth = 10f;
        private float _lastOverheadBallSize = -1f, _lastOverheadRouteWidth = -1f;

        [Header("Sensor grid — camera feeds, live, drag while in Play mode")]
        [Tooltip("Height of each video feed, in dashboard pixels. HEIGHT is the " +
                 "size control, not width: every feed gets the same height and " +
                 "keeps its own aspect, so a 16:9 player view, a 2:1 panorama " +
                 "and an 8:3 eye pair sit on one baseline at comparable scale. " +
                 "A common width would make the wide feeds short and the tall " +
                 "ones huge, and nothing would line up.")]
        [Range(48f, 400f)] public float frameHeightPixels = 132f;
        [Tooltip("Widest a single feed may get before it would crowd its " +
                 "neighbour. When this bites, height comes down with it so the " +
                 "aspect ratio is never distorted — a stretched feed is the kind " +
                 "of thing nobody notices until they measure something off it " +
                 "months later.")]
        [Range(80f, 800f)] public float frameMaxWidthPixels = 336f;
        [Tooltip("Gap between a feed's heading block (its label and status line) " +
                 "and the image itself, in pixels.")]
        [Range(0f, 60f)] public float frameHeadingGapPixels = 0f;
        [Tooltip("Horizontal gap between camera feeds, in pixels. The feed box " +
                 "widens to match, so the cells stay inside it.")]
        [Range(0f, 80f)] public float frameGapHorizontalPixels = 14f;
        [Tooltip("Vertical gap between rows of camera feeds, in pixels.")]
        [Range(0f, 80f)] public float frameGapVerticalPixels = 12f;
        [Tooltip("How many feeds sit on a row before it wraps. The feed box " +
                 "resizes to match, so this is the only field you need to " +
                 "touch to go from one wide row to a compact block.")]
        [Range(1, 8)] public int frameColumns = 4;
        private float _lastFrameH = -1f, _lastFrameMaxW = -1f, _lastFrameHeadGap = -1f;
        private float _lastFrameGapH = -1f, _lastFrameGapV = -1f;
        private int _lastFrameCols = -1;

        // ── Palette ─────────────────────────────────────────────────────
        private readonly Color _bg      = new Color(0.055f, 0.065f, 0.09f, 1f);
        private readonly Color _card    = new Color(0.10f, 0.12f, 0.17f, 1f);
        private readonly Color _card2   = new Color(0.07f, 0.08f, 0.12f, 1f);
        private readonly Color _btn     = new Color(0.16f, 0.19f, 0.26f, 1f);
        private readonly Color _btnSel  = new Color(0.16f, 0.55f, 0.40f, 1f);
        private readonly Color _accent  = new Color32(70, 220, 160, 255);
        private readonly Color _running = new Color32(80, 160, 235, 255);
        private readonly Color _estop   = new Color(0.85f, 0.22f, 0.22f, 1f);
        private readonly Color _recRed  = new Color(0.85f, 0.20f, 0.20f);
        private readonly Color _dim     = new Color(0.58f, 0.61f, 0.68f);
        private readonly Color _done    = new Color32(70, 200, 150, 255);
        private readonly Color _pending = new Color(0.45f, 0.47f, 0.53f);
        private readonly Color _text    = new Color(0.86f, 0.90f, 0.98f);
        private readonly Color32 _wave      = new Color32(70, 220, 160, 255);
        private readonly Color32 _wavePlay  = new Color32(80, 160, 235, 255);
        private readonly Color32 _gridLine  = new Color32(38, 42, 54, 255);
        private readonly Color32 _panelBg   = new Color32(18, 20, 28, 255);
        private readonly Color32 _boundBand = new Color32(34, 52, 70, 255);
        private readonly Color32 _boundEdge = new Color32(90, 130, 170, 255);
        private readonly Color32 _clip      = new Color32(240, 150, 60, 255);
        private readonly Color   _clipText  = new Color(0.96f, 0.62f, 0.25f);

        private Font _font;
        private float _redrawTimer;

        private void Start()
        {
            if (manager == null)  manager  = FindFirstObjectByType<DelphiManager>();
            if (session == null)  session  = FindFirstObjectByType<SessionController>();
            if (recorder == null) recorder = FindFirstObjectByType<SessionRecorder>();
            if (player == null)   player   = FindFirstObjectByType<SessionPlayer>();
            if (overviewIndicators == null) overviewIndicators = FindFirstObjectByType<OverviewIndicators>();
            if (manager == null) { Debug.LogError("[ExperimentUI] No DelphiManager."); enabled = false; return; }

            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
#if !UNITY_EDITOR
            if (dashboardDisplay >= 0 && dashboardDisplay < Display.displays.Length)
                Display.displays[dashboardDisplay].Activate();
#endif
            BuildClearCamera();
            BuildUI();
            RefreshSessions();
            Log("Experiment UI ready");
        }

        // ══ Per-frame ════════════════════════════════════════════════════
        private void Update()
        {
            HandleKeyboard();
            RefreshTimeline();
            RefreshSessionCard();
            RefreshTrialCard();
            RefreshBaselineCard();
            RefreshForcesGizmo();
            RefreshConnectionsCard();
            RefreshTransport();
            RefreshOverheadTuning();
            RefreshFrameTuning();
            PollEvents();

            _redrawTimer += Time.deltaTime;
            if (_redrawTimer >= RedrawInterval) { _redrawTimer = 0f; RefreshSensors(); }
        }

        // ════════════════════════════════════════════════════════════════
        //  SESSION CARD
        // ════════════════════════════════════════════════════════════════
        private GameObject _preGroup, _liveGroup;
        private Image _startImg;
        private Image[] _orderImg = new Image[SessionController.OrderCount];
        private Text _orderPreviewTxt;
        private InputField _userIdField;
        private Text _phaseTxt, _statusTxt;
        private Text[] _condLabel = new Text[3];
        private Text[] _condTime  = new Text[3];
        private Slider _condBar;
        private Button _qDoneBtn, _breakBtn, _continueBtn, _resumeBreakBtn,
                       _endFreePlayBtn, _nudgeBtn, _estopBtn, _estopResumeBtn;
        private Text _estopBanner;

        /// <summary>Researcher-facing phase name — underscored words, not the
        /// raw enum run together (e.g. not "WAITINGFORPARAMETERS"). Wording
        /// agreed with the researcher directly, not just a mechanical spacing
        /// of the C# name:
        ///   - Washout → PARAMETER_TRANSITION (that's literally what it is:
        ///     the ramp into the new parameter set settling, plus physiology
        ///     catching up, before Measuring starts)
        ///   - AwaitingRating → TRIAL_EVALUATION (the participant's rating of
        ///     ONE iteration/trial's parameter set — happens up to `iterations`
        ///     times per condition, feeds the optimizer directly)
        ///   - Questionnaire → CONDITION_EVALUATION (the post-drive trust/
        ///     predictability/safety/comfort form — happens ONCE per
        ///     condition, purely recorded, never touches the optimizer)
        ///   - FreePlay → FREE_ROAM (matches ConditionKind.FreeRoam, the name
        ///     used everywhere else in this UI — FreePlay is only the
        ///     internal C# name). No optimizer runs during it at all — the
        ///     participant adjusts the sliders directly — so unlike Implicit/
        ///     Explicit there's no PARAMETER_TRANSITION/MEASURING equivalent
        ///     for it to go through; unlike them, deliberately.
        /// Idle isn't here — RefreshSessionCard shows "IDLE" directly, since
        /// CanStart covers both Phase.Idle and Phase.Complete.</summary>
        private static string PhaseLabel(SessionController.Phase phase) => phase switch
        {
            SessionController.Phase.Intro                 => "INTRO",
            SessionController.Phase.Meditation             => "MEDITATION",
            SessionController.Phase.ConditionIntro         => "CONDITION_INTRO",
            SessionController.Phase.WaitingForOptimizer    => "WAITING_FOR_OPTIMIZER",
            SessionController.Phase.WaitingForParameters   => "WAITING_FOR_PARAMETERS",
            SessionController.Phase.Washout                => "PARAMETER_TRANSITION",
            SessionController.Phase.Measuring              => "MEASURING",
            SessionController.Phase.AwaitingRating         => "TRIAL_EVALUATION",
            SessionController.Phase.Questionnaire          => "CONDITION_EVALUATION",
            SessionController.Phase.BreakOffer             => "BREAK_OFFER",
            SessionController.Phase.FreePlay               => "FREE_ROAM",
            SessionController.Phase.Complete               => "SESSION_COMPLETE",
            SessionController.Phase.EmergencyStop          => "EMERGENCY_STOP",
            SessionController.Phase.Error                  => "ERROR",
            _                                               => phase.ToString().ToUpperInvariant(),
        };

        private void RefreshSessionCard()
        {
            if (session == null) return;
            var phase = session.CurrentPhase;
            bool idle = session.CanStart;
            bool stopped = phase == SessionController.Phase.EmergencyStop || phase == SessionController.Phase.Error;
            bool active = !idle && !stopped;

            _preGroup.SetActive(idle);
            _liveGroup.SetActive(!idle);

            if (idle)
            {
                for (int i = 0; i < _orderImg.Length; i++)
                    _orderImg[i].color = session.orderIndex == i + 1 ? _btnSel : _btn;
                // Spell the picked order out — the number alone is easy to
                // mis-set, and this is the one setting that can't be corrected
                // after the session starts.
                _orderPreviewTxt.text = SessionController.DescribeOrder(session.orderIndex);
            }
            else
            {
                _phaseTxt.text = PhaseLabel(phase);
                _phaseTxt.color = stopped ? _estop
                                : phase == SessionController.Phase.Complete ? _accent : _running;
                double pr = session.PhaseSecondsRemaining;
                _statusTxt.text = session.StatusLine + (pr > 0 ? $"   {Mmss((float)pr)} left" : "");

                bool roaming = phase == SessionController.Phase.FreePlay;
                // A slot is "done" only once the session has actually MOVED
                // ON past it (BreakOffer between conditions, or Complete after
                // the last one) — not merely because the BO iteration loop
                // isn't running right now, which is equally true during that
                // SAME condition's own intro/meditation/questionnaire and
                // used to mark a condition "done" before it had even started.
                bool sessionMovedOn = phase == SessionController.Phase.BreakOffer
                                   || phase == SessionController.Phase.Complete;
                for (int s = 0; s < _condLabel.Length; s++)
                {
                    var kind = session.ConditionKindAt(s);
                    _condLabel[s].text = $"Cond {s + 1}: {kind}";

                    bool isThisSlot = session.ConditionNumber == s + 1;
                    bool isDone = session.ConditionNumber > s + 1 || (isThisSlot && sessionMovedOn);
                    bool isActive = isThisSlot && !isDone;

                    if (isActive)
                    {
                        _condLabel[s].color = _condTime[s].color = _running;
                        _condTime[s].text = kind == SessionController.ConditionKind.FreeRoam
                            ? (roaming ? "running · open-ended" : "starting…")
                            : session.IsRunningCondition
                                ? $"running · ~{Mmss(session.CurrentConditionSecondsRemaining())} left"
                                : "starting…";
                    }
                    else if (isDone)
                    {
                        _condLabel[s].color = _condTime[s].color = _done;
                        _condTime[s].text = "done";
                    }
                    else
                    {
                        _condLabel[s].color = _condTime[s].color = _pending;
                        _condTime[s].text = kind == SessionController.ConditionKind.FreeRoam
                            ? "pending · open-ended"
                            : $"pending · ~{Mmss(session.EstimatedConditionSeconds(kind))}";
                    }
                }
                _condBar.value = session.CurrentConditionProgress();
            }

            // Normally the PARTICIPANT ends the post-condition questionnaire —
            // its onQuestionnaireFinished advances the session on its own. This
            // button is only the fallback for when no questionnaire is linked
            // (SessionController sets IsAwaitingResearcher in exactly that
            // case), so the researcher isn't left with no way forward.
            Show(_qDoneBtn, phase == SessionController.Phase.Questionnaire && session.IsAwaitingResearcher);
            Show(_breakBtn, phase == SessionController.Phase.BreakOffer && !session.AwaitingBreakResume);
            Show(_continueBtn, phase == SessionController.Phase.BreakOffer && !session.AwaitingBreakResume);
            Show(_resumeBreakBtn, phase == SessionController.Phase.BreakOffer && session.AwaitingBreakResume);
            Show(_endFreePlayBtn, phase == SessionController.Phase.FreePlay);
            Show(_nudgeBtn, phase == SessionController.Phase.FreePlay);
            Show(_estopBtn, active);
            Show(_estopResumeBtn, stopped);
            _estopBanner.gameObject.SetActive(stopped);
        }

        // ════════════════════════════════════════════════════════════════
        //  TRIAL READOUT (read-only, always — the researcher only WATCHES
        //  these values; during Phase.FreePlay the participant controls them
        //  on their own world-space panel, see FreePlayPanel.cs)
        // ════════════════════════════════════════════════════════════════
        private Image _optDot;
        private Text _optLabel, _iterTxt, _hvTxt, _trackPosTxt;
        private static readonly string[] ParamLabels =
            { "Acceleration", "Braking", "Follow dist", "Corner spd" };
        private Slider[] _paramBars = new Slider[ParamLabels.Length];
        private Text[] _paramVals = new Text[ParamLabels.Length];

        private void RefreshTrialCard()
        {
            if (session == null) return;
            var (c, l) = session.Optimizer switch
            {
                SessionController.OptimizerStatus.Connected    => (_accent, "optimizer: connected"),
                SessionController.OptimizerStatus.Starting      => (new Color(0.85f,0.75f,0.25f), "optimizer: starting…"),
                SessionController.OptimizerStatus.Disconnected  => (_estop, "optimizer: disconnected"),
                _                                                => (_pending, "optimizer: idle")
            };
            _optDot.color = c; _optLabel.text = l; _optLabel.color = c;

            bool running = session.IsRunningCondition;
            _iterTxt.text = running ? $"iteration {session.Iteration} / {session.TotalIterations}" : "iteration — / —";
            _hvTxt.text = float.IsNaN(session.LastCoverage) ? "hypervolume  —" : $"hypervolume  {session.LastCoverage:F3}";
            _hvTxt.color = float.IsNaN(session.LastCoverage) ? _dim : _accent;

            if (session.carDriver != null && session.carDriver.track != null)
            {
                float s = session.carDriver.S;
                float total = Mathf.Max(1f, session.carDriver.track.TotalLength);
                string speed = session.carDriver.IsParked ? "parked" : $"{session.carDriver.CurrentSpeedKmh:F0} km/h";
                _trackPosTxt.text = $"Speed: {speed}    Track: {s:F0}m / {total:F0}m ({100f * s / total:F0}%)";
            }
            else _trackPosTxt.text = "Track: —";

            if (session.carDriver != null)
            {
                var p = session.carDriver.parameters;
                float[] v = { p.accelerationJerk, p.brakingJerk, p.followDistance,
                              p.corneringSpeed };
                for (int i = 0; i < ParamLabels.Length; i++)
                {
                    _paramBars[i].SetValueWithoutNotify(v[i]);
                    _paramVals[i].text = v[i].ToString("F2");
                }
            }
        }

        /// <summary>Pushes the two Inspector sliders above into
        /// OverviewIndicators — only when a value actually changed, since
        /// applying it rebuilds every marker on the overhead view (see
        /// OverviewIndicators.ApplyTuning). Drag either slider in Play mode
        /// and the overhead feed updates within a frame.</summary>
        private void RefreshOverheadTuning()
        {
            // Self-healing rather than a one-shot Start()-time find: this
            // component can end up enabled/queried before OverviewIndicators
            // is ready depending on scene load order, and a null reference
            // here would otherwise silently disable the sliders for the rest
            // of the session.
            if (overviewIndicators == null) overviewIndicators = FindFirstObjectByType<OverviewIndicators>();
            if (overviewIndicators == null) return;
            if (Mathf.Approximately(overheadBallSize, _lastOverheadBallSize) &&
                Mathf.Approximately(overheadRouteWidth, _lastOverheadRouteWidth))
                return;

            _lastOverheadBallSize = overheadBallSize;
            _lastOverheadRouteWidth = overheadRouteWidth;
            overviewIndicators.ApplyTuning(overheadBallSize * 1.4f, overheadBallSize, overheadRouteWidth);
        }

        /// <summary>Applies the camera-feed layout fields when they change, so
        /// they can be dragged in Play mode against a live feed.
        ///
        /// Rebuilt on CHANGE rather than every frame: the cell rects and the
        /// box only move when a number moves, and re-anchoring every feed at
        /// the redraw rate would dirty the canvas continuously for a layout
        /// that is identical 99.9% of the time.</summary>
        private void RefreshFrameTuning()
        {
            if (Mathf.Approximately(frameHeightPixels, _lastFrameH) &&
                Mathf.Approximately(frameMaxWidthPixels, _lastFrameMaxW) &&
                Mathf.Approximately(frameHeadingGapPixels, _lastFrameHeadGap) &&
                Mathf.Approximately(frameGapHorizontalPixels, _lastFrameGapH) &&
                Mathf.Approximately(frameGapVerticalPixels, _lastFrameGapV) &&
                frameColumns == _lastFrameCols)
                return;

            _lastFrameH = frameHeightPixels;
            _lastFrameMaxW = frameMaxWidthPixels;
            _lastFrameHeadGap = frameHeadingGapPixels;
            _lastFrameGapH = frameGapHorizontalPixels;
            _lastFrameGapV = frameGapVerticalPixels;
            _lastFrameCols = frameColumns;

            float headerH = FrameHeaderH, gap = FrameHeadGap, h = FrameCellH;
            foreach (var p in _panels)
            {
                if (!p.isFrame || p.cellRt == null) continue;
                float cw = FrameCellW;
                p.cellRt.sizeDelta = new Vector2(cw, h + headerH + gap);
                if (p.badgeBg != null)
                    p.badgeBg.rectTransform.anchoredPosition = new Vector2(cw - BadgeW, -1f);
                if (p.title != null)
                    p.title.rectTransform.sizeDelta = new Vector2(cw - BadgeW - 8f, GridTitleH);
                var irt = p.image.rectTransform;
                irt.anchoredPosition = new Vector2(0, -(headerH + gap));
                // Width is re-derived from the live texture's aspect in
                // RefreshFrame; this is the placeholder size a feed with no
                // signal keeps, which otherwise stays at the old height and
                // makes the box look wrong for the one channel that is down.
                irt.sizeDelta = new Vector2(Mathf.Min(cw, _maxFrameW), h);
            }

            // The pack signature is unchanged (the same cells are active), so
            // force the reflow that would otherwise be skipped as a no-op.
            // RelayoutGrid re-sizes both boxes and their headers on the way out.
            _lastLayoutSig = -1;
            RelayoutGrid();

            // The right column was placed once, at build, against the centre
            // column's width at THAT moment. Widen the feeds past it and they
            // slide underneath it — recoverable (turn the slider back) but
            // baffling in silence, so say which slider did it.
            if (FrameBoxW > _builtCentreColW + 0.5f)
                Debug.LogWarning($"[ExperimentUI] The camera-feed box is now {FrameBoxW:0}px wide but the " +
                                 $"dashboard reserved {_builtCentreColW:0}px for the sensor grid, so the feeds " +
                                 "are running under the Forces/Connections column. Reduce Frame Columns, " +
                                 "Frame Max Width or Frame Gap Horizontal.", this);
        }

        // ════════════════════════════════════════════════════════════════
        //  SENSOR GRID (waveforms + video + trial bounds overlay)
        // ════════════════════════════════════════════════════════════════
        private class Panel
        {
            public bool isFrame;
            public Channel channel;
            public FrameChannel frameChannel;
            public GameObject cell;        // the whole grid cell (shown/hidden + moved)
            public RectTransform cellRt;
            public Text title, value;
            // Frame cells only: the status pill that sits on the heading line
            // beside the label, in place of a scalar cell's value row.
            public Image badgeBg, badgeDot;
            public Text badgeTxt;
            public RawImage image;
            public Texture2D tex;
            public Color32[] buffer;
            public float[] history;
            public bool hasBounds;
            public float lower, upper;
        }
        private readonly List<Panel> _panels = new();
        private int _cellW = 210, _cellH = 54;

        // Frame cells are taller than scalar cells: a waveform reads fine in a
        // 54 px strip, a camera feed does not. The video height is authored on
        // the component rather than taken from _cellH so the two can diverge
        // without disturbing the scalar grid's row maths.
        private float FrameCellH => Mathf.Max(24f, frameHeightPixels);
        private float _maxFrameW => Mathf.Max(FrameCellH * 0.25f, frameMaxWidthPixels);
        private float FrameHeadGap => Mathf.Max(0f, frameHeadingGapPixels);
        private float FrameGapH => Mathf.Max(0f, frameGapHorizontalPixels);
        private float FrameGapV => Mathf.Max(0f, frameGapVerticalPixels);
        private int FrameCols => Mathf.Clamp(frameColumns, 1, 8);

        /// <summary>A FRAME CELL IS AS WIDE AS A FEED CAN GET, not as wide as a
        /// scalar cell.
        ///
        /// This was the bug behind feeds hanging out of their box: cells were
        /// laid out at _cellW (210) while RefreshFrame is allowed to size an
        /// image up to frameMaxWidthPixels (336) from its own aspect. Every
        /// panorama therefore overflowed its cell, its neighbour and the card
        /// background, which had been measured for the narrow cell. Sizing the
        /// cell to the widest a feed may become makes the box, the packing and
        /// the image agree by construction.</summary>
        private float FrameCellW => Mathf.Max(_cellW, _maxFrameW);

        /// <summary>A feed's heading is ONE line — the label and its status
        /// badge side by side — where a scalar cell stacks a label over a
        /// value. A feed's status is two or three characters ("live"), so
        /// giving it a whole row of its own spent vertical space on nothing
        /// and pushed the image down for no reason.</summary>
        private float FrameHeaderH => GridTitleH;
        /// <summary>Width reserved for the status badge on the heading line.</summary>
        private const float BadgeW = 74f, BadgeH = 15f;

        // Grid layout — shared by BuildSensorGrid and RelayoutGrid so the live
        // reflow lands cells in exactly the same slots the initial build uses.
        private const int GridCols = 4;
        private const float GridX0 = 496, GridY0 = ColumnTopY, GridColGap = 14, GridRowGap = 12;
        private const float GridTitleH = 18, GridValH = 16;
        private float GridRowH => _cellH + GridTitleH + GridValH;
        /// <summary>Frame rows are taller than scalar rows, so the frame box
        /// and its packing must size off this rather than GridRowH — otherwise
        /// the box is drawn for short rows and the feeds overflow it.</summary>
        private float FrameRowH => FrameCellH + FrameHeaderH + FrameHeadGap;
        // Bit i = panel i is active. Recomputed each redraw; the grid only
        // reflows when this changes, so a steady set costs one long compare.
        private long _lastLayoutSig = -1;
        private string _lastPlaybackSig;

        private Color32 WaveColor => manager.IsInPlayback ? _wavePlay : _wave;
        private float HistoryWindow => _cellW * Mathf.Max(RedrawInterval, 0.001f);

        private void RefreshSensors()
        {
            // Show only the sensors that are actually active, packed with no
            // gaps — the grid reflows responsively as slots are toggled on/off
            // or (during playback) as the recording's channel set differs.
            RelayoutGrid();

            string sig = manager.IsInPlayback ? manager.Playback.LoadedPath : null;
            if (sig != _lastPlaybackSig) { _lastPlaybackSig = sig; foreach (var p in _panels) if (!p.isFrame) ClearHistory(p); }

            foreach (var p in _panels)
            {
                if (!p.cell.activeSelf) continue;   // hidden = inactive, skip
                if (p.isFrame) RefreshFrame(p); else RefreshScalar(p);
            }
        }

        // "Active" = a sensor is plugged in AND its toggle is on (Live or the
        // transient NoSignal). Off (Disabled) or empty (NotAttached) slots are
        // hidden. During playback we key off whether the recording HAS the
        // channel at all — stable, so a momentary NaN can't flicker a cell out.
        private bool IsPanelActive(Panel p)
        {
            if (manager.IsInPlayback)
                return p.isFrame ? manager.Playback.HasFrame(p.frameChannel)
                                 : manager.Playback.HasChannel(p.channel);
            var st = p.isFrame ? manager.GetStatus(p.frameChannel)
                               : manager.GetStatus(p.channel);
            return st == ChannelStatus.Live || st == ChannelStatus.NoSignal;
        }

        // Scalar and frame panels each pack within THEIR OWN box now (see
        // BuildSensorGrid) — local coordinates relative to that box's
        // top-left, starting just below its header.
        private void RelayoutGrid()
        {
            long sig = 0; int bit = 0;
            foreach (var p in _panels) { if (IsPanelActive(p)) sig |= 1L << bit; bit++; }
            if (sig == _lastLayoutSig) return; // set unchanged — nothing to move
            _lastLayoutSig = sig;

            _scalarRows = PackSection(isFrame: false);
            _frameRows  = PackSection(isFrame: true);
            ResizeSensorBoxes();
        }

        /// <summary>Returns how many rows the section actually packed into.</summary>
        private int PackSection(bool isFrame)
        {
            int idx = 0;
            foreach (var p in _panels)
            {
                if (p.isFrame != isFrame) continue;
                bool active = IsPanelActive(p);
                if (p.cell.activeSelf != active) p.cell.SetActive(active);
                if (!active) continue;
                int cols = isFrame ? FrameCols : GridCols;
                int col = idx % cols, row = idx / cols;
                // Frame cells carry their own gaps and column count so the
                // feeds can be spread out without loosening the scalar grid,
                // which is dense on purpose — twenty waveforms have to fit on
                // one screen.
                float rowPitch = isFrame ? FrameRowH + FrameGapV : GridRowH + GridRowGap;
                float colPitch = isFrame ? FrameCellW + FrameGapH : _cellW + GridColGap;
                p.cellRt.anchoredPosition = new Vector2(
                    SectionContentX + col * colPitch,
                    SectionContentY - row * rowPitch);
                idx++;
            }
            return Mathf.Max(1, Mathf.CeilToInt(idx / (float)(isFrame ? FrameCols : GridCols)));
        }

        /// <summary>Size each sensor box to the rows it IS SHOWING, and hang
        /// the frame box off the scalar box's live bottom edge.
        ///
        /// Both boxes used to be measured for every channel the enum defines,
        /// so a rig with six of twelve scalars plugged in drew a box with a
        /// whole empty row of background under the last waveform, and the frame
        /// box below it started that same dead row lower down. Nothing was
        /// wrong with the cells — the box was just built for a grid that wasn't
        /// there. Packing already knows the real answer (PackSection returns
        /// it), so the boxes follow it.</summary>
        private void ResizeSensorBoxes()
        {
            if (_scalarBoxRt == null || _frameBoxRt == null) return;

            float scalarH = CardHeaderH + _scalarRows * GridRowH + (_scalarRows - 1) * GridRowGap + 16f;
            _scalarBoxRt.sizeDelta = new Vector2(ScalarBoxW, scalarH);
            if (_scalarHeaderRt != null) _scalarHeaderRt.sizeDelta = new Vector2(ScalarBoxW, CardHeaderH);

            _frameBoxRt.anchoredPosition = new Vector2(GridX0, GridY0 - scalarH - 16f);
            _frameBoxRt.sizeDelta = new Vector2(FrameBoxW, FrameBoxH);
            if (_frameHeaderRt != null) _frameHeaderRt.sizeDelta = new Vector2(FrameBoxW, CardHeaderH);
        }

        private void RefreshScalar(Panel p)
        {
            bool has = manager.HasData(p.channel);
            float val = manager.GetValue(p.channel);
            p.hasBounds = !manager.IsInPlayback && session != null &&
                          session.TryGetBounds(p.channel, out p.lower, out p.upper);
            bool clipped = p.hasBounds && has && (val < p.lower || val > p.upper);

            if (manager.IsInPlayback) manager.Playback.FillHistory(p.channel, p.history, HistoryWindow);
            else { Array.Copy(p.history, 1, p.history, 0, _cellW - 1); p.history[_cellW - 1] = has ? val : float.NaN; }
            RedrawWave(p);

            var (_, unit) = DelphiManager.Meta(p.channel);
            if (has) { p.value.text = $"{val:F1} {unit}{(clipped ? "  CLIP" : "")}".TrimEnd(); p.value.color = clipped ? _clipText : (Color)WaveColor; }
            else
            {
                (p.value.text, p.value.color) = manager.GetStatus(p.channel) switch
                {
                    ChannelStatus.Disabled => ("disabled", new Color(0.85f,0.75f,0.25f)),
                    ChannelStatus.NoSignal => ("no signal", _estop),
                    _                      => ("not attached", _pending)
                };
            }
        }

        private void RefreshFrame(Panel p)
        {
            if (manager.HasFrame(p.frameChannel))
            {
                var tex = manager.GetFrame(p.frameChannel);
                if (tex != null && tex.width > 0)
                {
                    p.image.texture = tex;

                    // MATCH HEIGHT, DERIVE WIDTH FROM THE SOURCE ASPECT.
                    //
                    // Every feed gets the same height and keeps its own shape,
                    // so a 16:9 player view, a 2:1 panorama and an 8:3
                    // side-by-side eye pair all sit on one baseline at
                    // genuinely comparable scale — which a common WIDTH does
                    // not give you: it makes the wide feeds short and the tall
                    // ones huge, and nothing lines up.
                    //
                    // Width is still capped, because an 8:3 pair at full cell
                    // height would otherwise run into its neighbour. When the
                    // cap bites, height comes back down with it so the aspect
                    // ratio is never distorted — a stretched feed is the kind
                    // of thing nobody notices until they measure something off
                    // it months later.
                    float aspect = (float)tex.width / Mathf.Max(1, tex.height); // w:h
                    float h = FrameCellH, w = h * aspect;
                    if (w > _maxFrameW) { w = _maxFrameW; h = w / Mathf.Max(aspect, 1e-4f); }
                    p.image.rectTransform.sizeDelta = new Vector2(w, h);
                }
                if (manager.IsInPlayback) SetBadge(p, "playback", _running);
                else                      SetBadge(p, "live", (Color)WaveColor);
            }
            else
            {
                var (label, col) = manager.GetStatus(p.frameChannel) switch
                {
                    ChannelStatus.Disabled => ("disabled", new Color(0.85f, 0.75f, 0.25f)),
                    ChannelStatus.NoSignal => ("no signal", _estop),
                    _                      => ("not attached", _pending)
                };
                SetBadge(p, label, col);
            }
        }

        /// <summary>Paints a feed's status badge: hairline border, lit dot, and
        /// the same coloured word the cell always showed.</summary>
        private static void SetBadge(Panel p, string label, Color col)
        {
            if (p.badgeTxt == null || p.badgeBg == null) return;
            if (p.badgeTxt.text != label) p.badgeTxt.text = label;
            p.badgeTxt.color = col;
            p.badgeDot.color = col;
            // The border is the quiet part — at full strength it competes with
            // the dot and the word for the same small patch of screen.
            p.badgeBg.color = new Color(col.r, col.g, col.b, 0.55f);
        }

        // Procedural, not imported. Unity's built-in UI skin sprite does not
        // reliably resolve through GetBuiltinResource in this project, and a
        // missing sprite silently degrades to a hard rectangle — exactly the
        // "it looks awful" failure this artwork exists to avoid.
        private static Sprite _pillOutline, _dot;

        /// <summary>A rounded-rect RING, 9-sliced so it keeps a constant border
        /// weight and corner radius at whatever size the badge is drawn.</summary>
        private static Sprite PillOutlineSprite
        {
            get
            {
                if (_pillOutline != null) return _pillOutline;
                const int S = 32, R = 15;
                const float Border = 1.4f;
                var px = new Color[S * S];
                for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float dx = Mathf.Max(R - x - 0.5f, 0f, x + 0.5f - (S - R));
                    float dy = Mathf.Max(R - y - 0.5f, 0f, y + 0.5f - (S - R));
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    // Outer coverage minus inner coverage = the ring between them.
                    float outer = Mathf.Clamp01(R - d + 0.5f);
                    float inner = Mathf.Clamp01(R - Border - d + 0.5f);
                    px[y * S + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(outer - inner));
                }
                _pillOutline = MakeSprite(px, S, new Vector4(R, R, R, R));
                return _pillOutline;
            }
        }

        private static Sprite DotSprite
        {
            get
            {
                if (_dot != null) return _dot;
                const int S = 24;
                float r = S * 0.5f, c = r - 0.5f;
                var px = new Color[S * S];
                for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                    px[y * S + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(r - d));
                }
                _dot = MakeSprite(px, S, Vector4.zero);
                return _dot;
            }
        }

        private static Sprite MakeSprite(Color[] px, int size, Vector4 border)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };
            tex.SetPixels(px); tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f),
                                 100f, 0, SpriteMeshType.FullRect, border);
        }

        private void RedrawWave(Panel p)
        {
            int w = _cellW, h = _cellH;
            for (int i = 0; i < p.buffer.Length; i++) p.buffer[i] = _panelBg;

            float mn = float.MaxValue, mx = float.MinValue; bool any = false;
            for (int x = 0; x < w; x++) { float v = p.history[x]; if (float.IsNaN(v)) continue; any = true; if (v < mn) mn = v; if (v > mx) mx = v; }
            if (p.hasBounds) { if (!any) { mn = p.lower; mx = p.upper; } else { if (p.lower < mn) mn = p.lower; if (p.upper > mx) mx = p.upper; } }
            if (!any && !p.hasBounds) { int m = h / 2; for (int x = 0; x < w; x++) p.buffer[m * w + x] = _gridLine; Apply(p); return; }

            float range = Mathf.Max(mx - mn, 1e-3f); float pad = range * 0.12f; mn -= pad; range += pad * 2f;
            int Y(float v) => Mathf.Clamp(Mathf.RoundToInt((v - mn) / range * (h - 1)), 0, h - 1);

            if (p.hasBounds)
            {
                int lo = Y(p.lower), hi = Y(p.upper);
                for (int y = lo; y <= hi; y++) for (int x = 0; x < w; x++) p.buffer[y * w + x] = _boundBand;
                for (int x = 0; x < w; x++) { p.buffer[lo * w + x] = _boundEdge; p.buffer[hi * w + x] = _boundEdge; }
                int b = Y((p.lower + p.upper) * 0.5f); for (int x = 0; x < w; x++) p.buffer[b * w + x] = _gridLine;
            }
            else { int m = h / 2; for (int x = 0; x < w; x++) p.buffer[m * w + x] = _gridLine; }

            if (any)
            {
                int prev = -1;
                for (int x = 0; x < w; x++)
                {
                    float v = p.history[x]; if (float.IsNaN(v)) { prev = -1; continue; }
                    int yy = Y(v); if (prev < 0) prev = yy;
                    Color32 c = (p.hasBounds && (v < p.lower || v > p.upper)) ? _clip : WaveColor;
                    for (int k = Mathf.Min(prev, yy); k <= Mathf.Max(prev, yy); k++) p.buffer[k * w + x] = c;
                    prev = yy;
                }
            }
            Apply(p);
        }

        private void Apply(Panel p) { p.tex.SetPixels32(p.buffer); p.tex.Apply(false); }
        private void ClearHistory(Panel p) { for (int i = 0; i < p.history.Length; i++) p.history[i] = float.NaN; RedrawWave(p); }

        // ════════════════════════════════════════════════════════════════
        //  RECORD / PLAYBACK TRANSPORT
        // ════════════════════════════════════════════════════════════════
        private static readonly float[] Speeds = { 0.25f, 0.5f, 1f, 2f, 4f };
        private InputField _nameField;
        private Text _recStatus, _sessionLabel, _timeLabel;
        private Image _recImg; private Text _recTxt, _loadTxt, _playTxt, _speedTxt;
        private Button _playBtn, _stepBackBtn, _stepFwdBtn, _speedBtn, _loadBtn;
        private Slider _scrubber; private bool _scrubbing, _wasRecording;
        private string[] _sessions = Array.Empty<string>(); private int _sessionIdx, _speedIdx = 2;

        private void RefreshSessions()
        {
            string root = recorder != null ? recorder.SessionsRoot : SessionRecorder.DefaultSessionsRoot;
            _sessions = SessionPlayer.ListSessions(root);
            _sessionIdx = Mathf.Clamp(_sessionIdx, 0, Mathf.Max(0, _sessions.Length - 1));
        }
        private void ToggleRecord() { if (recorder == null) return; if (recorder.IsRecording) recorder.StopRecording(); else recorder.StartRecording(_nameField ? _nameField.text : null); }
        private void CycleSession(int d) { RefreshSessions(); if (_sessions.Length == 0) return; _sessionIdx = (_sessionIdx + d + _sessions.Length) % _sessions.Length; }
        private void ToggleLoad() { if (player == null) return; if (player.IsLoaded) { player.Unload(); return; } RefreshSessions(); if (_sessions.Length > 0) player.Load(_sessions[_sessionIdx]); }
        private void CycleSpeed(int d) { if (player == null) return; _speedIdx = Mathf.Clamp(_speedIdx + d, 0, Speeds.Length - 1); player.SetSpeed(Speeds[_speedIdx]); }

        // Ending a session is unrecoverable, and this button sits on a rig
        // next to the recording controls — so it arms on the first click and
        // only quits on a second one within the window, rather than acting on
        // a stray click mid-condition.
        private const string QuitIdleLabel = "QUIT & SAVE";
        private const float QuitArmSeconds = 4f;
        private Image _quitImg;
        private Text _quitTxt;
        private float _quitArmedUntil;

        private void OnQuitClicked()
        {
            if (Time.unscaledTime < _quitArmedUntil)
            {
                _quitArmedUntil = 0f;
                Log("Quitting — finalising recordings, this may take a few seconds…");
                if (session != null) session.QuitSession();
                else if (recorder != null && recorder.IsRecording) recorder.StopRecording();
                return;
            }
            _quitArmedUntil = Time.unscaledTime + QuitArmSeconds;
        }

        private void RefreshQuitButton()
        {
            if (_quitTxt == null || _quitImg == null) return;
            bool armed = Time.unscaledTime < _quitArmedUntil;
            _quitTxt.text = armed ? "CONFIRM QUIT?" : QuitIdleLabel;
            _quitImg.color = armed ? _estop : _btn;
        }

        private void RefreshTransport()
        {
            RefreshQuitButton();
            bool rec = recorder != null && recorder.IsRecording;
            if (_wasRecording && !rec) { RefreshSessions(); Log("Recording stopped"); }
            if (!_wasRecording && rec) Log("Recording started");
            _wasRecording = rec;

            if (recorder != null)
            {
                _recTxt.text = rec ? "STOP" : "REC"; _recImg.color = rec ? _recRed : _btn;
                _recStatus.text = rec ? $"REC  {Mmss(recorder.ElapsedSeconds)}" : $"idle — {_sessions.Length} on disk";
                _recStatus.color = rec ? _recRed : _dim;
                if (_nameField) _nameField.interactable = !rec;
            }
            bool loaded = player != null && player.IsLoaded;
            if (player != null)
            {
                _sessionLabel.text = loaded ? $"{Path.GetFileName(player.LoadedPath)} [loaded]"
                    : _sessions.Length > 0 ? Path.GetFileName(_sessions[_sessionIdx]) : "no recordings yet";
                _sessionLabel.color = loaded ? _accent : _dim;
                _loadTxt.text = loaded ? "Eject" : "Load";
                _playTxt.text = player.IsPlaying ? "Pause" : "Play";
                _speedTxt.text = $"{Speeds[_speedIdx]:0.##}x";
                _playBtn.interactable = _stepBackBtn.interactable = _stepFwdBtn.interactable =
                    _speedBtn.interactable = _scrubber.interactable = loaded;
                if (loaded)
                {
                    _timeLabel.text = $"{Mmss(player.TimeSec)} / {Mmss(player.Duration)}";
                    if (!_scrubbing && player.Duration > 0) _scrubber.SetValueWithoutNotify(player.TimeSec / player.Duration);
                }
                else { _timeLabel.text = "--:-- / --:--"; _scrubber.SetValueWithoutNotify(0f); }
            }
        }

        /// <summary>Dead time after a stop during which F1 will not resume.
        ///
        /// The one hazard in making a single key do both: F1 is pressed in a
        /// hurry, and a hurried press is often two presses. Without this, the
        /// second one puts a participant who is mid-incident straight back onto
        /// a moving platform. A second and a half is longer than any double-tap
        /// and shorter than any deliberate decision to continue — and the
        /// RESUME button is never locked, so nothing is actually unreachable.</summary>
        private const float EstopResumeLockSeconds = 1.5f;
        private float _estopResumeLockUntil;

        private void HandleEmergencyKey()
        {
            if (session.CurrentPhase != SessionController.Phase.EmergencyStop)
            {
                session.EmergencyStop();
                _estopResumeLockUntil = Time.unscaledTime + EstopResumeLockSeconds;
                Log("EMERGENCY STOP (F1). Press F1 again to resume.");
                return;
            }

            float wait = _estopResumeLockUntil - Time.unscaledTime;
            if (wait > 0f)
                Log($"F1 ignored — resume unlocks in {wait:0.0}s, so a double-tap can't restart the rig. " +
                    "The RESUME button works now if you need it.");
            else { session.Resume(); Log("RESUMED (F1)."); }
        }

        private void HandleKeyboard()
        {
            var kb = Keyboard.current; if (kb == null) return;

            // F1 STOPS AND RESUMES, and it is handled before every other guard
            // in this method — before the "is a recording loaded" split and
            // before the text-field check.
            //
            // A safety key that only works in some UI states is not a safety
            // key, and the state it would most plausibly be blocked in (someone
            // typing a session name) is not one where the participant is any
            // less strapped to a moving platform.
            if (kb.f1Key.wasPressedThisFrame && session != null) { HandleEmergencyKey(); return; }

            if (_nameField != null && _nameField.isFocused) return;

            // SPACE does two different things, decided by whether a recording
            // is loaded. That is not a clash: with a recording loaded the
            // dashboard IS a playback transport and space is play/pause; with
            // nothing loaded there is no transport to drive, so the key is
            // free and space is the obvious "go".
            if (player == null || !player.IsLoaded)
            {
                HandleOrderKeys(kb);
                if (kb.spaceKey.wasPressedThisFrame) StartSessionFromKeyboard();

                // Debug skip. Deliberately awkward — Ctrl held, and a key
                // nowhere near the others — because skipping the meditation
                // silently invalidates the next condition's baseline, and a
                // one-key version would eventually get pressed by accident
                // with a participant in the headset.
                if (kb.backspaceKey.wasPressedThisFrame &&
                    (kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed))
                {
                    session?.DebugSkipPhase();
                    Log($"DEBUG SKIP requested (Ctrl+Backspace) during {session?.CurrentPhase}.");
                }
                return;
            }

            if (kb.spaceKey.wasPressedThisFrame) player.TogglePlay();
            if (kb.leftArrowKey.wasPressedThisFrame) player.StepFrames(-1);
            if (kb.rightArrowKey.wasPressedThisFrame) player.StepFrames(1);
            if (kb.upArrowKey.wasPressedThisFrame) CycleSpeed(1);
            if (kb.downArrowKey.wasPressedThisFrame) CycleSpeed(-1);
        }

        /// <summary>Numpad 1–6 picks the counterbalancing order, exactly like
        /// the six order buttons above the START button.
        ///
        /// NUMPAD ONLY, not the top-row digits: those are within easy reach of
        /// a hand resting near the space bar, and this value is written into
        /// every CSV as GroupID. Silently relabelling a whole session's data is
        /// not a mistake worth making convenient.
        ///
        /// Refused once the session is running, for the same reason the
        /// buttons disappear then: the order determines the segment plan, which
        /// was already built at StartSession.</summary>
        private void HandleOrderKeys(Keyboard kb)
        {
            if (session == null) return;

            Key[] numpad =
            {
                Key.Numpad1, Key.Numpad2, Key.Numpad3,
                Key.Numpad4, Key.Numpad5, Key.Numpad6
            };

            for (int i = 0; i < numpad.Length; i++)
            {
                if (!kb[numpad[i]].wasPressedThisFrame) continue;
                int order = i + 1;

                if (session.CurrentPhase != SessionController.Phase.Idle)
                {
                    Log($"Order {order} ignored — session already running ({session.CurrentPhase}).");
                    return;
                }

                session.orderIndex = order;
                Log($"Order {order} selected ({SessionController.DescribeOrder(order)}).");
                return;
            }
        }

        /// <summary>Spacebar equivalent of the START SESSION button.
        ///
        /// Goes through session.StartSession() exactly like the button does,
        /// rather than reaching further in, so the two routes can never drift
        /// apart. The button gets its guard for free by being hidden once the
        /// session is under way; a key press has no such protection, so the
        /// idle check below is what stops a stray space bar restarting a
        /// session that is already running — with a participant in the
        /// headset, that is not a recoverable mistake.
        ///
        /// Every outcome is logged, including the refusals, because from the
        /// researcher's side a key that silently does nothing is
        /// indistinguishable from one that is not wired up.</summary>
        private void StartSessionFromKeyboard()
        {
            if (session == null)
            {
                Log("SPACE: no SessionController in the scene.");
                return;
            }

            if (session.CurrentPhase != SessionController.Phase.Idle)
            {
                Log($"SPACE ignored — session already running ({session.CurrentPhase}).");
                return;
            }

            if (session.StartSession()) Log("Session started (spacebar).");
            else Log("SPACE: StartSession() refused — check the Session card above.");
        }

        // ════════════════════════════════════════════════════════════════
        //  EVENT LOG
        // ════════════════════════════════════════════════════════════════
        private const int LogLines = 13;
        private readonly List<string> _log = new();
        private Text _logText;
        private Text _baselineText;
        private SessionController.Phase _lastPhase = (SessionController.Phase)(-1);
        private int _lastIter = -1;

        private void Log(string line)
        {
            _log.Add($"{DateTime.Now:HH:mm:ss}  {line}");
            if (_log.Count > LogLines) _log.RemoveAt(0);
            if (_logText != null) _logText.text = string.Join("\n", _log);
        }

        private void PollEvents()
        {
            if (session != null && session.CurrentPhase != _lastPhase)
            {
                _lastPhase = session.CurrentPhase;
                Log($"→ {session.CurrentPhase}" +
                    (session.IsRunningCondition ? $" ({session.CurrentConditionKind})" : ""));
            }
            if (session != null && session.Iteration != _lastIter)
            {
                _lastIter = session.Iteration;
                if (_lastIter > 0) Log($"   iteration {_lastIter}/{session.TotalIterations} applied");
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  UI CONSTRUCTION
        // ════════════════════════════════════════════════════════════════
        /// <summary>The camera that clears the dashboard display, and the
        /// canvas the dashboard is built on. Exposed for VrDashboardPanel,
        /// which redirects both into a RenderTexture so the dashboard can be
        /// shown on a floating panel inside the headset — a second monitor is
        /// no use to somebody wearing an XR-3.</summary>
        public Camera DashboardCamera { get; private set; }
        public Canvas DashboardCanvas { get; private set; }

        private void BuildClearCamera()
        {
            var camGO = new GameObject("Dashboard Camera", typeof(Camera));
            camGO.transform.SetParent(transform, false);
            var cam = camGO.GetComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = _bg;
            cam.cullingMask = 0; cam.targetDisplay = dashboardDisplay; cam.depth = 100;
            DashboardCamera = cam;
        }

        private Transform _root;
        private void BuildUI()
        {
            var canvasGO = new GameObject("Experiment Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGO.transform.SetParent(transform, false);
            var canvas = canvasGO.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.targetDisplay = dashboardDisplay;
            var scaler = canvasGO.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = 0.5f;
            canvasGO.GetComponent<RectTransform>().anchoredPosition = editorPreviewOffset;
            DashboardCanvas = canvas;
            _root = canvasGO.transform;

            var bg = NewImage(_root, _bg); Stretch(bg.rectTransform);

            // Header
            var title = Txt(_root, "DELPHI — Experiment Control", 24, _text,
                            new Vector2(EdgeMargin, -14), new Vector2(700, 30));
            title.fontStyle = FontStyle.Bold;

            BuildTimelineCard(_root);
            BuildSessionCard(_root);
            BuildTrialCard(_root);
            BuildBaselineCard(_root);
            BuildSensorGrid(_root);
            BuildForcesGizmo(_root);
            BuildConnectionsCard(_root);
            BuildEventLog(_root);
            BuildTransport(_root);

            VerifyLayout();
        }

        /// <summary>Width the right column was actually placed against —
        /// snapshotted at build, because the feed sliders can change
        /// CentreColW afterwards and the column does not move.</summary>
        private float _builtCentreColW;

        /// <summary>Re-checks the column arithmetic against the real card
        /// heights once everything is built, and names anything that lands on
        /// top of anything else.
        ///
        /// It is here because this file has now produced the same class of bug
        /// three separate times — a card grown without moving the one below it,
        /// a box measured for a wider grid than the screen has room for, a
        /// banner positioned past the bottom of its own card. Each was invisible
        /// until it appeared on the researcher's screen mid-session. The
        /// constants are all derived from each other now, which prevents most
        /// of it; this catches the rest at startup, in the log, where it costs
        /// nothing to read.</summary>
        private void VerifyLayout()
        {
            _builtCentreColW = CentreColW;

            void Check(string what, float bottomY)
            {
                if (bottomY >= TransportTopY) return;
                Debug.LogWarning($"[ExperimentUI] The {what} column ends at y={bottomY:0}, past the transport " +
                                 $"bar's top edge ({TransportTopY:0}) — its bottom card is drawing under the " +
                                 "record/playback controls.", this);
            }

            Check("left", BaselineCardY - BaselineCardH);
            Check("right", GridY0 - GizmoCardH - 12 - ConnectionsCardH - 12 - EventLogCardH);
            // The centre column is the one that moves at runtime (feed sliders),
            // so it's measured from the live boxes rather than from constants.
            Check("centre", _frameBoxRt != null
                ? _frameBoxRt.anchoredPosition.y - _frameBoxRt.sizeDelta.y
                : TransportTopY);

            if (RightColumnX + RightColW > CanvasW - EdgeMargin + 0.5f)
                Debug.LogWarning($"[ExperimentUI] The right column runs to x={RightColumnX + RightColW:0} on a " +
                                 $"{CanvasW:0}px dashboard — the sensor grid is too wide to leave it room. " +
                                 "Reduce Frame Columns or Frame Max Width.", this);
        }

        // ════════════════════════════════════════════════════════════════
        //  TIMELINE — the whole session as the five things it's actually
        //  made of: intro, the three conditions in this participant's
        //  counterbalancing order, end.
        //
        //  It answers two questions the rest of the dashboard could only
        //  answer piecemeal: WHERE ARE WE (one rail, filled by estimated
        //  time rather than by stop count, so a two-minute intro doesn't
        //  read as a fifth of the protocol), and CAN WE GO SOMEWHERE ELSE
        //  (click a stop). Jumping is genuinely destructive — see
        //  SessionController.JumpToStop — so every stop except "start at
        //  the beginning while idle" arms on the first click and only acts
        //  on a second one, the same way QUIT & SAVE does.
        //
        //  The stops come from SessionController.Timeline, which is derived
        //  from the same plan the session actually walks. Nothing here
        //  duplicates the protocol — a plan change shows up in this strip
        //  on its own.
        // ════════════════════════════════════════════════════════════════
        private const float TimelineX = EdgeMargin, TimelineY = -52f, TimelineH = 96f;
        private const float TimelineW = CanvasW - 2f * EdgeMargin;
        private const float TimelineSideMargin = 16f;
        private const float TimelineRailY = -58f;   // rail centreline, from the card's top
        private const float TimelinePillH = 30f, TimelineRailThickness = 3f;
        /// <summary>How long a stop stays armed after the first click. Same
        /// four seconds as QUIT & SAVE: longer than any double-click, shorter
        /// than a change of mind.</summary>
        private const float StopArmSeconds = 4f;

        private class StopNode
        {
            public Image pill, fill;       // fill is the per-stop progress inside the pill
            public RectTransform fillRt;
            public Text label, status;
            public float pillW;
        }
        private readonly List<StopNode> _stops = new();
        private RectTransform _railFillRt;
        private float _railLen;
        private Text _timelineSummary;
        private int _armedStop = -1;
        private float _armedUntil;

        private void BuildTimelineCard(Transform root)
        {
            var card = Card(root, "Session Timeline — click a stop to start or jump there",
                            new Vector2(TimelineX, TimelineY), new Vector2(TimelineW, TimelineH));
            var host = card.transform;

            _timelineSummary = Txt(host, "", 13, _dim,
                                   new Vector2(TimelineW - 16f - 620f, -8f), new Vector2(620, 20));
            _timelineSummary.alignment = TextAnchor.UpperRight;

            // The stop COUNT is fixed by the protocol (intro + three
            // conditions + end) and identical for all six orders, so the
            // widgets are built once here; only their labels change with the
            // order, in RefreshTimeline.
            int n = session != null ? session.Timeline.Count : 0;
            if (n <= 0) { Debug.LogWarning("[ExperimentUI] No session timeline to draw."); return; }

            float slotW = (TimelineW - 2f * TimelineSideMargin) / n;
            float railX0 = TimelineSideMargin + slotW * 0.5f;
            _railLen = slotW * (n - 1);

            // Rail first, so every pill draws over it.
            var rail = NewImage(host, _card2);
            rail.rectTransform.anchoredPosition = new Vector2(railX0, TimelineRailY + TimelineRailThickness * 0.5f);
            rail.rectTransform.sizeDelta = new Vector2(_railLen, TimelineRailThickness);
            rail.raycastTarget = false;

            var railFill = NewImage(host, _accent);
            _railFillRt = railFill.rectTransform;
            _railFillRt.anchoredPosition = rail.rectTransform.anchoredPosition;
            _railFillRt.sizeDelta = new Vector2(0f, TimelineRailThickness);
            railFill.raycastTarget = false;

            float pillW = Mathf.Min(slotW - 26f, 240f);
            for (int i = 0; i < n; i++)
            {
                int stop = i;               // capture per iteration, not the loop variable
                float cx = TimelineSideMargin + slotW * (i + 0.5f);
                var node = new StopNode { pillW = pillW };

                node.pill = Btn(host, "", new Vector2(cx - pillW * 0.5f, TimelineRailY + TimelinePillH * 0.5f),
                                new Vector2(pillW, TimelinePillH), () => OnStopClicked(stop), out node.label);
                node.pill.color = _card2;

                // The progress fill lives INSIDE the pill and is pushed behind
                // the label — a stop that is half done is half filled, which is
                // the same information the rail carries, at the resolution the
                // researcher actually asks about ("how far into condition 2").
                node.fill = NewImage(node.pill.transform, _running);
                node.fill.transform.SetAsFirstSibling();
                node.fill.raycastTarget = false;
                node.fillRt = node.fill.rectTransform;
                node.fillRt.anchoredPosition = Vector2.zero;
                node.fillRt.sizeDelta = new Vector2(0f, TimelinePillH);

                node.status = Txt(host, "", 11, _pending,
                                  new Vector2(cx - slotW * 0.5f, TimelineRailY - TimelinePillH * 0.5f - 4f),
                                  new Vector2(slotW, 16));
                node.status.alignment = TextAnchor.UpperCenter;

                _stops.Add(node);
            }
        }

        /// <summary>First click arms, second click acts — except for the one
        /// case that discards nothing: starting at stop 0 from idle, which is
        /// exactly what the START SESSION button already does in one click.</summary>
        private void OnStopClicked(int i)
        {
            if (session == null) return;
            var stops = session.Timeline;
            if (i < 0 || i >= stops.Count) return;

            bool idle = session.CanStart;
            string label = stops[i].label;
            bool needsConfirm = !(idle && i == 0);

            if (needsConfirm && !(_armedStop == i && Time.unscaledTime < _armedUntil))
            {
                _armedStop = i;
                _armedUntil = Time.unscaledTime + StopArmSeconds;
                Log(idle ? $"Click {label} again to START there (skips everything before it)."
                         : $"Click {label} again to JUMP there (ends this condition; its data is partial).");
                return;
            }

            _armedStop = -1;
            _armedUntil = 0f;
            if (session.JumpToStop(i)) Log($"→ {label}" + (idle ? " (session started here)" : " (jumped)"));
            else Log($"{label}: refused — see the console for why.");
        }

        private void RefreshTimeline()
        {
            if (session == null || _stops.Count == 0) return;

            var stops = session.Timeline;
            int cur = session.CurrentStopIndex;
            float progress = session.SessionProgress01;

            _railFillRt.sizeDelta = new Vector2(_railLen * progress, TimelineRailThickness);

            float est = session.EstimatedSessionSeconds;
            double elapsed = session.SessionElapsedSeconds;
            _timelineSummary.text = cur < 0
                ? $"not started  ·  est. {Mmss(est)} total"
                : $"{Mmss((float)elapsed)} elapsed  ·  est. {Mmss(est)} total  ·  {progress * 100f:F0}%";
            _timelineSummary.color = cur < 0 ? _dim : _accent;

            if (Time.unscaledTime >= _armedUntil) _armedStop = -1;

            for (int i = 0; i < _stops.Count; i++)
            {
                var node = _stops[i];
                bool have = i < stops.Count;
                if (node.pill.gameObject.activeSelf != have) node.pill.gameObject.SetActive(have);
                if (node.status.gameObject.activeSelf != have) node.status.gameObject.SetActive(have);
                if (!have) continue;

                var s = stops[i];
                bool done = cur >= 0 && i < cur;
                bool active = i == cur;
                Color state = done ? _done : active ? _running : _pending;

                if (_armedStop == i)
                {
                    node.pill.color = _estop;
                    node.fillRt.sizeDelta = new Vector2(0f, TimelinePillH);
                    node.label.text = session.CanStart ? "START HERE?" : "JUMP HERE?";
                    node.label.color = _text;
                }
                else
                {
                    node.pill.color = _card2;
                    node.fill.color = new Color(state.r, state.g, state.b, done || active ? 0.9f : 0.3f);
                    node.fillRt.sizeDelta = new Vector2(node.pillW * session.StopProgress01(i), TimelinePillH);
                    node.label.text = s.isCondition ? $"{s.label} · {s.detail.ToUpperInvariant()}" : s.label;
                    node.label.color = done || active ? _text : _dim;
                }

                node.status.text = StopStatus(i, s, cur);
                node.status.color = state;
            }
        }

        /// <summary>The line under each pill. For the stop that's RUNNING this
        /// is the live phase — the same PhaseLabel the session card shows —
        /// because "CONDITION 2" alone doesn't distinguish a meditation from a
        /// drive, and that distinction is most of what the researcher is
        /// watching for.</summary>
        private string StopStatus(int i, SessionController.TimelineStop s, int cur)
        {
            if (cur < 0)
                return i == 0 ? "click to start here"
                     : s.estimatedSeconds > 0 ? $"~{Mmss(s.estimatedSeconds)}" : "";
            if (i < cur) return "done";
            if (i > cur) return s.estimatedSeconds > 0 ? $"pending · ~{Mmss(s.estimatedSeconds)}" : "pending";

            var phase = session.CurrentPhase;
            if (phase == SessionController.Phase.Complete) return "complete";
            string name = PhaseLabel(phase);
            if (session.IsRunningCondition && session.TotalIterations > 0)
                return $"{name} · iter {session.Iteration}/{session.TotalIterations}";
            double left = session.PhaseSecondsRemaining;
            return left > 0 ? $"{name} · {Mmss((float)left)} left" : name;
        }

        private void BuildSessionCard(Transform root)
        {
            var card = Card(root, "Session", new Vector2(LeftColX, SessionCardY), new Vector2(LeftColW, SessionCardH));
            var host = card.transform;
            // One inset for every full-width control in this card, so the
            // START button, the progress bar, the contextual row and the
            // emergency stop all share the same left and right edges.
            const float inset = 16f, fullW = LeftColW - 2f * inset;

            _preGroup = Empty(host, "pre");

            // Counterbalancing order 1-6: one button per permutation of the
            // three conditions. This IS the group assignment — it's recorded
            // as GroupID in every CSV — so it's a switch rather than a typed
            // field: a typo in a free-text group box would silently mislabel
            // the whole session's data.
            Txt(_preGroup.transform, "Counterbalancing order", 13, _dim, new Vector2(inset, -34), new Vector2(190, 20));
            // Right-aligned as a block against the same inset as everything
            // else, so the six buttons end where the START button does instead
            // of stopping 8px short of it.
            const float orderBtnW = 35f, orderGap = 4f;
            float orderX = inset + fullW - SessionController.OrderCount * (orderBtnW + orderGap) + orderGap;
            for (int i = 0; i < SessionController.OrderCount; i++)
            {
                int order = i + 1; // capture per-iteration, not the loop variable
                _orderImg[i] = Btn(_preGroup.transform, order.ToString(),
                    new Vector2(orderX + i * (orderBtnW + orderGap), -32), new Vector2(orderBtnW, 26),
                    () => session.orderIndex = order, out _);
            }
            _orderPreviewTxt = Txt(_preGroup.transform, "", 12, _accent, new Vector2(inset, -60), new Vector2(fullW, 18));

            // Participant identifier — set per session here, not in the
            // Inspector (up to ~30 participants; a fixed Inspector value would
            // mean re-editing the prefab/scene between every run).
            Txt(_preGroup.transform, "Participant ID", 13, _dim, new Vector2(inset, -88), new Vector2(110, 20));
            _userIdField = Field(_preGroup.transform, new Vector2(inset + 116, -86), new Vector2(140, 26), "P01");
            _userIdField.text = session.userId;
            _userIdField.onValueChanged.AddListener(v => session.userId = v);

            _startImg = Btn(_preGroup.transform, "START SESSION", new Vector2(inset, -132), new Vector2(fullW, 48),
                () => session.StartSession(), out _);
            _startImg.color = _btnSel;
            Txt(_preGroup.transform, "…or click a stop on the timeline above to start there.", 12, _dim,
                new Vector2(inset, -188), new Vector2(fullW, 18));

            // Live rows. Every Y is derived from the row above it and every row
            // knows its own height, so the block cannot drift into the one
            // under it — which is how the stopped banner ended up 8px below the
            // bottom edge of the card it is drawn in.
            const float phaseH = 24f, statusH = 30f, condPitch = 22f, condH = 20f, barH = 10f;
            const float phaseY = -34f;
            const float statusY = phaseY - phaseH - 2f;                    // -60
            const float condTop = statusY - statusH - 4f;                  // -94
            float barY = condTop - _condLabel.Length * condPitch - 4f;     // -164
            float actionY = barY - barH - 6f;                              // -180
            float estopY = actionY - ActionRowH - 6f;                      // -222
            float bannerY = estopY - EstopRowH - 4f;                       // -270 (ends -286)

            _liveGroup = Empty(host, "live");
            _phaseTxt = Txt(_liveGroup.transform, "—", 20, _running, new Vector2(inset, phaseY), new Vector2(fullW, phaseH));
            _phaseTxt.fontStyle = FontStyle.Bold;
            _statusTxt = Txt(_liveGroup.transform, "", 13, _dim, new Vector2(inset, statusY), new Vector2(fullW, statusH));
            for (int i = 0; i < _condLabel.Length; i++)
            {
                float y = condTop - i * condPitch;
                _condLabel[i] = Txt(_liveGroup.transform, $"Cond {i + 1}", 14, _pending, new Vector2(inset, y), new Vector2(200, condH));
                _condTime[i] = Txt(_liveGroup.transform, "", 13, _pending, new Vector2(inset + 194, y), new Vector2(fullW - 194, condH));
                _condTime[i].alignment = TextAnchor.UpperRight;
            }
            _condBar = Bar(_liveGroup.transform, new Vector2(inset, barY), new Vector2(fullW, barH), _accent);

            // Contextual buttons — one shared row, either full width or split
            // in two with a single gap.
            const float halfGap = 12f;
            float halfW = (fullW - halfGap) / 2f;
            var wide = new Vector2(fullW, ActionRowH);
            var half = new Vector2(halfW, ActionRowH);
            var pos = new Vector2(inset, actionY);
            var posRight = new Vector2(inset + halfW + halfGap, actionY);
            _qDoneBtn = BtnObj(host, "QUESTIONNAIRE DONE", pos, wide, () => session.ConfirmQuestionnaire());
            _breakBtn = BtnObj(host, "BREAK", pos, half, () => session.ChooseBreak());
            _continueBtn = BtnObj(host, "CONTINUE", posRight, half, () => session.ChooseContinue());
            _resumeBreakBtn = BtnObj(host, "RESUME NEXT CONDITION", pos, wide, () => session.ResumeFromBreak());
            // Explore condition only — shares the row with END FREE-PLAY, the
            // same way BREAK/CONTINUE split it.
            _nudgeBtn = BtnObj(host, "PLAY EXPLORE NUDGE", pos, half, () => session.PlayExploreNudge());
            _endFreePlayBtn = BtnObj(host, "END FREE-PLAY", posRight, half, () => session.EndFreePlay());

            // The shortcut is on the button because a safety key nobody knows
            // about is not a safety key.
            var estopSize = new Vector2(fullW, EstopRowH);
            var estopImg = Btn(host, "EMERGENCY STOP   (F1)", new Vector2(inset, estopY), estopSize, () => session.EmergencyStop(), out _);
            _estopBtn = estopImg.GetComponent<Button>(); estopImg.color = _estop;
            var resumeImg = Btn(host, "RESUME   (F1)", new Vector2(inset, estopY), estopSize, () => session.Resume(), out _);
            _estopResumeBtn = resumeImg.GetComponent<Button>(); resumeImg.color = _btnSel;
            _estopBanner = Txt(host, "STOPPED — platform returning", 12, _estop,
                               new Vector2(inset, bannerY), new Vector2(fullW, BannerH));
            _estopBanner.alignment = TextAnchor.UpperCenter;
        }

        // Row heights the session card's tail is built from, named so the Y
        // chain above can be read as a stack rather than a list of literals.
        private const float ActionRowH = 36f, EstopRowH = 44f, BannerH = 16f;

        // ════════════════════════════════════════════════════════════════
        //  DASHBOARD GEOMETRY — one place, all of it derived
        //
        //  The dashboard is authored against a FIXED 1920×1080 reference
        //  (the canvas scaler maps that onto whatever the display really
        //  is). Every edge below is derived from its neighbour rather than
        //  hand-matched, because every layout bug this file has had came
        //  from two literals that were supposed to agree and stopped
        //  agreeing: a card grown without moving the one under it, a box
        //  measured for a narrower grid than it packs. VerifyLayout() at
        //  the end of BuildUI re-checks the arithmetic at runtime and says
        //  so in the log if anything still lands on top of anything else.
        // ════════════════════════════════════════════════════════════════
        private const float CanvasW = 1920f, CanvasH = 1080f;
        private const float EdgeMargin = 24f;
        private const float TransportH = 132f, TransportBottomGap = 12f;
        /// <summary>Top edge of the record/playback bar. Nothing in any
        /// column may reach past this.</summary>
        private const float TransportTopY = -(CanvasH - TransportH - TransportBottomGap);

        /// <summary>Top edge of ALL THREE columns — everything below the
        /// header/timeline band. One constant rather than three matching
        /// literals: the timeline card sits above all of them, so changing
        /// its height has to move the whole dashboard down together or the
        /// columns slide underneath it.</summary>
        private const float ColumnTopY = -(-TimelineY + TimelineH + 8f);

        // Left column — each card's Y derived from the one above it so a
        // height change here can't silently leave the next card overlapping.
        // The heights are sized to what the cards actually DRAW: the trial
        // card ends after its fourth parameter bar, and the baseline card is
        // tall enough for a full twelve-channel readout, which at 152px it
        // was not — the last channels simply spilled out of the bottom.
        private const float LeftColX = EdgeMargin, LeftColW = 452f;
        private const float SessionCardY = ColumnTopY, SessionCardH = 294;
        private const float TrialCardY = SessionCardY - SessionCardH - 12, TrialCardH = 212;
        private const float BaselineCardY = TrialCardY - TrialCardH - 8, BaselineCardH = 244;

        // Right-column layout (beside the Sensor Grid, GridX0) — Forces
        // gizmo, then Connections, then Event Log, top edge matching the
        // Sensor Grid's own (GridY0). Each Y computed where used, same
        // derive-from-the-one-above pattern as the left column.
        // GizmoGraphicsH is the crosshair/compass block's own height — every
        // graphic in it is positioned from THAT, not from the card height, so
        // growing the card to fit the speed strip along its bottom leaves the
        // gizmo exactly where it was. Cards below derive their Y from
        // GizmoCardH, so the whole right column reflows on its own.
        private const float GizmoGraphicsH = 220;
        private const float SpeedStripH = 52;
        private const float GizmoCardH = GizmoGraphicsH + SpeedStripH, ConnectionsCardH = 240;

        private void BuildTrialCard(Transform root)
        {
            const float inset = 16f, fullW = LeftColW - 2f * inset;
            var card = Card(root, "Trial Progress — driven by the session automatically",
                new Vector2(LeftColX, TrialCardY), new Vector2(LeftColW, TrialCardH));
            var host = card.transform;
            _optDot = Dot(host, new Vector2(inset, -32));
            _optLabel = Txt(host, "optimizer: idle", 13, _pending, new Vector2(inset + 18, -30), new Vector2(fullW - 18, 20));
            _iterTxt = Txt(host, "iteration — / —", 14, _dim, new Vector2(inset, -54), new Vector2(210, 22));
            _hvTxt = Txt(host, "hypervolume  —", 14, _dim, new Vector2(inset + 210, -54), new Vector2(fullW - 210, 22));
            _hvTxt.alignment = TextAnchor.UpperRight;

            _trackPosTxt = Txt(host, "Track: —", 13, _accent, new Vector2(inset, -78), new Vector2(fullW, 20));

            // Label / bar / value share one baseline per row: the value column
            // is right-aligned against the card's inset so the four numbers line
            // up under each other, which left-aligned in a 40px box they did not.
            const float labelW = 96f, valueW = 44f, gap = 8f;
            float paramBarW = fullW - labelW - valueW - 2f * gap;
            for (int i = 0; i < ParamLabels.Length; i++)
            {
                float y = -108 - i * 24;
                Txt(host, ParamLabels[i], 12, _dim, new Vector2(inset, y), new Vector2(labelW, 20));
                _paramBars[i] = Bar(host, new Vector2(inset + labelW + gap, y + 3), new Vector2(paramBarW, 14), _running);
                _paramVals[i] = Txt(host, "0.50", 12, _dim,
                                    new Vector2(inset + fullW - valueW, y), new Vector2(valueW, 20));
                _paramVals[i].alignment = TextAnchor.UpperRight;
            }
        }

        private void BuildBaselineCard(Transform root)
        {
            const float inset = 16f;
            var card = Card(root, "Baseline — captured during the meditation",
                            new Vector2(LeftColX, BaselineCardY), new Vector2(LeftColW, BaselineCardH));
            _baselineText = Txt(card.transform, "No baseline captured yet.", 12, _dim,
                                new Vector2(inset, -34), new Vector2(LeftColW - 2f * inset, BaselineCardH - 44));
            _baselineText.alignment = TextAnchor.UpperLeft;
        }

        private void RefreshBaselineCard()
        {
            if (session == null || _baselineText == null) return;

            var readings = session.LastBaselineReadings;
            if (readings == null || readings.Count == 0)
            {
                _baselineText.text = "No baseline captured yet.";
                return;
            }

            var sb = new StringBuilder();
            sb.Append($"Condition {session.LastBaselineConditionNumber} ({session.LastBaselineConditionKind})\n");
            foreach (var r in readings)
            {
                var (label, unit) = DelphiManager.Meta(r.channel);
                sb.Append($"{label,-16} {r.mean,8:F2} {unit}  ({r.sampleCount} smp)  " +
                          $"[{r.lowerBound:F2}, {r.upperBound:F2}]\n");
            }
            if (session.LastBaselineMissingChannels.Count > 0)
                sb.Append($"No samples: {string.Join(", ", session.LastBaselineMissingChannels)}\n");

            _baselineText.text = sb.ToString();
        }

        // ── Centre and right column widths ──────────────────────────────
        /// <summary>The scalar box's width. Fixed: the channel count only ever
        /// changes how many ROWS it packs, never how many columns.</summary>
        private float ScalarBoxW => GridCols * (_cellW + GridColGap) - GridColGap + 32f;

        /// <summary>The centre column is as wide as its WIDER box, which is not
        /// always the scalar one.
        ///
        /// This was a real collision, not a hypothetical: with the feed
        /// spacing currently authored on this component the frame box comes
        /// out 954px wide against the scalar box's 914, and the right column
        /// was placed off the scalar width alone — so the camera feeds ran 40px
        /// underneath the Forces/Connections cards.</summary>
        private float CentreColW => Mathf.Max(ScalarBoxW, FrameBoxW);

        // EVENT LOG sits below BASELINE now, not directly below TRIAL.
        // Event Log lives to the right of the Sensor Grid — same top edge
        // (GridY0), and starting clear of whichever sensor box is wider.
        private float RightColumnX => GridX0 + CentreColW + EdgeMargin;

        /// <summary>Whatever is left between the sensor grid and the right edge
        /// of the screen, so a wide feed layout narrows this column instead of
        /// pushing it off the display. Floored, because below about 320px the
        /// connection lines stop fitting and a clipped status is worse than a
        /// slightly overlapping one.</summary>
        private float RightColW => Mathf.Clamp(CanvasW - EdgeMargin - RightColumnX, 320f, 452f);
        private float RightInnerW => RightColW - 30f;

        // The right column is the tallest of the three and now starts lower
        // (the timeline band above it), so the log — the one card here whose
        // height is a free choice rather than dictated by its contents — is
        // what gives up the room, down to the LogLines it actually keeps.
        private const float EventLogCardH = 232;

        private void BuildEventLog(Transform root)
        {
            float y = GridY0 - GizmoCardH - 12 - ConnectionsCardH - 12;
            var card = Card(root, "Event Log", new Vector2(RightColumnX, y), new Vector2(RightColW, EventLogCardH));
            _logText = Txt(card.transform, "", 12, _dim, new Vector2(16, -32),
                           new Vector2(RightInnerW, EventLogCardH - 42));
            _logText.alignment = TextAnchor.UpperLeft;
            // WRAP, not overflow. Every other Txt in this file is a short
            // measured value that must never be re-flowed; a log line is
            // free-form prose and the long ones were running straight out
            // through the right-hand edge of the card and off the screen.
            _logText.horizontalOverflow = HorizontalWrapMode.Wrap;
        }

        // ════════════════════════════════════════════════════════════════
        //  FORCES GIZMO — live readout of the seat's actual commanded
        //  pitch/roll/yaw. Colors match Unity's own transform-gizmo
        //  convention (X=pitch=red, Y=yaw=green, Z=roll=blue).
        //
        //  Pitch/roll are a crosshair (vertical=pitch, horizontal=roll) —
        //  each a straight vector, since they're bounded ±max and only ever
        //  mean "how far, which direction along one axis." Yaw gets its own
        //  compass dial with a rotating needle instead of a vector line —
        //  it's a full 0-360° heading, not a bounded magnitude, so a line
        //  that only flips 180° can't represent it properly.
        // ════════════════════════════════════════════════════════════════
        private const float GizmoMaxLen = 56f;
        private Vector2 _crosshairOrigin, _compassOrigin;
        private Image _gizmoPitchLine, _gizmoRollLine, _compassNeedle;
        private Text _gizmoValuesTxt;

        private void BuildForcesGizmo(Transform root)
        {
            var card = Card(root, "Forces (commanded)", new Vector2(RightColumnX, GridY0),
                            new Vector2(RightColW, GizmoCardH));
            var host = card.transform;

            // The crosshair and the compass split the card between them, so
            // they stay evenly placed however wide the column ends up —
            // hard-coded at 120/330 they drifted off-centre (and the compass
            // ring off the right edge) the moment the column narrowed.
            float centerY = -(GizmoGraphicsH - CardHeaderH) / 2f - 6f;
            _crosshairOrigin = new Vector2(RightColW * 0.27f, centerY);
            _compassOrigin = new Vector2(RightColW * 0.73f, centerY);

            var pitchColor = new Color(0.95f, 0.3f, 0.3f);
            var rollColor = new Color(0.35f, 0.55f, 0.95f);
            var yawColor = new Color(0.35f, 0.85f, 0.4f);
            var refColor = new Color(1f, 1f, 1f, 0.15f);

            // Crosshair reference lines (full range, dim) + live vectors.
            SetVectorLine(MakeVectorLine(host, refColor), _crosshairOrigin, 90f, GizmoMaxLen, 2f);
            SetVectorLine(MakeVectorLine(host, refColor), _crosshairOrigin, 0f, GizmoMaxLen, 2f);
            _gizmoPitchLine = MakeVectorLine(host, pitchColor);
            _gizmoRollLine = MakeVectorLine(host, rollColor);

            var crosshairDot = NewImage(host, _text);
            var cdrt = crosshairDot.rectTransform;
            cdrt.pivot = new Vector2(0.5f, 0.5f);
            cdrt.anchoredPosition = _crosshairOrigin;
            cdrt.sizeDelta = new Vector2(8, 8);

            var pitchLabel = Txt(host, "PITCH", 11, pitchColor, _crosshairOrigin + new Vector2(-30, GizmoMaxLen + 10), new Vector2(70, 16));
            pitchLabel.fontStyle = FontStyle.Bold;
            var rollLabel = Txt(host, "ROLL", 11, rollColor, _crosshairOrigin + new Vector2(GizmoMaxLen + 6, -8), new Vector2(70, 16));
            rollLabel.fontStyle = FontStyle.Bold;

            // Compass ring (procedural texture, no asset dependency) + needle.
            var ring = new GameObject("YawRing", typeof(Image));
            ring.transform.SetParent(host, false);
            var ringImg = ring.GetComponent<Image>();
            ringImg.sprite = GetYawRingSprite();
            ringImg.color = new Color(yawColor.r, yawColor.g, yawColor.b, 0.5f);
            ringImg.raycastTarget = false;
            var ringRt = ringImg.rectTransform;
            ringRt.anchorMin = ringRt.anchorMax = new Vector2(0f, 1f); // top-left, matching every other element here
            ringRt.pivot = new Vector2(0.5f, 0.5f);
            ringRt.anchoredPosition = _compassOrigin;
            ringRt.sizeDelta = new Vector2((GizmoMaxLen + 6f) * 2f, (GizmoMaxLen + 6f) * 2f);

            _compassNeedle = MakeVectorLine(host, yawColor);

            var compassDot = NewImage(host, _text);
            var codrt = compassDot.rectTransform;
            codrt.pivot = new Vector2(0.5f, 0.5f);
            codrt.anchoredPosition = _compassOrigin;
            codrt.sizeDelta = new Vector2(8, 8);

            var yawLabel = Txt(host, "YAW", 11, yawColor, _compassOrigin + new Vector2(-20, GizmoMaxLen + 16), new Vector2(70, 16));
            yawLabel.fontStyle = FontStyle.Bold;

            _gizmoValuesTxt = Txt(host, "", 12, _dim,
                                  new Vector2(16, -(GizmoGraphicsH - CardHeaderH - 14)),
                                  new Vector2(RightInnerW, 20));

            BuildSpeedStrip(host);
        }

        // ════════════════════════════════════════════════════════════════
        //  SPEED STRIP — how fast the car is going, what it's TRYING to go,
        //  and what's stopping it from going faster. That last part is the
        //  point: the cruise target is the posted limit, pulled down by the
        //  corner slowdown, a red light or a lead vehicle — so "why won't it
        //  exceed X" has several possible answers and guessing between them
        //  from a bare speed number is impossible. CarDriver.Limiter names
        //  whichever term actually bound the target this frame.
        // ════════════════════════════════════════════════════════════════
        private const float SpeedBarX = 16;
        private float SpeedBarW => RightInnerW;
        private Text _speedBigTxt, _speedTargetTxt, _speedLimiterTxt;
        private Slider _speedBar;
        private Image _speedTargetTick;

        private void BuildSpeedStrip(Transform host)
        {
            float y = -GizmoGraphicsH;

            _speedBigTxt = Txt(host, "— km/h", 24, _accent, new Vector2(SpeedBarX, y + 2), new Vector2(150, 34));
            _speedBigTxt.fontStyle = FontStyle.Bold;

            _speedTargetTxt = Txt(host, "", 12, _dim, new Vector2(168, y + 2), new Vector2(RightColW - 184, 18));
            _speedLimiterTxt = Txt(host, "", 12, _dim, new Vector2(168, y - 14), new Vector2(RightColW - 184, 18));
            _speedLimiterTxt.fontStyle = FontStyle.Bold;

            _speedBar = Bar(host, new Vector2(SpeedBarX, y - 36), new Vector2(SpeedBarW, 8), _accent);

            // Thin marker sitting ON the bar at the target speed — the gap
            // between the fill edge and this tick is the speed the car is
            // still trying to pick up.
            _speedTargetTick = NewImage(host, _text);
            var trt = _speedTargetTick.rectTransform;
            trt.pivot = new Vector2(0.5f, 0.5f);
            trt.sizeDelta = new Vector2(2, 16);
            trt.anchoredPosition = new Vector2(SpeedBarX, y - 40);
        }

        private void RefreshSpeedStrip()
        {
            var car = session != null ? session.carDriver : null;
            if (car == null || car.track == null)
            {
                _speedBigTxt.text = "— km/h";
                _speedBigTxt.color = _dim;
                _speedTargetTxt.text = "no CarDriver in scene";
                _speedLimiterTxt.text = "";
                _speedBar.SetValueWithoutNotify(0f);
                _speedTargetTick.enabled = false;
                return;
            }

            // The bar's full scale is the POSTED limit here, not the target —
            // so the headroom the driving style is giving away stays visible
            // rather than being normalised out of existence.
            float postedKmh = Mathf.Max(1f, car.track.SpeedLimitAt(car.S));
            float speedKmh = car.CurrentSpeedKmh;
            float targetKmh = car.TargetSpeedKmh;

            _speedBigTxt.text = $"{speedKmh:F0} km/h";
            _speedBigTxt.color = car.IsParked ? _dim : _accent;
            _speedBar.SetValueWithoutNotify(Mathf.Clamp01(speedKmh / postedKmh));

            _speedTargetTick.enabled = true;
            var trt = _speedTargetTick.rectTransform;
            trt.anchoredPosition = new Vector2(
                SpeedBarX + SpeedBarW * Mathf.Clamp01(targetKmh / postedKmh),
                trt.anchoredPosition.y);

            _speedTargetTxt.text = $"target {targetKmh:F1}   ·   posted {postedKmh:F0} km/h";

            var (label, color) = car.Limiter switch
            {
                CarDriver.SpeedLimiter.RedLight => ("held by: red light ahead", _estop),
                CarDriver.SpeedLimiter.Corner   => ("held by: corner slowdown", _running),
                CarDriver.SpeedLimiter.Cruise   => ("held by: cruise margin below limit", _accent),
                _                                => ("held by: parked / stopping", _dim)
            };
            _speedLimiterTxt.text = label;
            _speedLimiterTxt.color = color;
        }

        private void RefreshForcesGizmo()
        {
            RefreshSpeedStrip();

            var yaw = YawVR3Connection.Instance;
            var cues = yaw != null && yaw.cues != null ? yaw.cues : FindFirstObjectByType<CarMotionCues>();

            if (cues == null)
            {
                _gizmoValuesTxt.text = "No CarMotionCues found";
                SetVectorLine(_gizmoPitchLine, _crosshairOrigin, 90f, 0f, 5f);
                SetVectorLine(_gizmoRollLine, _crosshairOrigin, 0f, 0f, 5f);
                SetVectorLine(_compassNeedle, _compassOrigin, 90f, 0f, 4f);
                return;
            }

            float pitchLen = Mathf.Clamp01(Mathf.Abs(cues.PitchDeg) / Mathf.Max(1f, cues.maxPitchDeg)) * GizmoMaxLen;
            float rollLen = Mathf.Clamp01(Mathf.Abs(cues.RollDeg) / Mathf.Max(1f, cues.maxRollDeg)) * GizmoMaxLen;
            SetVectorLine(_gizmoPitchLine, _crosshairOrigin, cues.PitchDeg >= 0f ? 90f : 270f, pitchLen, 5f);
            SetVectorLine(_gizmoRollLine, _crosshairOrigin, cues.RollDeg >= 0f ? 0f : 180f, rollLen, 5f);

            // Compass convention: 0° = up, increasing clockwise — standard
            // math angle (0°=right, CCW+) needs converting for that.
            float headingDeg = Mathf.Repeat(cues.YawDeg, 360f);
            float needleMathAngle = 90f - headingDeg;
            SetVectorLine(_compassNeedle, _compassOrigin, needleMathAngle, GizmoMaxLen, 4f);

            // accel shows car → seat: the left number is what the car is doing,
            // the right what the jerk limiter has let through to the lean yet.
            // They diverge during an onset and re-converge once it's ramped in.
            _gizmoValuesTxt.text = $"pitch {cues.PitchDeg:0.#}°   roll {cues.RollDeg:0.#}°   yaw {headingDeg:0.#}°   " +
                                    $"·   accel {cues.AccelMs2:0.0}→{cues.ShapedAccelMs2:0.0} m/s²   " +
                                    $"turn {cues.YawRateDegPerSec:0.0}°/s";
        }

        private Image MakeVectorLine(Transform parent, Color color)
        {
            var go = new GameObject("Vector", typeof(Image));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.color = color;
            img.raycastTarget = false;
            var rt = img.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 0.5f); // rotates around its START point
            return img;
        }

        private static void SetVectorLine(Image img, Vector2 origin, float angleDeg, float length, float thickness)
        {
            var rt = img.rectTransform;
            rt.anchoredPosition = origin;
            rt.sizeDelta = new Vector2(Mathf.Max(0.01f, length), thickness);
            rt.localRotation = Quaternion.Euler(0f, 0f, angleDeg);
        }

        // Procedural ring texture for the yaw compass — no imported sprite
        // asset needed. Generated once, cached.
        private static Sprite _yawRingSprite;

        private static Sprite GetYawRingSprite()
        {
            if (_yawRingSprite != null) return _yawRingSprite;
            const int size = 128;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
            var pixels = new Color32[size * size];
            float center = size / 2f;
            float outerR = size / 2f - 3f;
            float innerR = outerR - 4f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x + 0.5f - center, dy = y + 0.5f - center;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    byte a = (dist <= outerR && dist >= innerR) ? (byte)255 : (byte)0;
                    pixels[y * size + x] = new Color32(255, 255, 255, a);
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply();
            _yawRingSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
            return _yawRingSprite;
        }

        // ════════════════════════════════════════════════════════════════
        //  CONNECTIONS — every external I/O link at a glance: the YAW VR3
        //  rig, the BO optimizer process, and the sensor bridges. All of
        //  these connect automatically on Play; the YAW rig's motion going
        //  LIVE never does (see conversation) — that's the one explicit
        //  action in this card. The cues readout under YAW VR3 exists so
        //  you can see at a glance whether CarMotionCues is even computing
        //  nonzero tilt (car actually driving) versus the rig just sitting
        //  Connected-not-Started.
        // ════════════════════════════════════════════════════════════════
        private Image _connYawDot, _connBoDot, _connGsrDot, _connPolarDot;
        private Text _connYawTxt, _connYawCuesTxt, _connBoTxt, _connGsrTxt, _connPolarTxt;
        private Text _yawMotionTxt, _yawRumbleTxt;
        private Image _yawMotionImg, _yawRumbleImg;

        private void BuildConnectionsCard(Transform root)
        {
            float y = GridY0 - GizmoCardH - 12;
            var card = Card(root, "Connections", new Vector2(RightColumnX, y), new Vector2(RightColW, ConnectionsCardH));
            var host = card.transform;
            float lineW = RightColW - 48f;   // clear of the dot column at x=16

            _connYawDot = Dot(host, new Vector2(16, -34));
            _connYawTxt = Txt(host, "YAW VR3: —", 13, _dim, new Vector2(34, -30), new Vector2(lineW, 20));
            _connYawCuesTxt = Txt(host, "", 11, _dim, new Vector2(34, -50), new Vector2(lineW, 16));

            _connBoDot = Dot(host, new Vector2(16, -78));
            _connBoTxt = Txt(host, "BO Hub: —", 13, _dim, new Vector2(34, -74), new Vector2(lineW, 20));

            _connGsrDot = Dot(host, new Vector2(16, -102));
            _connGsrTxt = Txt(host, "GSR: —", 13, _dim, new Vector2(34, -98), new Vector2(lineW, 20));

            _connPolarDot = Dot(host, new Vector2(16, -126));
            _connPolarTxt = Txt(host, "Polar H10: —", 13, _dim, new Vector2(34, -122), new Vector2(lineW, 20));

            _yawMotionImg = Btn(host, "START MOTION", new Vector2(16, -156), new Vector2(RightInnerW, 32),
                ToggleYawMotion, out _yawMotionTxt);
            _yawRumbleImg = Btn(host, "RUMBLE: ON", new Vector2(16, -196), new Vector2(RightInnerW, 24),
                ToggleYawRumble, out _yawRumbleTxt);
        }

        private void ToggleYawMotion()
        {
            var yaw = YawVR3Connection.Instance;
            if (yaw == null) return;
            if (yaw.State == YawConnectionState.Connected) yaw.StartMotion();
            else if (yaw.State == YawConnectionState.Started) yaw.StopMotion();
        }

        private void ToggleYawRumble()
        {
            var yaw = YawVR3Connection.Instance;
            if (yaw != null) yaw.rumbleEnabled = !yaw.rumbleEnabled;
        }

        private void RefreshConnectionsCard()
        {
            // YAW VR3
            var yaw = YawVR3Connection.Instance;
            var motionBtn = _yawMotionImg.GetComponent<Button>();
            if (yaw == null)
            {
                _connYawTxt.text = "YAW VR3: not present in scene";
                _connYawTxt.color = _connYawDot.color = _dim;
                _connYawCuesTxt.text = "";
                motionBtn.interactable = false;
            }
            else
            {
                _connYawTxt.text = $"YAW VR3: {yaw.State} — {yaw.StatusText}";
                Color yawColor = yaw.State switch
                {
                    YawConnectionState.Started => _running,
                    YawConnectionState.Connected => _accent,
                    YawConnectionState.Discovering or YawConnectionState.Connecting
                        or YawConnectionState.Starting or YawConnectionState.Stopping => _pending,
                    _ => _dim
                };
                _connYawTxt.color = _connYawDot.color = yawColor;

                var cues = yaw.cues;
                _connYawCuesTxt.text = cues != null
                    ? $"cues — pitch {cues.PitchDeg:0.#}°  roll {cues.RollDeg:0.#}°  " +
                      $"(accel {cues.AccelMs2:0.00} m/s²  turn {cues.YawRateDegPerSec:0.0}°/s)"
                    : "cues — no CarMotionCues found";

                bool canStart = yaw.State == YawConnectionState.Connected;
                bool canStop = yaw.State == YawConnectionState.Started;
                motionBtn.interactable = canStart || canStop;
                _yawMotionTxt.text = canStop ? "STOP MOTION" : "START MOTION";
                _yawMotionImg.color = canStop ? _estop : (canStart ? _btnSel : _btn);

                _yawRumbleTxt.text = yaw.rumbleEnabled ? "RUMBLE: ON" : "RUMBLE: OFF";
                _yawRumbleImg.color = yaw.rumbleEnabled ? _btnSel : _btn;
            }

            // BO Hub — reuse SessionController's own optimizer status
            if (session != null)
            {
                var (c, l) = session.Optimizer switch
                {
                    SessionController.OptimizerStatus.Connected    => (_accent, "BO Hub: connected"),
                    SessionController.OptimizerStatus.Starting     => (new Color(0.85f, 0.75f, 0.25f), "BO Hub: starting…"),
                    SessionController.OptimizerStatus.Disconnected => (_estop, "BO Hub: disconnected"),
                    _                                               => (_pending, "BO Hub: idle")
                };
                _connBoTxt.text = l; _connBoTxt.color = c; _connBoDot.color = c;
            }
            else { _connBoTxt.text = "BO Hub: —"; _connBoTxt.color = _connBoDot.color = _dim; }

            // GSR
            var gsr = GSRSerialConnection.Instance;
            if (gsr == null) { _connGsrTxt.text = "GSR: not present in scene"; _connGsrTxt.color = _connGsrDot.color = _dim; }
            else
            {
                _connGsrTxt.text = gsr.IsConnected ? "GSR: connected" : "GSR: disconnected";
                _connGsrTxt.color = _connGsrDot.color = gsr.IsConnected ? _accent : _estop;
            }

            // Polar H10
            var polar = PolarH10OscConnection.Instance;
            if (polar == null) { _connPolarTxt.text = "Polar H10: not present in scene"; _connPolarTxt.color = _connPolarDot.color = _dim; }
            else
            {
                _connPolarTxt.text = polar.HasReceivedData ? "Polar H10: receiving" : "Polar H10: listening, no data yet";
                _connPolarTxt.color = _connPolarDot.color = polar.HasReceivedData ? _accent : _pending;
            }
        }

        // Cells pack starting just below each box's own header (see Card()).
        private const float SectionContentX = 16f, SectionContentY = -(CardHeaderH + 8f);

        // The feed box is sized from the SAME expressions the cells pack with,
        // so widening the gaps grows the box instead of pushing feeds out
        // through its right edge.
        private RectTransform _scalarBoxRt, _scalarHeaderRt, _frameBoxRt, _frameHeaderRt;
        // Rows each section is CURRENTLY packing — set by PackSection, which is
        // the only thing that knows how many cells are actually visible.
        private int _scalarRows = 1, _frameRows = 1;
        private float FrameBoxW => FrameCols * (FrameCellW + FrameGapH) - FrameGapH + 32f;
        private float FrameBoxH =>
            CardHeaderH + _frameRows * FrameRowH + (_frameRows - 1) * FrameGapV + 16f;

        /// <summary>Two clearly separate, individually labelled boxes — one
        /// for scalar (physiological/behavioral) channels, one for camera
        /// feeds — instead of one undifferentiated mixed grid, so it's
        /// obvious at a glance which kind of sensor a cell is without reading
        /// each label individually.</summary>
        private void BuildSensorGrid(Transform root)
        {
            var scalar = DelphiManager.AllChannels; var frame = DelphiManager.AllFrameChannels;

            // Provisional sizes only — ResizeSensorBoxes fits both boxes to the
            // rows that are genuinely in use as soon as the cells are packed,
            // and again whenever the active set or the feed tuning changes.
            var scalarBox = Card(root, "Scalar Sensors — physiological & behavioral signals",
                new Vector2(GridX0, GridY0), new Vector2(ScalarBoxW, CardHeaderH + GridRowH + 16f),
                out _scalarHeaderRt);
            _scalarBoxRt = (RectTransform)scalarBox;
            var frameBox = Card(root, "Frame Sensors — camera / video feeds",
                new Vector2(GridX0, GridY0), new Vector2(FrameBoxW, FrameBoxH),
                out _frameHeaderRt);
            _frameBoxRt = (RectTransform)frameBox;

            for (int i = 0; i < scalar.Length; i++)
                _panels.Add(BuildSensorCell(scalarBox, isFrame: false, channel: scalar[i], frameChannel: default));
            for (int i = 0; i < frame.Length; i++)
                _panels.Add(BuildSensorCell(frameBox, isFrame: true, channel: default, frameChannel: frame[i]));

            // Pack active cells and hide inactive ones from the start, so each
            // box opens showing only what's live rather than every empty slot.
            RelayoutGrid();
        }

        private Panel BuildSensorCell(Transform parent, bool isFrame, Channel channel, FrameChannel frameChannel)
        {
            // Frame rows are taller than scalar rows — see FrameCellH.
            float titleH = GridTitleH, valH = GridValH;
            float contentH = isFrame ? FrameCellH : _cellH;
            float headGap = isFrame ? FrameHeadGap : 0f;
            float headerH = isFrame ? FrameHeaderH : titleH + valH;
            float rowH = contentH + headerH + headGap;
            string label = isFrame ? DelphiManager.FrameMeta(frameChannel).label : DelphiManager.Meta(channel).label;

            // Position is provisional — RelayoutGrid() packs the ACTIVE cells
            // with no gaps and hides the rest immediately after this returns.
            var cont = Empty(parent, $"Cell_{label}"); var crt = cont.GetComponent<RectTransform>();
            var panel = new Panel { isFrame = isFrame, cell = cont, cellRt = crt };

            // A FEED'S LABEL AND STATUS SHARE ONE LINE; a scalar's are stacked.
            // A waveform's readout is a live number that changes every frame
            // and wants room ("128.4 bpm  CLIP"). A feed's is one word that
            // changes maybe twice a session, so it belongs beside the name as a
            // badge, not under it as a value.
            float cellW = isFrame ? FrameCellW : _cellW;
            float labelW = isFrame ? cellW - BadgeW - 8f : cellW;
            crt.sizeDelta = new Vector2(cellW, rowH);

            panel.title = Txt(cont.transform, label.TrimEnd(' ', '(', ')'), 13,
                              new Color(0.8f, 0.85f, 0.95f), new Vector2(0, 0), new Vector2(labelW, titleH));

            if (isFrame)
            {
                // OUTLINE, NOT A FILL. A solid block of colour with knocked-out
                // text is louder than the label it sits beside and drags the
                // eye to the one part of the cell that almost never changes.
                // A hairline border with a lit dot reads as a status at a
                // glance and still lets the feed be the thing you look at.
                var bg = NewImage(cont.transform, _pending);
                bg.sprite = PillOutlineSprite; bg.type = Image.Type.Sliced;
                var brt = bg.rectTransform;
                brt.anchoredPosition = new Vector2(cellW - BadgeW, -1f);
                brt.sizeDelta = new Vector2(BadgeW, BadgeH);
                panel.badgeBg = bg;

                var dot = NewImage(bg.transform, _pending);
                dot.sprite = DotSprite;
                var drt = dot.rectTransform;
                drt.anchorMin = drt.anchorMax = new Vector2(0f, 0.5f);
                drt.pivot = new Vector2(0.5f, 0.5f);
                drt.anchoredPosition = new Vector2(7f, 0f);
                drt.sizeDelta = new Vector2(5f, 5f);
                panel.badgeDot = dot;

                var bt = Txt(bg.transform, "…", 10, _pending, Vector2.zero, new Vector2(BadgeW, BadgeH));
                bt.alignment = TextAnchor.MiddleLeft;
                var trt = bt.rectTransform;
                trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
                trt.offsetMin = new Vector2(14f, 0f); trt.offsetMax = new Vector2(-4f, 0f);
                panel.badgeTxt = bt;
            }
            else
            {
                var v = Txt(cont.transform, "…", 12, _pending, new Vector2(0, -titleH), new Vector2(cellW, valH));
                v.alignment = TextAnchor.UpperRight;
                panel.value = v;
            }

            var imgGO = new GameObject("Content", typeof(RawImage)); imgGO.transform.SetParent(cont.transform, false);
            var raw = imgGO.GetComponent<RawImage>(); raw.color = Color.white;
            var irt = imgGO.GetComponent<RectTransform>();
            irt.anchorMin = irt.anchorMax = new Vector2(0, 1); irt.pivot = new Vector2(0, 1);
            irt.anchoredPosition = new Vector2(0, -(headerH + headGap)); irt.sizeDelta = new Vector2(cellW, contentH);
            panel.image = raw;
            if (isFrame)
            {
                panel.frameChannel = frameChannel;
                var ph = new Texture2D(2, 2); ph.SetPixels(new[] { (Color)_panelBg, (Color)_panelBg, (Color)_panelBg, (Color)_panelBg }); ph.Apply(); raw.texture = ph;
            }
            else
            {
                panel.channel = channel;
                panel.tex = new Texture2D(_cellW, _cellH, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                panel.buffer = new Color32[_cellW * _cellH]; panel.history = new float[_cellW];
                for (int x = 0; x < _cellW; x++) panel.history[x] = float.NaN;
                raw.texture = panel.tex; Apply(panel);
            }
            return panel;
        }

        /// <summary>Width of the transport bar in reference pixels. It stretches
        /// to the canvas, but the canvas is always 1920 reference units wide,
        /// so this is exact — and it lets the right-hand controls be placed
        /// against the bar's far edge instead of being left in a heap on the
        /// left with 900px of nothing beside them.</summary>
        private const float TransportW = CanvasW - 2f * EdgeMargin;

        private void BuildTransport(Transform root)
        {
            var bar = NewImage(root, _card); var brt = bar.rectTransform;
            brt.anchorMin = new Vector2(0, 0); brt.anchorMax = new Vector2(1, 0); brt.pivot = new Vector2(0.5f, 0);
            // Same 24px margins as every column above it — at -40 the bar was
            // 4px wider on each side than the cards it sits under, which is
            // exactly the sort of not-quite-aligned edge you see without being
            // able to say what's wrong.
            brt.sizeDelta = new Vector2(-2f * EdgeMargin, TransportH);
            brt.anchoredPosition = new Vector2(0, TransportBottomGap);
            var host = bar.transform;

            const float inset = 16f, rowH = 32f;
            float rightEdge = TransportW - inset;

            // Row 1 — recording. QUIT sits at the far right, as far from REC as
            // the bar allows: it ends the session and closes every encoder, and
            // it used to sit two buttons away from the record toggle.
            _recImg = Btn(host, "REC", new Vector2(inset, -12), new Vector2(84, rowH), ToggleRecord, out _recTxt);
            _nameField = Field(host, new Vector2(inset + 92, -12), new Vector2(260, rowH), "Session name (optional)");
            _recStatus = Txt(host, "idle", 15, _dim, new Vector2(inset + 364, -12), new Vector2(360, rowH));
            _recStatus.alignment = TextAnchor.MiddleLeft;
            _quitImg = Btn(host, QuitIdleLabel, new Vector2(rightEdge - 180, -12), new Vector2(180, rowH),
                           OnQuitClicked, out _quitTxt);

            // Row 2 — playback. The elapsed/duration readout is right-aligned
            // to the end of the scrubber below it, where the end of a timeline
            // is: left-aligned mid-bar it read as another button label.
            float y = -52;
            float x = inset;
            Btn(host, "<", new Vector2(x, y), new Vector2(32, rowH), () => CycleSession(-1), out _); x += 36;
            _sessionLabel = Txt(host, "…", 15, _dim, new Vector2(x, y), new Vector2(300, rowH));
            _sessionLabel.alignment = TextAnchor.MiddleCenter; x += 304;
            Btn(host, ">", new Vector2(x, y), new Vector2(32, rowH), () => CycleSession(1), out _); x += 40;
            _loadBtn = Btn(host, "Load", new Vector2(x, y), new Vector2(76, rowH), ToggleLoad, out _loadTxt).GetComponent<Button>(); x += 86;
            _playBtn = Btn(host, "Play", new Vector2(x, y), new Vector2(76, rowH), () => player?.TogglePlay(), out _playTxt).GetComponent<Button>(); x += 84;
            _stepBackBtn = Btn(host, "-1f", new Vector2(x, y), new Vector2(48, rowH), () => player?.StepFrames(-1), out _).GetComponent<Button>(); x += 52;
            _stepFwdBtn = Btn(host, "+1f", new Vector2(x, y), new Vector2(48, rowH), () => player?.StepFrames(1), out _).GetComponent<Button>(); x += 56;
            _speedBtn = Btn(host, "1x", new Vector2(x, y), new Vector2(58, rowH),
                () => CycleSpeed(_speedIdx >= Speeds.Length - 1 ? -(Speeds.Length - 1) : 1), out _speedTxt).GetComponent<Button>();
            _timeLabel = Txt(host, "--:-- / --:--", 15, _dim, new Vector2(rightEdge - 240, y), new Vector2(240, rowH));
            _timeLabel.alignment = TextAnchor.MiddleRight;

            _scrubber = Scrub(host, new Vector2(inset, -96), new Vector2(-2f * inset, 22));
            _scrubber.onValueChanged.AddListener(v => { if (player != null && player.IsLoaded) { _scrubbing = true; player.Seek(v * player.Duration); _scrubbing = false; } });
        }

        // ── Small widget factory ────────────────────────────────────────
        private static void Show(Button b, bool on) { if (b != null && b.gameObject.activeSelf != on) b.gameObject.SetActive(on); }
        private static string Mmss(float s) => s <= 0 ? "0:00" : $"{(int)(s / 60)}:{((int)s % 60):00}";
        private static void Stretch(RectTransform rt) { rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = rt.offsetMax = Vector2.zero; }

        private GameObject Empty(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform)); go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>(); rt.anchorMin = rt.anchorMax = new Vector2(0, 1); rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = Vector2.zero; rt.sizeDelta = Vector2.zero; return go;
        }

        private Image NewImage(Transform parent, Color c)
        {
            var go = new GameObject("Img", typeof(Image)); go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>(); img.color = c;
            var rt = go.GetComponent<RectTransform>(); rt.anchorMin = rt.anchorMax = new Vector2(0, 1); rt.pivot = new Vector2(0, 1);
            return img;
        }

        // Every card's title gets its OWN small header box, visually
        // separate from the card's content — not just text floating on the
        // card background.
        private const float CardHeaderH = 30f;

        private Transform Card(Transform parent, string title, Vector2 pos, Vector2 size) =>
            Card(parent, title, pos, size, out _);

        /// <summary>As Card, but hands back the header bar so a box that gets
        /// RESIZED at runtime can keep the two in step. The header is a
        /// separate image sized once at build; without this the feed box grows
        /// and its title bar stays at the old width, leaving a stub of header
        /// over a wider background.</summary>
        private Transform Card(Transform parent, string title, Vector2 pos, Vector2 size,
                               out RectTransform header)
        {
            var img = NewImage(parent, _card); var rt = img.rectTransform; rt.anchoredPosition = pos; rt.sizeDelta = size;

            var headerImg = NewImage(img.transform, _card2);
            var hrt = headerImg.rectTransform; hrt.sizeDelta = new Vector2(size.x, CardHeaderH);
            header = hrt;

            var t = Txt(header.transform, title, 14, _accent, new Vector2(14, -8), new Vector2(size.x - 28, 20));
            t.fontStyle = FontStyle.Bold;
            return img.transform;
        }

        private Image Btn(Transform parent, string label, Vector2 pos, Vector2 size, Action onClick, out Text txt)
        {
            var go = new GameObject($"Btn {label}", typeof(Image), typeof(Button)); go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>(); img.color = _btn;
            var rt = go.GetComponent<RectTransform>(); rt.anchorMin = rt.anchorMax = new Vector2(0, 1); rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = pos; rt.sizeDelta = size;
            var btn = go.GetComponent<Button>(); btn.targetGraphic = img; if (onClick != null) btn.onClick.AddListener(() => onClick());
            txt = Txt(go.transform, label, 14, _text, Vector2.zero, size); txt.alignment = TextAnchor.MiddleCenter;
            var trt = txt.rectTransform; trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one; trt.offsetMin = trt.offsetMax = Vector2.zero;
            return img;
        }

        private Button BtnObj(Transform parent, string label, Vector2 pos, Vector2 size, Action onClick)
            => Btn(parent, label, pos, size, onClick, out _).GetComponent<Button>();

        private Image Dot(Transform parent, Vector2 pos)
        {
            var go = new GameObject("Dot", typeof(Image)); go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>(); img.color = _pending;
            var rt = go.GetComponent<RectTransform>(); rt.anchorMin = rt.anchorMax = new Vector2(0, 1); rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = pos; rt.sizeDelta = new Vector2(11, 11); return img;
        }

        private Slider Bar(Transform parent, Vector2 pos, Vector2 size, Color fillColor)
        {
            var go = new GameObject("Bar", typeof(RectTransform), typeof(Image), typeof(Slider)); go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>(); rt.anchorMin = rt.anchorMax = new Vector2(0, 1); rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = pos; rt.sizeDelta = size;
            var bg = go.GetComponent<Image>(); bg.color = _card2; bg.raycastTarget = false;
            var fa = new GameObject("Fill Area", typeof(RectTransform)); fa.transform.SetParent(go.transform, false);
            var fart = fa.GetComponent<RectTransform>(); fart.anchorMin = Vector2.zero; fart.anchorMax = Vector2.one; fart.offsetMin = fart.offsetMax = Vector2.zero;
            var fill = new GameObject("Fill", typeof(Image)); fill.transform.SetParent(fa.transform, false);
            var fimg = fill.GetComponent<Image>(); fimg.color = fillColor; fimg.raycastTarget = false;
            var frt = fill.GetComponent<RectTransform>(); frt.anchorMin = Vector2.zero; frt.anchorMax = Vector2.one; frt.offsetMin = frt.offsetMax = Vector2.zero;
            var handle = new GameObject("Handle", typeof(Image)); handle.transform.SetParent(go.transform, false);
            var himg = handle.GetComponent<Image>(); himg.color = Color.clear; himg.raycastTarget = false;
            var hrt = handle.GetComponent<RectTransform>(); hrt.anchorMin = new Vector2(0, 0); hrt.anchorMax = new Vector2(0, 1); hrt.sizeDelta = Vector2.zero;
            var s = go.GetComponent<Slider>(); s.targetGraphic = bg; s.fillRect = frt; s.handleRect = hrt;
            s.minValue = 0; s.maxValue = 1; s.value = 0; s.interactable = false; s.transition = Selectable.Transition.None;
            s.navigation = new Navigation { mode = Navigation.Mode.None }; return s;
        }

        private Slider Scrub(Transform parent, Vector2 pos, Vector2 size, Color? fillColor = null)
        {
            var go = new GameObject("Scrubber", typeof(RectTransform), typeof(Slider)); go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>(); rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(1, 1); rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = pos; rt.sizeDelta = size;
            var bg = new GameObject("BG", typeof(Image)); bg.transform.SetParent(go.transform, false); bg.GetComponent<Image>().color = _card2;
            var bgrt = bg.GetComponent<RectTransform>(); bgrt.anchorMin = new Vector2(0, 0.35f); bgrt.anchorMax = new Vector2(1, 0.65f); bgrt.offsetMin = bgrt.offsetMax = Vector2.zero;
            var fa = new GameObject("Fill Area", typeof(RectTransform)); fa.transform.SetParent(go.transform, false);
            var fart = fa.GetComponent<RectTransform>(); fart.anchorMin = new Vector2(0, 0.35f); fart.anchorMax = new Vector2(1, 0.65f); fart.offsetMin = fart.offsetMax = Vector2.zero;
            var fill = new GameObject("Fill", typeof(Image)); fill.transform.SetParent(fa.transform, false); fill.GetComponent<Image>().color = fillColor ?? _accent;
            var frt = fill.GetComponent<RectTransform>(); frt.anchorMin = Vector2.zero; frt.anchorMax = Vector2.one; frt.offsetMin = frt.offsetMax = Vector2.zero;
            var ha = new GameObject("Handle Area", typeof(RectTransform)); ha.transform.SetParent(go.transform, false);
            var hart = ha.GetComponent<RectTransform>(); hart.anchorMin = Vector2.zero; hart.anchorMax = Vector2.one; hart.offsetMin = new Vector2(8, 0); hart.offsetMax = new Vector2(-8, 0);
            var handle = new GameObject("Handle", typeof(Image)); handle.transform.SetParent(ha.transform, false); handle.GetComponent<Image>().color = _text;
            var hrt = handle.GetComponent<RectTransform>(); hrt.sizeDelta = new Vector2(12, 0);
            var s = go.GetComponent<Slider>(); s.fillRect = frt; s.handleRect = hrt; s.targetGraphic = handle.GetComponent<Image>();
            s.minValue = 0; s.maxValue = 1; s.direction = Slider.Direction.LeftToRight; return s;
        }

        private InputField Field(Transform parent, Vector2 pos, Vector2 size, string placeholder)
        {
            var go = new GameObject("Field", typeof(Image), typeof(InputField)); go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>(); img.color = _btn;
            var rt = go.GetComponent<RectTransform>(); rt.anchorMin = rt.anchorMax = new Vector2(0, 1); rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = pos; rt.sizeDelta = size;
            var text = Txt(go.transform, "", 14, _text, Vector2.zero, Vector2.zero);
            var trt = text.rectTransform; trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one; trt.offsetMin = new Vector2(8, 2); trt.offsetMax = new Vector2(-8, -2);
            var ph = Txt(go.transform, placeholder, 14, _dim, Vector2.zero, Vector2.zero); ph.fontStyle = FontStyle.Italic;
            var phrt = ph.rectTransform; phrt.anchorMin = Vector2.zero; phrt.anchorMax = Vector2.one; phrt.offsetMin = new Vector2(8, 2); phrt.offsetMax = new Vector2(-8, -2);
            var f = go.GetComponent<InputField>(); f.textComponent = text; f.placeholder = ph; f.targetGraphic = img; return f;
        }

        private Text Txt(Transform parent, string content, int size, Color color, Vector2 pos, Vector2 sizeDelta)
        {
            var go = new GameObject("Text", typeof(Text)); go.transform.SetParent(parent, false);
            var t = go.GetComponent<Text>(); t.text = content; t.font = _font; t.fontSize = size; t.alignment = TextAnchor.UpperLeft; t.color = color;
            t.horizontalOverflow = HorizontalWrapMode.Overflow; t.verticalOverflow = VerticalWrapMode.Overflow;
            var rt = go.GetComponent<RectTransform>(); rt.anchorMin = rt.anchorMax = new Vector2(0, 1); rt.pivot = new Vector2(0, 1); rt.anchoredPosition = pos; rt.sizeDelta = sizeDelta;
            return t;
        }
    }
}
