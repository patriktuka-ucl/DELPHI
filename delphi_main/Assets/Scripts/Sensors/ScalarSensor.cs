namespace Delphi
{
    /// <summary>
    /// Base class for any sensor that produces a single float value per frame
    /// (HR, GSR, respiration, HRV, etc.). Drag any ScalarSensor subcomponent
    /// into a ScalarSensor slot on DelphiManager.
    /// </summary>
    public abstract class ScalarSensor : BaseSensor
    {
        /// <summary>Most recent sampled value. float.NaN when unavailable.</summary>
        public abstract float Current { get; protected set; }

        /// <summary>
        /// Sample the sensor and return the new value. Called once per frame
        /// by DelphiManager.Update(). Must never block the main thread.
        /// </summary>
        public abstract float ReadValue();
    }
}
