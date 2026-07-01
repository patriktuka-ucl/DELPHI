using UnityEngine;

namespace Delphi
{
    /// <summary>
    /// SENSOR BLUEPRINT — the foundational types every sensor in DELPHI is
    /// built from. Kept together in one file since they're conceptually one
    /// idea: what a "sensor" is allowed to be in this project.
    ///
    /// BaseSensor   — root MonoBehaviour type. Exists purely so Unity has one
    ///                concrete ancestor both sensor families share.
    /// ScalarSensor — any sensor producing a single float per frame
    ///                (HR, GSR, respiration, HRV, ...). Plugs into a
    ///                ScalarSensor slot on DelphiManager.
    /// FrameSensor  — any sensor producing a Texture per frame
    ///                (camera feeds, Tobii overlays, ...). Plugs into a
    ///                FrameSensor slot on DelphiManager.
    /// </summary>
    public abstract class BaseSensor : MonoBehaviour { }

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

    public abstract class FrameSensor : BaseSensor
    {
        /// <summary>Most recent frame. null when unavailable.</summary>
        public abstract Texture CurrentFrame { get; }

        /// <summary>
        /// Capture/refresh the latest frame and return it. Called once per
        /// frame by DelphiManager.Update(). Must never block the main thread.
        /// </summary>
        public abstract Texture ReadFrame();
    }
}