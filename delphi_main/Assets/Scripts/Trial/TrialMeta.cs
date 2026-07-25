using System;

namespace Delphi.Trial
{
    /// <summary>
    /// trial_meta.json — written once per trial (finished, aborted, or failed)
    /// next to trial_log.csv. Answers, without re-deriving anything from the raw
    /// logs: how many iterations actually ran, how long each took on average,
    /// which sensors fed the optimizer, how each channel was normalized into its
    /// objective, what the driving-parameter axes physically mean, and how the
    /// trial ended.
    /// </summary>
    [Serializable]
    public class TrialMeta
    {
        public string userId, conditionId, groupId;
        public string startedIso;      // wall-clock start, ISO-8601
        public string endReason;       // "Finished", "Aborted by user", "Error: ...", etc.
        public float totalDurationSeconds;

        public float baselineSeconds;
        public float baselineAveragingSeconds;
        public float windowSeconds;
        public float washoutSeconds;
        public float measureSeconds;
        /// <summary>Configured ramp time toward each new parameter set; the
        /// ACTUAL ramp used was min(this, washoutSeconds) — see SessionController.</summary>
        public float transitionSeconds;

        /// <summary>Shared activation shaping every channel's deviation into the
        /// minimized objective: "Linear", "ReLU" or "Tanh".</summary>
        public string activation;
        /// <summary>The objective value range implied by the activation
        /// (ReLU → [0,1], Linear/Tanh → [-1,1]); the optimizer minimizes.</summary>
        public float objectiveRangeLo, objectiveRangeHi;

        public int iterationsPlanned;
        public int iterationsCompleted;
        public int samplingIterationsPlanned;
        public int optimizationIterationsPlanned;
        /// <summary>Wall-clock seconds per iteration, averaged over the drive
        /// phase actually completed (baseline excluded).</summary>
        public float averageIterationSeconds;

        public float finalHypervolumeCoverage;

        public int optimizerSeed;
        public string pythonPathUsed;

        /// <summary>Session folder holding sensors.csv/videos for this trial, if
        /// recording was running — empty otherwise.</summary>
        public string sessionRecordingPath;

        /// <summary>DelphiManager's scalar-group sample rates at trial start —
        /// the acquisition rate behind every objective below.</summary>
        public float contactRateHz, gazeRateHz;

        /// <summary>Sensor-shaped objectives (Physiology trials) — empty for
        /// Questionnaire trials, which use questionnaireObjectiveKeys below
        /// instead. Neither concept fits the other cleanly enough to share
        /// one array.</summary>
        public TrialObjectiveMeta[] objectives;
        /// <summary>Which objectiveSource this trial ran with — "Physiology"
        /// or "Questionnaire".</summary>
        public string objectiveSource;
        /// <summary>Questionnaire trials only: the header names (from the
        /// linked QTQuestionnaireManager) that were sent to the optimizer as
        /// objectives, in order — empty for Physiology trials.</summary>
        public string[] questionnaireObjectiveKeys;
        public TrialParameterRangeMeta[] parameterRanges;
    }

    [Serializable]
    public class TrialObjectiveMeta
    {
        public string channel;
        public string sensorType;       // e.g. "MockSensor_Scalar", "GSRSensorSerial"
        public string sensorObjectName; // GameObject the sensor is on
        public float baselineMean;      // reference mean over the baseline averaging window
        public float literatureSd;      // native-unit SD the researcher supplied (NOT measured from baseline)
        public float boundK;            // bound half-width in SDs: bounds = baselineMean ± boundK·literatureSd
        public float lowerBound, upperBound; // native-unit; a window mean here maps to deviation ∓1
        /// <summary>false (default) = RISING above baseline is the penalized
        /// direction (HR, GSR, arousal). true = DROPPING below baseline is
        /// penalized (e.g. RMSSD — suppressed HRV means stress).</summary>
        public bool higherIsBetter;
    }

    [Serializable]
    public class TrialParameterRangeMeta
    {
        public string key;             // matches the 0..1 axis name sent to the optimizer
        public float physicalMin, physicalMax; // native units that 0 and 1 map to
        public string unit;
        /// <summary>False = excluded from the optimizer's search space this trial
        /// (disabled on CarDriver — provably no effect on the car), held fixed at
        /// its current value instead.</summary>
        public bool active;
    }
}
