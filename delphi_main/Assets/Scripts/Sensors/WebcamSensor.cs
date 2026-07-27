using System;
using UnityEngine;

namespace Delphi
{
    /// <summary>
    /// FrameSensor wrapping a physical webcam through WebCamTexture —
    /// typically the participant-facing camera. Plug into DelphiManager's
    /// Webcam slot.
    ///
    /// NO rate field, NO internal throttle: a frame is copied out of the
    /// hardware texture into a RenderTexture whenever ReadFrame() is called,
    /// and DelphiManager — the single owner of every rate in DELPHI —
    /// decides when that is (see its per-feed FPS fields). The driver
    /// delivers at whatever rate it likes into WebCamTexture; frames between
    /// the manager's ticks are simply never copied out, so its rate never
    /// leaks through to the dashboard or recordings.
    ///
    /// CurrentFrame is null until the first real frame lands, so the 16×16
    /// placeholder WebCamTexture reports before then never reaches
    /// consumers. If the feed never leaves that placeholder state, the most
    /// common cause on macOS is the OS camera permission not being granted
    /// to the Editor/app — see the warning this logs a few seconds after
    /// Play.
    /// </summary>
    public class WebcamSensor : FrameSensor
    {
        [Tooltip("Exact device name, or empty for the first available camera.")]
        public string deviceName = "";

        [Header("Requested format (the driver may pick the closest match)")]
        public int requestedWidth  = 1280;
        public int requestedHeight = 720;
        [Tooltip("Without this, Unity's WebCamTexture doesn't ask the driver " +
                 "for a target rate and can silently fall back to a much " +
                 "lower default capture mode even when the device (and its " +
                 "own OS app) is capable of far more.")]
        public int requestedFps    = 60;

        private WebCamTexture _tex;
        private RenderTexture _rt;    // manager-paced copy — what consumers actually see
        private bool _hasFrame;
        private bool _warnedNoDevice;
        private bool _warnedStuck;
        private float _timeSinceStart;

        // Delivery diagnostics: how fast the OS driver is ACTUALLY handing
        // us frames — measured, not requested. If the feed looks laggy and
        // this reads e.g. 8 fps, the bottleneck is the camera/driver side
        // (low light auto-exposure, USB bandwidth, Continuity Camera…), not
        // DELPHI's pipeline.
        public float MeasuredDriverFps { get; private set; }
        private uint _lastUpdateCount;   // Texture.updateCount at last blit
        private int _deliveredInWindow;
        private float _windowStart;
        private bool _loggedRate;

        public override Texture CurrentFrame => _hasFrame ? _rt : null;

        // Counts real driver deliveries once per rendered frame — pure
        // diagnostics, no rate control (all rates live on DelphiManager).
        private void Update()
        {
            if (_tex == null || !_tex.isPlaying || _tex.width <= 32) return;

            if (_tex.didUpdateThisFrame) _deliveredInWindow++;

            float elapsed = Time.unscaledTime - _windowStart;
            if (elapsed < 5f) return;
            MeasuredDriverFps = _deliveredInWindow / elapsed;
            _deliveredInWindow = 0;
            _windowStart = Time.unscaledTime;

            if (MeasuredDriverFps < 20f)
                Debug.LogWarning($"[WebcamSensor] Driver is only delivering " +
                                 $"{MeasuredDriverFps:0.#} fps from '{_tex.deviceName}' — the lag is " +
                                 "upstream of DELPHI. Usual causes: low light (auto-exposure " +
                                 "lengthens shutter time and halves the rate — add light or lock " +
                                 "exposure in the camera's own settings), USB bandwidth at high " +
                                 "resolution (try 640x480), or iPhone Continuity Camera latency.", this);
            else if (!_loggedRate)
            {
                _loggedRate = true;
                Debug.Log($"[WebcamSensor] Driver delivering {MeasuredDriverFps:0.#} fps " +
                          $"at {_tex.width}x{_tex.height}.", this);
            }
        }

        private void OnEnable()
        {
            _warnedStuck = false;
            _timeSinceStart = 0f;
            _hasFrame = false;
            _deliveredInWindow = 0;
            _windowStart = Time.unscaledTime;
            _lastUpdateCount = 0;
            _loggedRate = false;

            var devices = WebCamTexture.devices;
            if (devices.Length == 0)
            {
                if (!_warnedNoDevice)
                {
                    Debug.LogWarning("[WebcamSensor] No webcam devices found. On macOS this " +
                                     "usually means the OS hasn't granted camera access to this " +
                                     "app yet — check System Settings > Privacy & Security > " +
                                     "Camera.", this);
                    _warnedNoDevice = true;
                }
                return;
            }

            string device = deviceName;
            if (!string.IsNullOrEmpty(device))
            {
                bool found = false;
                foreach (var d in devices) if (d.name == device) { found = true; break; }
                if (!found)
                {
                    Debug.LogWarning($"[WebcamSensor] Configured device '{device}' not found among " +
                                      $"{devices.Length} available device(s) — falling back to " +
                                      $"'{devices[0].name}'. Available: " +
                                      string.Join(", ", Array.ConvertAll(devices, d => d.name)), this);
                    device = devices[0].name;
                }
            }
            else
            {
                device = devices[0].name;
            }

            Debug.Log($"[WebcamSensor] Found {devices.Length} device(s), using '{device}'.", this);
            _tex = new WebCamTexture(device, requestedWidth, requestedHeight, requestedFps);
            _tex.Play();
        }

        private void OnDisable()
        {
            if (_tex != null)
            {
                _tex.Stop();
                Destroy(_tex);
                _tex = null;
            }
            if (_rt != null) { _rt.Release(); Destroy(_rt); _rt = null; }
            _hasFrame = false;
        }

        public override Texture ReadFrame()
        {
            // Diagnose the classic "shows nothing" failure: the texture
            // never leaves its 16x16 placeholder because the OS is blocking
            // camera access (or the request format was never granted, or
            // another app is holding the device open).
            if (_tex != null && _tex.width <= 32)
            {
                _timeSinceStart += Time.deltaTime;
                if (_timeSinceStart > 5f && !_warnedStuck)
                {
                    _warnedStuck = true;
                    Debug.LogWarning($"[WebcamSensor] '{_tex.deviceName}' has been playing for " +
                                      $"{_timeSinceStart:F0}s but is still {_tex.width}x{_tex.height} " +
                                      "(placeholder size) — no real frames are arriving. On macOS: " +
                                      "grant camera access in System Settings > Privacy & Security > " +
                                      "Camera to the app running this (Unity Editor, or your build), " +
                                      "then press Play again. Also check no other app is using the " +
                                      "camera.", this);
                }
                return null; // still placeholder — nothing real to show yet
            }

            if (_tex == null || !_tex.isPlaying) return CurrentFrame;

            // Copy the driver's texture into our RT. No throttle here — the
            // manager paces these calls; between its ticks consumers keep
            // seeing the held frame. If the driver hasn't produced anything
            // new since the last copy (updateCount unchanged), skip the blit
            // entirely — re-copying identical pixels is pure waste.
            if (_tex.updateCount == _lastUpdateCount && _hasFrame)
                return CurrentFrame;
            _lastUpdateCount = _tex.updateCount;

            // RT is created lazily here because the webcam's true size is
            // only known once real frames arrive (16x16 until then).
            if (_rt == null || _rt.width != _tex.width || _rt.height != _tex.height)
            {
                if (_rt != null) { _rt.Release(); Destroy(_rt); }
                _rt = new RenderTexture(_tex.width, _tex.height, 0,
                                        RenderTextureFormat.ARGB32);
                _rt.Create();
            }

            Graphics.Blit(_tex, _rt);
            _hasFrame = true;

            return CurrentFrame;
        }
    }
}
