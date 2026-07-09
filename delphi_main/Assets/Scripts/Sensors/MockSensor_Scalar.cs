using UnityEngine;

namespace Delphi
{
    /// <summary>
    /// Dead-simple mock scalar sensor: one sine wave + noise. Drop it on a
    /// GameObject, set the numbers, then drag it into any ScalarSensor slot
    /// on DelphiManager to test that slot's pipeline end-to-end.
    /// </summary>
    public class MockSensor_Scalar : ScalarSensor
    {
        [Header("Signal")]
        public float frequency = 0.2f;   // cycles per second (Hz)
        public float amplitude = 8f;     // peak deviation from offset
        public float offset    = 72f;    // centre value
        public float noise     = 1.5f;   // ± random noise

        public override float Current { get; protected set; }

        // System.Random, not UnityEngine.Random: ReadValue runs on DELPHI's
        // sampling thread, where Unity APIs are off-limits. Only that one
        // thread calls ReadValue, so no locking is needed.
        private readonly System.Random _rng = new System.Random();

        private void Awake() => Current = offset;

        public override float ReadValue()
        {
            // Phase from DelphiClock (absolute, Unity-independent) — the
            // sampling thread calls this at the configured rate, so per-call
            // delta accumulation would both slow the wave and be thread-unsafe.
            float phase = (float)DelphiClock.Now * frequency * 2f * Mathf.PI;
            float n = noise > 0f ? (float)(_rng.NextDouble() * 2.0 - 1.0) * noise : 0f;
            Current = offset + amplitude * Mathf.Sin(phase) + n;
            return Current;
        }
    }
}