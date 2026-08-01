using UnityEditor;
using UnityEngine;
using Delphi.Motion;

namespace Delphi.EditorTools
{
    /// <summary>
    /// Rig connection + transport state at a glance, matching the status-dot
    /// language DelphiManagerEditor already established. Exists mainly to
    /// answer "why isn't rumble/tilt doing anything" without hunting through
    /// the researcher dashboard: State/StatusText (is the rig even Started —
    /// motion never transports otherwise), and, once Started, what actually
    /// went on the wire last tick.
    /// </summary>
    [CustomEditor(typeof(YawVR3Connection))]
    public class YawVR3ConnectionEditor : Editor
    {
        private static readonly Color DotLive    = new Color(0.35f, 0.85f, 0.50f);
        private static readonly Color DotPending  = new Color(0.85f, 0.75f, 0.25f);
        private static readonly Color DotDown     = new Color(0.85f, 0.30f, 0.30f);
        private static readonly Color DotIdle     = new Color(0.55f, 0.55f, 0.58f);

        public override bool RequiresConstantRepaint() => Application.isPlaying;

        public override void OnInspectorGUI()
        {
            var yaw = (YawVR3Connection)target;
            serializedObject.Update();

            DrawStateBox(yaw);
            EditorGUILayout.Space(8);

            DrawPropertiesExcluding(serializedObject, "m_Script");

            if (Application.isPlaying)
            {
                EditorGUILayout.Space(8);
                DrawLiveBox(yaw);
            }

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawStateBox(YawVR3Connection yaw)
        {
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.BeginHorizontal();

            Color dot = yaw.State switch
            {
                YawConnectionState.Started => DotLive,
                YawConnectionState.Connected => DotPending,
                YawConnectionState.Discovering or YawConnectionState.Connecting
                    or YawConnectionState.Starting or YawConnectionState.Stopping => DotPending,
                YawConnectionState.Initial => DotIdle,
                _ => DotDown
            };
            var dotRect = GUILayoutUtility.GetRect(12, 18, GUILayout.Width(12));
            dotRect.y += 4; dotRect.width = dotRect.height = 10;
            EditorGUI.DrawRect(dotRect, dot);

            EditorGUILayout.LabelField($"{yaw.State} — {yaw.StatusText}", EditorStyles.boldLabel);
            EditorGUILayout.EndHorizontal();

            if (Application.isPlaying)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.BeginHorizontal();
                bool canStart = yaw.State == YawConnectionState.Connected;
                bool canStop = yaw.State == YawConnectionState.Started;
                GUI.enabled = canStart || canStop;
                if (GUILayout.Button(canStop ? "STOP MOTION" : "START MOTION", GUILayout.Height(24)))
                {
                    if (canStart) yaw.StartMotion();
                    else if (canStop) yaw.StopMotion();
                }
                GUI.enabled = true;
                EditorGUILayout.EndHorizontal();

                if (!canStart && !canStop)
                    EditorGUILayout.HelpBox(
                        "Motion can only be Started once the rig reaches Connected — it isn't yet.",
                        MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "Discovery/connect happen automatically on Play. Start/Stop Motion are Play-mode-only " +
                    "actions — the rig must never be put in motion outside Play.", MessageType.None);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawLiveBox(YawVR3Connection yaw)
        {
            EditorGUILayout.LabelField("Live", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            var cues = yaw.cues;
            EditorGUILayout.LabelField("Tilt", EditorStyles.miniBoldLabel);
            EditorGUILayout.LabelField(cues != null
                ? $"pitch {cues.PitchDeg:0.#}°   roll {cues.RollDeg:0.#}°   yaw {cues.YawDeg:0.#}°   " +
                  $"blend {yaw.TiltBlend:0.00}   (accel {cues.AccelMs2:0.00} m/s², turn {cues.YawRateDegPerSec:0.0}°/s)"
                : "No CarMotionCues linked.");

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Rumble", EditorStyles.miniBoldLabel);
            var rum = yaw.rumble;
            if (rum == null)
            {
                EditorGUILayout.LabelField("No CarRumbleCues linked.");
            }
            else if (yaw.State != YawConnectionState.Started)
            {
                EditorGUILayout.LabelField(
                    $"Model {(rum.IsSilent ? "silent" : "active")} — NOT transported, rig isn't Started.");
            }
            else
            {
                EditorGUILayout.LabelField(
                    $"pads {yaw.SentRumbleRight}/{yaw.SentRumbleCentre}/{yaw.SentRumbleLeft} @ {yaw.SentRumbleHz} Hz " +
                    $"({(rum.IsSilent ? "silent" : "active")})");
            }

            EditorGUILayout.EndVertical();
            Repaint();
        }
    }
}
