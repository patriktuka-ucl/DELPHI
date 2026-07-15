using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Delphi.Simulation
{
    /// <summary>
    /// The driving-event vocabulary, aligned to the study's context taxonomy.
    ///   StopAndGo — a stop (red light, stop sign, pedestrian crossing): brake
    ///               to the line, wait, pull away.
    ///   Cruise    — a ranged zone with a max speed; steady-state driving.
    ///   Turn      — a ranged marker over a curve. The road actually curves via
    ///               the spline (cornering falls out of curvature); this just
    ///               LABELS the segment as a turn context (start→end).
    /// </summary>
    public enum TrackEventKind { StopAndGo, Cruise, Turn }

    /// <summary>
    /// A hand-placed marker on the Track. The researcher drags an empty
    /// GameObject (a child of the Track) anywhere near the spline and it SNAPS
    /// to the nearest point on the road, storing its arc-length position s. The
    /// ranged kinds (Cruise, Turn) also have an end handle in the Scene view for
    /// their far edge (endS).
    ///
    /// StopAndGo — a stop LINE. Wherever this marker sits is treated exactly
    /// like a line painted on the road: the car brakes smoothly on approach
    /// and is guaranteed to stop with its front bumper AT s, not past it —
    /// see CarDriver.Update(), which clamps position to the stop line the
    /// instant it would be crossed, rather than detecting overshoot after.
    ///
    /// Cruise — overrides the track's default speed limit from S to EndS.
    /// Turn   — no forced physics; the spline's curvature drives cornering.
    /// </summary>
    [ExecuteAlways]
    public class TrackEvent : MonoBehaviour
    {
        [Header("Identity")]
        public TrackEventKind kind = TrackEventKind.StopAndGo;

        [Tooltip("Arc-length position on the track (metres from the start). " +
                 "Set automatically by dragging the marker in the Scene view.")]
        [SerializeField] private float s;

        [Tooltip("Zone kinds only: arc-length of the zone's far edge.")]
        [SerializeField] private float endS;

        // ── StopAndGo ────────────────────────────────────────────────────
        [Tooltip("Seconds the car waits at the line before pulling away.")]
        public float waitDuration = 3f;

        // ── Cruise ──────────────────────────────────────────────────────
        [Tooltip("Max speed inside this cruise zone, overriding the track default.")]
        public float limitKmh = 30f;

        public float S => s;
        public float EndS => Mathf.Max(endS, s);
        /// <summary>Kinds that span a start→end range (have an EndS far edge and
        /// an end handle): Cruise and Turn. StopAndGo is a single point.</summary>
        public bool IsRanged => kind == TrackEventKind.Cruise || kind == TrackEventKind.Turn;
        /// <summary>Halts the car at the line.</summary>
        public bool IsStop => kind == TrackEventKind.StopAndGo;

        public static Color KindColor(TrackEventKind k) => k switch
        {
            TrackEventKind.StopAndGo => new Color(0.95f, 0.15f, 0.15f), // red
            TrackEventKind.Cruise    => new Color(0.20f, 0.85f, 0.95f), // cyan
            TrackEventKind.Turn      => new Color(0.70f, 0.45f, 0.95f), // purple
            _                        => Color.white
        };

        /// <summary>Sets the GameObject name to "{ordinal} - {kind} ({range})" —
        /// called by Track.RebuildEventIndex, which already knows every event's
        /// sorted position. Only touches .name when it actually changed, so this
        /// is cheap to call on every rebuild (up to several times a second while
        /// dragging markers around in Edit mode).</summary>
        public void ApplyDisplayName(int ordinal)
        {
            string range = IsRanged ? $"{S:F0}-{EndS:F0} m" : $"{S:F0} m";
            string newName = $"{ordinal} - {kind} ({range})";
            if (gameObject.name != newName) gameObject.name = newName;
        }

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

        private void OnEnable() => RefreshMarker();
        private void OnDisable() => DestroyMarker();

        // ── Always-visible marker (real geometry, not a Gizmo) ───────────
        // Gizmos need the Scene view's Gizmos toggle on, only ever show in
        // Scene view, and are faint — easy to lose track of an event while
        // authoring. This builds real, unlit, kind-coloured primitives
        // instead: a pole at S (+ a second pole and a ribbon to EndS for
        // ranged kinds), visible in Scene AND Game view with no toggle
        // dependency. Gated on the SAME Track.showDebugGizmos flag that
        // already governs the road's own always-visible debug line, so
        // there's still one master on/off switch.
        private GameObject _markerRoot;
        private TrackEventKind _markerKind;
        private float _markerS = float.NaN, _markerEndS, _markerLimit;
        private static Material _markerMat;

        private void RefreshMarker()
        {
            if (Track == null) return;
            // Self-sufficient rather than trusting Track's own Awake/OnEnable to
            // have already run: neither component has an explicit script
            // execution order, so at Play start TrackEvent.OnEnable can fire
            // BEFORE Track has built its arc-length LUT. When that LUT is empty,
            // EvaluatePosition resolves everything to t=0 — every marker lands
            // at the very start of the road. force:false makes this a cheap
            // no-op once Track has already built (the common case).
            Track.EnsureBuilt();
            bool visible = Track.showDebugGizmos;

            if (_markerRoot == null)
            {
                _markerRoot = new GameObject("[marker]") { hideFlags = HideFlags.DontSave };
                _markerRoot.transform.SetParent(transform, false);
            }
            _markerRoot.SetActive(visible);
            if (!visible) return;

            // Rebuild only when something that changes the shape/colour has
            // actually moved — called every edit-mode Update, so this must
            // stay cheap when nothing changed.
            if (kind == _markerKind && Mathf.Approximately(s, _markerS) &&
                Mathf.Approximately(EndS, _markerEndS) && Mathf.Approximately(limitKmh, _markerLimit))
                return;
            _markerKind = kind; _markerS = s; _markerEndS = EndS; _markerLimit = limitKmh;

            for (int i = _markerRoot.transform.childCount - 1; i >= 0; i--)
                DestroyImmediateOrRuntime(_markerRoot.transform.GetChild(i).gameObject);

            Color col = kind == TrackEventKind.Cruise ? Simulation.Track.SpeedLimitColor(limitKmh) : KindColor(kind);
            Vector3 p0 = Track.EvaluatePosition(s);
            AddPole(p0, col, 3f);

            if (IsRanged)
            {
                Vector3 p1 = Track.EvaluatePosition(EndS);
                AddPole(p1, col, 2f);
                AddRibbon(col);
            }
        }

        private void DestroyMarker()
        {
            if (_markerRoot != null) DestroyImmediateOrRuntime(_markerRoot);
            _markerRoot = null;
        }

        private static void DestroyImmediateOrRuntime(UnityEngine.Object o)
        {
            if (Application.isPlaying) Destroy(o); else DestroyImmediate(o);
        }

        private void AddPole(Vector3 basePos, Color col, float height)
        {
            var mat = MarkerMaterial();
            var poleMesh = Resources.GetBuiltinResource<Mesh>("Cylinder.fbx");
            var capMesh = Resources.GetBuiltinResource<Mesh>("Sphere.fbx");

            var pole = new GameObject("pole") { hideFlags = HideFlags.DontSave };
            pole.transform.SetParent(_markerRoot.transform, false);
            pole.transform.position = basePos + Vector3.up * (height * 0.5f);
            pole.transform.localScale = new Vector3(0.12f, height * 0.5f, 0.12f); // built-in cylinder is 2m tall
            AddMeshRenderer(pole, poleMesh, mat, col);

            var cap = new GameObject("cap") { hideFlags = HideFlags.DontSave };
            cap.transform.SetParent(_markerRoot.transform, false);
            cap.transform.position = basePos + Vector3.up * (height + 0.3f);
            cap.transform.localScale = Vector3.one * 0.6f; // built-in sphere is 1m diameter
            AddMeshRenderer(cap, capMesh, mat, col);
        }

        // A dashed-look ribbon along the road from S to EndS, real
        // LineRenderer geometry (same technique Track already uses for its
        // own debug line) rather than a Gizmo, so it's toggle-independent.
        private void AddRibbon(Color col)
        {
            var ribbon = new GameObject("ribbon") { hideFlags = HideFlags.DontSave };
            ribbon.transform.SetParent(_markerRoot.transform, false);
            var lr = ribbon.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.widthMultiplier = 0.25f;
            lr.numCapVertices = 2;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.material = MarkerMaterial();
            lr.startColor = lr.endColor = col;

            int steps = Mathf.Clamp(Mathf.CeilToInt((EndS - s) / 2f), 2, 400);
            var pts = new Vector3[steps + 1];
            for (int i = 0; i <= steps; i++)
                pts[i] = Track.EvaluatePosition(s + (EndS - s) * i / steps) + Vector3.up * 0.35f;
            lr.positionCount = pts.Length;
            lr.SetPositions(pts);
        }

        private static void AddMeshRenderer(GameObject go, Mesh mesh, Material mat, Color col)
        {
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            var mpb = new MaterialPropertyBlock();
            mpb.SetColor("_Color", col);
            mr.SetPropertyBlock(mpb);
        }

        private static Material MarkerMaterial()
        {
            if (_markerMat == null)
            {
                var shader = Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default");
                _markerMat = new Material(shader) { hideFlags = HideFlags.DontSave };
            }
            return _markerMat;
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
        // feels like sliding a bead along the road. Also keeps the
        // always-visible marker in sync as fields change in the Inspector.
        private void Update()
        {
            if (Application.isPlaying) return;
            if (Track == null) return;
            if (!_hasSnapped || (transform.position - _lastSnappedPos).sqrMagnitude > 1e-6f)
            {
                Track.EnsureBuiltEditor();
                s = Track.ProjectWorldPoint(transform.position, out Vector3 onTrack);
                transform.position = onTrack;
                _lastSnappedPos = onTrack;
                _hasSnapped = true;
                if (endS < s) endS = s + (IsRanged ? 50f : 0f);
            }
            RefreshMarker();
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

        private void OnDrawGizmos()
        {
            if (Track == null) return;
            Track.EnsureBuiltEditor();

            // Cruise zones colour-match the track's per-limit section colours;
            // everything else uses its kind colour.
            Color col = kind == TrackEventKind.Cruise
                ? Simulation.Track.SpeedLimitColor(limitKmh)
                : KindColor(kind);
            Gizmos.color = col;
            Vector3 p = Track.EvaluatePosition(s);

            if (!IsRanged)
            {
                // Point stop (StopAndGo): draw the stop LINE across the road,
                // not just a dot.
                Vector3 fwd = Track.EvaluateTangent(s);
                Vector3 side = Vector3.Cross(Vector3.up, fwd).normalized * 2f;
                Gizmos.DrawLine(p - side + Vector3.up * 0.05f, p + side + Vector3.up * 0.05f);
                Gizmos.DrawSphere(p + Vector3.up * 0.5f, 0.5f);
                Gizmos.DrawLine(p, p + Vector3.up * 3f);
            }
            else
            {
                // Ranged (Cruise / Turn): dot at start, dashes along, ring at end.
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

            string label = kind == TrackEventKind.Cruise ? $"Cruise ≤{limitKmh:F0} km/h  (s={s:F0}m)"
                         : IsRanged                       ? $"{kind}  (s={s:F0}→{EndS:F0}m)"
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

            if (ev.IsRanged)
            {
                EditorGUI.BeginChangeCheck();
                float newEnd = EditorGUILayout.FloatField("End S (m along track)", ev.EndS);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(ev, "Resize range");
                    ev.SetEndS(newEnd);
                }
            }

            EditorGUILayout.Space();
            switch (kind)
            {
                case TrackEventKind.StopAndGo:
                    EditorGUILayout.LabelField("Stop-and-go", EditorStyles.boldLabel);
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("waitDuration"));
                    break;

                case TrackEventKind.Cruise:
                    EditorGUILayout.LabelField("Cruise zone", EditorStyles.boldLabel);
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("limitKmh"),
                        new GUIContent("Max speed (km/h)"));
                    break;

                case TrackEventKind.Turn:
                    EditorGUILayout.LabelField("Turn (curve context)", EditorStyles.boldLabel);
                    EditorGUILayout.HelpBox("The road curves via the spline itself; this just " +
                        "marks the turn's start→end for the driving-context analysis. Drag the " +
                        "spline knots to shape the actual bend.", MessageType.None);
                    break;
            }

            serializedObject.ApplyModifiedProperties();
        }

        private void OnSceneGUI()
        {
            var ev = (TrackEvent)target;
            if (!ev.IsRanged || ev.Track == null) return;
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
