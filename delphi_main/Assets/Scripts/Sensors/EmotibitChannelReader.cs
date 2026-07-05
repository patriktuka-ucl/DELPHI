using UnityEngine;

namespace Delphi
{
    /// <summary>
    /// Plugs into any ScalarSensor slot on DelphiManager (HeartRate, RMSSD,
    /// or GSR) exactly like your other sensors. Pick which derived channel
    /// this instance exposes via the inspector dropdown — all backed by the
    /// single shared EmotibitOscConnection instance, so having one of these
    /// per channel does NOT open multiple connections/sockets.
    /// </summary>
    public class EmotibitChannelReader : ScalarSensor
    {
        public enum Channel
        {
            HeartRate,      // BPM, derived from PPG peak detection
            HRV_RMSSD,      // milliseconds, derived from PPG peak detection
            RawPpgInfrared, // raw PI stream
            RawEda          // raw EA stream (GSR — combined tonic+phasic)
        }

        [SerializeField] private Channel channel = Channel.HeartRate;

        public override float Current { get; protected set; } = float.NaN;

        public override float ReadValue()
        {
            var conn = EmotibitOscConnection.Instance;
            if (conn == null)
            {
                Current = float.NaN;
                return Current;
            }

            float value = channel switch
            {
                Channel.HeartRate => conn.GetHeartRateBpm(),
                Channel.HRV_RMSSD => conn.GetHrvRmssdMs(),
                Channel.RawPpgInfrared => conn.GetRawPpg(),
                Channel.RawEda => conn.GetRawEda(),
                _ => float.NaN
            };

            Current = value;
            return value;
        }
    }
}
