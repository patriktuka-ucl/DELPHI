using System;
using System.Collections.Generic;
using UnityEngine;

namespace Delphi.Session
{
    /// <summary>
    /// Plays the participant-facing spoken narration — pre-recorded audio
    /// clips, played back verbatim (nothing generated live), so instruction
    /// wording is identical for every participant.
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
    ///
    /// <see cref="Line"/> is ONE ENTRY PER RECORDED FILE — not one entry per
    /// abstract "moment". Two moments that share a recording (the meditation,
    /// the break question) share a Line; two moments that have their own
    /// recording (each condition slot's intro, each post-condition
    /// evaluation) get their own Line even though the code path is identical.
    /// <see cref="FileName"/> is the authoritative Line→file mapping, and the
    /// custom Inspector uses it to auto-assign the whole set by file name.
    /// </summary>
    public class NarrationController : MonoBehaviour
    {
        /// <summary>Every recorded clip, one entry per .mp3, in session order.
        /// The comment on each is the file it expects — see <see cref="FileName"/>.
        ///
        /// NOTE: append new lines at the END only. The Inspector's clips[]
        /// stores each entry's enum VALUE, so inserting in the middle silently
        /// re-points already-wired clips at the wrong lines.</summary>
        public enum Line
        {
            Intro,          // 00_intro          — welcome & task briefing
            Meditation,     // 0x_meditation     — shared: played before every drive
            Cond1Explicit,  // 01a_explicit      — slot-1 intro, Explicit condition
            Cond1Implicit,  // 01b_implicit      — slot-1 intro, Implicit condition
            Cond1Explore,   // 01c_explore       — slot-1 intro, Explore (FreeRoam) condition
            TrialEval1,     // 02_trialEval1     — evaluation questionnaire after drive 1
            BreakAsk,       // 0x_breakAsk       — shared: "would you like a break?"
            Cond2Explicit,  // 03a_explicit      — slot-2 intro, Explicit condition
            Cond2Implicit,  // 03b_implicit      — slot-2 intro, Implicit condition
            Cond2Explore,   // 03c_explore       — slot-2 intro, Explore (FreeRoam) condition
            TrialEval2,     // 04_trialEval2     — evaluation questionnaire after drive 2
            Cond3Explicit,  // 05a_explicit      — slot-3 intro, Explicit condition
            Cond3Implicit,  // 05b_implicit      — slot-3 intro, Implicit condition
            Cond3Explore,   // 05c_explore       — slot-3 intro, Explore (FreeRoam) condition
            TrialEval3,     // 06_trialEval3     — evaluation questionnaire after drive 3
            Closing,        // 07_closing        — session over (the interview happens in person, after this)
            EmergencyStop,  // extra_emergencyStop — safety halt, playable at any point
            ExploreNudge,   // extra_exploreNudge  — Explore condition only, on demand
        }

        /// <summary>The file this line expects, WITHOUT extension. The custom
        /// Inspector's "Auto-assign by file name" matches against exactly
        /// this, so renaming a recording means changing it here (and only
        /// here) rather than re-dragging by hand.</summary>
        public static string FileName(Line line) => line switch
        {
            Line.Intro         => "00_intro",
            Line.Meditation    => "0x_meditation",
            Line.Cond1Explicit => "01a_explicit",
            Line.Cond1Implicit => "01b_implicit",
            Line.Cond1Explore  => "01c_explore",
            Line.TrialEval1    => "02_trialEval1",
            Line.BreakAsk      => "0x_breakAsk",
            Line.Cond2Explicit => "03a_explicit",
            Line.Cond2Implicit => "03b_implicit",
            Line.Cond2Explore  => "03c_explore",
            Line.TrialEval2    => "04_trialEval2",
            Line.Cond3Explicit => "05a_explicit",
            Line.Cond3Implicit => "05b_implicit",
            Line.Cond3Explore  => "05c_explore",
            Line.TrialEval3    => "06_trialEval3",
            Line.Closing       => "07_closing",
            Line.EmergencyStop => "extra_emergencyStop",
            Line.ExploreNudge  => "extra_exploreNudge",
            _                  => line.ToString(),
        };

        public static readonly Line[] AllLines = (Line[])Enum.GetValues(typeof(Line));

        [Serializable]
        public struct Clip
        {
            public Line line;
            public AudioClip audio;
        }

        [Tooltip("Plays the narration audio. Leave empty to run the session " +
                 "with console-logged lines only (fine for development).")]
        public AudioSource source;

        [Tooltip("One entry per Line. Unassigned lines are logged only. Use " +
                 "the buttons above to create every slot / auto-assign by file name.")]
        public Clip[] clips = Array.Empty<Clip>();

        [Tooltip("Also print each spoken line to the console.")]
        public bool logLines = true;

        [Header("Testing")]
        [Tooltip("Skip the audio: every line is logged but not played, and " +
                 "each phase lasts muteTestSeconds instead of the clip's real " +
                 "length. For fast iteration through the flow without sitting " +
                 "through the real recordings.\n\n" +
                 "MUST BE OFF FOR A REAL SESSION — it also shortens the " +
                 "meditation, which is where the physiological baseline is " +
                 "measured.")]
        public bool muteTest = false;
        [Tooltip("Wait time used in Mute Test mode, seconds.")]
        [Min(0f)] public float muteTestSeconds = 3f;
        [Tooltip("Wait time used by WaitSeconds() when the line hasn't had a " +
                 "clip assigned yet (and Mute Test is off) — keeps the " +
                 "backbone runnable before every recording is wired.")]
        [Min(0f)] public float missingClipWaitSeconds = 3f;

        /// <summary>Speak a line: play its clip if one is wired, and (optionally)
        /// log the human-readable text. Never blocks — a missing clip is not an
        /// error, just a silent line. Call <see cref="WaitSeconds"/> separately
        /// to find out how long to hold the phase open for it.</summary>
        public void Play(Line line, string text = null)
        {
            if (logLines)
                Debug.Log($"[Narration] {line} ({FileName(line)}){(string.IsNullOrEmpty(text) ? "" : $": {text}")}");

            // Mute Test shortens WaitSeconds, so playing the real clip here
            // would guarantee it gets cut off mid-sentence when the phase
            // advances — the timer and the audio have to agree about how long
            // a line lasts, in both directions.
            if (muteTest) return;

            var audio = ClipFor(line);
            if (audio == null)
            {
                Debug.LogWarning($"[Narration] No clip assigned for {line} — expected '{FileName(line)}'. " +
                                 "Playing nothing; the phase still runs for missingClipWaitSeconds.");
                return;
            }
            if (source == null) return;

            source.Stop();
            source.clip = audio;
            source.time = 0f;
            source.Play();
            _current = line;
            _hasCurrent = true;
        }

        /// <summary>Cut the current line short — used when a phase ends before
        /// its narration does (the researcher clicks through). Forgets where it
        /// was, so a later ResumeSpeaking() has nothing to go back to.</summary>
        public void StopSpeaking()
        {
            if (source != null) source.Stop();
            _hasCurrent = false;
            _hasSuspended = false;
        }

        public bool IsSpeaking => source != null && source.isPlaying;

        // ── Suspend/resume (emergency stop) ─────────────────────────────
        private Line _current;       // what Play() last started
        private bool _hasCurrent;
        private Line _suspendedLine; // what SuspendSpeaking() interrupted
        private float _suspendedTime;
        private bool _hasSuspended;

        /// <summary>Remember where the current line had got to, then silence
        /// it. SessionController calls this on an emergency stop, immediately
        /// before playing the stop line over the top.
        ///
        /// The phase timer resumes in place rather than restarting, so without
        /// this the participant would lose whatever remained of a briefing or
        /// condition intro that a stop happened to land in the middle of — the
        /// countdown would keep ticking down against silence and the session
        /// would move on having only said half of it.</summary>
        public void SuspendSpeaking()
        {
            _hasSuspended = false;
            if (source == null || !_hasCurrent || !source.isPlaying) { source?.Stop(); return; }

            _suspendedLine = _current;
            _suspendedTime = source.time;
            _hasSuspended = true;
            source.Stop();
        }

        /// <summary>Pick the suspended line back up from where it was cut,
        /// give or take <paramref name="rewindSeconds"/> — a word or two of
        /// overlap is easier to follow than resuming mid-syllable. No-op if
        /// nothing was suspended, or if the clip has since been unassigned.</summary>
        public void ResumeSpeaking(float rewindSeconds = 1f)
        {
            if (source == null || !_hasSuspended) return;
            _hasSuspended = false;

            var audio = ClipFor(_suspendedLine);
            if (audio == null) return;

            // Clamp inside the clip: seeking to exactly length throws, and a
            // line that was cut off in its last fraction of a second has
            // nothing left worth replaying.
            float t = Mathf.Clamp(_suspendedTime - Mathf.Max(0f, rewindSeconds), 0f, Mathf.Max(0f, audio.length - 0.05f));
            if (_suspendedTime >= audio.length - 0.05f) return;

            if (logLines)
                Debug.Log($"[Narration] Resuming {_suspendedLine} at {t:0.0}s / {audio.length:0.0}s.");

            source.clip = audio;
            source.time = t;
            source.Play();
            _current = _suspendedLine;
            _hasCurrent = true;
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

        public AudioClip ClipFor(Line line)
        {
            foreach (var c in clips)
                if (c.line == line) return c.audio;
            return null;
        }

        /// <summary>Every line with no clip behind it. The custom Inspector
        /// shows this so a missing recording is caught while wiring the scene
        /// rather than as silence in front of a participant.</summary>
        public List<Line> MissingLines()
        {
            var missing = new List<Line>();
            foreach (var line in AllLines)
                if (ClipFor(line) == null) missing.Add(line);
            return missing;
        }

        /// <summary>Rebuild clips[] so it holds exactly one slot per Line, in
        /// enum order, keeping whatever was already assigned. Safe to re-run
        /// after Lines are added — it never drops a wired clip.</summary>
        public void EnsureAllSlots()
        {
            var existing = new Dictionary<Line, AudioClip>();
            foreach (var c in clips)
                if (c.audio != null && !existing.ContainsKey(c.line)) existing[c.line] = c.audio;

            var rebuilt = new Clip[AllLines.Length];
            for (int i = 0; i < AllLines.Length; i++)
            {
                var line = AllLines[i];
                rebuilt[i] = new Clip
                {
                    line = line,
                    audio = existing.TryGetValue(line, out var a) ? a : null
                };
            }
            clips = rebuilt;
        }
    }
}
