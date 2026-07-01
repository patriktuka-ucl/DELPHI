using UnityEngine;

namespace Delphi
{
    /// <summary>
    /// Dead-simple mock sensor: one sine wave + noise. Drop it on a GameObject,
    /// set the numbers, then drag it into a slot on the DelphiManager.
    /// The manager calls ReadValue() once per frame to sample it.
    /// </summary>
    public class MockSensor : ScalarSensor
    {
        [Header("Signal")]
        public float frequency = 0.2f;   // cycles per second (Hz)
        public float amplitude = 8f;     // peak deviation from offset
        public float offset    = 72f;    // centre value
        public float noise     = 1.5f;   // ± random noise

        public override float Current { get; protected set; }

        private float _phase;

        private void Awake() => Current = offset;

        public override float ReadValue()
        {
            _phase += Time.deltaTime * frequency * 2f * Mathf.PI;
            Current = offset + amplitude * Mathf.Sin(_phase) + Random.Range(-noise, noise);
            return Current;
        }
    }
}