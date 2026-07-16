using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Delphi
{
    /// <summary>
    /// Owns the single UDP socket receiving OSC messages relayed by
    /// PolarH10/polar_h10_stream.py, which owns the actual BLE connection to
    /// the strap. Unlike EmotibitOscConnection, there is no peak detection
    /// here — the Polar H10 already reports its own onboard-detected BPM and
    /// RR-intervals over the standard BLE Heart Rate service, so this class
    /// only needs to parse those two floats and derive RMSSD from the
    /// RR-interval history (standard consecutive-difference RMSSD, same
    /// formula EmotibitOscConnection uses, just over already-clean beats
    /// instead of ones detected from a raw PPG signal here).
    ///
    /// Only ONE of these should exist in the scene (singleton). Multiple
    /// PolarH10ChannelReader components read from it via Instance, avoiding
    /// multiple sockets/connections for HR/RMSSD.
    /// </summary>
    public class PolarH10OscConnection : MonoBehaviour
    {
        public static PolarH10OscConnection Instance { get; private set; }

        [Header("OSC Listener Settings")]
        [Tooltip("Must match OSC_PORT in polar_h10_stream.py")]
        [SerializeField] private int listenPort = 9500;
        [SerializeField] private string hrAddress = "/PolarH10/HR";
        [SerializeField] private string rrAddress = "/PolarH10/RR";
        [SerializeField] private bool logParseErrors = false;

        [Header("HRV Settings")]
        [Tooltip("Number of most recent RR-intervals used for RMSSD")]
        [SerializeField] private int rrHistorySize = 12;

        public enum AccAxis { X, Y, Z }

        [Header("Accelerometer")]
        [SerializeField] private string accXAddress = "/PolarH10/AccX";
        [SerializeField] private string accYAddress = "/PolarH10/AccY";
        [SerializeField] private string accZAddress = "/PolarH10/AccZ";
        [Tooltip("On: only the axis below is exposed through PolarH10ChannelReader (the other two read NaN/NoSignal). Off: all 3 axes are exposed. Which physical axis is actually front/back depends on how the strap sits on the chest — verify against Polar's own axis diagram rather than assuming, then set it below.")]
        [SerializeField] private bool lockToFrontBackAxis = false;
        [SerializeField] private AccAxis frontBackAxis = AccAxis.Z;

        public bool LockToFrontBackAxis => lockToFrontBackAxis;
        public AccAxis FrontBackAxis => frontBackAxis;

        [Header("Python Bridge")]
        [Tooltip("Launch PolarH10/polar_h10_stream.py automatically on Play and stop it when Play ends. Disable to run it yourself in a terminal instead.")]
        [SerializeField] private bool autoStartPythonScript = true;

        private UdpClient _udpClient;
        private Thread _listenThread;
        private volatile bool _running;
        private Process _pythonProcess;

        private readonly object _lock = new object();

        private float _latestHrBpm;
        private bool _hasHr;

        private float _latestRmssdMs;
        private bool _hasRmssd;

        private float _latestAccXmG, _latestAccYmG, _latestAccZmG;
        private bool _hasAcc;

        // Touched only from the listener thread.
        private readonly List<float> _rrHistoryMs = new List<float>();

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("[PolarH10OscConnection] Duplicate instance found — only one should exist. Destroying duplicate.");
                Destroy(this);
                return;
            }
            Instance = this;

            try
            {
                _udpClient = new UdpClient(listenPort);
            }
            catch (Exception e)
            {
                Debug.LogError($"[PolarH10OscConnection] Failed to bind UDP port {listenPort}: {e.Message}");
                enabled = false;
                return;
            }

            _running = true;
            _listenThread = new Thread(ListenLoop) { IsBackground = true };
            _listenThread.Start();

            StartPythonBridge();
        }

        // Launches PolarH10/polar_h10_stream.py from the project's dedicated
        // venv (sibling of Assets/, same layout on Windows once migrated —
        // see the isWindows branch below for the venv's differing folder
        // structure there). Output is piped into this Console with a
        // "[PolarH10 python]" prefix instead of needing its own terminal.
        private void StartPythonBridge()
        {
            if (!autoStartPythonScript) return;

            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            string venvDir = Path.Combine(projectRoot, "PolarH10PythonEnv");
            bool isWindows = Application.platform == RuntimePlatform.WindowsEditor
                           || Application.platform == RuntimePlatform.WindowsPlayer;
            string pythonExe = Path.Combine(venvDir, isWindows ? "Scripts" : "bin", isWindows ? "python.exe" : "python");
            string scriptPath = Path.Combine(projectRoot, "PolarH10", "polar_h10_stream.py");

            if (!File.Exists(pythonExe))
            {
                Debug.LogError($"[PolarH10OscConnection] Python venv not found at {pythonExe}. Run the PolarH10 setup steps first, or disable Auto Start Python Script.");
                return;
            }
            if (!File.Exists(scriptPath))
            {
                Debug.LogError($"[PolarH10OscConnection] Script not found at {scriptPath}.");
                return;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = pythonExe,
                Arguments = $"\"{scriptPath}\"",
                WorkingDirectory = projectRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            try
            {
                _pythonProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                _pythonProcess.OutputDataReceived += (_, e) => { if (e.Data != null) Debug.Log($"[PolarH10 python] {e.Data}"); };
                _pythonProcess.ErrorDataReceived  += (_, e) => { if (e.Data != null) Debug.LogWarning($"[PolarH10 python] {e.Data}"); };
                _pythonProcess.Start();
                _pythonProcess.BeginOutputReadLine();
                _pythonProcess.BeginErrorReadLine();
                Debug.Log($"[PolarH10OscConnection] Launched Python bridge (PID {_pythonProcess.Id}).");
            }
            catch (Exception e)
            {
                Debug.LogError($"[PolarH10OscConnection] Failed to launch Python bridge: {e.Message}");
            }
        }

        private void StopPythonBridge()
        {
            if (_pythonProcess == null) return;
            try
            {
                if (!_pythonProcess.HasExited)
                {
                    _pythonProcess.Kill();
                    _pythonProcess.WaitForExit(1000);
                }
            }
            catch (Exception)
            {
                // Already exited or inaccessible — nothing to clean up.
            }
            _pythonProcess.Dispose();
            _pythonProcess = null;
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

                if (logParseErrors)
                    Debug.Log($"[PolarH10OscConnection] Received {data.Length} bytes from {remoteEP}");

                try
                {
                    ParseOscPacket(data, 0, data.Length);
                }
                catch (Exception e)
                {
                    if (logParseErrors)
                        Debug.LogWarning($"[PolarH10OscConnection] Failed to parse OSC packet: {e.Message}");
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

            if (!gotFloat)
            {
                if (logParseErrors)
                    Debug.LogWarning($"[PolarH10OscConnection] Message to '{address}' had no float arg (typeTag='{typeTag}').");
                return;
            }

            if (address == hrAddress)
            {
                lock (_lock) { _latestHrBpm = firstFloat; _hasHr = true; }
                if (logParseErrors)
                    Debug.Log($"[PolarH10OscConnection] HR {firstFloat} bpm");
            }
            else if (address == rrAddress)
            {
                OnRrInterval(firstFloat);
                if (logParseErrors)
                    Debug.Log($"[PolarH10OscConnection] RR {firstFloat} ms");
            }
            else if (address == accXAddress)
            {
                lock (_lock) { _latestAccXmG = firstFloat; _hasAcc = true; }
            }
            else if (address == accYAddress)
            {
                lock (_lock) { _latestAccYmG = firstFloat; _hasAcc = true; }
            }
            else if (address == accZAddress)
            {
                lock (_lock) { _latestAccZmG = firstFloat; _hasAcc = true; }
            }
            else if (logParseErrors)
            {
                Debug.LogWarning($"[PolarH10OscConnection] Unrecognized OSC address '{address}' (expected '{hrAddress}' or '{rrAddress}').");
            }
        }

        // Runs on the listener thread; only ever writes the small set of
        // _latest* fields under lock for the main thread to read.
        private void OnRrInterval(float rrMs)
        {
            _rrHistoryMs.Add(rrMs);
            if (_rrHistoryMs.Count > rrHistorySize)
                _rrHistoryMs.RemoveAt(0);

            if (_rrHistoryMs.Count < 2) return;

            double sumSquaredDiffs = 0;
            int diffCount = 0;
            for (int i = 1; i < _rrHistoryMs.Count; i++)
            {
                double diff = _rrHistoryMs[i] - _rrHistoryMs[i - 1];
                sumSquaredDiffs += diff * diff;
                diffCount++;
            }
            float rmssdMs = (float)Math.Sqrt(sumSquaredDiffs / diffCount);

            lock (_lock)
            {
                _latestRmssdMs = rmssdMs;
                _hasRmssd = true;
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

        // --- Public accessors used by PolarH10ChannelReader ---

        public float GetHeartRateBpm() { lock (_lock) { return _hasHr ? _latestHrBpm : float.NaN; } }
        public float GetHrvRmssdMs() { lock (_lock) { return _hasRmssd ? _latestRmssdMs : float.NaN; } }
        public float GetAccXmG() { lock (_lock) { return _hasAcc ? _latestAccXmG : float.NaN; } }
        public float GetAccYmG() { lock (_lock) { return _hasAcc ? _latestAccYmG : float.NaN; } }
        public float GetAccZmG() { lock (_lock) { return _hasAcc ? _latestAccZmG : float.NaN; } }

        private void OnDestroy()
        {
            _running = false;
            try { _udpClient?.Close(); } catch { /* ignore on shutdown */ }
            _listenThread?.Join(300);
            StopPythonBridge();
            if (Instance == this) Instance = null;
        }
    }
}
