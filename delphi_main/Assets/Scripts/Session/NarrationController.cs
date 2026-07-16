using System;
using UnityEngine;

namespace Delphi.Session
{
    /// <summary>
    /// Plays the participant-facing spoken narration — pre-recorded audio
    /// clips, played back verbatim (nothing generated live), so instruction
    /// wording is identical for every participant. Was called
    /// "GuideController"; renamed because that name didn't say what it
    /// actually does.
    ///
    /// SessionController calls <see cref="Play"/> to speak a line, and
    /// <see cref="WaitSeconds"/> to ask how long that line's own duration is
    /// (the clip's length, or the mute/no-clip fallback) — the clip's own
    /// length IS the phase duration, no separately-typed seconds field to
    /// drift out of sync with the actual recording. SessionController drives
    /// the actual countdown itself (via its normal DelphiClock-based phase
    /// timer, the same one every other timed phase uses), which is what lets
    /// an emergency-stop pause mid-narration freeze and resume in place
    /// instead of restarting the line. The text is always logged so the whole
    /// flow is followable in the console before any audio exists.
    /// </summary>
    public class NarrationController : MonoBehaviour
    {
        /// <summary>Every distinct thing that gets said, so clips can be wired
        /// one-to-one in the Inspector and SessionController stays clip-agnostic.</summary>
        public enum Line
        {
            Welcome,            // general intro: "this is an optimisation task…"
            Meditation,         // relax, calm music — shared by both conditions
            IntroImplicit,      // framing before the Implicit (physiology-objective) condition
            IntroExplicit,      // framing before the Explicit (questionnaire-objective) condition
            Parking,            // "the car is parking now"
            Questionnaire,      // "please fill out this questionnaire"
            BreakOffer,         // "would you like a break?"
            BreakGranted,       // "okay, I'll let you out of the car"
            ContinueDrive,      // resuming for the next condition
            FreePlayIntro,      // hand-over to manual slider control
            Finished,           // whole session complete — the in-person interview happens after this, outside the app
            EmergencyStop,      // safety halt
            ResumeAfterStop     // re-intro after an emergency stop
        }

        [Serializable]
        public struct Clip
        {
            public Line line;
            public AudioClip audio;
        }

        [Tooltip("Optional — plays the narration audio. Leave empty to run " +
                 "the session with console-logged lines only (fine for development).")]
        public AudioSource source;

        [Tooltip("One entry per Line. Unassigned lines are logged only.")]
        public Clip[] clips = Array.Empty<Clip>();

        [Tooltip("Also print each spoken line to the console.")]
        public bool logLines = true;

        [Header("Testing")]
        [Tooltip("WaitSeconds() ignores the real clip length and returns a " +
                 "fixed short time instead — fast iteration through Intro/" +
                 "Meditation/condition intros without sitting through real " +
                 "audio every test run.")]
        public bool muteTest = false;
        [Tooltip("Wait time used in Mute Test mode, seconds.")]
        [Min(0f)] public float muteTestSeconds = 3f;
        [Tooltip("Wait time used by WaitSeconds() when the line hasn't had a " +
                 "clip recorded yet (and Mute Test is off) — keeps the " +
                 "backbone runnable before real recordings exist.")]
        [Min(0f)] public float missingClipWaitSeconds = 3f;

        /// <summary>Speak a line: play its clip if one is wired, and (optionally)
        /// log the human-readable text. Never blocks — a missing clip is not an
        /// error, just a silent line. Call <see cref="WaitSeconds"/> separately
        /// to find out how long to hold the phase open for it.</summary>
        public void Play(Line line, string text = null)
        {
            if (logLines)
                Debug.Log($"[Narration] {line}{(string.IsNullOrEmpty(text) ? "" : $": {text}")}");

            var audio = ClipFor(line);
            if (audio != null && source != null)
            {
                source.Stop();
                source.clip = audio;
                source.Play();
            }
        }

        /// <summary>How long this line's own duration is — the clip's length,
        /// or muteTestSeconds/missingClipWaitSeconds if there's no clip (or
        /// Mute Test is on). Call this to find out how long to hold a phase
        /// open; call <see cref="Play"/> separately to actually speak it. Kept
        /// as two steps (rather than one Play-and-wait call) so the caller
        /// drives the actual countdown itself via its own phase timer — which
        /// is what lets an emergency-stop pause freeze mid-line and resume
        /// exactly where it left off, instead of restarting the line.</summary>
        public float WaitSeconds(Line line)
        {
            if (muteTest) return muteTestSeconds;
            var audio = ClipFor(line);
            return audio != null ? audio.length : missingClipWaitSeconds;
        }

        private AudioClip ClipFor(Line line)
        {
            foreach (var c in clips)
                if (c.line == line) return c.audio;
            return null;
        }
    }
}
