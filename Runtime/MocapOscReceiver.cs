using System;
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
        const int ReceiveBufferSize = 65535;

        static readonly byte[] KinematicAddressBytes = Encoding.ASCII.GetBytes(KinematicAddress);
        static readonly byte[] OrientationAddressBytes = Encoding.ASCII.GetBytes(OrientationAddress);
        static readonly byte[] KinematicTagBytes = Encoding.ASCII.GetBytes(
            ",iiiss" + new string('f', GloveKinematicFrame.JointCount * 7));
        static readonly byte[] OrientationTagBytes = Encoding.ASCII.GetBytes(",iiissfffffff");

        [Tooltip("UDP port the Reality Core Driver streams OSC to.")]
        public int port = DefaultPort;

        [Tooltip("Log a periodic packet-rate status line while receiving.")]
        public bool logStatus = false;

        sealed class FrameBuffer
        {
            public readonly Vector3[] positions = new Vector3[GloveKinematicFrame.JointCount];
            public readonly Quaternion[] rotations = new Quaternion[GloveKinematicFrame.JointCount];
            public float receivedTime;
        }

        readonly object _lock = new object();
        readonly FrameBuffer[] _writeFrames = CreateFrameBuffers();
        readonly FrameBuffer[] _pendingFrames = CreateFrameBuffers();
        readonly FrameBuffer[] _mainThreadFrames = CreateFrameBuffers();
        readonly GloveKinematicFrame[] _latestFrames = new GloveKinematicFrame[3];
        readonly Quaternion[] _orientations = new Quaternion[3];
        readonly Vector3[] _accelerations = new Vector3[3];
        readonly byte[] _floatBytes = new byte[4];
        readonly byte[] _receiveBuffer = new byte[ReceiveBufferSize];
        readonly Stopwatch _clock = new Stopwatch();
        Socket _socket;
        Thread _receiveThread;
        volatile bool _running;
        bool _bindFailed;
        long _packetsSinceLog;
        float _lastStatusLogTime = float.NegativeInfinity;
        float _lastPacketTime = float.NegativeInfinity;
        bool _loggedBind;
        bool _loggedFirstDatagram;
        bool _loggedFirstKinematic;
        int _firstDatagramLength;
        int _firstKinematicHand;
        int _pendingKinematicMask;
        int _pendingOrientationMask;
        int _validOrientationMask;
        long _parseErrors;

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
            int firstDatagramLength = Volatile.Read(ref _firstDatagramLength);
            if (!_loggedFirstDatagram && firstDatagramLength > 0)
            {
                _loggedFirstDatagram = true;
                Debug.Log("[MocapOSC] First datagram received: " + firstDatagramLength + " bytes.");
            }

            int firstKinematicHand = Volatile.Read(ref _firstKinematicHand);
            if (!_loggedFirstKinematic && firstKinematicHand != 0)
            {
                _loggedFirstKinematic = true;
                Debug.Log($"[MocapOSC] First kinematic packet: hand={firstKinematicHand} " +
                          $"({GloveKinematicFrame.JointCount} joints).");
            }

            GloveKinematicFrame leftFrame = default;
            GloveKinematicFrame rightFrame = default;
            Quaternion leftOrientation = default;
            Quaternion rightOrientation = default;
            bool dispatchLeftFrame = false;
            bool dispatchRightFrame = false;
            bool dispatchLeftOrientation = false;
            bool dispatchRightOrientation = false;

            lock (_lock)
            {
                if ((_pendingKinematicMask & (1 << LeftHand)) != 0)
                {
                    leftFrame = PromotePendingFrame(LeftHand);
                    dispatchLeftFrame = true;
                }
                if ((_pendingKinematicMask & (1 << RightHand)) != 0)
                {
                    rightFrame = PromotePendingFrame(RightHand);
                    dispatchRightFrame = true;
                }
                _pendingKinematicMask = 0;

                if ((_pendingOrientationMask & (1 << LeftHand)) != 0)
                {
                    leftOrientation = _orientations[LeftHand];
                    dispatchLeftOrientation = true;
                }
                if ((_pendingOrientationMask & (1 << RightHand)) != 0)
                {
                    rightOrientation = _orientations[RightHand];
                    dispatchRightOrientation = true;
                }
                _pendingOrientationMask = 0;
            }

            Action<int, GloveKinematicFrame> frameCallback = KinematicFrameReceived;
            if (frameCallback != null)
            {
                if (dispatchLeftFrame) frameCallback.Invoke(LeftHand, leftFrame);
                if (dispatchRightFrame) frameCallback.Invoke(RightHand, rightFrame);
            }

            Action<int, Quaternion> orientationCallback = OrientationReceived;
            if (orientationCallback != null)
            {
                if (dispatchLeftOrientation) orientationCallback.Invoke(LeftHand, leftOrientation);
                if (dispatchRightOrientation) orientationCallback.Invoke(RightHand, rightOrientation);
            }

            float now = Time.realtimeSinceStartup;
            if (logStatus && IsReceiving && now - _lastStatusLogTime >= 5f)
            {
                float elapsed = now - _lastStatusLogTime;
                if (elapsed > 0.001f)
                {
                    PacketsPerSecond = Interlocked.Exchange(ref _packetsSinceLog, 0L) / elapsed;
                }
                _lastStatusLogTime = now;
                bool hasLeft;
                bool hasRight;
                lock (_lock)
                {
                    hasLeft = _latestFrames[LeftHand].IsValid;
                    hasRight = _latestFrames[RightHand].IsValid;
                }
                Debug.Log($"[MocapOSC] Receiving {PacketsPerSecond:F0} pkt/s, " +
                          $"left={hasLeft}, right={hasRight}, parseErrors={Interlocked.Read(ref _parseErrors)}");
            }
        }

        GloveKinematicFrame PromotePendingFrame(int handedness)
        {
            FrameBuffer previousMainThreadFrame = _mainThreadFrames[handedness];
            _mainThreadFrames[handedness] = _pendingFrames[handedness];
            _pendingFrames[handedness] = previousMainThreadFrame;

            FrameBuffer promoted = _mainThreadFrames[handedness];
            GloveKinematicFrame frame = new GloveKinematicFrame
            {
                handedness = handedness,
                positions = promoted.positions,
                rotations = promoted.rotations,
                receivedTime = promoted.receivedTime
            };
            _latestFrames[handedness] = frame;
            return frame;
        }

        public bool TryGetLatestFrame(int handedness, out GloveKinematicFrame frame)
        {
            if (!IsValidHandedness(handedness))
            {
                frame = default;
                return false;
            }

            lock (_lock)
            {
                frame = _latestFrames[handedness];
                return frame.IsValid;
            }
        }

        public bool TryGetOrientation(int handedness, out Quaternion orientation)
        {
            if (!IsValidHandedness(handedness))
            {
                orientation = default;
                return false;
            }

            lock (_lock)
            {
                orientation = _orientations[handedness];
                return (_validOrientationMask & (1 << handedness)) != 0;
            }
        }

        public bool TryGetAcceleration(int handedness, out Vector3 acceleration)
        {
            if (!IsValidHandedness(handedness))
            {
                acceleration = default;
                return false;
            }

            lock (_lock)
            {
                acceleration = _accelerations[handedness];
                return (_validOrientationMask & (1 << handedness)) != 0;
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
                _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                _socket.ReceiveBufferSize = 1024 * 1024;
                _socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
            }
            catch (SocketException ex)
            {
                _bindFailed = true;
                _socket?.Dispose();
                _socket = null;
                if (!_loggedBind)
                {
                    _loggedBind = true;
                    Debug.LogWarning($"[MocapOSC] Failed to bind UDP port {port}: {ex.Message}. " +
                                     "The StretchSense OpenXR API layer may be loaded in this process and holding the port.");
                }
                return;
            }

            _firstDatagramLength = 0;
            _firstKinematicHand = 0;
            _loggedFirstDatagram = false;
            _loggedFirstKinematic = false;
            _lastStatusLogTime = Time.realtimeSinceStartup;
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
                _socket?.Dispose();
            }
            catch (Exception)
            {
            }

            if (_receiveThread != null)
            {
                if (!_receiveThread.Join(500))
                {
                    Debug.LogWarning("[MocapOSC] Receive thread did not stop within 500 ms.");
                }
                _receiveThread = null;
            }
            _socket = null;

            lock (_lock)
            {
                Array.Clear(_latestFrames, 0, _latestFrames.Length);
                Array.Clear(_orientations, 0, _orientations.Length);
                Array.Clear(_accelerations, 0, _accelerations.Length);
                _pendingKinematicMask = 0;
                _pendingOrientationMask = 0;
                _validOrientationMask = 0;
                _lastPacketTime = float.NegativeInfinity;
            }
        }

        void ReceiveLoop()
        {
            Socket socket = _socket;

            while (_running)
            {
                int length;
                try
                {
                    length = socket.Receive(_receiveBuffer);
                }
                catch (SocketException)
                {
                    if (_running) Interlocked.Increment(ref _parseErrors);
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                Interlocked.CompareExchange(ref _firstDatagramLength, length, 0);

                try
                {
                    ProcessPacket(_receiveBuffer, length);
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref _parseErrors);
                }
            }
        }

        void ProcessPacket(byte[] data, int length)
        {
            int offset = 0;
            if (!TryReadOscStringBounds(data, length, ref offset, out int addressStart, out int addressLength) ||
                !TryReadOscStringBounds(data, length, ref offset, out int tagStart, out int tagLength))
            {
                return;
            }

            bool isKinematic = OscStringEquals(data, addressStart, addressLength, KinematicAddressBytes);
            bool isOrientation = OscStringEquals(data, addressStart, addressLength, OrientationAddressBytes);
            if (!isKinematic && !isOrientation)
            {
                return;
            }

            byte[] expectedTag = isKinematic ? KinematicTagBytes : OrientationTagBytes;
            if (!OscStringEquals(data, tagStart, tagLength, expectedTag))
            {
                return;
            }

            if (!TryReadInt32(data, length, ref offset, out _) ||
                !TryReadInt32(data, length, ref offset, out _) ||
                !TryReadInt32(data, length, ref offset, out int handedness) ||
                !TryReadOscStringBounds(data, length, ref offset, out _, out _) ||
                !TryReadOscStringBounds(data, length, ref offset, out _, out _) ||
                !IsValidHandedness(handedness))
            {
                return;
            }

            if (isKinematic)
            {
                FrameBuffer writeFrame = _writeFrames[handedness];

                for (int i = 0; i < GloveKinematicFrame.JointCount; i++)
                {
                    if (!TryReadFloat(data, length, ref offset, out float x) ||
                        !TryReadFloat(data, length, ref offset, out float y) ||
                        !TryReadFloat(data, length, ref offset, out float z) ||
                        !TryReadFloat(data, length, ref offset, out float qx) ||
                        !TryReadFloat(data, length, ref offset, out float qy) ||
                        !TryReadFloat(data, length, ref offset, out float qz) ||
                        !TryReadFloat(data, length, ref offset, out float qw))
                    {
                        return;
                    }
                    writeFrame.positions[i] = new Vector3(x, y, z);
                    writeFrame.rotations[i] = new Quaternion(qx, qy, qz, qw);
                }

                float receivedTime = (float)_clock.Elapsed.TotalSeconds;
                writeFrame.receivedTime = receivedTime;

                lock (_lock)
                {
                    FrameBuffer previousPending = _pendingFrames[handedness];
                    _pendingFrames[handedness] = writeFrame;
                    _writeFrames[handedness] = previousPending;
                    _pendingKinematicMask |= 1 << handedness;
                    _lastPacketTime = receivedTime;
                }
                Interlocked.Increment(ref _packetsSinceLog);
                Interlocked.CompareExchange(ref _firstKinematicHand, handedness, 0);
            }
            else
            {
                if (!TryReadFloat(data, length, ref offset, out float ax) ||
                    !TryReadFloat(data, length, ref offset, out float ay) ||
                    !TryReadFloat(data, length, ref offset, out float az) ||
                    !TryReadFloat(data, length, ref offset, out float qx) ||
                    !TryReadFloat(data, length, ref offset, out float qy) ||
                    !TryReadFloat(data, length, ref offset, out float qz) ||
                    !TryReadFloat(data, length, ref offset, out float qw))
                {
                    return;
                }

                float receivedTime = (float)_clock.Elapsed.TotalSeconds;
                lock (_lock)
                {
                    _accelerations[handedness] = new Vector3(ax, ay, az);
                    _orientations[handedness] = new Quaternion(qx, qy, qz, qw);
                    _pendingOrientationMask |= 1 << handedness;
                    _validOrientationMask |= 1 << handedness;
                    _lastPacketTime = receivedTime;
                }
                Interlocked.Increment(ref _packetsSinceLog);
            }
        }

        static bool TryReadOscStringBounds(byte[] data, int length, ref int offset, out int start, out int count)
        {
            start = offset;
            count = 0;
            if ((uint)offset >= (uint)length)
            {
                return false;
            }

            int end = offset;
            while (end < length && data[end] != 0)
            {
                end++;
            }

            if (end >= length) return false;

            count = end - start;
            offset = (end + 1 + 3) & ~3;
            return offset <= length;
        }

        static bool OscStringEquals(byte[] data, int start, int count, byte[] expected)
        {
            if (count != expected.Length) return false;
            for (int i = 0; i < count; i++)
            {
                if (data[start + i] != expected[i]) return false;
            }
            return true;
        }

        static bool TryReadInt32(byte[] data, int length, ref int offset, out int value)
        {
            value = 0;
            if (offset + 4 > length)
            {
                return false;
            }

            value = (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
            offset += 4;
            return true;
        }

        bool TryReadFloat(byte[] data, int length, ref int offset, out float value)
        {
            value = 0f;
            if (offset + 4 > length)
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

        static bool IsValidHandedness(int handedness)
        {
            return handedness == LeftHand || handedness == RightHand;
        }

        static FrameBuffer[] CreateFrameBuffers()
        {
            FrameBuffer[] buffers = new FrameBuffer[3];
            buffers[LeftHand] = new FrameBuffer();
            buffers[RightHand] = new FrameBuffer();
            return buffers;
        }
    }
}
