using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using Delphi.Trial;

namespace Delphi.Session
{
    /// <summary>
    /// Orchestrates a whole participant session as one linear walk through an
    /// ordered SEGMENT list, all inside a single scene (no scene loads — those
    /// would tear down sensors, the recorder and the optimizer mid-session).
    ///
    /// The list is built once at <see cref="StartSession"/> from the
    /// counterbalanced condition order + the free-play toggle, so changing the
    /// study structure changes the LIST, not the control flow:
    ///
    ///   Intro → Meditation → Habituation
    ///         → Condition[0] → Parking → Questionnaire → BreakOffer
    ///         → Condition[1] → Parking → Questionnaire
    ///         → [FreePlay] → Interview → Complete
    ///
    /// Segment kinds:
    ///   • Timed     — auto-advances after a DelphiClock duration (Intro,
    ///                 Meditation, Habituation, Parking).
    ///   • Gated     — waits for a researcher button call (Questionnaire,
    ///                 BreakOffer, FreePlay, Interview).
    ///   • Condition — delegates to TrialManager and advances when the trial
    ///                 reaches Finished/Error. The trial's OWN Baseline state is
    ///                 the baseline capture — one source of truth for the
    ///                 optimization; this class only sequences around it.
    ///
    /// Emergency stop is orthogonal: it can interrupt any active segment, aborts
    /// a running trial, and parks the machine in <see cref="Phase.EmergencyStop"/>
    /// until Resume(). The physical YAW-return + passthrough belongs to the VR
    /// layer (Phase 2) and hooks in via <see cref="onEmergencyStop"/>.
    ///
    /// Backbone caveats (intentional, this brick): both conditions currently run
    /// the same physiology-objective TrialManager — Implicit is complete, the
    /// Explicit/Likert objective source is future work. The drive/habituation
    /// segments are real timers but the car is stationary until the simulator is
    /// wired.
    /// </summary>
    public class SessionController : MonoBehaviour
    {
        public enum Phase
        {
            Idle, Intro, Meditation, Habituation, Condition,
            Parking, Questionnaire, BreakOffer, FreePlay,
            Interview, Complete, EmergencyStop
        }

        public enum ConditionKind { Implicit, Explicit }

        [Header("Links (auto-found if left empty)")]
        public TrialManager trial;
        public GuideController guide;

        [Header("Structure")]
        [Tooltip("Counterbalancing: the first main condition. The other one " +
                 "follows. Alternate this across participants.")]
        public ConditionKind firstCondition = ConditionKind.Implicit;
        [Tooltip("Run the optional 6-style free-play round after both main " +
                 "conditions (kept as a toggle for testing).")]
        public bool includeFreePlay = true;

        [Header("Timed-phase durations (seconds)")]
        [Min(0f)] public float introSeconds       = 15f;
        [Tooltip("Guided meditation / relaxation before the first drive.")]
        [Min(0f)] public float meditationSeconds   = 60f;
        [Tooltip("Novelty-washout drive; its data is discarded. Stationary until " +
                 "the simulator is wired.")]
        [Min(0f)] public float habituationSeconds  = 60f;
        [Tooltip("Self-park settle time before the questionnaire.")]
        [Min(0f)] public float parkingSeconds      = 10f;

        // ── Runtime state ───────────────────────────────────────────────
        public Phase CurrentPhase { get; private set; } = Phase.Idle;
        public string StatusLine { get; private set; } = "Idle";
        /// <summary>1-based index of the main condition in progress (0 outside a
        /// condition), and how many there are — for the researcher UI.</summary>
        public int ConditionNumber { get; private set; }
        public int ConditionCount => 2;
        public bool IsAwaitingResearcher { get; private set; }
        /// <summary>At a BreakOffer, true once the participant chose to take a
        /// break and we're waiting on ResumeFromBreak() — so the UI can show a
        /// "resume" control instead of the break/continue pair.</summary>
        public bool AwaitingBreakResume { get; private set; }
        /// <summary>The kind of condition currently running (or last run) — for
        /// the researcher UI's per-condition labels.</summary>
        public ConditionKind CurrentConditionKind { get; private set; }
        public bool CanStart => CurrentPhase == Phase.Idle || CurrentPhase == Phase.Complete;
        public double PhaseSecondsRemaining =>
            _phaseEnd > 0 ? Math.Max(0, _phaseEnd - DelphiClock.Now) : 0;

        [Header("Events (VR/passthrough hooks — optional)")]
        public UnityEngine.Events.UnityEvent onEmergencyStop;
        public UnityEngine.Events.UnityEvent onResume;

        private readonly List<Segment> _plan = new();
        private int _segmentIndex;
        private double _phaseEnd;       // DelphiClock time a timed segment ends
        private Phase _interruptedPhase; // what EmergencyStop paused, for Resume
        private int _interruptedSegment;

        private struct Segment
        {
            public Phase phase;
            public float seconds;        // timed segments only
            public ConditionKind kind;   // Condition segments only
        }

        private void Awake()
        {
            if (trial == null) trial = FindFirstObjectByType<TrialManager>();
            if (guide == null) guide = FindFirstObjectByType<GuideController>();
        }

        // ── Public control (researcher UI) ──────────────────────────────
        /// <summary>Build the plan and begin. No-op unless idle/complete.</summary>
        public bool StartSession()
        {
            if (CurrentPhase != Phase.Idle && CurrentPhase != Phase.Complete)
                return false;

            BuildPlan();
            _segmentIndex = -1;
            ConditionNumber = 0;
            Debug.Log($"[Session] Starting — first condition {firstCondition}, " +
                      $"free-play {(includeFreePlay ? "on" : "off")}, {_plan.Count} segments.");
            AdvanceToNextSegment();
            return true;
        }

        /// <summary>Researcher: the participant has finished the on-screen
        /// questionnaire.</summary>
        public void ConfirmQuestionnaire()
        {
            if (CurrentPhase == Phase.Questionnaire) AdvanceToNextSegment();
        }

        /// <summary>Researcher relays the participant's break choice at a
        /// BreakOffer. Break → guide lets them out, then Resume() (or the same
        /// UI's continue) proceeds; Continue → straight on to the next drive.</summary>
        public void ChooseBreak()
        {
            if (CurrentPhase != Phase.BreakOffer) return;
            guide?.Play(GuideController.Line.BreakGranted);
            IsAwaitingResearcher = true; // wait on ResumeFromBreak()
            AwaitingBreakResume = true;
            StatusLine = "Break — waiting to resume the next condition";
        }

        public void ChooseContinue()
        {
            if (CurrentPhase != Phase.BreakOffer) return;
            guide?.Play(GuideController.Line.ContinueDrive);
            AdvanceToNextSegment();
        }

        /// <summary>Researcher: come back from a granted break into the next
        /// condition.</summary>
        public void ResumeFromBreak()
        {
            if (CurrentPhase == Phase.BreakOffer && IsAwaitingResearcher)
            {
                IsAwaitingResearcher = false;
                guide?.Play(GuideController.Line.ContinueDrive);
                AdvanceToNextSegment();
            }
        }

        /// <summary>Researcher: free-play round is over.</summary>
        public void EndFreePlay()
        {
            if (CurrentPhase == Phase.FreePlay) AdvanceToNextSegment();
        }

        /// <summary>Researcher: the closing interview is done.</summary>
        public void EndInterview()
        {
            if (CurrentPhase == Phase.Interview) AdvanceToNextSegment();
        }

        /// <summary>Safety halt — usable at ANY point. Aborts a running trial,
        /// remembers where we were, and waits in EmergencyStop until Resume().
        /// The physical platform-return/passthrough is an onEmergencyStop
        /// listener (VR layer).</summary>
        public void EmergencyStop()
        {
            if (CurrentPhase == Phase.EmergencyStop || CurrentPhase == Phase.Idle) return;
            _interruptedPhase = CurrentPhase;
            _interruptedSegment = _segmentIndex;
            if (trial != null && trial.State != TrialManager.TrialState.Idle &&
                trial.State != TrialManager.TrialState.Finished &&
                trial.State != TrialManager.TrialState.Error)
                trial.AbortTrial();
            CurrentPhase = Phase.EmergencyStop;
            _phaseEnd = 0;
            IsAwaitingResearcher = true;
            StatusLine = $"EMERGENCY STOP (was {_interruptedPhase})";
            guide?.Play(GuideController.Line.EmergencyStop);
            onEmergencyStop?.Invoke();
            Debug.LogWarning($"[Session] Emergency stop during {_interruptedPhase}.");
        }

        /// <summary>Come back from an emergency stop. A condition can't be safely
        /// resumed mid-optimization, so we restart the interrupted condition's
        /// segment cleanly; non-condition segments just re-enter.</summary>
        public void Resume()
        {
            if (CurrentPhase != Phase.EmergencyStop) return;
            IsAwaitingResearcher = false;
            onResume?.Invoke();
            guide?.Play(GuideController.Line.ResumeAfterStop);
            // Re-enter the interrupted segment from the top (a half-finished
            // trial was aborted, so restart it rather than resume a dead run).
            _segmentIndex = _interruptedSegment - 1;
            Debug.Log($"[Session] Resuming — restarting segment {_interruptedSegment} ({_interruptedPhase}).");
            AdvanceToNextSegment();
        }

        // ── Plan construction ───────────────────────────────────────────
        private void BuildPlan()
        {
            _plan.Clear();
            ConditionKind second = firstCondition == ConditionKind.Implicit
                ? ConditionKind.Explicit : ConditionKind.Implicit;

            Add(Phase.Intro, introSeconds);
            Add(Phase.Meditation, meditationSeconds);
            Add(Phase.Habituation, habituationSeconds);

            AddCondition(firstCondition);
            Add(Phase.Parking, parkingSeconds);
            Add(Phase.Questionnaire);
            Add(Phase.BreakOffer);

            AddCondition(second);
            Add(Phase.Parking, parkingSeconds);
            Add(Phase.Questionnaire);

            if (includeFreePlay) Add(Phase.FreePlay);
            Add(Phase.Interview);
            Add(Phase.Complete);
        }

        private void Add(Phase phase, float seconds = 0f) =>
            _plan.Add(new Segment { phase = phase, seconds = seconds });

        private void AddCondition(ConditionKind kind) =>
            _plan.Add(new Segment { phase = Phase.Condition, kind = kind });

        // ── Segment walk ────────────────────────────────────────────────
        private void AdvanceToNextSegment()
        {
            _segmentIndex++;
            if (_segmentIndex >= _plan.Count) { EnterComplete(); return; }
            EnterSegment(_plan[_segmentIndex]);
        }

        private void EnterSegment(Segment seg)
        {
            CurrentPhase = seg.phase;
            IsAwaitingResearcher = false;
            AwaitingBreakResume = false;
            _phaseEnd = 0;

            switch (seg.phase)
            {
                case Phase.Intro:
                    guide?.Play(GuideController.Line.Welcome);
                    StartTimer(seg.seconds, "Intro — welcome & task briefing");
                    break;

                case Phase.Meditation:
                    guide?.Play(GuideController.Line.Meditation);
                    StartTimer(seg.seconds, "Meditation — relax with calm music");
                    break;

                case Phase.Habituation:
                    guide?.Play(GuideController.Line.HabituationStart);
                    StartTimer(seg.seconds, "Habituation drive — data discarded");
                    break;

                case Phase.Condition:
                    EnterCondition(seg.kind);
                    break;

                case Phase.Parking:
                    guide?.Play(GuideController.Line.Parking);
                    StartTimer(seg.seconds, "Parking");
                    break;

                case Phase.Questionnaire:
                    guide?.Play(GuideController.Line.Questionnaire);
                    IsAwaitingResearcher = true;
                    StatusLine = "Questionnaire — waiting for participant";
                    break;

                case Phase.BreakOffer:
                    guide?.Play(GuideController.Line.BreakOffer);
                    IsAwaitingResearcher = true;
                    StatusLine = "Break? — awaiting participant's choice";
                    break;

                case Phase.FreePlay:
                    guide?.Play(GuideController.Line.FreePlayIntro);
                    IsAwaitingResearcher = true;
                    StatusLine = "Free-play — participant explores 6 styles";
                    break;

                case Phase.Interview:
                    guide?.Play(GuideController.Line.Farewell);
                    IsAwaitingResearcher = true;
                    StatusLine = "Closing interview";
                    break;

                case Phase.Complete:
                    EnterComplete();
                    break;
            }
        }

        private void EnterCondition(ConditionKind kind)
        {
            ConditionNumber++;
            CurrentConditionKind = kind;
            guide?.Play(GuideController.Line.ConditionStart);
            StatusLine = $"Condition {ConditionNumber}/{ConditionCount} ({kind}) — starting";

            if (trial == null) { Fail("No TrialManager linked."); return; }
            trial.conditionId = kind.ToString().ToLowerInvariant();
            // NOTE: both kinds currently run the physiology-objective trial.
            // Explicit needs a Likert objective source (future brick); until
            // then it behaves like Implicit but is tagged "explicit" in logs.
            if (!trial.StartTrial())
                Fail($"Could not start the {kind} trial — see [Trial] console output.");
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
            Debug.Log("[Session] Complete.");
        }

        private void Fail(string why)
        {
            Debug.LogError($"[Session] {why}");
            StatusLine = $"Error: {why}";
            IsAwaitingResearcher = true; // stop here; researcher decides what to do
            CurrentPhase = Phase.EmergencyStop;
        }

        // ── Tick ────────────────────────────────────────────────────────
        private void Update()
        {
            switch (CurrentPhase)
            {
                case Phase.Intro:
                case Phase.Meditation:
                case Phase.Habituation:
                case Phase.Parking:
                    if (_phaseEnd > 0 && DelphiClock.Now >= _phaseEnd) AdvanceToNextSegment();
                    break;

                case Phase.Condition:
                    TickCondition();
                    break;
            }
        }

        private void TickCondition()
        {
            if (trial == null) return;
            switch (trial.State)
            {
                case TrialManager.TrialState.Finished:
                    Debug.Log($"[Session] Condition {ConditionNumber} finished.");
                    AdvanceToNextSegment();
                    break;
                case TrialManager.TrialState.Error:
                    Fail($"Condition {ConditionNumber} trial errored — see [Trial] console output.");
                    break;
                default:
                    // Mirror the trial's own live status so the researcher UI
                    // shows baseline/washout/measuring without duplicating it.
                    StatusLine = $"Condition {ConditionNumber}/{ConditionCount} — {trial.StatusLine}";
                    break;
            }
        }

        // ── Read helpers for the researcher UI ──────────────────────────
        /// <summary>The condition kind in main-condition slot 0 or 1, honouring
        /// the counterbalanced order.</summary>
        public ConditionKind ConditionKindAt(int slot)
        {
            ConditionKind second = firstCondition == ConditionKind.Implicit
                ? ConditionKind.Explicit : ConditionKind.Implicit;
            return slot == 0 ? firstCondition : second;
        }

        /// <summary>Rough wall-clock length of ONE condition: baseline + all
        /// iteration windows. Same for both (identical trial config).</summary>
        public float EstimatedConditionSeconds()
        {
            if (trial == null) return 0f;
            float window = trial.windowStrategy != null ? trial.windowStrategy.WindowSeconds : 0f;
            return trial.baselineSeconds + trial.iterations * window;
        }

        /// <summary>Estimated seconds left in the condition currently running
        /// (0 outside a condition): the current trial phase's remainder plus the
        /// windows not yet started.</summary>
        public float CurrentConditionSecondsRemaining()
        {
            if (trial == null || CurrentPhase != Phase.Condition) return 0f;
            float window = trial.windowStrategy != null ? trial.windowStrategy.WindowSeconds : 0f;
            int itersLeft = Mathf.Max(0, trial.TotalIterations - trial.Iteration);
            return (float)trial.PhaseSecondsRemaining + itersLeft * window;
        }

        /// <summary>Iteration progress of the running condition (0..1), or 0.</summary>
        public float CurrentConditionProgress()
        {
            if (trial == null || CurrentPhase != Phase.Condition || trial.TotalIterations <= 0)
                return 0f;
            return Mathf.Clamp01(trial.Iteration / (float)trial.TotalIterations);
        }

        /// <summary>Which main-condition slot (0/1) is running/last-run, so the UI
        /// can mark the per-condition rows done / active / pending.</summary>
        public int ActiveConditionSlot => Mathf.Clamp(ConditionNumber - 1, -1, 1);

        // ── Debug (context menu) ────────────────────────────────────────
        [ContextMenu("Start Session")] private void CtxStart() => StartSession();
        [ContextMenu("Emergency Stop")] private void CtxStop() => EmergencyStop();

        /// <summary>One-line human summary for the researcher UI / logs.</summary>
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
