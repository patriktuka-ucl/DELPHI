namespace Delphi
{
    /// <summary>
    /// Tonic skin-conductance level (SCL): the slow low-pass of the raw GSR
    /// signal. Drop this on a GameObject, point its Source at the raw GSR
    /// sensor, and plug it into DelphiManager's "GSR tonic" slot. See
    /// <see cref="GSRDecompositionSensor"/> for the shared low-pass.
    /// </summary>
    public class GSRTonicSensor : GSRDecompositionSensor
    {
        public override float ReadValue()
        {
            UpdateFilters();
            Current = Tonic;
            return Current;
        }
    }
}
