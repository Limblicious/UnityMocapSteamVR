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
    /// Latest per-bone pose received over VMC (/VMC/Ext/Bone/Pos) from StretchSense
    /// XR Game. Position and rotation are expressed in the glove-local hand space.
    /// </summary>
    [Serializable]
    public struct VmcBonePose
    {
        public Vector3 position;
        public Quaternion rotation;
        public float receivedTime;
    }

    /// <summary>
    /// Background-thread OSC receiver for the StretchSense XR Game VMC bone stream.
    ///
    /// XR Game sends /VMC/Ext/Bone/Pos packets (signature ",sfffffff" - bone name,
    /// position xyz, rotation xyzw) to UDP loopback port 39540 whenever VMC Streaming
    /// is enabled in the app. This component parses those packets off the main thread
    /// and exposes the latest pose per finger bone via <see cref="TryGetBonePose"/>,
    /// <see cref="GetBoneSnapshot"/> and the <see cref="BonePoseReceived"/> event.
    /// </summary>
    public class MocapVmcReceiver : MonoBehaviour
    {
        public const string BonePoseAddress = "/VMC/Ext/Bone/Pos";
        public const string TagSignature = ",sfffffff";
        /// <summary>
        /// Unity listens on 19540, the relay's forward port. XR Game streams VMC to
        /// 39540, which must NOT be bound inside the Unity process. The standalone
        /// MocapOscRelay bridges 39540 -> 19540. </summary>
        public const int DefaultPort = 19540;

        [Tooltip("UDP port XR Game streams VMC bone data to.")]
        public int port = DefaultPort;

        [Tooltip("Log a periodic packet-rate status line while receiving.")]
        public bool logStatus = true;

        readonly object _lock = new object();
        readonly Dictionary<string, VmcBonePose> _bones = new Dictionary<string, VmcBonePose>();
        readonly List<KeyValuePair<string, VmcBonePose>> _pendingEvents = new List<KeyValuePair<string, VmcBonePose>>();

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
        bool _loggedParseFailure;
        bool _loggedFirstDatagram;

        public event Action<string, VmcBonePose> BonePoseReceived;

        public int BoneCount { get { lock (_lock) { return _bones.Count; } } }

        public float PacketsPerSecond { get; private set; }

        public bool IsReceiving
        {
            get
            {
                lock (_lock) { return !_bindFailed && _running && _lastPacketTime >= 0f; }
            }
        }

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
            if (logStatus && IsReceiving && Time.realtimeSinceStartup - _lastLogTime >= 5f)
            {
                float elapsed = Time.realtimeSinceStartup - _lastLogTime;
                if (elapsed > 0.001f)
                {
                    PacketsPerSecond = _packetsSinceLog / elapsed;
                }
                _packetsSinceLog = 0;
                _lastLogTime = Time.realtimeSinceStartup;
                Debug.Log($"[MocapVMC] Receiving {PacketsPerSecond:F0} pkt/s, {BoneCount} bones tracked");
            }

            List<KeyValuePair<string, VmcBonePose>> pending = null;
            lock (_lock)
            {
                if (_pendingEvents.Count > 0)
                {
                    pending = new List<KeyValuePair<string, VmcBonePose>>(_pendingEvents);
                    _pendingEvents.Clear();
                }
            }

            if (pending == null || BonePoseReceived == null)
            {
                return;
            }

            for (int i = 0; i < pending.Count; i++)
            {
                BonePoseReceived.Invoke(pending[i].Key, pending[i].Value);
            }
        }

        public bool TryGetBonePose(string boneName, out VmcBonePose pose)
        {
            lock (_lock)
            {
                return _bones.TryGetValue(boneName, out pose);
            }
        }

        public Dictionary<string, VmcBonePose> GetBoneSnapshot()
        {
            lock (_lock)
            {
                return new Dictionary<string, VmcBonePose>(_bones);
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
                Debug.LogWarning($"[MocapVMC] Failed to bind UDP port {port}: {ex.Message}");
                return;
            }

            _running = true;
            _clock.Restart();
            Debug.Log("[MocapVMC] Listening on UDP port " + port);
            _receiveThread = new Thread(ReceiveLoop)
            {
                IsBackground = true,
                Name = "MocapVmcReceiver"
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
                _bones.Clear();
                _pendingEvents.Clear();
                _lastPacketTime = float.NegativeInfinity;
            }
        }

        void ReceiveLoop()
        {
            IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);

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

                try
                {
                    if (!_loggedFirstDatagram)
                    {
                        _loggedFirstDatagram = true;
                        Debug.Log("[MocapVMC] First datagram received: " + data.Length + " bytes.");
                    }

                    if (!TryParseBonePose(data, out string bone, out VmcBonePose pose))
                    {
                        if (!_loggedParseFailure)
                        {
                            _loggedParseFailure = true;
                            Debug.LogWarning("[MocapVMC] Packet rejected by parser (" + data.Length + " bytes).");
                        }
                        continue;
                    }

                    lock (_lock)
                    {
                        _lastPacketTime = pose.receivedTime;
                        _packetsSinceLog++;
                        _bones[bone] = pose;
                        _pendingEvents.Add(new KeyValuePair<string, VmcBonePose>(bone, pose));
                        if (!_loggedFirstPacket)
                        {
                            _loggedFirstPacket = true;
                            Debug.Log("[MocapVMC] First packet received: " + bone + " (" + _bones.Count + " bones so far).");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[MocapVMC] Receive-loop exception: " + ex);
                }
            }
        }

        bool TryParseBonePose(byte[] data, out string bone, out VmcBonePose pose)
        {
            bone = null;
            pose = default;

            int offset = 0;
            if (!TryReadOscString(data, ref offset, out string address) || address != BonePoseAddress)
            {
                return false;
            }

            if (!TryReadOscString(data, ref offset, out string tags) || tags != TagSignature)
            {
                return false;
            }

            if (!TryReadOscString(data, ref offset, out bone))
            {
                return false;
            }

            if (!TryReadFloat(data, ref offset, out float x) ||
                !TryReadFloat(data, ref offset, out float y) ||
                !TryReadFloat(data, ref offset, out float z) ||
                !TryReadFloat(data, ref offset, out float qx) ||
                !TryReadFloat(data, ref offset, out float qy) ||
                !TryReadFloat(data, ref offset, out float qz) ||
                !TryReadFloat(data, ref offset, out float qw))
            {
                bone = null;
                return false;
            }

            pose.position = new Vector3(x, y, z);
            pose.rotation = new Quaternion(qx, qy, qz, qw);
            pose.receivedTime = (float)_clock.Elapsed.TotalSeconds;
            return true;
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