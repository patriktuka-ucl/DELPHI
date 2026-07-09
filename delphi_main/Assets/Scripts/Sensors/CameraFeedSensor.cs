using UnityEngine;

namespace Delphi
{
    /// <summary>
    /// FrameSensor that mirrors any scene Camera into a RenderTexture —
    /// used for the scene-overview and player-view dashboard feeds.
    ///
    /// The source camera is never touched: a hidden clone camera (CopyFrom
    /// each capture) renders into the RT, so the participant's on-screen
    /// view keeps its own display/target untouched.
    ///
    /// NO rate field, NO internal throttle: this captures whenever
    /// ReadFrame() is called and DelphiManager — the single owner of every
    /// rate in DELPHI — decides when that is (see its per-feed FPS fields).
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

        private RenderTexture _rt;
        private Camera _renderCam;
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

            _renderCam.CopyFrom(sourceCamera);        // lens, culling, clear flags…
            _renderCam.targetDisplay = 7;             // keep it off real displays
            _renderCam.targetTexture = _rt;           // …but CopyFrom cloned the target, so re-point
            _renderCam.enabled = false;
            _renderCam.transform.SetPositionAndRotation(
                sourceCamera.transform.position, sourceCamera.transform.rotation);
            _renderCam.Render();
            _hasFrame = true;

            return CurrentFrame;
        }
    }
}
