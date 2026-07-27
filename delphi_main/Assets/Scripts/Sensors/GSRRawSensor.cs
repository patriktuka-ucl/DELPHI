using UnityEngine;

namespace Delphi
{
    /// <summary>
    /// Plugs into the "GSR (raw)" ScalarSensor slot on DelphiManager, exactly
    /// like your other sensors. Reads the latest raw ADC value (0–1023) from
    /// the shared GSRSerialConnection — the same shape as PolarH10ChannelReader
    /// reading from PolarH10OscConnection.Instance rather than owning any I/O
    /// itself. No conversion is applied; see GSRSerialConnection for why raw
    /// is kept as-is.
    /// </summary>
    public class GSRRawSensor : ScalarSensor
    {
        public override float Current { get; protected set; } = float.NaN;

        public override float ReadValue()
        {
            // Runs on DELPHI's sampling thread: plain reference null-check
            // (`is null`), same reasoning as PolarH10ChannelReader — Unity's
            // overloaded == belongs to the main thread. GSRSerialConnection's
            // getter is lock-protected, so reading it here is safe.
            var conn = GSRSerialConnection.Instance;
            Current = conn is null ? float.NaN : conn.GetRawValue();
            return Current;
        }
    }
}
