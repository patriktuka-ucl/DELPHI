using UnityEngine;

namespace Delphi
{
    /// <summary>
    /// Base class for any sensor that produces a Texture per frame
    /// (camera feeds, Tobii gaze overlays, etc.). Drag any FrameSensor
    /// subcomponent into a FrameSensor slot when those are needed.
    /// </summary>
    public abstract class FrameSensor : BaseSensor
    {
        /// <summary>Most recent frame. null when unavailable.</summary>
        public abstract Texture CurrentFrame { get; }

        /// <summary>
        /// Capture the latest frame and return it. Called once per frame.
        /// Must never block the main thread.
        /// </summary>
        public abstract Texture ReadFrame();
    }
}
