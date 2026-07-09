using System;

namespace Delphi.Trial
{
    /// <summary>
    /// trial_meta.json — written once per trial (finished, aborted, or
    /// failed) next to trial_log.csv. Answers, without re-deriving anything
    /// from the raw logs: how many iterations actually ran, how long each
    /// took on average, which sensors fed the optimizer and what their
    /// baselines were, what the six driving-parameter axes physically mean,
    /// and how the trial ended.
    /// </summary>
    [Serializable]
    public class TrialMeta
    {
        public string userId, conditionId, groupId;
        public string startedIso;      // wall-clock start, ISO-8601
        public string endReason;       // "Finished", "Aborted by user", "Error: ...", etc.
        public float totalDurationSeconds;

        public float baselineSeconds;
        public float baselineAveragingSecondsEffective;
        public float windowSeconds;
        public float washoutSeconds;
        public float measureSeconds;

        public int iterationsPlanned;
        public int iterationsCompleted;
        public int samplingIterationsPlanned;
        public int optimizationIterationsPlanned;
        /// <summary>Wall-clock seconds per iteration, averaged over the
        /// drive phase actually completed (baseline excluded).</summary>
        public float averageIterationSeconds;

        public float finalHypervolumeCoverage;

        public int optimizerSeed;
        public string pythonPathUsed;

        /// <summary>Session folder holding sensors.csv/videos for this
        /// trial, if recording was running — empty otherwise.</summary>
        public string sessionRecordingPath;

        /// <summary>DelphiManager's scalar-group sample rates at trial
        /// start — the acquisition rate behind every objective below.</summary>
        public float goldStandardRateHz, goodAdditionsRateHz, experimentalRateHz;

        public TrialObjectiveMeta[] objectives;
        public TrialParameterRangeMeta[] parameterRanges;
    }

    [Serializable]
    public class TrialObjectiveMeta
    {
        public string channel;
        public string sensorType;      // e.g. "MockSensor_Scalar", "GSRSensorSerial"
        public string sensorObjectName; // GameObject the sensor is on
        public float baselineMean;
        public float baselineStdDev;   // every window's delta is divided by this (z-score) before optimization
        public float nativeDeltaSafetyBound; // raw native-unit delta is clamped to [-bound,+bound] BEFORE z-scoring
        public float zScoreBound;      // the z-score itself is then clamped to this (symmetric or [0, bound] — see targetsBaseline)
        /// <summary>true = optimizer minimizes |z| (target: match baseline,
        /// deviating either direction is worse). false = optimizer maximizes
        /// raw z with no target — currently only RMSSD (suppressed HRV vs.
        /// baseline is bad; elevated HRV has no assumed ceiling).</summary>
        public bool targetsBaseline;
    }

    [Serializable]
    public class TrialParameterRangeMeta
    {
        public string key;             // matches the 0..1 axis name sent to the optimizer
        public float physicalMin, physicalMax; // native units that 0 and 1 map to
        public string unit;
        /// <summary>False = excluded from the optimizer's search space this
        /// trial (disabled on CarDriver — provably no effect on the car),
        /// held fixed at its current value instead.</summary>
        public bool active;
    }
}
