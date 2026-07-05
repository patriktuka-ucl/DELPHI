using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace Delphi
{
    /// <summary>
    /// Owns the single UDP socket receiving OSC messages relayed by EmotiBit
    /// Oscilloscope (see oscOutputSettings.xml). Parses raw PPG (infrared) and
    /// raw EDA, and runs a basic peak-detection pass on PPG to derive HR (BPM)
    /// and HRV (RMSSD).
    ///
    /// IMPORTANT — read before trusting this for real data collection:
    /// - Oscilloscope does NOT preprocess these signals. PPG is lightly
    ///   hardware-conditioned but not bandpass-filtered; EDA has an onboard
    ///   firmware calibration transform applied, but is still a combined
    ///   tonic+phasic signal, not phasic-only.
    /// - The HR/HRV extraction below is a basic threshold-crossing peak
    ///   detector with light detrending — NOT a validated algorithm. Motion
    ///   artifacts, poor sensor contact, or low signal amplitude will degrade
    ///   it. Cross-validate against a known-good reference (e.g. your Polar
    ///   H10) before trusting it for real sessions.
    /// - Phasic/tonic EDA decomposition (subtracting local tonic baseline,
    ///   per your DELPHI event-scoring design) is NOT done here — this script
    ///   only exposes the raw combined EDA signal. That decomposition should
    ///   happen in your analysis pipeline as already planned.
    ///
    /// Only ONE of these should exist in the scene (singleton). Multiple
    /// EmotibitChannelReader components read from it via the Instance,
    /// avoiding multiple sockets/connections for HR/HRV/GSR.
    /// </summary>
    public class EmotibitOscConnection : MonoBehaviour
    {
        public static EmotibitOscConnection Instance { get; private set; }

        [Header("OSC Listener Settings")]
        [SerializeField] private int listenPort = 12345;
        [SerializeField] private string ppgAddress = "/EmotiBit/0/PPG:IR";
        [SerializeField] private string edaAddress = "/EmotiBit/0/EDA";
        [SerializeField] private bool logParseErrors = false;

        [Header("HR/HRV Extraction Settings (basic — validate before trusting)")]
        [Tooltip("Stock EmotiBit PPG sampling rate")]
        [SerializeField] private float ppgSampleRateHz = 25f;
        [SerializeField] private float minPlausibleBpm = 40f;
        [SerializeField] private float maxPlausibleBpm = 180f;
        [Tooltip("Number of most recent inter-beat intervals used for HR/RMSSD")]
        [SerializeField] private int ibiHistorySize = 12;
        [Tooltip("Moving-average window (samples) used to detrend PPG before peak detection")]
        [SerializeField] private int detrendWindowSamples = 5;
        [Tooltip("Peak threshold as std-devs above local mean")]
        [SerializeField] private float peakThresholdStdDevs = 0.5f;
        [Tooltip("Window (seconds) used to estimate local mean/std for adaptive thresholding")]
        [SerializeField] private float statsWindowSeconds = 2f;

        private UdpClient _udpClient;
        private Thread _listenThread;
        private volatile bool _running;

        private readonly object _lock = new object();

        // Raw latest values
        private float _latestPpg;
        private float _latestEda;
        private bool _hasPpg;
        private bool _hasEda;

        // Derived HR/HRV state
        private float _latestHrBpm;
        private float _latestHrvRmssdMs;
        private bool _hasHr;
        private bool _hasHrv;

        // --- Peak detection working state (touched only from the listener thread) ---
        private readonly Queue<float> _detrendWindow = new Queue<float>();
        private float _detrendSum = 0f;
        private readonly Queue<float> _statsWindow = new Queue<float>();
        private int _statsWindowCapacity;
        private double _lastPeakTimeSec = -1;
        private readonly List<double> _ibiHistoryMs = new List<double>();
        private double _sampleClockSec = 0; // synthetic clock advanced one sample-period per PPG sample

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("[EmotibitOscConnection] Duplicate instance found — only one should exist. Destroying duplicate.");
                Destroy(this);
                return;
            }
            Instance = this;

            _statsWindowCapacity = Mathf.Max(4, Mathf.RoundToInt(statsWindowSeconds * ppgSampleRateHz));

            try
            {
                _udpClient = new UdpClient(listenPort);
            }
            catch (Exception e)
            {
                Debug.LogError($"[EmotibitOscConnection] Failed to bind UDP port {listenPort}: {e.Message}");
                enabled = false;
                return;
            }

            _running = true;
            _listenThread = new Thread(ListenLoop) { IsBackground = true };
            _listenThread.Start();
        }

        private void ListenLoop()
        {
            IPEndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
            while (_running)
            {
                byte[] data;
                try
                {
                    data = _udpClient.Receive(ref remoteEP);
                }
                catch (Exception)
                {
                    break; // socket closed during shutdown, or transient error
                }

                try
                {
                    ParseOscPacket(data, 0, data.Length);
                }
                catch (Exception e)
                {
                    if (logParseErrors)
                        Debug.LogWarning($"[EmotibitOscConnection] Failed to parse OSC packet: {e.Message}");
                }
            }
        }

        // Handles a single OSC message or a #bundle wrapping multiple messages.
        private void ParseOscPacket(byte[] buf, int offset, int end)
        {
            if (end - offset >= 8 && Encoding.ASCII.GetString(buf, offset, 7) == "#bundle")
            {
                int pos = offset + 8;  // skip "#bundle\0"
                pos += 8;              // skip 8-byte NTP timetag
                while (pos < end)
                {
                    int elementSize = ReadInt32BigEndian(buf, pos);
                    pos += 4;
                    ParseOscPacket(buf, pos, pos + elementSize);
                    pos += elementSize;
                }
            }
            else
            {
                ParseOscMessage(buf, offset, end);
            }
        }

        private void ParseOscMessage(byte[] buf, int offset, int end)
        {
            int pos = offset;
            string address = ReadOscString(buf, ref pos);
            string typeTag = ReadOscString(buf, ref pos); // starts with ','

            float firstFloat = 0f;
            bool gotFloat = false;

            for (int i = 1; i < typeTag.Length; i++) // skip leading ','
            {
                char tag = typeTag[i];
                switch (tag)
                {
                    case 'f':
                        float f = ReadFloat32BigEndian(buf, pos);
                        pos += 4;
                        if (!gotFloat) { firstFloat = f; gotFloat = true; }
                        break;
                    case 'i':
                        pos += 4;
                        break;
                    case 'd':
                        pos += 8;
                        break;
                    case 's':
                        ReadOscString(buf, ref pos);
                        break;
                    default:
                        i = typeTag.Length; // unknown tag type — stop parsing this message safely
                        break;
                }
            }

            if (!gotFloat) return;

            if (address == ppgAddress)
            {
                lock (_lock) { _latestPpg = firstFloat; _hasPpg = true; }
                ProcessPpgSample(firstFloat);
            }
            else if (address == edaAddress)
            {
                lock (_lock) { _latestEda = firstFloat; _hasEda = true; }
            }
        }

        /// <summary>
        /// Basic detrend + adaptive-threshold peak detector run per PPG sample.
        /// Runs on the listener thread; only ever writes the small set of
        /// _latest* fields under lock for the main thread to read.
        /// </summary>
        private void ProcessPpgSample(float raw)
        {
            // --- Light detrending via moving average subtraction ---
            _detrendWindow.Enqueue(raw);
            _detrendSum += raw;
            if (_detrendWindow.Count > detrendWindowSamples)
                _detrendSum -= _detrendWindow.Dequeue();
            float baseline = _detrendSum / _detrendWindow.Count;
            float detrended = raw - baseline;

            // --- Track local mean/std of the detrended signal for adaptive threshold ---
            _statsWindow.Enqueue(detrended);
            if (_statsWindow.Count > _statsWindowCapacity)
                _statsWindow.Dequeue();

            float mean = 0f;
            foreach (var v in _statsWindow) mean += v;
            mean /= _statsWindow.Count;

            float variance = 0f;
            foreach (var v in _statsWindow) variance += (v - mean) * (v - mean);
            variance /= Mathf.Max(1, _statsWindow.Count - 1);
            float stdDev = Mathf.Sqrt(variance);

            float threshold = mean + peakThresholdStdDevs * stdDev;

            // Synthetic sample clock (seconds), advanced one sample-period per call.
            _sampleClockSec += 1.0 / ppgSampleRateHz;

            double minIbiSec = 60.0 / maxPlausibleBpm; // refractory period from max plausible HR
            double maxIbiSec = 60.0 / minPlausibleBpm;

            bool aboveThreshold = detrended > threshold;
            bool refractoryOk = _lastPeakTimeSec < 0 || (_sampleClockSec - _lastPeakTimeSec) >= minIbiSec;

            if (aboveThreshold && refractoryOk)
            {
                if (_lastPeakTimeSec >= 0)
                {
                    double ibiSec = _sampleClockSec - _lastPeakTimeSec;
                    if (ibiSec <= maxIbiSec)
                    {
                        double ibiMs = ibiSec * 1000.0;
                        _ibiHistoryMs.Add(ibiMs);
                        if (_ibiHistoryMs.Count > ibiHistorySize)
                            _ibiHistoryMs.RemoveAt(0);

                        UpdateHrAndHrv();
                    }
                }
                _lastPeakTimeSec = _sampleClockSec;
            }
        }

        private void UpdateHrAndHrv()
        {
            if (_ibiHistoryMs.Count < 2) return;

            double meanIbiMs = 0;
            foreach (var ibi in _ibiHistoryMs) meanIbiMs += ibi;
            meanIbiMs /= _ibiHistoryMs.Count;

            float hrBpm = (float)(60000.0 / meanIbiMs);

            double sumSquaredDiffs = 0;
            int diffCount = 0;
            for (int i = 1; i < _ibiHistoryMs.Count; i++)
            {
                double diff = _ibiHistoryMs[i] - _ibiHistoryMs[i - 1];
                sumSquaredDiffs += diff * diff;
                diffCount++;
            }
            float rmssdMs = diffCount > 0 ? (float)Math.Sqrt(sumSquaredDiffs / diffCount) : 0f;

            lock (_lock)
            {
                _latestHrBpm = hrBpm;
                _latestHrvRmssdMs = rmssdMs;
                _hasHr = true;
                _hasHrv = diffCount > 0;
            }
        }

        private static string ReadOscString(byte[] buf, ref int pos)
        {
            int start = pos;
            while (pos < buf.Length && buf[pos] != 0) pos++;
            string s = Encoding.ASCII.GetString(buf, start, pos - start);
            pos++;
            pos = (pos + 3) & ~3;
            return s;
        }

        private static int ReadInt32BigEndian(byte[] buf, int pos)
        {
            return (buf[pos] << 24) | (buf[pos + 1] << 16) | (buf[pos + 2] << 8) | buf[pos + 3];
        }

        private static float ReadFloat32BigEndian(byte[] buf, int pos)
        {
            byte[] bytes = { buf[pos + 3], buf[pos + 2], buf[pos + 1], buf[pos] };
            return BitConverter.ToSingle(bytes, 0);
        }

        // --- Public accessors used by EmotibitChannelReader ---

        public float GetRawPpg() { lock (_lock) { return _hasPpg ? _latestPpg : 0f; } }
        public float GetRawEda() { lock (_lock) { return _hasEda ? _latestEda : 0f; } }
        public float GetHeartRateBpm() { lock (_lock) { return _hasHr ? _latestHrBpm : 0f; } }
        public float GetHrvRmssdMs() { lock (_lock) { return _hasHrv ? _latestHrvRmssdMs : 0f; } }

        private void OnDestroy()
        {
            _running = false;
            try { _udpClient?.Close(); } catch { /* ignore on shutdown */ }
            _listenThread?.Join(300);
            if (Instance == this) Instance = null;
        }
    }
}

