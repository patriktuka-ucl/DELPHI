using System;
using System.IO.Ports;
using System.Threading;
using UnityEngine;

namespace Delphi
{
    /// <summary>
    /// Real Grove GSR sensor read over a COM port. Inherits ScalarSensor, so it
    /// slots into any ScalarSensor field on DelphiManager — same as MockSensor.
    ///
    /// Outputs the RAW ADC reading (0-1023) as-is, no conversion. Simple and
    /// safe — no calibration formula to get wrong. Convert to resistance /
    /// conductance later in the signal-processing layer once we've actually
    /// looked at how the raw signal behaves. Reading happens on a background
    /// thread so a slow or stalled port never blocks Unity's main thread.
    /// </summary>
    public class GSRSensorSerial : ScalarSensor
    {
        [Header("Serial connection")]
        [Tooltip("e.g. COM3 on Windows, /dev/tty.usbmodemXXXX on Mac.")]
        public string portName = "COM3";
        public int baudRate = 115200;

        /// <summary>Latest raw ADC reading (0–1023). NaN until the first sample arrives.</summary>
        public override float Current { get; protected set; } = float.NaN;

        public bool IsConnected { get; private set; }

        private SerialPort _port;
        private Thread _readThread;
        private volatile bool _running;

        // Shared between the background thread and the main thread.
        private readonly object _lock = new object();
        private int _latestRaw;
        private bool _hasNewRaw;

        private void OnEnable()  => Connect();
        private void OnDisable() => Disconnect();

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
                Debug.Log($"[GSRSensorSerial] Connected to {portName} @ {baudRate} baud. " +
                          $"Board may take ~1-2s to start streaming after reset.");
            }
            catch (Exception e)
            {
                IsConnected = false;
                Debug.LogWarning($"[GSRSensorSerial] Could not open {portName}: {e.Message}");
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
                            _hasNewRaw = true;
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
                        Debug.LogWarning($"[GSRSensorSerial] Read error: {e.Message}");
                }
            }
        }

        /// <summary>
        /// Called once per frame by the manager. Picks up whatever the
        /// background thread has read since the last call. Never blocks.
        /// </summary>
        public override float ReadValue()
        {
            lock (_lock)
            {
                if (_hasNewRaw)
                {
                    Current = _latestRaw;
                    _hasNewRaw = false;
                }
            }
            return Current;
        }

        private void Disconnect()
        {
            _running = false;
            try { _readThread?.Join(300); } catch { }
            try { _port?.Close(); } catch { }
            _port = null;
            IsConnected = false;
            Debug.Log("[GSRSensorSerial] Disconnected.");
        }

        [ContextMenu("List Available Ports")]
        private void ListAvailablePorts()
        {
            var ports = SerialPort.GetPortNames();
            Debug.Log("[GSRSensorSerial] Available ports: " +
                      (ports.Length > 0 ? string.Join(", ", ports) : "(none found)"));
        }
    }
}