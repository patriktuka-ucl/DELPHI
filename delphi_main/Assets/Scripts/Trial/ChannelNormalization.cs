using System;
using UnityEngine;

namespace Delphi.Trial
{
    /// <summary>How a per-channel deviation from baseline is shaped into the
    /// value the optimizer minimizes. Chosen ONCE for the whole trial (all
    /// channels share it), on SessionController.</summary>
    public enum ActivationFunction
    {
        /// <summary>Proportional both ways: clamp(d, −1, +1). The good
        /// direction is rewarded, the bad direction penalized.</summary>
        Linear,
        /// <summary>One-sided: clamp(d, 0, +1). Deviation in the GOOD
        /// direction is neutral (0); only the bad direction is penalized.
        /// This is the "I only care if it exceeds baseline" behaviour.</summary>
        ReLU,
        /// <summary>Smooth saturating version of Linear: tanh(d). Never clamps
        /// hard; large deviations asymptote to ±1.</summary>
        Tanh
    }

    /// <summary>
    /// Per-channel facts the trial layer needs to turn a raw physiological
    /// mean into a BO objective. Edited in the SessionController inspector,
    /// one row per attached+enabled channel.
    ///
    /// NO z-scores anywhere: the bounds are baseline ± k·SD with a
    /// LITERATURE-supplied SD (NOT the noisy 30 s baseline's own spread), so
    /// the −1..1 mapping is stable and comparable regardless of how much this
    /// particular participant's signal happened to wander during baseline.
    /// </summary>
    [Serializable]
    public class ChannelNormalization
    {
        public Channel channel;

        [Tooltip("Native-unit standard deviation for THIS measure across the " +
                 "population, from literature (e.g. HR ≈ 10 bpm). The bound " +
                 "half-width is k × this. NOT measured from the baseline.")]
        [Min(1e-6f)]
        public float sd = 1f;

        [Tooltip("Unchecked (default): the signal RISING above baseline is the " +
                 "bad direction — HR, GSR, most arousal measures. Checked: the " +
                 "signal DROPPING below baseline is bad — e.g. RMSSD, where " +
                 "suppressed HRV means stress.")]
        public bool higherIsBetter = false;
    }

    /// <summary>
    /// The whole normalization math, kept pure and static so it's trivially
    /// testable and identical everywhere it's used (objective submission, the
    /// live UI bands, the trial log).
    /// </summary>
    public static class ChannelMath
    {
        /// <summary>Signed deviation in BOUND units: 0 at baseline, ±1 at the
        /// baseline ± k·SD bounds. |d| &gt; 1 means the value left the bound
        /// (see <see cref="IsClipped"/>). This is exactly a min-max rescale of
        /// [baseline−k·SD, baseline+k·SD] onto [−1, +1].</summary>
        public static float Deviation(float value, float baseline, float sd, float k)
        {
            float halfWidth = Mathf.Max(k * Mathf.Abs(sd), 1e-6f); // guards SD=0 / k=0
            return (value - baseline) / halfWidth;
        }

        public static bool IsClipped(float deviation) => Mathf.Abs(deviation) > 1f;

        /// <summary>Orient so the BAD direction is positive, then shape by the
        /// activation. The result is what the optimizer MINIMIZES (so a lower
        /// objective is always a better ride) — there is no per-channel
        /// maximize flag any more.</summary>
        public static float Objective(float deviation, bool higherIsBetter, ActivationFunction activation)
        {
            // higherIsBetter → dropping (negative deviation) is bad → flip so
            // "bad" is positive. Otherwise rising (positive) is already bad.
            float bad = higherIsBetter ? -deviation : deviation;
            return activation switch
            {
                ActivationFunction.Linear => Mathf.Clamp(bad, -1f, 1f),
                ActivationFunction.ReLU   => Mathf.Clamp(bad, 0f, 1f),
                ActivationFunction.Tanh   => (float)Math.Tanh(bad),
                _                         => Mathf.Clamp(bad, -1f, 1f)
            };
        }

        /// <summary>The objective's value range for a given activation — sent
        /// to mobo.py as the objective bounds. Minimize is always implied.</summary>
        public static (float lo, float hi) ObjectiveRange(ActivationFunction activation) => activation switch
        {
            ActivationFunction.ReLU => (0f, 1f),  // good direction collapses to 0
            _                       => (-1f, 1f)  // Linear clamps, Tanh asymptotes
        };

        /// <summary>Absolute native-unit bounds baseline ± k·SD — for drawing
        /// guideline bands and the clip indicator on the raw-value graphs.</summary>
        public static (float lower, float upper) Bounds(float baseline, float sd, float k)
        {
            float halfWidth = k * Mathf.Abs(sd);
            return (baseline - halfWidth, baseline + halfWidth);
        }

        // ── Literature-PLACEHOLDER defaults ─────────────────────────────
        // So the trial runs out of the box against mock sensors. REPLACE with
        // sourced numbers (see the literature-assumptions checklist) before any
        // real data collection — these are ballparks, not evidence.
        public static float DefaultSd(Channel ch) => ch switch
        {
            Channel.HeartRate     => 10f,   // bpm
            Channel.RMSSD         => 20f,   // ms
            Channel.RespRate      => 3f,    // breaths/min
            Channel.GSR           => 50f,   // raw 10-bit units (device-dependent!)
            Channel.BlinkRate     => 5f,    // blinks/min
            Channel.Gaze          => 0.3f,
            Channel.PupilDiameter => 0.5f,  // mm
            Channel.EEG           => 20f,   // µV
            Channel.Facial        => 0.3f,
            _                     => 1f
        };

        public static bool DefaultHigherIsBetter(Channel ch) => ch switch
        {
            // Suppressed HRV = stress → DROPPING below baseline is the bad
            // direction, so the deviation is sign-flipped before the activation.
            Channel.RMSSD => true,
            _             => false
        };
    }
}
