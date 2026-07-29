using System;
using System.Collections.Generic;
using UnityEngine;

namespace Delphi.Session
{
    /// <summary>
    /// One rating item: a statement, a scale, and the two words that anchor
    /// its ends.
    /// </summary>
    [Serializable]
    public class DelphiQuestion
    {
        [Tooltip("Short identifier. THIS IS THE OBJECTIVE NAME the optimizer " +
                 "sees and the column header in every CSV, so keep it stable — " +
                 "renaming it mid-study makes old and new sessions " +
                 "incomparable.")]
        public string key = "trust";

        [TextArea(2, 4)]
        [Tooltip("What the participant reads.")]
        public string text = "";

        [Tooltip("How many discrete points the scale has. Odd numbers give a " +
                 "neutral midpoint; even numbers force a side.")]
        [Range(2, 21)] public int steps = 7;

        [Tooltip("Label under the LEFT end (step 1).")]
        public string lowLabel = "Strongly disagree";

        [Tooltip("Label under the RIGHT end (last step).")]
        public string highLabel = "Strongly agree";

        [Tooltip("Tick for items where a HIGH answer is a BAD outcome — " +
                 "'I felt at risk', for instance. The optimizer maximises every " +
                 "objective it is given, so without this an inverted item would " +
                 "push the search toward the worst possible ride while the " +
                 "numbers looked like progress. See NormalisedValue.")]
        public bool inverted;

        /// <summary>Raw answer, 1..steps. 0 means unanswered.</summary>
        [NonSerialized] public int response;

        public bool Answered => response >= 1 && response <= steps;

        /// <summary>Answer mapped to 0..1 with inversion applied, so every
        /// item points the same way: 1 is always the good end.
        ///
        /// Doing this here rather than at the optimizer means the correction
        /// travels with the question that needs it — a later reader cannot
        /// pair the wrong flag with the wrong item.</summary>
        public float NormalisedValue
        {
            get
            {
                if (!Answered || steps < 2) return 0.5f;
                float t = (response - 1f) / (steps - 1f);
                return inverted ? 1f - t : t;
            }
        }
    }

    /// <summary>
    /// DELPHI's own questionnaire — the rating set shown after each measured
    /// iteration, and the source of the Explicit condition's objectives.
    ///
    /// REPLACES QuestionnaireToolkit for this purpose. QT's pages are built at
    /// run time out of screen-space uGUI, which is invisible in a headset, and
    /// its authoring flow is a separate window rather than the inspector. This
    /// is a plain serialized list edited in place, rendered by the same
    /// world-space, fingertip-driven controls as the driving-style panel, so
    /// there is one interaction model to learn and one look to maintain.
    ///
    /// WHAT IT GUARANTEES FOR THE OPTIMIZER:
    ///   • every item reports 0..1 with 1 = good, inversion already applied
    ///   • keys are stable identifiers, used as objective names and CSV headers
    ///   • an unanswered item reports the midpoint rather than zero, so a
    ///     missed question cannot masquerade as the worst possible rating
    /// </summary>
    public class DelphiQuestionnaire : MonoBehaviour
    {
        [Tooltip("The items, in the order the participant sees them. Edit " +
                 "these in the custom inspector below.")]
        public List<DelphiQuestion> questions = new()
        {
            new DelphiQuestion { key = "trust", steps = 7,
                text = "I trusted the car to handle the situation.",
                lowLabel = "Not at all", highLabel = "Completely" },

            new DelphiQuestion { key = "predictability", steps = 7,
                text = "The car's behaviour was predictable.",
                lowLabel = "Very unpredictable", highLabel = "Very predictable" },

            new DelphiQuestion { key = "efficiency", steps = 7,
                text = "The car made good progress towards the destination.",
                lowLabel = "Very inefficient", highLabel = "Very efficient" },

            new DelphiQuestion { key = "satisfaction", steps = 7,
                text = "I was satisfied with how the car drove.",
                lowLabel = "Very dissatisfied", highLabel = "Very satisfied" },

            new DelphiQuestion { key = "perceivedSafety", steps = 7,
                text = "I felt safe during this drive.",
                lowLabel = "Very unsafe", highLabel = "Very safe" },

            new DelphiQuestion { key = "comfort", steps = 7,
                text = "The ride felt comfortable.",
                lowLabel = "Very uncomfortable", highLabel = "Very comfortable" },

            // The one inverted item in the default set — a high answer here is
            // a bad ride, so it is flipped before the optimizer sees it.
            new DelphiQuestion { key = "perceivedRisk", steps = 7, inverted = true,
                text = "I felt at risk during this drive.",
                lowLabel = "No risk at all", highLabel = "Extreme risk" },
        };

        /// <summary>Raised when the participant submits, carrying every item's
        /// normalised value by key — ready to hand to the optimizer.</summary>
        public event Action<Dictionary<string, float>> Submitted;

        public bool AllAnswered
        {
            get
            {
                foreach (var q in questions) if (!q.Answered) return false;
                return true;
            }
        }

        /// <summary>Objective names, in order. Matches the keys of the
        /// dictionary from <see cref="Submitted"/>.</summary>
        public List<string> Keys
        {
            get
            {
                var keys = new List<string>(questions.Count);
                foreach (var q in questions) keys.Add(q.key);
                return keys;
            }
        }

        public void ClearResponses()
        {
            foreach (var q in questions) q.response = 0;
        }

        /// <summary>Collects the answers and fires <see cref="Submitted"/>.
        ///
        /// Unanswered items report the MIDPOINT, not zero. Zero is a real
        /// rating — the worst one — so letting a skipped question through as
        /// zero would hand the optimizer a strong negative signal that the
        /// participant never gave. The midpoint is the honest "no information"
        /// value, and the warning names what was missed.</summary>
        public Dictionary<string, float> Submit()
        {
            var values = new Dictionary<string, float>(questions.Count);
            foreach (var q in questions)
            {
                if (!q.Answered)
                    Debug.LogWarning($"[Questionnaire] '{q.key}' was not answered — submitting the scale " +
                                     "midpoint. That iteration's objective for this item carries no " +
                                     "information from the participant.", this);
                values[q.key] = q.NormalisedValue;
            }
            Submitted?.Invoke(values);
            return values;
        }

        /// <summary>Duplicate keys would silently collapse into one objective,
        /// so they are caught while editing rather than mid-session.</summary>
        public string ValidateKeys()
        {
            var seen = new HashSet<string>();
            foreach (var q in questions)
            {
                if (string.IsNullOrWhiteSpace(q.key)) return "A question has an empty key.";
                if (!seen.Add(q.key)) return $"Duplicate key '{q.key}' — keys become objective names and must be unique.";
            }
            return questions.Count < 2
                ? "mobo.py needs at least 2 objectives; add more questions."
                : null;
        }
    }
}
