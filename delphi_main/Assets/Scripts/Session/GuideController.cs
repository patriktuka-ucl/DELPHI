using System;
using UnityEngine;

namespace Delphi.Session
{
    /// <summary>
    /// The on-screen/voice guide. All lines are PRE-RECORDED clips played back
    /// verbatim (nothing generated live), so instruction wording is identical
    /// for every participant — one fewer experimenter-variability confound.
    ///
    /// SessionController calls <see cref="Play"/> at each phase transition with
    /// the line it wants spoken. If a clip is wired for that line it's played
    /// through the AudioSource; the text is always logged so the whole flow is
    /// followable in the console before any audio exists (the backbone runs
    /// fine with zero clips assigned).
    /// </summary>
    public class GuideController : MonoBehaviour
    {
        /// <summary>Every distinct thing the guide says, so clips can be wired
        /// one-to-one in the Inspector and SessionController stays clip-agnostic.</summary>
        public enum Line
        {
            Welcome,            // intro: "this is an optimisation task…"
            Meditation,         // relax, calm music
            HabituationStart,   // "we'll take a short drive to settle in"
            ConditionStart,     // a drive/condition begins
            Parking,            // "the car is parking now"
            Questionnaire,      // "please fill out this questionnaire"
            BreakOffer,         // "would you like a break?"
            BreakGranted,       // "okay, I'll let you out of the car"
            ContinueDrive,      // resuming for the next condition
            FreePlayIntro,      // the 6-style free-play round
            Farewell,           // experiment concluded
            EmergencyStop,      // safety halt
            ResumeAfterStop     // re-intro after an emergency stop
        }

        [Serializable]
        public struct Clip
        {
            public Line line;
            public AudioClip audio;
        }

        [Tooltip("Optional — plays the guide's voice. Leave empty to run the " +
                 "session with console-logged lines only (fine for development).")]
        public AudioSource source;

        [Tooltip("One entry per guide Line. Unassigned lines are logged only.")]
        public Clip[] clips = Array.Empty<Clip>();

        [Tooltip("Also print each spoken line to the console.")]
        public bool logLines = true;

        /// <summary>Speak a line: play its clip if one is wired, and (optionally)
        /// log the human-readable text. Never blocks — a missing clip is not an
        /// error, just a silent line.</summary>
        public void Play(Line line, string text = null)
        {
            if (logLines)
                Debug.Log($"[Guide] {line}{(string.IsNullOrEmpty(text) ? "" : $": {text}")}");

            var audio = ClipFor(line);
            if (audio != null && source != null)
            {
                source.Stop();
                source.clip = audio;
                source.Play();
            }
        }

        private AudioClip ClipFor(Line line)
        {
            foreach (var c in clips)
                if (c.line == line) return c.audio;
            return null;
        }
    }
}
