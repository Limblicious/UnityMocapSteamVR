using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem.XR;
using UnityEngine.XR;
using UnityEngine.XR.Hands;
using InputSystem = UnityEngine.InputSystem.InputSystem;

namespace MocapTools
{
    /// <summary>
    /// Play-Mode diagnostic for XR hand data (StretchSense Reality XR gloves via the
    /// SteamVR OpenXR runtime or the StretchSense OpenXR hand-tracking API layer).
    ///
    /// Reports every XRHandSubsystem hand joint with pose/state, plus a raw dump of
    /// XR input devices and InputSystem hand devices so the exact data route can be
    /// confirmed before any finger bridge is implemented. Console logging is opt-in
    /// via <see cref="logIntervalSeconds"/> (0 = off); a compact overlay is shown
    /// while <see cref="showOverlay"/> is on. <see cref="DumpNow"/> is callable from
    /// an MCP editor_eval or a menu during Play Mode.
    /// </summary>
    public class MocapHandDiagnostic : MonoBehaviour
    {
        [Tooltip("Show the status overlay during Play Mode.")]
        public bool showOverlay = true;

        [Tooltip("Seconds between console snapshots. 0 disables periodic logging.")]
        public float logIntervalSeconds = 0f;

        readonly List<XRHandSubsystem> _handSubsystems = new List<XRHandSubsystem>();
        readonly List<InputDevice> _rawDevices = new List<InputDevice>();

        static string s_LastSnapshot = "MocapHandDiagnostic not running.";
        float _nextLogTime;
        bool _dumpedRawDevices;
        string _status = "Waiting for XR...";

        void OnEnable()
        {
            _nextLogTime = Time.unscaledTime;
        }

        void Update()
        {
            SubsystemManager.GetSubsystems(_handSubsystems);

            var sb = new StringBuilder();
            bool anyRunning = false;

            if (_handSubsystems.Count == 0)
            {
                sb.AppendLine("No XRHandSubsystem found. (com.unity.xr.hands installed? Hand Tracking OpenXR feature enabled?)");
            }
            else
            {
                foreach (var subsystem in _handSubsystems)
                {
                    if (!subsystem.running)
                    {
                        sb.AppendLine("XRHandSubsystem present but not running.");
                        continue;
                    }

                    anyRunning = true;
                    AppendHand(subsystem.leftHand, "Left", sb);
                    AppendHand(subsystem.rightHand, "Right", sb);
                    sb.Append("updateSuccessFlags=").Append(subsystem.updateSuccessFlags).AppendLine();
                }
            }

            AppendInputSystemHandDevices(sb);

            if (!_dumpedRawDevices)
            {
                _dumpedRawDevices = true;
                DumpRawDevices();
            }

            _status = sb.ToString();
            s_LastSnapshot = _status;

            if (logIntervalSeconds > 0f && Time.unscaledTime >= _nextLogTime)
            {
                _nextLogTime = Time.unscaledTime + logIntervalSeconds;
                Debug.Log(anyRunning ? "[MocapHand] Hand data:\n" + _status : "[MocapHand] " + _status);
            }
        }

        static void AppendHand(XRHand hand, string side, StringBuilder sb)
        {
            sb.Append(side).Append(": isTracked=").Append(hand.isTracked);
            if (!hand.isTracked)
            {
                sb.AppendLine();
                return;
            }

            int validJoints = 0;
            foreach (XRHandJointID id in System.Enum.GetValues(typeof(XRHandJointID)))
            {
                if (id == XRHandJointID.BeginMarker || id == XRHandJointID.EndMarker) continue;
                XRHandJoint joint;
                try
                {
                    joint = hand.GetJoint(id);
                }
                catch (System.IndexOutOfRangeException)
                {
                    continue;
                }
                if (!joint.TryGetPose(out Pose pose)) continue;
                validJoints++;
                sb.Append("  ").Append(joint.id).Append(" state=").Append(joint.trackingState)
                  .Append(" pos=").Append(pose.position.ToString("F3"))
                  .Append(" euler=").Append(pose.rotation.eulerAngles.ToString("F1")).AppendLine();
            }

            sb.Append("  validJoints=").Append(validJoints).AppendLine();
        }

        static void AppendInputSystemHandDevices(StringBuilder sb)
        {
            int count = 0;
            foreach (var device in InputSystem.devices)
            {
                if (device is not XRHandDevice) continue;
                count++;
                sb.Append("XRHandDevice: ").Append(device.name)
                  .Append(" (layout=").Append(device.layout).AppendLine(")");
            }

            if (count == 0)
                sb.AppendLine("No XRHandDevice in InputSystem.");
        }

        void DumpRawDevices()
        {
            InputDevices.GetDevices(_rawDevices);
            Debug.Log("[MocapHand] Raw XR input devices (" + _rawDevices.Count + "):");
            foreach (var device in _rawDevices)
            {
                if (!device.isValid) continue;
                Debug.Log("  [" + device.characteristics + "] name=" + device.name +
                          " manufacturer=" + device.manufacturer +
                          " serial=" + device.serialNumber);
            }
        }

        [ContextMenu("Dump Hand Data")]
        public void DumpNow()
        {
            SubsystemManager.GetSubsystems(_handSubsystems);
            var sb = new StringBuilder();
            sb.AppendLine("[MocapHand] Manual snapshot:");
            if (_handSubsystems.Count == 0)
            {
                sb.AppendLine("  No XRHandSubsystem found.");
            }
            else
            {
                foreach (var subsystem in _handSubsystems)
                {
                    sb.Append("  running=").Append(subsystem.running).AppendLine();
                    if (!subsystem.running) continue;
                    AppendHand(subsystem.leftHand, "  Left", sb);
                    AppendHand(subsystem.rightHand, "  Right", sb);
                }
            }
            sb.AppendLine(_status);
            s_LastSnapshot = sb.ToString();
            Debug.Log(s_LastSnapshot);
        }

        /// <summary>
        /// Static entry point for MCP editor_eval / menus during Play Mode.
        /// Returns the latest snapshot text.
        /// </summary>
        public static string Snapshot()
        {
            Debug.Log("[MocapHand] Snapshot:\n" + s_LastSnapshot);
            return s_LastSnapshot;
        }

        void OnGUI()
        {
            if (!showOverlay) return;
            var width = Mathf.Min(640f, Screen.width - 20f);
            var height = Mathf.Min(420f, Screen.height - 20f);
            GUILayout.BeginArea(new Rect(10, 10, width, height), GUI.skin.box);
            GUILayout.Label("[MocapHandDiagnostic]  (0 = log off, see inspector logIntervalSeconds)");
            GUILayout.Label(_status);
            GUILayout.EndArea();
        }
    }
}
