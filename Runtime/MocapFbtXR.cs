using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace MocapTools
{
    /// <summary>
    /// XR input helpers for the VRChat-style calibration flow: controller
    /// discovery, trigger state, and device pose. Works through the standard
    /// UnityEngine.XR InputDevice API so it does not depend on SteamVR.
    /// </summary>
    public static class MocapFbtXR
    {
        static readonly List<InputDevice> ControllerBuffer = new List<InputDevice>();

        /// <summary>
        /// Finds the left- or right-hand controller device.
        /// </summary>
        public static bool TryGetController(bool leftHand, out InputDevice device)
        {
            device = default;
            ControllerBuffer.Clear();
            InputDevices.GetDevicesWithCharacteristics(
                InputDeviceCharacteristics.Controller | InputDeviceCharacteristics.TrackedDevice,
                ControllerBuffer);

            foreach (var candidate in ControllerBuffer)
            {
                if (!candidate.isValid) continue;

                bool isLeft = (candidate.characteristics & InputDeviceCharacteristics.Left) != 0;
                if (isLeft == leftHand)
                {
                    device = candidate;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// True while the controller's trigger is pressed (button first, axis fallback).
        /// </summary>
        public static bool IsTriggerPressed(InputDevice device)
        {
            if (!device.isValid) return false;
            if (device.TryGetFeatureValue(CommonUsages.triggerButton, out bool pressed))
                return pressed;
            if (device.TryGetFeatureValue(CommonUsages.trigger, out float axis))
                return axis > 0.5f;
            return false;
        }

        /// <summary>
        /// Reads the device world pose.
        /// </summary>
        public static bool TryGetPose(InputDevice device, out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            if (!device.isValid) return false;
            return device.TryGetFeatureValue(CommonUsages.devicePosition, out position) &&
                   device.TryGetFeatureValue(CommonUsages.deviceRotation, out rotation);
        }
    }
}
