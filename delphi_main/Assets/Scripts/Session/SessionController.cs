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

namespace Delphi.Session
{
    /// <summary>
    /// Orchestrates the WHOLE participant session — session-level pacing
    /// (Intro/Meditation/Questionnaire/BreakOffer/FreePlay) AND each
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
    ///   Intro → Meditation → CondIntro[0] → Condition[0] → Questionnaire → BreakOffer
    ///         → Meditation → CondIntro[1] → Condition[1] → Questionnaire → BreakOffer
    ///         → Meditation → CondIntro[2] → Condition[2] → Questionnaire → Complete
    ///
    /// where a Condition is the iteration loop for Implicit and Explicit, and
    /// (open-ended roaming, ended by the researcher's DONE button) for
    /// FreeRoam. The break is offered BETWEEN conditions only. Every segment
    /// here maps one-to-one onto a recorded narration file — BuildPlan is
    /// written against that recording list, so changing the flow means
    /// changing both together.
    ///
    /// There is no separate Parking segment: the moment the drive ends, the
    /// evaluation narration plays WHILE the car pulls over for real (see
    /// CarDriver.RequestPullover — a computed, on-demand stopping point on
    /// the existing driving line, not a hand-authored marker, so the route
    /// never needs pre-timing to land the car anywhere specific). The
    /// questionnaire itself only appears once the car has actually come to a
    /// halt and the narration has finished — see EnterQuestionnaire /
    /// TickQuestionnaire. Only the OUTGOING reset back to the start
    /// (CarDriver.ResetToStart) is an instant teleport, done once the
    /// questionnaire is submitted while its panel still fills the whole
    /// screen — invisible for the same reason it always was, just moved to
    /// the other side of the questionnaire. The segment advances the instant
    /// the questionnaire is submitted.
    ///
    /// THE MEDITATION IS THE BASELINE. There is no separate stationary
    /// baseline phase: the physiological reference means are accumulated
    /// during a window near the end of the meditation track, while the music
    /// is still playing and the participant is already settled. See
    /// EnterMeditation/CaptureBaseline. That's why every condition — not just
    /// the first — has a meditation in front of it.
    ///
    /// The closing interview happens IN PERSON, after the headset/screen
    /// experience ends — there is deliberately no in-app Interview phase.
    ///
    /// orderIndex/userId/conditionId are runtime state set by ExperimentUI
    /// (the researcher's actual control surface) right before StartSession() —
    /// not Inspector-configured, since they change every participant/session,
    /// not every build. groupId is derived from orderIndex, not typed.
    /// </summary>
    public class SessionController : MonoBehaviour
    {
        /// <summary>No Baseline member: the baseline is measured inside
        /// Meditation now, not in a phase of its own. No Parking member
        /// either — the car is instantly reset to start the moment
        /// Questionnaire begins (see EnterQuestionnaire), not driven there
        /// as a phase of its own.</summary>
        public enum Phase
        {
            Idle, Intro, Meditation, ConditionIntro,
            WaitingForOptimizer, WaitingForParameters, Washout, Trial, Measuring, AwaitingRating,
            Questionnaire, BreakOffer, FreePlay,
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
        /// how many iterations to run and how many of those are exploratory.
        /// Baseline timing is NOT here — it belongs to the meditation track,
        /// which is shared by all three conditions, so it's a session-level
        /// setting (see the Baseline header). Shared BO mechanics (activation,
        /// boundK, window/washout timing, questionnaire range, pythonPath)
        /// live in the BO Hub section below instead.</summary>
        [Serializable]
        public class ConditionTrialConfig
        {
            [Tooltip("Total number of parameter sets the optimizer gets to try.")]
            [Min(2)] public int iterations = 56;
            [Tooltip("How many of those iterations are quasi-random (Sobol) " +
                     "exploration before model-guided optimization starts.")]
            [Min(1)] public int samplingIterations = 12;
        }

        // ── Segment plan (session-level pacing) ─────────────────────────
        /// <summary>ConditionIntro and Condition are DELIBERATELY separate
        /// segments rather than one: the narration flow puts a Meditation
        /// between every condition's intro line and its drive (see BuildPlan),
        /// which is only expressible if the intro can be followed by an
        /// arbitrary segment instead of falling straight into the trial.
        ///
        /// No Parking here either — see EnterQuestionnaire for why parking
        /// happens AS PART OF Questionnaire rather than before it.</summary>
        private enum SegmentKind
        {
            Intro, Meditation, ConditionIntro, Condition,
            Questionnaire, BreakOffer, Complete
        }

        private struct Segment
        {
            public SegmentKind kind;
            public float seconds;         // timed segments only
            public ConditionKind condition; // Meditation/ConditionIntro/Condition segments only
            public int slot;              // 1..3 — which of the three drives this is
        }

        /// <summary>One STOP on the researcher's timeline — the five things a
        /// session is actually made of from the outside: the intro, the three
        /// conditions, the end. Deliberately coarser than the segment plan: a
        /// condition's meditation, framing line, drive and evaluation are one
        /// stop between them, because "jump to condition 2" means the whole
        /// run-up to that drive, never the drive with its baseline skipped.
        ///
        /// A stop OWNS a contiguous run of plan segments — [firstSegment,
        /// lastSegment] — which is what makes both jumping (enter
        /// firstSegment) and progress (how far through the run we are) fall
        /// out of the same structure rather than needing two mappings that can
        /// disagree.</summary>
        public readonly struct TimelineStop
        {
            public readonly string label;        // "INTRO", "CONDITION 2", "END"
            public readonly string detail;       // "Implicit", "welcome & briefing", …
            public readonly int firstSegment;    // plan index this stop begins at
            public readonly int lastSegment;     // inclusive; the segment before the next stop
            /// <summary>Rough wall-clock length of the whole stop, used to
            /// weight the progress bar. 0 for END.</summary>
            public readonly float estimatedSeconds;
            public readonly bool isCondition;
            public readonly ConditionKind condition; // conditions only
            public readonly int slot;                // 1..3 for conditions, 0 otherwise

            public TimelineStop(string label, string detail, int firstSegment, int lastSegment,
                                float estimatedSeconds, bool isCondition,
                                ConditionKind condition, int slot)
            {
                this.label = label;
                this.detail = detail;
                this.firstSegment = firstSegment;
                this.lastSegment = lastSegment;
                this.estimatedSeconds = estimatedSeconds;
                this.isCondition = isCondition;
                this.condition = condition;
                this.slot = slot;
            }
        }

        [Header("Links (auto-found if left empty)")]
        public DelphiManager manager;
        public CarDriver carDriver;
        [Tooltip("Recording has to start/stop in sync with each condition's " +
                 "trial (so sensors.csv/videos/trial_log all land in the same " +
                 "session folder) — that's the only reason this class needs it.")]
        public SessionRecorder recorder;
        [Tooltip("Optional — the YAW VR3 rig's motion cue driver. On an " +
                 "EmergencyStop it's forced back to neutral over " +
                 "emergencyReturnToNeutralSeconds regardless of what motion " +
                 "was mid-flight; ordinary pauses (red light, questionnaire) " +
                 "need no special handling since Speed already holds at 0.")]
        public Delphi.Motion.CarMotionCues motionCues;
        [Tooltip("How long the rig takes to slerp back to level, in seconds — " +
                 "used whenever the car goes idle/parked (questionnaire reset) " +
                 "or an EmergencyStop hits. Moderate pace: noticeable but not " +
                 "startling. NOT used for ordinary in-drive stops (red lights) " +
                 "or the per-iteration rating freeze — those keep real forces.")]
        public float returnToNeutralSeconds = 3f;
        [Tooltip("Plays the spoken instructions at each phase transition.")]
        public NarrationController narration;
        

        [Tooltip("DELPHI's own per-trial questionnaire — the Explicit " +
                 "condition's objectives. Replaces the QT manager above.")]
        public DelphiQuestionnaire delphiQuestionnaire;
        [Tooltip("Its VR panel. Shown at AwaitingRating, hidden on submit.")]
        public VR.VrQuestionnairePanel delphiQuestionnairePanel;
        [Tooltip("Post-condition evaluation. RECORDED ONLY — never sent to " +
                 "the optimizer, because it rates a whole condition and the " +
                 "optimizer scores single iterations.")]
        public DelphiQuestionnaire conditionEvaluation;
        public VR.VrQuestionnairePanel conditionEvaluationPanel;
        

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

        [Header("Meditation — three authored sections of ONE audio file: " +
                "acclimatisation (just listening) → measurement (the " +
                "baseline window) → fadeout (volume ramps to 0). The " +
                "recording should be AT LEAST as long as the three added " +
                "together — deliberately longer-than-needed is fine, " +
                "anything past that point in the file is simply never " +
                "reached, since the phase ends there regardless.")]
        [Tooltip("Seconds at the start of the track where the participant " +
                 "just listens — nothing is measured yet.")]
        [Min(0f)] public float meditationAcclimatisationSeconds = 30f;
        [Tooltip("Seconds immediately after acclimatisation during which " +
                 "every physiological sample is averaged into this " +
                 "condition's reference mean — this section IS the baseline " +
                 "window.")]
        [Min(1f)] public float meditationMeasurementSeconds = 60f;
        [Tooltip("Seconds after the measurement window during which the " +
                 "track's volume ramps linearly from full down to silent, " +
                 "ending exactly as the meditation phase ends.")]
        [Min(0f)] public float meditationFadeoutSeconds = 10f;

        [Header("Explore (FreeRoam) nudge")]
        [Tooltip("extra_exploreNudge plays automatically after this many " +
                 "seconds without the participant moving a slider during the " +
                 "Explore condition. 0 = never automatic — the researcher " +
                 "plays it by hand with the NUDGE button instead, which is " +
                 "the default so nothing unplanned reaches a participant's " +
                 "ears. The timer only ever runs during Phase.FreePlay.")]
        [Min(0f)] public float exploreNudgeIdleSeconds = 0f;
        [Tooltip("Once the nudge has played this many times in one Explore " +
                 "condition, the automatic timer stops re-firing. The " +
                 "researcher's manual button is never limited.")]
        [Min(1)] public int exploreNudgeMaxAuto = 2;

        [Header("Timeline (researcher UI only)")]
        [Tooltip("Nominal length of the Explore/FreeRoam drive, in seconds. " +
                 "FreeRoam is open-ended by design — it ends when the " +
                 "participant says so — so nothing can know its real length. " +
                 "This number is used ONLY to weight the timeline's progress " +
                 "bar, so an Explore slot doesn't count as zero and make the " +
                 "session look further along than it is. It never affects the " +
                 "condition itself, the data, or when anything ends.")]
        [Min(30f)] public float freeRoamEstimatedSeconds = 300f;

        [Header("Trial structure — per condition kind")]
        public ConditionTrialConfig implicitTrial = new();
        public ConditionTrialConfig explicitTrial = new();

        [Header("Physiological objective shaping")]
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
        // Bounds for every questionnaire objective sent to mobo.py — sourced
        // from the questionnaire itself rather than authored separately here,
        // so they can't drift out of sync with what it actually produces.
        // Sent to mobo.py as-is (raw rating, not pre-normalized) so its own
        // CSV log shows the real rating instead of an abstract number.
        // Higher is treated as the better outcome.
        /// <summary>Always 1 — DelphiQuestion.response is always 1..steps,
        /// universally, so there's nothing to author here.</summary>
        public float questionnaireMin => 1f;
        /// <summary>The largest `steps` across every question on
        /// delphiQuestionnaire, so no question's legitimate answer range is
        /// ever clamped by a bound sized for a different one. Falls back to 7
        /// (the placeholder questionnaires' own default) if nothing is linked
        /// yet.</summary>
        public float questionnaireMax =>
            delphiQuestionnaire != null && delphiQuestionnaire.questions != null && delphiQuestionnaire.questions.Count > 0
                ? delphiQuestionnaire.questions.Max(q => q.steps)
                : 7f;
        [Tooltip("When the optimizer hands over a new parameter set, ramp " +
                 "LINEARLY to it over this many seconds instead of snapping — " +
                 "an instant jolt is itself a startle stimulus.")]
        [Min(0f)] public float transitionSeconds = 3f;
        [Tooltip("Seconds AFTER the ramp completes where nothing changes — the " +
                 "participant just experiences the new parameter set — before " +
                 "measurement begins. Covers the physiological-lag buffer (GSR " +
                 "≈ 1–4s, HR ≈ 5–10s) so the window doesn't measure a body " +
                 "still catching up to the new parameter set.")]
        [Min(0f)] public float idleSeconds = 7f;
        [Tooltip("Seconds actually averaged into the objective, once " +
                 "transition + idle are done. Implicit only — Explicit has " +
                 "no fixed measurement phase (AwaitingRating ends whenever the " +
                 "participant submits).")]
        [Min(1f)] public float measurementSeconds = 30f;
        [Tooltip("Explicit only. How long the participant drives/experiences " +
                 "the current parameter set (Phase.Trial) after washout, " +
                 "before the simulator freezes and the rating questionnaire " +
                 "appears. Unlike Implicit's measurementSeconds this isn't " +
                 "averaged into anything — it's purely how long they get to " +
                 "feel the parameters before being asked to rate them.")]
        [Min(1f)] public float explicitTrialSeconds = 20f;
        /// <summary>transitionSeconds + idleSeconds — derived, not authored
        /// directly. What Explicit's per-iteration washout actually is; for
        /// Implicit it's the discarded lead-in before measurementSeconds
        /// starts. (Not drawn by the default Inspector — it's a computed
        /// property, not a serialized field; the custom editor's timing
        /// panel shows it as a readout instead.)</summary>
        public float washoutSeconds => transitionSeconds + idleSeconds;
        /// <summary>washoutSeconds + measurementSeconds — derived. Seconds
        /// one parameter set is active for before the next is requested,
        /// Implicit only.</summary>
        public float windowSeconds => washoutSeconds + measurementSeconds;
        [Tooltip("Empty = auto: the project-local venv at BOPythonEnv " +
                 "(Scripts/python.exe on Windows, bin/python3 elsewhere).")]
        public string pythonPath = "";
        public int seed = 3;

        // Drawn inside the custom editor's "BO configuration" foldout, which
        // supplies its own "Model-fit cost" section label — no [Header] here,
        // that would double up (PropertyField renders a field's own
        // decorator attributes even when called explicitly, so this used to
        // print its heading a second time right above the foldout's own).
        [Tooltip("How the GP model fit + acquisition optimization is allowed " +
                 "to cost, in real wall-clock seconds sitting between two " +
                 "iterations with a participant in the car. These three " +
                 "numbers (below) are what actually control that cost — " +
                 "problem size (a handful of driving parameters, a couple of objectives) is tiny, " +
                 "so cutting them down loses very little optimization quality " +
                 "for a large drop in wait time. Increase them later, offline, " +
                 "if you want to study convergence quality instead of run one " +
                 "live.")]
        [Min(1)] public int numRestarts = 3;
        [Min(1)] public int rawSamples = 128;
        [Min(1)] public int mcSamples = 64;
        [Tooltip("If mobo.py hasn't sent the next parameter set within this " +
                 "many seconds of receiving the previous objectives, fail the " +
                 "condition instead of sitting frozen forever. Generous on " +
                 "purpose — the FIRST model-guided iteration is always the " +
                 "slowest one (no warmed-up model yet) — but a real hang " +
                 "(dead process, Python exception that didn't bring the " +
                 "process down) needs a ceiling. 0 = no timeout.")]
        [Min(0f)] public float optimizerResponseTimeoutSeconds = 180f;
        [Tooltip("PLANNING ESTIMATE ONLY — how long the BO hub is expected to " +
                 "actually take between iterations, for the timing panel/" +
                 "timeline's wall-clock math. NOT a ceiling: unlike " +
                 "optimizerResponseTimeoutSeconds above (which the session " +
                 "waits up to before failing), this number doesn't gate " +
                 "anything — it's purely descriptive, so make a realistic " +
                 "guess rather than a safe-but-alarming worst case.")]
        [Min(0f)] public float boProcessingEstimateSeconds = 2f;

        /// <summary>Seconds of the window actually averaged into the
        /// objective — directly authored now (measurementSeconds); kept as
        /// its own property since callers elsewhere already read
        /// MeasureSeconds by this name.</summary>
        public float MeasureSeconds => measurementSeconds;

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
        public bool IsRunningCondition => CurrentPhase is Phase.WaitingForOptimizer
            or Phase.WaitingForParameters or Phase.Washout or Phase.Trial or Phase.Measuring or Phase.AwaitingRating;

        public int Iteration { get; private set; }
        public int TotalIterations => _activeConfig?.iterations ?? 0;
        public float LastCoverage { get; private set; } = float.NaN;

        /// <summary>One channel's snapshot from the most recently captured
        /// baseline. Read by ExperimentUI — see CaptureBaseline, which fills
        /// this in instead of dumping the numbers to the console.</summary>
        public readonly struct BaselineReading
        {
            public readonly Channel channel;
            public readonly float mean;
            public readonly int sampleCount;
            public readonly float lowerBound, upperBound;
            public readonly float sd;
            public readonly bool higherIsBetter;

            public BaselineReading(Channel channel, float mean, int sampleCount,
                                    float lowerBound, float upperBound, float sd, bool higherIsBetter)
            {
                this.channel = channel; this.mean = mean; this.sampleCount = sampleCount;
                this.lowerBound = lowerBound; this.upperBound = upperBound;
                this.sd = sd; this.higherIsBetter = higherIsBetter;
            }
        }

        /// <summary>The last condition's captured baseline, one entry per
        /// channel that delivered samples. Empty until the first baseline of
        /// the session, and REPLACED (not appended to) at every new one.</summary>
        public IReadOnlyList<BaselineReading> LastBaselineReadings { get; private set; } = Array.Empty<BaselineReading>();
        /// <summary>Channels that delivered zero samples during the last
        /// baseline window — excluded from that condition's objectives.</summary>
        public IReadOnlyList<Channel> LastBaselineMissingChannels { get; private set; } = Array.Empty<Channel>();
        /// <summary>Which condition LastBaselineReadings belongs to, so the UI
        /// can label the readout instead of showing unlabeled numbers.</summary>
        public int LastBaselineConditionNumber { get; private set; }
        public ConditionKind LastBaselineConditionKind { get; private set; }

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

        // ── Timeline state (researcher UI) ──────────────────────────────
        // The stops are a VIEW of the plan, rebuilt with it — see BuildPlan.
        private readonly List<TimelineStop> _timeline = new();
        private int _timelineOrder = -1;  // which orderIndex _timeline was built for
        private double _sessionStart;     // DelphiClock time the session began, 0 = not started

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
        private double _baselineWindowStart, _baselineWindowEnd; // absolute DelphiClock times inside the meditation
        private double _fadeoutStart; // absolute DelphiClock time the meditation volume ramp begins
        private bool _baselineCaptured;

        // ── Questionnaire state (Phase.Questionnaire) ───────────────────
        private bool _questionnaireUiShown;   // trialAsk narration finished, form now visible
        private bool _questionnaireConfirmed;
        private int _questionnaireSlot;
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
        private double _lastFreePlayActivity; // last slider move, for the idle nudge
        private int _autoNudgesPlayed;

        private void Awake()
        {
            if (manager == null)    manager    = FindAnyObjectByType<DelphiManager>();
            if (carDriver == null)  carDriver  = FindAnyObjectByType<CarDriver>();
            if (recorder == null)   recorder   = FindAnyObjectByType<SessionRecorder>();
            if (narration == null)  narration  = FindAnyObjectByType<NarrationController>();
            if (motionCues == null) motionCues = FindAnyObjectByType<Delphi.Motion.CarMotionCues>();

            // Two DelphiQuestionnaire instances live in every scene — the
            // per-iteration rating and the post-condition evaluation — so a
            // bare FindAnyObjectByType can't tell them apart. Disambiguated by
            // the scene's own naming convention under the "Questionnaires"
            // parent ("Questionnaire — Per Trial (...)" /
            // "Questionnaire — Condition Evaluation"), which is the only
            // signal that exists today — a wrong guess here would silently
            // attribute a participant's answers to the wrong questionnaire,
            // so this only auto-assigns on an unambiguous name match, never a
            // guess. VrQuestionnairePanel lives on the SAME GameObject as its
            // DelphiQuestionnaire in every authored scene, so once the
            // questionnaire is identified the panel is just a GetComponent.
            if (delphiQuestionnaire == null || conditionEvaluation == null)
            {
                foreach (var dq in FindObjectsByType<DelphiQuestionnaire>(FindObjectsInactive.Exclude))
                {
                    string n = dq.gameObject.name;
                    if (conditionEvaluation == null && n.Contains("Condition Evaluation"))
                    {
                        conditionEvaluation = dq;
                        if (conditionEvaluationPanel == null)
                            conditionEvaluationPanel = dq.GetComponent<VR.VrQuestionnairePanel>();
                    }
                    else if (delphiQuestionnaire == null && n.Contains("Per Trial"))
                    {
                        delphiQuestionnaire = dq;
                        if (delphiQuestionnairePanel == null)
                            delphiQuestionnairePanel = dq.GetComponent<VR.VrQuestionnairePanel>();
                    }
                }
            }

            // Phase.Questionnaire now advances from the condition-evaluation
            // panel's own submit (see OnConditionEvaluationSubmitted), so
            // there is no listener to attach here. The researcher fallback in
            // TickQuestionnaire still covers the case where no panel is linked.

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
            _sessionStart = DelphiClock.Now;
            Debug.Log($"[Session] Starting — participant '{userId}', order {orderIndex}/{OrderCount} " +
                      $"({DescribeOrder(orderIndex)}), {_plan.Count} segments.");
            AdvanceToNextSegment();
            return true;
        }

        /// <summary>Refuse to start a session the track can't actually run.
        ///
        /// Without a Park marker, CarDriver.RequestPark() brakes to a halt
        /// wherever the car happens to be instead of heading anywhere specific
        /// (see the warning below) — survivable for data (still stationary),
        /// but WHERE the participant ends up between conditions becomes
        /// uncontrolled, which undermines the consistency the procedure is
        /// meant to have.
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
                                 "The car will brake to a halt wherever it happens to be at the end " +
                                 "of each drive, so the participant won't stop at a consistent " +
                                 "place between conditions. Add a Park marker before collecting real data.");
            }
            return true;
        }

        /// <summary>Researcher: the participant has finished the on-screen
        /// questionnaire. Advances immediately — see TickQuestionnaire — the
        /// car was already reset to start when the trialAsk narration
        /// finished, so there's nothing left to wait for.</summary>
        public void ConfirmQuestionnaire()
        {
            if (CurrentPhase == Phase.Questionnaire && _questionnaireUiShown) _questionnaireConfirmed = true;
        }

        // The three break paths are all SILENT: 0x_breakAsk (played when the
        // BreakOffer segment is entered) is the only break recording there is.
        // Whatever is said once the participant has answered — granting the
        // break, calling them back to the car — is the researcher talking to
        // them in person, not a clip.
        public void ChooseBreak()
        {
            if (CurrentPhase != Phase.BreakOffer) return;
            narration?.StopSpeaking();
            IsAwaitingResearcher = true;
            AwaitingBreakResume = true;
            StatusLine = "Break — waiting to resume the next condition";
        }

        public void ChooseContinue()
        {
            if (CurrentPhase != Phase.BreakOffer) return;
            narration?.StopSpeaking();
            AdvanceToNextSegment();
        }

        public void ResumeFromBreak()
        {
            if (CurrentPhase == Phase.BreakOffer && IsAwaitingResearcher)
            {
                IsAwaitingResearcher = false;
                AdvanceToNextSegment();
            }
        }

        /// <summary>DEBUG ONLY — jump straight past the current timed phase.
        ///
        /// The meditation is several minutes long and sits in front of every
        /// single condition, so testing anything downstream means sitting
        /// through it repeatedly. This skips it.
        ///
        /// IT IS NOT A SHORTCUT FOR REAL SESSIONS AND IT SAYS SO LOUDLY.
        /// The meditation IS the baseline window — skipping it means the
        /// condition that follows has no baseline to score its deviations
        /// against, so that trial's data is not comparable to a proper run.
        /// The warning names the phase so it is unmistakable in the log if
        /// somebody does it by accident during a participant run.</summary>
        public void DebugSkipPhase()
        {
            if (CurrentPhase == Phase.Idle || CurrentPhase == Phase.Complete)
            {
                Debug.LogWarning($"[Trial] Skip ignored — nothing running ({CurrentPhase}).", this);
                return;
            }

            Debug.LogWarning($"[Trial] DEBUG SKIP of {CurrentPhase}." +
                             (CurrentPhase == Phase.Meditation
                                 ? " THE MEDITATION IS THE BASELINE WINDOW — the following condition will have " +
                                   "no baseline and its data must not be treated as a valid trial."
                                 : ""), this);

            narration?.StopSpeaking();
            AdvanceToNextSegment();
        }

        /// <summary>Researcher: the participant has said they're done roaming.
        /// Ends the FreeRoam condition and falls into the SAME tail every other
        /// condition has — the car heads to the park marker while the
        /// evaluation questionnaire runs, then the break offer.</summary>
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
            // SILENTLY DISCARDING THESE COST A LOT OF DEBUGGING TIME.
            //
            // Driving parameters are only writable during FreePlay — correct,
            // because in the measured conditions the optimizer owns them and a
            // stray slider would corrupt the trial. But a plain `return` makes
            // a slider that does nothing indistinguishable from a slider that
            // is not wired up, and outside FreePlay that is EVERY slider.
            // Say so instead.
            if (carDriver == null)
            {
                Debug.LogWarning($"[Trial] Driving parameter '{key}' ignored — no CarDriver assigned.", this);
                return;
            }
            if (CurrentPhase != Phase.FreePlay)
            {
                Debug.LogWarning($"[Trial] Driving parameter '{key}' ignored — parameters are only adjustable " +
                                 $"during FreePlay, and the session is in {CurrentPhase}. In the measured " +
                                 "conditions the optimizer owns these values.", this);
                return;
            }

            SetParam(carDriver.parameters, key, Mathf.Clamp01(value));
            _lastFreePlayActivity = DelphiClock.Now; // they're engaged — restart the idle countdown
            LogFreePlayRow();
        }

        /// <summary>Live manual control on ONE axis — "driving style", from
        /// defensive (0) to aggressive (1) — by moving every driving parameter
        /// together.
        ///
        /// This is only coherent because DrivingParameters holds a uniform
        /// convention: 0 is gentle and 1 is assertive on EVERY axis (the two
        /// that map to an inverted physical quantity, followDistance and
        /// corneringSpeed, are already inverted inside their own mapping). So a
        /// single scalar written to all four is a real point on the
        /// defensive–aggressive diagonal, not an average of four unrelated
        /// numbers.
        ///
        /// It exists rather than four SetFreePlayParameter calls because those
        /// would write four log rows per frame of a drag and leave three
        /// intermediate rows in the file where the car was in a state nobody
        /// asked for. One call, one applied state, one row.</summary>
        public void SetFreePlayStyle(float value)
        {
            if (carDriver == null)
            {
                Debug.LogWarning("[Trial] Driving style ignored — no CarDriver assigned.", this);
                return;
            }
            if (CurrentPhase != Phase.FreePlay)
            {
                Debug.LogWarning($"[Trial] Driving style ignored — parameters are only adjustable during " +
                                 $"FreePlay, and the session is in {CurrentPhase}. In the measured " +
                                 "conditions the optimizer owns these values.", this);
                return;
            }

            float v = Mathf.Clamp01(value);
            foreach (var info in DrivingParameterRegistry.All) info.Set(carDriver.parameters, v);
            _lastFreePlayActivity = DelphiClock.Now;
            LogFreePlayRow();
        }

        /// <summary>The car's current position on the defensive–aggressive
        /// axis: the mean of the style parameters. While only SetFreePlayStyle
        /// has written them they are all equal and this is exact; after a
        /// per-parameter session, or an optimizer handoff, it is the nearest
        /// single-scalar summary — which is the honest thing to show on a
        /// one-slider panel.</summary>
        public float CurrentFreePlayStyle
        {
            get
            {
                if (carDriver == null) return 0.5f;
                var p = carDriver.parameters;
                return DrivingParameterRegistry.All.Average(info => info.Get(p));
            }
        }

        /// <summary>Play extra_exploreNudge — the "try changing something"
        /// prompt for a participant who's sitting through the Explore
        /// condition without touching the sliders. Researcher-triggered from
        /// the UI, and also fired automatically if exploreNudgeIdleSeconds is
        /// set. Only meaningful during Phase.FreePlay; ignored elsewhere so a
        /// stray click can't talk over another phase's narration.</summary>
        public void PlayExploreNudge()
        {
            if (CurrentPhase != Phase.FreePlay) return;
            narration?.Play(NarrationController.Line.ExploreNudge);
            _lastFreePlayActivity = DelphiClock.Now; // don't stack a second nudge straight after
        }

        /// <summary>Automatic nudge, off by default (exploreNudgeIdleSeconds
        /// = 0). Capped at exploreNudgeMaxAuto per condition: a participant
        /// who genuinely wants to just ride along shouldn't be prodded every
        /// minute for the whole roam.</summary>
        private void TickExploreNudge()
        {
            if (exploreNudgeIdleSeconds <= 0f) return;
            if (_autoNudgesPlayed >= exploreNudgeMaxAuto) return;
            if (DelphiClock.Now - _lastFreePlayActivity < exploreNudgeIdleSeconds) return;

            _autoNudgesPlayed++;
            Debug.Log($"[Session] No slider activity for {exploreNudgeIdleSeconds:0}s — " +
                      $"playing the explore nudge ({_autoNudgesPlayed}/{exploreNudgeMaxAuto}).");
            PlayExploreNudge();
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
            iterations = 0,
            samplingIterations = 1
        };

        private void StartFreeRoamCondition()
        {
            conditionId = "freeroam";
            CurrentPhase = Phase.FreePlay;
            IsAwaitingResearcher = true;

            // Recording is IDENTICAL across all three conditions: the sensor
            // csv and every video feed are driven by SessionRecorder, already
            // started back at EnterMeditation (covers the meditation/baseline
            // and the intro narration too, not just the roam itself). These
            // fields only make the trial metadata come out too.
            _activeConfig = FreeRoamConfig;
            _objectiveChannels = new List<Channel>();
            // _baseline is deliberately NOT cleared: FreeRoam has no optimizer
            // to feed, but its meditation measured reference means all the
            // same, and they belong in this condition's trial_meta.json so the
            // recorded physiology is analysable against the same reference the
            // other two conditions use.
            Iteration = 0;
            LastCoverage = float.NaN;
            _trialStart = DelphiClock.Now;
            _driveStart = DelphiClock.Now;
            _trialActuallyStarted = true;
            _lastFreePlayActivity = DelphiClock.Now;
            _autoNudgesPlayed = 0;
            StatusLine = $"Condition {ConditionNumber}/{ConditionCount} (FreeRoam) — " +
                          "roaming; press DONE when the participant says they've finished";

            // The car is parked coming into every condition (startParked, or
            // the previous condition's Questionnaire segment). Nothing else
            // releases it here — the BO conditions un-park at their first iteration,
            // which FreeRoam never reaches — so it has to happen explicitly.
            carDriver?.ResumeDriving();
            motionCues?.Unfreeze();
            StartFreePlayLogging();
        }

        private void StartFreePlayLogging()
        {
            // Recording is already running (started at EnterMeditation);
            // this guard is just a fallback in case that call didn't fire.
            if (recorder != null && !recorder.IsRecording)
                recorder.StartRecording($"trial_{userId}_{conditionId}");

            string dir = recorder != null && recorder.IsRecording
                ? recorder.CurrentSessionPath
                : Path.Combine(Application.persistentDataPath, "Trials");
            Directory.CreateDirectory(dir);
            _freePlayStart = DelphiClock.Now;
            _freePlayLog = new StreamWriter(Path.Combine(dir, "freeplay_log.csv"));
            var header = new StringBuilder("t_s");
            foreach (var key in DrivingParameterRegistry.Keys) header.Append(',').Append(key);
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
            foreach (var info in DrivingParameterRegistry.All)
                row.Append(',').Append(F(info.Get(p)));
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
        /// optimizer connection and — combined with the condition intro not
        /// setting CurrentPhase until its narration finished — let a second
        /// Resume click during that window re-enter a second time).
        ///
        /// One exception to "everything else untouched": if a baseline or
        /// iteration measurement window is open (_acc != null), it is
        /// detached from DelphiCore for the duration of the stop and
        /// reattached on Resume — the car is halted and the researcher may be
        /// talking to the participant, so samples taken during the pause
        /// would silently contaminate that window's mean/variance. CSV/video
        /// recording is NOT paused — that continues exactly as before.
        ///
        /// The stop line (extra_emergencyStop) cuts off whatever was being
        /// said, deliberately: it has to be the thing the participant hears.</summary>
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
            motionCues?.ReturnToNeutral(returnToNeutralSeconds);
            CurrentPhase = Phase.EmergencyStop;
            _phaseEnd = 0;
            IsAwaitingResearcher = true;
            StatusLine = $"EMERGENCY STOP (was {_interruptedPhase})";
            // Detach (not discard) any open measurement window — _acc keeps
            // whatever was accumulated so far and picks back up on Resume,
            // but no samples land in it while the session is paused.
            if (manager != null && manager.Core != null) manager.Core.Accumulator = null;
            // Bookmark whatever was mid-sentence BEFORE talking over it — the
            // phase timer resumes in place, so the rest of that line still has
            // to be said when we come back (see NarrationController.Suspend/
            // ResumeSpeaking).
            narration?.SuspendSpeaking();
            narration?.Play(NarrationController.Line.EmergencyStop);
            Debug.LogWarning($"[Session] Emergency stop during {_interruptedPhase} — paused in place; " +
                              "optimizer connection and trial state untouched; measurement window (if any) detached.");
        }

        /// <summary>Come back from an emergency stop (continues exactly where
        /// it paused) or a trial error (the trial was already torn down by
        /// Fail(), so this restarts the interrupted condition's segment
        /// cleanly instead — there's nothing left to resume in place).</summary>
        public void Resume()
        {
            if (CurrentPhase == Phase.EmergencyStop)
            {
                motionCues?.CancelReturnToNeutral();
                // If the e-stop interrupted the frozen rating questionnaire,
                // the return-to-neutral we just cancelled was overriding that
                // freeze — re-latch it now the seat has settled, rather than
                // leaving it live-computing physics for a car that isn't
                // actually resuming (ResumeDriving is skipped below in this
                // exact case).
                if (_interruptedPhase == Phase.AwaitingRating) motionCues?.FreezeInPlace();
                if (!_wasParkedBeforeStop)
                {
                    carDriver?.ResumeDriving(); motionCues?.Unfreeze();
                    // ResumeDriving() clears any in-flight pullover heading too
                    // (see CarDriver.ResumeDriving) — if the stop interrupted
                    // the questionnaire's real-time pullover mid-manoeuvre,
                    // restart it, otherwise the car would just resume cruising
                    // and never reach IsParked, silently waiting out
                    // TickQuestionnaire's safety ceiling instead.
                    if (_interruptedPhase == Phase.Questionnaire) carDriver?.RequestPullover();
                }
                if (_pausedRemaining >= 0) _phaseEnd = DelphiClock.Now + _pausedRemaining;
                // Reattach whatever measurement window was open before the
                // stop (no-op if _acc is null, i.e. none was open) — see the
                // detach in EmergencyStop().
                if (manager != null && manager.Core != null) manager.Core.Accumulator = _acc;
                // The idle clock is wall-clock, so a long stop mid-roam would
                // otherwise trip the nudge the instant we un-pause.
                if (_interruptedPhase == Phase.FreePlay) _lastFreePlayActivity = DelphiClock.Now;
                CurrentPhase = _interruptedPhase;
                IsAwaitingResearcher = _pausedIsAwaitingResearcher;
                AwaitingBreakResume = _pausedAwaitingBreakResume;
                StatusLine = _pausedStatusLine;
                // There's no "we're carrying on now" recording — coming back is
                // the researcher speaking to the participant in person. What
                // this does is finish the line the stop interrupted, since its
                // phase timer is picking up where it left off too.
                narration?.ResumeSpeaking();
                Debug.Log($"[Session] Resumed — continuing {_interruptedPhase} in place.");
            }
            else if (CurrentPhase == Phase.Error)
            {
                IsAwaitingResearcher = false;
                // Hard stop rather than ResumeSpeaking: this path re-ENTERS the
                // interrupted segment from the top, so anything it narrates
                // will start over on its own.
                narration?.StopSpeaking();
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
        /// <summary>The plan mirrors the recorded narration one-for-one:
        ///
        ///   00_intro
        ///   → 0x_meditation → 01x → [drive 1] → park → 02_trialEval1 → 0x_breakAsk
        ///   → 0x_meditation → 03x → [drive 2] → park → 04_trialEval2 → 0x_breakAsk
        ///   → 0x_meditation → 05x → [drive 3] → park → 06_trialEval3
        ///   → 07_closing
        ///
        /// Every drive gets the same three-step run-up: meditation, framing,
        /// drive. The meditation is where that condition's physiological
        /// baseline is measured (see EnterMeditation), which is exactly why
        /// it repeats before every condition rather than playing once at the
        /// start — each condition needs its own reference means, taken
        /// minutes apart, under the same settled-with-music conditions. It
        /// comes before that condition's framing line (01x/03x/05x) rather
        /// than after, matching the order the lines were recorded in.
        ///
        /// The car is parked throughout (startParked for slot 1, the previous
        /// condition's Questionnaire segment afterwards), so the baseline is
        /// always measured stationary.
        ///
        /// The break is offered BETWEEN conditions only; after the last one
        /// the session just ends.</summary>
        private void BuildPlan()
        {
            _plan.Clear();
            _timeline.Clear();
            _timelineOrder = orderIndex;

            AddStop("INTRO", "welcome & briefing");
            Add(SegmentKind.Intro);

            var order = OrderFor(orderIndex);
            for (int i = 0; i < order.Length; i++)
            {
                int slot = i + 1;
                AddStop($"CONDITION {slot}", KindLabel(order[i]), order[i], slot);
                AddConditionSegment(SegmentKind.Meditation, order[i], slot);
                AddConditionSegment(SegmentKind.ConditionIntro, order[i], slot);
                AddConditionSegment(SegmentKind.Condition, order[i], slot);
                AddSlot(SegmentKind.Questionnaire, slot);
                if (i < order.Length - 1) Add(SegmentKind.BreakOffer);
            }

            AddStop("END", "closing");
            Add(SegmentKind.Complete);

            MeasureStops();
        }

        /// <summary>"FreeRoam" is the internal name; the researcher UI has
        /// called that condition Explore everywhere else since the exploration
        /// rebuild, and a timeline that disagrees with the buttons beside it is
        /// worse than either name on its own.</summary>
        public static string KindLabel(ConditionKind kind) =>
            kind == ConditionKind.FreeRoam ? "Explore" : kind.ToString();

        /// <summary>Opens a timeline stop at whatever the NEXT segment added
        /// will be — so it has to be called immediately before that
        /// segment's Add. The span (lastSegment) and length are filled in by
        /// MeasureStops once the whole plan exists.</summary>
        private void AddStop(string label, string detail,
                             ConditionKind condition = default, int slot = 0) =>
            _timeline.Add(new TimelineStop(label, detail, _plan.Count, _plan.Count,
                                           0f, slot > 0, condition, slot));

        /// <summary>Second pass over the finished plan: give every stop its
        /// segment span and its estimated length. Separate from AddStop
        /// because a stop's extent is only knowable once the stop AFTER it
        /// exists, and its length is the sum over the segments in between.</summary>
        private void MeasureStops()
        {
            for (int i = 0; i < _timeline.Count; i++)
            {
                var s = _timeline[i];
                int last = (i + 1 < _timeline.Count ? _timeline[i + 1].firstSegment : _plan.Count) - 1;
                float est = 0f;
                for (int seg = s.firstSegment; seg <= last && seg < _plan.Count; seg++)
                    est += SegmentEstimatedSeconds(_plan[seg]);
                _timeline[i] = new TimelineStop(s.label, s.detail, s.firstSegment, last,
                                                est, s.isCondition, s.condition, s.slot);
            }
        }

        /// <summary>Rough wall-clock length of ONE plan segment. Every one of
        /// these is an estimate the researcher can already see elsewhere in the
        /// UI (narration clip lengths, EstimatedConditionSeconds) — the point
        /// here is only to weight the timeline so a two-minute meditation and a
        /// twenty-minute drive don't advance the bar equally. BreakOffer is 0:
        /// it's untimed by design and its length says nothing about how far
        /// through the protocol anyone is.</summary>
        private float SegmentEstimatedSeconds(in Segment seg) => seg.kind switch
        {
            SegmentKind.Intro          => NarrationSeconds(NarrationController.Line.Intro),
            SegmentKind.Meditation     => NarrationSeconds(NarrationController.Line.Meditation),
            SegmentKind.ConditionIntro => NarrationSeconds(ConditionIntroLine(seg.slot, seg.condition)),
            SegmentKind.Condition      => seg.condition == ConditionKind.FreeRoam
                                            ? Mathf.Max(1f, freeRoamEstimatedSeconds)
                                            : EstimatedConditionSeconds(seg.condition),
            SegmentKind.Questionnaire  => NarrationSeconds(TrialEvalLine(seg.slot)),
            _                          => 0f,
        };

        private void Add(SegmentKind kind, float seconds = 0f) =>
            _plan.Add(new Segment { kind = kind, seconds = seconds });

        private void AddSlot(SegmentKind kind, int slot) =>
            _plan.Add(new Segment { kind = kind, slot = slot });

        private void AddConditionSegment(SegmentKind kind, ConditionKind condition, int slot) =>
            _plan.Add(new Segment { kind = kind, condition = condition, slot = slot });

        // ── Narration line lookup ───────────────────────────────────────
        /// <summary>Which of the nine condition-intro recordings this slot
        /// wants. They're indexed by SLOT as well as condition kind — the
        /// wording differs between "your first drive" and "your last drive" —
        /// so a counterbalancing order that puts Explicit third plays
        /// 05a_explicit, not 01a_explicit.</summary>
        private static NarrationController.Line ConditionIntroLine(int slot, ConditionKind kind) =>
            (Mathf.Clamp(slot, 1, 3), kind) switch
            {
                (1, ConditionKind.Explicit) => NarrationController.Line.Cond1Explicit,
                (1, ConditionKind.Implicit) => NarrationController.Line.Cond1Implicit,
                (1, _)                      => NarrationController.Line.Cond1Explore,
                (2, ConditionKind.Explicit) => NarrationController.Line.Cond2Explicit,
                (2, ConditionKind.Implicit) => NarrationController.Line.Cond2Implicit,
                (2, _)                      => NarrationController.Line.Cond2Explore,
                (_, ConditionKind.Explicit) => NarrationController.Line.Cond3Explicit,
                (_, ConditionKind.Implicit) => NarrationController.Line.Cond3Implicit,
                (_, _)                      => NarrationController.Line.Cond3Explore,
            };

        /// <summary>Which of the three post-drive evaluation recordings this
        /// slot wants. Indexed by slot only — the evaluation questionnaire is
        /// the same regardless of which condition preceded it.</summary>
        private static NarrationController.Line TrialEvalLine(int slot) => Mathf.Clamp(slot, 1, 3) switch
        {
            1 => NarrationController.Line.TrialEval1,
            2 => NarrationController.Line.TrialEval2,
            _ => NarrationController.Line.TrialEval3,
        };

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
                    narration?.Play(NarrationController.Line.Intro);
                    StartTimer(NarrationSeconds(NarrationController.Line.Intro), "Intro — welcome & task briefing");
                    break;

                case SegmentKind.Meditation:
                    EnterMeditation(seg.condition, seg.slot);
                    break;

                case SegmentKind.ConditionIntro:
                    EnterConditionIntro(seg.condition, seg.slot);
                    break;

                case SegmentKind.Condition:
                    // No narration and no timer of its own — the intro segment
                    // (and the meditation after it) has already played.
                    // FreeRoam is open-ended and ends on the researcher's DONE
                    // button; the other two run the baseline/iteration loop.
                    if (seg.condition == ConditionKind.FreeRoam) StartFreeRoamCondition();
                    else StartConditionTrial(seg.condition);
                    break;

                case SegmentKind.Questionnaire:
                    EnterQuestionnaire(seg.slot);
                    break;

                case SegmentKind.BreakOffer:
                    CurrentPhase = Phase.BreakOffer;
                    narration?.Play(NarrationController.Line.BreakAsk);
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

        /// <summary>The condition's framing narration — played right after
        /// its meditation/baseline (see BuildPlan), which already claimed the
        /// slot (ConditionNumber/CurrentConditionKind) and started recording,
        /// since it now runs first. kind/slot here are only needed to pick
        /// the right recorded line (ConditionIntroLine).</summary>
        private void EnterConditionIntro(ConditionKind kind, int slot)
        {
            var introLine = ConditionIntroLine(slot, kind);
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

            foreach (var key in DrivingParameterRegistry.Keys)
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

        // ── Questionnaire = evaluation, car reset in the background ─────
        /// <summary>The drive just ended: play the evaluation (trialAsk)
        /// line, reset the car back to the start, and wait for that
        /// narration to actually finish playing before the questionnaire
        /// appears — the participant hears the prompt before being handed a
        /// form, not both at once. The reset happens immediately regardless
        /// (an instant teleport, not a physical drive — see
        /// CarDriver.ResetToStart — so there's nothing for the narration to
        /// visually race against). See TickQuestionnaire for the rest: once
        /// the clip's own length has elapsed, the questionnaire appears; once
        /// it's submitted, the segment advances immediately.</summary>
        private void EnterQuestionnaire(int slot)
        {
            CurrentPhase = Phase.Questionnaire;
            _questionnaireConfirmed = false;
            _questionnaireUiShown = false;
            _questionnaireSlot = slot;

            narration?.Play(TrialEvalLine(slot));
            // Pulls over FOR REAL while the evaluation prompt plays — smooth
            // in-lane braking (see CarDriver.RequestPullover), felt through
            // the seat like any other deceleration. The questionnaire itself
            // only appears once the car actually halts (see TickQuestionnaire
            // below), not simply once the narration clip's own length has
            // elapsed — _phaseEnd here is a safety ceiling only, in case
            // something stops the car from ever reporting parked.
            carDriver?.RequestPullover();

            _phaseEnd = DelphiClock.Now + Mathf.Max(NarrationSeconds(TrialEvalLine(slot)), 5f) + 60f;
            StatusLine = "Pulling over for the evaluation…";
        }

        private void TickQuestionnaire()
        {
            if (!_questionnaireUiShown)
            {
                bool parked = carDriver == null || carDriver.IsParked;
                bool doneSpeaking = narration == null || !narration.IsSpeaking;
                bool safetyCeilingHit = _phaseEnd > 0 && DelphiClock.Now >= _phaseEnd;
                if (!(parked && doneSpeaking) && !safetyCeilingHit) return;
                if (safetyCeilingHit && !parked)
                    Debug.LogWarning("[Trial] Questionnaire safety ceiling reached before the car finished " +
                                     "pulling over — showing the evaluation panel anyway.", this);

                _questionnaireUiShown = true;
                _phaseEnd = 0;
                // The car has already come to rest under its own real braking
                // curve, so the seat is already at (or very near) neutral —
                // this just settles the last of it rather than fighting a
                // still-live deceleration cue, which an immediate call here
                // used to do back when this reset was an instant teleport.
                motionCues?.ReturnToNeutral(returnToNeutralSeconds);

                if (conditionEvaluationPanel != null && conditionEvaluation != null)
                {
                    conditionEvaluation.Submitted -= OnConditionEvaluationSubmitted;
                    conditionEvaluation.Submitted += OnConditionEvaluationSubmitted;
                    conditionEvaluationPanel.Show();
                    StatusLine = "Condition evaluation — waiting for participant";
                }
                else
                {
                    // No panel: fall back to the researcher advancing manually
                    // rather than silently skipping the evaluation, which would
                    // lose a condition's worth of data with no trace.
                    IsAwaitingResearcher = true;
                    StatusLine = "Evaluation — waiting for researcher (no condition-evaluation panel linked)";
                    Debug.LogWarning("[Trial] No condition-evaluation panel linked — this condition's " +
                                     "evaluation will not be collected.", this);
                }
                return;
            }

            if (_questionnaireConfirmed)
            {
                // NOW teleport back to the start for whatever comes next — the
                // panel still fills the whole screen at this point, so this
                // reset is exactly as invisible as the old immediate one was,
                // just moved to the OUTGOING side of the questionnaire instead
                // of the incoming one.
                carDriver?.ResetToStart();
                AdvanceToNextSegment();
            }
        }

        /// <summary>Records the post-condition evaluation and lets the session
        /// move on.
        ///
        /// RECORD-ONLY, BY DESIGN. These answers rate a whole condition, and
        /// the optimizer scores single iterations — there is no iteration for
        /// them to belong to, so they are never sent to mobo.py. They are
        /// written for later analysis instead, which means they have to
        /// actually reach disk: collected-and-discarded is the worst of both
        /// worlds, since it looks like data was gathered.
        ///
        /// Raw 1..steps is what gets written, matching the per-trial log, so
        /// both questionnaires are read the same way months from now.</summary>
        private void OnConditionEvaluationSubmitted(Dictionary<string, float> _)
        {
            conditionEvaluation.Submitted -= OnConditionEvaluationSubmitted;
            WriteConditionEvaluationRow();
            _questionnaireConfirmed = true;   // releases TickQuestionnaire
        }

        /// <summary>Appends this condition's evaluation to a per-session CSV,
        /// writing the header only when the file is first created so the three
        /// conditions accumulate into one readable table.</summary>
        private void WriteConditionEvaluationRow()
        {
            try
            {
                string dir = recorder != null && recorder.IsRecording
                    ? recorder.CurrentSessionPath
                    : Path.Combine(Application.persistentDataPath, "Trials");
                Directory.CreateDirectory(dir);

                string path = Path.Combine(dir, "condition_evaluation.csv");
                bool isNew = !File.Exists(path);

                using var w = new StreamWriter(path, append: true);
                if (isNew)
                {
                    var header = new StringBuilder("userId,orderIndex,conditionNumber,conditionKind,t_s");
                    foreach (var q in conditionEvaluation.questions)
                        header.Append(',').Append(q.key).Append("_1to").Append(q.steps);
                    w.WriteLine(header.ToString());
                }

                var row = new StringBuilder();
                row.Append(userId).Append(',').Append(orderIndex).Append(',')
                   .Append(ConditionNumber).Append(',').Append(CurrentConditionKind).Append(',')
                   .Append(F((float)(DelphiClock.Now - _trialStart)));
                foreach (var q in conditionEvaluation.questions)
                    row.Append(',').Append(q.response);   // raw 1..steps, 0 if unanswered
                w.WriteLine(row.ToString());

                Debug.Log($"[Trial] Condition evaluation written to {path}", this);
            }
            catch (Exception e)
            {
                // Never let a disk problem strand the participant mid-session:
                // the answer is lost, the session continues, and the log says so.
                Debug.LogError($"[Trial] Could not write the condition evaluation: {e.Message}", this);
            }
        }

        // ── Meditation = baseline ───────────────────────────────────────
        /// <summary>The meditation, which doubles as this condition's
        /// physiological baseline. One audio file, three authored sections:
        /// meditationAcclimatisationSeconds of just listening, then
        /// meditationMeasurementSeconds during which every sample is averaged
        /// into the reference means the whole Implicit objective is computed
        /// against, then meditationFadeoutSeconds during which the track's
        /// volume ramps to silence. The phase ends there — the recording may
        /// run longer than the three combined, deliberately, and whatever's
        /// past that point is simply never reached.
        ///
        /// Measuring here rather than in a separate silent phase is the point:
        /// the participant is already settled and the acoustic environment is
        /// identical every time, so the reference isn't contaminated by the
        /// transition into it. The fadeout after the window exists so the
        /// last moments — where they may already be bracing for the drive —
        /// stay out of the average.
        ///
        /// The phase length is the CLIP's length, so the window follows the
        /// actual recording rather than a typed number that can drift away
        /// from it.
        ///
        /// This is the first segment of each condition (see BuildPlan), so it
        /// also claims the slot (ConditionNumber/CurrentConditionKind) and
        /// starts recording — the researcher UI shows which condition is
        /// coming, and the csv/video folder for it, from the moment the
        /// meditation track starts rather than only once the framing line
        /// (ConditionIntro) plays afterward.</summary>
        private void EnterMeditation(ConditionKind kind, int slot)
        {
            ConditionNumber = slot;
            CurrentConditionKind = kind;
            ResetParametersToNeutral();

            if (recorder != null && !recorder.IsRecording)
                recorder.StartRecording($"trial_{userId}_{kind.ToString().ToLowerInvariant()}");

            CurrentPhase = Phase.Meditation;
            narration?.Play(NarrationController.Line.Meditation);

            float clipLength = NarrationSeconds(NarrationController.Line.Meditation);
            float needed = meditationAcclimatisationSeconds + meditationMeasurementSeconds + meditationFadeoutSeconds;
            float duration = Mathf.Min(clipLength, needed);

            // Anything left over from a previous condition must go: a stale
            // reference mean silently applied to the next condition is the
            // one failure here that produces plausible-looking bad data.
            _baseline.Clear();
            _baselineCaptured = false;
            _acc = null;
            if (manager != null && manager.Core != null) manager.Core.Accumulator = null;

            if (needed > clipLength)
            {
                Debug.LogWarning($"[Trial] The meditation track is {clipLength:0.0}s but the authored sections " +
                                 $"need {needed:0.0}s ({meditationAcclimatisationSeconds:0}s acclimatisation + " +
                                 $"{meditationMeasurementSeconds:0}s measurement + {meditationFadeoutSeconds:0}s " +
                                 "fadeout). Ending the phase at the clip's own length instead — the reference " +
                                 "means will be based on fewer samples than intended, and the fadeout may be cut " +
                                 "short. Use a longer recording, or shorten the authored sections.");
            }

            StartTimer(duration, "Meditation");

            double trackStart = _phaseEnd - duration;
            float acclimatisation = Mathf.Min(meditationAcclimatisationSeconds, duration);
            float measurementEnd = Mathf.Min(acclimatisation + meditationMeasurementSeconds, duration);
            _baselineWindowStart = trackStart + acclimatisation;
            _baselineWindowEnd = trackStart + measurementEnd;
            _fadeoutStart = trackStart + Mathf.Max(measurementEnd, duration - meditationFadeoutSeconds);

            StatusLine = $"Meditation ({Clock(duration)}) — acclimatisation {Clock(meditationAcclimatisationSeconds)}, " +
                         $"then measuring {Clock(meditationMeasurementSeconds)}";
            Debug.Log($"[Trial] Meditation started ({Clock(duration)}). Acclimatisation " +
                      $"{Clock(meditationAcclimatisationSeconds)} → measurement window " +
                      $"{Clock(_baselineWindowStart - trackStart)}–{Clock(_baselineWindowEnd - trackStart)} → " +
                      $"fadeout from {Clock(_fadeoutStart - trackStart)}.");
        }

        private void TickMeditation()
        {
            double now = DelphiClock.Now;

            if (!_baselineCaptured && _acc == null && now >= _baselineWindowStart && now < _baselineWindowEnd)
            {
                _acc = new WindowAccumulator();
                if (manager != null && manager.Core != null) manager.Core.Accumulator = _acc;
                StatusLine = "Meditation — measuring baseline";
                Debug.Log("[Trial] Baseline window open — accumulating.");
            }

            if (!_baselineCaptured && _acc != null && now >= _baselineWindowEnd)
                CaptureBaseline();

            // Fade the track to silence over meditationFadeoutSeconds, ending
            // exactly as the phase ends — not left to whatever the clip's own
            // mix happens to taper to past the authored sections.
            if (narration != null && narration.source != null &&
                meditationFadeoutSeconds > 0f && now >= _fadeoutStart)
            {
                float t = Mathf.Clamp01((float)((now - _fadeoutStart) / meditationFadeoutSeconds));
                narration.source.volume = 1f - t;
            }

            if (_phaseEnd > 0 && now >= _phaseEnd)
            {
                // The track ran out before the window closed (short clip, or
                // Mute Test). Take whatever was gathered rather than dropping
                // the baseline entirely — CaptureBaseline warns per channel.
                if (!_baselineCaptured && _acc != null) CaptureBaseline();
                AdvanceToNextSegment();
            }
        }

        /// <summary>Close the accumulator and turn it into this condition's
        /// reference means — one per attached channel, REGARDLESS of which
        /// objective source is about to run. Physiology channels are recorded
        /// through an Explicit condition too, and having their baseline on
        /// record makes that data analysable after the fact. The bounds come
        /// from baseline ± k·(literature SD), never from anything measured
        /// here.</summary>
        private void CaptureBaseline()
        {
            if (manager != null && manager.Core != null) manager.Core.Accumulator = null;

            _baseline.Clear();
            var readings = new List<BaselineReading>();
            var missing = new List<Channel>();
            foreach (var ch in CandidateChannels())
            {
                var (mean, count) = _acc.Mean(ch);
                if (count == 0) { missing.Add(ch); continue; }

                _baseline[ch] = mean;
                var cfg = EffectiveConfig(ch);
                var (lo, hi) = ChannelMath.Bounds(mean, cfg.sd, boundK);
                readings.Add(new BaselineReading(ch, mean, count, lo, hi, cfg.sd, cfg.higherIsBetter));
            }

            _acc = null;
            _baselineCaptured = true;

            // The actual readings go to the Experimenter UI (see
            // ExperimentUI's BASELINE panel), not the console — a researcher
            // watching the app has no reason to be tailing Player.log for
            // this. The console keeps only a one-line confirmation, plus a
            // warning if a channel actually dropped out (a real problem,
            // distinct from the numbers themselves).
            LastBaselineReadings = readings;
            LastBaselineMissingChannels = missing;
            LastBaselineConditionNumber = ConditionNumber;
            LastBaselineConditionKind = CurrentConditionKind;

            if (missing.Count > 0)
                Debug.LogWarning($"[Trial] Baseline: {missing.Count} channel(s) delivered no samples " +
                                  $"({string.Join(", ", missing)}) — excluded from this condition's objectives.");
            Debug.Log($"[Trial] Baseline captured for condition {ConditionNumber} ({CurrentConditionKind}) — " +
                      $"{readings.Count} channel(s). See the Experimenter UI for the readings.");
            StatusLine = "Meditation — baseline captured, winding down";
        }

        private void EnterComplete()
        {
            CurrentPhase = Phase.Complete;
            ConditionNumber = 0;
            IsAwaitingResearcher = false;
            _phaseEnd = 0;
            StatusLine = "Session complete";
            narration?.Play(NarrationController.Line.Closing);
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
                // Every purely-timed segment now behaves identically: when the
                // clock runs out, walk to the next segment. ConditionIntro
                // used to fall straight into the trial from here; the plan
                // holds a separate Condition segment for that now, so that a
                // Meditation can sit in between (see BuildPlan).
                case Phase.Intro:
                case Phase.ConditionIntro:
                    if (_phaseEnd > 0 && DelphiClock.Now >= _phaseEnd) AdvanceToNextSegment();
                    break;

                // Same timer, but it also runs the baseline window inside it.
                case Phase.Meditation: TickMeditation(); break;

                // No timer at all — gated on the participant answering AND
                // the car actually arriving, whichever finishes last.
                case Phase.Questionnaire: TickQuestionnaire(); break;

                case Phase.FreePlay:
                    TickExploreNudge();
                    break;

                case Phase.WaitingForOptimizer:    TickWaitingForOptimizer(); break;
                case Phase.WaitingForParameters:
                case Phase.Washout:
                case Phase.Trial:
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
                if (delphiQuestionnaire == null) { Fail("objectiveSource is Questionnaire but no DelphiQuestionnaire is linked."); return; }
                int headerCount = delphiQuestionnaire.questions?.Count ?? 0;
                if (headerCount < 2) { Fail("mobo.py needs ≥2 objectives — the linked DelphiQuestionnaire has fewer than 2 questions."); return; }
            }
            else if (CandidateChannels().Count < 2)
            {
                Fail("mobo.py needs ≥2 objectives — attach and enable at least two scalar sensors on DelphiManager.");
                return;
            }

            // Recording runs for the whole trial; csv + videos + trial log all
            // land in one session folder. Already started back at
            // EnterMeditation (covers the meditation/baseline and the
            // condition-intro narration too) — this guard is just a fallback
            // in case that call didn't fire.
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
            Iteration = 0;
            LastCoverage = float.NaN;
            _acc = null;
            _transTo = null;

            // The car must be parked coming in — the baseline was measured
            // stationary during the meditation, so a moving car here would
            // mean the reference and the first window aren't comparable. It
            // should already be (startParked for slot 1, the previous
            // condition's Questionnaire segment afterwards); this is a
            // diagnostic, not a fix.
            if (carDriver != null && !carDriver.IsParked)
                Debug.LogWarning("[Trial] The condition is starting but the car isn't parked. Check " +
                                  "CarDriver.startParked (should be true) if this is the first condition.");

            if (!SelectObjectiveKeys()) return; // Fail() already set Phase.Error

            Debug.Log($"[Trial] Started ({kind}): {_activeConfig.iterations} × {windowSeconds:0}s windows. " +
                      $"Objective source: {_objectiveSource}. " +
                      $"Baseline: {(_baselineCaptured ? $"{_baseline.Count} channel(s) from the meditation" : "NONE")}.");

            _driveStart = DelphiClock.Now;
            CurrentPhase = Phase.WaitingForOptimizer;
            _phaseEnd = DelphiClock.Now + 60; // generous cap for torch import
            StatusLine = "Waiting for optimizer";
        }

        /// <summary>Decide what this condition actually sends the optimizer as
        /// objectives, now that the baseline is in. Questionnaire mode ignores
        /// the physiology entirely — its keys come straight from the
        /// questionnaire's own header names. Physiology mode uses exactly the
        /// channels that delivered baseline samples during the meditation, so
        /// a sensor that dropped out is excluded rather than feeding the
        /// optimizer a mean it never measured.
        ///
        /// Returns false (having already called Fail) when the condition can't
        /// run.</summary>
        private bool SelectObjectiveKeys()
        {
            if (_objectiveSource == ObjectiveSource.Questionnaire)
            {
                _objectiveChannels = new List<Channel>();
                _questionnaireKeys = delphiQuestionnaire != null
                    ? delphiQuestionnaire.Keys
                    : new List<string>();

                if (!_baselineCaptured)
                    // Not fatal: this condition's objectives are ratings, not
                    // physiology. But the channels are still being recorded,
                    // and without a reference they're far less analysable.
                    Debug.LogWarning("[Trial] No baseline was captured during the meditation. The Explicit " +
                                      "condition can still run (its objectives are ratings), but the recorded " +
                                      "physiology will have no reference means for later analysis.");

                if (_questionnaireKeys.Count < 2)
                    return Fail("Fewer than 2 questionnaire header items are configured — cannot run MOBO.");
                return true;
            }

            _questionnaireKeys = new List<string>();

            if (!_baselineCaptured)
                // Fatal here. The Implicit objective IS deviation from the
                // baseline — without one there is nothing to deviate from,
                // and a condition run anyway would produce data that looks
                // fine and means nothing.
                return Fail("No baseline was captured during the meditation — the Implicit objective is " +
                            "measured against it, so this condition cannot run. Check the meditation clip " +
                            "is assigned and long enough, that Mute Test is off, and that the sensors are live.");

            _objectiveChannels = new List<Channel>(_baseline.Keys);
            if (_objectiveChannels.Count < 2)
                return Fail("Fewer than 2 channels delivered baseline data — cannot run MOBO.");
            return true;
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
            EnterWaitingForParameters("Waiting for first parameter set");
        }

        /// <summary>mobo.py has everything it needs and is about to fit a
        /// model and propose the next parameter set — or, for iterations
        /// still inside the Sobol sampling budget, just hand one back near-
        /// instantly. Both cases land here; optimizerResponseTimeoutSeconds
        /// is set generously enough to cover the slow one (the first
        /// model-guided iteration, with no warmed-up model yet) without
        /// letting a genuine hang sit frozen forever.</summary>
        private void EnterWaitingForParameters(string status)
        {
            CurrentPhase = Phase.WaitingForParameters;
            _phaseEnd = optimizerResponseTimeoutSeconds > 0f
                ? DelphiClock.Now + optimizerResponseTimeoutSeconds
                : 0;
            StatusLine = status;
        }

        private void TickIterationLoop()
        {
            DrainMessages();

            if (CurrentPhase == Phase.WaitingForParameters && _phaseEnd > 0 && DelphiClock.Now >= _phaseEnd)
            {
                Fail($"The optimizer took longer than {optimizerResponseTimeoutSeconds:0}s to propose the next " +
                     "parameter set — see [BO] console output for what it was doing. If this keeps happening, " +
                     "raise optimizerResponseTimeoutSeconds, or lower numRestarts/rawSamples/mcSamples so each " +
                     "model fit is cheaper.");
                return;
            }

            if (CurrentPhase == Phase.Washout && DelphiClock.Now >= _phaseEnd)
            {
                if (_objectiveSource == ObjectiveSource.Questionnaire)
                {
                    // Explicit: the participant drives/experiences the
                    // current parameter set for explicitTrialSeconds before
                    // being asked to rate it — same shape as Implicit's
                    // Washout->Measuring step, just without a windowed mean
                    // to gather (see the Phase.Trial branch below for where
                    // that timer actually ends).
                    _measureStart = DelphiClock.Now;
                    _phaseEnd = _measureStart + explicitTrialSeconds;
                    CurrentPhase = Phase.Trial;
                    StatusLine = $"Iteration {Iteration}/{TotalIterations} — trial";
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
            else if (CurrentPhase == Phase.Trial && DelphiClock.Now >= _phaseEnd)
            {
                // No windowed mean to gather — a rating is one discrete
                // value, not a sampled signal. Park (no jerk on the seat
                // either way) and wait for the participant to submit;
                // RequestNextIteration() (the bridge callback) is what ends
                // this phase, not a timer.
                CurrentPhase = Phase.AwaitingRating;
                StatusLine = $"Iteration {Iteration}/{TotalIterations} — parked, awaiting rating";
                _pendingQuestionnaireValues.Clear();
                carDriver?.FreezeInPlace(); // instant halt, not a drive to a (possibly distant) Park marker
                motionCues?.FreezeInPlace(); // seat holds real forces exactly as they were, not neutralized
                ShowRatingPanel();
            }
            else if (CurrentPhase == Phase.Measuring && DelphiClock.Now >= _phaseEnd)
            {
                manager.Core.Accumulator = null;
                SubmitObjectives();
                _acc = null;
                EnterWaitingForParameters($"Iteration {Iteration}/{TotalIterations} — submitted, waiting");
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
                        motionCues?.Unfreeze();
                        CurrentPhase = Phase.Washout;
                        _phaseEnd = DelphiClock.Now + washoutSeconds;
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
        private bool IsParamOn(string key) =>
            DrivingParameterRegistry.ByKey(key)?.IsOn(carDriver.parameters) ?? true;

        // ── Optimizer messages ──────────────────────────────────────────
        private bool SendInit()
        {
            _activeParamKeys = DrivingParameterRegistry.Keys.Where(IsParamOn).ToList();
            if (_activeParamKeys.Count == 0)
            {
                Fail($"All {DrivingParameterRegistry.All.Length} driving parameters are disabled on CarDriver — nothing for the optimizer to search over.");
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
                // INVERSION IS EXPRESSED AS minimize, NOT BY FLIPPING THE VALUE.
                // Flipping in C# would make the CSV disagree with what the
                // participant actually answered — the log would read 3 where
                // they chose 19. mobo.py already takes a per-objective
                // minimise flag, so the raw answer stays raw everywhere and
                // only the optimizer's sense of "better" changes.
                foreach (var key in objectiveKeys)
                {
                    var q = delphiQuestionnaire.questions.Find(x => x.key == key);
                    int minimize = q != null && q.inverted ? 1 : 0;
                    string objInit = string.Format(CultureInfo.InvariantCulture, "{0},{1},{2}",
                                                   questionnaireMin, questionnaireMax, minimize);
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
                    ["batchSize"] = 1, ["numRestarts"] = numRestarts,
                    ["rawSamples"] = rawSamples, ["mcSamples"] = mcSamples,
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

            var excluded = DrivingParameterRegistry.Keys.Except(_activeParamKeys).ToList();
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
                foreach (var key in DrivingParameterRegistry.Keys)
                    SetParam(p, key, Get(values, key, GetParam(p, key)));
                _transFrom = null;
                _transTo = null;
                _transDuration = 0f;
            }
            else
            {
                _transFrom = new Dictionary<string, float>();
                _transTo = new Dictionary<string, float>();
                foreach (var key in DrivingParameterRegistry.Keys)
                {
                    float current = GetParam(p, key);
                    _transFrom[key] = current;
                    _transTo[key] = Get(values, key, current);
                }
                _transStart = DelphiClock.Now;

                // washoutSeconds is now transitionSeconds + idleSeconds by
                // construction, so the ramp can never run past the washout —
                // no clamp/warning needed any more (used to guard against
                // transitionSeconds exceeding an independently-authored
                // washoutSeconds, which can no longer happen).
                _transDuration = transitionSeconds;
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
            foreach (var key in DrivingParameterRegistry.Keys)
                SetParam(p, key, Mathf.Lerp(_transFrom[key], _transTo[key], t));
            if (t >= 1f) _transTo = null;
        }

        private static float GetParam(DrivingParameters p, string key) =>
            DrivingParameterRegistry.ByKey(key)?.Get(p) ?? 0f;

        private static void SetParam(DrivingParameters p, string key, float v) =>
            DrivingParameterRegistry.ByKey(key)?.Set(p, v);

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

        /// <summary>Shows the per-trial rating panel and arms its submit.
        ///
        /// The handler is re-attached each time rather than once at startup so
        /// it cannot fire for a stale iteration if the panel is rebuilt.</summary>
        private void ShowRatingPanel()
        {
            if (delphiQuestionnairePanel == null)
            {
                Debug.LogError("[Trial] No questionnaire panel linked — the participant has no way to rate " +
                               "this iteration and the trial cannot advance.", this);
                return;
            }

            if (delphiQuestionnaire != null)
            {
                delphiQuestionnaire.Submitted -= OnRatingSubmitted;
                delphiQuestionnaire.Submitted += OnRatingSubmitted;
            }
            delphiQuestionnairePanel.Show();
        }

        /// <summary>Takes the participant's answers and advances the trial.
        ///
        /// RAW 1..steps IS WHAT GOES TO THE OPTIMIZER, not the normalised
        /// value. mobo.py validates each objective against the
        /// [questionnaireMin, questionnaireMax] bounds it was told at init and
        /// normalises internally — handing it an already-normalised number
        /// would fail that bounds check outright, and normalising twice is
        /// what corrupted the CSV in an earlier version of this code.
        ///
        /// It is also the number that belongs in the log: "17 out of 21" is
        /// recoverable and interpretable years later; "0.8" is not.</summary>
        private void OnRatingSubmitted(Dictionary<string, float> _)
        {
            if (CurrentPhase != Phase.AwaitingRating) return;   // stale panel, ignore

            foreach (var q in delphiQuestionnaire.questions)
                _pendingQuestionnaireValues[q.key] = q.response;   // raw 1..steps

            delphiQuestionnaire.Submitted -= OnRatingSubmitted;
            Debug.Log($"[Trial] Rating submitted for iteration {Iteration}: " +
                      string.Join(", ", _pendingQuestionnaireValues.Select(kv => $"{kv.Key}={kv.Value:0}")), this);

            RequestNextIteration();
        }

        private void RequestNextIteration()
        {
            if (CurrentPhase != Phase.AwaitingRating) return;
            SubmitQuestionnaireObjectives();
            carDriver?.ResumeDriving();
            motionCues?.Unfreeze();
            EnterWaitingForParameters($"Iteration {Iteration}/{TotalIterations} — submitted, waiting");
        }

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
            foreach (var key in DrivingParameterRegistry.Keys) header.Append(',').Append(key);
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
            foreach (var info in DrivingParameterRegistry.All)
                row.Append(',').Append(F(info.Get(p)));
            foreach (var c in objectiveCells) row.Append(',').Append(c);
            row.Append(',').Append(float.IsNaN(LastCoverage) ? "NaN" : F(LastCoverage));
            _trialLog.WriteLine(row.ToString());
            _trialLog.Flush();
        }

        private static string F(double v) => v.ToString("F4", CultureInfo.InvariantCulture);

        /// <summary>m:ss — baseline window boundaries are quoted against the
        /// meditation track, and "1:50–2:00" is checkable against the audio
        /// file in a way that "110s–120s" isn't.</summary>
        private static string Clock(double seconds)
        {
            int total = Mathf.Max(0, Mathf.RoundToInt((float)seconds));
            return $"{total / 60}:{total % 60:00}";
        }

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

                    meditationAcclimatisationSeconds = meditationAcclimatisationSeconds,
                    meditationMeasurementSeconds = meditationMeasurementSeconds,
                    meditationFadeoutSeconds = meditationFadeoutSeconds,
                    baselineChannelCount = _baseline.Count,
                    windowSeconds = windowSeconds,
                    washoutSeconds = washoutSeconds,
                    measureSeconds = MeasureSeconds,
                    transitionSeconds = transitionSeconds,
                    idleSeconds = idleSeconds,
                    explicitTrialSeconds = explicitTrialSeconds,

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

                    contactRateHz = manager != null ? manager.contactRateHz : 0f,
                    gazeRateHz = manager != null ? manager.gazeRateHz : 0f,

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
                // Newtonsoft, not JsonUtility — matches the optimizer protocol
                // (BoBridge) and, unlike JsonUtility, can be told to write
                // NaN as the JSON string "NaN" (FloatFormatHandling.String)
                // instead of a bare NaN token, which finalHypervolumeCoverage/
                // baselineMean can legitimately be and which bare-token JSON
                // isn't valid per spec / many parsers reject.
                var jsonSettings = new Newtonsoft.Json.JsonSerializerSettings
                {
                    Formatting = Newtonsoft.Json.Formatting.Indented,
                    FloatFormatHandling = Newtonsoft.Json.FloatFormatHandling.String
                };
                File.WriteAllText(Path.Combine(dir, "trial_meta.json"),
                                  Newtonsoft.Json.JsonConvert.SerializeObject(meta, jsonSettings));
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
            var ranges = new TrialParameterRangeMeta[DrivingParameterRegistry.All.Length];
            for (int i = 0; i < DrivingParameterRegistry.All.Length; i++)
            {
                var info = DrivingParameterRegistry.All[i];
                ranges[i] = new TrialParameterRangeMeta
                {
                    key = info.Key, unit = info.Unit,
                    physicalMin = info.PhysicalAtZero(p), physicalMax = info.PhysicalAtOne(p)
                };
            }
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

        // ══ TIMELINE — the researcher's map of the session ═══════════════
        //  Five stops (intro, three conditions, end), each owning a run of
        //  plan segments. Everything below is read-only except JumpToStop.
        // ═════════════════════════════════════════════════════════════════

        /// <summary>The stops, in order. Available BEFORE the session starts
        /// too — the researcher has to be able to see what order 4 actually
        /// means, and start at a specific stop, while still idle.
        ///
        /// While idle the plan is rebuilt on demand whenever orderIndex has
        /// changed since it was last built. That is safe precisely because
        /// BuildPlan is a pure function of orderIndex with no side effects
        /// beyond _plan/_timeline, and a running session never takes this
        /// branch — CanStart is false for every phase between Intro and
        /// Complete, and the UI locks the order buttons then anyway.</summary>
        public IReadOnlyList<TimelineStop> Timeline
        {
            get
            {
                // Edit mode: always rebuild fresh — the custom Inspector's
                // timeline needs to reflect whatever timing field was just
                // typed into, and there's no in-progress session plan that
                // rebuilding could disrupt. BuildPlan() is cheap (list
                // clears/inserts + narration length lookups), so this is
                // safe to do on every Inspector repaint.
                //
                // Play mode keeps the original orderIndex-keyed cache: once
                // CanStart goes false (a session is actually running),
                // nothing should rebuild the plan out from under it.
                if (!Application.isPlaying)
                    BuildPlan();
                else if (CanStart && (_timelineOrder != orderIndex || _timeline.Count == 0))
                    BuildPlan();
                return _timeline;
            }
        }

        /// <summary>Which stop the session is inside right now, or -1 when it
        /// hasn't started. Derived from the segment index rather than tracked
        /// separately, so it cannot drift out of step with the plan walk —
        /// including after a jump, an error restart or an emergency stop
        /// (which pauses in place and leaves _segmentIndex alone).</summary>
        public int CurrentStopIndex
        {
            get
            {
                if (CurrentPhase == Phase.Idle || _timeline.Count == 0) return -1;
                int found = 0;
                for (int i = 0; i < _timeline.Count; i++)
                {
                    if (_segmentIndex < _timeline[i].firstSegment) break;
                    found = i;
                }
                return found;
            }
        }

        /// <summary>0 for a stop not reached yet, 1 for one already passed, and
        /// the fraction through it for the one running.</summary>
        public float StopProgress01(int stopIndex)
        {
            int cur = CurrentStopIndex;
            if (cur < 0 || stopIndex < 0 || stopIndex >= _timeline.Count) return 0f;
            if (stopIndex < cur) return 1f;
            if (stopIndex > cur) return 0f;

            var stop = _timeline[stopIndex];
            float total = 0f, done = 0f;
            for (int seg = stop.firstSegment; seg <= stop.lastSegment && seg < _plan.Count; seg++)
            {
                float w = SegmentEstimatedSeconds(_plan[seg]);
                total += w;
                if (seg < _segmentIndex) done += w;
                else if (seg == _segmentIndex) done += w * CurrentSegmentFraction01();
            }
            return total <= 0f ? (_segmentIndex >= stop.lastSegment ? 1f : 0f)
                               : Mathf.Clamp01(done / total);
        }

        /// <summary>How far through the CURRENT segment we are. Each kind knows
        /// its own answer: timed segments from their countdown, a BO drive from
        /// its iteration count, and FreeRoam — which has no length at all — from
        /// elapsed time against freeRoamEstimatedSeconds, capped just short of
        /// full so an open-ended roam never reads as finished while it's still
        /// running.</summary>
        private float CurrentSegmentFraction01()
        {
            if (_segmentIndex < 0 || _segmentIndex >= _plan.Count) return 0f;
            var seg = _plan[_segmentIndex];

            if (seg.kind == SegmentKind.Condition)
            {
                if (CurrentPhase != Phase.FreePlay) return CurrentConditionProgress();
                double elapsed = _driveStart > 0 ? DelphiClock.Now - _driveStart : 0;
                return Mathf.Clamp((float)elapsed / Mathf.Max(1f, freeRoamEstimatedSeconds), 0f, 0.95f);
            }

            float est = SegmentEstimatedSeconds(seg);
            if (est <= 0f) return 0f;
            return Mathf.Clamp01(1f - (float)(PhaseSecondsRemaining / est));
        }

        /// <summary>How far through the whole session we are, weighted by each
        /// stop's estimated length — so the bar tracks time rather than
        /// counting a two-minute intro as a fifth of the protocol.</summary>
        public float SessionProgress01
        {
            get
            {
                var stops = Timeline;
                int cur = CurrentStopIndex;
                if (cur < 0 || stops.Count == 0) return 0f;
                if (CurrentPhase == Phase.Complete) return 1f;

                float total = 0f, done = 0f;
                for (int i = 0; i < stops.Count; i++)
                {
                    float w = Mathf.Max(0f, stops[i].estimatedSeconds);
                    total += w;
                    if (i < cur) done += w;
                    else if (i == cur) done += w * StopProgress01(i);
                }
                return total <= 0f ? 0f : Mathf.Clamp01(done / total);
            }
        }

        /// <summary>Estimated wall-clock length of the whole session.</summary>
        public float EstimatedSessionSeconds
        {
            get
            {
                float total = 0f;
                foreach (var s in Timeline) total += Mathf.Max(0f, s.estimatedSeconds);
                return total;
            }
        }

        /// <summary>Real time since the session started. 0 before it does.</summary>
        public double SessionElapsedSeconds =>
            _sessionStart > 0 && CurrentPhase != Phase.Idle ? DelphiClock.Now - _sessionStart : 0;

        /// <summary>Go straight to a stop — starting the session there when
        /// idle, or abandoning whatever is running and picking up from there
        /// when it isn't.
        ///
        /// THIS THROWS DATA AWAY AND IS MEANT TO. It exists for piloting,
        /// re-runs and recovering a session that has gone wrong — not for
        /// ordinary running, which walks the plan by itself. So a mid-session
        /// jump gets the same teardown an abort does (recordings finalised,
        /// trial metadata written, optimizer disposed and relaunched) rather
        /// than a cheaper one: the alternative is a truncated mp4 and a BO
        /// process still carrying the abandoned condition's model into the next
        /// one. Everything it discards is named in the log, loudly, because
        /// from the data's side a jumped-over condition and a completed one
        /// must never look alike afterwards.
        ///
        /// Refused during an emergency stop: that state is a pause with a
        /// participant on a rig mid-incident, and the way out of it is RESUME,
        /// not a silent relocation to somewhere else in the protocol.</summary>
        public bool JumpToStop(int stopIndex)
        {
            if (_quitting) return false;

            if (CurrentPhase == Phase.EmergencyStop)
            {
                Debug.LogWarning("[Session] Jump refused — the rig is in an emergency stop. " +
                                 "Resume it first (F1), then jump.");
                return false;
            }

            var stops = Timeline;            // builds the plan for us if we're idle
            if (stopIndex < 0 || stopIndex >= stops.Count)
            {
                Debug.LogWarning($"[Session] Jump refused — no stop {stopIndex} on a {stops.Count}-stop timeline.");
                return false;
            }
            string label = stops[stopIndex].label;

            if (CanStart)
            {
                if (!ValidateTrackForSession()) return false;
                BuildPlan();                 // fresh, for whatever orderIndex is set NOW
                ConditionNumber = 0;
                _sessionStart = DelphiClock.Now;
                Debug.Log($"[Session] Starting at '{label}' — participant '{userId}', " +
                          $"order {orderIndex}/{OrderCount} ({DescribeOrder(orderIndex)}), " +
                          $"{_plan.Count} segments.");
            }
            else
            {
                Debug.LogWarning($"[Session] JUMP from {CurrentPhase} to '{label}'. Whatever this " +
                                 "condition had recorded is being closed where it stands — it is a " +
                                 "partial run and must not be analysed as a completed one. The stop " +
                                 "jumped into starts a fresh recording of its own.");
                narration?.StopSpeaking();
                delphiQuestionnairePanel?.Hide();
                conditionEvaluationPanel?.Hide();
                carDriver?.ResetToStart();   // never leave the car driving itself
                motionCues?.ReturnToNeutral(returnToNeutralSeconds);
                Cleanup($"Jumped to {label}");
                PrewarmOptimizer();          // the next condition needs a live one
            }

            _segmentIndex = _timeline[stopIndex].firstSegment - 1;
            AdvanceToNextSegment();
            return true;
        }

        // ── Read helpers for the researcher UI ──────────────────────────
        public ConditionKind ConditionKindAt(int slot)
        {
            var order = OrderFor(orderIndex);
            return order[Mathf.Clamp(slot, 0, order.Length - 1)];
        }

        /// <summary>The FIXED wall-clock cost of one iteration, by condition
        /// kind — the actual variables the researcher configured, not a
        /// single blanket number. Implicit: washout + measure, i.e. the whole
        /// windowSeconds — every iteration genuinely takes that long.
        /// Explicit: washout + the timed Trial phase are fixed; AwaitingRating
        /// has no timer at all (it ends whenever the participant submits), so
        /// it's excluded — this is a FLOOR, not the real total.</summary>
        private float FixedSecondsPerIteration(ConditionKind kind) =>
            (kind == ConditionKind.Implicit
                ? windowSeconds
                : washoutSeconds + explicitTrialSeconds) + boProcessingEstimateSeconds;

        /// <summary>Rough wall-clock length of ONE condition's drive. Returns
        /// 0 for FreeRoam, which is open-ended by design — callers should
        /// show "open-ended" rather than print a fake estimate. For Explicit,
        /// this is a FLOOR, not a real total — it doesn't (can't) include
        /// however long the participant actually takes answering each
        /// rating.</summary>
        public float EstimatedConditionSeconds(ConditionKind kind)
        {
            if (kind == ConditionKind.FreeRoam) return 0f;
            var cfg = kind == ConditionKind.Implicit ? implicitTrial : explicitTrial;
            // The meditation is a separate segment, so it isn't counted here —
            // this is the drive only.
            return cfg.iterations * FixedSecondsPerIteration(kind);
        }

        /// <summary>Rough time left in the drive, iteration-boundary-aware:
        /// Iteration counts iterations that have FULLY STARTED (it increments
        /// the moment parameters for a new one arrive), so the iteration
        /// currently in flight is never part of itersLeft — its remaining
        /// time has to come from wherever CurrentPhase actually is:
        ///
        ///  • Washout: PhaseSecondsRemaining only covers washout ITSELF. The
        ///    fixed phase that follows it, later in this SAME iteration,
        ///    isn't reflected anywhere else — so MeasureSeconds (Implicit's
        ///    Measuring) or explicitTrialSeconds (Explicit's Trial) is added
        ///    on top. Leaving that out silently underestimated by exactly
        ///    that amount every single time a condition was mid-washout.
        ///  • Trial / Measuring: PhaseSecondsRemaining alone is already the
        ///    whole remaining truth.
        ///  • WaitingForParameters / AwaitingRating: genuinely nothing
        ///    knowable to add — WaitingForParameters' own _phaseEnd is
        ///    optimizerResponseTimeoutSeconds, a safety CEILING before giving
        ///    up, not an ETA (counting it made the number count down toward
        ///    a value with nothing to do with reality, then jump once the
        ///    real result landed); AwaitingRating (Explicit) ends whenever
        ///    the participant submits, which has no fixed length at all.
        /// </summary>
        public float CurrentConditionSecondsRemaining()
        {
            if (_activeConfig == null || !IsRunningCondition) return 0f;
            int itersLeft = Mathf.Max(0, TotalIterations - Iteration);

            double currentIterationRemaining = CurrentPhase switch
            {
                Phase.Washout => PhaseSecondsRemaining +
                                 (CurrentConditionKind == ConditionKind.Implicit ? MeasureSeconds : explicitTrialSeconds),
                Phase.Trial => PhaseSecondsRemaining,
                Phase.Measuring => PhaseSecondsRemaining,
                _ => 0,
            };

            return (float)currentIterationRemaining + itersLeft * FixedSecondsPerIteration(CurrentConditionKind);
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
