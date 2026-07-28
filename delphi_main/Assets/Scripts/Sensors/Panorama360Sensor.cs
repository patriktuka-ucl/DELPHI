using UnityEngine;

namespace Delphi
{
    /// <summary>
    /// A 360° equirectangular feed from the car — the environment as it
    /// surrounds the participant, for post-hoc review of what was actually
    /// around them at any moment of a trial.
    ///
    /// Renders a cubemap from one point and unwraps it to a 2:1 equirect
    /// texture, which is what every 360 player expects and what encodes
    /// cleanly to mp4 through the normal recording path.
    ///
    /// EXCLUSION is the point of this sensor: the ego vehicle, the
    /// participant's own body and every UI surface are culled, so the feed
    /// contains only the world. Anything drawn at or inside the capture point
    /// would otherwise smear across the whole panorama — a car interior at
    /// the origin fills most of the sphere — and researcher instrumentation
    /// (the overview discs) isn't part of the environment at all.
    /// </summary>
    public class Panorama360Sensor : FrameSensor
    {
        [Header("Capture point (defaults to this transform)")]
        [Tooltip("Where the panorama is captured FROM. Leave empty to use " +
                 "this GameObject — normally parented to the car at roughly " +
                 "the participant's head height.")]
        public Transform capturePoint;

        [Header("Feed format")]
        [Tooltip("Cubemap face size. Each frame renders SIX faces at this " +
                 "resolution, so this is the main cost — 512 is a reasonable " +
                 "balance, 1024 is noticeably heavier.")]
        public int cubemapSize = 512;
        [Tooltip("Equirect output width. Height is always half (2:1).")]
        public int outputWidth = 2048;

        [Header("What NOT to record")]
        [Tooltip("Layers excluded from the panorama. The ego vehicle, the " +
                 "participant and all UI belong here — see the class summary.")]
        public LayerMask excludedLayers;

        [Tooltip("Hierarchies hidden for the instant this feed renders — the " +
                 "car root belongs here. These are excluded by switching their " +
                 "RENDERERS off for the duration of the cubemap render, NOT by " +
                 "layer, so it works however the car is laid out.")]
        public Transform[] excludeHierarchies;

        private RenderTexture _cube;
        private RenderTexture _equirect;
        private Camera _renderCam;
        private bool _hasFrame;

        public override Texture CurrentFrame => _hasFrame ? _equirect : null;

        private void OnEnable()
        {
            int face = Mathf.Clamp(Mathf.ClosestPowerOfTwo(cubemapSize), 64, 2048);
            int w = Mathf.Clamp(Mathf.ClosestPowerOfTwo(outputWidth), 128, 4096);

            _cube = new RenderTexture(face, face, 24, RenderTextureFormat.ARGB32)
            {
                dimension = UnityEngine.Rendering.TextureDimension.Cube
            };
            _cube.Create();

            _equirect = new RenderTexture(w, w / 2, 0, RenderTextureFormat.ARGB32);
            _equirect.Create();

            var go = new GameObject($"[feed] {name} 360 cam");
            go.transform.SetParent(transform, false);
            go.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave;
            _renderCam = go.AddComponent<Camera>();
            _renderCam.enabled = false;      // rendered manually
            _renderCam.targetDisplay = 7;    // never a real display
            _renderCam.stereoTargetEye = StereoTargetEyeMask.None; // never an XR camera, even in VR
            _renderCam.nearClipPlane = 0.05f;
            _renderCam.farClipPlane = 2000f;

            _hasFrame = false;
        }

        /// <summary>Caches the renderers to hide while this feed renders.
        ///
        /// THIS USED TO FOLD THE HIERARCHIES' LAYERS INTO THE EXCLUSION MASK,
        /// AND THAT SILENTLY CULLED THE ENTIRE WORLD. The car sits on the
        /// Default layer — so do the track, the environment and essentially
        /// everything else — so "exclude every layer the car uses" resolved to
        /// "exclude Default", and the panorama rendered nothing but skybox.
        /// The feed still looked alive, which is what made it hard to spot.
        ///
        /// A camera can only cull by layer, so no mask can express "everything
        /// except the children of this transform" while the car shares a layer
        /// with the world. Toggling the renderers themselves can, exactly, and
        /// without moving anything onto a new layer — which would have risked
        /// the physics collision matrix and every layer-masked raycast in the
        /// driving code for a change that is only ever about what is DRAWN.</summary>
        private void Start()
        {
            RebuildExcludedRenderers();
        }

        /// <summary>Re-scans the excluded hierarchies. Call after spawning or
        /// swapping anything under them at run time.</summary>
        public void RebuildExcludedRenderers()
        {
            var found = new System.Collections.Generic.List<Renderer>();
            if (excludeHierarchies != null)
            {
                foreach (var root in excludeHierarchies)
                {
                    if (root == null) continue;
                    root.GetComponentsInChildren(true, _scratch);
                    found.AddRange(_scratch);
                }
            }
            _excluded = found.ToArray();
            _wasEnabled = new bool[_excluded.Length];
        }

        private readonly System.Collections.Generic.List<Renderer> _scratch = new();
        private Renderer[] _excluded = System.Array.Empty<Renderer>();
        private bool[] _wasEnabled = System.Array.Empty<bool>();

        private void OnDisable()
        {
            if (_renderCam != null) Destroy(_renderCam.gameObject);
            if (_cube != null) { _cube.Release(); Destroy(_cube); _cube = null; }
            if (_equirect != null) { _equirect.Release(); Destroy(_equirect); _equirect = null; }
            _hasFrame = false;
        }

        public override Texture ReadFrame()
        {
            if (_renderCam == null || _cube == null || _equirect == null) return null;

            var point = capturePoint != null ? capturePoint : transform;
            _renderCam.transform.SetPositionAndRotation(point.position, Quaternion.identity);
            _renderCam.cullingMask = ~excludedLayers.value;

            // Hide the ego vehicle for the duration of the render only.
            //
            // Safe to toggle here because RenderToCubemap is SYNCHRONOUS and
            // this runs from DelphiManager's Update: no other camera draws
            // between the disable and the restore, so nothing else — the
            // participant's own view least of all — ever sees the car vanish.
            for (int i = 0; i < _excluded.Length; i++)
            {
                var r = _excluded[i];
                if (r == null) continue;
                _wasEnabled[i] = r.enabled;
                r.enabled = false;
            }

            bool rendered;
            try
            {
                // Six faces in one call, then unwrap. Identity rotation on the
                // stereo param keeps it monoscopic — a stereo pair would double
                // the cost for no analytical gain here.
                rendered = _renderCam.RenderToCubemap(_cube, 63);
            }
            finally
            {
                // finally, because leaving the car invisible after a throw
                // would break the participant's view rather than just this feed.
                for (int i = 0; i < _excluded.Length; i++)
                {
                    var r = _excluded[i];
                    if (r != null) r.enabled = _wasEnabled[i];
                }
            }

            if (!rendered) return CurrentFrame;
            _cube.ConvertToEquirect(_equirect);
            _hasFrame = true;

            return CurrentFrame;
        }
    }
}
