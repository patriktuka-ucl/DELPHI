using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using Delphi.Simulation;

namespace Delphi.Motion
{
    public enum YawConnectionState
    {
        Initial, Discovering, Connecting, Connected, Starting, Started, Stopping, Disconnecting
    }

    /// <summary>
    /// Direct client for the YAW VR3 rig's own wire protocol — UDP discovery
    /// + motion, TCP command/control. Reverse-engineered from YawVR's own
    /// Unity/Unreal SDK source (no working package distribution exists right
    /// now — see conversation), so the ports and message shapes below come
    /// straight from that source, not guesswork:
    ///
    ///   UDP broadcast "YAW_CALLING" -> :50010
    ///   UDP reply     "YAWDEVICE;id;name;tcpPort;AVAILABLE|RESERVED"
    ///   TCP connect   -> device:tcpPort
    ///   TCP send      [0x30 CHECK_IN][4-byte BE int: our UDP port][ASCII game name]
    ///   TCP reply     [0x31 CHECK_IN_ANS]["AVAILABLE"]
    ///   TCP send      [0xA1 START]  -> device echoes [0xA1] once actually started
    ///   UDP send      "Y[yaw]P[pitch]R[roll]V[r,c,l,hz]" every tick while started
    ///                 (angles unsigned 000.00-359.99°, never 360.00)
    ///   TCP send      [0xA2 STOP] / [0xA3 EXIT] — mirror of connect
    ///
    /// Threading mirrors this project's established pattern (BoBridge,
    /// PolarH10OscConnection): background threads own the blocking socket
    /// calls, a ConcurrentQueue carries parsed events to the main thread,
    /// Update() drains it and drives the state machine. No MonoBehaviour
    /// code ever touches a socket directly.
    ///
    /// Discovery/connect run automatically from Awake — deliberately chosen
    /// for this rig. Start/Stop are separate, explicit calls: connecting
    /// must never by itself put the rig in motion.
    /// </summary>
    public class YawVR3Connection : MonoBehaviour
    {
        public static YawVR3Connection Instance { get; private set; }

        // ── Protocol constants (from YawVR's own SDK source) ─────────────
        private const int DiscoveryPort = 50010;   // broadcast port AND the device's own listen port for everything
        private const int GameUdpPort = 50060;     // our UDP listen port, told to the device at check-in
        private const string GameName = "DELPHI";

        private static class CommandIds
        {
            public const byte CHECK_IN = 0x30;
            public const byte CHECK_IN_ANS = 0x31;
            public const byte START = 0xA1;
            public const byte STOP = 0xA2;
            public const byte EXIT = 0xA3;
            // Device-side safety clamps — protocol supports these, not wired
            // up yet (deliberately deferred; see conversation).
            public const byte SET_TILT_LIMITS = 0x40;
            public const byte SET_YAW_LIMIT = 0x70;
        }

        [Header("Links (auto-found if left empty)")]
        public CarMotionCues cues;
        public CarDriver car;

        [Header("Rumble / haptic buzzer — subtle idle+road hum, modern-car feel")]
        public bool rumbleEnabled = true;
        [Range(0, 100)] public int rumbleBaseIntensity = 4;
        [Tooltip("Extra intensity per km/h of speed — keep small for a quiet modern car.")]
        public float rumbleSpeedScale = 0.15f;
        [Range(0, 100)] public int rumbleMaxIntensity = 30;
        public int rumbleHz = 45;

        [Header("Discovery")]
        [Tooltip("How often to re-broadcast the discovery ping while no device has been found, in seconds.")]
        public float rediscoverIntervalSeconds = 2f;

        [Header("Motion send rate — a FIXED interval, decoupled from " +
                 "Unity's variable render frame rate. Sending on every raw " +
                 "Update() (60-144+Hz, unevenly spaced) can read as jutter " +
                 "on the rig's own side even when the values themselves are " +
                 "smooth in software; a steady, modest rate tracks better.")]
        public float motionSendRateHz = 50f;

        public YawConnectionState State { get; private set; } = YawConnectionState.Initial;
        public string StatusText { get; private set; } = "Idle";

        // ── Networking state — background threads only below this point ──
        private UdpClient _udp;
        private Thread _udpListenThread;
        private volatile bool _udpRunning;

        private TcpClient _pendingTcp;
        private IAsyncResult _pendingConnect;
        private TcpClient _tcp;
        private NetworkStream _tcpStream;
        private Thread _tcpReadThread;
        private volatile bool _tcpRunning;

        private IPAddress _deviceIp;

        private readonly ConcurrentQueue<Action> _mainThreadActions = new();

        private float _nextDiscoveryTime;
        private float _nextMotionSendTime;
        private float _lastMotionSendTime;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("[YawVR3Connection] Duplicate instance — destroying.");
                Destroy(this);
                return;
            }
            Instance = this;

            if (cues == null) cues = FindFirstObjectByType<CarMotionCues>();
            if (car == null) car = FindFirstObjectByType<CarDriver>();

            // Defensive: if this component gets re-Awake()'d without
            // OnDestroy having run in between — Enter Play Mode Options with
            // Reload Domain/Scene disabled can skip the usual destroy/
            // recreate cycle between Play sessions — release whatever
            // socket/thread it was already holding first. Otherwise the
            // bind below fails silently (caught, logged, component
            // disabled) because the OLD socket is still bound to
            // GameUdpPort, and State/StatusText get stuck at their
            // Initial/"Idle" defaults forever since Update() never runs.
            ShutdownNetworking();

            try
            {
                _udp = new UdpClient(GameUdpPort) { EnableBroadcast = true };
            }
            catch (Exception e)
            {
                Debug.LogError($"[YawVR3Connection] Failed to bind UDP port {GameUdpPort}: {e.Message}. " +
                                "If this keeps happening, fully restart the Unity Editor — a previous " +
                                "Play session's socket may still be holding the port.");
                enabled = false;
                return;
            }

            _udpRunning = true;
            _udpListenThread = new Thread(UdpListenLoop) { IsBackground = true, Name = "YawVR3 UDP listen" };
            _udpListenThread.Start();

            SetState(YawConnectionState.Discovering, "Discovering...");
        }

        private void Update()
        {
            while (_mainThreadActions.TryDequeue(out var action)) action();

            switch (State)
            {
                case YawConnectionState.Discovering:
                    if (Time.unscaledTime >= _nextDiscoveryTime)
                    {
                        _nextDiscoveryTime = Time.unscaledTime + rediscoverIntervalSeconds;
                        BroadcastDiscovery();
                    }
                    break;

                case YawConnectionState.Connecting:
                    PollPendingConnect();
                    break;

                case YawConnectionState.Started:
                    if (Time.unscaledTime >= _nextMotionSendTime)
                    {
                        _nextMotionSendTime = Time.unscaledTime + 1f / Mathf.Max(1f, motionSendRateHz);
                        SendMotionTick();
                    }
                    break;
            }
        }

        // ── Discovery (send: main thread: receive: background thread) ────
        private void BroadcastDiscovery()
        {
            try
            {
                byte[] data = Encoding.ASCII.GetBytes("YAW_CALLING");
                _udp.Send(data, data.Length, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[YawVR3Connection] Discovery broadcast failed: {e.Message}");
            }
        }

        private void UdpListenLoop()
        {
            var remoteEp = new IPEndPoint(IPAddress.Any, 0);
            while (_udpRunning)
            {
                byte[] data;
                try { data = _udp.Receive(ref remoteEp); }
                catch (Exception) { break; } // socket closed during shutdown, or transient error

                string message;
                try { message = Encoding.ASCII.GetString(data); }
                catch (Exception) { continue; }

                if (message.StartsWith("YAWDEVICE"))
                {
                    HandleDiscoveryReply(message, remoteEp);
                }
                // Our own broadcast echoing back, and the rig's self-reported
                // actual position ("Y[..]P[..]R[..]" / "SY[..]...") are both
                // ignored — nothing in DELPHI needs the rig's own telemetry
                // back yet.
            }
        }

        private void HandleDiscoveryReply(string message, IPEndPoint remoteEp)
        {
            var parts = message.Split(';');
            if (parts.Length != 5) return;
            if (!int.TryParse(parts[3], out int tcpPort)) return;
            bool available = parts[4].Contains("AVAILABLE");
            string name = parts[2];
            var ip = remoteEp.Address;

            _mainThreadActions.Enqueue(() =>
            {
                if (State != YawConnectionState.Discovering) return; // already moved on
                if (!available)
                {
                    StatusText = $"{name} is reserved by another game";
                    return;
                }
                _deviceIp = ip;
                SetState(YawConnectionState.Connecting, $"Connecting to {name} ({ip})...");
                BeginTcpConnect(ip.ToString(), tcpPort);
            });
        }

        // ── TCP connect — non-blocking, BoBridge-style poll ──────────────
        private void BeginTcpConnect(string ip, int port)
        {
            _pendingTcp = new TcpClient();
            _pendingConnect = _pendingTcp.BeginConnect(ip, port, null, null);
        }

        private void PollPendingConnect()
        {
            if (_pendingConnect == null || !_pendingConnect.IsCompleted) return;

            var tcp = _pendingTcp;
            var result = _pendingConnect;
            _pendingTcp = null;
            _pendingConnect = null;

            try
            {
                tcp.EndConnect(result);
                _tcp = tcp;
                _tcpStream = tcp.GetStream();
                _tcpRunning = true;
                _tcpReadThread = new Thread(TcpReadLoop) { IsBackground = true, Name = "YawVR3 TCP read" };
                _tcpReadThread.Start();

                SendTcp(BuildCheckIn());
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[YawVR3Connection] TCP connect failed: {e.Message}");
                tcp.Close();
                SetState(YawConnectionState.Discovering, "Discovering...");
            }
        }

        private void TcpReadLoop()
        {
            var buf = new byte[256];
            try
            {
                while (_tcpRunning)
                {
                    int n = _tcpStream.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    byte[] data = new byte[n];
                    Array.Copy(buf, data, n);
                    _mainThreadActions.Enqueue(() => HandleTcpMessage(data));
                }
            }
            catch (Exception e)
            {
                if (_tcpRunning)
                    Debug.LogWarning($"[YawVR3Connection] TCP reader stopped: {e.Message}");
            }
            if (_tcpRunning)
                _mainThreadActions.Enqueue(HandleTcpDisconnect);
        }

        private void HandleTcpDisconnect()
        {
            if (State == YawConnectionState.Initial) return;
            Debug.LogWarning("[YawVR3Connection] Lost TCP connection to rig.");
            CloseTcp();
            SetState(YawConnectionState.Discovering, "Reconnecting...");
        }

        private void HandleTcpMessage(byte[] data)
        {
            if (data.Length == 0) return;
            byte commandId = data[0];

            switch (commandId)
            {
                case CommandIds.CHECK_IN_ANS:
                    string msg = Encoding.ASCII.GetString(data, 1, data.Length - 1);
                    if (msg.Contains("AVAILABLE"))
                    {
                        SetState(YawConnectionState.Connected, "Connected — ready to Start");
                    }
                    else
                    {
                        Debug.LogWarning($"[YawVR3Connection] Check-in rejected: {msg}");
                        CloseTcp();
                        SetState(YawConnectionState.Discovering, "Discovering...");
                    }
                    break;

                case CommandIds.START:
                    if (State == YawConnectionState.Starting)
                        SetState(YawConnectionState.Started, "Started — motion live");
                    break;

                case CommandIds.STOP:
                    if (State == YawConnectionState.Stopping || State == YawConnectionState.Started)
                        SetState(YawConnectionState.Connected, "Connected — ready to Start");
                    break;

                case CommandIds.EXIT:
                    CloseTcp();
                    SetState(YawConnectionState.Discovering, "Discovering...");
                    break;
            }
        }

        // ── Public control — Start/Stop are explicit, never automatic ───
        public void StartMotion()
        {
            if (State != YawConnectionState.Connected) return;
            SendTcp(new[] { CommandIds.START });
            SetState(YawConnectionState.Starting, "Starting...");
        }

        public void StopMotion()
        {
            if (State != YawConnectionState.Started) return;
            SendTcp(new[] { CommandIds.STOP });
            SetState(YawConnectionState.Stopping, "Stopping...");
        }

        // ── Manual test override — see YawVR3Tester ──────────────────────
        // (pitch=x, yaw=y, roll=z) — mirrors the reference transform's own
        // axis layout, so this slots into SendMotionTick with no extra
        // conversion. Null = normal operation (read from `cues`).
        private Vector3? _manualOverride;
        // The rig has no velocity concept of its own — every UDP packet is
        // "be at this exact angle now," and it slews there as fast as it
        // physically can. CarMotionCues rate-limits its own output before
        // it ever reaches here, but a raw nudge target wouldn't be — so we
        // ramp toward it ourselves, same idea, separately tunable (gentler
        // default, since this is the "I'm still nervous about this thing"
        // path).
        private Vector3 _manualCurrent;

        [Header("Manual test smoothing — see YawVR3Tester")]
        [Tooltip("Caps how fast a manual nudge target can actually move the " +
                 "rig. Lower = gentler ramp, higher = snappier response.")]
        public float manualMaxDegreesPerSecond = 15f;

        /// <summary>While set, motion ticks ramp toward THESE angles instead
        /// of reading CarMotionCues. For YawVR3Tester's safe manual testing —
        /// not used anywhere in the normal driving path.</summary>
        public void SetManualAngles(float yaw, float pitch, float roll)
        {
            var target = new Vector3(pitch, yaw, roll);
            if (!_manualOverride.HasValue) _manualCurrent = target; // first activation — nothing stale to ramp from
            _manualOverride = target;
        }

        /// <summary>Hand control back to CarMotionCues.</summary>
        public void ClearManualOverride() => _manualOverride = null;

        // ── Motion tick — UDP, only while Started ────────────────────────
        private void SendMotionTick()
        {
            // Actual elapsed time since the last send, not Time.deltaTime —
            // this now runs at motionSendRateHz, not every render frame, so
            // a per-frame delta would under-ramp the manual-test smoothing.
            float sendDt = _lastMotionSendTime > 0f ? Time.unscaledTime - _lastMotionSendTime : 0f;
            _lastMotionSendTime = Time.unscaledTime;

            float yaw = 0f, pitch = 0f, roll = 0f;
            if (_manualOverride.HasValue)
            {
                _manualCurrent = Vector3.MoveTowards(_manualCurrent, _manualOverride.Value,
                                                      manualMaxDegreesPerSecond * sendDt);
                pitch = _manualCurrent.x;
                yaw = _manualCurrent.y;
                roll = _manualCurrent.z;
            }
            else if (cues != null && cues.referenceTransform != null)
            {
                // Transform.localEulerAngles is already Unity's canonical
                // 0-360 unsigned decomposition — exactly the form the wire
                // protocol wants, no extra sign handling needed.
                var e = cues.referenceTransform.localEulerAngles;
                pitch = e.x;
                yaw = e.y;
                roll = e.z;
            }

            int buzz = 0;
            if (rumbleEnabled && car != null)
                buzz = Mathf.Clamp(Mathf.RoundToInt(rumbleBaseIntensity + rumbleSpeedScale * car.CurrentSpeedKmh),
                                    0, rumbleMaxIntensity);

            string message = $"Y[{FormatRotation(yaw)}]P[{FormatRotation(pitch)}]R[{FormatRotation(roll)}]" +
                              $"V[{buzz},{buzz},{buzz},{rumbleHz}]";
            SendUdpToDevice(message);
        }

        private void SendUdpToDevice(string message)
        {
            if (_deviceIp == null) return;
            try
            {
                byte[] data = Encoding.ASCII.GetBytes(message);
                _udp.Send(data, data.Length, new IPEndPoint(_deviceIp, DiscoveryPort));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[YawVR3Connection] Failed to send motion UDP: {e.Message}");
            }
        }

        // ── Formatting — mirrors YawVR's own Commands.FormatRotation ─────
        private static string FormatRotation(float f)
        {
            float i = (int)(f * 100) / 100f;
            while (i < 0) i += 360;
            while (i >= 360) i -= 360;
            string s = i.ToString("0.##", CultureInfo.InvariantCulture);
            if (i < 10) s = "00" + s;
            else if (i < 100) s = "0" + s;
            int dot = s.IndexOf('.');
            if (dot < 0) s += ".00";
            else if (s.Length - dot == 2) s += "0";
            return s;
        }

        private static byte[] BuildCheckIn()
        {
            byte[] portBytes = BitConverter.GetBytes(GameUdpPort);
            if (BitConverter.IsLittleEndian) Array.Reverse(portBytes);
            byte[] nameBytes = Encoding.ASCII.GetBytes(GameName);

            byte[] message = new byte[1 + portBytes.Length + nameBytes.Length];
            message[0] = CommandIds.CHECK_IN;
            portBytes.CopyTo(message, 1);
            nameBytes.CopyTo(message, 1 + portBytes.Length);
            return message;
        }

        private void SendTcp(byte[] data)
        {
            try { _tcpStream?.Write(data, 0, data.Length); }
            catch (Exception e) { Debug.LogWarning($"[YawVR3Connection] TCP send failed: {e.Message}"); }
        }

        // ── State/teardown ────────────────────────────────────────────
        private void SetState(YawConnectionState newState, string status)
        {
            State = newState;
            StatusText = status;
            Debug.Log($"[YawVR3Connection] {newState}: {status}");
        }

        private void CloseTcp()
        {
            _tcpRunning = false;
            try { _tcpStream?.Close(); } catch { /* ignore on shutdown */ }
            try { _tcp?.Close(); } catch { /* ignore on shutdown */ }
            _tcpStream = null;
            _tcp = null;
            _deviceIp = null;
        }

        /// <summary>Releases the UDP socket/thread and TCP connection — safe
        /// to call even when nothing is open (idempotent). Used both
        /// defensively from Awake() (see there) and for real teardown from
        /// OnDestroy.</summary>
        private void ShutdownNetworking()
        {
            _udpRunning = false;
            try { _udp?.Close(); } catch { /* ignore on shutdown */ }
            try { _udpListenThread?.Join(300); } catch { /* ignore on shutdown */ }
            _udp = null;
            _udpListenThread = null;

            CloseTcp();
            try { _tcpReadThread?.Join(300); } catch { /* ignore on shutdown */ }
            _tcpReadThread = null;
        }

        private void OnDestroy()
        {
            ShutdownNetworking();
            if (Instance == this) Instance = null;
        }

        private void OnApplicationQuit() => OnDestroy();
    }
}
