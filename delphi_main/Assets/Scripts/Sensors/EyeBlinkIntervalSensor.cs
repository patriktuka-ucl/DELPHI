namespace Delphi
{
    /// <summary>
    /// Plugs into the "Inter-blink interval" ScalarSensor slot on
    /// DelphiManager. Seconds between blink onsets, from the shared
    /// VarjoGazeConnection — the same shape as GSRRawSensor reading from
    /// GSRSerialConnection.Instance rather than owning any I/O itself.
    ///
    /// Blink detection has to see all 200 Hz of the Varjo stream, which is
    /// why none of it happens here: see VarjoGazeConnection for the detector
    /// and for why the value keeps growing between blinks instead of holding
    /// the last completed interval.
    /// </summary>
    public class EyeBlinkIntervalSensor : ScalarSensor
    {
        public override float Current { get; protected set; } = float.NaN;

        public override float ReadValue()
        {
            // Runs on DELPHI's sampling thread: plain reference null-check
            // (`is null`), same reasoning as GSRRawSensor — Unity's overloaded
            // == belongs to the main thread. The connection's getter is
            // lock-protected, so reading it here is safe.
            var conn = VarjoGazeConnection.Instance;
            Current = conn is null ? float.NaN : conn.GetInterBlinkIntervalSeconds();
            return Current;
        }
    }
}
