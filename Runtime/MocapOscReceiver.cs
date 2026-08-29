using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace MocapTools
{
    /// <summary>
    /// Latest 26-joint kinematic frame for one glove, decoded from the StretchSense
    /// Reality Core Driver OSC stream. Joint order matches OpenXR XrHandJointEXT.
    /// Positions/rotations are glove-local hand space.
    /// </summary>
    public struct GloveKinematicFrame
    {
        public const int JointCount = 26;

        public int handedness;
        public string serial;
        public string gloveType;
        public Vector3[] positions;
        public Quaternion[] rotations;
        public float receivedTime;

        public bool IsValid => positions != null && positions.Length == JointCount &&
                                rotations != null && rotations.Length == JointCount;
    }

    /// <summary>
    /// Background-thread OSC receiver for the StretchSense Reality Core Driver direct
    /// OSC stream (XR Game / XR Train, Connection Mode = OpenXR).
    ///
    /// XR Game streams to UDP loopback port 9002:
    ///   /v1/animation/kinematic/all  - 26 joints x (position xyz + rotation xyzw) per hand
    ///   /v1/orientation/all          - raw accelerometer xyz + hand orientation quat xyzw
    ///   /v1/controller_input/all     - emulated button/scalar states
    ///
    /// All integers and floats are big-endian. Handedness: 1 = LEFT, 2 = RIGHT.
    /// This path intentionally bypasses the StretchSense OpenXR API layer, so the
    /// layer must not be loaded into this process (it would hold port 9002).
    /// </summary>
    public class MocapOscReceiver : MonoBehaviour
    {
        public const string KinematicAddress = "/v1/animation/kinematic/all";
        public const string OrientationAddress = "/v1/orientation/all";
        public const string ControllerInputAddress = "/v1/controller_input/all";
        /// <summary>
        /// Unity listens on 19002, the relay's forward port. XR Game streams to
        /// 9002, which must NOT be bound inside the Unity process (XR Game stops
        /// streaming to ports held by Unity). The standalone MocapOscRelay
        /// bridges 9002 -> 19002. </summary>
        public const int DefaultPort = 19002;
        public const int LeftHand = 1;
        public const int RightHand = 2;

        [Tooltip("UDP port the Reality Core Driver streams OSC to.")]
        public int port = DefaultPort;

        [Tooltip("Log a periodic packet-rate status line while receiving.")]
        public bool logStatus = true;

        [Tooltip("Log the detailed per-5s parse diagnostics line (debug only).")]
        public bool logDiagnostics = false;

        readonly object _lock = new object();
        readonly Dictionary<int, GloveKinematicFrame> _latestFrames = new Dictionary<int, GloveKinematicFrame>();
        readonly Dictionary<int, Quaternion> _orientations = new Dictionary<int, Quaternion>();
        readonly Dictionary<int, Vector3> _accelerations = new Dictionary<int, Vector3>();
        readonly Dictionary<int, string> _serials = new Dictionary<int, string>();
        readonly List<int> _pendingKinematicHands = new List<int>();
        readonly List<int> _pendingOrientationHands = new List<int>();

        readonly byte[] _floatBytes = new byte[4];
        readonly Stopwatch _clock = new Stopwatch();
        UdpClient _client;
        Thread _receiveThread;
        volatile bool _running;
        bool _bindFailed;
        int _packetsSinceLog;
        float _lastLogTime = float.NegativeInfinity;
        float _lastPacketTime = float.NegativeInfinity;
        bool _loggedFirstPacket;
        bool _loggedBind;
        bool _loggedFirstDatagram;
        long _datagramsReceived;
        long _countKinematic;
        long _countOrientation;
        long _countOtherAddress;
        long _countBadHeader;
        long _countBadTag;
        long _countKinBranch;
        long _countFailFloat;
        string _firstBigAddress;
        string _firstBigTag;
        int _firstBigLen;

        public event Action<int, GloveKinematicFrame> KinematicFrameReceived;
        public event Action<int, Quaternion> OrientationReceived;

        public bool IsReceiving
        {
            get
            {
                lock (_lock) { return !_bindFailed && _running && _lastPacketTime >= 0f; }
            }
        }

        public float PacketsPerSecond { get; private set; }

        void OnEnable()
        {
            StartReceiving();
        }

        void OnDisable()
        {
            StopReceiving();
        }

        void Update()
        {
            if (logDiagnostics && Time.realtimeSinceStartup - _lastLogTime >= 5f)
            {
                _lastLogTime = Time.realtimeSinceStartup;
                bool threadAlive = _receiveThread != null && _receiveThread.IsAlive;
                string hands = string.Empty;
                lock (_lock)
                {
                    foreach (var kvp in _latestFrames)
                    {
                        hands += (hands.Length > 0 ? ", " : string.Empty) +
                                 $"hand={kvp.Key} joints={kvp.Value.positions?.Length ?? 0}";
                    }
                }
                Debug.Log($"[MocapOSC] diag: datagrams={Interlocked.Read(ref _datagramsReceived)} running={_running} threadAlive={threadAlive} bound={_client != null} frames=[{hands}] kin={Interlocked.Read(ref _countKinematic)} kinBranch={Interlocked.Read(ref _countKinBranch)} orient={Interlocked.Read(ref _countOrientation)} other={Interlocked.Read(ref _countOtherAddress)} badtag={Interlocked.Read(ref _countBadTag)} badhdr={Interlocked.Read(ref _countBadHeader)} failFloat={Interlocked.Read(ref _countFailFloat)} firstBig={_firstBigLen}:{_firstBigAddress}:{_firstBigTag?.Length}");
            }

            if (logStatus && IsReceiving && Time.realtimeSinceStartup - _lastLogTime >= 5f)
            {
                float elapsed = Time.realtimeSinceStartup - _lastLogTime;
                if (elapsed > 0.001f)
                {
                    PacketsPerSecond = _packetsSinceLog / elapsed;
                }
                _packetsSinceLog = 0;
                _lastLogTime = Time.realtimeSinceStartup;
                string hands = string.Empty;
                lock (_lock)
                {
                    foreach (var kvp in _latestFrames)
                    {
                        hands += (hands.Length > 0 ? ", " : string.Empty) + kvp.Key;
                    }
                }
                Debug.Log($"[MocapOSC] Receiving {PacketsPerSecond:F0} pkt/s, hands=[{hands}]");
            }

            List<int> pendingKinematic = null;
            List<int> pendingOrientation = null;
            lock (_lock)
            {
                if (_pendingKinematicHands.Count > 0)
                {
                    pendingKinematic = new List<int>(_pendingKinematicHands);
                    _pendingKinematicHands.Clear();
                }
                if (_pendingOrientationHands.Count > 0)
                {
                    pendingOrientation = new List<int>(_pendingOrientationHands);
                    _pendingOrientationHands.Clear();
                }
            }

            if (pendingKinematic != null && KinematicFrameReceived != null)
            {
                for (int i = 0; i < pendingKinematic.Count; i++)
                {
                    int hand = pendingKinematic[i];
                    GloveKinematicFrame frame;
                    lock (_lock)
                    {
                        if (!_latestFrames.TryGetValue(hand, out frame))
                        {
                            continue;
                        }
                    }
                    KinematicFrameReceived.Invoke(hand, frame);
                }
            }

            if (pendingOrientation != null && OrientationReceived != null)
            {
                for (int i = 0; i < pendingOrientation.Count; i++)
                {
                    int hand = pendingOrientation[i];
                    Quaternion orientation;
                    lock (_lock)
                    {
                        if (!_orientations.TryGetValue(hand, out orientation))
                        {
                            continue;
                        }
                    }
                    OrientationReceived.Invoke(hand, orientation);
                }
            }
        }

        public bool TryGetLatestFrame(int handedness, out GloveKinematicFrame frame)
        {
            lock (_lock)
            {
                return _latestFrames.TryGetValue(handedness, out frame);
            }
        }

        public bool TryGetOrientation(int handedness, out Quaternion orientation)
        {
            lock (_lock)
            {
                return _orientations.TryGetValue(handedness, out orientation);
            }
        }

        public bool TryGetAcceleration(int handedness, out Vector3 acceleration)
        {
            lock (_lock)
            {
                return _accelerations.TryGetValue(handedness, out acceleration);
            }
        }

        public string GetSerial(int handedness)
        {
            lock (_lock)
            {
                _serials.TryGetValue(handedness, out string serial);
                return serial;
            }
        }

        void StartReceiving()
        {
            if (_running)
            {
                return;
            }

            _bindFailed = false;
            try
            {
                _client = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
                _client.Client.ReceiveTimeout = 250;
            }
            catch (SocketException ex)
            {
                _bindFailed = true;
                if (!_loggedBind)
                {
                    _loggedBind = true;
                    Debug.LogWarning($"[MocapOSC] Failed to bind UDP port {port}: {ex.Message}. " +
                                     "The StretchSense OpenXR API layer may be loaded in this process and holding the port.");
                }
                return;
            }

            _running = true;
            _clock.Restart();
            Debug.Log("[MocapOSC] Listening on UDP port " + port);
            _receiveThread = new Thread(ReceiveLoop)
            {
                IsBackground = true,
                Name = "MocapOscReceiver"
            };
            _receiveThread.Start();
        }

        void StopReceiving()
        {
            _running = false;
            try
            {
                _client?.Dispose();
            }
            catch (Exception)
            {
            }
            _client = null;

            if (_receiveThread != null)
            {
                if (!_receiveThread.Join(500))
                {
                    try
                    {
                        _receiveThread.Abort();
                    }
                    catch (Exception)
                    {
                    }
                }
                _receiveThread = null;
            }

            lock (_lock)
            {
                _latestFrames.Clear();
                _orientations.Clear();
                _accelerations.Clear();
                _pendingKinematicHands.Clear();
                _pendingOrientationHands.Clear();
                _lastPacketTime = float.NegativeInfinity;
            }
        }

        void ReceiveLoop()
        {
            IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            Vector3[] positions = new Vector3[GloveKinematicFrame.JointCount];
            Quaternion[] rotations = new Quaternion[GloveKinematicFrame.JointCount];
            Vector3[] positionsSwap = new Vector3[GloveKinematicFrame.JointCount];
            Quaternion[] rotationsSwap = new Quaternion[GloveKinematicFrame.JointCount];

            while (_running)
            {
                byte[] data;
                try
                {
                    data = _client.Receive(ref remote);
                }
                catch (SocketException)
                {
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                if (!_loggedFirstDatagram)
                {
                    _loggedFirstDatagram = true;
                    Debug.Log("[MocapOSC] First datagram received: " + data.Length + " bytes.");
                }
                Interlocked.Increment(ref _datagramsReceived);

                if (data.Length > 900 && _firstBigAddress == null)
                {
                    int aEnd = Array.IndexOf(data, (byte)0, 0);
                    int tStart = (aEnd + 1 + 3) & ~3;
                    int tEnd = Array.IndexOf(data, (byte)0, tStart);
                    _firstBigLen = data.Length;
                    _firstBigAddress = aEnd > 0 ? Encoding.UTF8.GetString(data, 0, aEnd) : "?";
                    _firstBigTag = tEnd > tStart ? Encoding.UTF8.GetString(data, tStart, tEnd - tStart) : "?";
                }

                try
                {
                    ProcessPacket(data, positions, rotations, positionsSwap, rotationsSwap);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[MocapOSC] Receive-loop exception: " + ex);
                }
            }
        }

        void ProcessPacket(byte[] data, Vector3[] positions, Quaternion[] rotations,
            Vector3[] positionsSwap, Quaternion[] rotationsSwap)
        {
            int offset = 0;
            if (!TryReadOscString(data, ref offset, out string address))
            {
                return;
            }

            if (!TryReadOscString(data, ref offset, out string tag))
            {
                return;
            }

            if (!TryReadInt32(data, ref offset, out _) ||
                !TryReadInt32(data, ref offset, out _) ||
                !TryReadInt32(data, ref offset, out int handedness) ||
                !TryReadOscString(data, ref offset, out string serial) ||
                !TryReadOscString(data, ref offset, out _))
            {
                Interlocked.Increment(ref _countBadHeader);
                return;
            }

            if (address == KinematicAddress)
            {
                Interlocked.Increment(ref _countKinBranch);
                if (tag.Length != 6 + GloveKinematicFrame.JointCount * 7)
                {
                    Interlocked.Increment(ref _countBadTag);
                    return;
                }

                for (int i = 0; i < GloveKinematicFrame.JointCount; i++)
                {
                    if (!TryReadFloat(data, ref offset, out float x) ||
                        !TryReadFloat(data, ref offset, out float y) ||
                        !TryReadFloat(data, ref offset, out float z) ||
                        !TryReadFloat(data, ref offset, out float qx) ||
                        !TryReadFloat(data, ref offset, out float qy) ||
                        !TryReadFloat(data, ref offset, out float qz) ||
                        !TryReadFloat(data, ref offset, out float qw))
                    {
                        Interlocked.Increment(ref _countFailFloat);
                        return;
                    }
                    positionsSwap[i] = new Vector3(x, y, z);
                    rotationsSwap[i] = new Quaternion(qx, qy, qz, qw);
                }

                (positionsSwap, positions) = (positions, positionsSwap);
                (rotationsSwap, rotations) = (rotations, rotationsSwap);

                lock (_lock)
                {
                    _lastPacketTime = (float)_clock.Elapsed.TotalSeconds;
                    _packetsSinceLog++;
                    _serials[handedness] = serial;
                    _latestFrames[handedness] = new GloveKinematicFrame
                    {
                        handedness = handedness,
                        serial = serial,
                        positions = positions,
                        rotations = rotations,
                        receivedTime = (float)_clock.Elapsed.TotalSeconds
                    };
                    _pendingKinematicHands.Add(handedness);
                    Interlocked.Increment(ref _countKinematic);
                    if (!_loggedFirstPacket)
                    {
                        _loggedFirstPacket = true;
                        Debug.Log($"[MocapOSC] First kinematic packet: hand={handedness} serial='{serial}' ({GloveKinematicFrame.JointCount} joints).");
                    }
                }
            }
            else if (address == OrientationAddress && tag == ",iiissfffffff")
            {
                Interlocked.Increment(ref _countOrientation);
                if (!TryReadFloat(data, ref offset, out float ax) ||
                    !TryReadFloat(data, ref offset, out float ay) ||
                    !TryReadFloat(data, ref offset, out float az) ||
                    !TryReadFloat(data, ref offset, out float qx) ||
                    !TryReadFloat(data, ref offset, out float qy) ||
                    !TryReadFloat(data, ref offset, out float qz) ||
                    !TryReadFloat(data, ref offset, out float qw))
                {
                    return;
                }

                lock (_lock)
                {
                    _lastPacketTime = (float)_clock.Elapsed.TotalSeconds;
                    _packetsSinceLog++;
                    _serials[handedness] = serial;
                    _accelerations[handedness] = new Vector3(ax, ay, az);
                    _orientations[handedness] = new Quaternion(qx, qy, qz, qw);
                    _pendingOrientationHands.Add(handedness);
                }
            }
            else
            {
                Interlocked.Increment(ref _countOtherAddress);
            }
        }

        static bool TryReadOscString(byte[] data, ref int offset, out string value)
        {
            value = null;
            if (offset >= data.Length)
            {
                return false;
            }

            int start = offset;
            int end = Array.IndexOf(data, (byte)0, offset);
            if (end < 0)
            {
                return false;
            }

            value = Encoding.UTF8.GetString(data, start, end - start);
            offset = (end + 1 + 3) & ~3;
            return offset <= data.Length;
        }

        static bool TryReadInt32(byte[] data, ref int offset, out int value)
        {
            value = 0;
            if (offset + 4 > data.Length)
            {
                return false;
            }

            value = (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
            offset += 4;
            return true;
        }

        bool TryReadFloat(byte[] data, ref int offset, out float value)
        {
            value = 0f;
            if (offset + 4 > data.Length)
            {
                return false;
            }

            _floatBytes[0] = data[offset + 3];
            _floatBytes[1] = data[offset + 2];
            _floatBytes[2] = data[offset + 1];
            _floatBytes[3] = data[offset];
            value = BitConverter.ToSingle(_floatBytes, 0);
            offset += 4;
            return true;
        }
    }
}
