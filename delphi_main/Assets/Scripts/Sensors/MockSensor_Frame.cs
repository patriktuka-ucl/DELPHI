using UnityEngine;
using UnityEngine.Video;

namespace Delphi
{
    /// <summary>
    /// Dead-simple mock frame sensor: loops a test video clip so you can
    /// exercise the video-feed side of the pipeline without a real camera.
    ///
    /// Setup: import your test_pattern.mp4 into the project (drag it into
    /// Assets — Unity turns it into a VideoClip automatically), then drag
    /// that VideoClip into the 'clip' field below.
    /// </summary>
    public class MockSensor_Frame : FrameSensor
    {
        [Header("Test clip")]
        public VideoClip clip;

        [Header("Fallback size (only used if no clip is assigned yet)")]
        public int fallbackWidth  = 640;
        public int fallbackHeight = 360;

        public override Texture CurrentFrame => _renderTexture;

        private VideoPlayer   _player;
        private RenderTexture _renderTexture;

        private void Awake()
        {
            // Use the clip's own resolution so the texture carries the true
            // native aspect ratio — the dashboard reads this back later to
            // size its preview box correctly instead of stretching it.
            int w = clip != null ? (int)clip.width  : fallbackWidth;
            int h = clip != null ? (int)clip.height : fallbackHeight;
            if (w <= 0) w = fallbackWidth;
            if (h <= 0) h = fallbackHeight;

            _renderTexture = new RenderTexture(w, h, 0);
            _renderTexture.Create();

            _player = gameObject.AddComponent<VideoPlayer>();
            _player.playOnAwake   = false;
            _player.isLooping     = true;
            _player.renderMode    = VideoRenderMode.RenderTexture;
            _player.targetTexture = _renderTexture;
            _player.source        = VideoSource.VideoClip;
            _player.clip          = clip;
        }

        private void OnEnable()
        {
            if (clip == null)
            {
                Debug.LogWarning($"[MockSensor_Frame] '{name}' has no video clip assigned.");
                return;
            }
            _player.Play();
        }

        private void OnDisable()
        {
            if (_player != null) _player.Stop();
        }

        public override Texture ReadFrame()
        {
            // The VideoPlayer writes into _renderTexture continuously on its
            // own — nothing to poll here, just hand back the live texture.
            return _renderTexture;
        }
    }
}