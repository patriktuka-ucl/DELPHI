using UnityEditor;
using UnityEngine;

namespace Delphi.Motion.Editor
{
    [CustomEditor(typeof(YawVR3Connection))]
    public class YawVR3ConnectionEditor : UnityEditor.Editor
    {
        private SerializedProperty _cues, _car;
        private SerializedProperty _rumbleEnabled, _rumbleBase, _rumbleSpeedScale, _rumbleMax, _rumbleHz;
        private SerializedProperty _rediscoverInterval;
        private SerializedProperty _motionSendRateHz;
        private SerializedProperty _manualMaxDegreesPerSecond;

        private void OnEnable()
        {
            _cues = serializedObject.FindProperty("cues");
            _car = serializedObject.FindProperty("car");
            _rumbleEnabled = serializedObject.FindProperty("rumbleEnabled");
            _rumbleBase = serializedObject.FindProperty("rumbleBaseIntensity");
            _rumbleSpeedScale = serializedObject.FindProperty("rumbleSpeedScale");
            _rumbleMax = serializedObject.FindProperty("rumbleMaxIntensity");
            _rumbleHz = serializedObject.FindProperty("rumbleHz");
            _rediscoverInterval = serializedObject.FindProperty("rediscoverIntervalSeconds");
            _motionSendRateHz = serializedObject.FindProperty("motionSendRateHz");
            _manualMaxDegreesPerSecond = serializedObject.FindProperty("manualMaxDegreesPerSecond");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            if (Application.isPlaying)
            {
                var conn = (YawVR3Connection)target;
                EditorGUILayout.HelpBox($"{conn.State}: {conn.StatusText}", MessageType.None);
                EditorGUILayout.Space(6);
            }

            EditorGUILayout.LabelField("Links", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_cues);
            EditorGUILayout.PropertyField(_car);

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Rumble / Haptic Buzzer", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_rumbleEnabled);
            using (new EditorGUI.DisabledScope(!_rumbleEnabled.boolValue))
            {
                EditorGUILayout.PropertyField(_rumbleBase);
                EditorGUILayout.PropertyField(_rumbleSpeedScale);
                EditorGUILayout.PropertyField(_rumbleMax);
                EditorGUILayout.PropertyField(_rumbleHz);
            }

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Discovery", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_rediscoverInterval);

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Motion Send Rate", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_motionSendRateHz);

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Manual Test Smoothing", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_manualMaxDegreesPerSecond);

            serializedObject.ApplyModifiedProperties();
        }
    }
}
