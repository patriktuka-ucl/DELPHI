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

            using (new EditorGUI.DisabledScope(tester.ManualModeActive))
            {
                if (GUILayout.Button("ENTER MANUAL TEST MODE", GUILayout.Height(28)))
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

                EditorGUILayout.Space(6);
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

                EditorGUILayout.Space(10);
                if (GUILayout.Button("Exit Manual Test Mode (hand back to CarMotionCues)"))
                    tester.ExitManualMode();
            }

            Repaint(); // live-update the status/commanded readout while Started
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
