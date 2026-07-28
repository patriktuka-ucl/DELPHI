using UnityEditor;
using UnityEngine;

namespace Delphi.EditorTools
{
    /// <summary>
    /// Adds a per-feed "record this" section to SessionRecorder, in the same
    /// patch-bay style as DelphiManagerEditor.
    ///
    /// The distinction the UI has to make obvious is CAPTURED vs RECORDED.
    /// DelphiManager decides whether a feed exists at all; these decide only
    /// whether it reaches disk. A feed that is off in DelphiManager cannot be
    /// recorded no matter what is ticked here, so those rows say so rather
    /// than offering a toggle that would silently do nothing.
    /// </summary>
    [CustomEditor(typeof(SessionRecorder))]
    public class SessionRecorderEditor : Editor
    {
        private static readonly Color OnChip  = new Color(0.20f, 0.85f, 0.35f);
        private static readonly Color OffChip = new Color(0.35f, 0.35f, 0.38f);
        private static readonly Color Unavail = new Color(0.85f, 0.75f, 0.25f);

        public override bool RequiresConstantRepaint() => Application.isPlaying;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var rec = (SessionRecorder)target;
            var mgr = Object.FindFirstObjectByType<DelphiManager>();

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Recorded feeds", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Which feeds are WRITTEN TO DISK. Unticking one removes its RenderTexture, " +
                "its GPU readback and its ffmpeg process entirely — it is the toggle that " +
                "actually sheds recording load. It does NOT hide the feed from the dashboard; " +
                "that is DelphiManager's per-channel toggle.",
                MessageType.None);

            if (Application.isPlaying && rec.IsRecording)
                EditorGUILayout.HelpBox(
                    "Recording is running. Changes take effect on the NEXT recording — feeds " +
                    "are opened once at StartRecording.", MessageType.Warning);

            foreach (var ch in DelphiManager.AllFrameChannels)
            {
                var (label, _) = DelphiManager.FrameMeta(ch);
                bool capturedOff = mgr != null && mgr.GetStatus(ch) == ChannelStatus.NotAttached;
                bool disabledInManager = mgr != null && mgr.GetStatus(ch) == ChannelStatus.Disabled;

                EditorGUILayout.BeginHorizontal();

                bool on = rec.IsFeedRecorded(ch);
                var chip = capturedOff || disabledInManager ? Unavail : (on ? OnChip : OffChip);
                var prev = GUI.backgroundColor;
                GUI.backgroundColor = chip;

                EditorGUI.BeginChangeCheck();
                bool next = GUILayout.Toggle(on, on ? "REC" : "off", EditorStyles.miniButton,
                                             GUILayout.Width(46));
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(rec, "Toggle recorded feed");
                    rec.SetFeedRecorded(ch, next);
                    EditorUtility.SetDirty(rec);
                }
                GUI.backgroundColor = prev;

                EditorGUILayout.LabelField(label, GUILayout.Width(150));

                if (capturedOff)
                    EditorGUILayout.LabelField("no sensor attached", EditorStyles.miniLabel);
                else if (disabledInManager)
                    EditorGUILayout.LabelField("off in DelphiManager — cannot record", EditorStyles.miniLabel);
                else if (Application.isPlaying)
                    EditorGUILayout.LabelField(
                        mgr != null && mgr.HasFrame(ch) ? "live" : "no frames yet", EditorStyles.miniLabel);

                EditorGUILayout.EndHorizontal();
            }
        }
    }
}
