using UnityEngine;

namespace Delphi
{
    /// <summary>
    /// FrameSensor that mirrors any scene Camera into a RenderTexture —
    /// used for the track-overview and player-view dashboard feeds.
    ///
    /// The source camera is never touched: a hidden clone camera (CopyFrom
    /// each capture) renders into the RT, so the participant's on-screen
    /// view keeps its own display/target untouched.
    ///
    /// WHEN THE SOURCE IS THE HEADSET CAMERA, the clone borrows one eye's
    /// view and projection matrices, so the recording is literally a frame
    /// the participant was shown rather than a mono approximation of it.
    /// See stereoEye.
    ///
    /// NO rate field, NO internal throttle: this captures whenever
    /// ReadFrame() is called and DelphiManager — the single owner of every
    /// rate in DELPHI — decides when that is (see its per-feed FPS fields).
    /// </summary>
    public class CameraFeedSensor : FrameSensor
    {
        /// <summary>Which eye a stereo source camera is recorded through.
        /// Ignored entirely when the source isn't rendering in stereo.</summary>
        public enum StereoEye
        {
            /// <summary>The left eye's actual view and projection.</summary>
            Left,
            /// <summary>The right eye's actual view and projection.</summary>
            Right,
            /// <summary>One mono view from between the eyes, at the clone's own
            /// FOV. Narrower and cheaper than an eye view, and free of the
            /// headset's very wide field — but not what anybody saw.</summary>
            MonoFromHead
        }

        [Tooltip("The camera to mirror. For a track overview, add a " +
                 "dedicated overhead camera (it can be disabled — the clone " +
                 "does the rendering).")]
        public Camera sourceCamera;

        [Header("Feed format")]
        public int width  = 1280;
        public int height = 720;

        [Tooltip("Which eye to record when the source camera is the headset's. " +
                 "One eye is enough: the pair differs only by a few centimetres " +
                 "of parallax, which no reviewer is going to read off a flat mp4, " +
                 "and recording both would double the encode for it.")]
        public StereoEye stereoEye = StereoEye.Left;

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

            // CopyFrom brought the source's stereo settings across too. The
            // clone must never be an XR camera — it renders one flat frame
            // into an RT, whatever the source is doing.
            _renderCam.stereoTargetEye = StereoTargetEyeMask.None;
            _renderCam.ResetWorldToCameraMatrix();
            _renderCam.ResetProjectionMatrix();
            _renderCam.transform.SetPositionAndRotation(
                sourceCamera.transform.position, sourceCamera.transform.rotation);

            if (stereoEye != StereoEye.MonoFromHead && sourceCamera.stereoEnabled)
            {
                // Borrow the eye's real matrices rather than reconstructing
                // them from an eye offset and an FOV: on a Varjo the
                // projection is neither symmetric nor the same between eyes,
                // so anything reconstructed would be subtly the wrong frame.
                var eye = stereoEye == StereoEye.Right
                    ? Camera.StereoscopicEye.Right
                    : Camera.StereoscopicEye.Left;
                _renderCam.worldToCameraMatrix = sourceCamera.GetStereoViewMatrix(eye);
                _renderCam.projectionMatrix    = sourceCamera.GetStereoProjectionMatrix(eye);
                WarnOnceIfAspectMismatched(_renderCam.projectionMatrix);
            }

            _renderCam.Render();
            _hasFrame = true;

            return CurrentFrame;
        }

        private bool _warnedAspect;

        /// <summary>An eye's projection carries the headset's own aspect, which
        /// on a Varjo is close to square — nothing like the 16:9 this feed
        /// defaults to. Rendering one into the other doesn't crop, it
        /// STRETCHES, and a stretched recording is the kind of thing nobody
        /// notices until they're measuring something off it months later. So
        /// say so, once, with the numbers to fix it.</summary>
        private void WarnOnceIfAspectMismatched(Matrix4x4 projection)
        {
            if (_warnedAspect) return;
            if (Mathf.Approximately(projection.m00, 0f)) return;

            // For any projection, m11/m00 is height-over-width in clip space,
            // so its reciprocal is the aspect the frame was authored at.
            float eyeAspect = Mathf.Abs(projection.m11 / projection.m00);
            float feedAspect = (float)_rt.width / _rt.height;
            if (Mathf.Abs(eyeAspect - feedAspect) / eyeAspect < 0.05f) return;

            _warnedAspect = true;
            int suggestedHeight = Mathf.RoundToInt(_rt.width / eyeAspect / 2f) * 2; // even, for the encoder
            Debug.LogWarning(
                $"[CameraFeedSensor] '{name}' is recording the {stereoEye} eye (aspect {eyeAspect:0.###}) " +
                $"into a {_rt.width}x{_rt.height} feed (aspect {feedAspect:0.###}), so the recording is " +
                $"stretched. Set height to {suggestedHeight} to match, or switch stereoEye to " +
                "MonoFromHead if you'd rather keep 16:9 and a normal field of view.", this);
        }
    }
}
