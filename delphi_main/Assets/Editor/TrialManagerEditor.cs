using System;
using UnityEditor;
using UnityEngine;
using Delphi;
using Delphi.Trial;

namespace Delphi.EditorTools
{
    /// <summary>
    /// Default TrialManager inspector plus a live timing panel:
    ///   - total trial length = baseline + iterations × window, recomputed
    ///     as the numbers are edited,
    ///   - the effective baseline averaging span (auto-raised to channel
    ///     minimums),
    ///   - a warning per attached channel whose minimum meaningful window
    ///     exceeds the strategy's measurement span.
    /// </summary>
    [CustomEditor(typeof(TrialManager))]
    public class TrialManagerEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var trial = (TrialManager)target;
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
            EditorGUILayout.LabelField(
                $"Baseline averaging: last {Fmt(trial.EffectiveBaselineAveragingSeconds)}" +
                (trial.EffectiveBaselineAveragingSeconds > trial.baselineAveragingSeconds
                    ? $" (raised from {Fmt(trial.baselineAveragingSeconds)} by channel minimums)"
                    : ""));

            int sampling = Mathf.Clamp(trial.samplingIterations, 1, trial.iterations - 1);
            EditorGUILayout.LabelField(
                $"Budget: {sampling} Sobol exploration + {trial.iterations - sampling} model-guided");

            EditorGUILayout.EndVertical();

            // Per-channel minimum-window checks (only meaningful in Play
            // mode, when sensor attachment is known; in edit mode, check
            // every channel so the numbers are still visible).
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Per-measure minimum windows", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            var channels = Application.isPlaying && trial.manager != null
                ? trial.CandidateChannels()
                : new System.Collections.Generic.List<Channel>(DelphiManager.AllChannels);
            bool anyWarning = false;
            foreach (var ch in channels)
            {
                float min = TrialObjectiveInfo.MinWindowSeconds(ch);
                if (min > measure)
                {
                    anyWarning = true;
                    EditorGUILayout.HelpBox(
                        $"{ch}: needs ≥{Fmt(min)} of measurement, but the window " +
                        $"only measures {Fmt(measure)} — its per-window mean will be unreliable.",
                        MessageType.Warning);
                }
                else
                {
                    EditorGUILayout.LabelField($"{ch}: min {Fmt(min)}  ✓");
                }
            }
            if (!anyWarning && !Application.isPlaying)
                EditorGUILayout.LabelField(
                    "(checked against every channel — in Play mode only attached ones are listed)",
                    EditorStyles.miniLabel);

            EditorGUILayout.EndVertical();

            if (Application.isPlaying)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField(
                    $"State: {trial.State}   Iteration {trial.Iteration}/{trial.iterations}   {trial.StatusLine}");
                Repaint();
            }
        }

        private static string Fmt(double seconds)
        {
            var t = TimeSpan.FromSeconds(seconds);
            return t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}:{t.Seconds:00} min" : $"{seconds:0.#}s";
        }
    }
}
