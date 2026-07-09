using UnityEngine;

namespace Delphi.Trial
{
    /// <summary>
    /// The simple version: every iteration is a fixed wall-clock window.
    /// Apply parameters, optionally discard a short washout, measure the
    /// rest, submit. No track awareness, no adaptivity.
    /// </summary>
    public class FixedTrialWindow : TrialWindowStrategy
    {
        [Tooltip("Seconds per iteration — one parameter set is active for " +
                 "exactly this long.")]
        [Min(1f)]
        public float windowSeconds = 30f;

        [Tooltip("Seconds discarded at the start of each window before " +
                 "measurement begins (GSR lags 1–4 s, HR 5–10 s, and the car " +
                 "needs a moment to express the new parameters). Set 0 to " +
                 "measure the full window.")]
        [Min(0f)]
        public float washoutSeconds = 5f;

        public override float WindowSeconds  => windowSeconds;
        public override float WashoutSeconds => Mathf.Min(washoutSeconds, windowSeconds);
    }
}
