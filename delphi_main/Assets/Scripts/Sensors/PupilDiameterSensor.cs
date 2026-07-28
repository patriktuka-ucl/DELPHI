namespace Delphi
{
    /// <summary>
    /// Plugs into the "Pupil diameter" ScalarSensor slot on DelphiManager.
    /// Millimetres, averaged across both eyes, from the shared
    /// VarjoGazeConnection.
    ///
    /// Varjo's own estimate in real mm (EyeMeasurements.leftPupilDiameterInMM
    /// / rightPupilDiameterInMM), NOT the older normalised 0–1 pupilSize on
    /// GazeData — that one is marked Obsolete in the plugin and is scaled to
    /// whatever range the headset happened to observe, which is not comparable
    /// between participants and therefore useless as a study measure.
    ///
    /// The averaging rule (an eye reporting 0 means "unavailable" and is
    /// dropped, not averaged in) lives in VarjoGazeConnection.
    /// </summary>
    public class PupilDiameterSensor : ScalarSensor
    {
        public override float Current { get; protected set; } = float.NaN;

        public override float ReadValue()
        {
            // Runs on DELPHI's sampling thread — see EyeBlinkIntervalSensor
            // for the `is null` reasoning.
            var conn = VarjoGazeConnection.Instance;
            Current = conn is null ? float.NaN : conn.GetPupilDiameterMm();
            return Current;
        }
    }
}
