using UnityEngine;
using UnityEngine.Splines;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Delphi.Simulation
{
    public enum TileKind { Filler, RedLight, CatchUp, Corner }

    /// <summary>
    /// A self-contained road segment. The curve (SplineContainer) is the road
    /// shape; entry is always t=0 and exit is always t=1 on that curve. Each
    /// event kind has its own extra data — the custom inspector at the bottom
    /// of this file shows only the block matching the current Kind.
    ///
    /// Debug markers (and the base plane's colour) react live every frame to
    /// both slider changes AND toggling the checkbox on/off mid-Play.
    /// </summary>
    [RequireComponent(typeof(SplineContainer))]
    public class RouteTile : MonoBehaviour
    {
        [Header("Identity")]
        public TileKind kind = TileKind.Filler;
        public string tileName = "";

        [Header("Road shape")]
        public SplineContainer splineContainer;

        [Header("Debug")]
        public bool showDebugMarkersAtRuntime = true;
        [Tooltip("The ground/road plane for this tile. While debug is on, it's " +
                 "tinted with Debug Color so you can tell tiles apart at a glance.")]
        public Renderer basePlaneRenderer;
        [Tooltip("This tile's own unique colour for the base plane while debugging. " +
                 "Give each tile instance a different one.")]
        public Color debugColor = Color.gray;

        // ── RedLight-specific ───────────────────────────────────────────
        [Range(0f, 1f)] public float stopPointT = 0.5f;
        public float waitDuration = 2f;

        // ── Corner-specific ─────────────────────────────────────────────
        [Range(0f, 1f)] public float cornerStartT = 0.3f;
        [Range(0f, 1f)] public float cornerEndT = 0.7f;

        // ── CatchUp-specific (draft — will likely change) ───────────────
        public float leadCarSpeed = 8f;
        [Range(0f, 1f)] public float catchUpPointT = 0.5f;
        public float holdDuration = 5f;

        [Header("Decoration (this tile's own slots)")]
        public Transform[] decorationSlots;

        public bool IsEvent => kind != TileKind.Filler;

        private void Reset() => splineContainer = GetComponent<SplineContainer>();

        public Vector3 Evaluate(float t)
        {
            if (splineContainer == null || splineContainer.Spline == null) return transform.position;
            SplineUtility.Evaluate(splineContainer.Spline, t,
                out Unity.Mathematics.float3 pos,
                out Unity.Mathematics.float3 tan,
                out Unity.Mathematics.float3 up);
            return splineContainer.transform.TransformPoint((Vector3)pos);
        }

        public Vector3 EntryPosition => Evaluate(0f);
        public Vector3 ExitPosition  => Evaluate(1f);

        // Consistent marker colours across every tile kind — same meaning,
        // same colour, everywhere. Dark blue = an "onset" scored point
        // (red light stop, catch-up point, corner start). Light blue = the
        // one "end" point a scored range has (corner end).
        private static readonly Color OnsetColor = new Color(0.06f, 0.16f, 0.55f);
        private static readonly Color EndColor   = new Color(0.55f, 0.75f, 1.00f);

        private GameObject _entryMarker, _exitMarker;
        private GameObject _redLightMarker;
        private GameObject _cornerStartMarker, _cornerEndMarker;
        private GameObject _catchUpMarker;

        // Everything lives in Update() now — reacts live whether you change a
        // slider or flip the debug checkbox, at any point during Play.
        private void Update()
        {
            if (showDebugMarkersAtRuntime && _entryMarker == null)
                CreateAllMarkers();

            if (_entryMarker == null) return; // debug never turned on this run

            bool show = showDebugMarkersAtRuntime;
            _entryMarker.SetActive(show);
            _exitMarker.SetActive(show);

            bool showRedLight = show && kind == TileKind.RedLight;
            bool showCorner   = show && kind == TileKind.Corner;
            bool showCatchUp  = show && kind == TileKind.CatchUp;

            _redLightMarker.SetActive(showRedLight);
            _cornerStartMarker.SetActive(showCorner);
            _cornerEndMarker.SetActive(showCorner);
            _catchUpMarker.SetActive(showCatchUp);

            if (!show) return;

            _entryMarker.transform.position = EntryPosition;
            _exitMarker.transform.position  = ExitPosition;
            if (showRedLight) _redLightMarker.transform.position = Evaluate(stopPointT);
            if (showCorner)
            {
                _cornerStartMarker.transform.position = Evaluate(cornerStartT);
                _cornerEndMarker.transform.position   = Evaluate(cornerEndT);
            }
            if (showCatchUp) _catchUpMarker.transform.position = Evaluate(catchUpPointT);

            if (basePlaneRenderer != null)
                basePlaneRenderer.material.color = debugColor;
        }

        private void CreateAllMarkers()
        {
            _entryMarker       = CreateMarker(Color.green,  "Entry",         0.6f, PrimitiveType.Cube);
            _exitMarker        = CreateMarker(Color.red,    "Exit",          0.6f, PrimitiveType.Cube);
            _redLightMarker    = CreateMarker(OnsetColor,   "StopPoint",     0.8f, PrimitiveType.Sphere);
            _cornerStartMarker = CreateMarker(OnsetColor,   "CornerStart",   0.7f, PrimitiveType.Sphere);
            _cornerEndMarker   = CreateMarker(EndColor,     "CornerEnd",     0.7f, PrimitiveType.Sphere);
            _catchUpMarker     = CreateMarker(OnsetColor,   "CatchUpPoint",  0.8f, PrimitiveType.Sphere);
        }

        private GameObject CreateMarker(Color color, string label, float size, PrimitiveType shape)
        {
            var marker = GameObject.CreatePrimitive(shape);
            marker.name = $"[debug] {label}";
            marker.transform.SetParent(transform, true);
            marker.transform.localScale = Vector3.one * size;
            Destroy(marker.GetComponent<Collider>());
            marker.GetComponent<Renderer>().material.color = color;
            return marker;
        }
    }

#if UNITY_EDITOR
    [CustomEditor(typeof(RouteTile))]
    public class RouteTileEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.PropertyField(serializedObject.FindProperty("kind"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("tileName"));

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Road shape", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("splineContainer"));

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Debug", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("showDebugMarkersAtRuntime"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("basePlaneRenderer"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("debugColor"));

            var kindProp = serializedObject.FindProperty("kind");
            var kind = (TileKind)kindProp.enumValueIndex;

            EditorGUILayout.Space();
            switch (kind)
            {
                case TileKind.RedLight:
                    EditorGUILayout.LabelField("Red light", EditorStyles.boldLabel);
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("stopPointT"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("waitDuration"));
                    break;

                case TileKind.Corner:
                    EditorGUILayout.LabelField("Corner", EditorStyles.boldLabel);
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("cornerStartT"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("cornerEndT"));
                    break;

                case TileKind.CatchUp:
                    EditorGUILayout.LabelField("Catch-up (draft)", EditorStyles.boldLabel);
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("leadCarSpeed"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("catchUpPointT"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("holdDuration"));
                    break;

                case TileKind.Filler:
                    EditorGUILayout.HelpBox("Filler tiles have no event data — just road.", MessageType.None);
                    break;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Decoration", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("decorationSlots"), true);

            serializedObject.ApplyModifiedProperties();
        }
    }
#endif
}