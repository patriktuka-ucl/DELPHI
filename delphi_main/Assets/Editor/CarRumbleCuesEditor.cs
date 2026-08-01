using UnityEditor;
using UnityEngine;
using Delphi.Motion;

namespace Delphi.EditorTools
{
    /// <summary>
    /// Surfaces the model's own diagnostics (already public on CarRumbleCues —
    /// this just draws them) side by side with minEffectiveIntensity/
    /// maxIntensity, since the model's own code comments say the floor is a
    /// MEASURED hardware quantity, not something to guess from code. If the
    /// computed levels below look right but nothing is felt on the rig, the
    /// floor value itself is the thing to re-measure with YawVR3Tester's
    /// rumble bench — not a code bug.
    /// </summary>
    [CustomEditor(typeof(CarRumbleCues))]
    public class CarRumbleCuesEditor : Editor
    {
        private static readonly Color BarFill = new Color32(70, 220, 160, 255);
        private static readonly Color BarTrack = new Color(0.18f, 0.20f, 0.26f);
        private static readonly Color FloorMark = new Color(0.85f, 0.30f, 0.30f);

        public override bool RequiresConstantRepaint() => Application.isPlaying;

        public override void OnInspectorGUI()
        {
            var rum = (CarRumbleCues)target;
            serializedObject.Update();

            DrawPropertiesExcluding(serializedObject, "m_Script");

            if (Application.isPlaying)
            {
                EditorGUILayout.Space(8);
                DrawLiveDiagnostics(rum);
            }

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawLiveDiagnostics(CarRumbleCues rum)
        {
            EditorGUILayout.LabelField("Live diagnostics", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            EditorGUILayout.LabelField(
                $"Mode: {(rum.SoloMode ? "solo (no tilt — extra gain)" : "with tilt")}   " +
                $"Mute: {rum.MuteBlend:0.00}   {(rum.IsSilent ? "SILENT" : "active")}");

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Layer levels (0–1, before the floor mapping)", EditorStyles.miniBoldLabel);
            Level01Bar("Road bed", rum.RoadBedLevel);
            Level01Bar("Longitudinal (shaped)", rum.LongitudinalLevel);
            Level01Bar("Longitudinal (raw, pre-envelope)", rum.LongitudinalRaw);
            Level01Bar("Lateral", rum.LateralLevel);
            EditorGUILayout.LabelField($"Brakeness: {rum.Brakeness:0.00}  (0 = accelerating, 1 = braking)",
                                       EditorStyles.miniLabel);

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField(
                $"Motor outputs (0–100, floor-mapped into [{rum.minEffectiveIntensity} … {rum.maxIntensity}])",
                EditorStyles.miniBoldLabel);
            MotorBar("Right", rum.MotorRight, rum.minEffectiveIntensity, rum.maxIntensity);
            MotorBar("Centre", rum.MotorCentre, rum.minEffectiveIntensity, rum.maxIntensity);
            MotorBar("Left", rum.MotorLeft, rum.minEffectiveIntensity, rum.maxIntensity);
            EditorGUILayout.LabelField($"Frequency: {rum.Hz} Hz", EditorStyles.miniLabel);

            if (!rum.IsSilent &&
                rum.MotorRight <= rum.minEffectiveIntensity &&
                rum.MotorCentre <= rum.minEffectiveIntensity &&
                rum.MotorLeft <= rum.minEffectiveIntensity)
            {
                EditorGUILayout.HelpBox(
                    "Every active pad is sitting right at the floor. If that still isn't felt on the " +
                    "rig, minEffectiveIntensity is set too low for this hardware — re-measure it with " +
                    "YawVR3Tester's rumble bench rather than raising it blind.", MessageType.Warning);
            }

            EditorGUILayout.EndVertical();
            Repaint();
        }

        private void Level01Bar(string label, float value01)
        {
            Rect r = GUILayoutUtility.GetRect(0, 16, GUILayout.ExpandWidth(true));
            float labelW = 170f;
            var labelRect = new Rect(r.x, r.y, labelW, r.height);
            var barRect = new Rect(r.x + labelW, r.y + 2, r.width - labelW - 40, r.height - 4);
            var valRect = new Rect(barRect.xMax + 4, r.y, 36, r.height);

            EditorGUI.LabelField(labelRect, label, EditorStyles.miniLabel);
            EditorGUI.DrawRect(barRect, BarTrack);
            EditorGUI.DrawRect(new Rect(barRect.x, barRect.y, barRect.width * Mathf.Clamp01(value01), barRect.height), BarFill);
            EditorGUI.LabelField(valRect, value01.ToString("0.00"), EditorStyles.miniLabel);
        }

        private void MotorBar(string label, int value0to100, int floor, int ceiling)
        {
            Rect r = GUILayoutUtility.GetRect(0, 16, GUILayout.ExpandWidth(true));
            float labelW = 60f;
            var labelRect = new Rect(r.x, r.y, labelW, r.height);
            var barRect = new Rect(r.x + labelW, r.y + 2, r.width - labelW - 36, r.height - 4);
            var valRect = new Rect(barRect.xMax + 4, r.y, 32, r.height);

            EditorGUI.LabelField(labelRect, label, EditorStyles.miniLabel);
            EditorGUI.DrawRect(barRect, BarTrack);
            EditorGUI.DrawRect(new Rect(barRect.x, barRect.y, barRect.width * (value0to100 / 100f), barRect.height), BarFill);

            // Floor marker — everything nonzero lands at or above this line.
            float floorX = barRect.x + barRect.width * (Mathf.Clamp(floor, 0, 100) / 100f);
            EditorGUI.DrawRect(new Rect(floorX, barRect.y - 2, 1.5f, barRect.height + 4), FloorMark);

            EditorGUI.LabelField(valRect, value0to100.ToString(), EditorStyles.miniLabel);
        }
    }
}
