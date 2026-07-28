using System;
using System.IO.Ports;
using System.Threading;
using UnityEngine;

namespace Delphi
{
    /// <summary>
    /// Owns the single SerialPort connection to the Grove GSR sensor's
    /// Arduino. Mirrors PolarH10OscConnection's role for the Polar strap —
    /// the only thing that opens/reads the port — so GSRRawSensor can stay a
    /// thin reader, the same shape as PolarH10ChannelReader reading from
    /// PolarH10OscConnection.Instance. Split out from what used to be a
    /// single GSRSensorSerial script purely for that structural consistency;
    /// GSR only ever has this one raw channel, so unlike the H10 (5 channels
    /// over one BLE link) there's no fan-out here that requires the split.
    ///
    /// Only ONE of these should exist in the scene (singleton).
    /// </summary>
    public class GSRSerialConnection : MonoBehaviour
    {
        public static GSRSerialConnection Instance { get; private set; }

        [Header("Serial connection")]
        [Tooltip("e.g. COM3 on Windows, /dev/tty.usbmodemXXXX on Mac.")]
        [SerializeField] private string portName = "COM3";
        [SerializeField] private int baudRate = 115200;

        public bool IsConnected { get; private set; }

        private SerialPort _port;
        private Thread _readThread;
        private volatile bool _running;

        // Shared between the background thread and readers calling GetRawValue().
        private readonly object _lock = new object();
        private int _latestRaw;
        private bool _hasRaw;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("[GSRSerialConnection] Duplicate instance found — only one should exist. Destroying duplicate.");
                Destroy(this);
                return;
            }
            Instance = this;

            var mgr = FindFirstObjectByType<DelphiManager>();
            if (mgr != null && !mgr.IsAnyChannelOn(Channel.GSR, Channel.GsrTonic, Channel.GsrPhasic))
            {
                Debug.Log("[GSRSerialConnection] GSR/GsrTonic/GsrPhasic all disabled in DelphiManager — skipping connection.");
                return;
            }

            Connect();
        }

        private void Connect()
        {
            try
            {
                _port = new SerialPort(portName, baudRate)
                {
                    ReadTimeout = 2000,
                    DtrEnable   = true,   // needed to wake the Uno's USB-serial bridge
                    RtsEnable   = true,
                    NewLine     = "\r\n"  // matches Arduino's Serial.println() line ending
                };
                _port.Open();
                _running = true;
                _readThread = new Thread(ReadLoop) { IsBackground = true };
                _readThread.Start();
                IsConnected = true;
                Debug.Log($"[GSRSerialConnection] Connected to {portName} @ {baudRate} baud. " +
                          $"Board may take ~1-2s to start streaming after reset.");
            }
            catch (Exception e)
            {
                IsConnected = false;
                Debug.LogWarning($"[GSRSerialConnection] Could not open {portName}: {e.Message}");
            }
        }

        // Runs on a background thread. Blocks on ReadLine(), which is fine
        // here — it never touches the Unity main thread.
        private void ReadLoop()
        {
            while (_running)
            {
                try
                {
                    string line = _port.ReadLine();
                    if (int.TryParse(line.Trim(), out int raw))
                    {
                        lock (_lock)
                        {
                            _latestRaw = raw;
                            _hasRaw = true;
                        }
                    }
                }
                catch (TimeoutException)
                {
                    // No line arrived within the timeout — normal, just retry.
                }
                catch (Exception e)
                {
                    if (_running)
                        Debug.LogWarning($"[GSRSerialConnection] Read error: {e.Message}");
                }
            }
        }

        /// <summary>Latest raw ADC reading (0–1023), lock-protected. NaN
        /// until the first sample arrives. Called from DelphiCore's sampling
        /// thread via GSRRawSensor.</summary>
        public float GetRawValue()
        {
            lock (_lock) { return _hasRaw ? _latestRaw : float.NaN; }
        }

        private void OnDestroy()
        {
            _running = false;
            try { _readThread?.Join(300); } catch { }
            try { _port?.Close(); } catch { }
            _port = null;
            IsConnected = false;
            if (Instance == this) Instance = null;
            Debug.Log("[GSRSerialConnection] Disconnected.");
        }

        [ContextMenu("List Available Ports")]
        private void ListAvailablePorts()
        {
            var ports = SerialPort.GetPortNames();
            Debug.Log("[GSRSerialConnection] Available ports: " +
                      (ports.Length > 0 ? string.Join(", ", ports) : "(none found)"));
        }
    }
}
