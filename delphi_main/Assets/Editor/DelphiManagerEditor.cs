using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Delphi.EditorTools
{
    /// <summary>
    /// Replaces the raw checkbox + object-field list Unity would draw by
    /// default with a patch-bay style row per input: an ON/OFF chip, the
    /// sensor slot, and a live status dot using the same colours as
    /// DashboardUI (gray/yellow/red/green). Purely cosmetic — all the
    /// actual logic lives on DelphiManager itself.
    /// </summary>
    [CustomEditor(typeof(DelphiManager))]
    public class DelphiManagerEditor : Editor
    {
        private struct Row
        {
            public string label, onProp, sensorProp, rateProp;
            public Row(string label, string onProp, string sensorProp, string rateProp = null)
            { this.label = label; this.onProp = onProp; this.sensorProp = sensorProp; this.rateProp = rateProp; }
        }

        // rateProp is the group's shared Hz field (drawn on the header line,
        // right-aligned) — every sensor in a group samples at that one rate.
        private static readonly (string header, string rateProp, Row[] rows)[] ScalarGroups =
        {
            ("Gold-standard inputs", "goldStandardRateHz", new[]
            {
                new Row("Heart rate",     "heartRateOn",      "heartRate"),
                new Row("HRV (RMSSD)",    "hrvRmssdOn",        "hrvRmssd"),
                new Row("Resp. rate",     "respRateOn",        "respRate"),
                new Row("GSR",            "gsrOn",             "gsr"),
            }),
            ("Good additions", "goodAdditionsRateHz", new[]
            {
                new Row("Blink rate",     "blinkRateOn",       "blinkRate"),
                new Row("Gaze / Saccade", "gazeOn",            "gaze"),
                new Row("Pupil diameter", "pupilDiameterOn",   "pupilDiameter"),
            }),
            ("Experimental", "experimentalRateHz", new[]
            {
                new Row("EEG",            "eegOn",             "eeg"),
                new Row("Facial affect",   "facialOn",          "facial"),
            }),
        };

        // Frame feeds each carry their own FPS field — on the MANAGER, like
        // every other rate in DELPHI. Sensors themselves have no clocks.
        private static readonly Row[] FrameRows =
        {
            new Row("Webcam",         "webcamOn",        "webcam",        "webcamFps"),
            new Row("Scene overview", "sceneOverviewOn", "sceneOverview", "sceneOverviewFps"),
            new Row("Player view",    "playerViewOn",    "playerView",    "playerViewFps"),
        };

        private static readonly Dictionary<string, Channel> ChannelByProp = new()
        {
            { "heartRate", Channel.HeartRate }, { "hrvRmssd", Channel.RMSSD },
            { "respRate", Channel.RespRate },   { "gsr", Channel.GSR },
            { "blinkRate", Channel.BlinkRate }, { "gaze", Channel.Gaze },
            { "pupilDiameter", Channel.PupilDiameter },
            { "eeg", Channel.EEG },             { "facial", Channel.Facial },
        };

        private static readonly Dictionary<string, FrameChannel> FrameChannelByProp = new()
        {
            { "webcam", FrameChannel.Webcam },
            { "sceneOverview", FrameChannel.SceneOverview },
            { "playerView", FrameChannel.PlayerView },
        };

        private static readonly Color OnChip     = new Color(0.30f, 0.55f, 0.35f);
        private static readonly Color OffChip    = new Color(0.35f, 0.35f, 0.38f);
        private static readonly Color DotLive        = new Color(0.35f, 0.85f, 0.50f);
        private static readonly Color DotNoSignal     = new Color(0.85f, 0.30f, 0.30f);
        private static readonly Color DotDisabled     = new Color(0.85f, 0.75f, 0.25f);
        private static readonly Color DotNotAttached  = new Color(0.55f, 0.55f, 0.58f);

        // Repaint every frame in Play mode so status dots track live data.
        public override bool RequiresConstantRepaint() => Application.isPlaying;

        public override void OnInspectorGUI()
        {
            var mgr = (DelphiManager)target;
            serializedObject.Update();

            EditorGUILayout.Space(2);
            DrawLegend();
            EditorGUILayout.Space(6);

            foreach (var (header, rateProp, rows) in ScalarGroups)
            {
                DrawGroupHeader(header, rateProp);
                EditorGUILayout.BeginVertical("box");
                foreach (var row in rows)
                    DrawRow(row.label, row.onProp, row.sensorProp,
                            mgr.GetStatus(ChannelByProp[row.sensorProp]));
                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(4);
            }

            EditorGUILayout.LabelField("Video / frame inputs", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");
            foreach (var row in FrameRows)
                DrawRow(row.label, row.onProp, row.sensorProp,
                        mgr.GetStatus(FrameChannelByProp[row.sensorProp]), row.rateProp);
            EditorGUILayout.EndVertical();

            serializedObject.ApplyModifiedProperties();
        }

        // Header line for a scalar group, with that group's shared Hz field
        // right-aligned on the same line — every sensor below it samples at
        // this one rate (unlike frame inputs, which each get their own Hz
        // per row since their costs differ too much to share).
        //
        // Explicit Rect placement rather than GUILayout auto-layout (as used
        // for the legend below) — mixing FlexibleSpace with an unconstrained
        // label here was overflowing/duplicating in a narrow Inspector.
        private void DrawGroupHeader(string header, string rateFieldName)
        {
            Rect row = GUILayoutUtility.GetRect(0, EditorGUIUtility.singleLineHeight,
                                                GUILayout.ExpandWidth(true));

            // Right-hand block: "Hz" label + a compact number box. A plain
            // FloatField, NOT PropertyField — the field carries a [Range]
            // attribute, so PropertyField would draw a slider that collapses
            // to an unreadable sliver at this width. Clamp the value here to
            // keep the same 1–240 bounds the slider enforced.
            const float fieldWidth = 46f, hzLabelWidth = 20f, gap = 4f;
            float rightBlock = hzLabelWidth + fieldWidth;

            var headerRect = new Rect(row.x, row.y, row.width - rightBlock - gap, row.height);
            EditorGUI.LabelField(headerRect, header, EditorStyles.boldLabel);

            var hzLabelRect = new Rect(row.xMax - rightBlock, row.y, hzLabelWidth, row.height);
            EditorGUI.LabelField(hzLabelRect, "Hz");

            var fieldRect = new Rect(row.xMax - fieldWidth, row.y, fieldWidth, row.height);
            var rateProp = serializedObject.FindProperty(rateFieldName);
            rateProp.floatValue = Mathf.Clamp(
                EditorGUI.FloatField(fieldRect, rateProp.floatValue), 1f, 240f);
        }

        private void DrawLegend()
        {
            float rowHeight = EditorGUIUtility.singleLineHeight;
            Rect row = GUILayoutUtility.GetRect(0, rowHeight, GUILayout.ExpandWidth(true));

            float x = row.x;
            x = DrawLegendLabel(row, x, "Status:", 46, EditorStyles.boldLabel);
            x = DrawLegendDot(row, x, DotNotAttached, "not attached", 82);
            x = DrawLegendDot(row, x, DotDisabled, "disabled", 62);
            x = DrawLegendDot(row, x, DotNoSignal, "no signal", 68);
            DrawLegendDot(row, x, DotLive, "live", 40);
        }

        // Draws a colour dot vertically centred on `row`, then its label
        // immediately after with a fixed gap, and returns the x cursor for
        // whatever comes next — this is what keeps every legend entry's dot
        // and text aligned to the same baseline instead of drifting.
        private float DrawLegendDot(Rect row, float x, Color c, string label, float labelWidth)
        {
            const float dotSize = 10f, gapAfterDot = 6f, gapAfterLabel = 10f;
            var dotRect = new Rect(x, row.y + (row.height - dotSize) / 2f, dotSize, dotSize);
            EditorGUI.DrawRect(dotRect, c);
            x += dotSize + gapAfterDot;
            var labelRect = new Rect(x, row.y, labelWidth, row.height);
            EditorGUI.LabelField(labelRect, label);
            return x + labelWidth + gapAfterLabel;
        }

        private float DrawLegendLabel(Rect row, float x, string label, float labelWidth, GUIStyle style)
        {
            var labelRect = new Rect(x, row.y, labelWidth, row.height);
            EditorGUI.LabelField(labelRect, label, style);
            return x + labelWidth;
        }

        private void DrawRow(string label, string onPropName, string sensorPropName,
                             ChannelStatus status, string ratePropName = null)
        {
            var onProp     = serializedObject.FindProperty(onPropName);
            var sensorProp = serializedObject.FindProperty(sensorPropName);
            bool on = onProp.boolValue;

            EditorGUILayout.BeginHorizontal();

            Color prevBg = GUI.backgroundColor;
            GUI.backgroundColor = on ? OnChip : OffChip;
            if (GUILayout.Button(on ? "ON" : "OFF", GUILayout.Width(38), GUILayout.Height(18)))
                onProp.boolValue = !on;
            GUI.backgroundColor = prevBg;

            GUILayout.Label(label, GUILayout.Width(120));
            EditorGUILayout.PropertyField(sensorProp, GUIContent.none, GUILayout.MinWidth(80));

            // Frame feeds: per-feed FPS, a field on the MANAGER itself —
            // sensors carry no rate fields at all.
            if (ratePropName != null)
            {
                var rateProp = serializedObject.FindProperty(ratePropName);
                float prevLabelWidth = EditorGUIUtility.labelWidth;
                EditorGUIUtility.labelWidth = 32;
                EditorGUILayout.PropertyField(rateProp, new GUIContent("FPS"), GUILayout.Width(86));
                EditorGUIUtility.labelWidth = prevLabelWidth;
            }

            var dotRect = GUILayoutUtility.GetRect(14, 18, GUILayout.Width(14));
            dotRect.y += 4; dotRect.width = dotRect.height = 10;
            EditorGUI.DrawRect(dotRect, status switch
            {
                ChannelStatus.Live     => DotLive,
                ChannelStatus.NoSignal => DotNoSignal,
                ChannelStatus.Disabled => DotDisabled,
                _                      => DotNotAttached
            });

            EditorGUILayout.EndHorizontal();
        }
    }
}
