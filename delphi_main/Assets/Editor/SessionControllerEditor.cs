using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Delphi;
using Delphi.Session;
using Delphi.Trial;

namespace Delphi.EditorTools
{
    /// <summary>
    /// SessionController inspector, custom-drawn so the merged session+trial
    /// config stays legible:
    ///   - the standard fields (minus the raw channelConfigs list, drawn as a
    ///     table below) — organized by the [Header] groups on the fields;
    ///   - a PER-CHANNEL normalization table driven by the plugged-in
    ///     DelphiManager's enabled channels (Physiology objective only);
    ///   - TWO live trial-timing summaries, one per condition kind, since
    ///     Implicit/Explicit can now have different baseline/iteration config;
    ///   - in Play mode, the running phase/iteration and captured bounds.
    /// </summary>
    [CustomEditor(typeof(SessionController))]
    public class SessionControllerEditor : Editor
    {
        // Fields whose section is collapsible are excluded from the default
        // property list; each is instead drawn once, inside its own foldout.
        private static readonly string[] LinkFields =
            { "manager", "carDriver", "recorder", "narration", "questionnaire", "finalEvaluationQuestionnaire" };

        public override void OnInspectorGUI()
        {
            var ctrl = (SessionController)target;
            serializedObject.Update();

            var excluded = new List<string> { "channelConfigs", "implicitTrial", "explicitTrial" };
            excluded.AddRange(LinkFields);
            DrawPropertiesExcluding(serializedObject, excluded.ToArray());

            if (Foldout("links", "Links (auto-found if left empty)"))
            {
                EditorGUILayout.BeginVertical("box");
                foreach (var field in LinkFields)
                    EditorGUILayout.PropertyField(serializedObject.FindProperty(field));
                EditorGUILayout.EndVertical();
            }

            DrawNormalizationTable(ctrl);

            if (Foldout("implicitTiming", "Implicit condition timing"))
                DrawTimingPanel(ctrl, "implicitTrial", SessionController.ConditionKind.Implicit);
            if (Foldout("explicitTiming", "Explicit condition timing"))
                DrawTimingPanel(ctrl, "explicitTrial", SessionController.ConditionKind.Explicit);

            DrawLivePanel(ctrl);

            serializedObject.ApplyModifiedProperties();
        }

        // ── Collapsible section headers, remembered across selections ───
        private static bool Foldout(string key, string label)
        {
            string prefKey = "DELPHI.SessionControllerEditor." + key;
            bool value = EditorPrefs.GetBool(prefKey, true);
            EditorGUILayout.Space(8);
            bool next = EditorGUILayout.Foldout(value, label, true, EditorStyles.foldoutHeader);
            if (next != value) EditorPrefs.SetBool(prefKey, next);
            return next;
        }

        // ── Per-channel normalization table (Physiology objective) ─────
        private void DrawNormalizationTable(SessionController ctrl)
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Per-channel normalization (Physiology objective)", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            if (ctrl.manager == null)
            {
                EditorGUILayout.HelpBox(
                    "Plug a DelphiManager into 'Manager' above (or leave it for " +
                    "auto-find in Play mode) — the channel list comes from its " +
                    "enabled sensors.", MessageType.Info);
                EditorGUILayout.EndVertical();
                return;
            }

            List<Channel> channels = Application.isPlaying
                ? ctrl.CandidateChannels()
                : new List<Channel>(DelphiManager.AllChannels);

            EnsureRows(channels);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Channel", EditorStyles.miniBoldLabel, GUILayout.Width(150));
            EditorGUILayout.LabelField("SD (native)", EditorStyles.miniBoldLabel, GUILayout.Width(90));
            EditorGUILayout.LabelField("Higher is better", EditorStyles.miniBoldLabel);
            EditorGUILayout.EndHorizontal();

            var listProp = serializedObject.FindProperty("channelConfigs");
            foreach (var ch in channels)
            {
                var el = FindRow(listProp, ch);
                if (el == null) continue;
                var sd = el.FindPropertyRelative("sd");
                var hib = el.FindPropertyRelative("higherIsBetter");

                EditorGUILayout.BeginHorizontal();
                var (label, unit) = DelphiManager.Meta(ch);
                EditorGUILayout.LabelField(string.IsNullOrEmpty(unit) ? label : $"{label} ({unit})",
                                           GUILayout.Width(150));
                sd.floatValue = Mathf.Max(1e-6f, EditorGUILayout.FloatField(sd.floatValue, GUILayout.Width(90)));
                hib.boolValue = EditorGUILayout.Toggle(hib.boolValue);
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField(
                "SD = population native-unit spread from literature (placeholders " +
                "until you set them). Bounds = baseline ± k·SD. 'Higher is better' " +
                "ON = dropping below baseline is penalized (RMSSD); OFF = rising " +
                "above is (HR, GSR). Explicit/Questionnaire trials don't use this " +
                "table at all — see 'Questionnaire objective' above instead.",
                EditorStyles.miniLabel);

            EditorGUILayout.EndVertical();
        }

        private void EnsureRows(List<Channel> channels)
        {
            var listProp = serializedObject.FindProperty("channelConfigs");
            foreach (var ch in channels)
            {
                if (FindRow(listProp, ch) != null) continue;
                int idx = listProp.arraySize;
                listProp.InsertArrayElementAtIndex(idx);
                var el = listProp.GetArrayElementAtIndex(idx);
                el.FindPropertyRelative("channel").enumValueIndex = (int)ch;
                el.FindPropertyRelative("sd").floatValue = ChannelMath.DefaultSd(ch);
                el.FindPropertyRelative("higherIsBetter").boolValue = ChannelMath.DefaultHigherIsBetter(ch);
            }
        }

        private static SerializedProperty FindRow(SerializedProperty listProp, Channel ch)
        {
            for (int i = 0; i < listProp.arraySize; i++)
            {
                var el = listProp.GetArrayElementAtIndex(i);
                if (el.FindPropertyRelative("channel").enumValueIndex == (int)ch) return el;
            }
            return null;
        }

        // ── Timing summary — one panel per condition kind ───────────────
        private void DrawTimingPanel(SessionController ctrl, string configFieldName, SessionController.ConditionKind kind)
        {
            EditorGUILayout.BeginVertical("box");

            var cfgProp = serializedObject.FindProperty(configFieldName);
            var iterationsProp = cfgProp.FindPropertyRelative("iterations");
            var samplingProp = cfgProp.FindPropertyRelative("samplingIterations");

            EditorGUILayout.PropertyField(iterationsProp, new GUIContent("Iterations"));
            EditorGUILayout.PropertyField(samplingProp, new GUIContent("Sampling iterations"));

            float window = ctrl.windowSeconds;
            float washout = Mathf.Min(ctrl.washoutSeconds, ctrl.windowSeconds);
            float measure = ctrl.MeasureSeconds;
            double driveSeconds = (double)iterationsProp.intValue * window;

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(
                $"Drive: {iterationsProp.intValue} × {Fmt(window)}  =  {Fmt(driveSeconds)}",
                EditorStyles.largeLabel);
            // The baseline is session-level now (it lives inside the shared
            // meditation track), so it isn't part of this per-condition panel —
            // but it IS part of the wall-clock time, so say so here rather
            // than let the number read as the whole condition.
            EditorGUILayout.LabelField(
                $"+ the meditation before it, which is where the baseline is measured " +
                $"({Fmt(ctrl.baselineWindowSeconds)} window, ending {Fmt(ctrl.baselineWindowEndOffsetSeconds)} " +
                "before the track ends)");
            EditorGUILayout.LabelField(
                $"Per iteration: {Fmt(washout)} washout" +
                (kind == SessionController.ConditionKind.Explicit
                    ? " + awaiting rating (no fixed measure window — participant-gated)"
                    : $" + {Fmt(measure)} measured"));

            float effectiveTransition = Mathf.Min(ctrl.transitionSeconds, washout);
            EditorGUILayout.LabelField(
                $"Parameter ramp: {Fmt(effectiveTransition)} of the washout" +
                (ctrl.transitionSeconds > washout
                    ? $" (clamped from {Fmt(ctrl.transitionSeconds)} — exceeds washout)"
                    : ""));

            int sampling = Mathf.Clamp(samplingProp.intValue, 1, Mathf.Max(1, iterationsProp.intValue - 1));
            EditorGUILayout.LabelField(
                $"Budget: {sampling} Sobol exploration + {iterationsProp.intValue - sampling} model-guided");

            EditorGUILayout.EndVertical();
        }

        // ── Live (Play mode) ────────────────────────────────────────────
        private void DrawLivePanel(SessionController ctrl)
        {
            if (!Application.isPlaying) return;

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Live", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField(
                $"Phase: {ctrl.CurrentPhase}   Iteration {ctrl.Iteration}/{ctrl.TotalIterations}   {ctrl.StatusLine}");

            if (ctrl.ObjectiveChannels != null && ctrl.ObjectiveChannels.Count > 0)
            {
                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField(
                    $"Captured bounds (activation {ctrl.ActivationFn}):", EditorStyles.miniBoldLabel);
                foreach (var ch in ctrl.ObjectiveChannels)
                {
                    if (ctrl.TryGetBounds(ch, out float lo, out float hi))
                        EditorGUILayout.LabelField($"{ch}: [{lo:F2}, {hi:F2}]", EditorStyles.miniLabel);
                }
            }
            EditorGUILayout.EndVertical();

            Repaint();
        }

        private static string Fmt(double seconds)
        {
            var t = TimeSpan.FromSeconds(seconds);
            return t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}:{t.Seconds:00} min" : $"{seconds:0.#}s";
        }
    }
}
