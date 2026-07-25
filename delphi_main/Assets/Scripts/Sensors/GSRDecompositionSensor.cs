using UnityEngine;

namespace Delphi
{
    /// <summary>
    /// Shared base for the two live-derived GSR sub-signals. Mirrors the
    /// STRUCTURE of NeuroKit2's default EDA pipeline (nk.eda_process):
    ///
    ///   1. CLEAN — low-pass the raw signal at ~3 Hz to strip measurement
    ///      noise (NeuroKit's eda_clean(method="neurokit") = 3 Hz Butterworth).
    ///   2. DECOMPOSE — split the cleaned signal at ~0.05 Hz
    ///      (NeuroKit's eda_phasic(method="highpass")):
    ///        • TONIC  (SCL) = the < 0.05 Hz low-pass  → GSRTonicSensor
    ///        • PHASIC (SCR) = cleaned − tonic         → GSRPhasicSensor
    ///
    /// The cutoff frequencies match NeuroKit's defaults, so the bands are the
    /// same. What CANNOT match, by nature, is the filter itself: NeuroKit runs
    /// OFFLINE, zero-phase, high-order Butterworth (scipy filtfilt, which uses
    /// future samples). This runs LIVE on the sampling thread, so it uses
    /// causal one-pole IIR low-passes — same cutoffs, but with the phase lag
    /// and gentler roll-off any real-time filter has. Treat the live tonic/
    /// phasic as a faithful MONITORING approximation; for the definitive
    /// analysis values, run the RECORDED raw GSR through NeuroKit2 offline
    /// (which is exactly why the raw GSR channel is kept and logged).
    ///
    /// Nothing here converts ADC → microsiemens; NeuroKit works in µS after a
    /// units conversion, but since every stage is linear the SHAPE is identical
    /// and only the scale differs — do the µS calibration in the analysis layer.
    ///
    /// THREADING: ReadValue runs on DelphiCore's sampling thread, so this only
    /// reads the source's Current latch (an atomic float read) and DelphiClock
    /// — no Unity main-thread APIs, no locks (only this one thread samples).
    /// </summary>
    public abstract class GSRDecompositionSensor : ScalarSensor
    {
        [Header("Source")]
        [Tooltip("The RAW GSR sensor to decompose — e.g. the GSRSensorSerial " +
                 "feeding the 'GSR (raw)' slot, or a mock for testing.")]
        [SerializeField] protected ScalarSensor source;

        [Header("Decomposition (NeuroKit2-aligned cutoffs)")]
        [Tooltip("Cleaning low-pass cutoff, Hz. Removes high-frequency noise " +
                 "before decomposition. NeuroKit2 eda_clean default = 3 Hz.")]
        [Min(0.1f)]
        [SerializeField] protected float cleaningCutoffHz = 3f;

        [Tooltip("Tonic/phasic split cutoff, Hz. Below it = tonic (SCL); above " +
                 "it = phasic (SCR). NeuroKit2 eda_phasic(highpass) default = 0.05 Hz.")]
        [Min(0.001f)]
        [SerializeField] protected float tonicCutoffHz = 0.05f;

        public override float Current { get; protected set; } = float.NaN;

        // Cascaded one-pole IIR state — only the sampling thread touches these.
        private double _lastTime = double.NaN;
        private float _cleaned = float.NaN; // stage 1: 3 Hz low-pass of raw
        private float _tonic = float.NaN;   // stage 2: 0.05 Hz low-pass of cleaned

        /// <summary>Advance both filters with the latest raw sample. dt comes
        /// from DelphiClock, so the cutoffs hold regardless of sample rate.
        /// One-pole low-pass: alpha = 1 − exp(−2π·fc·dt).</summary>
        protected void UpdateFilters()
        {
            if (source == null) { _cleaned = _tonic = float.NaN; return; }
            float raw = source.Current;
            if (float.IsNaN(raw)) return; // dropout: hold the last estimates

            double now = DelphiClock.Now;
            if (float.IsNaN(_cleaned) || double.IsNaN(_lastTime))
            {
                _cleaned = _tonic = raw; // seed both on the first real sample
                _lastTime = now;
                return;
            }

            float dt = (float)(now - _lastTime);
            _lastTime = now;
            if (dt <= 0f) return;

            const float twoPi = 2f * Mathf.PI;
            float aClean = 1f - Mathf.Exp(-twoPi * cleaningCutoffHz * dt);
            _cleaned += aClean * (raw - _cleaned);

            float aTonic = 1f - Mathf.Exp(-twoPi * tonicCutoffHz * dt);
            _tonic += aTonic * (_cleaned - _tonic);
        }

        /// <summary>Slow skin-conductance level (SCL): &lt; 0.05 Hz low-pass.</summary>
        protected float Tonic => _tonic;

        /// <summary>Fast skin-conductance response (SCR): cleaned − tonic, i.e.
        /// the 0.05–3 Hz band. NaN until the first sample seeds the filters.</summary>
        protected float Phasic => (float.IsNaN(_cleaned) || float.IsNaN(_tonic))
                                  ? float.NaN : _cleaned - _tonic;
    }
}
