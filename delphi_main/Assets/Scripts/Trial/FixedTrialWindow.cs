using UnityEngine;

namespace Delphi.Trial
{
    /// <summary>
    /// The simple version: every iteration is a fixed wall-clock window. Ramp
    /// the parameters in, discard the washout, measure the rest, submit. No
    /// track awareness, no adaptivity.
    ///
    /// Defaults encode the "30 s everywhere" decision: the MEASURED span is 30 s
    /// (matching the baseline averaging window), with the washout on top — so
    /// windowSeconds = washout + 30.
    /// </summary>
    public class FixedTrialWindow : TrialWindowStrategy
    {
        [Tooltip("Seconds per iteration — one parameter set is active for exactly " +
                 "this long. This is washout + measured, so the default 40 s " +
                 "yields the agreed 30 s of measurement.")]
        [Min(1f)]
        public float windowSeconds = 40f;

        [Tooltip("Seconds discarded at the start of each window before measurement " +
                 "begins. Must cover BOTH the parameter ramp (TrialManager." +
                 "transitionSeconds — the car takes this long to actually reach the " +
                 "new style) AND the physiological lag behind it (GSR ≈ 1–4 s, " +
                 "HR ≈ 5–10 s). Too short and the window measures the tail of the " +
                 "PREVIOUS parameter set. Set 0 to measure the full window.")]
        [Min(0f)]
        public float washoutSeconds = 10f;

        public override float WindowSeconds  => windowSeconds;
        public override float WashoutSeconds => Mathf.Min(washoutSeconds, windowSeconds);
    }
}
