using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Delphi.Simulation;

namespace Delphi.Session
{
    /// <summary>
    /// The researcher's overhead-camera visualization — ENTIRELY separate
    /// from Track/TrackEvent's own edit-time visuals (the thin debug line and
    /// pole/flag markers used while authoring the road). Those stay exactly
    /// as they've always been; this draws its own bold route line and small
    /// ball markers, purpose-built for legibility from directly above on a
    /// track that can run into the thousands of metres.
    ///
    /// Everything here lives on its own layer, which every OTHER camera
    /// excludes, so the participant never sees it and it never contaminates
    /// their driving view — this is researcher instrumentation only.
    /// </summary>
    public class OverviewIndicators : MonoBehaviour
    {
        [Header("Links (auto-found if left empty)")]
        public CarDriver car;
        public Track track;
        [Tooltip("The camera these markers are FOR. Every other camera has the " +
                 "indicator layer removed from its culling mask.")]
        public Camera overviewCamera;

        [Header("Layer")]
        [Tooltip("Layer index used exclusively by these indicators. Must be a " +
                 "layer no real scene geometry uses.")]
        public int indicatorLayer = 8;

        [Header("Route line — bold, THIS view only (Track's own edit-time " +
                 "debug line is untouched and stays thin). ONE continuous " +
                 "line, not two overlaid ones — it just changes colour over " +
                 "Turn zones, so there's nothing to z-fight against itself.")]
        public Color routeLineColor = new Color(0.25f, 0.85f, 0.3f);
        [Min(0.1f)] public float routeLineWidth = 10f;
        [Tooltip("Sample spacing along the spline, metres — finer follows " +
                 "curves more faithfully, coarser is cheaper to build.")]
        [Min(1f)] public float routeSampleSpacingMeters = 8f;

        [Header("Ball sizes (world metres)")]
        public float carBallSize = 140f;
        public float eventBallSize = 100f;

        [Header("Heights above the road (metres) — the car ball draws " +
                 "ABOVE EVERYTHING else, everything else is flush with the " +
                 "single route line now that it's not sharing space with a " +
                 "second overlaid line")]
        public float routeHeight = 2f;
        public float eventBallHeight = 2.2f;
        public float carHeight = 3.2f;

        [Header("Colours")]
        [Tooltip("StopAndGo/Turn/Park already have their own colours (red/" +
                 "purple/blue) via TrackEvent.KindColor — this is only the car.")]
        public Color carColor = Color.white;

        private Transform _carMarker;
        private readonly List<GameObject> _spawned = new();
        private static Material _mat;

        private void Start()
        {
            if (car == null) car = FindFirstObjectByType<CarDriver>();
            if (track == null) track = FindFirstObjectByType<Track>();
            if (overviewCamera == null)
                foreach (var c in Camera.allCameras)
                    if (c.name == "Track Overview Camera") overviewCamera = c;

            if (car == null || track == null)
            {
                Debug.LogWarning("[OverviewIndicators] No CarDriver/Track found — indicators disabled.");
                enabled = false;
                return;
            }

            ApplyCullingMasks();
            Build();
        }

        /// <summary>Only the overview camera renders the indicator layer. Done
        /// in code rather than by hand so adding a camera later can't silently
        /// leak researcher markers into the participant's view.</summary>
        private void ApplyCullingMasks()
        {
            int bit = 1 << indicatorLayer;
            foreach (var c in Camera.allCameras)
            {
                if (c == overviewCamera) c.cullingMask |= bit;
                else                     c.cullingMask &= ~bit;
            }
        }

        private void Build()
        {
            BuildRouteLine();

            foreach (var ev in track.Events)
            {
                switch (ev.kind)
                {
                    case TrackEventKind.StopAndGo:
                        Ball($"[overview] StopAndGo {ev.S:F0}m", track.EvaluatePosition(ev.S),
                             eventBallSize, TrackEvent.KindColor(ev.kind), eventBallHeight);
                        break;
                    case TrackEventKind.Park:
                        Ball($"[overview] Park {ev.S:F0}m", track.EvaluatePosition(ev.S),
                             eventBallSize, TrackEvent.KindColor(ev.kind), eventBallHeight);
                        break;
                    case TrackEventKind.Turn:
                        var col = TrackEvent.KindColor(TrackEventKind.Turn);
                        Ball($"[overview] Turn start {ev.S:F0}m", track.EvaluatePosition(ev.S), eventBallSize, col, eventBallHeight);
                        Ball($"[overview] Turn end {ev.EndS:F0}m", track.EvaluatePosition(ev.EndS), eventBallSize, col, eventBallHeight);
                        break;
                    // Cruise has no ball here — wasn't asked for. Say the word
                    // if you want one (it already has its own colour via
                    // TrackEvent.KindColor, same as the other three).
                }
            }

            _carMarker = Ball("[overview] Car", car.transform.position, carBallSize, carColor, carHeight).transform;
        }

        /// <summary>ExperimentUI's ball-size/route-width Inspector sliders
        /// call this — sets the fields then rebuilds ONLY if something
        /// actually changed, since Build() bakes sizes/widths into the
        /// spawned geometry once and won't react to the fields changing on
        /// their own afterward.</summary>
        public void ApplyTuning(float newCarBallSize, float newEventBallSize, float newRouteLineWidth)
        {
            bool changed = !Mathf.Approximately(carBallSize, newCarBallSize)
                        || !Mathf.Approximately(eventBallSize, newEventBallSize)
                        || !Mathf.Approximately(routeLineWidth, newRouteLineWidth);

            carBallSize = newCarBallSize;
            eventBallSize = newEventBallSize;
            routeLineWidth = newRouteLineWidth;

            if (changed && _spawned.Count > 0) Rebuild();
        }

        private void Rebuild()
        {
            foreach (var go in _spawned) if (go != null) Destroy(go);
            _spawned.Clear();
            Build();
        }

        /// <summary>ONE continuous line the whole length of the track — it
        /// just switches to Turn's colour for the stretch inside each Turn
        /// zone, instead of drawing a second line on top of the first at a
        /// different height. Two overlapping lines was exactly what was
        /// fighting/z-fighting before; a single path that changes colour
        /// can't fight itself.</summary>
        private void BuildRouteLine()
        {
            var turnRanges = new List<(float from, float to)>();
            foreach (var ev in track.Events)
                if (ev.kind == TrackEventKind.Turn)
                    turnRanges.Add((Mathf.Min(ev.S, ev.EndS), Mathf.Max(ev.S, ev.EndS)));
            turnRanges.Sort((a, b) => a.from.CompareTo(b.from));

            var turnColor = TrackEvent.KindColor(TrackEventKind.Turn);
            float total = track.TotalLength;
            float cursor = 0f;

            foreach (var (from, to) in turnRanges)
            {
                float segStart = Mathf.Clamp(from, cursor, total);
                if (segStart > cursor)
                    BuildLine("[overview] Route", cursor, segStart, routeLineColor, routeLineWidth, routeHeight);

                float segEnd = Mathf.Clamp(to, segStart, total);
                if (segEnd > segStart)
                    BuildLine("[overview] Route (turn)", segStart, segEnd, turnColor, routeLineWidth, routeHeight);

                cursor = Mathf.Max(cursor, segEnd);
            }

            if (cursor < total)
                BuildLine("[overview] Route", cursor, total, routeLineColor, routeLineWidth, routeHeight);
        }

        private void BuildLine(string name, float sFrom, float sTo, Color col, float width, float height)
        {
            var go = new GameObject(name) { layer = indicatorLayer };
            go.transform.SetParent(transform, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.widthMultiplier = width;
            lr.numCapVertices = 4;
            lr.shadowCastingMode = ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.material = MarkerMaterial();
            // The Unlit shader doesn't multiply by LineRenderer's own per-
            // vertex colors, so startColor/endColor alone rendered as the
            // material's default white — same fix the sphere balls already
            // use below: instantiate a per-object clone (accessing .material
            // does this automatically) and tint THAT.
            lr.material.color = col;
            lr.startColor = lr.endColor = col;

            int steps = Mathf.Clamp(Mathf.CeilToInt(Mathf.Abs(sTo - sFrom) / routeSampleSpacingMeters), 2, 4000);
            var pts = new Vector3[steps + 1];
            for (int i = 0; i <= steps; i++)
            {
                float s = Mathf.Lerp(sFrom, sTo, i / (float)steps);
                Vector3 p = track.EvaluatePosition(s);
                pts[i] = new Vector3(p.x, height, p.z);
            }
            lr.positionCount = pts.Length;
            lr.SetPositions(pts);
            _spawned.Add(go);
        }

        private void LateUpdate()
        {
            if (_carMarker == null || car == null) return;
            Vector3 p = car.transform.position;
            _carMarker.position = new Vector3(p.x, carHeight, p.z);
            // A sphere looks identical at any rotation, so unlike the old flat
            // disc there's nothing to orient toward heading here.
        }

        private GameObject Ball(string name, Vector3 pos, float size, Color col, float height)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = name;
            go.layer = indicatorLayer;
            go.transform.SetParent(transform, false);
            go.transform.position = new Vector3(pos.x, height, pos.z);
            go.transform.localScale = Vector3.one * size;

            var collider = go.GetComponent<Collider>();
            if (collider != null) Destroy(collider); // pure visual, never blocks a raycast

            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = MarkerMaterial();
            r.material.color = col;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            _spawned.Add(go);
            return go;
        }

        private static Material MarkerMaterial()
        {
            if (_mat != null) return _mat;
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            _mat = new Material(shader) { hideFlags = HideFlags.DontSave };
            return _mat;
        }

        private void OnDestroy()
        {
            foreach (var go in _spawned) if (go != null) Destroy(go);
            _spawned.Clear();
        }
    }
}
