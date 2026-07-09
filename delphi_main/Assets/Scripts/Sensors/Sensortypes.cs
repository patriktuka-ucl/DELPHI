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
        /// <summary>Most recent sampled value. float.NaN when unavailable.
        /// Written by the sampling thread, read from anywhere (float reads
        /// are atomic).</summary>
        public abstract float Current { get; protected set; }

        /// <summary>
        /// Sample the sensor and return the new value.
        ///
        /// THREADING CONTRACT: called from DelphiCore's dedicated sampling
        /// thread, NOT the Unity main thread. Implementations must be
        /// thread-safe, must never block, and must not touch Unity APIs
        /// that are main-thread-only (Time, Random, transforms, GameObjects
        /// …). Use DelphiClock.Now for time and System.Random for noise.
        /// IO-backed sensors should acquire on their own thread and just
        /// hand over the latest latched value here (see GSRSensorSerial).
        /// </summary>
        public abstract float ReadValue();
    }

    public abstract class FrameSensor : BaseSensor
    {
        /// <summary>Most recent frame. null when unavailable.</summary>
        public abstract Texture CurrentFrame { get; }

        /// <summary>
        /// Capture/refresh the latest frame and return it. Called by
        /// DelphiManager on the MAIN thread (unlike scalar sensors — camera
        /// and texture APIs are main-thread-only in Unity), paced by the
        /// manager's per-feed FPS settings. Must never block.
        /// </summary>
        public abstract Texture ReadFrame();
    }
}