using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Splines;

namespace Delphi.Simulation
{
    /// <summary>
    /// THE road. One continuous, hand-authored spline (open, A-to-B), single
    /// lane, that the ego drives along. The researcher draws the whole map
    /// as a single spline and then scatters TrackEvent markers (red lights,
    /// speed zones) along it by hand.
    ///
    /// Everything positional in the simulation lives in "route space": a 1-D
    /// coordinate s = metres of arc length from the start of the spline.
    /// This class owns the s ↔ spline-t mapping (an arc-length lookup table,
    /// so distance is metrically honest even where spline knots are unevenly
    /// spaced) plus the sorted event registry.
    ///
    /// Deliberately out of scope for now (re-add later, one at a time):
    /// traffic/NPCs, a passing lane, overtaking.
    /// </summary>
    [ExecuteAlways]
    [RequireComponent(typeof(SplineContainer))]
    public class Track : MonoBehaviour
    {
        [Header("Road")]
        public SplineContainer splineContainer;

        [Tooltip("Posted speed limit anywhere no Cruise zone overrides it.")]
        public float defaultSpeedLimitKmh = 50f;

        [Header("Arc-length sampling")]
        [Tooltip("LUT samples per metre of track. 1 is plenty for road-scale " +
                 "curvature; raise it only if hairpins look faceted.")]
        public float samplesPerMeter = 1f;

        [Tooltip("Half-width (m) of the three-point stencil CurvatureAt uses. " +
                 "Must be comfortably WIDER than the LUT spacing above: at 1 " +
                 "sample/metre, a stencil of a metre or two mostly measures " +
                 "the LUT's own linear-interpolation error, and that noise " +
                 "then flickers the car's corner slowdown — and therefore its " +
                 "target speed — every single frame. 6 m buries the noise and " +
                 "still resolves the ~10 m bend CarDriver treats as 'tight'.")]
        public float curvatureSampleSpanMeters = 6f;

        [Header("Debug")]
        [Tooltip("Draw the driving line, event markers, and distance labels. " +
                 "The line/markers are REAL geometry (built and kept live in " +
                 "BOTH edit and Play mode), so they're visible in Scene AND " +
                 "Game view with no Gizmos-toggle dependency; the distance-" +
                 "marker labels are Scene-view Gizmos on top (need that " +
                 "view's own Gizmos toggle).")]
        public bool showDebugGizmos = true;
        [Tooltip("Spacing (m) between the small distance-marker dots/labels " +
                 "(Scene-view gizmo only).")]
        public float gizmoMarkerSpacingMeters = 50f;
        [Tooltip("Width (m) of the runtime debug line drawn in the Game view.")]
        public float debugLineWidth = 0.35f;

        /// <summary>Fired once in Play mode after the LUT + event registry are
        /// built. Subscribe in Awake/OnEnable; if IsReady is already true you
        /// missed it and can just proceed.</summary>
        public event Action OnTrackReady;
        public bool IsReady { get; private set; }

        public float TotalLength => _cumS != null && _cumS.Length > 0 ? _cumS[_cumS.Length - 1] : 0f;
        public IReadOnlyList<TrackEvent> Events => _events;

        /// <summary>A run of track with one constant posted limit. The whole
        /// track partitions into these: default-limit stretches between
        /// SpeedZone markers, plus the zones themselves.</summary>
        public struct SpeedSection
        {
            public float startS;
            public float endS;
            public float limitKmh;
        }

        // ── Internals ───────────────────────────────────────────────────
        private float[] _cumS;        // cumulative arc length at uniform t samples
        private int _sampleCount;
        private readonly List<TrackEvent> _events = new();
        private readonly List<TrackEvent> _stops = new();     // StopAndGo, sorted by S
        private readonly List<TrackEvent> _cruiseZones = new(); // Cruise, sorted by S
        private readonly List<TrackEvent> _turns = new();      // Turn, sorted by S
        private readonly List<TrackEvent> _parks = new();      // Park, sorted by S

        /// <summary>Cruise zones (speed overrides), sorted by S — for authoring/analysis.</summary>
        public IReadOnlyList<TrackEvent> CruiseZones => _cruiseZones;
        /// <summary>Stop events, sorted by S.</summary>
        public IReadOnlyList<TrackEvent> Stops => _stops;
        /// <summary>Turn context markers, sorted by S.</summary>
        public IReadOnlyList<TrackEvent> Turns => _turns;
        /// <summary>Park markers, sorted by S.</summary>
        public IReadOnlyList<TrackEvent> Parks => _parks;
        private bool _built;
        private double _lastEditorBuildTime;
        private GameObject _debugLineRoot;
        private readonly List<LineRenderer> _debugLines = new();

        private void Awake()
        {
            if (splineContainer == null) splineContainer = GetComponent<SplineContainer>();
            if (!Application.isPlaying) return;

            EnsureBuilt(force: true);
            IsReady = true;
            OnTrackReady?.Invoke();
            Debug.Log($"[Track] Ready — {TotalLength:F0} m, {_events.Count} events " +
                      $"({_stops.Count} stops, {_cruiseZones.Count} cruise zones, {_turns.Count} turns).");

            RefreshDebugVisual();
        }

        // [ExecuteAlways]: also runs in Edit mode, e.g. on scene load or when
        // the component is (re)enabled — so the road is visible immediately,
        // not only after pressing Play or waiting on a Gizmo repaint.
        private void OnEnable()
        {
            if (splineContainer == null) splineContainer = GetComponent<SplineContainer>();
            if (Application.isPlaying) return;
            EnsureBuilt(force: true);
            RefreshDebugVisual();
        }

#if UNITY_EDITOR
        private double _lastDebugVisualRefreshTime;
#endif

        private void Update()
        {
            // Toggling the checkbox just flips visibility — cheap.
            if (_debugLineRoot != null && _debugLineRoot.activeSelf != showDebugGizmos)
                _debugLineRoot.SetActive(showDebugGizmos);

#if UNITY_EDITOR
            // Keep the road line (and every TrackEvent's own marker, which
            // reads this same geometry indirectly via Track) live while
            // dragging spline knots or event markers around in Edit mode.
            // Play mode geometry is static once Awake runs — no need to
            // rebuild every frame there.
            if (!Application.isPlaying && showDebugGizmos)
            {
                double now = UnityEditor.EditorApplication.timeSinceStartup;
                if (now - _lastDebugVisualRefreshTime > 0.25)
                {
                    _lastDebugVisualRefreshTime = now;
                    EnsureBuiltEditor();
                    RefreshDebugVisual();
                }
            }
#endif
        }

        // One LineRenderer per constant-limit section, coloured by that
        // section's posted limit — REAL geometry (not a Gizmo), visible in
        // Scene AND Game view regardless of any Gizmos toggle, live in both
        // Edit and Play mode. Edit-mode-built objects are marked DontSave so
        // they never bloat the scene file — they're rebuilt fresh whenever
        // the scene (re)loads via OnEnable.
        private void RefreshDebugVisual()
        {
            if (_debugLineRoot != null) DestroyImmediateOrRuntime(_debugLineRoot);
            _debugLines.Clear();
            _debugLineRoot = null;
            if (_sampleCount == 0) return;

            _debugLineRoot = new GameObject("[debug] Track line") { hideFlags = HideFlags.DontSave };
            _debugLineRoot.transform.SetParent(transform, false);
            var mat = new Material(Shader.Find("Sprites/Default")) { hideFlags = HideFlags.DontSave };

            foreach (var sec in GetSpeedSections())
            {
                var go = new GameObject($"section {sec.startS:F0}-{sec.endS:F0}m @{sec.limitKmh:F0}kmh")
                    { hideFlags = HideFlags.DontSave };
                go.transform.SetParent(_debugLineRoot.transform, false);
                var lr = go.AddComponent<LineRenderer>();
                lr.useWorldSpace = true;
                lr.widthMultiplier = debugLineWidth;
                lr.numCapVertices = 2;
                lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                lr.receiveShadows = false;
                lr.material = mat;
                lr.startColor = lr.endColor = SpeedLimitColor(sec.limitKmh);

                float len = sec.endS - sec.startS;
                int steps = Mathf.Clamp(Mathf.CeilToInt(len / 4f), 2, 4000);
                var points = new Vector3[steps + 1];
                for (int i = 0; i <= steps; i++)
                    points[i] = EvaluatePosition(sec.startS + len * i / steps) + Vector3.up * 0.05f;
                lr.positionCount = points.Length;
                lr.SetPositions(points);
                _debugLines.Add(lr);
            }
            _debugLineRoot.SetActive(showDebugGizmos);
        }

        private void OnDisable()
        {
            if (_debugLineRoot != null) DestroyImmediateOrRuntime(_debugLineRoot);
            _debugLineRoot = null;
            _debugLines.Clear();
        }

        private static void DestroyImmediateOrRuntime(UnityEngine.Object o)
        {
            if (Application.isPlaying) Destroy(o); else DestroyImmediate(o);
        }

        private void Reset() => splineContainer = GetComponent<SplineContainer>();

        // ── Building ────────────────────────────────────────────────────
        /// <summary>Build (or rebuild) the arc-length LUT and event index.
        /// Cheap to call repeatedly — only rebuilds when forced or unbuilt.</summary>
        public void EnsureBuilt(bool force = false)
        {
            if (_built && !force) return;
            if (splineContainer == null) splineContainer = GetComponent<SplineContainer>();
            if (splineContainer == null || splineContainer.Spline == null ||
                splineContainer.Spline.Count < 2)
            {
                _cumS = new float[] { 0f };
                _sampleCount = 0;
                _built = false;
                RebuildEventIndex(); // keep the registry honest even with no usable geometry
                return;
            }

            float rough = SplineUtility.CalculateLength(
                splineContainer.Spline, splineContainer.transform.localToWorldMatrix);
            _sampleCount = Mathf.Clamp(Mathf.CeilToInt(rough * Mathf.Max(samplesPerMeter, 0.1f)), 64, 16384);

            _cumS = new float[_sampleCount + 1];
            Vector3 prev = EvalSplineWorld(0f);
            _cumS[0] = 0f;
            for (int i = 1; i <= _sampleCount; i++)
            {
                Vector3 p = EvalSplineWorld((float)i / _sampleCount);
                _cumS[i] = _cumS[i - 1] + Vector3.Distance(prev, p);
                prev = p;
            }

            RebuildEventIndex();
            _built = true;
        }

        /// <summary>Edit-mode variant used by TrackEvent snapping/gizmos —
        /// rebuilds at most a few times per second so dragging stays smooth.</summary>
        public void EnsureBuiltEditor()
        {
#if UNITY_EDITOR
            double now = UnityEditor.EditorApplication.timeSinceStartup;
            if (!_built || now - _lastEditorBuildTime > 0.25)
            {
                _lastEditorBuildTime = now;
                EnsureBuilt(force: true);
            }
#else
            EnsureBuilt();
#endif
        }

        /// <summary>Re-collect and sort all TrackEvent children. Call after
        /// adding/moving markers at runtime (rare) — done automatically on build.</summary>
        public void RebuildEventIndex()
        {
            _events.Clear(); _stops.Clear(); _cruiseZones.Clear(); _turns.Clear(); _parks.Clear();
            GetComponentsInChildren(includeInactive: false, _events);
            _events.Sort((a, b) => a.S.CompareTo(b.S));
            for (int i = 0; i < _events.Count; i++)
            {
                var ev = _events[i];
                switch (ev.kind)
                {
                    case TrackEventKind.StopAndGo: _stops.Add(ev);       break;
                    case TrackEventKind.Cruise:    _cruiseZones.Add(ev); break;
                    case TrackEventKind.Turn:      _turns.Add(ev);       break;
                    case TrackEventKind.Park:      _parks.Add(ev);      break;
                }
                // Descriptive, order-and-position GameObject name — kept live
                // by the same throttled rebuild that keeps the LUT current, so
                // it tracks dragging in the Hierarchy in near-real-time:
                // "1 - StopAndGo (42 m)", "2 - Turn (68-71 m)".
                ev.ApplyDisplayName(i + 1);
            }
        }

        // ── s ↔ t mapping ───────────────────────────────────────────────
        /// <summary>Spline parameter t for a given arc length s (clamped).</summary>
        public float ToT(float s)
        {
            if (_sampleCount == 0) return 0f;
            s = Mathf.Clamp(s, 0f, TotalLength);
            int lo = 0, hi = _sampleCount;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (_cumS[mid] <= s) lo = mid; else hi = mid;
            }
            float span = _cumS[hi] - _cumS[lo];
            float frac = span > 1e-6f ? (s - _cumS[lo]) / span : 0f;
            return (lo + frac) / _sampleCount;
        }

        /// <summary>Arc length s for a given spline parameter t (clamped).</summary>
        public float ToS(float t)
        {
            if (_sampleCount == 0) return 0f;
            float x = Mathf.Clamp01(t) * _sampleCount;
            int i = Mathf.Min(Mathf.FloorToInt(x), _sampleCount - 1);
            return Mathf.Lerp(_cumS[i], _cumS[i + 1], x - i);
        }

        // ── Geometry queries ────────────────────────────────────────────
        /// <summary>World position at arc length s (on the single driving line).</summary>
        public Vector3 EvaluatePosition(float s) => EvalSplineWorld(ToT(s));

        /// <summary>Normalized driving direction at s (finite difference).</summary>
        public Vector3 EvaluateTangent(float s)
        {
            const float h = 0.5f;
            Vector3 a = EvalSplineWorld(ToT(Mathf.Max(0f, s - h)));
            Vector3 b = EvalSplineWorld(ToT(Mathf.Min(TotalLength, s + h)));
            Vector3 d = b - a;
            return d.sqrMagnitude > 1e-8f ? d.normalized : transform.forward;
        }

        /// <summary>Curvature magnitude (1/radius, metres) at s — three-point
        /// method, no dependency on any Splines-package curvature API.</summary>
        public float CurvatureAt(float s) => Mathf.Abs(SignedCurvatureAt(s));

        /// <summary>Curvature at s WITH a turn direction: positive = the road
        /// bends right (clockwise seen from above), negative = left. Sign
        /// comes from the cross product of the two sample chords, so it's the
        /// road's own geometry — nothing here differentiates the vehicle's
        /// transform, which is why this stays smooth frame to frame where a
        /// rotation delta would not.</summary>
        public float SignedCurvatureAt(float s)
        {
            float h = Mathf.Max(1f, curvatureSampleSpanMeters);
            Vector3 a = EvaluatePosition(Mathf.Max(0f, s - h));
            Vector3 c = EvaluatePosition(s);
            Vector3 b = EvaluatePosition(Mathf.Min(TotalLength, s + h));
            Vector3 v1 = c - a, v2 = b - c;
            v1.y = v2.y = 0f;
            if (v1.sqrMagnitude < 1e-6f || v2.sqrMagnitude < 1e-6f) return 0f;
            float angle = Vector3.Angle(v1, v2) * Mathf.Deg2Rad;
            float arc = v2.magnitude;
            if (arc <= 1e-4f) return 0f;
            float sign = Mathf.Sign(Vector3.Cross(v1, v2).y);
            return sign * angle / arc;
        }

        /// <summary>Project a world point onto the track; returns arc length s
        /// and the exact on-spline world position. Used by marker snapping.</summary>
        public float ProjectWorldPoint(Vector3 worldPos, out Vector3 onTrack)
        {
            if (splineContainer == null || splineContainer.Spline == null ||
                splineContainer.Spline.Count < 2)
            {
                onTrack = worldPos;
                return 0f;
            }
            Vector3 local = splineContainer.transform.InverseTransformPoint(worldPos);
            SplineUtility.GetNearestPoint(splineContainer.Spline,
                (Unity.Mathematics.float3)local,
                out Unity.Mathematics.float3 nearest, out float t,
                resolution: 8, iterations: 3);
            onTrack = splineContainer.transform.TransformPoint((Vector3)nearest);
            return ToS(t);
        }

        // ── Rules queries ───────────────────────────────────────────────
        /// <summary>Posted limit at s (m/s conversion is the caller's job).</summary>
        public float SpeedLimitAt(float s)
        {
            // Few zones, linear scan is fine; zones may not overlap (authoring
            // rule — last one wins if they do).
            float limit = defaultSpeedLimitKmh;
            foreach (var z in _cruiseZones)
                if (s >= z.S && s <= z.EndS) limit = z.limitKmh;
            return limit;
        }

        /// <summary>Partition the whole track into constant-limit sections:
        /// section edges are the start/end of every SpeedZone (plus the track
        /// ends), and each span's limit is whatever SpeedLimitAt says in its
        /// middle. Adjacent equal-limit spans are merged.</summary>
        public List<SpeedSection> GetSpeedSections()
        {
            var sections = new List<SpeedSection>();
            if (TotalLength <= 0f) return sections;

            var edges = new List<float> { 0f, TotalLength };
            foreach (var z in _cruiseZones)
            {
                edges.Add(Mathf.Clamp(z.S, 0f, TotalLength));
                edges.Add(Mathf.Clamp(z.EndS, 0f, TotalLength));
            }
            edges.Sort();

            for (int i = 0; i < edges.Count - 1; i++)
            {
                float a = edges[i], b = edges[i + 1];
                if (b - a < 0.01f) continue;
                float limit = SpeedLimitAt((a + b) * 0.5f);
                if (sections.Count > 0 &&
                    Mathf.Approximately(sections[^1].limitKmh, limit))
                {
                    var last = sections[^1];
                    last.endS = b;
                    sections[^1] = last;
                }
                else
                {
                    sections.Add(new SpeedSection { startS = a, endS = b, limitKmh = limit });
                }
            }
            return sections;
        }

        /// <summary>Shared limit → colour mapping for every debug visual
        /// (track line, gizmos, SpeedZone markers): warm red = slow zone,
        /// through green, to cyan-blue = fast. One glance tells the regime.</summary>
        public static Color SpeedLimitColor(float limitKmh)
        {
            float t = Mathf.InverseLerp(20f, 90f, limitKmh);
            return Color.HSVToRGB(Mathf.Lerp(0.02f, 0.55f, t), 0.85f, 1f);
        }

        /// <summary>Earliest red light NOT YET in the caller's served set —
        /// found by served-status alone, deliberately NOT filtered by
        /// position, so an unserved light keeps commanding a stop (its
        /// distance-remaining clamps to 0 once passed) until the vehicle
        /// actually latches the wait. Each vehicle keeps its own served set
        /// so it stops once per light and never re-stops while pulling away.</summary>
        public bool TryNextStop(ICollection<TrackEvent> served,
                                out float stopS, out TrackEvent ev)
        {
            foreach (var st in _stops)
            {
                if (served != null && served.Contains(st)) continue;
                stopS = st.S;
                ev = st;
                return true;
            }
            stopS = float.PositiveInfinity;
            ev = null;
            return false;
        }

        /// <summary>The track's designated parking spot — the FIRST Park
        /// marker by S if more than one exists (multi-park tracks aren't
        /// supported yet). Unlike TryNextStop, this isn't gated by a served
        /// set: CarDriver only consults it while actively heading to park
        /// (see CarDriver.RequestPark), not on every frame.</summary>
        public bool TryGetPark(out TrackEvent park)
        {
            if (_parks.Count > 0) { park = _parks[0]; return true; }
            park = null;
            return false;
        }

        /// <summary>The next Park marker at or ahead of arc-length s. This is
        /// what makes several parks on one track work: the car can only ever
        /// drive forwards, so "park now" has to mean the next one it will
        /// actually reach, not always the first on the track.</summary>
        public bool TryGetParkAhead(float s, out TrackEvent park)
        {
            for (int i = 0; i < _parks.Count; i++)      // _parks is sorted by S
                if (_parks[i].S >= s) { park = _parks[i]; return true; }
            park = null;
            return false;
        }

        // How far a computed pullover point must sit from any StopAndGo/Park
        // marker — so it can never land on (or just short of) a line that
        // already means something else.
        private const float PulloverMarkerClearanceMeters = 15f;

        /// <summary>Scans forward from `fromS` for the nearest point that's
        /// safe to stop a vehicle at on the existing driving line: curvature
        /// under maxAbsCurvature (not mid-corner) and clear of every
        /// StopAndGo/Park marker. Unlike Park, this point is computed on
        /// demand for a real-time pullover, not hand-authored — see
        /// CarDriver.RequestPullover.</summary>
        public bool TryFindSafeStoppingPoint(float fromS, float maxAbsCurvature, float searchAheadMeters, out float resultS)
        {
            const float scanStep = 2f;
            float limit = Mathf.Min(TotalLength, fromS + Mathf.Max(scanStep, searchAheadMeters));

            for (float s = Mathf.Max(0f, fromS); s <= limit; s += scanStep)
            {
                if (CurvatureAt(s) > maxAbsCurvature) continue;

                bool tooClose = false;
                foreach (var st in _stops) if (Mathf.Abs(st.S - s) < PulloverMarkerClearanceMeters) { tooClose = true; break; }
                if (!tooClose)
                    foreach (var pk in _parks) if (Mathf.Abs(pk.S - s) < PulloverMarkerClearanceMeters) { tooClose = true; break; }
                if (tooClose) continue;

                resultS = s;
                return true;
            }
            resultS = 0f;
            return false;
        }

        // ── Debug visualisation (Scene view only, before pressing Play) ──
        private void OnDrawGizmos()
        {
            if (!showDebugGizmos) return;
            EnsureBuiltEditor();
            if (_sampleCount == 0) return;

            float length = TotalLength;

            // Driving line, coloured per speed-limit section.
            foreach (var sec in GetSpeedSections())
            {
                Gizmos.color = SpeedLimitColor(sec.limitKmh);
                float len = sec.endS - sec.startS;
                int steps = Mathf.Clamp(Mathf.CeilToInt(len / 4f), 2, 4000);
                Vector3 prev = EvaluatePosition(sec.startS);
                for (int i = 1; i <= steps; i++)
                {
                    Vector3 cur = EvaluatePosition(sec.startS + len * i / steps);
                    Gizmos.DrawLine(prev, cur);
                    prev = cur;
                }
#if UNITY_EDITOR
                Vector3 mid = EvaluatePosition((sec.startS + sec.endS) * 0.5f);
                UnityEditor.Handles.Label(mid + Vector3.up * 2.2f,
                    $"{sec.limitKmh:F0} km/h",
                    new GUIStyle(UnityEditor.EditorStyles.boldLabel)
                        { normal = { textColor = SpeedLimitColor(sec.limitKmh) } });
#endif
            }

#if UNITY_EDITOR
            Gizmos.color = Color.white;
            for (float s = 0f; s <= length; s += Mathf.Max(5f, gizmoMarkerSpacingMeters))
            {
                Vector3 p = EvaluatePosition(s);
                Gizmos.DrawSphere(p, 0.35f);
                UnityEditor.Handles.Label(p + Vector3.up * 1.4f, $"{s:F0}m");
            }
#endif
        }

        // ── Helpers ─────────────────────────────────────────────────────
        private Vector3 EvalSplineWorld(float t)
        {
            SplineUtility.Evaluate(splineContainer.Spline, t,
                out Unity.Mathematics.float3 pos,
                out Unity.Mathematics.float3 tan,
                out Unity.Mathematics.float3 up);
            return splineContainer.transform.TransformPoint((Vector3)pos);
        }
    }
}
