using System;
using System.Collections.Generic;
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
    /// a rest pose per bone (glove rest = first received frame, rig rest =
    /// current local rotations) and applies only the delta from rest:
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

        [Tooltip("Maximum per-joint delta rotation per frame (deg), guards against bad frames.")]
        public float maxDeltaDegrees = 40f;

        [Tooltip("Log first-frame and rest-pose diagnostics.")]
        public bool logDiagnostics = true;

        struct FingerBone
        {
            public Transform rig;
            public int handedness;
            public int jointIndex;
            public Quaternion rigRest;
            public Quaternion gloveRest;
        }

        readonly List<FingerBone> _bones = new List<FingerBone>();
        readonly bool[] _handReady = new bool[3];
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
            _handReady[MocapOscReceiver.LeftHand] = false;
            _handReady[MocapOscReceiver.RightHand] = false;
            Debug.Log("[FingerBridge] Rest pose will be recaptured on next frames.");
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
            if (!apply || !frame.IsValid)
            {
                return;
            }

            Transform handRoot = handedness == MocapOscReceiver.LeftHand ? leftHandRoot : rightHandRoot;
            if (handRoot == null)
            {
                return;
            }

            if (!_handReady[handedness])
            {
                CaptureRest(handedness, handRoot, frame);
                return;
            }

            for (int i = 0; i < _bones.Count; i++)
            {
                FingerBone bone = _bones[i];
                if (bone.handedness != handedness || bone.rig == null)
                {
                    continue;
                }

                Quaternion delta = Quaternion.Inverse(bone.gloveRest) * frame.rotations[bone.jointIndex];
                if (maxDeltaDegrees > 0f && Quaternion.Angle(Quaternion.identity, delta) > maxDeltaDegrees)
                {
                    continue;
                }

                bone.rig.localRotation = bone.rigRest * delta;
                _bones[i] = bone;
            }
        }

        void CaptureRest(int handedness, Transform handRoot, GloveKinematicFrame frame)
        {
            _handReady[handedness] = true;
            string suffix = handedness == MocapOscReceiver.LeftHand ? "L" : "R";

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

                _bones.Add(new FingerBone
                {
                    handedness = handedness,
                    rig = rig,
                    jointIndex = jointIndex,
                    rigRest = rig.localRotation,
                    gloveRest = frame.rotations[jointIndex]
                });
            }

            if (logDiagnostics)
            {
                Debug.Log($"[FingerBridge] Rest captured for hand={handedness}: {_bones.Count} bones mapped.");
            }
        }
    }
}
