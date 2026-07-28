using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.XR;
using Varjo.XR;
// The plugin's session accessor lives on a CLASS called Varjo, inside a
// NAMESPACE also called Varjo. Written bare, `Varjo.GetVarjoSession()`
// resolves the namespace first and fails to compile. The alias picks the
// class explicitly.
using VarjoApi = Varjo.XR.Varjo;

namespace Delphi
{
    /// <summary>
    /// FrameSensor delivering the Varjo XR-3's INFRARED EYE CAMERAS — the
    /// actual images the eye tracker sees — as one side-by-side texture
    /// (left eye left, right eye right). Plug into DelphiManager's Eye
    /// Cameras slot.
    ///
    /// WHY THIS TALKS TO VarjoLib DIRECTLY INSTEAD OF THE UNITY PLUGIN:
    ///
    ///   The Unity XR plugin wraps only three of Varjo's data streams —
    ///   CameraMetadata, DistortedColor and EnvironmentCubemap — and its
    ///   MRStartDataStream entry point takes that same three-value enum, so
    ///   there is no way to ask it for the eye cameras. The NATIVE data
    ///   stream API does expose them (varjo_StreamType_EyeCamera = 3, Y8
    ///   greyscale, 640x480, 200 Hz, both eyes delivered in one callback).
    ///
    ///   So this binds the raw varjo_* datastream functions out of VarjoLib.
    ///   It does NOT open its own session: Varjo.GetVarjoSession() hands back
    ///   the very session the XR plugin already runs, so the eye tracker,
    ///   VarjoGazeConnection and this sensor all share one connection to the
    ///   headset rather than fighting over it.
    ///
    /// THE 200 Hz PROBLEM, AND HOW THE CALLBACK AVOIDS IT:
    ///
    ///   Two 640x480 frames at 200 Hz is roughly 123 MB/s. Copying all of
    ///   that would cost more than everything else DELPHI records put
    ///   together, to produce frames nobody asked for — DelphiManager paces
    ///   this feed at a few fps like every other one.
    ///
    ///   So the native callback does nothing at all unless the main thread
    ///   has already consumed the previous frame. Work happens at the
    ///   CONSUMPTION rate, not the delivery rate, and the dropped frames cost
    ///   one flag check each. What work there is stays off the main thread:
    ///   the callback memcpys under Varjo's buffer lock, releases it, and
    ///   only then expands Y8 to RGB24, so the lock is held for as little
    ///   time as possible and Unity's frame is left alone.
    ///
    /// NOTHING HERE MAY TOUCH A UNITY API. The callback arrives on one of
    /// Varjo's own threads. It works with plain byte arrays only; the sole
    /// Unity call (uploading to the texture) happens in ReadFrame on the main
    /// thread, exactly like every other FrameSensor.
    /// </summary>
    public class VarjoEyeCameraSensor : FrameSensor
    {
        [Tooltip("Draw a divider between the two eyes in the composed image. " +
                 "Purely cosmetic — it makes the recording readable at a " +
                 "glance when the two eyes look similar.")]
        public bool drawEyeDivider = true;

        [Tooltip("Log the stream's actual format once when it starts. Worth " +
                 "leaving on: it is the only confirmation that the eye " +
                 "cameras — rather than some other stream — are what got " +
                 "opened.")]
        public bool logStreamConfig = true;

        // ── Native binding ───────────────────────────────────────────────
        // Signatures cross-checked against the ones the Varjo Unity plugin
        // itself declares for the buffer calls (VarjoMixedReality.cs), so the
        // marshalling below is not guesswork for those. The stream
        // enumeration calls follow the same session-first convention.

        private const long StreamTypeEyeCamera = 3;   // varjo_StreamType_EyeCamera
        private const long BufferTypeCPU = 1;         // varjo_BufferType_CPU
        private const long TextureFormatA8 = 4;       // A8_UNORM — Varjo's Y8 greyscale

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void FrameListener(IntPtr frame, IntPtr session, IntPtr userData);

        [DllImport("VarjoLib")] private static extern int varjo_GetDataStreamConfigCount(IntPtr session);
        [DllImport("VarjoLib")] private static extern void varjo_GetDataStreamConfigs(IntPtr session, [In, Out] VarjoStreamConfig[] configs, int maxSize);
        [DllImport("VarjoLib")] private static extern void varjo_StartDataStream(IntPtr session, long streamId, ulong channels, FrameListener listener, IntPtr userData);
        [DllImport("VarjoLib")] private static extern void varjo_StopDataStream(IntPtr session, long streamId);
        [DllImport("VarjoLib")] private static extern long varjo_GetBufferId(IntPtr session, long streamId, long frameNumber, long channelIndex);
        [DllImport("VarjoLib")] private static extern void varjo_LockDataStreamBuffer(IntPtr session, long bufferId);
        [DllImport("VarjoLib")] private static extern void varjo_UnlockDataStreamBuffer(IntPtr session, long bufferId);
        [DllImport("VarjoLib")] private static extern EyeBufferMetadata varjo_GetBufferMetadata(IntPtr session, long bufferId);
        [DllImport("VarjoLib")] private static extern IntPtr varjo_GetBufferCPUData(IntPtr session, long bufferId);

        /// <summary>Mirrors the plugin's internal VarjoBufferMetadata field for
        /// field. Redeclared rather than reused only because the plugin marks
        /// its copy `internal`.</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct EyeBufferMetadata
        {
            public long textureFormat;
            public long bufferType;
            public int byteSize;
            public int rowStride;
            public int width;
            public int height;
        }

        /// <summary>A deliberately TRUNCATED varjo_StreamFrame — the leading
        /// fields only.
        ///
        /// The real struct ends in a union of per-stream metadata whose exact
        /// size would have to be replicated byte-perfectly to marshal, and
        /// getting that wrong reads past the end of Varjo's buffer. Since the
        /// union sits LAST and nothing here needs it, declaring only the
        /// prefix is both sufficient and strictly safer: Marshal reads exactly
        /// the bytes named below and never touches the rest.
        ///
        /// DO NOT ADD FIELDS. Anything appended starts reading into that union.</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct EyeStreamFramePrefix
        {
            public long type;
            public long id;
            public long frameNumber;
            public ulong channelFlags;
            public ulong dataFlags;
        }

        // ── State ────────────────────────────────────────────────────────

        private IntPtr _session = IntPtr.Zero;
        private long _streamId = -1;
        private bool _streaming;

        // Held in a field for the lifetime of the stream. A delegate passed
        // to native code is NOT rooted by the callee, so letting this be a
        // temporary means the GC collects the thunk Varjo is still calling
        // into — a crash that only shows up under memory pressure, minutes in.
        private FrameListener _listener;

        private readonly object _bufferLock = new object();
        private byte[] _rgb;              // composed side-by-side RGB24, shared
        private byte[] _y8;               // scratch for one eye's raw rows
        private volatile bool _pending;   // a composed frame is waiting for the main thread
        private int _eyeWidth, _eyeHeight;
        private volatile bool _formatLogged;
        private volatile int _framesDelivered;

        private Texture2D _tex;
        private bool _hasFrame;

        public override Texture CurrentFrame => _hasFrame ? _tex : null;

        /// <summary>Eye camera frames seen since start. Diagnostics — this
        /// counts DELIVERIES, so it climbs at ~200 Hz even though only a few
        /// are ever composed.</summary>
        public int FramesDelivered => _framesDelivered;

        // ── Lifecycle ────────────────────────────────────────────────────

        private void OnEnable()
        {
            if (!XRSettings.isDeviceActive)
            {
                Debug.Log("[VarjoEyeCameraSensor] No XR device running — no eye camera feed. " +
                          "This is the normal desktop path.", this);
                return;
            }

            try { StartStream(); }
            catch (DllNotFoundException)
            {
                Debug.LogWarning("[VarjoEyeCameraSensor] VarjoLib not found — the Varjo runtime does not " +
                                 "appear to be installed. No eye camera feed.", this);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[VarjoEyeCameraSensor] Could not start the eye camera stream: {e.Message}", this);
            }
        }

        private void OnDisable()
        {
            StopStream();
            if (_tex != null) { Destroy(_tex); _tex = null; }
            _hasFrame = false;
        }

        private void StartStream()
        {
            _session = VarjoApi.GetVarjoSession();
            if (_session == IntPtr.Zero)
            {
                Debug.LogWarning("[VarjoEyeCameraSensor] Varjo session handle is null — the XR plugin has " +
                                 "not initialised. No eye camera feed.", this);
                return;
            }

            int count = varjo_GetDataStreamConfigCount(_session);
            if (count <= 0)
            {
                Debug.LogWarning("[VarjoEyeCameraSensor] Varjo reports no data stream configurations.", this);
                return;
            }

            var configs = new VarjoStreamConfig[count];
            varjo_GetDataStreamConfigs(_session, configs, count);

            bool found = false;
            VarjoStreamConfig cfg = default;
            foreach (var c in configs)
            {
                if ((long)c.streamType != StreamTypeEyeCamera) continue;
                if ((long)c.bufferType != BufferTypeCPU) continue;
                cfg = c; found = true; break;
            }

            if (!found)
            {
                Debug.LogWarning($"[VarjoEyeCameraSensor] No CPU eye-camera stream among {count} configuration(s). " +
                                 "Eye camera streaming needs a Varjo runtime that supports it, and eye tracking " +
                                 "must be permitted in Varjo Base.", this);
                return;
            }

            if (cfg.width <= 0 || cfg.height <= 0)
            {
                Debug.LogWarning($"[VarjoEyeCameraSensor] Eye camera stream reports an implausible size " +
                                 $"({cfg.width}x{cfg.height}) — not starting it.", this);
                return;
            }

            _eyeWidth = cfg.width;
            _eyeHeight = cfg.height;
            _streamId = cfg.streamId;

            // Side by side: left eye then right eye.
            _rgb = new byte[_eyeWidth * 2 * _eyeHeight * 3];
            _y8 = new byte[_eyeWidth * _eyeHeight];

            if (logStreamConfig)
            {
                Debug.Log($"[VarjoEyeCameraSensor] Eye camera stream {_streamId}: {cfg.width}x{cfg.height} " +
                          $"@ {cfg.frameRate} Hz, format {cfg.format}, channels {cfg.channelFlags}. " +
                          $"Composed side-by-side into {_eyeWidth * 2}x{_eyeHeight}.", this);
                if ((long)cfg.format != TextureFormatA8)
                {
                    Debug.LogWarning($"[VarjoEyeCameraSensor] Expected A8_UNORM (Y8 greyscale) but got {cfg.format}. " +
                                     "The image will be wrong — this needs the unpack below updating.", this);
                }
            }

            _listener = OnFrame; // rooted for the stream's lifetime — see field comment
            varjo_StartDataStream(_session, _streamId, (ulong)VarjoChannelFlags.All, _listener, IntPtr.Zero);
            _streaming = true;
        }

        private void StopStream()
        {
            if (!_streaming) return;
            _streaming = false;
            try { varjo_StopDataStream(_session, _streamId); }
            catch (Exception e) { Debug.LogWarning($"[VarjoEyeCameraSensor] Stopping the stream failed: {e.Message}", this); }
            _listener = null;
            _pending = false;
        }

        // ── Native callback (NOT the Unity thread) ───────────────────────

        /// <summary>Called by Varjo at up to 200 Hz on one of its own threads.
        /// Touches no Unity API and takes no Unity lock.</summary>
        private void OnFrame(IntPtr framePtr, IntPtr session, IntPtr userData)
        {
            _framesDelivered++;

            // Drop unless the main thread has taken the last one. This is the
            // whole throttle: composing every delivered frame would burn
            // ~123 MB/s to produce images DelphiManager would just discard.
            if (_pending || !_streaming || framePtr == IntPtr.Zero) return;

            try
            {
                var frame = Marshal.PtrToStructure<EyeStreamFramePrefix>(framePtr);

                lock (_bufferLock)
                {
                    CopyEye(session, frame.frameNumber, 0, 0);            // left  → left half
                    CopyEye(session, frame.frameNumber, 1, _eyeWidth);    // right → right half
                    if (drawEyeDivider) DrawDivider();
                }
                _pending = true;
            }
            catch
            {
                // A throw crossing back into native code would take the
                // process down, so this boundary must swallow. Losing a frame
                // is survivable; the stream keeps running.
            }
        }

        /// <summary>Pulls one eye's Y8 buffer and writes it into the composed
        /// RGB24 image at the given column offset.</summary>
        private void CopyEye(IntPtr session, long frameNumber, long channelIndex, int xOffset)
        {
            long bufferId = varjo_GetBufferId(session, _streamId, frameNumber, channelIndex);
            if (bufferId == 0) return;

            varjo_LockDataStreamBuffer(session, bufferId);
            try
            {
                var meta = varjo_GetBufferMetadata(session, bufferId);
                IntPtr data = varjo_GetBufferCPUData(session, bufferId);
                if (data == IntPtr.Zero || meta.width <= 0 || meta.height <= 0) return;

                int w = Mathf.Min(meta.width, _eyeWidth);
                int h = Mathf.Min(meta.height, _eyeHeight);

                // Copy row by row rather than in one block: rowStride is the
                // buffer's real pitch and is NOT required to equal width, so a
                // single memcpy would shear the image on any padded buffer.
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy(data + y * meta.rowStride, _y8, y * _eyeWidth, w);
                }
            }
            finally
            {
                varjo_UnlockDataStreamBuffer(session, bufferId);
            }

            // Expansion happens AFTER the unlock above — Varjo's buffer is a
            // shared resource and the tracker itself is waiting on it at
            // 200 Hz, so it is held only for the memcpy.
            // ROWS ARE WRITTEN BOTTOM-UP ON PURPOSE.
            //
            // Varjo hands over the image top-down, the way every camera and
            // image format does. Texture2D.LoadRawTextureData reads the
            // OPPOSITE way — its first row is the BOTTOM of the texture,
            // because Unity's texture origin is bottom-left. Copying rows in
            // order therefore produces a vertically mirrored eye, which looks
            // like a plausible image and is easy to stare straight past.
            // Flipping here costs nothing; the loop had to run anyway.
            int stride = _eyeWidth * 2 * 3;
            for (int y = 0; y < _eyeHeight; y++)
            {
                int src = y * _eyeWidth;
                int dst = (_eyeHeight - 1 - y) * stride + xOffset * 3;
                for (int x = 0; x < _eyeWidth; x++)
                {
                    byte g = _y8[src + x];
                    _rgb[dst] = g; _rgb[dst + 1] = g; _rgb[dst + 2] = g;
                    dst += 3;
                }
            }
        }

        private void DrawDivider()
        {
            int stride = _eyeWidth * 2 * 3;
            int x = _eyeWidth * 3;
            for (int y = 0; y < _eyeHeight; y++)
            {
                int i = y * stride + x;
                _rgb[i] = 255; _rgb[i + 1] = 60; _rgb[i + 2] = 60;
            }
        }

        // ── Main thread ──────────────────────────────────────────────────

        public override Texture ReadFrame()
        {
            if (!_streaming || _rgb == null) return CurrentFrame;
            if (!_pending) return CurrentFrame; // nothing new — consumers keep the held frame

            if (_tex == null)
            {
                _tex = new Texture2D(_eyeWidth * 2, _eyeHeight, TextureFormat.RGB24, false)
                {
                    name = "Varjo Eye Cameras",
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp
                };
            }

            lock (_bufferLock)
            {
                _tex.LoadRawTextureData(_rgb);
            }
            _tex.Apply(false, false);

            // Cleared AFTER the upload, so the callback cannot be composing
            // into _rgb while LoadRawTextureData is reading it.
            _pending = false;
            _hasFrame = true;
            return _tex;
        }
    }
}
