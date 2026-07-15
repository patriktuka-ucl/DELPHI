using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Delphi.Simulation;

namespace Delphi.EditorTools
{
    /// <summary>
    /// The road-authoring control panel — a custom Track inspector built to make
    /// laying out a drive as painless as possible:
    ///
    ///   • ROAD — length/point/event summary + Frame-in-Scene. The path itself
    ///     is drawn with Unity's own Spline tool (select this object, use the
    ///     Spline toolbar / click-drag the curve directly) — that tool is
    ///     already better at this than any custom button could be, so this
    ///     panel doesn't duplicate it.
    ///   • EVENTS — one button per kind (Stop-and-go, Cruise, Turn) spawns a
    ///     snapped marker at the Scene-view pivot, ready to nudge along the road.
    ///   • LIST — every event, sorted by distance, with Select + Delete, so the
    ///     whole drive is editable from this one panel.
    ///
    /// The road line and every event's marker are ALWAYS-VISIBLE real geometry
    /// (see Track.RefreshDebugVisual / TrackEvent.RefreshMarker) gated on
    /// Track.showDebugGizmos — visible in Scene AND Game view while authoring,
    /// no Gizmos-toggle dependency. All mutations here are Undo-recorded and
    /// mark the scene dirty.
    /// </summary>
    [CustomEditor(typeof(Track))]
    public class TrackAuthoringEditor : Editor
    {
        private Vector2 _scroll;

        public override void OnInspectorGUI()
        {
            var track = (Track)target;
            serializedObject.Update();

            DrawRoadSection(track);
            EditorGUILayout.Space(6);
            DrawEventsSection(track);
            EditorGUILayout.Space(8);

            EditorGUILayout.LabelField("Advanced", EditorStyles.boldLabel);
            DrawPropertiesExcluding(serializedObject, "m_Script", "defaultSpeedLimitKmh");

            serializedObject.ApplyModifiedProperties();
        }

        // ── Road ────────────────────────────────────────────────────────
        private void DrawRoadSection(Track track)
        {
            EditorGUILayout.LabelField("Road", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            EditorGUILayout.PropertyField(serializedObject.FindProperty("defaultSpeedLimitKmh"),
                new GUIContent("Default limit (km/h)"));

            track.EnsureBuiltEditor();
            int knots = track.splineContainer != null && track.splineContainer.Spline != null
                ? track.splineContainer.Spline.Count : 0;
            EditorGUILayout.LabelField($"Length {track.TotalLength:F0} m   ·   {knots} path point(s)   ·   {track.Events.Count} event(s)");

            EditorGUILayout.HelpBox("Draw/edit the road with Unity's own Spline tool: select this " +
                "object, pick the Spline tool in the Scene view toolbar (or click directly on the " +
                "curve), then click along the road to add points or drag existing ones.",
                MessageType.Info);

            if (GUILayout.Button("Frame road in Scene") && SceneView.lastActiveSceneView != null)
                SceneView.lastActiveSceneView.FrameSelected();

            EditorGUILayout.EndVertical();
        }

        // ── Events ──────────────────────────────────────────────────────
        private void DrawEventsSection(Track track)
        {
            EditorGUILayout.LabelField("Events", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            if (track.TotalLength <= 1f)
                EditorGUILayout.HelpBox("Draw a path first — events snap onto the road.", MessageType.Info);

            // No Cruise button — cruising isn't an authored event here, it's
            // the implicit default for any stretch with nothing else going on
            // (v2's contextual BO is where "cruise" becomes its own context).
            using (new EditorGUI.DisabledScope(track.TotalLength <= 1f))
            {
                EditorGUILayout.BeginHorizontal();
                AddButton(track, TrackEventKind.StopAndGo, "＋ Stop-and-go");
                AddButton(track, TrackEventKind.Turn, "＋ Turn");
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.Space(4);
            var events = new List<TrackEvent>(track.Events);
            events.Sort((a, b) => a.S.CompareTo(b.S));

            if (events.Count == 0)
                EditorGUILayout.LabelField("No events yet.", EditorStyles.miniLabel);
            else
            {
                _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MaxHeight(220));
                foreach (var ev in events)
                {
                    if (ev == null) continue;
                    EditorGUILayout.BeginHorizontal();

                    var prev = GUI.color; GUI.color = TrackEvent.KindColor(ev.kind);
                    GUILayout.Label("■", GUILayout.Width(16)); GUI.color = prev;

                    string range = ev.IsRanged ? $"{ev.S:F0}→{ev.EndS:F0} m" : $"{ev.S:F0} m";
                    string extra = ev.kind == TrackEventKind.Cruise ? $"  ≤{ev.limitKmh:F0}km/h"
                                 : ev.IsStop ? $"  wait {ev.waitDuration:F0}s" : "";
                    GUILayout.Label($"{ev.kind}", GUILayout.Width(84));
                    GUILayout.Label($"{range}{extra}", EditorStyles.miniLabel);

                    if (GUILayout.Button("Select", GUILayout.Width(58)))
                    {
                        Selection.activeGameObject = ev.gameObject;
                        if (SceneView.lastActiveSceneView != null) SceneView.lastActiveSceneView.FrameSelected();
                    }
                    var pc = GUI.backgroundColor; GUI.backgroundColor = new Color(0.8f, 0.4f, 0.4f);
                    if (GUILayout.Button("✕", GUILayout.Width(24)))
                    {
                        Undo.DestroyObjectImmediate(ev.gameObject);
                        track.RebuildEventIndex(); MarkDirty(track);
                        GUI.backgroundColor = pc; EditorGUILayout.EndHorizontal();
                        GUIUtility.ExitGUI(); // list changed under us; bail this pass
                    }
                    GUI.backgroundColor = pc;
                    EditorGUILayout.EndHorizontal();
                }
                EditorGUILayout.EndScrollView();
            }

            EditorGUILayout.EndVertical();
        }

        private void AddButton(Track track, TrackEventKind kind, string label)
        {
            var prev = GUI.backgroundColor;
            GUI.backgroundColor = Color.Lerp(prev, TrackEvent.KindColor(kind), 0.5f);
            if (GUILayout.Button(label, GUILayout.Height(24))) AddEvent(track, kind);
            GUI.backgroundColor = prev;
        }

        private void AddEvent(Track track, TrackEventKind kind)
        {
            var go = new GameObject(kind.ToString());
            Undo.RegisterCreatedObjectUndo(go, $"Add {kind}");
            go.transform.SetParent(track.transform, false);
            var ev = Undo.AddComponent<TrackEvent>(go);
            ev.kind = kind;

            // Place at the point on the road nearest the Scene-view pivot, so it
            // lands roughly where the researcher is looking; else mid-track.
            track.EnsureBuiltEditor();
            float s = track.TotalLength * 0.5f;
            if (SceneView.lastActiveSceneView != null)
                s = track.ProjectWorldPoint(SceneView.lastActiveSceneView.pivot, out _);
            ev.SetS(s);
            if (ev.IsRanged) ev.SetEndS(Mathf.Min(s + 40f, track.TotalLength));

            track.RebuildEventIndex();
            Selection.activeGameObject = go;
            MarkDirty(track);
        }

        private static void MarkDirty(Track track)
        {
            EditorUtility.SetDirty(track);
            if (!Application.isPlaying)
                EditorSceneManager.MarkSceneDirty(track.gameObject.scene);
        }
    }
}
