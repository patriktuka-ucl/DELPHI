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
    ///        → per iteration: apply BO-suggested driving parameters to the
    ///          ego car → washout → measure window (means via DelphiCore's
    ///          sampling thread) → submit baseline-corrected deltas to the
    ///          optimizer → repeat
    ///        → Finished (after the configured iteration budget).
    ///
    /// The optimizer is mobo.py (BoTorch qLogNEHVI) via BoBridge; objectives
    /// are DISCOVERED at runtime: every scalar channel that is attached and
    /// enabled on DelphiManager (and actually produced baseline data)
    /// becomes one objective — per Patrik's spec, the MOBO optimizes all
    /// attached measures. Window timing comes from the swappable
    /// TrialWindowStrategy. All timing is DelphiClock, all window means are
    /// computed on the core's sampling thread. The session recorder is
    /// started/stopped with the trial, and a trial_log.csv is written into
    /// the session folder.
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
                 "plain '30 s per parameter set' scheme; fancier strategies " +
                 "(event-aligned, contextual/cBO) plug in here later.")]
        public TrialWindowStrategy windowStrategy;

        [Header("Trial structure")]
        [Tooltip("Stationary baseline before the drive, seconds.")]
        [Min(10f)]
        public float baselineSeconds = 120f;
        [Tooltip("Only this many seconds at the END of the baseline are " +
                 "averaged into the reference values. Automatically raised " +
                 "to the largest per-channel minimum among attached sensors " +
                 "(see the timing panel below).")]
        [Min(5f)]
        public float baselineAveragingSeconds = 10f;
        [Tooltip("Total number of parameter sets the optimizer gets to try " +
                 "(= BO iterations). Total trial time is shown below.")]
        [Min(2)]
        public int iterations = 56;
        [Tooltip("How many of those iterations are quasi-random (Sobol) " +
                 "exploration before model-guided optimization starts. " +
                 "Rule of thumb: ~2 per parameter dimension.")]
        [Min(1)]
        public int samplingIterations = 12;

        [Header("Optimizer")]
        [Tooltip("Empty = auto: the project-local venv at BOPythonEnv/bin/python3.")]
        public string pythonPath = "";
        public int seed = 3;

        [Header("Study identifiers (written into the optimizer's logs)")]
        public string userId = "P0";
        public string conditionId = "pilot";
        public string groupId = "0";

        // ── Runtime state (read by TrialControlsUI / editor) ────────────
        /// <summary>Where the python optimizer process/socket stands — for
        /// the dashboard's connection indicator.</summary>
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

        public float EffectiveBaselineAveragingSeconds
        {
            get
            {
                float eff = baselineAveragingSeconds;
                foreach (var ch in CandidateChannels())
                    eff = Mathf.Max(eff, TrialObjectiveInfo.MinWindowSeconds(ch));
                return eff;
            }
        }

        private BoBridge _bo;
        private bool _trialActuallyStarted; // false until past the "can this even begin" checks
        private double _trialStart;     // DelphiClock zero for the trial
        private double _driveStart;     // DelphiClock time the baseline ended / iterations began
        private double _phaseEnd;       // DelphiClock time the current phase ends
        private WindowAccumulator _acc; // active baseline/measure accumulator
        private double _measureStart;
        private readonly Dictionary<Channel, float> _baseline = new();
        // Baseline standard deviation per channel — every window's delta is
        // divided by this (z-scored) before being sent to the optimizer, so
        // a participant whose signal barely moves gets the same effective
        // resolution as one who swings wildly, instead of a fixed generic
        // native-unit bound treating both the same.
        private readonly Dictionary<Channel, float> _baselineSd = new();
        private List<Channel> _objectiveChannels = new();
        private Dictionary<string, float> _lastParams = new();
        private StreamWriter _trialLog;

        // Objectives are sent to the optimizer as z-scores (delta / baseline
        // SD), bounded to ±ZMax — six SDs is generous enough that a real
        // clamp almost never triggers. Target semantics (confirmed 2026-07-
        // 09): arousal-type channels (HigherIsWorse) minimize |z| — optimum
        // is z≈0, i.e. MATCHING baseline, not drifting arbitrarily calmer.
        // RMSSD keeps its original flipped behaviour: maximize raw z (no
        // absolute value) — suppressed HRV vs. baseline is bad, elevated
        // HRV has no assumed ceiling, so there's no "target zero" for it.
        private const float ZMax = 6f;
        private const float ZEpsilon = 1e-3f; // SD floor — guards a dead-flat baseline (e.g. noiseless mock sensor)

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

            // Reset before the early-return checks below — otherwise a
            // failed attempt after a previously SUCCESSFUL trial would
            // inherit that trial's stale _trialStart/_driveStart and write
            // a nonsense trial_meta.json.
            _trialActuallyStarted = false;

            if (manager == null || manager.Core == null) return Fail("No DelphiManager/core running.");
            if (carDriver == null)                       return Fail("No CarDriver in the scene.");
            if (windowStrategy == null)                  return Fail("No TrialWindowStrategy in the scene — add a FixedTrialWindow.");
            if (CandidateChannels().Count < 2)
                return Fail("mobo.py needs ≥2 objectives — attach and enable at least two scalar sensors on DelphiManager.");

            // Recording runs for the whole trial; csv + videos + trial log
            // all land in one session folder.
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
            _baselineSd.Clear();
            _acc = null;
            State = TrialState.Baseline;
            StatusLine = "Baseline — participant sits still";
            Debug.Log($"[Trial] Started: baseline {baselineSeconds:0}s, then {iterations} × " +
                      $"{windowStrategy.WindowSeconds:0}s windows.");
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
            double avgStart = _phaseEnd - EffectiveBaselineAveragingSeconds;
            if (_acc == null && DelphiClock.Now >= avgStart)
            {
                _acc = new WindowAccumulator();
                manager.Core.Accumulator = _acc;
            }

            if (DelphiClock.Now < _phaseEnd) return;

            // Baseline over — snapshot reference mean AND spread (SD), the
            // latter being what every later window's delta gets divided by.
            manager.Core.Accumulator = null;
            _objectiveChannels = new List<Channel>();
            foreach (var ch in CandidateChannels())
            {
                var (mean, count) = _acc.Mean(ch);
                if (count == 0)
                {
                    Debug.LogWarning($"[Trial] {ch} produced no baseline samples — excluded from objectives.");
                    continue;
                }
                float sd = _acc.StdDev(ch);
                if (float.IsNaN(sd)) sd = 0f; // exactly 1 sample — treat as zero spread, ZEpsilon floors it later
                _baseline[ch] = mean;
                _baselineSd[ch] = sd;
                _objectiveChannels.Add(ch);
                Debug.Log($"[Trial] Baseline {ch}: mean {mean:F2}, SD {sd:F2} ({count} samples)");
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
        // CarDriver.cs — accelerationJerkOn/brakingJerkOn/speedBelowLimitOn
        // gate their DrivingParameters property; followDistanceOn/
        // corneringSpeedOn are checked at the call site instead — either
        // way, off means the axis provably has zero effect on the car).
        // A disabled axis is EXCLUDED from the search space entirely, not
        // sent as a dimension the optimizer wastes budget exploring for no
        // signal — it just sits at whatever value it already has.
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
        /// <summary>Returns false (and calls Fail) if there's nothing left
        /// to search over — mobo.py requires nParameters >= 1.</summary>
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

            // Bounds are in Z-SCORE units, not native units — see the ZMax
            // doc comment. Arousal-type channels target baseline (minimize
            // |z| over [0,ZMax]); RMSSD keeps maximizing raw z over
            // [-ZMax,ZMax] (elevated HRV vs. baseline has no assumed ceiling).
            var objectives = new JArray();
            foreach (var ch in _objectiveChannels)
            {
                bool targetBaseline = TrialObjectiveInfo.HigherIsWorse(ch);
                string objInit = targetBaseline
                    ? string.Format(CultureInfo.InvariantCulture, "{0},{1},{2}", 0, ZMax, 1)
                    : string.Format(CultureInfo.InvariantCulture, "{0},{1},{2}", -ZMax, ZMax, 0);
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
                      $", {_objectiveChannels.Count} objectives ({string.Join(", ", _objectiveChannels)}).");
            return true;
        }

        private void ApplyParameters(JObject values)
        {
            var p = carDriver.parameters;
            p.accelerationJerk    = Get(values, "accelerationJerk",    p.accelerationJerk);
            p.brakingJerk         = Get(values, "brakingJerk",         p.brakingJerk);
            p.followDistance      = Get(values, "followDistance",      p.followDistance);
            p.corneringSpeed      = Get(values, "corneringSpeed",      p.corneringSpeed);
            p.takeoverProbability = Get(values, "takeoverProbability", p.takeoverProbability);
            p.speedBelowLimit     = Get(values, "speedBelowLimit",     p.speedBelowLimit);

            _lastParams = new Dictionary<string, float>();
            foreach (var prop in values.Properties()) _lastParams[prop.Name] = (float)prop.Value;

            Debug.Log($"[Trial] Applied parameter set #{Iteration + 1}: {values.ToString(Newtonsoft.Json.Formatting.None)}");
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
            var objectiveValues = new Dictionary<string, float>(); // what's actually sent to the optimizer (z-scores)
            var deltasForLog = new Dictionary<string, float>();    // native-unit deltas, for the console line
            var logCells = new List<string>();
            foreach (var ch in _objectiveChannels)
            {
                var (mean, count) = _acc.Mean(ch);
                float delta;
                if (count == 0)
                {
                    // No data this window (sensor dropout): report "no change"
                    // rather than crashing the optimizer, but say so loudly.
                    Debug.LogWarning($"[Trial] {ch} delivered no samples in window {Iteration} — submitting z 0.");
                    delta = 0f;
                    mean = _baseline[ch];
                }
                else
                {
                    delta = mean - _baseline[ch];
                }
                // Safety clamp on the RAW native-unit delta first (guards a
                // corrupted single-window mean), THEN z-score by dividing by
                // this channel's baseline SD (ZEpsilon floors a dead-flat
                // baseline so a near-zero SD never explodes the ratio).
                float nativeCap = TrialObjectiveInfo.DeltaRange(ch);
                delta = Mathf.Clamp(delta, -nativeCap, nativeCap);
                float sd = Mathf.Max(_baselineSd[ch], ZEpsilon);
                float z = delta / sd;

                bool targetBaseline = TrialObjectiveInfo.HigherIsWorse(ch);
                float objectiveValue = targetBaseline
                    ? Mathf.Clamp(Mathf.Abs(z), 0f, ZMax)   // minimize distance FROM baseline
                    : Mathf.Clamp(z, -ZMax, ZMax);          // maximize raw z (RMSSD: higher than baseline is fine)

                objectiveValues[ch.ToString()] = objectiveValue;
                deltasForLog[ch.ToString()] = delta;
                logCells.Add(F(_baseline[ch])); logCells.Add(F(mean)); logCells.Add(F(delta));
                logCells.Add(F(sd)); logCells.Add(F(z));
            }

            Debug.Log($"[Trial] Iteration {Iteration}/{iterations} result — " +
                      $"params: {{{FormatDict(_lastParams)}}} | " +
                      $"native deltas (vs. baseline): {{{FormatDict(deltasForLog)}}} | " +
                      $"z-scored objectives sent: {{{FormatDict(objectiveValues)}}}");

            _bo.SendObjectives(objectiveValues);
            WriteTrialLogRow(logCells);
        }

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
                // delta = native-unit deviation from baseline (clamped, pre-z);
                // z = delta/baselineSd — the actual value sent to the optimizer
                // is |z| for target-baseline channels, raw z for RMSSD-style ones.
                header.Append($",{ch}_baseline,{ch}_mean,{ch}_delta,{ch}_baselineSd,{ch}_z");
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
        // sessions) reads first: how far the trial got, how fast, on what
        // sensors, and what the six 0..1 axes physically meant.
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
                    objectives.Add(new TrialObjectiveMeta
                    {
                        channel = ch.ToString(),
                        sensorType = sensor != null ? sensor.GetType().Name : "(none)",
                        sensorObjectName = sensor != null ? sensor.gameObject.name : "(none)",
                        baselineMean = _baseline.TryGetValue(ch, out var b) ? b : float.NaN,
                        baselineStdDev = _baselineSd.TryGetValue(ch, out var sd) ? sd : float.NaN,
                        nativeDeltaSafetyBound = TrialObjectiveInfo.DeltaRange(ch),
                        zScoreBound = ZMax,
                        targetsBaseline = TrialObjectiveInfo.HigherIsWorse(ch)
                    });
                }

                var meta = new TrialMeta
                {
                    userId = userId, conditionId = conditionId, groupId = groupId,
                    startedIso = DateTime.Now.AddSeconds(-(DelphiClock.Now - _trialStart)).ToString("o"),
                    endReason = endReason,
                    totalDurationSeconds = (float)(DelphiClock.Now - _trialStart),

                    baselineSeconds = baselineSeconds,
                    baselineAveragingSecondsEffective = EffectiveBaselineAveragingSeconds,
                    windowSeconds = windowStrategy != null ? windowStrategy.WindowSeconds : 0f,
                    washoutSeconds = windowStrategy != null ? windowStrategy.WashoutSeconds : 0f,
                    measureSeconds = windowStrategy != null ? windowStrategy.MeasureSeconds : 0f,

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
                new TrialParameterRangeMeta { key = "corneringSpeed", unit = "fraction of comfortable lateral accel",
                    physicalMin = p.cornerMin, physicalMax = p.cornerMax },
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
        /// DelphiManager (whether they also deliver data is settled at the
        /// end of the baseline).</summary>
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
