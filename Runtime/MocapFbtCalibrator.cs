using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace MocapTools
{
    /// <summary>
    /// VRChat-style full-body calibration.
    ///
    /// Mirrors the documented VRChat flow
    /// (docs.vrchat.com/docs/full-body-tracking, "Using Full-Body Tracking in VRChat"):
    ///   - The avatar is pinned to the HMD while calibrating.
    ///   - Tracker spheres show where each tracker is.
    ///   - The performer stands straight, looks forward, and confirms with both triggers.
    ///
    /// Also implements the IK 2.0 documented concepts (docs.vrchat.com/docs/ik-20-features-and-options):
    ///   - Avatar measurement by height (uniform scale to the tracked eye height).
    ///   - Lock Head spine behavior (strict head pin, pelvis may drift).
    ///   - Calibration saving (JSON profile re-applied on later sessions).
    ///
    /// The "Adjust FBT" sphere-grab interaction is a screenshot-derived design:
    /// small spheres at calibrated targets, nearest-controller line, trigger grab/drag.
    /// </summary>
    [DefaultExecutionOrder(10)] // Pin the avatar after Final IK stores its defaults, before its LateUpdate solve.
    public class MocapFbtCalibrator : MonoBehaviour
    {
        public enum FbtState
        {
            Idle,
            Calibrating,
            Confirming,
            Adjusting,
            Active,
            Failed
        }

        [Header("References")]
        public Transform characterRoot;
        public Transform trackingRoot;
        [Tooltip("Optional explicit avatar viewpoint (between the eyes). Falls back to a Head-bone-derived eye position.")]
        public Transform avatarViewpoint;
        [Tooltip("The HMD tracker transform (Tracked_Head). Auto-resolved if empty.")]
        public Transform headTracker;
        public MocapFbtSolver solver;
        public MocapFbtUi ui;

        [Header("Settings")]
        public bool autoApplySavedProfile = true;
        [Tooltip("How long to sample after both triggers are pressed.")]
        public float confirmSampleSeconds = 0.5f;
        [Tooltip("Head-bone-local offset to the avatar viewpoint, used when avatarViewpoint is not assigned.")]
        public Vector3 eyeLocalOffset = new Vector3(0f, 0.0565f, 0.037f);
        public float minScale = 0.8f;
        public float maxScale = 1.4f;
        [Tooltip("Reach distance for grabbing adjustment spheres (m).")]
        public float adjustGrabRadius = 0.4f;

        public FbtState State { get; private set; } = FbtState.Idle;
        public string StatusMessage { get; private set; } = "Idle";

        public event System.Action<FbtState> OnStateChanged;
        public event System.Action<MocapFbtCalibrationProfile> OnCalibrationComplete;

        MocapFbtCalibrationProfile _profile;
        Component _vrik;
        bool _vrikWasEnabled;
        Animator _animator;
        bool _animatorWasEnabled;
        Transform _headBone;

        // Restore state
        Vector3 _originalLocalScale;
        bool _scaleCaptured;
        Vector3 _originalRootPos;
        Quaternion _originalRootRot;
        float _floorY;

        // Confirmation sampling
        float _sampleTimer;
        bool _confirmingFrozen;
        class SampleAccum
        {
            public readonly List<Vector3> TrackerPositions = new List<Vector3>();
            public readonly List<Quaternion> TrackerRotations = new List<Quaternion>();
            public readonly List<Vector3> BonePositions = new List<Vector3>();
            public readonly List<Quaternion> BoneRotations = new List<Quaternion>();
        }
        Dictionary<MocapFbtBinding, SampleAccum> _samples;

        // Adjustment grab state
        MocapFbtBinding _grabbedBinding;
        Vector3 _grabStartControllerPos;
        Vector3 _grabStartTargetWorld;
        bool _leftPressed;
        bool _rightPressed;

        #region Public API

        /// <summary>
        /// Enters calibration mode: disables locomotion, scales and pins the avatar
        /// to the HMD, and shows tracker spheres + mirror. Confirm with both triggers.
        /// </summary>
        public bool EnterCalibrationMode()
        {
            if (State == FbtState.Calibrating || State == FbtState.Confirming)
            {
                // VRChat behavior: re-clicking Calibrate resets the process.
                Debug.Log("[MocapFbt] Re-entering calibration mode; resetting previous calibration.");
                Cancel();
            }

            if (!Validate(out string error))
            {
                Fail(error);
                return false;
            }

            DeactivateSolver();
            CaptureOriginals();

            _profile = MocapFbtCalibrationProfile.Load(MocapFbtCalibrationProfile.DefaultSavePath)
                        ?? MocapFbtCalibrationProfile.CreateDefaults();
            _profile.characterName = characterRoot.name;

            // Disable VRIK and freeze the animator while re-anchoring the avatar.
            _vrik = MocapFbtReflection.FindVrik(characterRoot);
            if (_vrik != null)
            {
                _vrikWasEnabled = ((Behaviour)_vrik).enabled;
                ((Behaviour)_vrik).enabled = false;
            }

            ResolveBindings(_profile);

            // Uniform scale to the tracked eye height (avatar measurement by height).
            float userEyeHeight = headTracker.position.y;
            if (userEyeHeight < 0.5f)
            {
                Debug.LogWarning("[MocapFbt] HMD height is very low. Is the XR tracking origin in Floor mode? Using 1.6m fallback.");
                userEyeHeight = 1.6f;
            }
            float avatarEyeHeight = characterRoot.InverseTransformPoint(GetViewpointWorld()).y;
            float scale = Mathf.Clamp(userEyeHeight / Mathf.Max(avatarEyeHeight, 0.01f), minScale, maxScale);
            _profile.uniformScale = scale;
            characterRoot.localScale = _originalLocalScale * scale;
            Debug.Log($"[MocapFbt] Eye height: user={userEyeHeight:F3}m avatar={avatarEyeHeight:F3}m -> scale={scale:F3}");

            // Root Y stays on the floor; XZ/yaw follow the HMD so the avatar is pinned.
            _floorY = characterRoot.position.y;
            _profile.floorY = _floorY;

            ApplyBindingOffsets(_profile);
            PinToHead();
            ApplyHeadPinOffset();

            // Live preview: VRIK on with targets assigned, head pinned at a fixed HMD-local offset.
            MocapFbtReflection.AssignTargets(_vrik, _profile);
            MocapFbtReflection.ApplyFullTrackingPolicy(_vrik);
            MocapFbtReflection.ApplyMissingBindingWeights(_vrik, _profile);
            if (_vrik != null)
                ((Behaviour)_vrik).enabled = true;

            if (ui == null) ui = FindFirstObjectByType<MocapFbtUi>();
            if (ui != null) ui.ShowCalibration(_profile.bindings);

            SetState(FbtState.Calibrating);
            StatusMessage = "Calibration mode. Stand straight, look forward, press both triggers.";
            Debug.Log($"[MocapFbt] Calibration mode entered. Scale={_profile.uniformScale:F3}");
            return true;
        }

        /// <summary>
        /// Enters the "Adjust FBT" sphere-grab mode. Requires an applied calibration.
        /// </summary>
        public bool EnterAdjustMode()
        {
            if (State != FbtState.Active)
            {
                Debug.LogWarning("[MocapFbt] Adjust mode requires an applied calibration.");
                return false;
            }

            _grabbedBinding = null;
            _leftPressed = false;
            _rightPressed = false;
            if (ui == null) ui = FindFirstObjectByType<MocapFbtUi>();
            if (ui != null) ui.ShowAdjustSpheres(_profile.bindings);

            SetState(FbtState.Adjusting);
            StatusMessage = "Adjust FBT: grab a blue sphere with the trigger to move it.";
            Debug.Log("[MocapFbt] Adjust FBT mode entered.");
            return true;
        }

        public void ExitAdjustMode()
        {
            if (State != FbtState.Adjusting) return;
            _grabbedBinding = null;
            if (ui != null)
            {
                ui.HideAdjustSpheres();
            }
            _profile.Save(MocapFbtCalibrationProfile.DefaultSavePath);
            SetState(FbtState.Active);
            StatusMessage = "Adjustments saved.";
            Debug.Log("[MocapFbt] Adjust FBT mode exited; adjustments saved.");
        }

        /// <summary>
        /// Cancels calibration/adjustment and restores the pre-calibration state.
        /// </summary>
        public void Cancel()
        {
            if (State == FbtState.Idle || State == FbtState.Failed) return;

            RestoreOriginals();
            RestoreComponents();

            if (solver != null) solver.SetActive(false);
            if (ui != null)
            {
                ui.HideCalibration();
                ui.HideAdjustSpheres();
            }

            SetState(FbtState.Idle);
            StatusMessage = "Cancelled.";
            Debug.Log("[MocapFbt] Calibration cancelled; pre-calibration state restored.");
        }

        /// <summary>
        /// Attempts to re-apply a previously saved profile without recalibrating
        /// (VRChat IK 2.0 calibration-saving analog).
        /// </summary>
        public bool TryApplySavedProfile()
        {
            if (State != FbtState.Idle) return false;

            var loaded = MocapFbtCalibrationProfile.Load(MocapFbtCalibrationProfile.DefaultSavePath);
            if (loaded == null || loaded.characterName != characterRoot.name)
            {
                Debug.Log("[MocapFbt] No saved calibration profile for this character.");
                return false;
            }

            if (solver == null) solver = FindFirstObjectByType<MocapFbtSolver>();

            CaptureOriginals();
            characterRoot.localScale = _originalLocalScale * loaded.uniformScale;

            ResolveBindings(loaded);
            ApplyBindingOffsets(loaded);

            if (solver != null)
            {
                solver.ApplyProfile(loaded);
                solver.SetActive(true);
            }

            _profile = loaded;
            StatusMessage = "Saved calibration profile applied.";
            SetState(FbtState.Active);
            Debug.Log($"[MocapFbt] Saved profile applied (scale={loaded.uniformScale:F3}).");
            return true;
        }

        #endregion

        #region Unity Lifecycle

        void Start()
        {
            if (autoApplySavedProfile && State == FbtState.Idle)
                StartCoroutine(AutoApplyCoroutine());
        }

        IEnumerator AutoApplyCoroutine()
        {
            yield return null;
            TryApplySavedProfile();
        }

        void Update()
        {
            switch (State)
            {
                case FbtState.Calibrating:
                    UpdateCalibrating();
                    break;
                case FbtState.Confirming:
                    UpdateConfirming();
                    break;
                case FbtState.Adjusting:
                    UpdateAdjusting();
                    break;
            }
        }

        void OnDestroy()
        {
            if (State == FbtState.Calibrating || State == FbtState.Confirming || State == FbtState.Adjusting)
            {
                RestoreOriginals();
                RestoreComponents();
            }
        }

        #endregion

        #region Calibration Update

        void UpdateCalibrating()
        {
            // Keep the avatar pinned under the HMD (position + yaw) each frame.
            PinToHead();

            if (ui != null)
                ui.UpdateCalibrationVisuals(_profile.bindings);

            bool leftPressed = MocapFbtXR.TryGetController(true, out var left) && MocapFbtXR.IsTriggerPressed(left);
            bool rightPressed = MocapFbtXR.TryGetController(false, out var right) && MocapFbtXR.IsTriggerPressed(right);
            _leftPressed = leftPressed;
            _rightPressed = rightPressed;

            if (leftPressed && rightPressed)
                BeginConfirm();
        }

        void BeginConfirm()
        {
            // Freeze the root and the avatar in its natural standing pose while
            // sampling tracker-to-bone offsets (mount geometry).
            _confirmingFrozen = true;
            _sampleTimer = 0f;
            _samples = new Dictionary<MocapFbtBinding, SampleAccum>();
            foreach (var binding in _profile.bindings)
            {
                if (binding.isValid)
                    _samples[binding] = new SampleAccum();
            }

            if (_vrik != null)
                ((Behaviour)_vrik).enabled = false;
            if (_animator != null)
            {
                _animatorWasEnabled = _animator.enabled;
                _animator.enabled = false;
            }

            SetState(FbtState.Confirming);
            StatusMessage = "Sampling... hold still.";
            Debug.Log("[MocapFbt] Both triggers pressed; sampling calibration offsets.");
        }

        void UpdateConfirming()
        {
            if (ui != null)
                ui.UpdateCalibrationVisuals(_profile.bindings);

            _sampleTimer += Time.deltaTime;
            foreach (var pair in _samples)
            {
                var binding = pair.Key;
                var accum = pair.Value;
                accum.TrackerPositions.Add(binding.trackerTransform.position);
                accum.TrackerRotations.Add(binding.trackerTransform.rotation);
                accum.BonePositions.Add(binding.boneTransform.position);
                accum.BoneRotations.Add(binding.boneTransform.rotation);
            }

            if (_sampleTimer >= confirmSampleSeconds)
                FinishCalibration();
        }

        void FinishCalibration()
        {
            foreach (var pair in _samples)
            {
                var binding = pair.Key;
                var accum = pair.Value;
                if (accum.TrackerPositions.Count == 0)
                {
                    binding.isValid = false;
                    continue;
                }

                Vector3 avgTrackerPos = AveragePositions(accum.TrackerPositions);
                Quaternion avgTrackerRot = AverageRotations(accum.TrackerRotations);
                Vector3 avgBonePos = AveragePositions(accum.BonePositions);
                Quaternion avgBoneRot = AverageRotations(accum.BoneRotations);

                // Tracker-local offset to the avatar bone. Hand offsets are kept:
                // wrist-mounted trackers need their real mount offset.
                binding.offsetPosition = Quaternion.Inverse(avgTrackerRot) * (avgBonePos - avgTrackerPos);
                if (binding.applyRotation && !binding.positionOnly)
                    binding.offsetRotationEuler = (Quaternion.Inverse(avgTrackerRot) * avgBoneRot).eulerAngles;
                else
                    binding.offsetRotationEuler = Vector3.zero;

                ApplyBindingOffset(binding);
                Debug.Log($"[MocapFbt] {binding.trackerName} offset pos={binding.offsetPosition} rot={binding.offsetRotationEuler}");
            }

            // Capture root-locomotion data for the solver.
            var pelvisBinding = _profile.GetBinding(MocapFbtRegion.Hips);
            if (pelvisBinding != null && pelvisBinding.trackerTransform != null)
            {
                _profile.rootLocalPelvisOffset = characterRoot.InverseTransformPoint(pelvisBinding.trackerTransform.position);
                _profile.yawBias = characterRoot.rotation.eulerAngles.y - pelvisBinding.trackerTransform.rotation.eulerAngles.y;
            }

            _profile.floorY = _floorY;
            _profile.Save(MocapFbtCalibrationProfile.DefaultSavePath);

            RestoreComponents();

            if (_vrik != null)
            {
                MocapFbtReflection.AssignTargets(_vrik, _profile);
                MocapFbtReflection.ApplyFullTrackingPolicy(_vrik);
                MocapFbtReflection.ApplyMissingBindingWeights(_vrik, _profile);
                // The FBT system owns VRIK after calibration, even if the scene had it disabled.
                ((Behaviour)_vrik).enabled = true;
            }

            if (solver == null) solver = FindFirstObjectByType<MocapFbtSolver>();
            if (solver != null)
            {
                solver.ApplyProfile(_profile);
                solver.SetActive(true);
            }

            if (ui != null) ui.EndCalibration();

            _samples = null;
            SetState(FbtState.Active);
            StatusMessage = "Calibration complete.";
            Debug.Log("[MocapFbt] Calibration complete.");
            OnCalibrationComplete?.Invoke(_profile);
        }

        #endregion

        #region Adjust FBT

        void UpdateAdjusting()
        {
            bool hasLeft = MocapFbtXR.TryGetController(true, out var leftDevice);
            bool hasRight = MocapFbtXR.TryGetController(false, out var rightDevice);
            bool leftPressed = hasLeft && MocapFbtXR.IsTriggerPressed(leftDevice);
            bool rightPressed = hasRight && MocapFbtXR.IsTriggerPressed(rightDevice);

            bool leftEdge = leftPressed && !_leftPressed;
            bool rightEdge = rightPressed && !_rightPressed;

            // Trigger grab/drag.
            if (_grabbedBinding == null)
            {
                InputDevice grabDevice = default;
                if (leftEdge && leftDevice.isValid) grabDevice = leftDevice;
                else if (rightEdge && rightDevice.isValid) grabDevice = rightDevice;

                if (grabDevice.isValid &&
                    MocapFbtXR.TryGetPose(grabDevice, out var grabPos, out _) &&
                    TryFindNearestSphere(grabDevice, out var grabbed, out var grabbedPos))
                {
                    _grabbedBinding = grabbed;
                    _grabStartControllerPos = grabPos;
                    _grabStartTargetWorld = grabbedPos;
                    Debug.Log($"[MocapFbt] Grabbed adjustment sphere: {grabbed.region}");
                }
            }
            else
            {
                InputDevice activeDevice = leftPressed ? leftDevice : rightDevice;
                if (MocapFbtXR.TryGetPose(activeDevice, out var activePos, out _))
                {
                    Vector3 newWorld = _grabStartTargetWorld + (activePos - _grabStartControllerPos);
                    var trackerRot = _grabbedBinding.trackerTransform.rotation;
                    _grabbedBinding.offsetPosition = Quaternion.Inverse(trackerRot) * (newWorld - _grabbedBinding.trackerTransform.position);
                    _grabbedBinding.offsetTransform.localPosition = _grabbedBinding.offsetPosition;
                }

                if (!leftPressed && !rightPressed)
                {
                    Debug.Log($"[MocapFbt] Released sphere: {_grabbedBinding.region}");
                    _grabbedBinding = null;
                    _profile.Save(MocapFbtCalibrationProfile.DefaultSavePath);
                }
            }

            if (ui != null) ui.UpdateAdjustVisuals(_profile.bindings);

            _leftPressed = leftPressed;
            _rightPressed = rightPressed;
        }

        bool TryFindNearestSphere(InputDevice device, out MocapFbtBinding nearest, out Vector3 spherePos)
        {
            nearest = null;
            spherePos = Vector3.zero;

            if (!MocapFbtXR.TryGetPose(device, out var controllerPos, out _))
                return false;

            float bestSqr = adjustGrabRadius * adjustGrabRadius;
            foreach (var binding in _profile.bindings)
            {
                if (!binding.isValid || binding.offsetTransform == null) continue;
                if (!MocapFbtRegionUtil.IsAdjustable(binding.region)) continue;
                Vector3 pos = binding.offsetTransform.position;
                float sqr = (pos - controllerPos).sqrMagnitude;
                if (sqr <= bestSqr)
                {
                    bestSqr = sqr;
                    nearest = binding;
                    spherePos = pos;
                }
            }

            return nearest != null;
        }

        #endregion

        #region Helpers

        void SetState(FbtState newState)
        {
            State = newState;
            OnStateChanged?.Invoke(newState);
        }

        void Fail(string message)
        {
            StatusMessage = $"FAILED: {message}";
            Debug.LogError($"[MocapFbt] {message}");
            SetState(FbtState.Failed);
        }

        bool Validate(out string error)
        {
            error = null;
            if (characterRoot == null) { error = "characterRoot is not assigned."; return false; }

            _animator = characterRoot.GetComponentInChildren<Animator>();
            if (_animator == null || !_animator.isHuman)
            {
                error = "Character must have a Humanoid Animator.";
                return false;
            }

            _headBone = _animator.GetBoneTransform(HumanBodyBones.Head);
            if (_headBone == null)
            {
                error = "Humanoid Head bone not found.";
                return false;
            }

            if (headTracker == null)
                headTracker = FindTransform(trackingRoot, "Tracked_Head");
            if (headTracker == null)
            {
                error = "HMD tracker (Tracked_Head) not found.";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Current avatar viewpoint in world space: the serialized avatarViewpoint
        /// if assigned, otherwise the Head bone plus the eye offset. Computed live
        /// so it always reflects the avatar's current scale.
        /// </summary>
        Vector3 GetViewpointWorld()
        {
            if (avatarViewpoint != null)
                return avatarViewpoint.position;

            if (_headBone == null)
                return characterRoot != null ? characterRoot.position : Vector3.zero;

            return _headBone.position + _headBone.rotation * eyeLocalOffset;
        }

        void CaptureOriginals()
        {
            if (!_scaleCaptured)
            {
                _originalLocalScale = characterRoot.localScale;
                _scaleCaptured = true;
            }
            _originalRootPos = characterRoot.position;
            _originalRootRot = characterRoot.rotation;
        }

        void RestoreOriginals()
        {
            if (!_scaleCaptured || characterRoot == null) return;
            characterRoot.localScale = _originalLocalScale;
            characterRoot.position = _originalRootPos;
            characterRoot.rotation = _originalRootRot;
        }

        void DeactivateSolver()
        {
            if (solver == null) solver = FindFirstObjectByType<MocapFbtSolver>();
            if (solver != null) solver.SetActive(false);
        }

        void RestoreComponents()
        {
            if (_vrik != null)
                ((Behaviour)_vrik).enabled = _vrikWasEnabled;

            if (_animator != null && _confirmingFrozen)
            {
                _animator.enabled = _animatorWasEnabled;
                _confirmingFrozen = false;
            }
        }

        void PinToHead()
        {
            if (characterRoot == null || headTracker == null)
                return;

            Vector3 viewpointLocal = characterRoot.InverseTransformPoint(GetViewpointWorld());
            Vector3 pos = characterRoot.position;
            pos.x = headTracker.position.x - viewpointLocal.x;
            pos.z = headTracker.position.z - viewpointLocal.z;
            pos.y = _floorY;
            characterRoot.position = pos;

            // Yaw follows the HMD so the avatar turns with the performer.
            characterRoot.rotation = Quaternion.Euler(0f, headTracker.rotation.eulerAngles.y + _profile.yawBias, 0f);
        }

        /// <summary>
        /// Pins the head at a FIXED offset in the HMD frame, derived once from the
        /// avatar's rest geometry. Unlike the previous live feedback version, this
        /// never re-reads the solved head bone, so no error can be baked in.
        /// </summary>
        void ApplyHeadPinOffset()
        {
            var headBinding = _profile.GetBinding(MocapFbtRegion.Head);
            if (headBinding == null || !headBinding.isValid || headBinding.offsetTransform == null)
                return;

            headBinding.offsetTransform.localPosition =
                Quaternion.Inverse(headTracker.rotation) * (_headBone.position - headTracker.position);
            headBinding.offsetTransform.localRotation =
                Quaternion.Inverse(headTracker.rotation) * _headBone.rotation;
        }

        void ResolveBindings(MocapFbtCalibrationProfile profile)
        {
            foreach (var binding in profile.bindings)
            {
                binding.isValid = false;
                binding.trackerTransform = FindTransform(trackingRoot, binding.trackerName);

                if (binding.trackerTransform == null && binding.region == MocapFbtRegion.Hips)
                    binding.trackerTransform = FindTransform(trackingRoot, "Tracked_Waist");

                if (binding.trackerTransform == null)
                {
                    Debug.LogWarning($"[MocapFbt] Tracker not found: {binding.trackerName}");
                    continue;
                }

                binding.offsetTransform = EnsureOffsetChild(binding.trackerTransform, binding.offsetChildName);
                binding.boneTransform = _animator.GetBoneTransform(BoneForRegion(binding.region));
                if (binding.boneTransform == null)
                {
                    Debug.LogWarning($"[MocapFbt] Bone not found for region {binding.region}");
                    continue;
                }

                binding.isValid = true;
            }
        }

        void ApplyBindingOffsets(MocapFbtCalibrationProfile profile)
        {
            foreach (var binding in profile.bindings)
            {
                if (binding.isValid)
                    ApplyBindingOffset(binding);
            }
        }

        void ApplyBindingOffset(MocapFbtBinding binding)
        {
            if (binding.offsetTransform == null) return;
            binding.offsetTransform.localPosition = binding.offsetPosition;
            binding.offsetTransform.localRotation = binding.applyRotation && !binding.positionOnly
                ? binding.OffsetRotation
                : Quaternion.identity;
        }

        static HumanBodyBones BoneForRegion(MocapFbtRegion region)
        {
            return region switch
            {
                MocapFbtRegion.Head => HumanBodyBones.Head,
                MocapFbtRegion.Hips => HumanBodyBones.Hips,
                MocapFbtRegion.LeftHand => HumanBodyBones.LeftHand,
                MocapFbtRegion.RightHand => HumanBodyBones.RightHand,
                MocapFbtRegion.LeftFoot => HumanBodyBones.LeftFoot,
                MocapFbtRegion.RightFoot => HumanBodyBones.RightFoot,
                MocapFbtRegion.LeftElbow => HumanBodyBones.LeftLowerArm,
                MocapFbtRegion.RightElbow => HumanBodyBones.RightLowerArm,
                _ => HumanBodyBones.LastBone
            };
        }

        static Transform FindTransform(Transform root, string name)
        {
            if (root != null)
                return FindChildRecursive(root, name);

            var go = GameObject.Find(name);
            return go != null ? go.transform : null;
        }

        static Transform FindChildRecursive(Transform parent, string name)
        {
            foreach (Transform child in parent)
            {
                if (child.name == name)
                    return child;

                var found = FindChildRecursive(child, name);
                if (found != null)
                    return found;
            }
            return null;
        }

        static Transform EnsureOffsetChild(Transform tracker, string offsetName)
        {
            Transform existing = tracker.Find(offsetName);
            if (existing != null)
                return existing;

            var offsetGo = new GameObject(offsetName);
            offsetGo.transform.SetParent(tracker, false);
            offsetGo.transform.localPosition = Vector3.zero;
            offsetGo.transform.localRotation = Quaternion.identity;
            offsetGo.transform.localScale = Vector3.one;
            return offsetGo.transform;
        }

        static Vector3 AveragePositions(List<Vector3> positions)
        {
            if (positions.Count == 0) return Vector3.zero;
            Vector3 sum = Vector3.zero;
            foreach (var p in positions) sum += p;
            return sum / positions.Count;
        }

        static Quaternion AverageRotations(List<Quaternion> rotations)
        {
            if (rotations.Count == 0) return Quaternion.identity;

            Quaternion first = rotations[0];
            Vector4 cumulative = new Vector4(first.x, first.y, first.z, first.w);
            for (int i = 1; i < rotations.Count; i++)
            {
                Quaternion q = rotations[i];
                if (first.x * q.x + first.y * q.y + first.z * q.z + first.w * q.w < 0)
                    q = new Quaternion(-q.x, -q.y, -q.z, -q.w);

                cumulative.x += q.x;
                cumulative.y += q.y;
                cumulative.z += q.z;
                cumulative.w += q.w;
            }

            float mag = Mathf.Sqrt(cumulative.x * cumulative.x + cumulative.y * cumulative.y +
                                    cumulative.z * cumulative.z + cumulative.w * cumulative.w);
            return mag < 0.0001f
                ? Quaternion.identity
                : new Quaternion(cumulative.x / mag, cumulative.y / mag, cumulative.z / mag, cumulative.w / mag);
        }

        #endregion
    }
}
