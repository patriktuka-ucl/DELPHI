using UnityEngine;

namespace Delphi.Trial
{
    /// <summary>
    /// Decides how each trial iteration's time is spent. TrialManager asks
    /// the strategy for the iteration length and how much of its start to
    /// discard before measuring. Swap implementations to change the trial's
    /// windowing logic without touching the orchestration — the planned
    /// fancier variants (event-aligned windows, contextual/cBO scheduling)
    /// subclass this. See FixedTrialWindow.cs for the simple version.
    /// </summary>
    public abstract class TrialWindowStrategy : MonoBehaviour
    {
        /// <summary>Total seconds one iteration occupies (one parameter set).</summary>
        public abstract float WindowSeconds { get; }

        /// <summary>Seconds at the start of the window to discard before
        /// measurement begins (physiological lag + the car needing a moment
        /// to express the new parameters). 0 = measure the whole window.</summary>
        public abstract float WashoutSeconds { get; }

        public float MeasureSeconds => Mathf.Max(0f, WindowSeconds - WashoutSeconds);
    }
}
