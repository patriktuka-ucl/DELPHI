namespace Delphi
{
    /// <summary>
    /// Plugs into the "Gaze distance" ScalarSensor slot on DelphiManager.
    /// Degrees between where the participant is looking now and the baseline
    /// fixation direction captured at the start of the session — i.e. how far
    /// the eye has saccaded off centre — from the shared VarjoGazeConnection.
    ///
    /// Head-relative by construction, because Varjo reports the gaze ray
    /// relative to head pose. That is what makes the channel usable in a
    /// motion rig: the YAW3 swings the participant's head around constantly,
    /// and a world-referenced version of this number would mostly measure the
    /// platform rather than the eye.
    ///
    /// Capture the baseline with VarjoGazeConnection's baseline key (F9 by
    /// default) once the participant is seated, calibrated and looking down
    /// the road.
    /// </summary>
    public class GazeDistanceSensor : ScalarSensor
    {
        public override float Current { get; protected set; } = float.NaN;

        public override float ReadValue()
        {
            // Runs on DELPHI's sampling thread — see EyeBlinkIntervalSensor
            // for the `is null` reasoning.
            var conn = VarjoGazeConnection.Instance;
            Current = conn is null ? float.NaN : conn.GetGazeDistanceDegrees();
            return Current;
        }
    }
}
