using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

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
        [Tooltip("Source of the tilt cue.")]
        public CarMotionCues cues;
        [Tooltip("Source of the rumble cue. Independent of `cues` — either can " +
                 "be absent and the other still works. Without this, rumble " +
                 "has no model and the vibration field is sent as zeros.")]
        public CarRumbleCues rumble;

        [Header("Output modes — TILT and RUMBLE are two completely " +
                 "independent cue channels sharing one packet. Either, both, " +
                 "or neither. Note that the rig must still be STARTED for " +
                 "either to reach it: the vibration field rides inside the " +
                 "motion packet, which only goes out while Started, and " +
                 "Started is also the hardware's own 'powered and holding' " +
                 "gate. So Start is the transport; these two are the content.")]
        [Tooltip("Off = the rig is sent level angles (0/0/0) and stays still, " +
                 "while CarMotionCues keeps computing and logging exactly as " +
                 "normal. This is what makes rumble-only mode possible.")]
        public bool tiltEnabled = true;
        [Tooltip("Off = the vibration field is sent as zeros. CarRumbleCues " +
                 "keeps modelling, it just isn't transported.")]
        public bool rumbleEnabled = true;
        [Tooltip("How long the seat takes to ease to level when tilt is " +
                 "switched off mid-drive (and to pick the cue back up when " +
                 "it's switched on). Never instant — the rig slews to whatever " +
                 "angle it is sent as fast as it physically can, so a bare " +
                 "toggle would be a lurch.")]
        [Range(0.05f, 5f)] public float tiltTransitionSeconds = 0.8f;
        [Tooltip("Frequency sent while there is nothing to say — no rumble " +
                 "model in the scene, or rumble switched off. Intensity is " +
                 "zero in those cases, so this is inaudible either way; it " +
                 "exists only so the field is never garbage.")]
        public int idleRumbleHz = 40;

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

            if (cues == null) cues = FindAnyObjectByType<CarMotionCues>();
            if (rumble == null) rumble = FindAnyObjectByType<CarRumbleCues>();

            // Start already faded in/out to match whatever the toggle says, so
            // a scene saved with tilt off doesn't spend the first second
            // ramping down from a lean it never had.
            _tiltBlend = tiltEnabled ? 1f : 0f;

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

        [Header("Wire deadband — the rig slews to whatever angle each packet " +
                 "names, so a value that dithers in the last decimal makes it " +
                 "hunt back and forth in place. Angles are quantised to the " +
                 "0.01° the protocol actually carries, then held until they " +
                 "move by at least this much; the rig still gets a packet " +
                 "every tick, it just gets a BIT-IDENTICAL one while the seat " +
                 "is settled instead of a ±0.01° twitch.")]
        [Range(0f, 1f)] public float minAngleChangeDeg = 0.05f;

        // Last angles actually put on the wire, so the deadband above has
        // something to hold. NaN = nothing sent yet this session.
        private float _sentYaw = float.NaN, _sentPitch = float.NaN, _sentRoll = float.NaN;

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

        // Manual rumble — the tester's bench. Deliberately overrides BOTH the
        // model and rumbleEnabled: the whole point of the bench is to drive a
        // single pad at a known intensity with the model out of the way, and
        // having to remember to switch rumble on first would just be a trap.
        private (int r, int c, int l, int hz)? _manualRumble;

        /// <summary>Drive the three pads directly, bypassing CarRumbleCues and
        /// the rumbleEnabled switch. For YawVR3Tester's rumble bench — this is
        /// how minEffectiveIntensity and the usable Hz range get MEASURED
        /// rather than guessed. Not used anywhere in the normal driving
        /// path.</summary>
        public void SetManualRumble(int right, int centre, int left, int hz) =>
            _manualRumble = (Mathf.Clamp(right, 0, 100), Mathf.Clamp(centre, 0, 100),
                             Mathf.Clamp(left, 0, 100), Mathf.Clamp(hz, 0, 255));

        /// <summary>Hand the vibration field back to CarRumbleCues.</summary>
        public void ClearManualRumble() => _manualRumble = null;

        public bool HasManualRumble => _manualRumble.HasValue;

        /// <summary>How far tilt is faded in, 0-1. 1 = full cue, 0 = level.
        /// Exposed so the UI can show a switch mid-transition rather than
        /// claiming it has already taken effect.</summary>
        public float TiltBlend => _tiltBlend;

        // ── What actually went on the wire last tick — for the tester
        //    readout and the researcher UI. These are the transported values,
        //    which is not the same thing as what the model asked for.
        public int SentRumbleRight { get; private set; }
        public int SentRumbleCentre { get; private set; }
        public int SentRumbleLeft { get; private set; }
        public int SentRumbleHz { get; private set; }

        private float _tiltBlend = 1f;

        // ── Motion tick — UDP, only while Started ────────────────────────
        private void SendMotionTick()
        {
            // Actual elapsed time since the last send, not Time.deltaTime —
            // this now runs at motionSendRateHz, not every render frame, so
            // a per-frame delta would under-ramp the manual-test smoothing.
            float sendDt = _lastMotionSendTime > 0f ? Time.unscaledTime - _lastMotionSendTime : 0f;
            _lastMotionSendTime = Time.unscaledTime;

            // Tilt fade. A blend rather than a second servo: scaling the
            // commanded angles toward zero converges on EXACTLY level with no
            // state of its own to go stale, and it takes yaw with it, so
            // switching tilt off also unwinds the rig back to home heading
            // instead of leaving it parked wherever the last corner left it.
            _tiltBlend = Mathf.MoveTowards(_tiltBlend, tiltEnabled ? 1f : 0f,
                                           sendDt / Mathf.Max(0.05f, tiltTransitionSeconds));

            float yaw = 0f, pitch = 0f, roll = 0f;
            if (_manualOverride.HasValue)
            {
                _manualCurrent = Vector3.MoveTowards(_manualCurrent, _manualOverride.Value,
                                                      manualMaxDegreesPerSecond * sendDt);
                pitch = _manualCurrent.x;
                yaw = _manualCurrent.y;
                roll = _manualCurrent.z;
            }
            else if (cues != null)
            {
                // Straight from the commanded angles, NOT from the reference
                // transform's localEulerAngles. That used to be the transport,
                // and it is lossy: CarMotionCues builds the transform with
                // Quaternion.Euler(pitch, yaw, -roll), and re-decomposing a
                // quaternion back to Euler is not the identity once two axes
                // are non-zero — Unity picks its own canonical branch, so with
                // yaw accumulating through a corner the pitch/roll convention
                // could flip mid-drive. The transform is still written, purely
                // for the Scene view and the researcher UI.
                // Scaled by the tilt fade — at 0 this is a hard level 0/0/0,
                // which is exactly what rumble-only mode needs to send.
                pitch = cues.PitchDeg * _tiltBlend;
                yaw = cues.YawDeg * _tiltBlend;
                roll = -cues.RollDeg * _tiltBlend;   // same sign convention the transform used
            }

            // Quantise to the resolution the wire actually carries (FormatRotation
            // truncates to 0.01°), then hold unless the change is worth making —
            // see minAngleChangeDeg.
            yaw = Deadband(yaw, ref _sentYaw);
            pitch = Deadband(pitch, ref _sentPitch);
            roll = Deadband(roll, ref _sentRoll);

            // ── Vibration — the second, independent cue channel ──────────
            // Three separate pads and a frequency, all modelled by
            // CarRumbleCues; this end only transports them. Sending explicit
            // zeros when rumble is off matters: the rig holds the last
            // vibration command it was given, so simply omitting the update
            // would leave the pads running.
            int right = 0, centre = 0, left = 0, hz = idleRumbleHz;
            if (_manualRumble.HasValue)
            {
                (right, centre, left, hz) = _manualRumble.Value;
            }
            else if (rumbleEnabled && rumble != null)
            {
                // Rumble works harder when it's carrying the cue alone. Read
                // one tick later than it's set, which at 50 Hz is irrelevant.
                rumble.SoloMode = !tiltEnabled;
                right = rumble.MotorRight;
                centre = rumble.MotorCentre;
                left = rumble.MotorLeft;
                hz = rumble.Hz;
            }
            SentRumbleRight = right; SentRumbleCentre = centre;
            SentRumbleLeft = left; SentRumbleHz = hz;

            string message = $"Y[{FormatRotation(yaw)}]P[{FormatRotation(pitch)}]R[{FormatRotation(roll)}]" +
                              $"V[{right},{centre},{left},{hz}]";
            SendUdpToDevice(message);
        }

        /// <summary>Quantise to the wire's own 0.01° resolution and return the
        /// previously-sent value unless the new one has moved by at least
        /// minAngleChangeDeg. `sent` carries the held value between ticks.</summary>
        private float Deadband(float value, ref float sent)
        {
            float quantised = Mathf.Round(value * 100f) / 100f;
            if (float.IsNaN(sent) || Mathf.Abs(quantised - sent) >= minAngleChangeDeg)
                sent = quantised;
            return sent;
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
