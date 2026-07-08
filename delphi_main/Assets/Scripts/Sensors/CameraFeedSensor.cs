using UnityEngine;

namespace Delphi
{
    /// <summary>
    /// FrameSensor that mirrors any scene Camera into a RenderTexture —
    /// used for the scene-overview and player-view dashboard feeds.
    ///
    /// The source camera is never touched: a hidden clone camera (CopyFrom
    /// each capture) renders into the RT, so the participant's on-screen
    /// view keeps its own display/target untouched. Capture is throttled to
    /// captureFps because each capture is a full extra scene render.
    /// </summary>
    public class CameraFeedSensor : FrameSensor
    {
        [Tooltip("The camera to mirror. For a scene overview, add a " +
                 "dedicated overhead camera (it can be disabled — the clone " +
                 "does the rendering).")]
        public Camera sourceCamera;

        [Header("Feed format")]
        public int width  = 1280;
        public int height = 720;

        [Tooltip("Captures per second. Each capture is an extra scene " +
                 "render, so keep this at/below the recording fps.")]
        public float captureFps = 30f;

        private RenderTexture _rt;
        private Camera _renderCam;
        private float _nextCapture;
        private bool _hasFrame;

        public override Texture CurrentFrame => _hasFrame ? _rt : null;

        private void OnEnable()
        {
            _rt = new RenderTexture(Mathf.Max(64, width), Mathf.Max(64, height), 24,
                                    RenderTextureFormat.ARGB32);
            _rt.Create();

            var go = new GameObject($"[feed] {name} render cam");
            go.transform.SetParent(transform, false);
            go.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave;
            _renderCam = go.AddComponent<Camera>();
            _renderCam.enabled = false; // rendered manually, never to a display
            _hasFrame = false;
            _nextCapture = 0f;
        }

        private void OnDisable()
        {
            if (_renderCam != null) Destroy(_renderCam.gameObject);
            if (_rt != null) { _rt.Release(); Destroy(_rt); _rt = null; }
            _hasFrame = false;
        }

        public override Texture ReadFrame()
        {
            if (sourceCamera == null || _renderCam == null) return null;

            if (Time.unscaledTime >= _nextCapture)
            {
                _nextCapture = Time.unscaledTime + 1f / Mathf.Max(1f, captureFps);

                _renderCam.CopyFrom(sourceCamera);        // lens, culling, clear flags…
                _renderCam.targetDisplay = 7;             // keep it off real displays
                _renderCam.targetTexture = _rt;           // …but CopyFrom cloned the target, so re-point
                _renderCam.enabled = false;
                _renderCam.transform.SetPositionAndRotation(
                    sourceCamera.transform.position, sourceCamera.transform.rotation);
                _renderCam.Render();
                _hasFrame = true;
            }
            return CurrentFrame;
        }
    }
}
