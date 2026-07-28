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
            RefreshSessionCard();
            RefreshTrialCard();
            RefreshBaselineCard();
            RefreshForcesGizmo();
            RefreshConnectionsCard();
            RefreshTransport();
            RefreshOverheadTuning();
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
        // 54 px strip, a camera feed does not. The video height is set here
        // rather than from _cellH so the two can diverge without disturbing
        // the scalar grid's row maths.
        private const int FrameCellH = 132;
        // How wide a single feed may get before it would crowd its neighbour.
        // Generous enough for a 2:1 panorama at full height; an 8:3 eye pair
        // hits it and scales down proportionally.
        private float _maxFrameW => _cellW * 1.6f;

        // Grid layout — shared by BuildSensorGrid and RelayoutGrid so the live
        // reflow lands cells in exactly the same slots the initial build uses.
        private const int GridCols = 4;
        private const float GridX0 = 496, GridY0 = -60, GridColGap = 14, GridRowGap = 12;
        private const float GridTitleH = 18, GridValH = 16;
        private float GridRowH => _cellH + GridTitleH + GridValH;
        /// <summary>Frame rows are taller than scalar rows, so the frame box
        /// and its packing must size off this rather than GridRowH — otherwise
        /// the box is drawn for short rows and the feeds overflow it.</summary>
        private float FrameRowH => FrameCellH + GridTitleH + GridValH;
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

            PackSection(isFrame: false);
            PackSection(isFrame: true);
        }

        private void PackSection(bool isFrame)
        {
            int idx = 0;
            foreach (var p in _panels)
            {
                if (p.isFrame != isFrame) continue;
                bool active = IsPanelActive(p);
                if (p.cell.activeSelf != active) p.cell.SetActive(active);
                if (!active) continue;
                int col = idx % GridCols, row = idx / GridCols;
                float rowPitch = (isFrame ? FrameRowH : GridRowH) + GridRowGap;
                p.cellRt.anchoredPosition = new Vector2(
                    SectionContentX + col * (_cellW + GridColGap),
                    SectionContentY - row * rowPitch);
                idx++;
            }
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
                p.value.text = manager.IsInPlayback ? "playback" : "live"; p.value.color = (Color)WaveColor;
            }
            else (p.value.text, p.value.color) = manager.GetStatus(p.frameChannel) switch
            {
                ChannelStatus.Disabled => ("disabled", new Color(0.85f,0.75f,0.25f)),
                ChannelStatus.NoSignal => ("no signal", _estop),
                _                      => ("not attached", _pending)
            };
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

        private void HandleKeyboard()
        {
            var kb = Keyboard.current; if (kb == null) return;
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
        private readonly List<string> _log = new();
        private Text _logText;
        private Text _baselineText;
        private SessionController.Phase _lastPhase = (SessionController.Phase)(-1);
        private int _lastIter = -1;

        private void Log(string line)
        {
            _log.Add($"{DateTime.Now:HH:mm:ss}  {line}");
            if (_log.Count > 14) _log.RemoveAt(0);
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
            var title = Txt(_root, "DELPHI — Experiment Control", 24, _text, new Vector2(24, -16), new Vector2(700, 32));
            title.fontStyle = FontStyle.Bold;

            BuildSessionCard(_root);
            BuildTrialCard(_root);
            BuildBaselineCard(_root);
            BuildSensorGrid(_root);
            BuildForcesGizmo(_root);
            BuildConnectionsCard(_root);
            BuildEventLog(_root);
            BuildTransport(_root);
        }

        private void BuildSessionCard(Transform root)
        {
            var card = Card(root, "Session", new Vector2(24, SessionCardY), new Vector2(452, SessionCardH));
            var host = card.transform;

            _preGroup = Empty(host, "pre");

            // Counterbalancing order 1-6: one button per permutation of the
            // three conditions. This IS the group assignment — it's recorded
            // as GroupID in every CSV — so it's a switch rather than a typed
            // field: a typo in a free-text group box would silently mislabel
            // the whole session's data.
            Txt(_preGroup.transform, "Counterbalancing order", 13, _dim, new Vector2(16, -34), new Vector2(200, 20));
            for (int i = 0; i < SessionController.OrderCount; i++)
            {
                int order = i + 1; // capture per-iteration, not the loop variable
                _orderImg[i] = Btn(_preGroup.transform, order.ToString(),
                    new Vector2(212 + i * 39, -32), new Vector2(35, 26),
                    () => session.orderIndex = order, out _);
            }
            _orderPreviewTxt = Txt(_preGroup.transform, "", 12, _accent, new Vector2(16, -58), new Vector2(422, 18));

            // Participant identifier — set per session here, not in the
            // Inspector (up to ~30 participants; a fixed Inspector value would
            // mean re-editing the prefab/scene between every run).
            Txt(_preGroup.transform, "Participant ID", 13, _dim, new Vector2(16, -86), new Vector2(110, 20));
            _userIdField = Field(_preGroup.transform, new Vector2(132, -84), new Vector2(140, 26), "P01");
            _userIdField.text = session.userId;
            _userIdField.onValueChanged.AddListener(v => session.userId = v);

            _startImg = Btn(_preGroup.transform, "START SESSION", new Vector2(16, -148), new Vector2(422, 48),
                () => session.StartSession(), out _);
            _startImg.color = _btnSel;

            _liveGroup = Empty(host, "live");
            _phaseTxt = Txt(_liveGroup.transform, "—", 20, _running, new Vector2(16, -34), new Vector2(422, 26)); _phaseTxt.fontStyle = FontStyle.Bold;
            _statusTxt = Txt(_liveGroup.transform, "", 13, _dim, new Vector2(16, -60), new Vector2(422, 32));
            for (int i = 0; i < _condLabel.Length; i++)
            {
                float y = -96 - i * 23; // 3 rows now — tightened to clear the bar below
                _condLabel[i] = Txt(_liveGroup.transform, $"Cond {i + 1}", 14, _pending, new Vector2(16, y), new Vector2(200, 22));
                _condTime[i] = Txt(_liveGroup.transform, "", 13, _pending, new Vector2(210, y), new Vector2(228, 22));
                _condTime[i].alignment = TextAnchor.UpperRight;
            }
            _condBar = Bar(_liveGroup.transform, new Vector2(16, -166), new Vector2(422, 10), _accent);

            // Contextual buttons (shared row ~ -176)
            var wide = new Vector2(422, 38); var pos = new Vector2(16, -178);
            _qDoneBtn = BtnObj(host, "QUESTIONNAIRE DONE", pos, wide, () => session.ConfirmQuestionnaire());
            _breakBtn = BtnObj(host, "BREAK", new Vector2(16, -178), new Vector2(205, 38), () => session.ChooseBreak());
            _continueBtn = BtnObj(host, "CONTINUE", new Vector2(233, -178), new Vector2(205, 38), () => session.ChooseContinue());
            _resumeBreakBtn = BtnObj(host, "RESUME NEXT CONDITION", pos, wide, () => session.ResumeFromBreak());
            // Explore condition only — shares the row with END FREE-PLAY, the
            // same way BREAK/CONTINUE split it.
            _nudgeBtn = BtnObj(host, "PLAY EXPLORE NUDGE", new Vector2(16, -178), new Vector2(205, 38), () => session.PlayExploreNudge());
            _endFreePlayBtn = BtnObj(host, "END FREE-PLAY", new Vector2(233, -178), new Vector2(205, 38), () => session.EndFreePlay());

            var estopImg = Btn(host, "EMERGENCY STOP", new Vector2(16, -240), new Vector2(422, 46), () => session.EmergencyStop(), out _);
            _estopBtn = estopImg.GetComponent<Button>(); estopImg.color = _estop;
            var resumeImg = Btn(host, "RESUME", new Vector2(16, -240), new Vector2(422, 46), () => session.Resume(), out _);
            _estopResumeBtn = resumeImg.GetComponent<Button>(); resumeImg.color = _btnSel;
            _estopBanner = Txt(host, "STOPPED — platform returning, passthrough on", 12, _estop, new Vector2(16, -290), new Vector2(422, 18));
        }

        // Left-column layout, each derived from the one above it so a height
        // change here can't silently leave the next card overlapping —
        // that already happened once by hand-matching two magic numbers.
        private const float SessionCardY = -60, SessionCardH = 300;
        private const float TrialCardY = SessionCardY - SessionCardH - 12, TrialCardH = 258;
        private const float BaselineCardY = TrialCardY - TrialCardH - 8, BaselineCardH = 152;

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
            var card = Card(root, "Trial Progress — driven by the session automatically",
                new Vector2(24, TrialCardY), new Vector2(452, TrialCardH));
            var host = card.transform;
            _optDot = Dot(host, new Vector2(16, -32)); _optLabel = Txt(host, "optimizer: idle", 13, _pending, new Vector2(34, -30), new Vector2(404, 20));
            _iterTxt = Txt(host, "iteration — / —", 14, _dim, new Vector2(16, -54), new Vector2(240, 22));
            _hvTxt = Txt(host, "hypervolume  —", 14, _dim, new Vector2(210, -54), new Vector2(228, 22)); _hvTxt.alignment = TextAnchor.UpperRight;

            _trackPosTxt = Txt(host, "Track: —", 13, _accent, new Vector2(16, -76), new Vector2(422, 20));

            for (int i = 0; i < ParamLabels.Length; i++)
            {
                float y = -106 - i * 24;
                Txt(host, ParamLabels[i], 12, _dim, new Vector2(16, y), new Vector2(96, 20));
                _paramBars[i] = Bar(host, new Vector2(116, y - 2), new Vector2(280, 14), _running);
                _paramVals[i] = Txt(host, "0.50", 12, _dim, new Vector2(402, y), new Vector2(40, 20));
            }
        }

        private void BuildBaselineCard(Transform root)
        {
            var card = Card(root, "Baseline — captured during the meditation", new Vector2(24, BaselineCardY), new Vector2(452, BaselineCardH));
            _baselineText = Txt(card.transform, "No baseline captured yet.", 12, _dim, new Vector2(16, -32), new Vector2(422, BaselineCardH - 42));
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

        // EVENT LOG sits below BASELINE now, not directly below TRIAL.
        // Event Log lives to the right of the Sensor Grid — same top edge
        // (GridY0), sized with the exact same box-width formula
        // BuildSensorGrid uses for its own boxes (that formula is fixed —
        // channel count only changes box HEIGHT there, not width).
        private float RightColumnX => GridX0 + (GridCols * (_cellW + GridColGap) - GridColGap + 32f) + 24f;

        private void BuildEventLog(Transform root)
        {
            float y = GridY0 - GizmoCardH - 12 - ConnectionsCardH - 12;
            var card = Card(root, "Event Log", new Vector2(RightColumnX, y), new Vector2(452, 300));
            _logText = Txt(card.transform, "", 12, _dim, new Vector2(16, -32), new Vector2(422, 258));
            _logText.alignment = TextAnchor.UpperLeft;
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
            var card = Card(root, "Forces (commanded)", new Vector2(RightColumnX, GridY0), new Vector2(452, GizmoCardH));
            var host = card.transform;

            float centerY = -(GizmoGraphicsH - CardHeaderH) / 2f - 6f;
            _crosshairOrigin = new Vector2(120, centerY);
            _compassOrigin = new Vector2(330, centerY);

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

            _gizmoValuesTxt = Txt(host, "", 12, _dim, new Vector2(16, -(GizmoGraphicsH - CardHeaderH - 14)), new Vector2(420, 20));

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
        private const float SpeedBarX = 16, SpeedBarW = 420;
        private Text _speedBigTxt, _speedTargetTxt, _speedLimiterTxt;
        private Slider _speedBar;
        private Image _speedTargetTick;

        private void BuildSpeedStrip(Transform host)
        {
            float y = -GizmoGraphicsH;

            _speedBigTxt = Txt(host, "— km/h", 24, _accent, new Vector2(SpeedBarX, y + 2), new Vector2(150, 34));
            _speedBigTxt.fontStyle = FontStyle.Bold;

            _speedTargetTxt = Txt(host, "", 12, _dim, new Vector2(168, y + 2), new Vector2(268, 18));
            _speedLimiterTxt = Txt(host, "", 12, _dim, new Vector2(168, y - 14), new Vector2(268, 18));
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
            var card = Card(root, "Connections", new Vector2(RightColumnX, y), new Vector2(452, ConnectionsCardH));
            var host = card.transform;

            _connYawDot = Dot(host, new Vector2(16, -34));
            _connYawTxt = Txt(host, "YAW VR3: —", 13, _dim, new Vector2(34, -30), new Vector2(404, 20));
            _connYawCuesTxt = Txt(host, "", 11, _dim, new Vector2(34, -50), new Vector2(404, 16));

            _connBoDot = Dot(host, new Vector2(16, -78));
            _connBoTxt = Txt(host, "BO Hub: —", 13, _dim, new Vector2(34, -74), new Vector2(404, 20));

            _connGsrDot = Dot(host, new Vector2(16, -102));
            _connGsrTxt = Txt(host, "GSR: —", 13, _dim, new Vector2(34, -98), new Vector2(404, 20));

            _connPolarDot = Dot(host, new Vector2(16, -126));
            _connPolarTxt = Txt(host, "Polar H10: —", 13, _dim, new Vector2(34, -122), new Vector2(404, 20));

            _yawMotionImg = Btn(host, "START MOTION", new Vector2(16, -156), new Vector2(422, 32),
                ToggleYawMotion, out _yawMotionTxt);
            _yawRumbleImg = Btn(host, "RUMBLE: ON", new Vector2(16, -196), new Vector2(422, 24),
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

        /// <summary>Two clearly separate, individually labelled boxes — one
        /// for scalar (physiological/behavioral) channels, one for camera
        /// feeds — instead of one undifferentiated mixed grid, so it's
        /// obvious at a glance which kind of sensor a cell is without reading
        /// each label individually.</summary>
        private void BuildSensorGrid(Transform root)
        {
            var scalar = DelphiManager.AllChannels; var frame = DelphiManager.AllFrameChannels;

            int scalarRows = Mathf.Max(1, Mathf.CeilToInt(scalar.Length / (float)GridCols));
            int frameRows = Mathf.Max(1, Mathf.CeilToInt(frame.Length / (float)GridCols));
            float boxW = GridCols * (_cellW + GridColGap) - GridColGap + 32f;
            float scalarBoxH = CardHeaderH + scalarRows * GridRowH + (scalarRows - 1) * GridRowGap + 16f;
            float frameBoxH = CardHeaderH + frameRows * FrameRowH + (frameRows - 1) * GridRowGap + 16f;

            var scalarBox = Card(root, "Scalar Sensors — physiological & behavioral signals",
                new Vector2(GridX0, GridY0), new Vector2(boxW, scalarBoxH));
            var frameBox = Card(root, "Frame Sensors — camera / video feeds",
                new Vector2(GridX0, GridY0 - scalarBoxH - 16f), new Vector2(boxW, frameBoxH));

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
            float rowH = contentH + titleH + valH;
            string label = isFrame ? DelphiManager.FrameMeta(frameChannel).label : DelphiManager.Meta(channel).label;

            // Position is provisional — RelayoutGrid() packs the ACTIVE cells
            // with no gaps and hides the rest immediately after this returns.
            var cont = Empty(parent, $"Cell_{label}"); var crt = cont.GetComponent<RectTransform>();
            crt.sizeDelta = new Vector2(_cellW, rowH);

            var t = Txt(cont.transform, label.TrimEnd(' ', '(', ')'), 13, new Color(0.8f,0.85f,0.95f), new Vector2(0, 0), new Vector2(_cellW, titleH));
            var v = Txt(cont.transform, "…", 12, _pending, new Vector2(0, -titleH), new Vector2(_cellW, valH)); v.alignment = TextAnchor.UpperRight;

            var imgGO = new GameObject("Content", typeof(RawImage)); imgGO.transform.SetParent(cont.transform, false);
            var raw = imgGO.GetComponent<RawImage>(); raw.color = Color.white;
            var irt = imgGO.GetComponent<RectTransform>();
            irt.anchorMin = irt.anchorMax = new Vector2(0, 1); irt.pivot = new Vector2(0, 1);
            irt.anchoredPosition = new Vector2(0, -(titleH + valH)); irt.sizeDelta = new Vector2(_cellW, contentH);

            var panel = new Panel { isFrame = isFrame, cell = cont, cellRt = crt, title = t, value = v, image = raw };
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

        private void BuildTransport(Transform root)
        {
            var bar = NewImage(root, _card); var brt = bar.rectTransform;
            brt.anchorMin = new Vector2(0, 0); brt.anchorMax = new Vector2(1, 0); brt.pivot = new Vector2(0.5f, 0);
            brt.anchoredPosition = new Vector2(0, 0); brt.sizeDelta = new Vector2(-40, 132); brt.anchoredPosition = new Vector2(0, 12);
            var host = bar.transform;

            _recImg = Btn(host, "REC", new Vector2(16, -12), new Vector2(84, 32), ToggleRecord, out _recTxt);
            _nameField = Field(host, new Vector2(108, -12), new Vector2(260, 32), "Session name (optional)");
            _recStatus = Txt(host, "idle", 15, _dim, new Vector2(380, -14), new Vector2(360, 28));
            _quitImg = Btn(host, QuitIdleLabel, new Vector2(756, -12), new Vector2(180, 32),
                           OnQuitClicked, out _quitTxt);

            float y = -52;
            Btn(host, "<", new Vector2(16, y), new Vector2(32, 32), () => CycleSession(-1), out _);
            _sessionLabel = Txt(host, "…", 15, _dim, new Vector2(52, y - 2), new Vector2(300, 32)); _sessionLabel.alignment = TextAnchor.MiddleCenter;
            Btn(host, ">", new Vector2(356, y), new Vector2(32, 32), () => CycleSession(1), out _);
            _loadBtn = Btn(host, "Load", new Vector2(396, y), new Vector2(76, 32), ToggleLoad, out _loadTxt).GetComponent<Button>();
            _playBtn = Btn(host, "Play", new Vector2(482, y), new Vector2(76, 32), () => player?.TogglePlay(), out _playTxt).GetComponent<Button>();
            _stepBackBtn = Btn(host, "-1f", new Vector2(566, y), new Vector2(48, 32), () => player?.StepFrames(-1), out _).GetComponent<Button>();
            _stepFwdBtn = Btn(host, "+1f", new Vector2(618, y), new Vector2(48, 32), () => player?.StepFrames(1), out _).GetComponent<Button>();
            _speedBtn = Btn(host, "1x", new Vector2(674, y), new Vector2(58, 32),
                () => CycleSpeed(_speedIdx >= Speeds.Length - 1 ? -(Speeds.Length - 1) : 1), out _speedTxt).GetComponent<Button>();
            _timeLabel = Txt(host, "--:-- / --:--", 15, _dim, new Vector2(742, y - 2), new Vector2(240, 32)); _timeLabel.alignment = TextAnchor.MiddleLeft;

            _scrubber = Scrub(host, new Vector2(16, -96), new Vector2(-32, 22));
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

        private Transform Card(Transform parent, string title, Vector2 pos, Vector2 size)
        {
            var img = NewImage(parent, _card); var rt = img.rectTransform; rt.anchoredPosition = pos; rt.sizeDelta = size;

            var header = NewImage(img.transform, _card2);
            var hrt = header.rectTransform; hrt.sizeDelta = new Vector2(size.x, CardHeaderH);

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
