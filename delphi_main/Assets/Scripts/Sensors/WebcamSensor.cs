using System;
using UnityEngine;

namespace Delphi
{
    /// <summary>
    /// FrameSensor wrapping a physical webcam through WebCamTexture —
    /// typically the participant-facing camera. Plug into DelphiManager's
    /// Webcam slot.
    ///
    /// Note: WebCamTexture reports a placeholder 16×16 size until the first
    /// real frame arrives, so consumers that care about dimensions (the
    /// recorder) should ignore this feed until width is sensible. If it
    /// never leaves that placeholder state, the most common cause on macOS
    /// is the OS camera permission not being granted to the Editor/app — see
    /// the warning this logs a few seconds after Play.
    /// </summary>
    public class WebcamSensor : FrameSensor
    {
        [Tooltip("Exact device name, or empty for the first available camera.")]
        public string deviceName = "";

        [Header("Requested format (the driver may pick the closest match)")]
        public int requestedWidth  = 1280;
        public int requestedHeight = 720;
        public int requestedFps    = 30;

        private WebCamTexture _tex;
        private bool _warnedNoDevice;
        private bool _warnedStuck;
        private float _timeSinceStart;

        public override Texture CurrentFrame =>
            _tex != null && _tex.isPlaying ? _tex : null;

        private void OnEnable()
        {
            _warnedStuck = false;
            _timeSinceStart = 0f;

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
            }
            return CurrentFrame;
        }
    }
}
