using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Delphi;
using Delphi.Trial;

namespace Delphi.EditorTools
{
    /// <summary>
    /// TrialManager inspector, custom-drawn so the normalization is legible:
    ///   - the standard fields (minus the raw channelConfigs list),
    ///   - a PER-CHANNEL normalization table driven by the plugged-in
    ///     DelphiManager's enabled channels — one row each with the literature
    ///     SD and the "higher is better" bad-direction toggle,
    ///   - a live trial-timing summary (baseline + iterations × window, plus the
    ///     parameter ramp's effective length),
    ///   - in Play mode, the captured baseline + native-unit bounds per
    ///     objective channel, and the running state.
    /// </summary>
    [CustomEditor(typeof(TrialManager))]
    public class TrialManagerEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            var trial = (TrialManager)target;
            serializedObject.Update();

            // Everything except the config list (drawn as a table below).
            DrawPropertiesExcluding(serializedObject, "channelConfigs");

            DrawNormalizationTable(trial);
            DrawTimingPanel(trial);
            DrawLivePanel(trial);

            serializedObject.ApplyModifiedProperties();
        }

        // ── Per-channel normalization table ─────────────────────────────
        private void DrawNormalizationTable(TrialManager trial)
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Per-channel normalization", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            if (trial.manager == null)
            {
                EditorGUILayout.HelpBox(
                    "Plug a DelphiManager into 'Manager' above (or leave it for " +
                    "auto-find in Play mode) — the channel list comes from its " +
                    "enabled sensors.", MessageType.Info);
                EditorGUILayout.EndVertical();
                return;
            }

            // In Play mode we know exactly which channels are attached+enabled;
            // in edit mode show all so every SD is reachable.
            List<Channel> channels = Application.isPlaying
                ? trial.CandidateChannels()
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
                if (el == null) continue; // EnsureRows should have made it
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
                "above is (HR, GSR).", EditorStyles.miniLabel);

            EditorGUILayout.EndVertical();
        }

        /// <summary>Add a config row (serialized, undoable) for any channel that
        /// lacks one, seeded with placeholder defaults.</summary>
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

        // ── Timing summary ──────────────────────────────────────────────
        private void DrawTimingPanel(TrialManager trial)
        {
            var strategy = trial.windowStrategy != null
                ? trial.windowStrategy
                : FindFirstObjectByType<TrialWindowStrategy>();

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Trial timing", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            if (strategy == null)
            {
                EditorGUILayout.HelpBox(
                    "No TrialWindowStrategy found — add a FixedTrialWindow " +
                    "component (same GameObject is fine).", MessageType.Error);
                EditorGUILayout.EndVertical();
                return;
            }

            float window  = strategy.WindowSeconds;
            float washout = strategy.WashoutSeconds;
            float measure = strategy.MeasureSeconds;
            double driveSeconds = (double)trial.iterations * window;
            double totalSeconds = trial.baselineSeconds + driveSeconds;

            EditorGUILayout.LabelField(
                $"Baseline {Fmt(trial.baselineSeconds)}  +  " +
                $"{trial.iterations} × {Fmt(window)}  =  {Fmt(totalSeconds)} total",
                EditorStyles.largeLabel);
            EditorGUILayout.LabelField(
                $"Per iteration: {Fmt(washout)} washout + {Fmt(measure)} measured");

            float effectiveTransition = Mathf.Min(trial.transitionSeconds, washout);
            EditorGUILayout.LabelField(
                $"Parameter ramp: {Fmt(effectiveTransition)} of the washout" +
                (trial.transitionSeconds > washout
                    ? $" (clamped from {Fmt(trial.transitionSeconds)} — exceeds washout)"
                    : ""));
            EditorGUILayout.LabelField(
                $"Baseline averaging: last {Fmt(trial.baselineAveragingSeconds)}");

            int sampling = Mathf.Clamp(trial.samplingIterations, 1, Mathf.Max(1, trial.iterations - 1));
            EditorGUILayout.LabelField(
                $"Budget: {sampling} Sobol exploration + {trial.iterations - sampling} model-guided");

            EditorGUILayout.EndVertical();
        }

        // ── Live (Play mode) ────────────────────────────────────────────
        private void DrawLivePanel(TrialManager trial)
        {
            if (!Application.isPlaying) return;

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(
                $"State: {trial.State}   Iteration {trial.Iteration}/{trial.TotalIterations}   {trial.StatusLine}");

            if (trial.ObjectiveChannels != null && trial.ObjectiveChannels.Count > 0)
            {
                EditorGUILayout.BeginVertical("box");
                EditorGUILayout.LabelField(
                    $"Captured bounds (activation {trial.ActivationFn}):", EditorStyles.miniBoldLabel);
                foreach (var ch in trial.ObjectiveChannels)
                {
                    if (trial.TryGetBounds(ch, out float lo, out float hi))
                        EditorGUILayout.LabelField($"{ch}: [{lo:F2}, {hi:F2}]", EditorStyles.miniLabel);
                }
                EditorGUILayout.EndVertical();
            }

            Repaint();
        }

        private static string Fmt(double seconds)
        {
            var t = TimeSpan.FromSeconds(seconds);
            return t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}:{t.Seconds:00} min" : $"{seconds:0.#}s";
        }
    }
}
