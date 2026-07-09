using System;

namespace Delphi
{
    /// <summary>
    /// meta.json — the manifest written next to the mp4s and sensors.csv in
    /// every session folder. Everything playback needs to reconstruct the
    /// session without guessing: video fps, the csv row rate (independent —
    /// playback looks scalars up by each row's time_s, so the two clocks
    /// don't have to match), which scalar channels were logged, and which
    /// video feeds exist.
    /// </summary>
    [Serializable]
    public class SessionMeta
    {
        public string started;          // ISO-8601 wall-clock start
        public int fps;                 // fastest feed rate — playback's frame-step size
        public float csvRateHz;         // NOMINAL scalar rate at record time; csv rows carry their own true time_s (event-timed, not a grid)
        public float duration;          // seconds
        public string[] scalarChannels; // Channel enum names, csv column order
        public SessionFeedMeta[] feeds;
    }

    [Serializable]
    public class SessionFeedMeta
    {
        public string channel; // FrameChannel enum name
        public string file;    // file name inside the session folder
        public int width;
        public int height;
        public int fps;        // this feed's capture rate (DelphiManager.FrameRate at record time)
    }
}
