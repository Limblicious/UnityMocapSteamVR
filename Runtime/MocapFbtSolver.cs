using UnityEngine;

namespace MocapTools
{
    /// <summary>
    /// Per-frame full-body solver driver for the VRChat-style setup.
    ///
    /// Runs in LateUpdate with a negative execution order so it moves the
    /// character root before Final IK VRIK (execution order 0) reads and solves.
    ///
    /// Root locomotion follows the tracked pelvis horizontally and keeps the
    /// root Y pinned to the floor, which is the physical-locomotion analog of
    /// VRChat's tracked-hip FBT. The head stays pinned through the calibrated
    /// Head_Off offset child under the HMD tracker.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public class MocapFbtSolver : MonoBehaviour
    {
        [Header("References")]
        public Transform characterRoot;
        public Transform trackingRoot;

        [Header("Locomotion Settings")]
        [Tooltip("How fast the root moves toward the tracked pelvis (1/s).")]
        public float rootFollowSpeed = 12f;
        [Tooltip("How fast the root yaw aligns with the tracked pelvis (1/s).")]
        public float rootTurnSpeed = 10f;

        /// <summary>True while a calibration profile is applied and driving.</summary>
        public bool IsActive { get; private set; }

        MocapFbtCalibrationProfile _profile;
        Component _vrik;
        Transform _pelvisTarget;
        Transform _headTarget;

        /// <summary>
        /// Applies a calibration profile: resolves bindings, assigns VRIK targets,
        /// applies the full-tracking policy, and enables per-frame locomotion.
        /// </summary>
        public void ApplyProfile(MocapFbtCalibrationProfile profile)
        {
            _profile = profile;

            if (characterRoot == null)
            {
                Debug.LogError("[MocapFbt] Solver has no characterRoot.");
                IsActive = false;
                return;
            }

            _vrik = MocapFbtReflection.FindVrik(characterRoot);
            if (_vrik == null)
            {
                Debug.LogError("[MocapFbt] No VRIK component found under character root.");
                IsActive = false;
                return;
            }

            var pelvisBinding = profile.GetBinding(MocapFbtRegion.Hips);
            var headBinding = profile.GetBinding(MocapFbtRegion.Head);
            _pelvisTarget = ResolveTarget(pelvisBinding);
            _headTarget = ResolveTarget(headBinding);

            object solver = MocapFbtReflection.GetSolver(_vrik);
            if (solver != null)
            {
                MocapFbtReflection.SetTarget(solver, "spine", "headTarget", _headTarget);
                MocapFbtReflection.SetTarget(solver, "spine", "pelvisTarget", _pelvisTarget);
                MocapFbtReflection.SetTarget(solver, "leftArm", "target", ResolveTarget(profile.GetBinding(MocapFbtRegion.LeftHand)));
                MocapFbtReflection.SetTarget(solver, "rightArm", "target", ResolveTarget(profile.GetBinding(MocapFbtRegion.RightHand)));
                MocapFbtReflection.SetTarget(solver, "leftArm", "bendGoal", ResolveTarget(profile.GetBinding(MocapFbtRegion.LeftElbow)));
                MocapFbtReflection.SetTarget(solver, "rightArm", "bendGoal", ResolveTarget(profile.GetBinding(MocapFbtRegion.RightElbow)));
                MocapFbtReflection.SetTarget(solver, "leftLeg", "target", ResolveTarget(profile.GetBinding(MocapFbtRegion.LeftFoot)));
                MocapFbtReflection.SetTarget(solver, "rightLeg", "target", ResolveTarget(profile.GetBinding(MocapFbtRegion.RightFoot)));
            }

            MocapFbtReflection.ApplyFullTrackingPolicy(_vrik);
            MocapFbtReflection.ApplyMissingBindingWeights(_vrik, profile);

            // The FBT system owns VRIK after calibration, even if the scene had it disabled.
            ((Behaviour)_vrik).enabled = true;

            IsActive = true;
            Debug.Log("[MocapFbt] Solver profile applied; root locomotion active.");
        }

        public void SetActive(bool value)
        {
            IsActive = value && _vrik != null;
        }

        Transform ResolveTarget(MocapFbtBinding binding)
        {
            if (binding == null)
                return null;
            if (binding.offsetTransform != null)
                return binding.offsetTransform;
            return binding.trackerTransform;
        }

        void LateUpdate()
        {
            if (!IsActive || _pelvisTarget == null || characterRoot == null || _profile == null)
                return;

            // Horizontal root locomotion from the tracked pelvis; Y pinned to the floor.
            Vector3 desiredPos = characterRoot.position;
            desiredPos.x = _pelvisTarget.position.x;
            desiredPos.z = _pelvisTarget.position.z;
            desiredPos.y = _profile.floorY;

            // Root yaw follows the pelvis yaw, preserving the calibration yaw bias.
            float pelvisYaw = _pelvisTarget.rotation.eulerAngles.y;
            Quaternion desiredRot = Quaternion.Euler(0f, pelvisYaw + _profile.yawBias, 0f);

            float moveT = 1f - Mathf.Exp(-rootFollowSpeed * Time.deltaTime);
            float turnT = 1f - Mathf.Exp(-rootTurnSpeed * Time.deltaTime);

            characterRoot.position = Vector3.Lerp(characterRoot.position, desiredPos, moveT);
            characterRoot.rotation = Quaternion.Slerp(characterRoot.rotation, desiredRot, turnT);
        }
    }
}
