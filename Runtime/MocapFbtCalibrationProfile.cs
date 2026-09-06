using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace MocapTools
{
    /// <summary>
    /// Tracked body regions used by the VRChat-style full-body calibration system.
    /// </summary>
    public enum MocapFbtRegion
    {
        Head,
        Hips,
        LeftHand,
        RightHand,
        LeftFoot,
        RightFoot,
        LeftElbow,
        RightElbow
    }

    /// <summary>
    /// Region helpers shared by the FBT components.
    /// </summary>
    public static class MocapFbtRegionUtil
    {
        /// <summary>
        /// True for regions that support the "Adjust FBT" sphere-grab
        /// interaction (VRChat adjusts elbows, hip, and feet).
        /// </summary>
        public static bool IsAdjustable(MocapFbtRegion region)
        {
            return region == MocapFbtRegion.Hips ||
                   region == MocapFbtRegion.LeftFoot ||
                   region == MocapFbtRegion.RightFoot ||
                   region == MocapFbtRegion.LeftElbow ||
                   region == MocapFbtRegion.RightElbow;
        }
    }

    /// <summary>
    /// Binding between a tracked device and an avatar region. Offsets are stored
    /// in the tracker's local frame so they survive arbitrary tracker rotation.
    /// </summary>
    [Serializable]
    public class MocapFbtBinding
    {
        public MocapFbtRegion region;
        public string trackerName;
        public string offsetChildName;
        public Vector3 offsetPosition;
        public Vector3 offsetRotationEuler;
        public bool applyRotation = true;
        public bool positionOnly;

        [NonSerialized] public Transform trackerTransform;
        [NonSerialized] public Transform offsetTransform;
        [NonSerialized] public Transform boneTransform;
        [NonSerialized] public bool isValid;

        public Quaternion OffsetRotation => Quaternion.Euler(offsetRotationEuler);

        /// <summary>
        /// World-space position the calibrated target should occupy for this binding.
        /// </summary>
        public Vector3 TargetPosition =>
            trackerTransform == null
                ? Vector3.zero
                : trackerTransform.position + trackerTransform.rotation * offsetPosition;
    }

    /// <summary>
    /// Serializable calibration state for the VRChat-style full-body system.
    /// Persisted as JSON (VRChat IK 2.0 "calibration saving" analog) so a
    /// previously calibrated profile can be re-applied without recalibrating.
    /// </summary>
    [Serializable]
    public class MocapFbtCalibrationProfile
    {
        public const int LockHip = 0;
        public const int LockHead = 1;
        public const int LockBoth = 2;

        public int version = 1;
        public string characterName = "";
        public float uniformScale = 1f;
        public float yawBias = 0f;
        public float floorY = 0f;
        public Vector3 rootLocalPelvisOffset = Vector3.zero;
        public Vector3 headOffsetPosition = Vector3.zero;
        public Vector3 headOffsetRotationEuler = Vector3.zero;
        public int lockMode = LockHead;
        public List<MocapFbtBinding> bindings = new List<MocapFbtBinding>();

        public static string DefaultSavePath =>
            Path.Combine(Application.persistentDataPath, "mocap_fbt_profile.json");

        public static MocapFbtCalibrationProfile CreateDefaults()
        {
            var profile = new MocapFbtCalibrationProfile();
            profile.bindings.Add(NewBinding(MocapFbtRegion.Head, "Tracked_Head", "Head_Off", true, false));
            profile.bindings.Add(NewBinding(MocapFbtRegion.Hips, "Tracked_Hips", "Hips_Off", true, false));
            profile.bindings.Add(NewBinding(MocapFbtRegion.LeftHand, "Tracked_HandL", "HandL_Off", true, false));
            profile.bindings.Add(NewBinding(MocapFbtRegion.RightHand, "Tracked_HandR", "HandR_Off", true, false));
            profile.bindings.Add(NewBinding(MocapFbtRegion.LeftFoot, "Tracked_FootL", "FootL_Off", true, false));
            profile.bindings.Add(NewBinding(MocapFbtRegion.RightFoot, "Tracked_FootR", "FootR_Off", true, false));
            profile.bindings.Add(NewBinding(MocapFbtRegion.LeftElbow, "Tracked_ElbowL", "ElbowL_Off", false, true));
            profile.bindings.Add(NewBinding(MocapFbtRegion.RightElbow, "Tracked_ElbowR", "ElbowR_Off", false, true));
            return profile;
        }

        static MocapFbtBinding NewBinding(MocapFbtRegion region, string trackerName,
            string offsetName, bool applyRotation, bool positionOnly)
        {
            return new MocapFbtBinding
            {
                region = region,
                trackerName = trackerName,
                offsetChildName = offsetName,
                applyRotation = applyRotation,
                positionOnly = positionOnly
            };
        }

        public MocapFbtBinding GetBinding(MocapFbtRegion region)
        {
            foreach (var binding in bindings)
            {
                if (binding.region == region)
                    return binding;
            }
            return null;
        }

        public void Save(string path)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(path, JsonUtility.ToJson(this, true));
                Debug.Log($"[MocapFbt] Calibration profile saved: {path}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[MocapFbt] Failed to save calibration profile: {e.Message}");
            }
        }

        public static MocapFbtCalibrationProfile Load(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return null;
                var profile = JsonUtility.FromJson<MocapFbtCalibrationProfile>(File.ReadAllText(path));
                if (profile == null || profile.bindings == null || profile.bindings.Count == 0)
                    return null;
                return profile;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[MocapFbt] Failed to load calibration profile: {e.Message}");
                return null;
            }
        }
    }
}
