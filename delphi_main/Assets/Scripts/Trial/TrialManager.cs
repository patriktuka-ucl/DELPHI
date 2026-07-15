using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Delphi.Simulation;

namespace Delphi.Trial
{
    /// <summary>
    /// Orchestrates one optimization trial, started from the dashboard:
    ///
    ///   Idle → Baseline (stationary, N s; only the last part is averaged)
    ///        → per iteration: RAMP the BO-suggested driving parameters onto
    ///          the ego car → washout → measure window (means via DelphiCore's
    ///          sampling thread) → submit baseline-anchored objectives → repeat
    ///        → Finished (after the configured iteration budget).
    ///
    /// The optimizer is mobo.py (BoTorch qLogNEHVI) via BoBridge; objectives
    /// are DISCOVERED at runtime: every scalar channel attached and enabled on
    /// DelphiManager (and actually producing baseline data) becomes one
    /// objective.
    ///
    /// NORMALIZATION (the whole point of this layer — see
    /// ChannelNormalization.cs): NO z-scores. Each window's mean becomes a
    /// signed deviation d = (mean − baseline) / (k · SD), where SD is a
    /// LITERATURE value the researcher types in per channel (not the noisy
    /// baseline's own spread) and k is a shared bound multiplier — so d = ±1
    /// exactly at the baseline ± k·SD bounds. d is oriented (per-channel
    /// "higher is better") and shaped by one shared activation (Linear / ReLU /
    /// Tanh) into the value the optimizer MINIMIZES.
    ///
    /// Clean split of concerns: DelphiManager is concerned ONLY with acquiring
    /// and cleaning real signals (raw native units → recorder + viz).
    /// Everything BO-facing — bounds, activation, objectives — lives here.
    /// </summary>
    public class TrialManager : MonoBehaviour
    {
        public enum TrialState
        {
            Idle, Baseline, WaitingForOptimizer, WaitingForParameters,
            Washout, Measuring, Finished, Error
        }

        [Header("Links (auto-found if left empty)")]
        public DelphiManager manager;
        public CarDriver carDriver;
        public SessionRecorder recorder;
        [Tooltip("Window logic for each iteration — FixedTrialWindow for the " +
                 "plain 'fixed seconds per parameter set' scheme; fancier " +
                 "strategies (event-aligned, contextual/cBO) plug in here later.")]
        public TrialWindowStrategy windowStrategy;

        [Header("Normalization → BO (see the per-channel table below)")]
        [Tooltip("Shaping applied to every channel's signed deviation before it " +
                 "goes to the optimizer. Linear = proportional both ways; ReLU = " +
                 "only the bad direction is penalized (good direction → 0); Tanh " +
                 "= smooth saturating. Shared by ALL channels.")]
        public ActivationFunction activation = ActivationFunction.Linear;
        [Tooltip("Bound half-width in SDs: each channel's bounds are " +
                 "baseline ± k·SD, and a window's deviation reaches ±1 there. " +
                 "3 ≈ covers 99.7% of the population per the literature SD.")]
        [Min(0.1f)]
        public float boundK = 3f;
        [Tooltip("Per-channel literature SD + bad-direction. Auto-populated from " +
                 "the plugged-in DelphiManager's enabled channels; edit the SDs " +
                 "to your sourced numbers.")]
        public List<ChannelNormalization> channelConfigs = new();

        [Header("Trial structure")]
        [Tooltip("Stationary baseline before the drive, seconds.")]
        [Min(10f)]
        public float baselineSeconds = 120f;
        [Tooltip("Only this many seconds at the END of the baseline are averaged " +
                 "into the reference means. 30 s per the '30 s everywhere' decision.")]
        [Min(1f)]
        public float baselineAveragingSeconds = 30f;
        [Tooltip("Total number of parameter sets the optimizer gets to try " +
                 "(= BO iterations). Total trial time is shown below.")]
        [Min(2)]
        public int iterations = 56;
        [Tooltip("How many of those iterations are quasi-random (Sobol) " +
                 "exploration before model-guided optimization starts. Rule of " +
                 "thumb: ~2 per parameter dimension.")]
        [Min(1)]
        public int samplingIterations = 12;

        [Header("Parameter transition")]
        [Tooltip("When the optimizer hands over a new parameter set, ramp " +
                 "LINEARLY from the current values to the new ones over this many " +
                 "seconds instead of snapping — an instant jolt in driving style " +
                 "is itself a startle stimulus that would contaminate the very " +
                 "physiology we're measuring. Clamped to the window strategy's " +
                 "washout so measurement never starts mid-ramp (a longer setting " +
                 "is silently shortened, with a console warning).")]
        [Min(0f)]
        public float transitionSeconds = 3f;

        [Header("Optimizer")]
        [Tooltip("Empty = auto: the project-local venv at BOPythonEnv/bin/python3.")]
        public string pythonPath = "";
        public int seed = 3;

        [Header("Study identifiers (written into the optimizer's logs)")]
        public string userId = "P0";
        public string conditionId = "pilot";
        public string groupId = "0";

        // ── Runtime state (read by TrialControlsUI / editor) ────────────
        /// <summary>Where the python optimizer process/socket stands — for the
        /// dashboard's connection indicator.</summary>
        public enum OptimizerStatus { NotStarted, Starting, Connected, Disconnected }

        public TrialState State { get; private set; } = TrialState.Idle;
        public int Iteration { get; private set; }
        public int TotalIterations => iterations;
        public float LastCoverage { get; private set; } = float.NaN;
        public string StatusLine { get; private set; } = "";

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
        public double PhaseSecondsRemaining =>
            _phaseEnd > 0 ? Math.Max(0, _phaseEnd - DelphiClock.Now) : 0;

        private BoBridge _bo;
        private bool _trialActuallyStarted; // false until past the "can this even begin" checks
        private double _trialStart;     // DelphiClock zero for the trial
        private double _driveStart;     // DelphiClock time the baseline ended / iterations began
        private double _phaseEnd;       // DelphiClock time the current phase ends
        private WindowAccumulator _acc; // active baseline/measure accumulator
        private double _measureStart;
        // Per-channel baseline MEAN only — the spread that defines the bounds is
        // the literature SD from channelConfigs, NOT measured here.
        private readonly Dictionary<Channel, float> _baseline = new();
        private List<Channel> _objectiveChannels = new();
        private Dictionary<string, float> _lastParams = new();
        private StreamWriter _trialLog;

        // Parameter ramp — see ApplyParameters/TickTransition. _transTo is null
        // when no ramp is in flight (steady state between iterations).
        private Dictionary<string, float> _transFrom;
        private Dictionary<string, float> _transTo;
        private double _transStart;
        private float _transDuration;

        // All six axes stay in the code; a param DISABLED on CarDriver is excluded
        // from the search space at SendInit (see IsParamOn) — which is how the v1
        // "4 parameters" decision is expressed: untick takeoverProbability and
        // speedBelowLimit on the CarDriver, no code change needed.
        private static readonly string[] ParameterKeys =
        {
            "accelerationJerk", "brakingJerk", "followDistance",
            "corneringSpeed", "takeoverProbability", "speedBelowLimit"
        };

        private void Awake()
        {
            if (manager == null)        manager        = FindFirstObjectByType<DelphiManager>();
            if (carDriver == null)      carDriver      = FindFirstObjectByType<CarDriver>();
            if (recorder == null)       recorder       = FindFirstObjectByType<SessionRecorder>();
            if (windowStrategy == null) windowStrategy = FindFirstObjectByType<TrialWindowStrategy>();
        }

        // ── Public control (dashboard button) ───────────────────────────
        public bool StartTrial()
        {
            if (State != TrialState.Idle && State != TrialState.Finished && State != TrialState.Error)
                return false;

            // Reset before the early-return checks below — otherwise a failed
            // attempt after a previously SUCCESSFUL trial would inherit that
            // trial's stale _trialStart/_driveStart and write a nonsense
            // trial_meta.json.
            _trialActuallyStarted = false;

            if (manager == null || manager.Core == null) return Fail("No DelphiManager/core running.");
            if (carDriver == null)                       return Fail("No CarDriver in the scene.");
            if (windowStrategy == null)                  return Fail("No TrialWindowStrategy in the scene — add a FixedTrialWindow.");
            if (CandidateChannels().Count < 2)
                return Fail("mobo.py needs ≥2 objectives — attach and enable at least two scalar sensors on DelphiManager.");

            // Recording runs for the whole trial; csv + videos + trial log all
            // land in one session folder.
            if (recorder != null && !recorder.IsRecording)
                recorder.StartRecording($"trial_{userId}_{conditionId}");

            // Launch python now so torch imports while the baseline runs.
            _bo = new BoBridge();
            try
            {
                _bo.StartProcess(pythonPath,
                    Path.Combine(Application.streamingAssetsPath, "BOData", "BayesianOptimization"),
                    "mobo.py");
            }
            catch (Exception e)
            {
                _bo.Dispose(); _bo = null;
                return Fail(e.Message);
            }

            _trialStart = DelphiClock.Now;
            _trialActuallyStarted = true;
            _phaseEnd = _trialStart + baselineSeconds;
            Iteration = 0;
            LastCoverage = float.NaN;
            _baseline.Clear();
            _acc = null;
            _transTo = null;
            State = TrialState.Baseline;
            StatusLine = "Baseline — participant sits still";
            Debug.Log($"[Trial] Started: baseline {baselineSeconds:0}s (avg last {baselineAveragingSeconds:0}s), " +
                      $"then {iterations} × {windowStrategy.WindowSeconds:0}s windows. " +
                      $"Activation={activation}, k={boundK}.");
            return true;
        }

        public void AbortTrial()
        {
            if (State == TrialState.Idle) return;
            Cleanup("Aborted by user");
            State = TrialState.Idle;
            StatusLine = "Aborted";
            Debug.Log("[Trial] Aborted.");
        }

        // ── State machine ───────────────────────────────────────────────
        private void Update()
        {
            // Independent of State — a ramp started as we entered Washout must
            // keep advancing every frame until it completes.
            TickTransition();

            switch (State)
            {
                case TrialState.Baseline:            TickBaseline(); break;
                case TrialState.WaitingForOptimizer: TickWaitingForOptimizer(); break;
                case TrialState.WaitingForParameters:
                case TrialState.Washout:
                case TrialState.Measuring:           TickIterationLoop(); break;
            }

            // A dead optimizer process mid-trial is fatal.
            if (State != TrialState.Idle && State != TrialState.Finished &&
                State != TrialState.Error && _bo != null && !_bo.ProcessAlive)
            {
                Fail("Optimizer process exited unexpectedly — see [BO] console output.");
            }
        }

        private void TickBaseline()
        {
            _bo.TryConnect(); // non-blocking; python needs a few seconds to boot

            // Install the accumulator only for the trailing averaging span.
            double avgStart = _phaseEnd - baselineAveragingSeconds;
            if (_acc == null && DelphiClock.Now >= avgStart)
            {
                _acc = new WindowAccumulator();
                manager.Core.Accumulator = _acc;
            }

            if (DelphiClock.Now < _phaseEnd) return;

            // Baseline over — snapshot the per-channel reference MEAN. The bounds
            // come from baseline ± k·(literature SD), NOT from anything measured
            // in this window.
            manager.Core.Accumulator = null;
            _objectiveChannels = new List<Channel>();
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
                _objectiveChannels.Add(ch);
                var cfg = EffectiveConfig(ch);
                var (lo, hi) = ChannelMath.Bounds(mean, cfg.sd, boundK);
                Debug.Log($"[Trial] Baseline {ch}: mean {mean:F2} ({count} samples) → bounds [{lo:F2}, {hi:F2}] " +
                          $"(SD {cfg.sd}, {(cfg.higherIsBetter ? "higher is better" : "higher is worse")})");
            }
            _acc = null;

            if (_objectiveChannels.Count < 2)
            {
                Fail("Fewer than 2 channels delivered baseline data — cannot run MOBO.");
                return;
            }

            _driveStart = DelphiClock.Now; // iteration clock starts now, for averageIterationSeconds
            State = TrialState.WaitingForOptimizer;
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

            if (!SendInit()) return; // Fail() already set State = Error
            OpenTrialLog();
            State = TrialState.WaitingForParameters;
            _phaseEnd = 0;
            StatusLine = "Waiting for first parameter set";
        }

        private void TickIterationLoop()
        {
            DrainMessages();

            if (State == TrialState.Washout && DelphiClock.Now >= _phaseEnd)
            {
                _acc = new WindowAccumulator();
                manager.Core.Accumulator = _acc;
                _measureStart = DelphiClock.Now;
                _phaseEnd = _measureStart + windowStrategy.MeasureSeconds;
                State = TrialState.Measuring;
                StatusLine = $"Iteration {Iteration}/{iterations} — measuring";
            }
            else if (State == TrialState.Measuring && DelphiClock.Now >= _phaseEnd)
            {
                manager.Core.Accumulator = null;
                SubmitObjectives();
                _acc = null;
                State = TrialState.WaitingForParameters;
                _phaseEnd = 0;
                StatusLine = $"Iteration {Iteration}/{iterations} — submitted, waiting";
            }
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
                        // Only accept while idle-waiting; a suggestion can never
                        // arrive mid-window because we haven't submitted yet.
                        ApplyParameters((JObject)msg["values"]);
                        Iteration++;
                        State = TrialState.Washout;
                        _phaseEnd = DelphiClock.Now + windowStrategy.WashoutSeconds;
                        StatusLine = $"Iteration {Iteration}/{iterations} — washout";
                        break;

                    case "coverage":
                    case "tempCoverage":
                        LastCoverage = (float)msg["value"];
                        break;

                    case "optimization_finished":
                        Debug.Log("[Trial] Optimization finished.");
                        Cleanup("Finished");
                        State = TrialState.Finished;
                        StatusLine = $"Finished — {Iteration} iterations, coverage {LastCoverage:F3}";
                        return; // bridge is gone; stop draining
                }
            }
        }

        // Mirrors what CarDriver's OWN driving logic actually checks (see
        // CarDriver.cs — accelerationJerkOn/brakingJerkOn/speedBelowLimitOn gate
        // their DrivingParameters property; followDistanceOn/corneringSpeedOn are
        // checked at the call site instead — either way, off means the axis
        // provably has zero effect on the car). A disabled axis is EXCLUDED from
        // the search space entirely, not sent as a dimension the optimizer wastes
        // budget exploring for no signal — it just sits at its current value.
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

        private List<string> _activeParamKeys = new();

        // ── Optimizer messages ──────────────────────────────────────────
        /// <summary>Returns false (and calls Fail) if there's nothing left to
        /// search over — mobo.py requires nParameters >= 1.</summary>
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

            // Objective bounds are in ACTIVATION-OUTPUT units, and the optimizer
            // always MINIMIZES (the per-channel "higher is better" flip already
            // put the bad direction on the positive side). ReLU collapses the good
            // direction to 0 → range [0,1]; Linear/Tanh keep both → [-1,1].
            var (lo, hi) = ChannelMath.ObjectiveRange(activation);
            var objectives = new JArray();
            foreach (var ch in _objectiveChannels)
            {
                string objInit = string.Format(CultureInfo.InvariantCulture, "{0},{1},{2}", lo, hi, 1);
                objectives.Add(new JObject { ["key"] = ch.ToString(), ["init"] = objInit });
            }

            int sampling = Mathf.Clamp(samplingIterations, 1, iterations - 1);
            var init = new JObject
            {
                ["type"] = "init",
                ["config"] = new JObject
                {
                    ["numSamplingIterations"] = sampling,
                    ["numOptimizationIterations"] = iterations - sampling,
                    ["batchSize"] = 1, ["numRestarts"] = 10,
                    ["rawSamples"] = 512, ["mcSamples"] = 256,
                    ["seed"] = seed,
                    ["nParameters"] = _activeParamKeys.Count,
                    ["nObjectives"] = _objectiveChannels.Count,
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
                      $", {_objectiveChannels.Count} objectives ({string.Join(", ", _objectiveChannels)}), " +
                      $"activation {activation} range [{lo},{hi}] minimize.");
            return true;
        }

        /// <summary>Starts a ramp toward the optimizer's new parameter set rather
        /// than snapping to it — TickTransition advances it every frame
        /// thereafter. Snapping would itself be a sudden jolt in driving
        /// behaviour, i.e. a startle stimulus contaminating the very physiology
        /// the next window measures.</summary>
        private void ApplyParameters(JObject values)
        {
            var p = carDriver.parameters;
            _transFrom = new Dictionary<string, float>();
            _transTo = new Dictionary<string, float>();
            foreach (var key in ParameterKeys)
            {
                float current = GetParam(p, key);
                _transFrom[key] = current;
                _transTo[key] = Get(values, key, current);
            }
            _transStart = DelphiClock.Now;

            float washout = windowStrategy != null ? windowStrategy.WashoutSeconds : 0f;
            _transDuration = Mathf.Clamp(transitionSeconds, 0f, washout);
            if (transitionSeconds > washout)
                Debug.LogWarning($"[Trial] transitionSeconds ({transitionSeconds:0.#}s) exceeds the washout " +
                                 $"({washout:0.#}s) — clamped to {_transDuration:0.#}s so measurement never " +
                                 "starts mid-ramp.");

            _lastParams = new Dictionary<string, float>();
            foreach (var prop in values.Properties()) _lastParams[prop.Name] = (float)prop.Value;

            Debug.Log($"[Trial] Applied parameter set #{Iteration + 1} (ramping over {_transDuration:0.#}s): " +
                      $"{values.ToString(Newtonsoft.Json.Formatting.None)}");
        }

        /// <summary>Advances the in-flight ramp (if any) by linearly interpolating
        /// every driving-parameter axis from its pre-suggestion value to the
        /// optimizer's target. A no-op once the ramp completes (_transTo goes
        /// null) until the next ApplyParameters call.</summary>
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
            var objectiveValues = new Dictionary<string, float>();  // sent to the optimizer (activation output)
            var deviationsForLog = new Dictionary<string, float>(); // signed d, for the console line
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
                    // No data this window (sensor dropout): submit the
                    // baseline-neutral objective rather than crashing the
                    // optimizer, but say so loudly.
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

            Debug.Log($"[Trial] Iteration {Iteration}/{iterations} result — " +
                      $"params: {{{FormatDict(_lastParams)}}} | " +
                      $"deviations d (vs. baseline, in bound units): {{{FormatDict(deviationsForLog)}}} | " +
                      $"objectives sent (minimize): {{{FormatDict(objectiveValues)}}}");

            _bo.SendObjectives(objectiveValues);
            WriteTrialLogRow(logCells);
        }

        // ── Per-channel normalization config ────────────────────────────
        /// <summary>The config the researcher edited for this channel, or null if
        /// none exists yet.</summary>
        public ChannelNormalization ConfigFor(Channel ch)
        {
            foreach (var c in channelConfigs)
                if (c != null && c.channel == ch) return c;
            return null;
        }

        /// <summary>Never-null config: falls back to literature-placeholder
        /// defaults so a missing row can't crash a running trial.</summary>
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

        /// <summary>Native-unit bounds baseline ± k·SD once the baseline has been
        /// captured — for the dashboard's guideline bands / clip flag. False
        /// before the baseline exists for this channel.</summary>
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
        // trial_meta.json — the summary a human (or a script auditing many
        // sessions) reads first: how far the trial got, how fast, on what sensors,
        // how each channel was normalized, and what the driving axes physically
        // meant.
        private void WriteTrialMeta(string endReason, string sessionPath)
        {
            try
            {
                int sampling = Mathf.Clamp(samplingIterations, 1, Math.Max(1, iterations - 1));
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

                    baselineSeconds = baselineSeconds,
                    baselineAveragingSeconds = baselineAveragingSeconds,
                    windowSeconds = windowStrategy != null ? windowStrategy.WindowSeconds : 0f,
                    washoutSeconds = windowStrategy != null ? windowStrategy.WashoutSeconds : 0f,
                    measureSeconds = windowStrategy != null ? windowStrategy.MeasureSeconds : 0f,
                    transitionSeconds = transitionSeconds,

                    activation = activation.ToString(),
                    objectiveRangeLo = objLo,
                    objectiveRangeHi = objHi,

                    iterationsPlanned = iterations,
                    iterationsCompleted = Iteration,
                    samplingIterationsPlanned = sampling,
                    optimizationIterationsPlanned = Math.Max(0, iterations - sampling),
                    averageIterationSeconds = avgIterationSeconds,

                    finalHypervolumeCoverage = LastCoverage,

                    optimizerSeed = seed,
                    pythonPathUsed = string.IsNullOrWhiteSpace(pythonPath)
                        ? Path.GetFullPath(Path.Combine(Application.dataPath, "..", "BOPythonEnv", "bin", "python3"))
                        : pythonPath,

                    sessionRecordingPath = sessionPath ?? "",

                    goldStandardRateHz = manager != null ? manager.goldStandardRateHz : 0f,
                    goodAdditionsRateHz = manager != null ? manager.goodAdditionsRateHz : 0f,
                    experimentalRateHz = manager != null ? manager.experimentalRateHz : 0f,

                    objectives = objectives.ToArray(),
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
                // Never let meta-writing failure mask the trial's actual outcome.
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
        /// <summary>Channels eligible as objectives: attached AND enabled on
        /// DelphiManager (whether they also deliver data is settled at the end of
        /// the baseline).</summary>
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

        private bool Fail(string why)
        {
            Debug.LogError($"[Trial] {why}");
            Cleanup($"Error: {why}");
            State = TrialState.Error;
            StatusLine = why;
            return false;
        }

        private void Cleanup(string endReason = "Interrupted")
        {
            if (manager != null && manager.Core != null) manager.Core.Accumulator = null;
            _acc = null;
            _transTo = null; // don't let a ramp outlive the trial that started it
            _bo?.Dispose();
            _bo = null;
            _trialLog?.Close();
            _trialLog = null;

            // Capture the session path BEFORE stopping the recorder — once
            // stopped, CurrentSessionPath goes back to null.
            string sessionPath = recorder != null ? recorder.CurrentSessionPath : null;
            if (recorder != null && recorder.IsRecording) recorder.StopRecording();

            if (_trialActuallyStarted) WriteTrialMeta(endReason, sessionPath);
            _trialActuallyStarted = false;
        }

        private void OnDestroy() => Cleanup();
        private void OnApplicationQuit() => Cleanup();
    }
}
