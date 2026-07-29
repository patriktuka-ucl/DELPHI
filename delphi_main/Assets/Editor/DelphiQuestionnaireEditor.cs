using Delphi.Session;
using UnityEditor;
using UnityEngine;

namespace Delphi.EditorTools
{
    /// <summary>
    /// Authoring UI for DelphiQuestionnaire: add, reorder, delete and edit
    /// items in place, with a live preview of the scale the participant will
    /// actually see.
    ///
    /// The preview matters more than it looks. A 21-step scale with two long
    /// end labels is perfectly reasonable in a list of fields and unreadable
    /// on a panel in a headset, and there is no way to tell which you have
    /// without drawing it. Showing the steps here catches that at authoring
    /// time instead of with a participant sitting in the car.
    /// </summary>
    [CustomEditor(typeof(DelphiQuestionnaire))]
    public class DelphiQuestionnaireEditor : Editor
    {
        private static readonly Color Accent = new Color(0.24f, 0.83f, 0.76f);
        private static readonly Color Warn   = new Color(0.95f, 0.66f, 0.25f);

        public override void OnInspectorGUI()
        {
            var q = (DelphiQuestionnaire)target;
            serializedObject.Update();

            DrawRoleBanner(q);

            EditorGUILayout.LabelField("Questions", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Each question becomes one OPTIMIZER OBJECTIVE, named by its key. " +
                "Answers are normalised to 0..1 with 1 always meaning 'good' — tick " +
                "Inverted on items where a high answer is a bad outcome.",
                MessageType.None);

            string problem = q.ValidateKeys();
            if (problem != null)
            {
                var prev = GUI.color; GUI.color = Warn;
                EditorGUILayout.HelpBox(problem, MessageType.Warning);
                GUI.color = prev;
            }

            int removeAt = -1, moveUp = -1, moveDown = -1;

            for (int i = 0; i < q.questions.Count; i++)
            {
                var item = q.questions[i];

                EditorGUILayout.BeginVertical(EditorStyles.helpBox);

                // Header row: key + reorder/delete
                EditorGUILayout.BeginHorizontal();
                var c = GUI.color; GUI.color = Accent;
                EditorGUILayout.LabelField($"{i + 1}.", GUILayout.Width(22));
                GUI.color = c;

                EditorGUI.BeginChangeCheck();
                string newKey = EditorGUILayout.TextField(item.key);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(q, "Edit question key");
                    item.key = newKey.Trim();
                    EditorUtility.SetDirty(q);
                }

                using (new EditorGUI.DisabledScope(i == 0))
                    if (GUILayout.Button("▲", GUILayout.Width(26))) moveUp = i;
                using (new EditorGUI.DisabledScope(i == q.questions.Count - 1))
                    if (GUILayout.Button("▼", GUILayout.Width(26))) moveDown = i;
                if (GUILayout.Button("✕", GUILayout.Width(26))) removeAt = i;
                EditorGUILayout.EndHorizontal();

                EditorGUI.BeginChangeCheck();
                string text = EditorGUILayout.TextArea(item.text, GUILayout.MinHeight(36));
                int steps = EditorGUILayout.IntSlider("Steps", item.steps, 2, 21);

                EditorGUILayout.BeginHorizontal();
                string low  = EditorGUILayout.TextField("Low end", item.lowLabel);
                string high = EditorGUILayout.TextField("High end", item.highLabel);
                EditorGUILayout.EndHorizontal();

                bool inverted = EditorGUILayout.ToggleLeft(
                    new GUIContent("Inverted — a HIGH answer is a BAD outcome",
                        "Flips this item before it reaches the optimizer, so 1 still means good."),
                    item.inverted);

                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(q, "Edit question");
                    item.text = text; item.steps = steps;
                    item.lowLabel = low; item.highLabel = high; item.inverted = inverted;
                    EditorUtility.SetDirty(q);
                }

                DrawScalePreview(item);
                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(4);
            }

            // Applied after the loop — mutating the list while enumerating it
            // would throw or skip an entry.
            if (moveUp > 0)
            {
                Undo.RecordObject(q, "Reorder questions");
                (q.questions[moveUp - 1], q.questions[moveUp]) = (q.questions[moveUp], q.questions[moveUp - 1]);
                EditorUtility.SetDirty(q);
            }
            if (moveDown >= 0 && moveDown < q.questions.Count - 1)
            {
                Undo.RecordObject(q, "Reorder questions");
                (q.questions[moveDown + 1], q.questions[moveDown]) = (q.questions[moveDown], q.questions[moveDown + 1]);
                EditorUtility.SetDirty(q);
            }
            if (removeAt >= 0)
            {
                Undo.RecordObject(q, "Delete question");
                q.questions.RemoveAt(removeAt);
                EditorUtility.SetDirty(q);
            }

            EditorGUILayout.Space(6);
            if (GUILayout.Button("Add question", GUILayout.Height(26)))
            {
                Undo.RecordObject(q, "Add question");
                q.questions.Add(new DelphiQuestion
                {
                    key = "newItem" + q.questions.Count,
                    text = "",
                    steps = 7,
                    lowLabel = "Strongly disagree",
                    highLabel = "Strongly agree"
                });
                EditorUtility.SetDirty(q);
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField($"{q.questions.Count} objectives will be sent to the optimizer.",
                                       EditorStyles.miniLabel);

            serializedObject.ApplyModifiedProperties();
        }

        /// <summary>Names which of the two questionnaires this is and where it
        /// is shown. The set is edited here but POSITIONED on the
        /// VrQuestionnairePanel below, and there is nothing on this component
        /// that would tell you that.</summary>
        private static void DrawRoleBanner(DelphiQuestionnaire q)
        {
            var session = Object.FindFirstObjectByType<SessionController>();
            string role =
                session == null                              ? null :
                session.delphiQuestionnaire == q             ? "PER-TRIAL questionnaire (Explicit condition). Shown after every measured iteration; each item below is one optimizer objective." :
                session.conditionEvaluation == q             ? "CONDITION-EVALUATION questionnaire. Shown once at the end of each condition; recorded to CSV, not optimized." :
                null;

            if (role != null) EditorGUILayout.HelpBox(role, MessageType.Info);
            else EditorGUILayout.HelpBox(
                "Not assigned to the SessionController — drag this object into its " +
                "'Delphi Questionnaire' or 'Condition Evaluation' slot, or it is never shown.",
                MessageType.Warning);

            if (q.GetComponent<Delphi.VR.VrQuestionnairePanel>() == null)
                EditorGUILayout.HelpBox(
                    "There is no VrQuestionnairePanel on this GameObject, so these questions have " +
                    "nothing to draw them in VR. Add one — its inspector is where the popup's " +
                    "size and position live.", MessageType.Warning);
            else
                EditorGUILayout.LabelField(
                    "Popup size and position: VrQuestionnairePanel component, below.",
                    EditorStyles.miniLabel);
        }

        /// <summary>Draws the scale as the participant will meet it, so an
        /// unreadable step count or an over-long label is obvious here.</summary>
        private static void DrawScalePreview(DelphiQuestion item)
        {
            var r = EditorGUILayout.GetControlRect(false, 26);
            float pad = 4f;
            float w = r.width - pad * 2f;
            int n = Mathf.Max(2, item.steps);

            EditorGUI.DrawRect(new Rect(r.x + pad, r.y + 11f, w, 2f), new Color(1, 1, 1, 0.16f));
            for (int s = 0; s < n; s++)
            {
                float x = r.x + pad + (w - 6f) * (s / (float)(n - 1));
                bool end = s == 0 || s == n - 1;
                EditorGUI.DrawRect(new Rect(x, r.y + (end ? 5f : 8f), 6f, end ? 14f : 8f),
                                   end ? Accent : new Color(1, 1, 1, 0.35f));
            }

            var style = new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = new Color(0.7f, 0.75f, 0.82f) } };
            var lr = new Rect(r.x + pad, r.y + 12f, w * 0.5f, 16f);
            var rr = new Rect(r.x + pad + w * 0.5f, r.y + 12f, w * 0.5f, 16f);
            GUI.Label(lr, item.lowLabel, style);
            var right = new GUIStyle(style) { alignment = TextAnchor.MiddleRight };
            GUI.Label(rr, item.highLabel, right);

            if (item.inverted)
                EditorGUILayout.LabelField("   ↳ inverted: high answer scores as low", EditorStyles.miniLabel);
        }
    }
}
