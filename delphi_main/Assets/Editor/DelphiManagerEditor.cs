using System;
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
            public string label, onProp, sensorProp, rateProp, tooltip;
            // tooltip is optional and comes AFTER rateProp so the frame rows
            // below (which pass a rateProp positionally) keep working unchanged.
            public Row(string label, string onProp, string sensorProp,
                       string rateProp = null, string tooltip = null)
            {
                this.label = label; this.onProp = onProp; this.sensorProp = sensorProp;
                this.rateProp = rateProp; this.tooltip = tooltip;
            }
        }

        // rateProp is the group's shared Hz field (drawn on the header line,
        // right-aligned) — every sensor in a group samples at that one rate.
        // Each row's tooltip explains HOW that measure is calculated (hover the
        // label in the Inspector) — cross-referenced against the sensor code.
        private static readonly (string header, string rateProp, Row[] rows)[] ScalarGroups =
        {
            ("Contact sensors", "contactRateHz", new[]
            {
                new Row("Heart rate", "heartRateOn", "heartRate", tooltip:
                    "Heart rate (bpm). Detected on the Polar H10 chest strap itself and " +
                    "relayed over OSC (/PolarH10/HR) — DELPHI passes it through unchanged."),
                new Row("HRV (RMSSD)", "hrvRmssdOn", "hrvRmssd", tooltip:
                    "Root-mean-square of successive RR-interval differences over the last " +
                    "~12 beats, from the strap's beat-to-beat RR intervals (/PolarH10/RR)."),
                new Row("Resp. rate", "respRateOn", "respRate", tooltip:
                    "Respiration rate (breaths/min). No respiration sensor is wired yet — " +
                    "currently a placeholder / mock signal."),
                new Row("GSR (raw)", "gsrOn", "gsr", tooltip:
                    "Raw 0–1023 ADC value from the Grove GSR sensor over serial, " +
                    "uncalibrated (higher = more skin conductance / arousal)."),
                new Row("GSR tonic", "gsrTonicOn", "gsrTonic", tooltip:
                    "Skin-conductance LEVEL (SCL): the slow < 0.05 Hz component of the raw " +
                    "GSR (cleaned at 3 Hz first). Same band split as NeuroKit2's eda_phasic; " +
                    "computed live with a causal filter (GSRTonicSensor)."),
                new Row("GSR phasic", "gsrPhasicOn", "gsrPhasic", tooltip:
                    "Skin-conductance RESPONSE (SCR): the fast 0.05–3 Hz band (cleaned − " +
                    "tonic) — arousal-driven bursts. NeuroKit2-aligned cutoffs, live causal " +
                    "filter; run recorded raw GSR through NeuroKit2 for the definitive values."),
            }),
            ("Gaze Metrics", "gazeRateHz", new[]
            {
                new Row("Inter-blink interval", "blinkIntervalOn", "blinkInterval", tooltip:
                    "Seconds between consecutive blinks — a CONTINUOUS measure (updated on " +
                    "each blink, held in between), not a windowed blink-per-minute count. No " +
                    "eye tracker is wired yet — placeholder / mock signal."),
                new Row("Gaze distance", "gazeOn", "gaze", tooltip:
                    "Distance of the current gaze point from the participant's baseline " +
                    "fixation point (degrees of visual angle) — a CONTINUOUS per-sample " +
                    "deviation, no windowing. No eye tracker is wired yet — placeholder / mock."),
                new Row("Pupil diameter", "pupilDiameterOn", "pupilDiameter", tooltip:
                    "Pupil diameter (mm). No eye tracker is wired yet — placeholder / mock signal."),
            }),
            ("IMU / Accelerometer", "imuRateHz", new[]
            {
                new Row("Acc X", "accXOn", "accX", tooltip:
                    "Raw Polar H10 accelerometer, X axis, in milli-g, streamed over OSC at " +
                    "200 Hz — passed through unchanged."),
                new Row("Acc Y", "accYOn", "accY", tooltip:
                    "Raw Polar H10 accelerometer, Y axis, in milli-g, streamed over OSC at " +
                    "200 Hz — passed through unchanged."),
                new Row("Acc Z", "accZOn", "accZ", tooltip:
                    "Raw Polar H10 accelerometer, Z axis, in milli-g, streamed over OSC at " +
                    "200 Hz — passed through unchanged."),
            }),
        };

        // Frame feeds each carry their own FPS field — on the MANAGER, like
        // every other rate in DELPHI. Sensors themselves have no clocks.
        private static readonly Row[] FrameRows =
        {
            new Row("Webcam",         "webcamOn",        "webcam",        "webcamFps"),
            new Row("Track overview", "trackOverviewOn", "trackOverview", "trackOverviewFps"),
            new Row("Player view",    "playerViewOn",    "playerView",    "playerViewFps"),
            new Row("360° environment", "panorama360On", "panorama360",  "panorama360Fps"),
            new Row("Eye cameras",    "eyeCamerasOn",    "eyeCameras",    "eyeCamerasFps"),
        };

        private static readonly Dictionary<string, Channel> ChannelByProp = new()
        {
            { "heartRate", Channel.HeartRate }, { "hrvRmssd", Channel.RMSSD },
            { "respRate", Channel.RespRate },   { "gsr", Channel.GSR },
            { "gsrTonic", Channel.GsrTonic },   { "gsrPhasic", Channel.GsrPhasic },
            { "blinkInterval", Channel.InterBlinkInterval }, { "gaze", Channel.Gaze },
            { "pupilDiameter", Channel.PupilDiameter },
            { "accX", Channel.AccX },           { "accY", Channel.AccY },
            { "accZ", Channel.AccZ },
        };

        private static readonly Dictionary<string, FrameChannel> FrameChannelByProp = new()
        {
            { "webcam", FrameChannel.Webcam },
            { "trackOverview", FrameChannel.TrackOverview },
            { "playerView", FrameChannel.PlayerView },
            { "panorama360", FrameChannel.Panorama360 },
            { "eyeCameras", FrameChannel.EyeCameras },
        };

        private static readonly Color OnChip     = new Color(0.20f, 0.85f, 0.35f);
        private static readonly Color OffChip    = new Color(0.35f, 0.35f, 0.38f);
        private static readonly Color DotLive        = new Color(0.35f, 0.85f, 0.50f);
        private static readonly Color DotNoSignal     = new Color(0.85f, 0.30f, 0.30f);
        private static readonly Color DotDisabled     = new Color(0.85f, 0.75f, 0.25f);
        private static readonly Color DotNotAttached  = new Color(0.55f, 0.55f, 0.58f);
        private static readonly Color StrikeThrough   = Color.white;

        // Repaint every frame in Play mode so status dots track live data.
        public override bool RequiresConstantRepaint() => Application.isPlaying;

        public override void OnInspectorGUI()
        {
            var mgr = (DelphiManager)target;
            serializedObject.Update();

            DrawDebugSection(mgr);

            EditorGUILayout.Space(2);
            DrawLegend();
            EditorGUILayout.Space(10);

            foreach (var (header, rateProp, rows) in ScalarGroups)
            {
                DrawGroupHeader(header, rateProp);
                EditorGUILayout.BeginVertical("box");
                foreach (var row in rows)
                    // Lazy lookup, not a pre-computed value: evaluated INSIDE
                    // DrawRow, after that row's own click (if any) has already
                    // been pushed to the live target — see DrawRow's comment.
                    DrawRow(row.label, row.onProp, row.sensorProp,
                            () => mgr.GetStatus(ChannelByProp[row.sensorProp]),
                            tooltip: row.tooltip);
                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(14);
            }

            EditorGUILayout.LabelField("Video / frame inputs", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");
            foreach (var row in FrameRows)
                DrawRow(row.label, row.onProp, row.sensorProp,
                        () => mgr.GetStatus(FrameChannelByProp[row.sensorProp]), row.rateProp);
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

        // Debug Constant Feed toggle. Driven here (not as a plain field) so
        // flipping it fires DelphiManager.SetDebugConstantFeed, which snapshots
        // + clears every channel toggle on the way in and restores them on the
        // way out. After that direct-field mutation we re-Update the serialized
        // buffer so the group rows below redraw with the cleared/restored state.
        private void DrawDebugSection(DelphiManager mgr)
        {
            EditorGUILayout.BeginVertical("box");

            bool dbg = mgr.DebugConstantFeed;
            bool newDbg = EditorGUILayout.ToggleLeft(
                " DEBUG — feed a constant to ALL metrics (run with no hardware)",
                dbg, EditorStyles.boldLabel);
            if (newDbg != dbg)
            {
                Undo.RecordObject(mgr, "Toggle Debug Constant Feed");
                mgr.SetDebugConstantFeed(newDbg);
                EditorUtility.SetDirty(mgr);
                serializedObject.Update(); // resync after direct field edits
            }

            if (mgr.DebugConstantFeed)
            {
                EditorGUILayout.PropertyField(
                    serializedObject.FindProperty("debugValue"),
                    new GUIContent("Constant value", "Fed to every metric, and into " +
                                   "the BO baseline/windows, while debug is on."));
                EditorGUILayout.HelpBox(
                    "All channel toggles are forced OFF and every metric reports this " +
                    "constant, so a full session / BO loop runs with no sensors attached. " +
                    "Turn this off to restore your previous ON/OFF toggles.",
                    MessageType.Info);
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(8);
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
                             Func<ChannelStatus> getStatus, string ratePropName = null,
                             string tooltip = null)
        {
            var onProp     = serializedObject.FindProperty(onPropName);
            var sensorProp = serializedObject.FindProperty(sensorPropName);
            bool on = onProp.boolValue;

            EditorGUILayout.BeginHorizontal();

            Color prevBg = GUI.backgroundColor;
            GUI.backgroundColor = on ? OnChip : OffChip;
            if (GUILayout.Button(on ? "ON" : "OFF", GUILayout.Width(38), GUILayout.Height(18)))
            {
                onProp.boolValue = !on;
                on = !on;
                // Push straight to the live DelphiManager NOW rather than
                // waiting for OnInspectorGUI's single ApplyModifiedProperties
                // at the very end — getStatus() below reads mgr.GetStatus(...),
                // which resolves off the ACTUAL field, not the SerializedObject
                // buffer. Without this, the dot would show last redraw's status
                // for one more pass even though the label/strike-through above
                // (driven by the local `on`, already flipped) look instantly
                // correct — a mismatch that's exactly what looked like "the dot
                // doesn't update" even after the button/label started working.
                serializedObject.ApplyModifiedProperties();
            }
            GUI.backgroundColor = prevBg;

            const float labelWidth = 120f;
            Rect labelRect = GUILayoutUtility.GetRect(labelWidth, EditorGUIUtility.singleLineHeight,
                                                       GUILayout.Width(labelWidth));
            // GUIContent carries the tooltip — hovering the label shows how the
            // measure is calculated. null tooltip = no hover text (frame rows).
            EditorGUI.LabelField(labelRect, new GUIContent(label, tooltip));
            if (!on)
            {
                // No strikethrough FontStyle in IMGUI — draw the line by hand,
                // sized to the label's actual rendered width so it doesn't
                // trail off into the empty rest of the 120px column.
                float textWidth = Mathf.Min(labelWidth, GUI.skin.label.CalcSize(new GUIContent(label)).x);
                var strikeRect = new Rect(labelRect.x, labelRect.y + labelRect.height / 2f, textWidth, 1f);
                EditorGUI.DrawRect(strikeRect, StrikeThrough);
            }

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
            EditorGUI.DrawRect(dotRect, getStatus() switch
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
