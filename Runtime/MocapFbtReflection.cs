using System.Reflection;
using UnityEngine;

namespace MocapTools
{
    /// <summary>
    /// Reflective access to Final IK VRIK. The mocap package deliberately avoids
    /// a direct dependency on the FinalIK assembly, so all solver configuration
    /// goes through these helpers.
    /// </summary>
    public static class MocapFbtReflection
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>
        /// Finds a VRIK (or FullBodyBipedIK) component anywhere under the root.
        /// </summary>
        public static Component FindVrik(Transform root)
        {
            if (root == null) return null;
            var components = root.GetComponentsInChildren<MonoBehaviour>();
            foreach (var comp in components)
            {
                if (comp == null) continue;
                string typeName = comp.GetType().Name;
                if (typeName == "VRIK" || typeName == "FullBodyBipedIK")
                    return comp;
            }
            return null;
        }

        public static object GetSolver(Component vrik)
        {
            return vrik == null ? null : GetMember(vrik, "solver");
        }

        public static object GetMember(object target, string memberName)
        {
            if (target == null) return null;
            var type = target.GetType();

            var field = type.GetField(memberName, Flags);
            if (field != null)
                return field.GetValue(target);

            var property = type.GetProperty(memberName, Flags);
            return property != null && property.CanRead ? property.GetValue(target) : null;
        }

        public static bool SetMember(object target, string memberName, object value)
        {
            if (target == null) return false;
            var type = target.GetType();

            var field = type.GetField(memberName, Flags);
            if (field != null)
            {
                field.SetValue(target, value);
                return true;
            }

            var property = type.GetProperty(memberName, Flags);
            if (property == null || !property.CanWrite)
                return false;

            property.SetValue(target, value);
            return true;
        }

        /// <summary>
        /// Sets a value at a dotted path, e.g. "spine.minHeadHeight".
        /// Returns false if any segment cannot be resolved.
        /// </summary>
        public static bool SetPath(object root, string path, object value)
        {
            if (root == null) return false;

            string[] parts = path.Split('.');
            object current = root;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                current = GetMember(current, parts[i]);
                if (current == null) return false;
            }

            return SetMember(current, parts[parts.Length - 1], value);
        }

        /// <summary>
        /// Assigns one solver part target (e.g. spine.headTarget) to a transform.
        /// </summary>
        public static bool SetTarget(object solver, string partName, string targetMember, Transform value)
        {
            if (solver == null) return false;
            object part = GetMember(solver, partName);
            if (part == null) return false;
            return SetMember(part, targetMember, value);
        }

        /// <summary>
        /// Assigns all VRIK targets from a calibration profile. Missing bindings
        /// are skipped so optional trackers (elbows) never clear existing targets.
        /// </summary>
        public static int AssignTargets(Component vrik, MocapFbtCalibrationProfile profile)
        {
            if (vrik == null || profile == null) return 0;
            object solver = GetSolver(vrik);
            if (solver == null) return 0;

            int assigned = 0;
            foreach (var binding in profile.bindings)
            {
                if (!binding.isValid || binding.offsetTransform == null)
                    continue;

                bool ok;
                switch (binding.region)
                {
                    case MocapFbtRegion.Head:
                        ok = SetTarget(solver, "spine", "headTarget", binding.offsetTransform);
                        break;
                    case MocapFbtRegion.Hips:
                        ok = SetTarget(solver, "spine", "pelvisTarget", binding.offsetTransform);
                        break;
                    case MocapFbtRegion.LeftHand:
                        ok = SetTarget(solver, "leftArm", "target", binding.offsetTransform);
                        break;
                    case MocapFbtRegion.RightHand:
                        ok = SetTarget(solver, "rightArm", "target", binding.offsetTransform);
                        break;
                    case MocapFbtRegion.LeftFoot:
                        ok = SetTarget(solver, "leftLeg", "target", binding.offsetTransform);
                        break;
                    case MocapFbtRegion.RightFoot:
                        ok = SetTarget(solver, "rightLeg", "target", binding.offsetTransform);
                        break;
                    case MocapFbtRegion.LeftElbow:
                        ok = SetTarget(solver, "leftArm", "bendGoal", binding.offsetTransform);
                        break;
                    case MocapFbtRegion.RightElbow:
                        ok = SetTarget(solver, "rightArm", "bendGoal", binding.offsetTransform);
                        break;
                    default:
                        continue;
                }

                if (ok) assigned++;
            }

            return assigned;
        }

        /// <summary>
        /// Applies the full-tracker VRIK policy for the VRChat-style setup:
        /// locomotion off, plantFeet off, no head-height clamp, unrestricted root
        /// angle, and full pelvis/arm/leg weights. Lock Head is used for the
        /// spine (strict HMD head pin; pelvis may drift).
        /// </summary>
        public static void ApplyFullTrackingPolicy(Component vrik)
        {
            object solver = GetSolver(vrik);
            if (solver == null)
            {
                Debug.LogWarning("[MocapFbt] Could not access the VRIK solver to apply the tracking policy.");
                return;
            }

            SetPath(solver, "plantFeet", false);

            // Lock Head: strict head tracking, pelvis may drift to keep the spine sane.
            SetPath(solver, "spine.minHeadHeight", 0f);
            SetPath(solver, "spine.maxRootAngle", 180f);
            SetPath(solver, "spine.headClampWeight", 0.5f);
            SetPath(solver, "spine.positionWeight", 1f);
            SetPath(solver, "spine.rotationWeight", 1f);
            SetPath(solver, "spine.pelvisPositionWeight", 1f);
            SetPath(solver, "spine.pelvisRotationWeight", 1f);

            // Physical locomotion comes from the tracked pelvis (MocapFbtSolver),
            // not from Final IK's procedural stepping.
            SetPath(solver, "locomotion.weight", 0f);

            SetPath(solver, "leftArm.positionWeight", 1f);
            SetPath(solver, "leftArm.rotationWeight", 1f);
            SetPath(solver, "leftArm.bendGoalWeight", 0.5f);
            SetPath(solver, "rightArm.positionWeight", 1f);
            SetPath(solver, "rightArm.rotationWeight", 1f);
            SetPath(solver, "rightArm.bendGoalWeight", 0.5f);

            SetPath(solver, "leftLeg.positionWeight", 1f);
            SetPath(solver, "leftLeg.rotationWeight", 1f);
            SetPath(solver, "rightLeg.positionWeight", 1f);
            SetPath(solver, "rightLeg.rotationWeight", 1f);

            Debug.Log("[MocapFbt] VRIK full-tracking policy applied (Lock Head, locomotion off).");
        }

        /// <summary>
        /// Zeroes solver weights for bindings that have no live tracker, so
        /// missing hands/feet/elbows leave those limbs at their rest pose instead
        /// of flailing against a null target. Call AFTER ApplyFullTrackingPolicy.
        /// </summary>
        public static void ApplyMissingBindingWeights(Component vrik, MocapFbtCalibrationProfile profile)
        {
            object solver = GetSolver(vrik);
            if (solver == null || profile == null) return;

            if (profile.GetBinding(MocapFbtRegion.LeftHand)?.isValid != true)
            {
                SetPath(solver, "leftArm.positionWeight", 0f);
                SetPath(solver, "leftArm.rotationWeight", 0f);
            }

            if (profile.GetBinding(MocapFbtRegion.RightHand)?.isValid != true)
            {
                SetPath(solver, "rightArm.positionWeight", 0f);
                SetPath(solver, "rightArm.rotationWeight", 0f);
            }

            if (profile.GetBinding(MocapFbtRegion.LeftElbow)?.isValid != true)
                SetPath(solver, "leftArm.bendGoalWeight", 0f);

            if (profile.GetBinding(MocapFbtRegion.RightElbow)?.isValid != true)
                SetPath(solver, "rightArm.bendGoalWeight", 0f);

            if (profile.GetBinding(MocapFbtRegion.LeftFoot)?.isValid != true)
            {
                SetPath(solver, "leftLeg.positionWeight", 0f);
                SetPath(solver, "leftLeg.rotationWeight", 0f);
            }

            if (profile.GetBinding(MocapFbtRegion.RightFoot)?.isValid != true)
            {
                SetPath(solver, "rightLeg.positionWeight", 0f);
                SetPath(solver, "rightLeg.rotationWeight", 0f);
            }
        }
    }
}
