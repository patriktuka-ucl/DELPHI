namespace Delphi.Trial
{
    /// <summary>
    /// Per-channel facts the trial layer needs to turn a physiological
    /// channel into a BO objective:
    ///
    ///   MinWindowSeconds — the shortest measurement window that yields a
    ///     meaningful mean for this signal (shown as inspector warnings, not
    ///     enforced): rate-like measures need enough events (beats, breaths,
    ///     blinks) to estimate a rate; RMSSD's 30 s is the accepted
    ///     "ultra-short HRV" floor.
    ///
    ///   DeltaRange — |bound| the RAW native-unit delta (windowMean −
    ///     baselineMean) is safety-clamped to BEFORE z-scoring by the
    ///     baseline's standard deviation. Not the optimizer's bound directly
    ///     (that's ZMax, in TrialManager, applied to the z-score) — this
    ///     just guards against one corrupted window producing an absurd z.
    ///
    ///   HigherIsWorse — objective TARGET, confirmed with Patrik 2026-07-09:
    ///     for most arousal measures, the optimum is baseline itself (z≈0,
    ///     matching resting calm — NOT drifting arbitrarily calmer), so the
    ///     optimizer minimizes |z|. RMSSD is the one exception: suppressed
    ///     HRV vs. baseline is bad, but elevated HRV has no assumed ceiling,
    ///     so it keeps the ORIGINAL behaviour of maximizing raw z with no
    ///     baseline target.
    /// </summary>
    public static class TrialObjectiveInfo
    {
        public static float MinWindowSeconds(Channel ch) => ch switch
        {
            Channel.HeartRate     => 10f,  // a rough rate needs ~a dozen beats
            Channel.RMSSD         => 30f,  // ultra-short HRV floor (60 s preferred)
            Channel.RespRate      => 30f,  // 12–20 breaths/min → needs several breaths
            Channel.GSR           => 10f,  // tonic level settles within seconds
            Channel.BlinkRate     => 30f,  // ~15 blinks/min → needs several blinks
            Channel.Gaze          => 10f,
            Channel.PupilDiameter => 5f,
            Channel.EEG           => 10f,
            Channel.Facial        => 5f,
            _                     => 10f
        };

        public static float DeltaRange(Channel ch) => ch switch
        {
            Channel.HeartRate     => 40f,   // bpm
            Channel.RMSSD         => 100f,  // ms
            Channel.RespRate      => 10f,   // breaths/min
            Channel.GSR           => 300f,  // raw 10-bit units
            Channel.BlinkRate     => 30f,   // blinks/min
            Channel.Gaze          => 100f,
            Channel.PupilDiameter => 2f,    // mm
            Channel.EEG           => 100f,  // µV
            Channel.Facial        => 1f,
            _                     => 100f
        };

        public static bool HigherIsWorse(Channel ch) => ch switch
        {
            Channel.RMSSD => false, // suppressed HRV = stress → maximize delta
            _             => true   // raised arousal measures → minimize delta
        };
    }
}
