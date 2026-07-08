using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Delphi.Simulation
{
    public enum TrackEventKind { RedLight, SpeedZone }

    /// <summary>
    /// A hand-placed marker on the Track. The researcher drags an empty
    /// GameObject (a child of the Track) anywhere near the spline and it
    /// SNAPS to the nearest point on the road, storing its arc-length
    /// position s. SpeedZone also has an end handle in the Scene view for
    /// its far edge (endS).
    ///
    /// RedLight — a stop LINE. Wherever this marker sits is treated exactly
    /// like a line painted on the road: the car brakes smoothly on
    /// approach and is guaranteed to stop with its front bumper AT s, not
    /// past it — see CarDriver.Update(), which clamps position to the stop
    /// line the instant it would be crossed, rather than detecting overshoot
    /// after the fact.
    ///
    /// SpeedZone — overrides the track's default speed limit from S to EndS.
    ///
    /// Gizmo colours: red light = red, speed zone = cyan.
    /// </summary>
    [ExecuteAlways]
    public class TrackEvent : MonoBehaviour
    {
        [Header("Identity")]
        public TrackEventKind kind = TrackEventKind.RedLight;

        [Tooltip("Arc-length position on the track (metres from the start). " +
                 "Set automatically by dragging the marker in the Scene view.")]
        [SerializeField] private float s;

        [Tooltip("Zone kinds only: arc-length of the zone's far edge.")]
        [SerializeField] private float endS;

        // ── RedLight ────────────────────────────────────────────────────
        [Tooltip("Seconds the car waits at the line before pulling away.")]
        public float waitDuration = 3f;

        // ── SpeedZone ───────────────────────────────────────────────────
        [Tooltip("Posted limit inside this zone, overriding the track default.")]
        public float limitKmh = 30f;

        public float S => s;
        public float EndS => Mathf.Max(endS, s);
        public bool IsZone => kind == TrackEventKind.SpeedZone;

        private Track _track;
        public Track Track
        {
            get
            {
                if (_track == null) _track = GetComponentInParent<Track>();
                return _track;
            }
        }

        private void Awake()
        {
            if (Application.isPlaying && Track == null)
                Debug.LogWarning($"[TrackEvent] '{name}' is not parented under a Track — " +
                                 "it will be invisible to red lights/zones. Drag it to be " +
                                 "a child of the Track object.", this);
        }

#if UNITY_EDITOR
        private Vector3 _lastSnappedPos;
        private bool _hasSnapped;

        // A newly-added component defaults to its GameObject's current
        // transform — if that's a fresh child of Track dropped at local
        // (0,0,0), that's often s≈0, the very start of the road. Since the
        // ego always starts at S=0 too, a RedLight sitting there makes the
        // car think it's already AT the stop line before it's moved at
        // all. Nudge new markers partway down the track instead.
        private void Reset()
        {
            if (Track == null) return;
            Track.EnsureBuiltEditor();
            if (Track.TotalLength > 1f) SetS(Mathf.Min(50f, Track.TotalLength * 0.5f));
        }

        // Edit-mode magnetic snapping: whenever the marker is moved, project
        // it back onto the spline and record the resulting s. Dragging then
        // feels like sliding a bead along the road.
        private void Update()
        {
            if (Application.isPlaying) return;
            if (Track == null) return;
            if (_hasSnapped && (transform.position - _lastSnappedPos).sqrMagnitude < 1e-6f) return;

            Track.EnsureBuiltEditor();
            s = Track.ProjectWorldPoint(transform.position, out Vector3 onTrack);
            transform.position = onTrack;
            _lastSnappedPos = onTrack;
            _hasSnapped = true;
            if (endS < s) endS = s + (IsZone ? 50f : 0f);
        }

        /// <summary>Editor helper: move the marker to a specific s.</summary>
        public void SetS(float newS)
        {
            if (Track == null) return;
            Track.EnsureBuiltEditor();
            s = Mathf.Clamp(newS, 0f, Track.TotalLength);
            transform.position = _lastSnappedPos = Track.EvaluatePosition(s);
            _hasSnapped = true;
        }

        public void SetEndS(float newEndS)
        {
            if (Track == null) return;
            Track.EnsureBuiltEditor();
            endS = Mathf.Clamp(newEndS, s, Track.TotalLength);
        }

        public static Color KindColor(TrackEventKind k) => k switch
        {
            TrackEventKind.RedLight  => new Color(0.95f, 0.15f, 0.15f),
            TrackEventKind.SpeedZone => new Color(0.20f, 0.85f, 0.95f),
            _                        => Color.white
        };

        private void OnDrawGizmos()
        {
            if (Track == null) return;
            Track.EnsureBuiltEditor();

            // SpeedZones colour-match the track's per-limit section colours.
            Color col = IsZone ? Simulation.Track.SpeedLimitColor(limitKmh)
                               : KindColor(kind);
            Gizmos.color = col;
            Vector3 p = Track.EvaluatePosition(s);

            if (kind == TrackEventKind.RedLight)
            {
                // Draw the stop LINE across the road, not just a point.
                Vector3 fwd = Track.EvaluateTangent(s);
                Vector3 side = Vector3.Cross(Vector3.up, fwd).normalized * 2f;
                Gizmos.DrawLine(p - side + Vector3.up * 0.05f, p + side + Vector3.up * 0.05f);
                Gizmos.DrawSphere(p + Vector3.up * 0.5f, 0.5f);
                Gizmos.DrawLine(p, p + Vector3.up * 3f);
            }
            else
            {
                Gizmos.DrawSphere(p + Vector3.up * 0.5f, 0.8f);
                Gizmos.DrawLine(p, p + Vector3.up * 3f);

                Vector3 prev = p + Vector3.up * 0.3f;
                float step = Mathf.Max(2f, (EndS - s) / 64f);
                for (float ss = s + step; ss <= EndS; ss += step)
                {
                    Vector3 cur = Track.EvaluatePosition(ss) + Vector3.up * 0.3f;
                    Gizmos.DrawLine(prev, cur);
                    prev = cur;
                }
                Vector3 pe = Track.EvaluatePosition(EndS);
                Gizmos.DrawWireSphere(pe + Vector3.up * 0.5f, 0.8f);
            }

            string label = IsZone ? $"{kind} {limitKmh:F0} km/h  (s={s:F0}m)"
                                  : $"{kind}  (s={s:F0}m)";
            Handles.Label(p + Vector3.up * 3.4f, label,
                new GUIStyle(EditorStyles.boldLabel) { normal = { textColor = col } });
        }
#endif
    }

#if UNITY_EDITOR
    [CustomEditor(typeof(TrackEvent))]
    [CanEditMultipleObjects]
    public class TrackEventEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var ev = (TrackEvent)target;

            EditorGUILayout.PropertyField(serializedObject.FindProperty("kind"));

            EditorGUI.BeginChangeCheck();
            float newS = EditorGUILayout.FloatField("S (m along track)", ev.S);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(ev.transform, "Move track event");
                Undo.RecordObject(ev, "Move track event");
                ev.SetS(newS);
            }

            var kind = (TrackEventKind)serializedObject.FindProperty("kind").enumValueIndex;

            if (ev.IsZone)
            {
                EditorGUI.BeginChangeCheck();
                float newEnd = EditorGUILayout.FloatField("End S (m along track)", ev.EndS);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(ev, "Resize zone");
                    ev.SetEndS(newEnd);
                }
            }

            EditorGUILayout.Space();
            switch (kind)
            {
                case TrackEventKind.RedLight:
                    EditorGUILayout.LabelField("Red light", EditorStyles.boldLabel);
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("waitDuration"));
                    break;

                case TrackEventKind.SpeedZone:
                    EditorGUILayout.LabelField("Speed zone", EditorStyles.boldLabel);
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("limitKmh"));
                    break;
            }

            serializedObject.ApplyModifiedProperties();
        }

        private void OnSceneGUI()
        {
            var ev = (TrackEvent)target;
            if (!ev.IsZone || ev.Track == null) return;
            ev.Track.EnsureBuiltEditor();

            Vector3 endPos = ev.Track.EvaluatePosition(ev.EndS) + Vector3.up * 0.5f;
            EditorGUI.BeginChangeCheck();
            Vector3 moved = Handles.FreeMoveHandle(endPos, 1.2f, Vector3.zero,
                                                   Handles.SphereHandleCap);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(ev, "Resize zone");
                float movedS = ev.Track.ProjectWorldPoint(moved, out _);
                ev.SetEndS(movedS);
            }
        }
    }
#endif
}
