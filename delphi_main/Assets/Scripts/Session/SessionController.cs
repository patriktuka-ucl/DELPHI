using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Delphi.Simulation;
using Delphi.Trial;
using QuestionnaireToolkit.Scripts;

namespace Delphi.Session
{
    /// <summary>
    /// Orchestrates the WHOLE participant session — session-level pacing
    /// (Intro/Meditation/Parking/BreakOffer/FreePlay) AND each
    /// condition's optimization trial (baseline → iteration loop → BO
    /// communication → objective submission). These used to be two separate
    /// classes (SessionController + TrialManager) doing the same job — one
    /// linear "what happens when" state machine — with real duplication.
    /// Merged into one so there's a single source of truth. BoBridge stays
    /// separate: its ONLY job is the socket/process plumbing to mobo.py —
    /// this class decides WHEN to talk to it and what to send.
    ///
    /// mobo.py is a ONE-RUN-PER-PROCESS script — it accepts a single
    /// connection, runs exactly one condition's optimization, then exits.
    /// So "keep the optimizer available" means: launch it the moment Play
    /// starts (see Awake/PrewarmOptimizer), and immediately launch the NEXT
    /// one the instant a condition finishes — never leave a gap where no
    /// process is booting/connected. It is never torn down by a pause
    /// (EmergencyStop no longer aborts the trial — see EmergencyStop/Resume).
    ///
    /// The plan is one linear SEGMENT list built once at StartSession from the
    /// counterbalancing order the researcher picked (1–6, see
    /// CounterbalanceOrders). All THREE conditions — Implicit, Explicit and
    /// FreeRoam — get identical scaffolding, so the only thing that differs
    /// between them is what happens during the drive itself:
    ///
    ///   Intro → Meditation
    ///         → Condition[0] → Parking → Questionnaire → BreakOffer
    ///         → Condition[1] → Parking → Questionnaire → BreakOffer
    ///         → Condition[2] → Parking → Questionnaire → Complete
    ///
    /// where a Condition is (intro → baseline → iterations) for Implicit and
    /// Explicit, and (intro → open-ended roaming, ended by the researcher's
    /// DONE button) for FreeRoam. The break is offered BETWEEN conditions only.
    ///
    /// The closing interview happens IN PERSON, after the headset/screen
    /// experience ends — there is deliberately no in-app Interview phase.
    ///
    /// orderIndex/userId/conditionId are runtime state set by ExperimentUI
    /// (the researcher's actual control surface) right before StartSession() —
    /// not Inspector-configured, since they change every participant/session,
    /// not every build. groupId is derived from orderIndex, not typed.
    /// </summary>
    public class SessionController : MonoBehaviour, IQuestionnaireOptimizationBridge
    {
        public enum Phase
        {
            Idle, Intro, Meditation, ConditionIntro,
            Baseline, WaitingForOptimizer, WaitingForParameters, Washout, Measuring, AwaitingRating,
            Parking, Questionnaire, BreakOffer, FreePlay,
            Complete, EmergencyStop, Error
        }

        /// <summary>The three conditions. FreeRoam is a full peer of the other
        /// two — same intro/parking/questionnaire/break scaffolding — it just
        /// has no optimizer loop: the participant roams until they say they're
        /// done and the researcher ends it. Append-only: the value is
        /// serialized in the segment plan and written to every CSV.</summary>
        public enum ConditionKind { Implicit, Explicit, FreeRoam }

        /// <summary>The six ways three conditions can be ordered. The
        /// researcher picks one per participant (1–6); index 0..5 maps to that
        /// number. The table is FIXED and must never be reordered — the chosen
        /// number is recorded as GroupID in every CSV, so "order 4" has to mean
        /// the same sequence at analysis time as it did on the day.</summary>
        public static readonly ConditionKind[][] CounterbalanceOrders =
        {
            new[] { ConditionKind.Implicit, ConditionKind.Explicit, ConditionKind.FreeRoam },
            new[] { ConditionKind.Implicit, ConditionKind.FreeRoam, ConditionKind.Explicit },
            new[] { ConditionKind.Explicit, ConditionKind.Implicit, ConditionKind.FreeRoam },
            new[] { ConditionKind.Explicit, ConditionKind.FreeRoam, ConditionKind.Implicit },
            new[] { ConditionKind.FreeRoam, ConditionKind.Implicit, ConditionKind.Explicit },
            new[] { ConditionKind.FreeRoam, ConditionKind.Explicit, ConditionKind.Implicit },
        };

        public const int OrderCount = 6;

        /// <summary>The order this participant is running, as the 1–6 the
        /// researcher actually picks. Clamped, so a bad value can't index off
        /// the table mid-session.</summary>
        public static ConditionKind[] OrderFor(int oneBasedOrder) =>
            CounterbalanceOrders[Mathf.Clamp(oneBasedOrder, 1, OrderCount) - 1];

        /// <summary>"Implicit → FreeRoam → Explicit" — for the researcher UI
        /// and the session log, so the picked order is legible at a glance
        /// instead of being a bare number.</summary>
        public static string DescribeOrder(int oneBasedOrder) =>
            string.Join(" → ", OrderFor(oneBasedOrder));

        /// <summary>What feeds the optimizer's objectives for the condition
        /// currently running. Physiology: the baseline-deviation pipeline
        /// (Implicit). Questionnaire: each iteration's objective is the
        /// participant's raw submitted rating, sent to mobo.py as-is with its
        /// own [questionnaireMin, questionnaireMax] bounds — see
        /// SubmitQuestionnaireObjectives. Physiological channels keep
        /// recording throughout either way — this only controls what's SENT
        /// TO THE OPTIMIZER.</summary>
        public enum ObjectiveSource { Physiology, Questionnaire }

        /// <summary>The only things that genuinely differ per condition kind:
        /// how long the baseline is and how many iterations to run. Shared BO
        /// mechanics (activation, boundK, window/washout timing, questionnaire
        /// range, pythonPath) live in the BO Hub section below instead.</summary>
        [Serializable]
        public class ConditionTrialConfig
        {
            [Tooltip("Stationary baseline before this condition's drive, seconds.")]
            [Min(10f)] public float baselineSeconds = 120f;
            [Tooltip("Only this many seconds at the END of the baseline are " +
                     "averaged into the reference means.")]
            [Min(1f)] public float baselineAveragingSeconds = 30f;
            [Tooltip("Total number of parameter sets the optimizer gets to try.")]
            [Min(2)] public int iterations = 56;
            [Tooltip("How many of those iterations are quasi-random (Sobol) " +
                     "exploration before model-guided optimization starts.")]
            [Min(1)] public int samplingIterations = 12;
        }

        // ── Segment plan (session-level pacing) ─────────────────────────
        private enum SegmentKind
        {
            Intro, Meditation, Condition,
            Parking, Questionnaire, BreakOffer, Complete
        }

        private struct Segment
        {
            public SegmentKind kind;
            public float seconds;         // timed segments only
            public ConditionKind condition; // Condition segments only
        }

        [Header("Links (auto-found if left empty)")]
        public DelphiManager manager;
        public CarDriver carDriver;
        [Tooltip("Recording has to start/stop in sync with each condition's " +
                 "trial (so sensors.csv/videos/trial_log all land in the same " +
                 "session folder) — that's the only reason this class needs it.")]
        public SessionRecorder recorder;
        [Tooltip("Plays the spoken instructions at each phase transition.")]
        public NarrationController narration;
        [Tooltip("The per-iteration rating questionnaire, shown during Explicit " +
                 "conditions' AwaitingRating phase.")]
        public QTQuestionnaireManager questionnaire;
        [Tooltip("The post-condition evaluation questionnaire, shown during the " +
                 "Questionnaire phase (after Parking, before BreakOffer).")]
        public QTQuestionnaireManager finalEvaluationQuestionnaire;

        // ── Set by ExperimentUI at runtime, not Inspector-configured ─────
        // (a researcher picks these fresh per participant/session, not once
        // per build — see ExperimentUI's session-setup controls.)
        [HideInInspector] public int orderIndex = 1; // 1..6, see CounterbalanceOrders
        [HideInInspector] public string userId = "P1";
        [HideInInspector] public string conditionId = "pilot"; // computed internally per condition, not set by anyone

        /// <summary>The counterbalancing order (1–6) this participant ran,
        /// which is exactly what the BO framework's "GroupID" column should
        /// carry: derived from <see cref="orderIndex"/> rather than typed, so
        /// the recorded group can never disagree with the order actually run.
        /// </summary>
        public string groupId => orderIndex.ToString();

        [Header("Timed-phase durations (seconds)")]
        [Tooltip("Self-park settle time before the questionnaire.")]
        [Min(0f)] public float parkingSeconds = 10f;

        [Header("Trial structure — per condition kind")]
        public ConditionTrialConfig implicitTrial = new();
        public ConditionTrialConfig explicitTrial = new();

        [Header("BO Hub — everything that configures the optimizer")]
        [Tooltip("Shaping applied to every physiology channel's signed " +
                 "deviation before it goes to the optimizer (Physiology " +
                 "objective only — Questionnaire ignores this entirely).")]
        public ActivationFunction activation = ActivationFunction.Linear;
        [Tooltip("Bound half-width in SDs: each channel's bounds are " +
                 "baseline ± k·SD, and a window's deviation reaches ±1 there.")]
        [Min(0.1f)]
        public float boundK = 3f;
        [Tooltip("Per-channel literature SD + bad-direction. Auto-populated " +
                 "from the plugged-in DelphiManager's enabled channels.")]
        public List<ChannelNormalization> channelConfigs = new();
        [Tooltip("The rating scale's own range — matches the 7-point Likert " +
                 "items in the placeholder questionnaires. Sent to mobo.py " +
                 "as-is (raw rating, not pre-normalized) so its own CSV log " +
                 "shows the real rating instead of an abstract number. Higher " +
                 "is treated as the better outcome.")]
        public float questionnaireMin = 1f;
        public float questionnaireMax = 7f;
        [Tooltip("Seconds per iteration — one parameter set is active for " +
                 "exactly this long before the next one is requested.")]
        [Min(1f)] public float windowSeconds = 40f;
        [Tooltip("Seconds discarded at the start of each window before " +
                 "measurement begins. Must cover BOTH the parameter ramp " +
                 "(Transition, right below) AND physiological lag (GSR ≈ 1–4s, " +
                 "HR ≈ 5–10s) — too short and the window measures the tail of " +
                 "the PREVIOUS parameter set.")]
        [Min(0f)] public float washoutSeconds = 10f;
        [Tooltip("When the optimizer hands over a new parameter set, ramp " +
                 "LINEARLY to it over this many seconds instead of snapping — " +
                 "an instant jolt is itself a startle stimulus. Clamped to " +
                 "Washout above so measurement never starts mid-ramp.")]
        [Min(0f)] public float transitionSeconds = 3f;
        [Tooltip("Empty = auto: the project-local venv at BOPythonEnv " +
                 "(Scripts/python.exe on Windows, bin/python3 elsewhere).")]
        public string pythonPath = "";
        public int seed = 3;

        /// <summary>Seconds of the window actually averaged into the
        /// objective, after washout — computed, not separately configured.</summary>
        public float MeasureSeconds => Mathf.Max(0f, windowSeconds - EffectiveWashoutSeconds);
        private float EffectiveWashoutSeconds => Mathf.Min(washoutSeconds, windowSeconds);

        // ── Runtime state (read by ExperimentUI / editor) ───────────────
        public enum OptimizerStatus { NotStarted, Starting, Connected, Disconnected }

        public Phase CurrentPhase { get; private set; } = Phase.Idle;
        public string StatusLine { get; private set; } = "Idle";
        public int ConditionNumber { get; private set; }
        public int ConditionCount => OrderCount == 0 ? 0 : CounterbalanceOrders[0].Length;
        public bool IsAwaitingResearcher { get; private set; }
        public bool AwaitingBreakResume { get; private set; }
        public ConditionKind CurrentConditionKind { get; private set; }
        public bool CanStart => CurrentPhase == Phase.Idle || CurrentPhase == Phase.Complete;
        public double PhaseSecondsRemaining =>
            _phaseEnd > 0 ? Math.Max(0, _phaseEnd - DelphiClock.Now) : 0;
        /// <summary>True while a condition's baseline/iteration loop is
        /// actively running.</summary>
        public bool IsRunningCondition => CurrentPhase is Phase.Baseline or Phase.WaitingForOptimizer
            or Phase.WaitingForParameters or Phase.Washout or Phase.Measuring or Phase.AwaitingRating;

        public int Iteration { get; private set; }
        public int TotalIterations => _activeConfig?.iterations ?? 0;
        public float LastCoverage { get; private set; } = float.NaN;

        public OptimizerStatus Optimizer
        {
            get
            {
                if (_bo == null) return OptimizerStatus.NotStarted;
                if (_bo.Connected) return OptimizerStatus.Connected;
                if (_bo.ProcessAlive) return OptimizerStatus.Starting;
                return OptimizerStatus.Disconnected;
            }
        }

        // ── Segment plan state ──────────────────────────────────────────
        private readonly List<Segment> _plan = new();
        private int _segmentIndex;
        private double _phaseEnd;        // DelphiClock time the current timed/trial phase ends
        private Phase _interruptedPhase; // what EmergencyStop/Fail paused, for Resume
        private int _interruptedSegment; // Fail()'s Resume path only — rewinds and restarts the segment

        // ── EmergencyStop/Resume pause state (Phase.EmergencyStop only) ──
        private double _pausedRemaining = -1;  // remaining _phaseEnd countdown at the moment of pausing, -1 = none was running
        private bool _pausedIsAwaitingResearcher;
        private bool _pausedAwaitingBreakResume;
        private string _pausedStatusLine;
        private bool _wasParkedBeforeStop;     // car was deliberately parked (Parking/AwaitingRating) before the halt

        // ── Trial runtime state ──────────────────────────────────────────
        private BoBridge _bo;
        private bool _quitting; // QuitSession blocks for seconds — guards re-entry
        private string _lastBoLaunchError;
        private bool _trialActuallyStarted;
        private double _trialStart;
        private double _driveStart;
        private WindowAccumulator _acc;
        private double _measureStart;
        private ConditionTrialConfig _activeConfig; // implicitTrial or explicitTrial, whichever is running
        private ObjectiveSource _objectiveSource;
        private readonly Dictionary<Channel, float> _baseline = new();
        private List<Channel> _objectiveChannels = new();
        private List<string> _questionnaireKeys = new();
        private readonly Dictionary<string, float> _pendingQuestionnaireValues = new();
        private Dictionary<string, float> _lastParams = new();
        private StreamWriter _trialLog;

        private Dictionary<string, float> _transFrom;
        private Dictionary<string, float> _transTo;
        private double _transStart;
        private float _transDuration;

        private List<string> _activeParamKeys = new();

        // ── Free-play state ──────────────────────────────────────────────
        private StreamWriter _freePlayLog;
        private double _freePlayStart;

        private static readonly string[] ParameterKeys =
        {
            "accelerationJerk", "brakingJerk", "followDistance",
            "corneringSpeed", "takeoverProbability", "speedBelowLimit"
        };

        private void Awake()
        {
            if (manager == null)    manager    = FindFirstObjectByType<DelphiManager>();
            if (carDriver == null)  carDriver  = FindFirstObjectByType<CarDriver>();
            if (recorder == null)   recorder   = FindFirstObjectByType<SessionRecorder>();
            if (narration == null)  narration  = FindFirstObjectByType<NarrationController>();

            // Auto-advance past Phase.Questionnaire once the participant
            // submits — no researcher button click needed, unlike the
            // "no questionnaire linked" fallback path in EnterSegment.
            if (finalEvaluationQuestionnaire != null)
                finalEvaluationQuestionnaire.onQuestionnaireFinished.AddListener(ConfirmQuestionnaire);

            // Launch mobo.py the moment Play starts, not when a trial starts —
            // torch import (several seconds) happens while the researcher is
            // still doing pre-session setup instead of during the baseline.
            PrewarmOptimizer();
        }

        // ── Public control (researcher UI) ──────────────────────────────
        /// <summary>Build the plan and begin. No-op unless idle/complete.
        /// orderIndex/userId should already be set by the caller (ExperimentUI)
        /// before calling this.</summary>
        public bool StartSession()
        {
            if (CurrentPhase != Phase.Idle && CurrentPhase != Phase.Complete)
                return false;

            if (!ValidateTrackForSession()) return false;

            BuildPlan();
            _segmentIndex = -1;
            ConditionNumber = 0;
            Debug.Log($"[Session] Starting — participant '{userId}', order {orderIndex}/{OrderCount} " +
                      $"({DescribeOrder(orderIndex)}), {_plan.Count} segments.");
            AdvanceToNextSegment();
            return true;
        }

        /// <summary>Refuse to start a session the track can't actually run.
        ///
        /// Without a Park marker RequestPark() only logs a warning and returns,
        /// so the Parking segment after each condition silently does nothing:
        /// the car keeps driving through the questionnaire and the break, and
        /// the NEXT condition's baseline is then recorded while it's still
        /// moving. That baseline is the reference the whole Implicit objective
        /// is computed against, so the session would look like it ran fine and
        /// produce quietly worthless physiological data. Better to not start.
        /// </summary>
        private bool ValidateTrackForSession()
        {
            if (carDriver == null)
            {
                Debug.LogError("[Session] No CarDriver — cannot start.");
                StatusLine = "No CarDriver in the scene";
                return false;
            }
            if (carDriver.track == null || !carDriver.track.IsReady)
            {
                Debug.LogError("[Session] The CarDriver has no ready Track — cannot start.");
                StatusLine = "Track not ready";
                return false;
            }
            if (!carDriver.track.TryGetPark(out _))
            {
                // Not fatal any more: CarDriver now brakes to a halt in place
                // when asked to park with no marker, so the baseline is still
                // recorded stationary. Only WHERE the participant ends up is
                // uncontrolled, which is a study-design concern rather than a
                // corrupted-data one — so warn loudly and let the run proceed.
                Debug.LogWarning("[Session] The track has no Park marker (TrackEventKind.Park). " +
                                 "The car will brake to a halt wherever it happens to be at each " +
                                 "Parking segment, so the participant won't stop at a consistent " +
                                 "place between conditions. Add a Park marker before collecting real data.");
            }
            return true;
        }

        /// <summary>Researcher: the participant has finished the on-screen
        /// questionnaire.</summary>
        public void ConfirmQuestionnaire()
        {
            if (CurrentPhase == Phase.Questionnaire) AdvanceToNextSegment();
        }

        public void ChooseBreak()
        {
            if (CurrentPhase != Phase.BreakOffer) return;
            narration?.Play(NarrationController.Line.BreakGranted);
            IsAwaitingResearcher = true;
            AwaitingBreakResume = true;
            StatusLine = "Break — waiting to resume the next condition";
        }

        public void ChooseContinue()
        {
            if (CurrentPhase != Phase.BreakOffer) return;
            narration?.Play(NarrationController.Line.ContinueDrive);
            AdvanceToNextSegment();
        }

        public void ResumeFromBreak()
        {
            if (CurrentPhase == Phase.BreakOffer && IsAwaitingResearcher)
            {
                IsAwaitingResearcher = false;
                narration?.Play(NarrationController.Line.ContinueDrive);
                AdvanceToNextSegment();
            }
        }

        /// <summary>Researcher: the participant has said they're done roaming.
        /// Ends the FreeRoam condition and falls into the SAME tail every other
        /// condition has — Parking (the car drives itself to the park marker),
        /// then the questionnaire, then the break offer.</summary>
        public void EndFreePlay()
        {
            if (CurrentPhase != Phase.FreePlay) return;
            _freePlayLog?.Close();
            _freePlayLog = null;
            if (recorder != null && recorder.IsRecording) recorder.StopRecording();
            AdvanceToNextSegment();
        }

        /// <summary>Live manual control during Phase.FreePlay — called by
        /// ExperimentUI whenever the researcher/participant drags a slider.
        /// Applies the value directly (no ramp — this is manual dial-in, not
        /// a BO handoff) and logs the change so the recording folder shows
        /// what was active when. No-op outside FreePlay.</summary>
        public void SetFreePlayParameter(string key, float value)
        {
            if (CurrentPhase != Phase.FreePlay || carDriver == null) return;
            SetParam(carDriver.parameters, key, Mathf.Clamp01(value));
            LogFreePlayRow();
        }

        /// <summary>Begin the FreeRoam condition proper, once its intro
        /// narration has finished. Deliberately does NOT touch the optimizer:
        /// the prewarmed process is left connected and untouched for whichever
        /// condition comes next, so a FreeRoam slot in the middle of the order
        /// costs nothing to restart afterwards.</summary>
        /// <summary>FreeRoam has no baseline and no iterations. This exists so
        /// the shared metadata writer has something to read, and so a FreeRoam
        /// folder on disk ends up the same shape as an Implicit/Explicit one —
        /// same sensors.csv, same videos, same trial_meta.json — rather than
        /// being a special case at analysis time.</summary>
        private static readonly ConditionTrialConfig FreeRoamConfig = new()
        {
            baselineSeconds = 0f,
            baselineAveragingSeconds = 0f,
            iterations = 0,
            samplingIterations = 1
        };

        private void StartFreeRoamCondition()
        {
            conditionId = "freeroam";
            CurrentPhase = Phase.FreePlay;
            IsAwaitingResearcher = true;

            // Recording is IDENTICAL across all three conditions: the sensor
            // csv and every video feed are driven by SessionRecorder, which
            // StartFreePlayLogging starts exactly as StartConditionTrial does.
            // These fields only make the trial metadata come out too.
            _activeConfig = FreeRoamConfig;
            _objectiveChannels = new List<Channel>();
            _baseline.Clear();
            Iteration = 0;
            LastCoverage = float.NaN;
            _trialStart = DelphiClock.Now;
            _driveStart = DelphiClock.Now;
            _trialActuallyStarted = true;
            StatusLine = $"Condition {ConditionNumber}/{ConditionCount} (FreeRoam) — " +
                          "roaming; press DONE when the participant says they've finished";

            // The car is parked coming into every condition (startParked, or
            // the previous condition's Parking segment). Nothing else releases
            // it here — the BO conditions un-park at their first iteration,
            // which FreeRoam never reaches — so it has to happen explicitly.
            carDriver?.ResumeDriving();
            StartFreePlayLogging();
        }

        private void StartFreePlayLogging()
        {
            if (recorder != null && !recorder.IsRecording)
                recorder.StartRecording($"trial_{userId}_{conditionId}");

            string dir = recorder != null && recorder.IsRecording
                ? recorder.CurrentSessionPath
                : Path.Combine(Application.persistentDataPath, "Trials");
            Directory.CreateDirectory(dir);
            _freePlayStart = DelphiClock.Now;
            _freePlayLog = new StreamWriter(Path.Combine(dir, "freeplay_log.csv"));
            var header = new StringBuilder("t_s");
            foreach (var key in ParameterKeys) header.Append(',').Append(key);
            _freePlayLog.WriteLine(header.ToString());
            _freePlayLog.Flush();
            LogFreePlayRow(); // capture the starting values too, not just changes
        }

        private void LogFreePlayRow()
        {
            if (_freePlayLog == null || carDriver == null) return;
            var p = carDriver.parameters;
            var row = new StringBuilder();
            row.Append(F(DelphiClock.Now - _freePlayStart));
            foreach (float v in new[] { p.accelerationJerk, p.brakingJerk, p.followDistance,
                                        p.corneringSpeed, p.takeoverProbability, p.speedBelowLimit })
                row.Append(',').Append(F(v));
            _freePlayLog.WriteLine(row.ToString());
            _freePlayLog.Flush();
        }


        /// <summary>Safety halt — usable at ANY point. TRUE pause, not an
        /// abort: halts the car in place and freezes whatever countdown is
        /// running (baseline/washout/measuring/intro-narration timers all
        /// share the same _phaseEnd mechanism), but leaves everything else —
        /// the optimizer connection, accumulated baseline/iteration data,
        /// the trial log, recording — completely untouched. Resume() picks
        /// back up in the SAME phase, so repeated stop/resume clicks can't
        /// skip ahead or re-run a condition (the old design restarted the
        /// whole condition segment on resume, which both threw away the live
        /// optimizer connection and — combined with EnterCondition not
        /// setting CurrentPhase until its intro narration finished — let a
        /// second Resume click during that window re-enter a second time).</summary>
        public void EmergencyStop()
        {
            if (CurrentPhase == Phase.EmergencyStop || CurrentPhase == Phase.Idle || CurrentPhase == Phase.Complete) return;
            _interruptedPhase = CurrentPhase;
            _pausedRemaining = _phaseEnd > 0 ? Math.Max(0, _phaseEnd - DelphiClock.Now) : -1;
            _pausedIsAwaitingResearcher = IsAwaitingResearcher;
            _pausedAwaitingBreakResume = AwaitingBreakResume;
            _pausedStatusLine = StatusLine;
            _wasParkedBeforeStop = carDriver != null && carDriver.IsParked;

            carDriver?.EmergencyHalt();
            CurrentPhase = Phase.EmergencyStop;
            _phaseEnd = 0;
            IsAwaitingResearcher = true;
            StatusLine = $"EMERGENCY STOP (was {_interruptedPhase})";
            narration?.Play(NarrationController.Line.EmergencyStop);
            Debug.LogWarning($"[Session] Emergency stop during {_interruptedPhase} — paused in place; " +
                              "optimizer connection and trial state untouched.");
        }

        /// <summary>Come back from an emergency stop (continues exactly where
        /// it paused) or a trial error (the trial was already torn down by
        /// Fail(), so this restarts the interrupted condition's segment
        /// cleanly instead — there's nothing left to resume in place).</summary>
        public void Resume()
        {
            if (CurrentPhase == Phase.EmergencyStop)
            {
                if (!_wasParkedBeforeStop) carDriver?.ResumeDriving();
                if (_pausedRemaining >= 0) _phaseEnd = DelphiClock.Now + _pausedRemaining;
                CurrentPhase = _interruptedPhase;
                IsAwaitingResearcher = _pausedIsAwaitingResearcher;
                AwaitingBreakResume = _pausedAwaitingBreakResume;
                StatusLine = _pausedStatusLine;
                narration?.Play(NarrationController.Line.ResumeAfterStop);
                Debug.Log($"[Session] Resumed — continuing {_interruptedPhase} in place.");
            }
            else if (CurrentPhase == Phase.Error)
            {
                IsAwaitingResearcher = false;
                narration?.Play(NarrationController.Line.ResumeAfterStop);
                _segmentIndex = _interruptedSegment - 1;
                Debug.Log($"[Session] Resuming after error — restarting segment {_interruptedSegment} ({_interruptedPhase}).");
                AdvanceToNextSegment();
            }
        }

        public void AbortTrial()
        {
            if (!IsRunningCondition) return;
            Cleanup("Aborted by user");
            PrewarmOptimizer(); // keep an optimizer warm for the next attempt
            CurrentPhase = Phase.Idle;
            StatusLine = "Aborted";
            Debug.Log("[Trial] Aborted.");
        }

        /// <summary>Researcher: end everything and close the application —
        /// safely. Every video feed is finalised (pending GPU readbacks are
        /// drained, then each ffmpeg encoder gets its stdin closed and is
        /// waited on, so no mp4 is left truncated/unplayable), the sensor csv
        /// and trial/free-roam logs are closed, the trial meta is written and
        /// the optimizer process is shut down — and only THEN does the app
        /// exit. Usable from ANY phase, including mid-condition: a session
        /// that has to be abandoned still leaves complete, playable data.
        ///
        /// Blocks the main thread for up to ~15s per feed while ffmpeg
        /// finalises. That freeze is the point — quitting before the encoders
        /// flush is exactly how you lose a participant's recording.</summary>
        public void QuitSession(string reason = "Quit by researcher")
        {
            // Finish() blocks, so the UI can't repaint and the researcher may
            // well click again — re-entering teardown would double-dispose.
            if (_quitting) return;
            _quitting = true;

            IsAwaitingResearcher = false;
            _phaseEnd = 0;
            StatusLine = "Quitting — finalising recordings…";
            Debug.Log("[Session] Quit requested — finalising recordings before exit.");

            // Stop the car before the long blocking wait, so it isn't left
            // driving itself while the encoders flush.
            carDriver?.EmergencyHalt();

            // Same teardown every other end-path uses, so quitting can't
            // produce a differently-shaped session folder than a normal finish.
            Cleanup(reason);

            CurrentPhase = Phase.Complete;
            Debug.Log("[Session] All recordings closed and processes stopped — exiting.");
            QuitApplication();
        }

        private static void QuitApplication()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        // ── Plan construction ───────────────────────────────────────────
        private void BuildPlan()
        {
            _plan.Clear();

            Add(SegmentKind.Intro);
            Add(SegmentKind.Meditation);

            // All three conditions get IDENTICAL scaffolding — intro, the
            // condition itself, park, questionnaire — so nothing about the
            // procedure differs between them except what happens during the
            // drive. The break is offered BETWEEN conditions only; after the
            // last one the session just ends.
            var order = OrderFor(orderIndex);
            for (int i = 0; i < order.Length; i++)
            {
                AddCondition(order[i]);
                Add(SegmentKind.Parking, parkingSeconds);
                Add(SegmentKind.Questionnaire);
                if (i < order.Length - 1) Add(SegmentKind.BreakOffer);
            }

            Add(SegmentKind.Complete);
        }

        private void Add(SegmentKind kind, float seconds = 0f) =>
            _plan.Add(new Segment { kind = kind, seconds = seconds });

        private void AddCondition(ConditionKind kind) =>
            _plan.Add(new Segment { kind = SegmentKind.Condition, condition = kind });

        // ── Segment walk ────────────────────────────────────────────────
        private void AdvanceToNextSegment()
        {
            _segmentIndex++;
            if (_segmentIndex >= _plan.Count) { EnterComplete(); return; }
            EnterSegment(_plan[_segmentIndex]);
        }

        private void EnterSegment(Segment seg)
        {
            IsAwaitingResearcher = false;
            AwaitingBreakResume = false;
            _phaseEnd = 0;

            switch (seg.kind)
            {
                case SegmentKind.Intro:
                    CurrentPhase = Phase.Intro;
                    narration?.Play(NarrationController.Line.Welcome);
                    StartTimer(NarrationSeconds(NarrationController.Line.Welcome), "Intro — welcome & task briefing");
                    break;

                case SegmentKind.Meditation:
                    CurrentPhase = Phase.Meditation;
                    narration?.Play(NarrationController.Line.Meditation);
                    StartTimer(NarrationSeconds(NarrationController.Line.Meditation), "Meditation — relax with calm music");
                    break;

                case SegmentKind.Condition:
                    EnterCondition(seg.condition);
                    break;

                case SegmentKind.Parking:
                    CurrentPhase = Phase.Parking;
                    narration?.Play(NarrationController.Line.Parking);
                    carDriver?.RequestPark();
                    StartTimer(seg.seconds, "Parking");
                    break;

                case SegmentKind.Questionnaire:
                    CurrentPhase = Phase.Questionnaire;
                    narration?.Play(NarrationController.Line.Questionnaire);
                    if (finalEvaluationQuestionnaire != null)
                    {
                        finalEvaluationQuestionnaire.StartQuestionnaire();
                        StatusLine = "Final evaluation questionnaire — waiting for participant";
                    }
                    else
                    {
                        IsAwaitingResearcher = true;
                        StatusLine = "Questionnaire — waiting for participant (no QTQuestionnaireManager linked)";
                    }
                    break;

                case SegmentKind.BreakOffer:
                    CurrentPhase = Phase.BreakOffer;
                    narration?.Play(NarrationController.Line.BreakOffer);
                    IsAwaitingResearcher = true;
                    StatusLine = "Break? — awaiting participant's choice";
                    break;

                case SegmentKind.Complete:
                    EnterComplete();
                    break;
            }
        }

        private const float DefaultNarrationFallbackSeconds = 3f; // no NarrationController linked at all

        private float NarrationSeconds(NarrationController.Line line) =>
            narration != null ? narration.WaitSeconds(line) : DefaultNarrationFallbackSeconds;

        private void EnterCondition(ConditionKind kind)
        {
            ConditionNumber++;
            CurrentConditionKind = kind;
            ResetParametersToNeutral();

            var introLine = kind switch
            {
                ConditionKind.Implicit => NarrationController.Line.IntroImplicit,
                ConditionKind.Explicit => NarrationController.Line.IntroExplicit,
                _                      => NarrationController.Line.IntroFreeRoam,
            };

            narration?.Play(introLine);
            CurrentPhase = Phase.ConditionIntro;
            StartTimer(NarrationSeconds(introLine), $"Condition {ConditionNumber}/{ConditionCount} ({kind}) — intro");
        }

        /// <summary>Middle of every parameter's 0–1 range — the neutral driving
        /// style each condition starts from.</summary>
        public const float NeutralParameterValue = 0.5f;

        /// <summary>Put every driving parameter back to neutral at the start of
        /// each condition. Without this a condition inherits whatever the
        /// previous one left behind — the last set the optimizer chose, or
        /// wherever the participant dragged the FreeRoam sliders — so the
        /// second and third conditions would begin from a different driving
        /// style than the first did. That is a straightforward order effect,
        /// and it would be baked in underneath the counterbalancing that
        /// exists to cancel exactly this.</summary>
        private void ResetParametersToNeutral()
        {
            if (carDriver == null) return;

            foreach (var key in ParameterKeys)
                SetParam(carDriver.parameters, key, NeutralParameterValue);

            // Drop any ramp still in flight from the previous condition, or
            // TickTransition would immediately drag these values back toward
            // that condition's last target.
            _transFrom = null;
            _transTo = null;
            _transDuration = 0f;
            _lastParams = new Dictionary<string, float>();

            Debug.Log($"[Trial] Driving parameters reset to {NeutralParameterValue:0.##} for the new condition.");
        }

        private void StartTimer(float seconds, string status)
        {
            _phaseEnd = DelphiClock.Now + Mathf.Max(0f, seconds);
            StatusLine = status;
        }

        private void EnterComplete()
        {
            CurrentPhase = Phase.Complete;
            ConditionNumber = 0;
            IsAwaitingResearcher = false;
            _phaseEnd = 0;
            StatusLine = "Session complete";
            narration?.Play(NarrationController.Line.Finished);
            Debug.Log("[Session] Complete.");
        }

        private bool Fail(string why)
        {
            Debug.LogError($"[Session] {why}");
            _interruptedPhase = CurrentPhase;
            _interruptedSegment = _segmentIndex;
            Cleanup($"Error: {why}");
            PrewarmOptimizer(); // ready for the retry Resume() triggers
            CurrentPhase = Phase.Error;
            StatusLine = why;
            IsAwaitingResearcher = true; // stop here; researcher decides what to do
            return false;
        }

        // ── Tick ────────────────────────────────────────────────────────
        private void Update()
        {
            // Independent of phase — a ramp started as we entered Washout must
            // keep advancing every frame until it completes.
            TickTransition();

            // Independent of phase too — keep trying to connect the moment a
            // (pre-warmed or freshly launched) optimizer process is booting,
            // rather than only polling once a condition's baseline starts.
            if (_bo != null && !_bo.Connected) _bo.TryConnect();

            switch (CurrentPhase)
            {
                case Phase.Intro:
                case Phase.Meditation:
                case Phase.ConditionIntro:
                case Phase.Parking:
                    if (_phaseEnd > 0 && DelphiClock.Now >= _phaseEnd)
                    {
                        if (CurrentPhase != Phase.ConditionIntro) AdvanceToNextSegment();
                        // FreeRoam has no optimizer/baseline/iteration loop —
                        // it's open-ended and ends on the researcher's button.
                        else if (CurrentConditionKind == ConditionKind.FreeRoam) StartFreeRoamCondition();
                        else StartConditionTrial(CurrentConditionKind);
                    }
                    break;

                case Phase.Baseline:              TickBaseline(); break;
                case Phase.WaitingForOptimizer:    TickWaitingForOptimizer(); break;
                case Phase.WaitingForParameters:
                case Phase.Washout:
                case Phase.Measuring:
                case Phase.AwaitingRating:         TickIterationLoop(); break;
            }

            // A dead optimizer process mid-condition is fatal.
            if (IsRunningCondition && _bo != null && !_bo.ProcessAlive)
                Fail("Optimizer process exited unexpectedly — see [BO] console output.");
        }

        // ── Trial: starting a condition ─────────────────────────────────
        private void StartConditionTrial(ConditionKind kind)
        {
            _trialActuallyStarted = false;

            if (manager == null || manager.Core == null) { Fail("No DelphiManager/core running."); return; }
            if (carDriver == null)                        { Fail("No CarDriver in the scene."); return; }

            conditionId = kind.ToString().ToLowerInvariant();
            _objectiveSource = kind == ConditionKind.Explicit ? ObjectiveSource.Questionnaire : ObjectiveSource.Physiology;
            _activeConfig = kind == ConditionKind.Implicit ? implicitTrial : explicitTrial;

            // Fail fast here rather than only at baseline-end — otherwise a
            // misconfigured Explicit trial wastes a full baseline period
            // before anyone notices.
            if (_objectiveSource == ObjectiveSource.Questionnaire)
            {
                if (questionnaire == null) { Fail("objectiveSource is Questionnaire but no QTQuestionnaireManager is linked."); return; }
                int headerCount = questionnaire.resultsHeaderItems?.Count ?? 0;
                if (headerCount < 2) { Fail("mobo.py needs ≥2 objectives — the linked questionnaire has fewer than 2 header items configured."); return; }
            }
            else if (CandidateChannels().Count < 2)
            {
                Fail("mobo.py needs ≥2 objectives — attach and enable at least two scalar sensors on DelphiManager.");
                return;
            }

            // Recording runs for the whole trial; csv + videos + trial log all
            // land in one session folder.
            if (recorder != null && !recorder.IsRecording)
                recorder.StartRecording($"trial_{userId}_{conditionId}");

            // Usually already booting/connected — PrewarmOptimizer() launches
            // it at Awake() and again right after each condition finishes.
            // Only launch it here as a fallback if that hasn't happened yet.
            if (_bo == null)
            {
                PrewarmOptimizer();
                if (_bo == null) { Fail(_lastBoLaunchError ?? "Could not launch the optimizer process."); return; }
            }

            _trialStart = DelphiClock.Now;
            _trialActuallyStarted = true;
            _phaseEnd = _trialStart + _activeConfig.baselineSeconds;
            Iteration = 0;
            LastCoverage = float.NaN;
            _baseline.Clear();
            _acc = null;
            _transTo = null;
            CurrentPhase = Phase.Baseline;
            StatusLine = "Baseline — participant sits still";
            Debug.Log($"[Trial] Started ({kind}): baseline {_activeConfig.baselineSeconds:0}s " +
                      $"(avg last {_activeConfig.baselineAveragingSeconds:0}s), then " +
                      $"{_activeConfig.iterations} × {windowSeconds:0}s windows. " +
                      $"Objective source: {_objectiveSource}.");

            // Baseline is stationary, in BOTH conditions — the car must stay
            // parked here. It should already be (startParked at session
            // start for the first condition, or the Parking segment between
            // conditions for the second) — this is a diagnostic, not a fix:
            // if it fires, something upstream left the car driving.
            if (carDriver != null && !carDriver.IsParked)
                Debug.LogWarning("[Trial] Baseline is starting but the car isn't parked. Baseline must be " +
                                  "stationary — check CarDriver.startParked (should be true) if this is " +
                                  "the first condition, since there's no Parking segment before it.");
        }

        private void TickBaseline()
        {
            // Connecting is handled generically in Update() now (so it starts
            // the moment the process boots, not just once baseline begins).

            double avgStart = _phaseEnd - _activeConfig.baselineAveragingSeconds;
            if (_acc == null && DelphiClock.Now >= avgStart)
            {
                _acc = new WindowAccumulator();
                manager.Core.Accumulator = _acc;
            }

            if (DelphiClock.Now < _phaseEnd) return;

            // Baseline over — snapshot the per-channel reference MEAN for
            // whatever's attached, REGARDLESS of objectiveSource: harmless,
            // and still useful post-hoc reference data even for channels that
            // won't feed the optimizer this trial (e.g. physiology during an
            // Explicit/Questionnaire condition). The bounds come from
            // baseline ± k·(literature SD), NOT from anything measured here.
            manager.Core.Accumulator = null;
            var physiologyObjectiveChannels = new List<Channel>();
            _baseline.Clear();
            foreach (var ch in CandidateChannels())
            {
                var (mean, count) = _acc.Mean(ch);
                if (count == 0)
                {
                    Debug.LogWarning($"[Trial] {ch} produced no baseline samples — excluded from objectives.");
                    continue;
                }
                _baseline[ch] = mean;
                physiologyObjectiveChannels.Add(ch);
                var cfg = EffectiveConfig(ch);
                var (lo, hi) = ChannelMath.Bounds(mean, cfg.sd, boundK);
                Debug.Log($"[Trial] Baseline {ch}: mean {mean:F2} ({count} samples) → bounds [{lo:F2}, {hi:F2}] " +
                          $"(SD {cfg.sd}, {(cfg.higherIsBetter ? "higher is better" : "higher is worse")})");
            }
            _acc = null;

            // What actually gets sent to the optimizer as objectives —
            // Questionnaire mode ignores the physiology scan above entirely
            // (there's no DelphiManager channel here at all; the objective
            // keys come straight from the questionnaire's own header names).
            if (_objectiveSource == ObjectiveSource.Questionnaire)
            {
                _objectiveChannels = new List<Channel>();
                _questionnaireKeys = questionnaire.resultsHeaderItems != null
                    ? new List<string>(questionnaire.resultsHeaderItems)
                    : new List<string>();

                if (_questionnaireKeys.Count < 2)
                {
                    Fail("Fewer than 2 questionnaire header items are configured — cannot run MOBO.");
                    return;
                }
            }
            else
            {
                _questionnaireKeys = new List<string>();
                _objectiveChannels = physiologyObjectiveChannels;
                if (_objectiveChannels.Count < 2)
                {
                    Fail("Fewer than 2 channels delivered baseline data — cannot run MOBO.");
                    return;
                }
            }

            _driveStart = DelphiClock.Now;
            CurrentPhase = Phase.WaitingForOptimizer;
            _phaseEnd = DelphiClock.Now + 60; // generous cap for torch import
            StatusLine = "Baseline done — waiting for optimizer";
        }

        private void TickWaitingForOptimizer()
        {
            if (!_bo.TryConnect())
            {
                if (DelphiClock.Now > _phaseEnd)
                    Fail("Optimizer never opened its socket — see [BO] console output.");
                return;
            }

            if (!SendInit()) return; // Fail() already set Phase.Error
            OpenTrialLog();
            CurrentPhase = Phase.WaitingForParameters;
            _phaseEnd = 0;
            StatusLine = "Waiting for first parameter set";
        }

        private void TickIterationLoop()
        {
            DrainMessages();

            if (CurrentPhase == Phase.Washout && DelphiClock.Now >= _phaseEnd)
            {
                if (_objectiveSource == ObjectiveSource.Questionnaire)
                {
                    // No windowed mean to gather — a rating is one discrete
                    // value, not a sampled signal. Park (no jerk on the seat
                    // either way) and wait for the participant to submit;
                    // RequestNextIteration() (the bridge callback) is what
                    // ends this phase, not a timer.
                    CurrentPhase = Phase.AwaitingRating;
                    StatusLine = $"Iteration {Iteration}/{TotalIterations} — parked, awaiting rating";
                    _measureStart = DelphiClock.Now;
                    _pendingQuestionnaireValues.Clear();
                    carDriver?.FreezeInPlace(); // instant halt, not a drive to a (possibly distant) Park marker
                    questionnaire.StartQuestionnaire();
                }
                else
                {
                    _acc = new WindowAccumulator();
                    manager.Core.Accumulator = _acc;
                    _measureStart = DelphiClock.Now;
                    _phaseEnd = _measureStart + MeasureSeconds;
                    CurrentPhase = Phase.Measuring;
                    StatusLine = $"Iteration {Iteration}/{TotalIterations} — measuring";
                }
            }
            else if (CurrentPhase == Phase.Measuring && DelphiClock.Now >= _phaseEnd)
            {
                manager.Core.Accumulator = null;
                SubmitObjectives();
                _acc = null;
                CurrentPhase = Phase.WaitingForParameters;
                _phaseEnd = 0;
                StatusLine = $"Iteration {Iteration}/{TotalIterations} — submitted, waiting";
            }
            // AwaitingRating has no timer check here — see RequestNextIteration.
        }

        private void DrainMessages()
        {
            // _bo goes null when optimization_finished tears the bridge down
            // mid-drain — re-check every pass, not just on entry.
            while (_bo != null && _bo.Incoming.TryDequeue(out var msg))
            {
                switch ((string)msg["type"])
                {
                    case "parameters":
                        // The ramp exists so a parameter change mid-drive isn't
                        // itself a startle stimulus. A PARKED car has nothing to
                        // startle: ramping there just means the first pull-away
                        // happens on a blend of 0.5 and the optimizer's values
                        // rather than on the set being evaluated. So the first
                        // set lands instantly, before the car is released.
                        ApplyParameters((JObject)msg["values"],
                                        instant: carDriver != null && carDriver.IsParked);
                        Iteration++;
                        // Baseline (and any wait before this point) was
                        // stationary — the drive begins exactly when the
                        // first real BO-chosen parameter set is about to be
                        // applied. No-ops on every later iteration (already
                        // driving by then) — safe/idempotent to call every time.
                        carDriver?.ResumeDriving();
                        CurrentPhase = Phase.Washout;
                        _phaseEnd = DelphiClock.Now + EffectiveWashoutSeconds;
                        StatusLine = $"Iteration {Iteration}/{TotalIterations} — washout";
                        break;

                    case "coverage":
                    case "tempCoverage":
                        LastCoverage = (float)msg["value"];
                        break;

                    case "optimization_finished":
                        Debug.Log($"[Trial] Optimization finished. Condition {ConditionNumber} done.");
                        Cleanup("Finished");
                        // mobo.py exits after one run — immediately launch the
                        // next condition's process (if any) so it's already
                        // warm by the time the researcher/flow reaches it.
                        if (ConditionNumber < ConditionCount) PrewarmOptimizer();
                        AdvanceToNextSegment();
                        return; // bridge is gone; stop draining
                }
            }
        }

        // Mirrors what CarDriver's OWN driving logic actually checks — off
        // means the axis provably has zero effect on the car. A disabled
        // axis is EXCLUDED from the search space entirely, not sent as a
        // dimension the optimizer wastes budget exploring for no signal.
        private bool IsParamOn(string key) => key switch
        {
            "accelerationJerk"    => carDriver.parameters.accelerationJerkOn,
            "brakingJerk"         => carDriver.parameters.brakingJerkOn,
            "followDistance"      => carDriver.parameters.followDistanceOn,
            "corneringSpeed"      => carDriver.parameters.corneringSpeedOn,
            "takeoverProbability" => carDriver.parameters.takeoverProbabilityOn,
            "speedBelowLimit"     => carDriver.parameters.speedBelowLimitOn,
            _                     => true
        };

        // ── Optimizer messages ──────────────────────────────────────────
        private bool SendInit()
        {
            _activeParamKeys = ParameterKeys.Where(IsParamOn).ToList();
            if (_activeParamKeys.Count == 0)
            {
                Fail("All six driving parameters are disabled on CarDriver — nothing for the optimizer to search over.");
                return false;
            }

            var parameters = new JArray();
            foreach (var key in _activeParamKeys)
                parameters.Add(new JObject { ["key"] = key, ["init"] = "0,1" });

            bool isQuestionnaire = _objectiveSource == ObjectiveSource.Questionnaire;
            List<string> objectiveKeys;
            var objectives = new JArray();

            if (isQuestionnaire)
            {
                // RAW rating, native scale — mobo.py does its own
                // normalization AND uses [lo,hi] to denormalize back to
                // native units for its own CSV research log. Sending an
                // already-pre-normalized [0,1] value here (as an earlier
                // version of this did) made that log show a meaningless
                // abstract number instead of the actual 1-7 rating.
                // minimize=0: higher raw rating = better outcome.
                objectiveKeys = _questionnaireKeys;
                foreach (var key in objectiveKeys)
                {
                    string objInit = string.Format(CultureInfo.InvariantCulture, "{0},{1},{2}", questionnaireMin, questionnaireMax, 0);
                    objectives.Add(new JObject { ["key"] = key, ["init"] = objInit });
                }
            }
            else
            {
                // Physiology: we've ALREADY shaped/oriented the deviation in
                // C# (activation function, higherIsBetter flip) into a value
                // where lower=better, so mobo.py's minimize=1 here is a
                // second, deliberate flip that composes correctly with our
                // own — see SubmitObjectives. This one intentionally does NOT
                // pass through raw native units (Tanh/ReLU shaping has no
                // equivalent in mobo.py's plain linear normalize).
                var (lo, hi) = ChannelMath.ObjectiveRange(activation);
                objectiveKeys = _objectiveChannels.Select(ch => ch.ToString()).ToList();
                foreach (var key in objectiveKeys)
                {
                    string objInit = string.Format(CultureInfo.InvariantCulture, "{0},{1},{2}", lo, hi, 1);
                    objectives.Add(new JObject { ["key"] = key, ["init"] = objInit });
                }
            }

            int sampling = Mathf.Clamp(_activeConfig.samplingIterations, 1, _activeConfig.iterations - 1);
            var init = new JObject
            {
                ["type"] = "init",
                ["config"] = new JObject
                {
                    ["numSamplingIterations"] = sampling,
                    ["numOptimizationIterations"] = _activeConfig.iterations - sampling,
                    ["batchSize"] = 1, ["numRestarts"] = 10,
                    ["rawSamples"] = 512, ["mcSamples"] = 256,
                    ["seed"] = seed,
                    ["nParameters"] = _activeParamKeys.Count,
                    ["nObjectives"] = objectiveKeys.Count,
                    ["warmStart"] = false
                },
                ["user"] = new JObject
                {
                    ["userId"] = userId, ["conditionId"] = conditionId, ["groupId"] = groupId
                },
                ["parameters"] = parameters,
                ["objectives"] = objectives
            };
            _bo.Send(init);

            var excluded = ParameterKeys.Except(_activeParamKeys).ToList();
            Debug.Log($"[Trial] Init sent: {_activeParamKeys.Count} parameters " +
                      $"({string.Join(", ", _activeParamKeys)})" +
                      (excluded.Count > 0 ? $" — excluded (disabled on CarDriver): {string.Join(", ", excluded)}" : "") +
                      $", {objectiveKeys.Count} objectives ({string.Join(", ", objectiveKeys)}), " +
                      (isQuestionnaire ? $"questionnaire source, range [{questionnaireMin},{questionnaireMax}] maximize."
                                       : $"activation {activation} minimize."));
            return true;
        }

        private void ApplyParameters(JObject values, bool instant = false)
        {
            var p = carDriver.parameters;

            if (instant)
            {
                // Land the values now and cancel any ramp — the car is parked,
                // so it pulls away already ON the set being evaluated.
                foreach (var key in ParameterKeys)
                    SetParam(p, key, Get(values, key, GetParam(p, key)));
                _transFrom = null;
                _transTo = null;
                _transDuration = 0f;
            }
            else
            {
                _transFrom = new Dictionary<string, float>();
                _transTo = new Dictionary<string, float>();
                foreach (var key in ParameterKeys)
                {
                    float current = GetParam(p, key);
                    _transFrom[key] = current;
                    _transTo[key] = Get(values, key, current);
                }
                _transStart = DelphiClock.Now;

                _transDuration = Mathf.Clamp(transitionSeconds, 0f, EffectiveWashoutSeconds);
                if (transitionSeconds > EffectiveWashoutSeconds)
                    Debug.LogWarning($"[Trial] transitionSeconds ({transitionSeconds:0.#}s) exceeds the washout " +
                                     $"({EffectiveWashoutSeconds:0.#}s) — clamped to {_transDuration:0.#}s so measurement never " +
                                     "starts mid-ramp.");
            }

            _lastParams = new Dictionary<string, float>();
            foreach (var prop in values.Properties()) _lastParams[prop.Name] = (float)prop.Value;

            Debug.Log($"[Trial] Applied parameter set #{Iteration + 1} " +
                      (instant ? "(instantly — car parked)" : $"(ramping over {_transDuration:0.#}s)") + ": " +
                      $"{values.ToString(Newtonsoft.Json.Formatting.None)}");
        }

        private void TickTransition()
        {
            if (_transTo == null || carDriver == null) return;
            float t = _transDuration > 0f
                ? Mathf.Clamp01((float)((DelphiClock.Now - _transStart) / _transDuration))
                : 1f;
            var p = carDriver.parameters;
            foreach (var key in ParameterKeys)
                SetParam(p, key, Mathf.Lerp(_transFrom[key], _transTo[key], t));
            if (t >= 1f) _transTo = null;
        }

        private static float GetParam(DrivingParameters p, string key) => key switch
        {
            "accelerationJerk"    => p.accelerationJerk,
            "brakingJerk"         => p.brakingJerk,
            "followDistance"      => p.followDistance,
            "corneringSpeed"      => p.corneringSpeed,
            "takeoverProbability" => p.takeoverProbability,
            "speedBelowLimit"     => p.speedBelowLimit,
            _                     => 0f
        };

        private static void SetParam(DrivingParameters p, string key, float v)
        {
            switch (key)
            {
                case "accelerationJerk":    p.accelerationJerk    = v; break;
                case "brakingJerk":         p.brakingJerk         = v; break;
                case "followDistance":      p.followDistance      = v; break;
                case "corneringSpeed":      p.corneringSpeed      = v; break;
                case "takeoverProbability": p.takeoverProbability = v; break;
                case "speedBelowLimit":     p.speedBelowLimit     = v; break;
            }
        }

        private static string FormatDict(Dictionary<string, float> d) =>
            string.Join(", ", d.Select(kv => $"{kv.Key}={kv.Value:F3}"));

        private static float Get(JObject values, string key, float fallback)
        {
            var tok = values[key];
            return tok == null ? fallback : Mathf.Clamp01((float)tok);
        }

        private void SubmitObjectives()
        {
            var objectiveValues = new Dictionary<string, float>();
            var deviationsForLog = new Dictionary<string, float>();
            var logCells = new List<string>();
            foreach (var ch in _objectiveChannels)
            {
                var cfg = EffectiveConfig(ch);
                var (mean, count) = _acc.Mean(ch);
                float baseline = _baseline[ch];
                float d;
                bool clipped;

                if (count == 0)
                {
                    Debug.LogWarning($"[Trial] {ch} delivered no samples in window {Iteration} — submitting neutral objective.");
                    mean = baseline; d = 0f; clipped = false;
                }
                else
                {
                    d = ChannelMath.Deviation(mean, baseline, cfg.sd, boundK);
                    clipped = ChannelMath.IsClipped(d);
                }
                float objective = ChannelMath.Objective(d, cfg.higherIsBetter, activation);

                objectiveValues[ch.ToString()] = objective;
                deviationsForLog[ch.ToString()] = d;
                logCells.Add(F(baseline)); logCells.Add(F(mean)); logCells.Add(F(d));
                logCells.Add(F(objective)); logCells.Add(clipped ? "1" : "0");
            }

            Debug.Log($"[Trial] Iteration {Iteration}/{TotalIterations} result — " +
                      $"params: {{{FormatDict(_lastParams)}}} | " +
                      $"deviations d (vs. baseline, in bound units): {{{FormatDict(deviationsForLog)}}} | " +
                      $"objectives sent (minimize): {{{FormatDict(objectiveValues)}}}");

            _bo.SendObjectives(objectiveValues);
            WriteTrialLogRow(logCells);
        }

        /// <summary>Questionnaire-mode counterpart to SubmitObjectives() —
        /// sends the participant's RAW rating straight through, clamped to
        /// [questionnaireMin, questionnaireMax]. No C#-side normalization:
        /// mobo.py already does its own, and doing it twice is what broke the
        /// CSV log earlier (see SendInit's comment).</summary>
        private void SubmitQuestionnaireObjectives()
        {
            var objectiveValues = new Dictionary<string, float>();
            var logCells = new List<string>();
            foreach (var key in _questionnaireKeys)
            {
                bool has = _pendingQuestionnaireValues.TryGetValue(key, out float raw);
                if (!has)
                {
                    Debug.LogWarning($"[Trial] No questionnaire value received for '{key}' in iteration {Iteration} — submitting the scale midpoint.");
                    raw = (questionnaireMin + questionnaireMax) * 0.5f;
                }
                raw = Mathf.Clamp(raw, questionnaireMin, questionnaireMax);
                objectiveValues[key] = raw;
                logCells.Add(F(raw));
            }

            Debug.Log($"[Trial] Iteration {Iteration}/{TotalIterations} questionnaire result — " +
                      $"params: {{{FormatDict(_lastParams)}}} | " +
                      $"raw ratings sent (maximize): {{{FormatDict(objectiveValues)}}}");

            _bo.SendObjectives(objectiveValues);
            WriteTrialLogRow(logCells);
        }

        // ── IQuestionnaireOptimizationBridge (per-iteration rating) ─────
        // This class IS the bridge — it already owns _objectiveChannels/
        // _questionnaireKeys and the whole state machine, so a separate
        // adapter would just be a second state machine to keep in sync.
        bool IQuestionnaireOptimizationBridge.UsesExternalIterationSignal => true;
        bool IQuestionnaireOptimizationBridge.EnablePriorRatingHints => false;
        float IQuestionnaireOptimizationBridge.PriorRatingHintAlpha => 0f;
        string IQuestionnaireOptimizationBridge.UserId => userId;
        string IQuestionnaireOptimizationBridge.ConditionId => conditionId;
        string IQuestionnaireOptimizationBridge.GroupId => groupId;

        // StartConditionTrial() already ran mobo.py's real init before the
        // car ever started driving toward this rating — nothing left to
        // start here.
        void IQuestionnaireOptimizationBridge.OptimizationStart() { }

        void IQuestionnaireOptimizationBridge.RequestNextIteration()
        {
            if (CurrentPhase != Phase.AwaitingRating) return;
            SubmitQuestionnaireObjectives();
            carDriver?.ResumeDriving();
            CurrentPhase = Phase.WaitingForParameters;
            _phaseEnd = 0;
            StatusLine = $"Iteration {Iteration}/{TotalIterations} — submitted, waiting";
        }

        void IQuestionnaireOptimizationBridge.SubmitQuestionnaireObjectiveValue(string headerName, string rawValue, string sourceName)
        {
            if (string.IsNullOrEmpty(headerName)) return;

            if (!float.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
            {
                Debug.LogWarning($"[Trial] Could not parse questionnaire value '{rawValue}' for '{headerName}' (from '{sourceName}').");
                return;
            }

            _pendingQuestionnaireValues[headerName] = v;
        }

        // Optional prior-rating-hint UX (pre-filling a slider with the last
        // rating) — not wired yet, safe no-ops.
        void IQuestionnaireOptimizationBridge.SetPriorSliderRatingHint(string questionKey, float sliderValue) { }
        bool IQuestionnaireOptimizationBridge.TryGetPriorSliderRatingHint(string questionKey, out float sliderValue)
        {
            sliderValue = 0f;
            return false;
        }
        void IQuestionnaireOptimizationBridge.RemovePriorSliderRatingHint(string questionKey) { }
        void IQuestionnaireOptimizationBridge.SetPriorLinearScaleRatingHint(string questionKey, string answerValue) { }
        bool IQuestionnaireOptimizationBridge.TryGetPriorLinearScaleRatingHint(string questionKey, out string answerValue)
        {
            answerValue = null;
            return false;
        }
        void IQuestionnaireOptimizationBridge.RemovePriorLinearScaleRatingHint(string questionKey) { }

        // ── Per-channel normalization config ────────────────────────────
        public ChannelNormalization ConfigFor(Channel ch)
        {
            foreach (var c in channelConfigs)
                if (c != null && c.channel == ch) return c;
            return null;
        }

        private ChannelNormalization EffectiveConfig(Channel ch)
        {
            var c = ConfigFor(ch);
            if (c != null) return c;
            return new ChannelNormalization
            {
                channel = ch,
                sd = ChannelMath.DefaultSd(ch),
                higherIsBetter = ChannelMath.DefaultHigherIsBetter(ch)
            };
        }

        public bool TryGetBounds(Channel ch, out float lower, out float upper)
        {
            lower = upper = float.NaN;
            if (!_baseline.TryGetValue(ch, out float b)) return false;
            var cfg = EffectiveConfig(ch);
            (lower, upper) = ChannelMath.Bounds(b, cfg.sd, boundK);
            return true;
        }

        public ActivationFunction ActivationFn => activation;
        public IReadOnlyList<Channel> ObjectiveChannels => _objectiveChannels;

        // ── Trial log ───────────────────────────────────────────────────
        private void OpenTrialLog()
        {
            string dir = recorder != null && recorder.IsRecording
                ? recorder.CurrentSessionPath
                : Path.Combine(Application.persistentDataPath, "Trials");
            Directory.CreateDirectory(dir);
            _trialLog = new StreamWriter(Path.Combine(dir, "trial_log.csv"));

            var header = new StringBuilder("iteration,t_measure_start_s,t_measure_end_s");
            foreach (var key in ParameterKeys) header.Append(',').Append(key);
            if (_objectiveSource == ObjectiveSource.Questionnaire)
                // raw = the participant's submitted rating, native scale
                // (questionnaireMin..questionnaireMax) — sent to mobo.py as-is.
                foreach (var key in _questionnaireKeys)
                    header.Append($",{key}_raw");
            else
                foreach (var ch in _objectiveChannels)
                    // baseline = reference mean; mean = this window's mean;
                    // d = (mean−baseline)/(k·SD) signed deviation in bound units;
                    // objective = activation(oriented d) = the minimized value;
                    // clipped = 1 when |d|>1 (window left the baseline ± k·SD bound).
                    header.Append($",{ch}_baseline,{ch}_mean,{ch}_d,{ch}_objective,{ch}_clipped");
            header.Append(",coverage");
            _trialLog.WriteLine(header.ToString());
            _trialLog.Flush();
        }

        private void WriteTrialLogRow(List<string> objectiveCells)
        {
            if (_trialLog == null) return;
            var p = carDriver.parameters;
            var row = new StringBuilder();
            row.Append(Iteration).Append(',');
            row.Append(F(_measureStart - _trialStart)).Append(',');
            row.Append(F(DelphiClock.Now - _trialStart));
            foreach (float v in new[] { p.accelerationJerk, p.brakingJerk, p.followDistance,
                                        p.corneringSpeed, p.takeoverProbability, p.speedBelowLimit })
                row.Append(',').Append(F(v));
            foreach (var c in objectiveCells) row.Append(',').Append(c);
            row.Append(',').Append(float.IsNaN(LastCoverage) ? "NaN" : F(LastCoverage));
            _trialLog.WriteLine(row.ToString());
            _trialLog.Flush();
        }

        private static string F(double v) => v.ToString("F4", CultureInfo.InvariantCulture);

        // ── Trial meta ──────────────────────────────────────────────────
        private void WriteTrialMeta(string endReason, string sessionPath)
        {
            try
            {
                int sampling = Mathf.Clamp(_activeConfig.samplingIterations, 1, Math.Max(1, _activeConfig.iterations - 1));
                double driveElapsed = _driveStart > 0 ? DelphiClock.Now - _driveStart : 0;
                float avgIterationSeconds = Iteration > 0 ? (float)(driveElapsed / Iteration) : 0f;

                var objectives = new List<TrialObjectiveMeta>();
                foreach (var ch in _objectiveChannels)
                {
                    var sensor = manager.GetSensor(ch);
                    var cfg = EffectiveConfig(ch);
                    float baseline = _baseline.TryGetValue(ch, out var b) ? b : float.NaN;
                    var (lo, hi) = ChannelMath.Bounds(baseline, cfg.sd, boundK);
                    objectives.Add(new TrialObjectiveMeta
                    {
                        channel = ch.ToString(),
                        sensorType = sensor != null ? sensor.GetType().Name : "(none)",
                        sensorObjectName = sensor != null ? sensor.gameObject.name : "(none)",
                        baselineMean = baseline,
                        literatureSd = cfg.sd,
                        boundK = boundK,
                        lowerBound = lo,
                        upperBound = hi,
                        higherIsBetter = cfg.higherIsBetter
                    });
                }

                var (objLo, objHi) = ChannelMath.ObjectiveRange(activation);
                var meta = new TrialMeta
                {
                    userId = userId, conditionId = conditionId, groupId = groupId,
                    startedIso = DateTime.Now.AddSeconds(-(DelphiClock.Now - _trialStart)).ToString("o"),
                    endReason = endReason,
                    totalDurationSeconds = (float)(DelphiClock.Now - _trialStart),

                    baselineSeconds = _activeConfig.baselineSeconds,
                    baselineAveragingSeconds = _activeConfig.baselineAveragingSeconds,
                    windowSeconds = windowSeconds,
                    washoutSeconds = EffectiveWashoutSeconds,
                    measureSeconds = MeasureSeconds,
                    transitionSeconds = transitionSeconds,

                    activation = activation.ToString(),
                    objectiveRangeLo = objLo,
                    objectiveRangeHi = objHi,

                    iterationsPlanned = _activeConfig.iterations,
                    iterationsCompleted = Iteration,
                    samplingIterationsPlanned = sampling,
                    optimizationIterationsPlanned = Math.Max(0, _activeConfig.iterations - sampling),
                    averageIterationSeconds = avgIterationSeconds,

                    finalHypervolumeCoverage = LastCoverage,

                    optimizerSeed = seed,
                    pythonPathUsed = string.IsNullOrWhiteSpace(pythonPath)
                        ? Delphi.Trial.BoBridge.DefaultPythonPath
                        : pythonPath,

                    sessionRecordingPath = sessionPath ?? "",

                    goldStandardRateHz = manager != null ? manager.goldStandardRateHz : 0f,
                    goodAdditionsRateHz = manager != null ? manager.goodAdditionsRateHz : 0f,
                    experimentalRateHz = manager != null ? manager.experimentalRateHz : 0f,

                    objectives = objectives.ToArray(),
                    objectiveSource = _objectiveSource.ToString(),
                    questionnaireObjectiveKeys = _objectiveSource == ObjectiveSource.Questionnaire
                        ? _questionnaireKeys.ToArray()
                        : Array.Empty<string>(),
                    parameterRanges = BuildParameterRanges()
                };

                string dir = !string.IsNullOrEmpty(sessionPath)
                    ? sessionPath
                    : Path.Combine(Application.persistentDataPath, "Trials");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "trial_meta.json"),
                                  JsonUtility.ToJson(meta, prettyPrint: true));
                Debug.Log($"[Trial] Wrote trial_meta.json ({Iteration} iterations, " +
                          $"avg {avgIterationSeconds:F1}s/iteration) → {dir}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[Trial] Failed to write trial_meta.json: {e.Message}");
            }
        }

        private TrialParameterRangeMeta[] BuildParameterRanges()
        {
            if (carDriver == null) return Array.Empty<TrialParameterRangeMeta>();
            var p = carDriver.parameters;
            var ranges = new[]
            {
                new TrialParameterRangeMeta { key = "accelerationJerk", unit = "m/s^3",
                    physicalMin = p.accelJerkMin, physicalMax = p.accelJerkMax },
                new TrialParameterRangeMeta { key = "brakingJerk", unit = "m/s^3",
                    physicalMin = p.brakeJerkMin, physicalMax = p.brakeJerkMax },
                new TrialParameterRangeMeta { key = "followDistance", unit = "s headway (inverted: 0=far/gentle, 1=close/assertive)",
                    physicalMin = p.followMax, physicalMax = p.followMin },
                new TrialParameterRangeMeta { key = "corneringSpeed", unit = "km/h cut in the tightest realistic turn (inverted: 0=cuts a lot/gentle, 1=barely cuts/assertive)",
                    physicalMin = p.cornerSlowdownMaxKmh, physicalMax = p.cornerSlowdownMinKmh },
                new TrialParameterRangeMeta { key = "takeoverProbability", unit = "probability",
                    physicalMin = 0f, physicalMax = 1f },
                new TrialParameterRangeMeta { key = "speedBelowLimit", unit = "km/h below posted limit (inverted: 0=big margin/gentle, 1=at limit/assertive)",
                    physicalMin = p.belowLimitMaxKmh, physicalMax = p.belowLimitMinKmh },
            };
            foreach (var r in ranges) r.active = _activeParamKeys.Contains(r.key);
            return ranges;
        }

        // ── Helpers / teardown ──────────────────────────────────────────
        /// <summary>Channels eligible as Physiology objectives: attached AND
        /// enabled on DelphiManager.</summary>
        public List<Channel> CandidateChannels()
        {
            var list = new List<Channel>();
            if (manager == null) return list;
            foreach (var ch in DelphiManager.AllChannels)
            {
                var status = manager.GetStatus(ch);
                if (status == ChannelStatus.NotAttached || status == ChannelStatus.Disabled) continue;
                list.Add(ch);
            }
            return list;
        }

        /// <summary>Launches mobo.py proactively, ahead of actually needing
        /// it — called at Awake() (the moment Play starts) and again right
        /// after every condition finishes/aborts/fails, so there's always a
        /// process already booting or connected by the time a condition
        /// needs one. No-ops if one is already alive. mobo.py itself is
        /// one-run-per-process (see the class doc comment), so this is what
        /// "always available" actually means in practice — never launched
        /// lazily at trial-start, never left cold between conditions.</summary>
        private void PrewarmOptimizer()
        {
            if (_bo != null) return;
            try
            {
                _bo = new BoBridge();
                _bo.StartProcess(pythonPath,
                    Path.Combine(Application.streamingAssetsPath, "BOData", "BayesianOptimization"),
                    "mobo.py");
                _lastBoLaunchError = null;
                Debug.Log("[Trial] Optimizer process launched.");
            }
            catch (Exception e)
            {
                _bo?.Dispose();
                _bo = null;
                _lastBoLaunchError = e.Message;
                Debug.LogWarning($"[Trial] Could not launch the optimizer yet: {e.Message} — will retry when a condition starts.");
            }
        }

        private void Cleanup(string endReason = "Interrupted")
        {
            if (manager != null && manager.Core != null) manager.Core.Accumulator = null;
            _acc = null;
            _transTo = null;
            _bo?.Dispose();
            _bo = null;
            _trialLog?.Close();
            _trialLog = null;
            _freePlayLog?.Close(); // defensive — normally EndFreePlay() already closed this
            _freePlayLog = null;

            string sessionPath = recorder != null ? recorder.CurrentSessionPath : null;
            if (recorder != null && recorder.IsRecording) recorder.StopRecording();

            if (_trialActuallyStarted) WriteTrialMeta(endReason, sessionPath);
            _trialActuallyStarted = false;
        }

        private void OnDestroy() => Cleanup();
        private void OnApplicationQuit() => Cleanup();

        // ── Read helpers for the researcher UI ──────────────────────────
        public ConditionKind ConditionKindAt(int slot)
        {
            var order = OrderFor(orderIndex);
            return order[Mathf.Clamp(slot, 0, order.Length - 1)];
        }

        /// <summary>Rough wall-clock length of ONE condition of this kind:
        /// baseline + all iteration windows. Returns 0 for FreeRoam, which is
        /// open-ended by design — callers should show "open-ended" rather than
        /// print a fake estimate.</summary>
        public float EstimatedConditionSeconds(ConditionKind kind)
        {
            if (kind == ConditionKind.FreeRoam) return 0f;
            var cfg = kind == ConditionKind.Implicit ? implicitTrial : explicitTrial;
            return cfg.baselineSeconds + cfg.iterations * windowSeconds;
        }

        public float CurrentConditionSecondsRemaining()
        {
            if (_activeConfig == null || !IsRunningCondition) return 0f;
            int itersLeft = Mathf.Max(0, TotalIterations - Iteration);
            return (float)PhaseSecondsRemaining + itersLeft * windowSeconds;
        }

        public float CurrentConditionProgress()
        {
            if (_activeConfig == null || !IsRunningCondition || TotalIterations <= 0) return 0f;
            return Mathf.Clamp01(Iteration / (float)TotalIterations);
        }

        public int ActiveConditionSlot => Mathf.Clamp(ConditionNumber - 1, -1, ConditionCount - 1);

        // ── Debug (context menu) ────────────────────────────────────────
        [ContextMenu("Start Session")] private void CtxStart() => StartSession();
        [ContextMenu("Emergency Stop")] private void CtxStop() => EmergencyStop();

        public string Describe()
        {
            var sb = new StringBuilder();
            sb.Append(CurrentPhase);
            if (ConditionNumber > 0) sb.Append($"  [cond {ConditionNumber}/{ConditionCount}]");
            if (PhaseSecondsRemaining > 0) sb.Append($"  {PhaseSecondsRemaining:0}s left");
            return sb.ToString();
        }
    }
}
