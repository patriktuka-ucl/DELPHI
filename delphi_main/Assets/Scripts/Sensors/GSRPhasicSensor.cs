namespace Delphi
{
    /// <summary>
    /// Phasic skin-conductance response (SCR): the cleaned GSR minus its tonic
    /// component — the fast, arousal-driven bursts (the 0.05–3 Hz band). Drop
    /// this on a GameObject, point its Source at the SAME raw GSR sensor the
    /// tonic uses, and plug it into DelphiManager's "GSR phasic" slot. It runs
    /// its own copy of the decomposition (same cutoffs as the tonic sensor by
    /// default), so it's self-contained and independent of sample order.
    /// See <see cref="GSRDecompositionSensor"/> for the NeuroKit2-aligned math.
    /// </summary>
    public class GSRPhasicSensor : GSRDecompositionSensor
    {
        public override float ReadValue()
        {
            UpdateFilters();
            Current = Phasic;
            return Current;
        }
    }
}
