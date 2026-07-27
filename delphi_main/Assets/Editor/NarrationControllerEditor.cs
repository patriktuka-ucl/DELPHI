using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Delphi.Session;

namespace Delphi.EditorTools
{
    /// <summary>
    /// Inspector for NarrationController: one labelled row per recorded line,
    /// each showing the FILE that line expects, plus a one-click assign of the
    /// whole set by file name.
    ///
    /// The default array Inspector technically works, but it makes the two
    /// mistakes that matter here both easy and silent — wiring a clip to the
    /// wrong enum entry, and leaving an entry out entirely. Either one only
    /// shows up as the wrong words (or no words) playing to a participant
    /// mid-session, which is exactly the point at which you cannot fix it. So
    /// this draws a fixed row per Line and refuses to let the list drift out
    /// of one-slot-per-line shape.
    /// </summary>
    [CustomEditor(typeof(NarrationController))]
    public class NarrationControllerEditor : UnityEditor.Editor
    {
        /// <summary>Where the recordings are expected to live. Only used to
        /// narrow the auto-assign search and to word the help box — a clip
        /// dragged in from anywhere else works exactly the same.</summary>
        private const string DefaultAudioFolder = "Assets/Content/Audio/Narration";

        public override void OnInspectorGUI()
        {
            var nc = (NarrationController)target;

            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("source"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("logLines"));
            serializedObject.ApplyModifiedProperties();

            if (nc.source == null)
                EditorGUILayout.HelpBox(
                    "No AudioSource assigned — the session will run with console-logged " +
                    "lines only and the participant will hear nothing. Add an AudioSource " +
                    "component and drag it in here.",
                    MessageType.Warning);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Narration clips", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                $"One row per recording. Drop the .mp3 files into {DefaultAudioFolder} " +
                "and press \"Auto-assign by file name\" — the expected file for each row " +
                "is shown in grey. Anything it can't match, drag in by hand.",
                MessageType.None);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Auto-assign by file name")) AutoAssign(nc);
                if (GUILayout.Button("Create empty slots")) { RecordAndEnsure(nc); }
                if (GUILayout.Button("Clear all")) ClearAll(nc);
            }

            EditorGUILayout.Space();
            DrawSlots(nc);

            EditorGUILayout.Space();
            var missing = nc.MissingLines();
            if (missing.Count == 0)
                EditorGUILayout.HelpBox("All " + NarrationController.AllLines.Length +
                                        " lines have a clip assigned.", MessageType.Info);
            else
                EditorGUILayout.HelpBox(
                    $"{missing.Count} line(s) have no clip and will play silence:\n  " +
                    string.Join("\n  ", missing.Select(l => $"{l}  ({NarrationController.FileName(l)})")),
                    MessageType.Warning);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Testing", EditorStyles.boldLabel);
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("muteTest"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("muteTestSeconds"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("missingClipWaitSeconds"));
            serializedObject.ApplyModifiedProperties();
        }

        /// <summary>Draws a fixed row per Line and writes edits back through
        /// EnsureAllSlots, so clips[] can never end up with a duplicate or a
        /// missing entry no matter what order things are assigned in.</summary>
        private void DrawSlots(NarrationController nc)
        {
            if (nc.clips == null || nc.clips.Length != NarrationController.AllLines.Length)
                RecordAndEnsure(nc);

            var grey = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleLeft };
            grey.normal.textColor = Color.grey;

            for (int i = 0; i < nc.clips.Length; i++)
            {
                var line = nc.clips[i].line;
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(line.ToString(), GUILayout.Width(120));
                    EditorGUILayout.LabelField(NarrationController.FileName(line), grey, GUILayout.Width(150));
                    var next = (AudioClip)EditorGUILayout.ObjectField(
                        nc.clips[i].audio, typeof(AudioClip), allowSceneObjects: false);
                    if (next != nc.clips[i].audio)
                    {
                        Undo.RecordObject(nc, "Assign narration clip");
                        nc.clips[i].audio = next;
                        EditorUtility.SetDirty(nc);
                    }
                }
            }
        }

        private void RecordAndEnsure(NarrationController nc)
        {
            Undo.RecordObject(nc, "Create narration slots");
            nc.EnsureAllSlots();
            EditorUtility.SetDirty(nc);
        }

        private void ClearAll(NarrationController nc)
        {
            if (!EditorUtility.DisplayDialog("Clear narration clips",
                    "Unassign every clip? The slots stay, only the audio references are cleared.",
                    "Clear", "Cancel"))
                return;

            Undo.RecordObject(nc, "Clear narration clips");
            nc.clips = System.Array.Empty<NarrationController.Clip>();
            nc.EnsureAllSlots();
            EditorUtility.SetDirty(nc);
        }

        /// <summary>Match every Line to an AudioClip whose asset file name is
        /// exactly NarrationController.FileName(line). Clips already assigned
        /// are overwritten — the file name is the source of truth, so a
        /// re-run after re-exporting the audio is meant to re-point everything.
        /// Searches the whole project (not just the default folder) so a
        /// differently-organised import still resolves, but prefers a match
        /// inside the default folder when the same name exists twice.</summary>
        private void AutoAssign(NarrationController nc)
        {
            RecordAndEnsure(nc);

            var byName = new Dictionary<string, List<string>>();
            foreach (var guid in AssetDatabase.FindAssets("t:AudioClip"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                string name = Path.GetFileNameWithoutExtension(path);
                if (!byName.TryGetValue(name, out var list)) byName[name] = list = new List<string>();
                list.Add(path);
            }

            Undo.RecordObject(nc, "Auto-assign narration clips");
            int found = 0;
            var unmatched = new List<string>();

            for (int i = 0; i < nc.clips.Length; i++)
            {
                string expected = NarrationController.FileName(nc.clips[i].line);
                if (!byName.TryGetValue(expected, out var paths) || paths.Count == 0)
                {
                    unmatched.Add(expected);
                    continue;
                }

                // Prefer the canonical folder when a name is ambiguous, so an
                // old take left elsewhere in the project can't quietly win.
                string chosen = paths.FirstOrDefault(p => p.StartsWith(DefaultAudioFolder)) ?? paths[0];
                if (paths.Count > 1)
                    Debug.LogWarning($"[Narration] {paths.Count} audio clips are named '{expected}' — " +
                                     $"using {chosen}. Delete the duplicates to remove the ambiguity.");

                nc.clips[i].audio = AssetDatabase.LoadAssetAtPath<AudioClip>(chosen);
                found++;
            }

            EditorUtility.SetDirty(nc);

            string msg = $"[Narration] Auto-assigned {found}/{nc.clips.Length} clips by file name.";
            if (unmatched.Count > 0)
                msg += $" No audio asset found for: {string.Join(", ", unmatched)}.";
            Debug.Log(msg);
        }
    }
}
