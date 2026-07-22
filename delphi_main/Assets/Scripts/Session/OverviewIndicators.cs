using System.Collections.Generic;
using UnityEngine;
using Delphi.Simulation;

namespace Delphi.Session
{
    /// <summary>
    /// Top-down markers for the researcher's Scene-overview feed.
    ///
    /// The overview camera has to frame the WHOLE track, which on a 136 m
    /// track renders a 4.5 m car at roughly seven pixels and a marker pole at
    /// under one — everything real is too small to find. These are deliberately
    /// out-of-scale flat discs/arrows (tens of metres across) whose only job is
    /// to be legible from directly above at that framing.
    ///
    /// They live on their own layer, which every OTHER camera excludes, so the
    /// participant never sees them: this is researcher instrumentation drawn
    /// into the world, not part of the simulated scene.
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

        [Header("Sizes (world metres — these are intentionally not to scale)")]
        [Tooltip("Diameter of the car's disc.")]
        public float carSize = 14f;
        [Tooltip("Diameter of a track-event disc.")]
        public float eventSize = 10f;
        [Tooltip("Height above the road to draw at, so nothing z-fights.")]
        public float drawHeight = 2f;

        [Header("Colours")]
        public Color carColor = new Color(1f, 0.95f, 0.2f, 1f);

        private Transform _carMarker;
        private readonly List<GameObject> _spawned = new();
        private static Material _mat;

        private void Start()
        {
            if (car == null) car = FindFirstObjectByType<CarDriver>();
            if (track == null) track = FindFirstObjectByType<Track>();
            if (overviewCamera == null)
                foreach (var c in Camera.allCameras)
                    if (c.name == "Overview Camera") overviewCamera = c;

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
            foreach (var ev in track.Events)
            {
                Vector3 pos = track.EvaluatePosition(ev.S);
                var col = TrackEvent.KindColor(ev.kind);
                _spawned.Add(Disc($"[overview] {ev.kind} {ev.S:F0}m", pos, eventSize, col));
            }

            _carMarker = Disc("[overview] Car", car.transform.position, carSize, carColor).transform;
        }

        private void LateUpdate()
        {
            if (_carMarker == null || car == null) return;
            Vector3 p = car.transform.position;
            _carMarker.position = new Vector3(p.x, drawHeight + 0.1f, p.z);
            // Point the disc along the car's heading so the feed shows which
            // way it is travelling, not just where it is.
            _carMarker.rotation = Quaternion.Euler(90f, car.transform.eulerAngles.y, 0f);
        }

        private GameObject Disc(string name, Vector3 pos, float size, Color col)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = name;
            go.layer = indicatorLayer;
            go.transform.SetParent(transform, false);
            go.transform.position = new Vector3(pos.x, drawHeight, pos.z);
            go.transform.rotation = Quaternion.Euler(90f, 0f, 0f); // face straight up
            go.transform.localScale = Vector3.one * size;

            var collider = go.GetComponent<Collider>();
            if (collider != null) Destroy(collider); // pure visual, never blocks a raycast

            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = MarkerMaterial();
            r.material.color = col;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
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
