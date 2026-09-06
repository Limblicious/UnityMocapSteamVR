using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;

namespace MocapTools
{
    /// <summary>
    /// Runtime component that performs T-Pose calibration for VRIK mocap setups.
    /// Creates offset transforms under tracked objects so VRIK targets align with avatar bones.
    ///
    /// This component is automatically created and managed by MocapCalibratorWindow.
    /// </summary>
    public class MocapCalibratorRunner : MonoBehaviour
    {
        #region Enums and Data Structures

        /// <summary>
        /// Current state of the calibration process.
        /// </summary>
        public enum CalibrationState
        {
            Idle,
            CountingDown,
            Sampling,
            Applying,
            Completed,
            Failed,
            Cancelled
        }

        /// <summary>
        /// Mapping between a tracked object and an avatar bone.
        /// </summary>
        [Serializable]
        public class TrackerMapping
        {
            public string TrackerName;          // e.g., "Tracked_HandL"
            public string OffsetChildName;      // e.g., "HandL_Off"
            public HumanBodyBones Bone;         // Target bone on avatar
            public bool ApplyRotation;          // Whether to apply rotation offset
            public bool PositionOnly;           // If true, only position (for elbows)

            // Runtime references (populated during calibration)
            [NonSerialized] public Transform TrackerTransform;
            [NonSerialized] public Transform OffsetTransform;
            [NonSerialized] public Transform BoneTransform;
            [NonSerialized] public bool IsValid;
        }

        /// <summary>
        /// Request parameters for calibration.
        /// </summary>
        public class CalibrationRequest
        {
            public Transform CharacterRoot;
            public Transform TrackingRoot;
            public float CountdownSeconds = 5f;
            public float SampleDurationSeconds = 0.5f;
            public bool EnableVRIKAfterCalibration = true;
            public bool FreezeAnimatorDuringCalibration = false;
            public bool ApplyRotationForHeadHandsFeetPelvis = true;
            public bool ApplyRotationForFeet = true;
            public List<TrackerMapping> CustomMappings = null;  // null = use defaults
        }

        /// <summary>
        /// Result of calibration for each mapping.
        /// </summary>
        public class CalibrationResult
        {
            public string TrackerName;
            public Vector3 AppliedLocalPosition;
            public Quaternion AppliedLocalRotation;
            public bool Success;
            public string ErrorMessage;
        }

        #endregion

        #region Public Properties

        public CalibrationState State { get; private set; } = CalibrationState.Idle;
        public float CountdownRemaining { get; private set; }
        public float SamplingProgress { get; private set; }
        public string StatusMessage { get; private set; } = "Idle";
        public List<CalibrationResult> Results { get; private set; } = new List<CalibrationResult>();

        public event Action<CalibrationState> OnStateChanged;
        public event Action<float> OnCountdownTick;
        public event Action<List<CalibrationResult>> OnCalibrationComplete;

        #endregion

        #region Private Fields

        private Coroutine _calibrationCoroutine;
        private CalibrationRequest _currentRequest;
        private MonoBehaviour _vrikComponent;
        private bool _vrikWasEnabled;
        private bool _vrikStateCaptured;
        private Quaternion _rootYawOffset = Quaternion.identity;
        private Vector3 _rootOldPos;
        private Animator _animator;
        private bool _animatorWasEnabled;

        // Sampling data
        private class SampleData
        {
            // Tracker world pose samples
            public List<Vector3> Positions = new List<Vector3>();
            public List<Quaternion> Rotations = new List<Quaternion>();
            // Tracker LOCAL pose samples (relative to TrackingRoot)
            public List<Vector3> LocalPositions = new List<Vector3>();
            public List<Quaternion> LocalRotations = new List<Quaternion>();
            // Bone world pose samples
            public List<Vector3> BonePositions = new List<Vector3>();
            public List<Quaternion> BoneRotations = new List<Quaternion>();
        }
        private Dictionary<TrackerMapping, SampleData> _samples;

        // Constant for head tracker name
        private const string HEAD_TRACKER_NAME = "Tracked_Head";

        // Default tracker names and mappings
        private static readonly (string tracker, string offset, HumanBodyBones bone, bool applyRot, bool posOnly)[] DefaultMappings =
        {
            ("Tracked_Head", "Head_Off", HumanBodyBones.Head, true, false),
            ("Tracked_HandL", "HandL_Off", HumanBodyBones.LeftHand, true, false),
            ("Tracked_HandR", "HandR_Off", HumanBodyBones.RightHand, true, false),
            ("Tracked_Hips", "Hips_Off", HumanBodyBones.Hips, true, false),
            ("Tracked_Waist", "Hips_Off", HumanBodyBones.Hips, true, false),  // Alternative name
            ("Tracked_FootL", "FootL_Off", HumanBodyBones.LeftFoot, true, false),
            ("Tracked_FootR", "FootR_Off", HumanBodyBones.RightFoot, true, false),
            ("Tracked_ElbowL", "ElbowL_Off", HumanBodyBones.LeftLowerArm, false, true),
            ("Tracked_ElbowR", "ElbowR_Off", HumanBodyBones.RightLowerArm, false, true),
        };

        #endregion

        #region Public Methods

        /// <summary>
        /// Starts the calibration process with the given request parameters.
        /// </summary>
        public void StartCalibration(CalibrationRequest request)
        {
            if (State == CalibrationState.CountingDown || State == CalibrationState.Sampling)
            {
                Debug.LogWarning("[MocapCalibrator] Calibration already in progress. Cancel first.");
                return;
            }

            _currentRequest = request;
            Results.Clear();

            if (_calibrationCoroutine != null)
            {
                StopCoroutine(_calibrationCoroutine);
            }

            _calibrationCoroutine = StartCoroutine(CalibrationCoroutine());
        }

        /// <summary>
        /// Cancels an in-progress calibration.
        /// </summary>
        public void CancelCalibration()
        {
            if (_calibrationCoroutine != null)
            {
                StopCoroutine(_calibrationCoroutine);
                _calibrationCoroutine = null;
            }

            // Restore VRIK/Animator state
            RestoreComponents();

            SetState(CalibrationState.Cancelled);
            StatusMessage = "Calibration cancelled.";
        }

        /// <summary>
        /// Gets the default tracker mappings for UI display.
        /// </summary>
        public static List<TrackerMapping> GetDefaultMappings()
        {
            var mappings = new List<TrackerMapping>();

            // Add unique mappings (skip Tracked_Waist as it's an alternative to Tracked_Hips)
            var added = new HashSet<HumanBodyBones>();
            foreach (var m in DefaultMappings)
            {
                if (m.tracker == "Tracked_Waist") continue;  // Skip alternative

                mappings.Add(new TrackerMapping
                {
                    TrackerName = m.tracker,
                    OffsetChildName = m.offset,
                    Bone = m.bone,
                    ApplyRotation = m.applyRot,
                    PositionOnly = m.posOnly
                });
            }

            return mappings;
        }

        /// <summary>
        /// Finds tracked objects and bones for the given request (for UI preview).
        /// </summary>
        public List<TrackerMapping> ValidateMappings(CalibrationRequest request)
        {
            var mappings = request.CustomMappings ?? GetDefaultMappings();
            var animator = request.CharacterRoot?.GetComponentInChildren<Animator>();

            foreach (var mapping in mappings)
            {
                mapping.IsValid = false;

                // Find tracker
                mapping.TrackerTransform = FindTrackerTransform(request.TrackingRoot, mapping.TrackerName);

                // Find bone
                if (animator != null && animator.isHuman)
                {
                    mapping.BoneTransform = animator.GetBoneTransform(mapping.Bone);
                }

                // Check validity
                mapping.IsValid = mapping.TrackerTransform != null && mapping.BoneTransform != null;
            }

            return mappings;
        }

        #endregion

        #region Calibration Coroutine

        private IEnumerator CalibrationCoroutine()
        {
            StatusMessage = "Initializing...";
            SetState(CalibrationState.CountingDown);

            // Validate request
            if (_currentRequest.CharacterRoot == null)
            {
                Fail("Character Root is not assigned.");
                yield break;
            }

            // Find Animator
            _animator = _currentRequest.CharacterRoot.GetComponentInChildren<Animator>();
            if (_animator == null || !_animator.isHuman)
            {
                Fail("Character must have a Humanoid Animator.");
                yield break;
            }

            // Find VRIK
            _vrikComponent = FindVRIK(_currentRequest.CharacterRoot);
            if (_vrikComponent == null && _currentRequest.EnableVRIKAfterCalibration)
            {
                Debug.LogWarning("[MocapCalibrator] VRIK component not found. VRIK will not be enabled after calibration.");
            }
            else if (_vrikComponent != null)
            {
                _vrikWasEnabled = ((Behaviour)_vrikComponent).enabled;
                _vrikStateCaptured = true;
                ((Behaviour)_vrikComponent).enabled = false;
            }

            // Build mappings
            var mappings = _currentRequest.CustomMappings ?? GetDefaultMappings();

            // Resolve transforms and create offset children if needed
            int validMappings = 0;
            foreach (var mapping in mappings)
            {
                // Find tracker
                mapping.TrackerTransform = FindTrackerTransform(_currentRequest.TrackingRoot, mapping.TrackerName);
                if (mapping.TrackerTransform == null)
                {
                    // Try alternative names
                    if (mapping.TrackerName == "Tracked_Hips")
                    {
                        mapping.TrackerTransform = FindTrackerTransform(_currentRequest.TrackingRoot, "Tracked_Waist");
                    }
                }

                if (mapping.TrackerTransform == null)
                {
                    Debug.LogWarning($"[MocapCalibrator] Tracker not found: {mapping.TrackerName}");
                    continue;
                }

                // Find or create offset child
                mapping.OffsetTransform = EnsureOffsetChild(mapping.TrackerTransform, mapping.OffsetChildName);

                // Find bone
                mapping.BoneTransform = _animator.GetBoneTransform(mapping.Bone);
                if (mapping.BoneTransform == null)
                {
                    Debug.LogWarning($"[MocapCalibrator] Bone not found: {mapping.Bone}");
                    continue;
                }

                // Apply rotation settings
                if (mapping.PositionOnly)
                {
                    mapping.ApplyRotation = false;
                }
                else if (mapping.Bone == HumanBodyBones.LeftFoot || mapping.Bone == HumanBodyBones.RightFoot)
                {
                    mapping.ApplyRotation = _currentRequest.ApplyRotationForFeet;
                }
                else
                {
                    mapping.ApplyRotation = _currentRequest.ApplyRotationForHeadHandsFeetPelvis;
                }

                mapping.IsValid = true;
                validMappings++;
            }

            if (validMappings == 0)
            {
                Fail("No valid tracker-bone mappings found. Check your TrackingRoot and Character setup.");
                yield break;
            }

            Debug.Log($"[MocapCalibrator] Found {validMappings} valid mappings. Starting countdown...");

            // TrackingRoot stays at identity: tracker targets live in the XR tracking
            // space so the VR camera rig (which is also at identity) keeps its correct pose.
            // The character root is aligned to the tracked head instead.
            _currentRequest.TrackingRoot.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            _rootYawOffset = Quaternion.identity;
            _rootOldPos = Vector3.zero;

            // Remove any proportion scaler left over from older calibration runs.
            var staleScaler = FindFirstObjectByType<MocapProportionScaler>();
            if (staleScaler != null)
            {
                Destroy(staleScaler);
                Debug.Log("[MocapCalibrator] Removed stale MocapProportionScaler.");
            }

            // Freeze animator during calibration if requested
            if (_currentRequest.FreezeAnimatorDuringCalibration && _animator != null)
            {
                _animatorWasEnabled = _animator.enabled;
                _animator.enabled = false;
                Debug.Log("[MocapCalibrator] Animator disabled for calibration.");
            }

            // Wait one frame for pose to settle
            yield return null;

            // Countdown phase
            CountdownRemaining = _currentRequest.CountdownSeconds;
            while (CountdownRemaining > 0)
            {
                StatusMessage = $"Hold T-Pose: {CountdownRemaining:F1}s";
                OnCountdownTick?.Invoke(CountdownRemaining);

                yield return null;
                CountdownRemaining -= Time.deltaTime;
            }

            CountdownRemaining = 0;

            // Sampling phase
            SetState(CalibrationState.Sampling);
            StatusMessage = "Sampling...";

            _samples = new Dictionary<TrackerMapping, SampleData>();
            foreach (var mapping in mappings)
            {
                if (mapping.IsValid)
                {
                    _samples[mapping] = new SampleData();
                }
            }

            float sampleStartTime = Time.time;
            float sampleDuration = _currentRequest.SampleDurationSeconds;

            while (Time.time - sampleStartTime < sampleDuration)
            {
                SamplingProgress = (Time.time - sampleStartTime) / sampleDuration;
                StatusMessage = $"Sampling: {SamplingProgress * 100f:F0}%";

                // Collect samples
                foreach (var mapping in mappings)
                {
                    if (!mapping.IsValid) continue;

                    var data = _samples[mapping];

                    // Sample tracker world pose
                    data.Positions.Add(mapping.TrackerTransform.position);
                    data.Rotations.Add(mapping.TrackerTransform.rotation);

                    // Sample tracker LOCAL pose (relative to parent/TrackingRoot)
                    data.LocalPositions.Add(mapping.TrackerTransform.localPosition);
                    data.LocalRotations.Add(mapping.TrackerTransform.localRotation);

                    // Sample bone world pose
                    data.BonePositions.Add(mapping.BoneTransform.position);
                    data.BoneRotations.Add(mapping.BoneTransform.rotation);
                }

                yield return null;
            }

            SamplingProgress = 1f;

            // Apply offsets
            SetState(CalibrationState.Applying);
            StatusMessage = "Aligning TrackingRoot to head...";

            yield return null;

            // Step 1: Align the character root to the tracked head so the avatar
            // stands where the performer is. TrackingRoot remains at identity, keeping
            // tracker targets in the XR tracking space and the VR camera rig correct.
            if (!AlignCharacterRootToHead(mappings))
            {
                yield break; // AlignCharacterRootToHead already called Fail()
            }

            StatusMessage = "Applying offsets...";
            yield return null;

            // Step 2: Apply offsets to each tracker.
            // TrackingRoot is identity, so sampled local poses equal the tracked world
            // poses. Using the sampled averages avoids mixing stale and live data.
            Quaternion alignedTRRot = _currentRequest.TrackingRoot.rotation;
            Vector3 alignedTRPos = _currentRequest.TrackingRoot.position;

            foreach (var mapping in mappings)
            {
                if (!mapping.IsValid)
                {
                    Results.Add(new CalibrationResult
                    {
                        TrackerName = mapping.TrackerName,
                        Success = false,
                        ErrorMessage = "Invalid mapping"
                    });
                    continue;
                }

                var data = _samples[mapping];
                if (data.Positions.Count == 0)
                {
                    Results.Add(new CalibrationResult
                    {
                        TrackerName = mapping.TrackerName,
                        Success = false,
                        ErrorMessage = "No samples collected"
                    });
                    continue;
                }

                // Average all sampled data for this tracker
                Vector3 avgBonePos = AveragePositions(data.BonePositions);
                Quaternion avgBoneRot = AverageRotations(data.BoneRotations);
                Vector3 avgTrackerLocalPos = AveragePositions(data.LocalPositions);
                Quaternion avgTrackerLocalRot = AverageRotations(data.LocalRotations);

                // Bone samples were captured before the character root was aligned.
                // Transform them into the aligned root frame so offsets do not bake
                // in the pre-alignment translation/rotation.
                Vector3 avgBonePosAligned = _rootYawOffset * (avgBonePos - _rootOldPos) + _currentRequest.CharacterRoot.position;
                Quaternion avgBoneRotAligned = _rootYawOffset * avgBoneRot;

                // Compute expected tracker world pose from sampled local pose + aligned TrackingRoot.
                // This uses all data from the same sampling window, eliminating drift from
                // the frames between sampling and offset application.
                Vector3 expectedWorldPos = alignedTRPos + alignedTRRot * avgTrackerLocalPos;
                Quaternion expectedWorldRot = alignedTRRot * avgTrackerLocalRot;

                // Offset from expected tracker pose to bone pose (manual InverseTransformPoint)
                Vector3 localPos = Quaternion.Inverse(expectedWorldRot) * (avgBonePosAligned - expectedWorldPos);
                Quaternion localRot = Quaternion.Inverse(expectedWorldRot) * avgBoneRotAligned;

                // Hands: zero position - wrist trackers have high accuracy.
                if (mapping.Bone == HumanBodyBones.LeftHand ||
                    mapping.Bone == HumanBodyBones.RightHand)
                {
                    localPos = Vector3.zero;
                }

                // Apply to offset transform
                mapping.OffsetTransform.localPosition = localPos;

                if (mapping.ApplyRotation)
                {
                    mapping.OffsetTransform.localRotation = localRot;
                }
                else
                {
                    mapping.OffsetTransform.localRotation = Quaternion.identity;
                }

                Results.Add(new CalibrationResult
                {
                    TrackerName = mapping.TrackerName,
                    AppliedLocalPosition = localPos,
                    AppliedLocalRotation = mapping.ApplyRotation ? localRot : Quaternion.identity,
                    Success = true
                });

                Debug.Log($"[MocapCalibrator] {mapping.TrackerName} -> {mapping.OffsetChildName}: " +
                          $"localPos={localPos}, localRot={localRot.eulerAngles}");
            }

            ConfigureVRIKTargets(mappings);

            // Restore animator state
            RestoreComponents();

            // Enable VRIK now that calibration is complete
            if (_currentRequest.EnableVRIKAfterCalibration && _vrikComponent != null)
            {
                ((Behaviour)_vrikComponent).enabled = true;
                Debug.Log("[MocapCalibrator] VRIK enabled after calibration.");
            }

            // Done
            SetState(CalibrationState.Completed);
            StatusMessage = $"Calibration complete. Applied {Results.FindAll(r => r.Success).Count} offsets.";

            Debug.Log($"[MocapCalibrator] {StatusMessage}");
            OnCalibrationComplete?.Invoke(Results);

            _calibrationCoroutine = null;
        }

        #endregion

        #region Helper Methods

        private void SetState(CalibrationState newState)
        {
            State = newState;
            OnStateChanged?.Invoke(newState);
        }

        private void Fail(string message)
        {
            StatusMessage = $"FAILED: {message}";
            Debug.LogError($"[MocapCalibrator] {message}");
            RestoreComponents();
            SetState(CalibrationState.Failed);
        }

        /// <summary>
        /// Moves the character root so the avatar's head matches the tracked head
        /// (translation only). TrackingRoot remains at identity, so tracker targets
        /// stay in the XR tracking space and the VR camera rig keeps its correct pose.
        /// Returns true on success, false on failure.
        /// </summary>
        private bool AlignCharacterRootToHead(List<TrackerMapping> mappings)
        {
            // Find the head mapping
            TrackerMapping headMapping = null;
            SampleData headSamples = null;

            foreach (var mapping in mappings)
            {
                if (mapping.TrackerName == HEAD_TRACKER_NAME && mapping.IsValid)
                {
                    headMapping = mapping;
                    if (_samples.TryGetValue(mapping, out var data))
                    {
                        headSamples = data;
                    }
                    break;
                }
            }

            if (headMapping == null || headSamples == null ||
                headSamples.LocalPositions.Count == 0 ||
                headSamples.BonePositions.Count == 0)
            {
                Fail("Head tracker (Tracked_Head) not found or invalid. Head is required as the calibration anchor.");
                return false;
            }

            if (_currentRequest.CharacterRoot == null)
            {
                Fail("CharacterRoot must be assigned for head-anchored calibration.");
                return false;
            }

            // TrackingRoot is identity, so tracked local poses equal world poses.
            Vector3 avgHeadTrackerWorldPos = AveragePositions(headSamples.LocalPositions);
            Quaternion avgHeadTrackerWorldRot = AverageRotations(headSamples.LocalRotations);
            Vector3 avgHeadBoneWorldPos = AveragePositions(headSamples.BonePositions);
            Quaternion avgHeadBoneWorldRot = AverageRotations(headSamples.BoneRotations);

            Transform root = _currentRequest.CharacterRoot;
            _rootOldPos = root.position;

            // Yaw-align the avatar to the tracked head, then translate it so the
            // avatar head sits exactly on the tracked head. Sampled bone poses are
            // transformed into this aligned frame later when offsets are computed.
            float trackerYaw = avgHeadTrackerWorldRot.eulerAngles.y;
            float boneYaw = avgHeadBoneWorldRot.eulerAngles.y;
            _rootYawOffset = Quaternion.Euler(0f, trackerYaw - boneYaw, 0f);

            Vector3 oldPos = root.position;
            root.rotation = _rootYawOffset * root.rotation;
            root.position = avgHeadTrackerWorldPos - _rootYawOffset * (avgHeadBoneWorldPos - _rootOldPos);

            Debug.Log($"[MocapCalibrator] Character root aligned to head anchor:\n" +
                      $"  Old pos: {oldPos}\n" +
                      $"  New pos: {root.position}\n" +
                      $"  Yaw delta: {trackerYaw - boneYaw:F2}°\n" +
                      $"  Head error after alignment: {Vector3.Distance(headMapping.TrackerTransform.position, headMapping.BoneTransform.position):F4}m");

            return true;
        }

        private void RestoreComponents()
        {
            if (_currentRequest == null) return;

            if (_currentRequest.FreezeAnimatorDuringCalibration && _animator != null)
            {
                _animator.enabled = _animatorWasEnabled;
                Debug.Log("[MocapCalibrator] Animator restored.");
            }

            if (_vrikStateCaptured && _vrikComponent != null)
            {
                ((Behaviour)_vrikComponent).enabled = _vrikWasEnabled;
                _vrikStateCaptured = false;
            }
        }

        private void ConfigureVRIKTargets(List<TrackerMapping> mappings)
        {
            if (_vrikComponent == null)
                return;

            object solver = GetMemberValue(_vrikComponent, "solver");
            if (solver == null)
            {
                Debug.LogWarning("[MocapCalibrator] Could not access the VRIK solver to assign calibrated targets.");
                return;
            }

            int assigned = 0;
            foreach (var mapping in mappings)
            {
                if (!mapping.IsValid || mapping.OffsetTransform == null)
                    continue;

                string solverPart;
                string targetMember;
                switch (mapping.Bone)
                {
                    case HumanBodyBones.Head:
                        solverPart = "spine";
                        targetMember = "headTarget";
                        break;
                    case HumanBodyBones.Hips:
                        solverPart = "spine";
                        targetMember = "pelvisTarget";
                        break;
                    case HumanBodyBones.LeftHand:
                        solverPart = "leftArm";
                        targetMember = "target";
                        break;
                    case HumanBodyBones.RightHand:
                        solverPart = "rightArm";
                        targetMember = "target";
                        break;
                    case HumanBodyBones.LeftFoot:
                        solverPart = "leftLeg";
                        targetMember = "target";
                        break;
                    case HumanBodyBones.RightFoot:
                        solverPart = "rightLeg";
                        targetMember = "target";
                        break;
                    case HumanBodyBones.LeftLowerArm:
                        solverPart = "leftArm";
                        targetMember = "bendGoal";
                        break;
                    case HumanBodyBones.RightLowerArm:
                        solverPart = "rightArm";
                        targetMember = "bendGoal";
                        break;
                    default:
                        continue;
                }

                object part = GetMemberValue(solver, solverPart);
                if (part != null && SetMemberValue(part, targetMember, mapping.OffsetTransform))
                    assigned++;
                else
                    Debug.LogWarning($"[MocapCalibrator] Could not assign VRIK {solverPart}.{targetMember}.");
            }

            Debug.Log($"[MocapCalibrator] Assigned {assigned} calibrated VRIK target(s).");
        }

        private static object GetMemberValue(object target, string memberName)
        {
            if (target == null)
                return null;

            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic;
            var type = target.GetType();
            var field = type.GetField(memberName, flags);
            if (field != null)
                return field.GetValue(target);

            var property = type.GetProperty(memberName, flags);
            return property != null && property.CanRead ? property.GetValue(target) : null;
        }

        private static bool SetMemberValue(object target, string memberName, object value)
        {
            if (target == null)
                return false;

            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic;
            var type = target.GetType();
            var field = type.GetField(memberName, flags);
            if (field != null)
            {
                field.SetValue(target, value);
                return true;
            }

            var property = type.GetProperty(memberName, flags);
            if (property == null || !property.CanWrite)
                return false;

            property.SetValue(target, value);
            return true;
        }

        private Transform FindTrackerTransform(Transform trackingRoot, string trackerName)
        {
            if (trackingRoot == null)
            {
                // Search entire scene
                var go = GameObject.Find(trackerName);
                return go?.transform;
            }

            // Search under tracking root (recursive)
            return FindChildRecursive(trackingRoot, trackerName);
        }

        private Transform FindChildRecursive(Transform parent, string name)
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

        private Transform EnsureOffsetChild(Transform tracker, string offsetName)
        {
            // Check if offset child already exists
            Transform existing = tracker.Find(offsetName);
            if (existing != null)
                return existing;

            // Create new offset child
            GameObject offsetGO = new GameObject(offsetName);
            offsetGO.transform.SetParent(tracker, false);
            offsetGO.transform.localPosition = Vector3.zero;
            offsetGO.transform.localRotation = Quaternion.identity;
            offsetGO.transform.localScale = Vector3.one;

            Debug.Log($"[MocapCalibrator] Created offset child: {tracker.name}/{offsetName}");

            return offsetGO.transform;
        }

        private MonoBehaviour FindVRIK(Transform root)
        {
            // Find VRIK by type name (to avoid direct dependency on FinalIK)
            var components = root.GetComponentsInChildren<MonoBehaviour>();
            foreach (var comp in components)
            {
                if (comp == null) continue;
                string typeName = comp.GetType().Name;
                if (typeName == "VRIK" || typeName == "FullBodyBipedIK")
                {
                    return comp;
                }
            }
            return null;
        }

        private Vector3 AveragePositions(List<Vector3> positions)
        {
            if (positions.Count == 0) return Vector3.zero;

            Vector3 sum = Vector3.zero;
            foreach (var p in positions)
            {
                sum += p;
            }
            return sum / positions.Count;
        }

        private Quaternion AverageRotations(List<Quaternion> rotations)
        {
            if (rotations.Count == 0) return Quaternion.identity;

            // Use cumulative averaging with hemisphere correction
            Quaternion first = rotations[0];
            Vector4 cumulative = new Vector4(first.x, first.y, first.z, first.w);

            for (int i = 1; i < rotations.Count; i++)
            {
                Quaternion q = rotations[i];

                // Ensure quaternions are in the same hemisphere
                float dot = first.x * q.x + first.y * q.y + first.z * q.z + first.w * q.w;
                if (dot < 0)
                {
                    q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
                }

                cumulative.x += q.x;
                cumulative.y += q.y;
                cumulative.z += q.z;
                cumulative.w += q.w;
            }

            // Normalize
            float mag = Mathf.Sqrt(cumulative.x * cumulative.x + cumulative.y * cumulative.y +
                                    cumulative.z * cumulative.z + cumulative.w * cumulative.w);
            if (mag < 0.0001f)
                return Quaternion.identity;

            return new Quaternion(
                cumulative.x / mag,
                cumulative.y / mag,
                cumulative.z / mag,
                cumulative.w / mag);
        }

        #endregion

        #region Lifecycle

        private void OnDestroy()
        {
            if (State == CalibrationState.CountingDown || State == CalibrationState.Sampling)
            {
                RestoreComponents();
                Debug.LogWarning("[MocapCalibrator] Calibrator destroyed during calibration.");
            }
        }

        #endregion
    }
}
