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
    /// SessionController inspector, custom-drawn top to bottom as:
    ///   - a static (non-interactive) session-flow timeline;
    ///   - Shared metrics — the meditation's three authored sections, with a
    ///     small proportional visualization;
    ///   - Timing — one Implicit/Explicit/Exploration subsection each, header
    ///     row showing the estimated length on the right;
    ///   - Physiological objectives + normalization — activation, bound K,
    ///     and a per-channel table (every DelphiManager channel, live status
    ///     dot, SD, higher-is-better);
    ///   - BO metrics — seed + the BoTorch model-fit-cost knobs;
    ///   - in Play mode, the running phase/iteration and captured bounds.
    /// </summary>
    [CustomEditor(typeof(SessionController))]
    public class SessionControllerEditor : Editor
    {
        private static readonly string[] LinkFields =
            { "manager", "carDriver", "recorder", "narration", "motionCues",
              "delphiQuestionnaire", "delphiQuestionnairePanel",
              "conditionEvaluation", "conditionEvaluationPanel" };

        // Every field re-homed into one of the custom sections below —
        // excluded from the generic top-level pass so each appears exactly
        // once. pythonPath is deliberately excluded AND never drawn anywhere:
        // hidden from this UI entirely, the field/its script default (auto-
        // detect the project venv) still works for anyone who needs it.
        private static readonly string[] RelocatedFields =
            { "meditationAcclimatisationSeconds", "meditationMeasurementSeconds", "meditationFadeoutSeconds",
              "exploreNudgeIdleSeconds", "exploreNudgeMaxAuto", "freeRoamEstimatedSeconds",
              "implicitTrial", "explicitTrial",
              "transitionSeconds", "idleSeconds", "measurementSeconds", "explicitTrialSeconds", "boProcessingEstimateSeconds",
              "activation", "boundK", "channelConfigs",
              "pythonPath", "seed", "numRestarts", "rawSamples", "mcSamples", "optimizerResponseTimeoutSeconds" };

        public override void OnInspectorGUI()
        {
            var ctrl = (SessionController)target;
            serializedObject.Update();

            var excluded = new List<string>(LinkFields);
            excluded.AddRange(RelocatedFields);
            DrawPropertiesExcluding(serializedObject, excluded.ToArray());

            if (Foldout("links", "Links (auto-found if left empty)"))
            {
                EditorGUILayout.BeginVertical("box");
                foreach (var field in LinkFields)
                    EditorGUILayout.PropertyField(serializedObject.FindProperty(field));
                EditorGUILayout.EndVertical();
            }

            DrawTimeline(ctrl);
            DrawSharedMetrics(ctrl);
            DrawTiming(ctrl);
            DrawPhysiologicalObjectives(ctrl);
            DrawBoMetrics(ctrl);
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

        /// <summary>Foldout header with a right-aligned summary on the same
        /// line (label left, estimate right) — used for the three Timing
        /// subsections.</summary>
        private static bool SubFoldout(string key, string label, string rightText)
        {
            string prefKey = "DELPHI.SessionControllerEditor." + key;
            bool value = EditorPrefs.GetBool(prefKey, true);

            Rect row = GUILayoutUtility.GetRect(0, EditorGUIUtility.singleLineHeight + 2, GUILayout.ExpandWidth(true));
            bool next = EditorGUI.Foldout(new Rect(row.x, row.y, row.width * 0.6f, row.height),
                                          value, label, true, EditorStyles.foldoutHeader);
            var rightStyle = new GUIStyle(EditorStyles.miniBoldLabel) { alignment = TextAnchor.MiddleRight };
            GUI.Label(new Rect(row.x + row.width * 0.4f, row.y, row.width * 0.6f - 4f, row.height), rightText, rightStyle);

            if (next != value) EditorPrefs.SetBool(prefKey, next);
            return next;
        }

        // ── Session-flow timeline — static, read-only ────────────────────
        // Reads the same public Timeline/CurrentStopIndex/StopProgress01 API
        // ExperimentUI's runtime timeline uses — one source of truth for what
        // the plan actually is, in the Editor before Play and live during it.
        // Every bar gets a guaranteed minimum pixel width (the widest label's
        // own measured size), so text is never clipped to a mangled
        // fragment — extra space beyond that floor is what actually varies
        // proportionally by each stop's estimated length.
        private void DrawTimeline(SessionController ctrl)
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Session timeline", EditorStyles.boldLabel);

            IReadOnlyList<SessionController.TimelineStop> stops = ctrl.Timeline;
            if (stops == null || stops.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "No timeline yet — needs a CarDriver/NarrationController linked and a valid " +
                    "condition order to estimate segment lengths.", MessageType.Info);
                return;
            }

            const float BarHeight = 30f;
            const float DetailHeight = 30f;

            var titleStyle = new GUIStyle(EditorStyles.miniBoldLabel)
                { alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } };
            var detailStyle = new GUIStyle(EditorStyles.miniLabel)
                { alignment = TextAnchor.UpperCenter, wordWrap = true };

            float minBarPx = 24f;
            float totalSeconds = 0f;
            foreach (var s in stops)
            {
                minBarPx = Mathf.Max(minBarPx, titleStyle.CalcSize(new GUIContent(s.label)).x + 10f);
                totalSeconds += s.estimatedSeconds;
            }

            Rect full = GUILayoutUtility.GetRect(0, BarHeight + DetailHeight, GUILayout.ExpandWidth(true));
            Rect barRect = new Rect(full.x, full.y, full.width, BarHeight);
            Rect detailRect = new Rect(full.x, full.y + BarHeight + 2, full.width, DetailHeight);

            float minTotalPx = minBarPx * stops.Count;
            float extraPx = Mathf.Max(0f, barRect.width - minTotalPx);
            float weightSum = 0f;
            foreach (var s in stops) weightSum += Mathf.Max(1f, s.estimatedSeconds);

            bool playing = Application.isPlaying;
            int currentStop = playing ? ctrl.CurrentStopIndex : -1;

            float x = barRect.x;
            for (int i = 0; i < stops.Count; i++)
            {
                var stop = stops[i];
                float share = Mathf.Max(1f, stop.estimatedSeconds) / Mathf.Max(1f, weightSum);
                float w = (minTotalPx > barRect.width ? barRect.width / stops.Count : minBarPx + extraPx * share);
                Rect r = new Rect(x, barRect.y, Mathf.Max(1f, w - 2f), barRect.height);

                Color baseColor = StopColor(stop);
                bool isCurrent = playing && i == currentStop;
                EditorGUI.DrawRect(r, isCurrent ? baseColor : Darken(baseColor, 0.55f));
                if (playing && stop.isCondition)
                {
                    float prog = ctrl.StopProgress01(i);
                    if (prog > 0f)
                        EditorGUI.DrawRect(new Rect(r.x, r.y, r.width * Mathf.Clamp01(prog), r.height), baseColor);
                }

                GUI.Label(r, stop.label, titleStyle);
                GUI.Label(new Rect(r.x, detailRect.y, r.width, detailRect.height),
                          $"{stop.detail}\n{Fmt(stop.estimatedSeconds)}", detailStyle);

                x += w;
            }

            EditorGUILayout.LabelField($"Estimated total: {Fmt(totalSeconds)}", EditorStyles.miniLabel);
        }

        private static Color StopColor(SessionController.TimelineStop stop)
        {
            if (!stop.isCondition) return new Color(0.45f, 0.47f, 0.53f); // neutral — Intro/End
            return stop.condition switch
            {
                SessionController.ConditionKind.Implicit => new Color32(70, 220, 160, 255),
                SessionController.ConditionKind.Explicit => new Color32(235, 170, 60, 255),
                SessionController.ConditionKind.FreeRoam => new Color32(80, 160, 235, 255),
                _ => Color.gray
            };
        }

        private static Color Darken(Color c, float factor) => new Color(c.r * factor, c.g * factor, c.b * factor, c.a);

        // ── Shared metrics — the meditation's three authored sections ──
        private void DrawSharedMetrics(SessionController ctrl)
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Shared metrics", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            EditorGUILayout.LabelField("Meditation — one audio file, three authored sections", EditorStyles.miniBoldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("meditationAcclimatisationSeconds"),
                                          new GUIContent("Acclimatisation"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("meditationMeasurementSeconds"),
                                          new GUIContent("Measurement"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("meditationFadeoutSeconds"),
                                          new GUIContent("Fadeout"));

            float acc = Mathf.Max(0f, ctrl.meditationAcclimatisationSeconds);
            float meas = Mathf.Max(0f, ctrl.meditationMeasurementSeconds);
            float fade = Mathf.Max(0f, ctrl.meditationFadeoutSeconds);
            float total = Mathf.Max(0.01f, acc + meas + fade);

            EditorGUILayout.Space(2);
            Rect row = GUILayoutUtility.GetRect(0, 22, GUILayout.ExpandWidth(true));
            float x = row.x;
            DrawProportionalSegment(ref x, row, acc / total, new Color32(140, 150, 165, 255), "Acclimatisation");
            DrawProportionalSegment(ref x, row, meas / total, new Color32(70, 220, 160, 255), "Measurement");
            DrawProportionalSegment(ref x, row, fade / total, new Color32(235, 170, 60, 255), "Fadeout");

            EditorGUILayout.LabelField($"Total meditation length: {Fmt(total)}", EditorStyles.miniLabel);
            EditorGUILayout.EndVertical();
        }

        private static void DrawProportionalSegment(ref float x, Rect row, float frac01, Color c, string label)
        {
            float w = row.width * Mathf.Clamp01(frac01);
            Rect seg = new Rect(x, row.y, Mathf.Max(1f, w - 1f), row.height);
            EditorGUI.DrawRect(seg, c);

            var style = new GUIStyle(EditorStyles.miniLabel)
                { alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } };
            if (style.CalcSize(new GUIContent(label)).x <= seg.width - 4f)
                GUI.Label(seg, label, style);
            x += w;
        }

        // ── Timing — Implicit / Explicit / Exploration ───────────────────
        private void DrawTiming(SessionController ctrl)
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Timing", EditorStyles.boldLabel);

            DrawImplicitTiming(ctrl);
            DrawExplicitTiming(ctrl);
            DrawExplorationTiming();
        }

        private void DrawImplicitTiming(SessionController ctrl)
        {
            var cfgProp = serializedObject.FindProperty("implicitTrial");
            var iterationsProp = cfgProp.FindPropertyRelative("iterations");
            var samplingProp = cfgProp.FindPropertyRelative("samplingIterations");

            float perIteration = ctrl.washoutSeconds + ctrl.measurementSeconds + ctrl.boProcessingEstimateSeconds;
            float total = iterationsProp.intValue * perIteration;

            if (!SubFoldout("timingImplicit", "Implicit", Fmt(total))) return;

            EditorGUILayout.BeginVertical("box");
            DrawExplorationExploitationSplit(iterationsProp, samplingProp);

            EditorGUILayout.Space(4);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("transitionSeconds"),
                                          new GUIContent("Transition period",
                                              "Interpolating from the current parameters to the newly-received BO ones."));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("idleSeconds"),
                                          new GUIContent("Idle",
                                              "Nothing happens — the participant just experiences the new parameters."));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("measurementSeconds"),
                                          new GUIContent("Measurement period",
                                              "Averaging each physiological input separately; the averages are what's sent to BO."));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("boProcessingEstimateSeconds"),
                                          new GUIContent("BO buffer",
                                              "Processing time for BO to return the next parameter set. Planning estimate " +
                                              "only — not a cutoff."));

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(
                $"Per iteration: {Fmt(perIteration)}  ×  {iterationsProp.intValue} iterations  =  {Fmt(total)}",
                EditorStyles.miniLabel);
            EditorGUILayout.EndVertical();
        }

        private void DrawExplicitTiming(SessionController ctrl)
        {
            var cfgProp = serializedObject.FindProperty("explicitTrial");
            var iterationsProp = cfgProp.FindPropertyRelative("iterations");
            var samplingProp = cfgProp.FindPropertyRelative("samplingIterations");

            // washout (transition + idle, both shared with Implicit) + the
            // timed Trial phase are the fixed-length parts of an Explicit
            // iteration. AwaitingRating itself is untimed (participant-
            // gated), so this total is still a floor, not the real total.
            float perIteration = ctrl.washoutSeconds + ctrl.explicitTrialSeconds;
            float total = iterationsProp.intValue * perIteration;

            if (!SubFoldout("timingExplicit", "Explicit", Fmt(total) + "+")) return;

            EditorGUILayout.BeginVertical("box");
            DrawExplorationExploitationSplit(iterationsProp, samplingProp);

            EditorGUILayout.Space(4);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("transitionSeconds"),
                                          new GUIContent("Transition period",
                                              "Morphing to the new parameters once they arrive — same field as Implicit's."));
            EditorGUILayout.LabelField(
                $"(+ {Fmt(ctrl.idleSeconds)} idle, set above under Implicit, shared)", EditorStyles.miniLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("explicitTrialSeconds"),
                                          new GUIContent("Trial period",
                                              "How long the participant drives/experiences this parameter set before " +
                                              "the simulator freezes and the questionnaire appears."));

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(
                "After the trial period: the simulator freezes and the rating questionnaire appears — untimed, " +
                "participant-gated from there. The next transition starts the moment new parameters arrive.",
                EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.LabelField(
                $"Questionnaire objective range: [{ctrl.questionnaireMin:0.#}, {ctrl.questionnaireMax:0.#}] " +
                "— derived from delphiQuestionnaire's own question steps.",
                EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(
                $"Per iteration (floor, excludes rating time): {Fmt(perIteration)}  ×  {iterationsProp.intValue} " +
                $"iterations  =  {Fmt(total)}",
                EditorStyles.miniLabel);
            EditorGUILayout.EndVertical();
        }

        private void DrawExplorationTiming()
        {
            var freeRoamProp = serializedObject.FindProperty("freeRoamEstimatedSeconds");
            if (!SubFoldout("timingExploration", "Exploration", Fmt(freeRoamProp.floatValue))) return;

            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.PropertyField(freeRoamProp, new GUIContent("Expected time"));
            EditorGUILayout.LabelField(
                "Planning estimate only, for the timeline above — Explore is open-ended by design and ends on " +
                "the researcher's DONE button in the experiment UI, never on a timer.",
                EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Explore nudge (\"try changing something\" prompt)", EditorStyles.miniBoldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("exploreNudgeIdleSeconds"),
                                          new GUIContent("Idle before auto-nudge"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("exploreNudgeMaxAuto"),
                                          new GUIContent("Max automatic nudges"));
            EditorGUILayout.EndVertical();
        }

        /// <summary>Two side-by-side int boxes — exploration (Sobol
        /// sampling) and exploitation (model-guided) — writing back to the
        /// underlying iterations/samplingIterations pair (iterations =
        /// exploration + exploitation; samplingIterations = exploration).
        /// Respects ConditionTrialConfig's own [Min] constraints
        /// (iterations >= 2, samplingIterations >= 1).</summary>
        private static void DrawExplorationExploitationSplit(SerializedProperty iterationsProp, SerializedProperty samplingProp)
        {
            int exploration = Mathf.Max(1, samplingProp.intValue);
            int exploitation = Mathf.Max(0, iterationsProp.intValue - samplingProp.intValue);

            EditorGUILayout.LabelField("Exploration / exploitation split", EditorStyles.miniBoldLabel);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Exploration", GUILayout.Width(90));
            int newExploration = Mathf.Max(1, EditorGUILayout.IntField(exploration, GUILayout.Width(50)));
            GUILayout.Space(12);
            EditorGUILayout.LabelField("Exploitation", GUILayout.Width(90));
            int newExploitation = Mathf.Max(0, EditorGUILayout.IntField(exploitation, GUILayout.Width(50)));
            EditorGUILayout.EndHorizontal();

            if (newExploration != exploration || newExploitation != exploitation)
            {
                samplingProp.intValue = newExploration;
                iterationsProp.intValue = Mathf.Max(2, newExploration + newExploitation);
            }
        }

        // ── Physiological objectives + normalization ─────────────────────
        private void DrawPhysiologicalObjectives(SessionController ctrl)
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Physiological objectives + normalization", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            EditorGUILayout.PropertyField(serializedObject.FindProperty("activation"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("boundK"),
                new GUIContent("Bound K",
                    "Bound half-width in standard deviations: each channel's objective bounds are " +
                    "baseline ± K·SD, and a measurement window's deviation reaches ±1 (fully saturated) right " +
                    "at that edge. Higher = a wider, more forgiving range before the objective saturates."));

            EditorGUILayout.Space(6);
            if (ctrl.manager == null)
            {
                EditorGUILayout.HelpBox(
                    "Plug a DelphiManager into 'Manager' (Links above) — the channel list comes from it.",
                    MessageType.Info);
                EditorGUILayout.EndVertical();
                return;
            }

            var allChannels = new List<Channel>(DelphiManager.AllChannels);
            EnsureRows(allChannels);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(16); // status-dot column
            EditorGUILayout.LabelField("Channel", EditorStyles.miniBoldLabel, GUILayout.Width(150));
            EditorGUILayout.LabelField("SD (native)", EditorStyles.miniBoldLabel, GUILayout.Width(90));
            EditorGUILayout.LabelField("Higher is better", EditorStyles.miniBoldLabel);
            EditorGUILayout.EndHorizontal();

            var listProp = serializedObject.FindProperty("channelConfigs");
            foreach (var ch in allChannels)
            {
                var el = FindRow(listProp, ch);
                if (el == null) continue;
                var sd = el.FindPropertyRelative("sd");
                var hib = el.FindPropertyRelative("higherIsBetter");

                EditorGUILayout.BeginHorizontal();
                var dotRect = GUILayoutUtility.GetRect(14, 16, GUILayout.Width(14));
                dotRect.y += 3; dotRect.width = dotRect.height = 10;
                EditorGUI.DrawRect(dotRect, StatusColor(ctrl.manager.GetStatus(ch)));

                var (label, unit) = DelphiManager.Meta(ch);
                EditorGUILayout.LabelField(string.IsNullOrEmpty(unit) ? label : $"{label} ({unit})",
                                           GUILayout.Width(150));
                sd.floatValue = Mathf.Max(1e-6f, EditorGUILayout.FloatField(sd.floatValue, GUILayout.Width(90)));
                hib.boolValue = EditorGUILayout.Toggle(hib.boolValue);
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField(
                "Dot = live / no signal / disabled / not attached (DelphiManager). SD = population native-unit " +
                "spread from literature (placeholders until you set them). Bounds = baseline ± K·SD. 'Higher is " +
                "better' ON = dropping below baseline is penalized (RMSSD); OFF = rising above is (HR, GSR). " +
                "Explicit/Questionnaire trials don't use this table — see the questionnaire objective range " +
                "under Explicit timing instead.",
                EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.EndVertical();
        }

        private static Color StatusColor(ChannelStatus status) => status switch
        {
            ChannelStatus.Live => new Color(0.35f, 0.85f, 0.50f),
            ChannelStatus.NoSignal => new Color(0.85f, 0.30f, 0.30f),
            ChannelStatus.Disabled => new Color(0.85f, 0.75f, 0.25f),
            _ => new Color(0.55f, 0.55f, 0.58f)
        };

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

        // ── BO metrics ────────────────────────────────────────────────────
        private void DrawBoMetrics(SessionController ctrl)
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("BO metrics", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            EditorGUILayout.PropertyField(serializedObject.FindProperty("seed"));

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Model-fit cost (every iteration past sampling)", EditorStyles.miniBoldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("numRestarts"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("rawSamples"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("mcSamples"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("optimizerResponseTimeoutSeconds"));

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
            var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}:{t.Seconds:00} min" : $"{seconds:0.#}s";
        }
    }
}
