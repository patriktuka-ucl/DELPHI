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
            HRV_RMSSD  // milliseconds, computed from Polar's RR-intervals
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

            float value = channel switch
            {
                Channel.HeartRate => conn.GetHeartRateBpm(),
                Channel.HRV_RMSSD => conn.GetHrvRmssdMs(),
                _ => float.NaN
            };

            Current = value;
            return value;
        }
    }
}
