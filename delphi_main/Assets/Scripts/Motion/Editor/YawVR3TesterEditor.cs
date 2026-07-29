using System;
using UnityEditor;
using UnityEngine;

namespace Delphi.Motion.Editor
{
    [CustomEditor(typeof(YawVR3Tester))]
    public class YawVR3TesterEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var tester = (YawVR3Tester)target;
            var connection = tester.connection;

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Manual Test Controls", EditorStyles.boldLabel);

            string stateLabel = connection == null ? "No YawVR3Connection found in scene" : $"{connection.State}: {connection.StatusText}";
            EditorGUILayout.HelpBox(stateLabel, MessageType.None);

            EditorGUILayout.LabelField($"Manual mode: {(tester.ManualModeActive ? "ACTIVE" : "inactive")}");
            EditorGUILayout.LabelField($"Commanded — Yaw {tester.Yaw:0.#}°  Pitch {tester.Pitch:0.#}°  Roll {tester.Roll:0.#}°");

            EditorGUILayout.Space(6);
            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox("Enter Play mode to test — nothing can be sent while the Editor isn't running.", MessageType.None);
                return;
            }

            // START/STOP moved OUT of the manual-angle scope: it's the
            // transport, and both benches below need it. Gating it behind
            // manual ANGLE mode meant you couldn't start the rig to test the
            // buzzers without first putting it into a tilt override.
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(connection == null || connection.State != YawConnectionState.Connected))
            {
                if (GUILayout.Button("START MOTION", GUILayout.Height(28))) tester.StartMotion();
            }
            using (new EditorGUI.DisabledScope(connection == null || connection.State != YawConnectionState.Started))
            {
                var prevColor = GUI.backgroundColor;
                GUI.backgroundColor = new Color(0.9f, 0.4f, 0.35f);
                if (GUILayout.Button("STOP MOTION", GUILayout.Height(28))) tester.StopMotion();
                GUI.backgroundColor = prevColor;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(8);
            using (new EditorGUI.DisabledScope(tester.ManualModeActive))
            {
                if (GUILayout.Button("ENTER MANUAL TEST MODE (angles)", GUILayout.Height(28)))
                    tester.EnterManualMode();
            }

            using (new EditorGUI.DisabledScope(!tester.ManualModeActive))
            {
                EditorGUILayout.Space(4);
                DrawAxisRow(tester, "Yaw", tester.NudgeYaw);
                DrawAxisRow(tester, "Pitch", tester.NudgePitch);
                DrawAxisRow(tester, "Roll", tester.NudgeRoll);

                EditorGUILayout.Space(6);
                if (GUILayout.Button("RESET TO LEVEL (0,0,0)", GUILayout.Height(24)))
                    tester.ResetToLevel();

                EditorGUILayout.Space(10);
                if (GUILayout.Button("Exit Manual Test Mode (hand back to CarMotionCues)"))
                    tester.ExitManualMode();
            }

            DrawRumbleBench(tester, connection);

            Repaint(); // live-update the status/commanded readout while Started
        }

        // ════════════════════════════════════════════════════════════════
        //  Rumble bench — deliberately usable WITHOUT entering manual angle
        //  mode. Measuring the buzzers should never require tilting the rig.
        // ════════════════════════════════════════════════════════════════
        private static void DrawRumbleBench(YawVR3Tester tester, YawVR3Connection connection)
        {
            EditorGUILayout.Space(14);
            EditorGUILayout.LabelField("Rumble Bench", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Measures the two constants CarRumbleCues can't guess: the intensity floor " +
                "(below which the motors don't turn over) and the usable Hz window. Run a " +
                "sweep, press MARK the moment you feel the change, then copy the recorded " +
                "numbers into CarRumbleCues.", MessageType.Info);

            // The commonest way to waste a bench session: sweep the whole
            // range feeling nothing, because the vibration field only travels
            // inside the motion packet and that only goes out while Started.
            if (connection == null || connection.State != YawConnectionState.Started)
                EditorGUILayout.HelpBox(
                    "The rig is not STARTED — nothing is being sent, so the bench will feel " +
                    "like dead silence no matter what you set. Start motion first (the button " +
                    "above, or the researcher UI).", MessageType.Warning);

            using (new EditorGUI.DisabledScope(tester.RumbleTestActive))
            {
                if (GUILayout.Button("ENTER RUMBLE BENCH", GUILayout.Height(26)))
                    tester.EnterRumbleTest();
            }

            using (new EditorGUI.DisabledScope(!tester.RumbleTestActive))
            {
                EditorGUILayout.LabelField(
                    $"Commanded — intensity {tester.TestIntensity}/100   {tester.TestHz} Hz   " +
                    $"channel {tester.TestChannel}");
                if (connection != null)
                    EditorGUILayout.LabelField(
                        $"On the wire — R {connection.SentRumbleRight}  C {connection.SentRumbleCentre}  " +
                        $"L {connection.SentRumbleLeft}  @ {connection.SentRumbleHz} Hz");

                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Channel — isolate one pad to confirm which is which",
                                            EditorStyles.miniLabel);
                EditorGUILayout.BeginHorizontal();
                DrawChannelButton(tester, YawVR3Tester.RumbleChannel.All, "ALL");
                DrawChannelButton(tester, YawVR3Tester.RumbleChannel.Right, "RIGHT");
                DrawChannelButton(tester, YawVR3Tester.RumbleChannel.Centre, "CENTRE");
                DrawChannelButton(tester, YawVR3Tester.RumbleChannel.Left, "LEFT");
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.Space(4);
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Intensity", GUILayout.Width(60));
                if (GUILayout.Button($"-{tester.rumbleStep}")) tester.NudgeIntensity(-tester.rumbleStep);
                if (GUILayout.Button($"+{tester.rumbleStep}")) tester.NudgeIntensity(tester.rumbleStep);
                if (GUILayout.Button("0")) tester.NudgeIntensity(-100);
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Frequency", GUILayout.Width(60));
                if (GUILayout.Button($"-{tester.hzStep} Hz")) tester.NudgeHz(-tester.hzStep);
                if (GUILayout.Button($"+{tester.hzStep} Hz")) tester.NudgeHz(tester.hzStep);
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.Space(6);
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("SWEEP INTENSITY", GUILayout.Height(24))) tester.StartIntensitySweep();
                if (GUILayout.Button("SWEEP Hz", GUILayout.Height(24))) tester.StartFrequencySweep();
                EditorGUILayout.EndHorizontal();

                using (new EditorGUI.DisabledScope(tester.ActiveSweep == YawVR3Tester.SweepMode.None))
                {
                    var prev = GUI.backgroundColor;
                    GUI.backgroundColor = new Color(0.45f, 0.8f, 0.5f);
                    if (GUILayout.Button($"MARK — I can feel it now  ({tester.ActiveSweep} sweep running)",
                                          GUILayout.Height(28)))
                        tester.MarkThreshold();
                    GUI.backgroundColor = prev;
                }

                EditorGUILayout.Space(4);
                string floor = tester.MeasuredFloorIntensity >= 0
                    ? $"{tester.MeasuredFloorIntensity}/100" : "not measured";
                string hz = tester.MeasuredHz >= 0 ? $"{tester.MeasuredHz} Hz" : "not measured";
                EditorGUILayout.HelpBox(
                    $"Measured floor: {floor}   →  CarRumbleCues.minEffectiveIntensity\n" +
                    $"Measured Hz:    {hz}   →  CarRumbleCues.minHz / maxHz (run twice to bracket)",
                    MessageType.None);

                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Clear measurements")) tester.ClearMeasurements();
                if (GUILayout.Button("Exit Rumble Bench (hand back to CarRumbleCues)"))
                    tester.ExitRumbleTest();
                EditorGUILayout.EndHorizontal();
            }
        }

        private static void DrawChannelButton(YawVR3Tester tester, YawVR3Tester.RumbleChannel channel, string label)
        {
            bool selected = tester.TestChannel == channel;
            var prev = GUI.backgroundColor;
            if (selected) GUI.backgroundColor = new Color(0.4f, 0.7f, 0.95f);
            if (GUILayout.Button(label)) tester.SetChannel(channel);
            GUI.backgroundColor = prev;
        }

        private static void DrawAxisRow(YawVR3Tester tester, string label, Action<float> nudge)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, GUILayout.Width(50));
            if (GUILayout.Button($"-{tester.stepDegrees:0.#}°")) nudge(-tester.stepDegrees);
            if (GUILayout.Button($"+{tester.stepDegrees:0.#}°")) nudge(tester.stepDegrees);
            EditorGUILayout.EndHorizontal();
        }
    }
}
