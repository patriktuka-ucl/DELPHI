using System;
using UnityEditor;
using UnityEngine;

namespace Delphi.EditorTools
{
    /// <summary>
    /// Replaces the raw "deviceName" text field with a dropdown listing the
    /// actual cameras Unity currently sees, so picking between multiple
    /// webcams doesn't require typing an exact device name by hand.
    /// </summary>
    [CustomEditor(typeof(WebcamSensor))]
    public class WebcamSensorEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var deviceNameProp = serializedObject.FindProperty("deviceName");
            var devices = WebCamTexture.devices;

            if (devices.Length == 0)
            {
                EditorGUILayout.HelpBox(
                    "No webcam devices detected. On macOS, grant camera access to the Editor in " +
                    "System Settings > Privacy & Security > Camera, then reopen this Inspector.",
                    MessageType.Warning);
                EditorGUILayout.PropertyField(deviceNameProp, new GUIContent("Device Name (manual)"));
            }
            else
            {
                string[] deviceNames = new string[devices.Length];
                for (int i = 0; i < devices.Length; i++) deviceNames[i] = devices[i].name;

                string[] options = new string[deviceNames.Length + 1];
                options[0] = "(First available)";
                Array.Copy(deviceNames, 0, options, 1, deviceNames.Length);

                int currentIndex = string.IsNullOrEmpty(deviceNameProp.stringValue)
                    ? 0
                    : Array.IndexOf(deviceNames, deviceNameProp.stringValue) + 1;

                if (currentIndex < 0)
                {
                    EditorGUILayout.HelpBox(
                        $"Configured device '{deviceNameProp.stringValue}' isn't currently detected — " +
                        "showing the picker below; selecting an option will overwrite it.",
                        MessageType.Warning);
                    currentIndex = 0;
                }

                int newIndex = EditorGUILayout.Popup("Camera Device", currentIndex, options);
                deviceNameProp.stringValue = newIndex == 0 ? "" : deviceNames[newIndex - 1];
            }

            EditorGUILayout.PropertyField(serializedObject.FindProperty("requestedWidth"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("requestedHeight"));

            serializedObject.ApplyModifiedProperties();
        }
    }
}
