using UnityEngine;

namespace MocapTools
{
    /// <summary>
    /// Retargets live StretchSense Reality glove finger motion (received via
    /// <see cref="MocapOscReceiver"/>) onto Linnra hand rigs.
    ///
    /// The Reality Core Driver streams 26 joints per hand in OpenXR
    /// XrHandJointEXT order, expressed in the glove's own local hand space and
    /// aligned to StretchSense's internal hand avatar. A direct rotation copy
    /// would therefore be wrong for a different hand rig. This bridge captures
    /// a rest pose per bone (glove rest = a stable received frame, rig rest =
    /// initial local rotations) and applies only the delta from rest:
    ///
    ///   bone.localRotation = rigRest * (inverse(gloveRest) * gloveNow)
    ///
    /// Hand position/orientation remains the responsibility of the tracker
    /// pipeline (TrackerPoseRelay); this component drives fingers only.
    /// </summary>
    public class MocapFingerPoseBridge : MonoBehaviour
    {
        [Tooltip("Apply the glove finger motion to the target rig.")]
        public bool apply = true;

        [Tooltip("Left hand root transform. Auto-resolved from Hand_L if empty.")]
        public Transform leftHandRoot;

        [Tooltip("Right hand root transform. Auto-resolved from Hand_R if empty.")]
        public Transform rightHandRoot;

        [Tooltip("Maximum per-joint rotation change per frame. Larger changes are clamped instead of discarded.")]
        public float maxDeltaDegrees = 40f;

        [Tooltip("Exponential smoothing speed in 1/seconds. 0 applies frames immediately.")]
        [Min(0f)] public float smoothingSpeed = 25f;

        [Tooltip("Consecutive stable glove frames required before capturing rest.")]
        [Min(1)] public int restStableFrames = 10;

        [Tooltip("Maximum joint motion between frames while considering the glove stable.")]
        [Min(0f)] public float restStabilityDegrees = 1.5f;

        [Tooltip("Capture rest after this timeout even if the glove does not become stable. 0 disables the timeout.")]
        [Min(0f)] public float restCaptureTimeoutSeconds = 2f;

        [Tooltip("Recapture rest after a stream gap of at least this duration. 0 disables automatic recapture.")]
        [Min(0f)] public float streamGapRecaptureSeconds = 2f;

        [Tooltip("Log bone mapping, stream-gap and rest-pose diagnostics.")]
        public bool logDiagnostics = false;

        struct FingerBone
        {
            public Transform rig;
            public int jointIndex;
            public Quaternion rigRest;
            public Quaternion gloveRest;
            public Quaternion smoothedDelta;
        }

        readonly FingerBone[][] _bonesByHand = new FingerBone[3][];
        readonly bool[] _handReady = new bool[3];
        readonly bool[] _hasRigRest = new bool[3];
        readonly bool[] _hasRestCandidate = new bool[3];
        readonly int[] _stableFrameCounts = new int[3];
        readonly float[] _restCandidateStartTimes = new float[3];
        readonly float[] _lastFrameTimes = new float[3];
        readonly float[] _lastAppliedTimes = new float[3];
        readonly Quaternion[][] _previousCandidateRotations = CreateCandidateRotations();
        MocapOscReceiver _receiver;

        /// <summary>
        /// OpenXR joint index -> Linnra bone suffix ("ThumbProximal" etc.).
        /// The 26 glove joints arrive in XrHandJointEXT order.
        /// </summary>
        static readonly (int Joint, string Bone)[] JointToLinnra =
        {
            (2, "ThumbProximal"),
            (3, "ThumbIntermediate"),
            (4, "ThumbDistal"),
            (7, "IndexProximal"),
            (8, "IndexIntermediate"),
            (9, "IndexDistal"),
            (12, "MiddleProximal"),
            (13, "MiddleIntermediate"),
            (14, "MiddleDistal"),
            (17, "RingProximal"),
            (18, "RingIntermediate"),
            (19, "RingDistal"),
            (22, "LittleProximal"),
            (23, "LittleIntermediate"),
            (24, "LittleDistal"),
        };

        void Awake()
        {
            _receiver = GetComponent<MocapOscReceiver>();
            if (_receiver == null)
            {
                Debug.LogWarning("[FingerBridge] No MocapOscReceiver on this GameObject; glove frames will not be applied.");
                return;
            }

            ResolveHands();
            BuildBoneMap(MocapOscReceiver.LeftHand, leftHandRoot);
            BuildBoneMap(MocapOscReceiver.RightHand, rightHandRoot);

            _lastFrameTimes[MocapOscReceiver.LeftHand] = -1f;
            _lastFrameTimes[MocapOscReceiver.RightHand] = -1f;
            _lastAppliedTimes[MocapOscReceiver.LeftHand] = -1f;
            _lastAppliedTimes[MocapOscReceiver.RightHand] = -1f;
        }

        void OnEnable()
        {
            if (_receiver != null)
            {
                _receiver.KinematicFrameReceived += OnKinematicFrame;
            }
        }

        void OnDisable()
        {
            if (_receiver != null)
            {
                _receiver.KinematicFrameReceived -= OnKinematicFrame;
            }
        }

        [ContextMenu("Recapture Rest Pose")]
        public void RecaptureRestPose()
        {
            ResetHandCapture(MocapOscReceiver.LeftHand, true);
            ResetHandCapture(MocapOscReceiver.RightHand, true);
            _lastFrameTimes[MocapOscReceiver.LeftHand] = -1f;
            _lastFrameTimes[MocapOscReceiver.RightHand] = -1f;
            Debug.Log("[FingerBridge] Rest pose will be recaptured after the gloves are stable.");
        }

        void ResolveHands()
        {
            if (leftHandRoot == null)
            {
                leftHandRoot = FindHandRoot("Hand_L");
            }
            if (rightHandRoot == null)
            {
                rightHandRoot = FindHandRoot("Hand_R");
            }

            if (leftHandRoot == null || rightHandRoot == null)
            {
                Debug.LogWarning("[FingerBridge] Could not resolve Hand_L/Hand_R roots. " +
                                 "Assign leftHandRoot/rightHandRoot manually or place this bridge under the same hierarchy as the rig.");
            }
        }

        static Transform FindHandRoot(string name)
        {
            foreach (var root in FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (root.name == name)
                {
                    return root;
                }
            }

            return null;
        }

        static Transform FindDeepChild(Transform parent, string name)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (child.name == name)
                {
                    return child;
                }
            }

            for (int i = 0; i < parent.childCount; i++)
            {
                Transform found = FindDeepChild(parent.GetChild(i), name);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        void OnKinematicFrame(int handedness, GloveKinematicFrame frame)
        {
            if (!apply || !frame.IsValid ||
                (handedness != MocapOscReceiver.LeftHand && handedness != MocapOscReceiver.RightHand))
            {
                return;
            }

            Transform handRoot = handedness == MocapOscReceiver.LeftHand ? leftHandRoot : rightHandRoot;
            if (handRoot == null)
            {
                return;
            }

            if (_bonesByHand[handedness] == null)
            {
                BuildBoneMap(handedness, handRoot);
            }

            FingerBone[] bones = _bonesByHand[handedness];
            if (bones == null || bones.Length == 0)
            {
                return;
            }

            float previousFrameTime = _lastFrameTimes[handedness];
            bool clockRestarted = previousFrameTime >= 0f && frame.receivedTime < previousFrameTime;
            bool streamGap = streamGapRecaptureSeconds > 0f && previousFrameTime >= 0f &&
                             frame.receivedTime - previousFrameTime >= streamGapRecaptureSeconds;
            if (clockRestarted || streamGap)
            {
                ResetHandCapture(handedness, true);
                if (logDiagnostics)
                {
                    Debug.Log($"[FingerBridge] Stream resumed for hand={handedness}; waiting for a stable rest pose.");
                }
            }
            _lastFrameTimes[handedness] = frame.receivedTime;

            if (!_handReady[handedness])
            {
                UpdateRestCandidate(handedness, frame);
                return;
            }

            float frameDelta = _lastAppliedTimes[handedness] >= 0f
                ? frame.receivedTime - _lastAppliedTimes[handedness]
                : 1f / 60f;
            frameDelta = Mathf.Clamp(frameDelta, 0.0001f, 0.1f);
            float smoothing = smoothingSpeed <= 0f
                ? 1f
                : 1f - Mathf.Exp(-smoothingSpeed * frameDelta);

            for (int i = 0; i < bones.Length; i++)
            {
                FingerBone bone = bones[i];
                if (bone.rig == null)
                {
                    continue;
                }

                Quaternion targetDelta = Quaternion.Inverse(bone.gloveRest) * frame.rotations[bone.jointIndex];
                if (maxDeltaDegrees > 0f)
                {
                    targetDelta = Quaternion.RotateTowards(bone.smoothedDelta, targetDelta, maxDeltaDegrees);
                }

                bone.smoothedDelta = Quaternion.Slerp(bone.smoothedDelta, targetDelta, smoothing);
                bone.rig.localRotation = bone.rigRest * bone.smoothedDelta;
                bones[i] = bone;
            }

            _lastAppliedTimes[handedness] = frame.receivedTime;
        }

        void BuildBoneMap(int handedness, Transform handRoot)
        {
            if (handRoot == null)
            {
                _bonesByHand[handedness] = null;
                return;
            }

            string suffix = handedness == MocapOscReceiver.LeftHand ? "L" : "R";
            FingerBone[] bones = new FingerBone[JointToLinnra.Length];
            int mappedCount = 0;

            for (int j = 0; j < JointToLinnra.Length; j++)
            {
                var (jointIndex, boneName) = JointToLinnra[j];
                Transform rig = FindDeepChild(handRoot, boneName + "_" + suffix);
                if (rig == null)
                {
                    rig = FindDeepChild(handRoot, boneName + suffix);
                }
                if (rig == null)
                {
                    if (logDiagnostics)
                    {
                        Debug.LogWarning("[FingerBridge] Missing bone " + boneName + "_" + suffix + " under " + handRoot.name);
                    }
                    continue;
                }

                bones[j] = new FingerBone
                {
                    rig = rig,
                    jointIndex = jointIndex,
                    rigRest = rig.localRotation,
                    gloveRest = Quaternion.identity,
                    smoothedDelta = Quaternion.identity
                };
                mappedCount++;
            }

            _bonesByHand[handedness] = bones;
            _hasRigRest[handedness] = false;
            if (logDiagnostics)
            {
                Debug.Log($"[FingerBridge] Mapped hand={handedness}: {mappedCount}/{JointToLinnra.Length} bones.");
            }
        }

        void UpdateRestCandidate(int handedness, GloveKinematicFrame frame)
        {
            FingerBone[] bones = _bonesByHand[handedness];
            Quaternion[] previousRotations = _previousCandidateRotations[handedness];

            if (!_hasRestCandidate[handedness])
            {
                CopyMappedRotations(bones, frame, previousRotations);
                _hasRestCandidate[handedness] = true;
                _stableFrameCounts[handedness] = 1;
                _restCandidateStartTimes[handedness] = frame.receivedTime;
            }
            else
            {
                bool stable = true;
                for (int i = 0; i < bones.Length; i++)
                {
                    if (bones[i].rig == null) continue;
                    int jointIndex = bones[i].jointIndex;
                    Quaternion current = frame.rotations[jointIndex];
                    if (Quaternion.Angle(previousRotations[jointIndex], current) > restStabilityDegrees)
                    {
                        stable = false;
                    }
                    previousRotations[jointIndex] = current;
                }
                _stableFrameCounts[handedness] = stable ? _stableFrameCounts[handedness] + 1 : 1;
            }

            bool stableLongEnough = _stableFrameCounts[handedness] >= Mathf.Max(1, restStableFrames);
            bool timedOut = restCaptureTimeoutSeconds > 0f &&
                            frame.receivedTime - _restCandidateStartTimes[handedness] >= restCaptureTimeoutSeconds;
            if (stableLongEnough || timedOut)
            {
                CaptureRest(handedness, frame, timedOut && !stableLongEnough);
            }
        }

        void CaptureRest(int handedness, GloveKinematicFrame frame, bool timedOut)
        {
            FingerBone[] bones = _bonesByHand[handedness];
            int mappedCount = 0;
            for (int i = 0; i < bones.Length; i++)
            {
                FingerBone bone = bones[i];
                if (bone.rig == null) continue;

                if (!_hasRigRest[handedness])
                {
                    // Capture after the Animator has produced a frame, not during Awake.
                    bone.rigRest = bone.rig.localRotation;
                }
                bone.gloveRest = frame.rotations[bone.jointIndex];
                bone.smoothedDelta = Quaternion.identity;
                bone.rig.localRotation = bone.rigRest;
                bones[i] = bone;
                mappedCount++;
            }

            if (mappedCount > 0) _hasRigRest[handedness] = true;
            _handReady[handedness] = mappedCount > 0;
            _hasRestCandidate[handedness] = false;
            _stableFrameCounts[handedness] = 0;
            _lastAppliedTimes[handedness] = frame.receivedTime;

            if (logDiagnostics)
            {
                string reason = timedOut ? "timeout" : "stable";
                Debug.Log($"[FingerBridge] Rest captured for hand={handedness}: {mappedCount} bones ({reason}).");
            }
        }

        void ResetHandCapture(int handedness, bool restoreRigRest)
        {
            _handReady[handedness] = false;
            _hasRestCandidate[handedness] = false;
            _stableFrameCounts[handedness] = 0;
            _lastAppliedTimes[handedness] = -1f;

            FingerBone[] bones = _bonesByHand[handedness];
            if (bones == null) return;

            for (int i = 0; i < bones.Length; i++)
            {
                FingerBone bone = bones[i];
                bone.smoothedDelta = Quaternion.identity;
                if (restoreRigRest && bone.rig != null)
                {
                    bone.rig.localRotation = bone.rigRest;
                }
                bones[i] = bone;
            }
        }

        static void CopyMappedRotations(FingerBone[] bones, GloveKinematicFrame frame, Quaternion[] destination)
        {
            for (int i = 0; i < bones.Length; i++)
            {
                if (bones[i].rig == null) continue;
                int jointIndex = bones[i].jointIndex;
                destination[jointIndex] = frame.rotations[jointIndex];
            }
        }

        static Quaternion[][] CreateCandidateRotations()
        {
            Quaternion[][] rotations = new Quaternion[3][];
            rotations[MocapOscReceiver.LeftHand] = new Quaternion[GloveKinematicFrame.JointCount];
            rotations[MocapOscReceiver.RightHand] = new Quaternion[GloveKinematicFrame.JointCount];
            return rotations;
        }

        void OnValidate()
        {
            maxDeltaDegrees = Mathf.Max(0f, maxDeltaDegrees);
            smoothingSpeed = Mathf.Max(0f, smoothingSpeed);
            restStableFrames = Mathf.Max(1, restStableFrames);
            restStabilityDegrees = Mathf.Max(0f, restStabilityDegrees);
            restCaptureTimeoutSeconds = Mathf.Max(0f, restCaptureTimeoutSeconds);
            streamGapRecaptureSeconds = Mathf.Max(0f, streamGapRecaptureSeconds);
        }

        void OnDestroy()
        {
            ResetHandCapture(MocapOscReceiver.LeftHand, true);
            ResetHandCapture(MocapOscReceiver.RightHand, true);
        }

        void Reset()
        {
            ResolveHands();
        }
    }
}
