using UnityEngine;

namespace Delphi
{
    /// <summary>
    /// Plugs into the HeartRate or RMSSD ScalarSensor slot on DelphiManager,
    /// exactly like your other sensors. Pick which channel this instance
    /// exposes via the inspector dropdown — both backed by the single
    /// shared PolarH10OscConnection instance, so having one of these per
    /// channel does NOT open multiple sockets/connections.
    /// </summary>
    public class PolarH10ChannelReader : ScalarSensor
    {
        public enum Channel
        {
            HeartRate, // BPM, Polar's own onboard beat detection
            HRV_RMSSD, // milliseconds, computed from Polar's RR-intervals
            AccX,      // milli-g, raw PMD accelerometer
            AccY,      // milli-g, raw PMD accelerometer
            AccZ       // milli-g, raw PMD accelerometer
        }

        [SerializeField] private Channel channel = Channel.HeartRate;

        public override float Current { get; protected set; } = float.NaN;

        public override float ReadValue()
        {
            // Runs on DELPHI's sampling thread: plain reference null-check
            // (`is null`), because Unity's overloaded == belongs to the main
            // thread. The connection's getters are lock-protected, so
            // reading them here is safe.
            var conn = PolarH10OscConnection.Instance;
            if (conn is null)
            {
                Current = float.NaN;
                return Current;
            }

            // Front/back-only mode: the two non-selected accelerometer axes
            // report NaN (NoSignal in the dashboard) rather than a stale/
            // misleading number, so it's visually obvious they're not in use.
            if (conn.LockToFrontBackAxis && IsAccelerometerChannel(channel) && !MatchesAxis(channel, conn.FrontBackAxis))
            {
                Current = float.NaN;
                return Current;
            }

            float value = channel switch
            {
                Channel.HeartRate => conn.GetHeartRateBpm(),
                Channel.HRV_RMSSD => conn.GetHrvRmssdMs(),
                Channel.AccX => conn.GetAccXmG(),
                Channel.AccY => conn.GetAccYmG(),
                Channel.AccZ => conn.GetAccZmG(),
                _ => float.NaN
            };

            Current = value;
            return value;
        }

        private static bool IsAccelerometerChannel(Channel ch) =>
            ch == Channel.AccX || ch == Channel.AccY || ch == Channel.AccZ;

        private static bool MatchesAxis(Channel ch, PolarH10OscConnection.AccAxis axis) => (ch, axis) switch
        {
            (Channel.AccX, PolarH10OscConnection.AccAxis.X) => true,
            (Channel.AccY, PolarH10OscConnection.AccAxis.Y) => true,
            (Channel.AccZ, PolarH10OscConnection.AccAxis.Z) => true,
            _ => false
        };
    }
}
