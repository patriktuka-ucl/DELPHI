using UnityEditor;
using UnityEngine;

namespace Delphi.Motion.Editor
{
    [CustomEditor(typeof(YawVR3Connection))]
    public class YawVR3ConnectionEditor : UnityEditor.Editor
    {
        private SerializedProperty _cues, _rumbleCues;
        private SerializedProperty _tiltEnabled, _rumbleEnabled, _tiltTransition, _idleHz;
        private SerializedProperty _rediscoverInterval;
        private SerializedProperty _motionSendRateHz, _minAngleChange;
        private SerializedProperty _manualMaxDegreesPerSecond;

        private void OnEnable()
        {
            _cues = serializedObject.FindProperty("cues");
            _rumbleCues = serializedObject.FindProperty("rumble");
            _tiltEnabled = serializedObject.FindProperty("tiltEnabled");
            _rumbleEnabled = serializedObject.FindProperty("rumbleEnabled");
            _tiltTransition = serializedObject.FindProperty("tiltTransitionSeconds");
            _idleHz = serializedObject.FindProperty("idleRumbleHz");
            _rediscoverInterval = serializedObject.FindProperty("rediscoverIntervalSeconds");
            _motionSendRateHz = serializedObject.FindProperty("motionSendRateHz");
            _minAngleChange = serializedObject.FindProperty("minAngleChangeDeg");
            _manualMaxDegreesPerSecond = serializedObject.FindProperty("manualMaxDegreesPerSecond");
        }

        public override bool RequiresConstantRepaint() => Application.isPlaying;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var conn = (YawVR3Connection)target;

            if (Application.isPlaying)
            {
                EditorGUILayout.HelpBox($"{conn.State}: {conn.StatusText}", MessageType.None);
                EditorGUILayout.Space(6);
            }

            EditorGUILayout.LabelField("Links", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_cues);
            EditorGUILayout.PropertyField(_rumbleCues);

            // Empty is not the same as missing: Awake() falls back to a scene
            // search for both links, so an unassigned field is only a problem
            // if there is genuinely nothing in the scene to find. Warning on
            // the field alone would fire on a perfectly working setup.
            if (_rumbleCues.objectReferenceValue == null)
            {
                var found = FindAnyObjectByType<CarRumbleCues>();
                if (found != null)
                {
                    EditorGUILayout.HelpBox(
                        $"Unassigned, but '{found.name}' will be found automatically on Awake. " +
                        "Assign it explicitly if the scene ever grows a second one.",
                        MessageType.None);
                }
                else
                {
                    EditorGUILayout.HelpBox(
                        "No CarRumbleCues anywhere in the scene — rumble has no model and the " +
                        "vibration field will be sent as zeros.", MessageType.Warning);
                    if (!Application.isPlaying &&
                        GUILayout.Button("Add CarRumbleCues to this GameObject"))
                    {
                        var added = Undo.AddComponent<CarRumbleCues>(conn.gameObject);
                        _rumbleCues.objectReferenceValue = added;
                    }
                }
            }

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Output Modes", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "TILT and RUMBLE are independent. Either, both or neither — but the rig must " +
                "be STARTED for either to reach it, because the vibration field travels inside " +
                "the motion packet.", MessageType.None);
            EditorGUILayout.PropertyField(_tiltEnabled, new GUIContent("Tilt Enabled"));
            using (new EditorGUI.DisabledScope(!_tiltEnabled.boolValue))
                EditorGUILayout.PropertyField(_tiltTransition);
            EditorGUILayout.PropertyField(_rumbleEnabled, new GUIContent("Rumble Enabled"));
            EditorGUILayout.PropertyField(_idleHz);

            if (Application.isPlaying)
            {
                EditorGUILayout.Space(4);
                // Mid-transition the toggle and the reality disagree — say so,
                // rather than letting the tick box imply it already took effect.
                string tilt = conn.TiltBlend >= 0.999f ? "full"
                            : conn.TiltBlend <= 0.001f ? "level"
                            : $"fading — {conn.TiltBlend * 100f:0}%";
                EditorGUILayout.LabelField($"Tilt: {tilt}");
                EditorGUILayout.LabelField(
                    $"Rumble on the wire — R {conn.SentRumbleRight}  C {conn.SentRumbleCentre}  " +
                    $"L {conn.SentRumbleLeft}  @ {conn.SentRumbleHz} Hz" +
                    (conn.HasManualRumble ? "   (BENCH OVERRIDE)" : ""));
            }

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Discovery", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_rediscoverInterval);

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Motion Send Rate", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_motionSendRateHz);
            EditorGUILayout.PropertyField(_minAngleChange);

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Manual Test Smoothing", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_manualMaxDegreesPerSecond);

            serializedObject.ApplyModifiedProperties();
        }
    }
}
