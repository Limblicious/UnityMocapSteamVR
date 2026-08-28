using UnityEngine;

namespace MocapTools
{
    /// <summary>
    /// Runtime component that positions VRIK target offset children each frame
    /// based on live tracker positions, with optional per-limb proportion scaling.
    ///
    /// Hip target:
    ///   Follows HipTracker directly every frame. Head position has NO influence
    ///   over hip placement. The old head-anchored torso-scaling approach caused
    ///   the hip VRIK target to whip in an arc whenever the head moved or rotated.
    ///   With a dedicated hip tracker (e.g. Vive Tracker on pelvis), the tracker
    ///   is the ground truth — use it directly.
    ///
    /// Foot targets:
    ///   Scale the hip->foot vector by LegScale, anchored from the raw HipTracker
    ///   world position. If LegScale = 1.0 (user and avatar have matching leg lengths),
    ///   feet follow trackers exactly. Scale > 1.0 when avatar legs are longer than
    ///   the user's; scale < 1.0 when shorter.
    ///
    /// Hands are not handled here — hand controllers are used directly as VRIK targets.
    /// </summary>
    [DefaultExecutionOrder(-100)] // Run before VRIK in LateUpdate
    public class MocapProportionScaler : MonoBehaviour
    {
        [Header("Tracker References")]
        [Tooltip("Dedicated hip/pelvis tracker. Hip VRIK target follows this directly.")]
        public Transform HipTracker;
        [Tooltip("Left foot tracker.")]
        public Transform FootLTracker;
        [Tooltip("Right foot tracker.")]
        public Transform FootRTracker;

        [Header("Offset Children (VRIK Targets)")]
        public Transform HipOffset;
        public Transform FootLOffset;
        public Transform FootROffset;

        [Header("Leg Scale Factors")]
        [Tooltip("Scales the hip->left foot vector. 1.0 = tracker direct. " +
                 "Increase if avatar left leg is longer than yours; decrease if shorter.")]
        public float LegScaleL = 1f;
        [Tooltip("Scales the hip->right foot vector. 1.0 = tracker direct.")]
        public float LegScaleR = 1f;

        private void LateUpdate()
        {
            // Hip: follow the dedicated tracker directly.
            // Head position intentionally NOT used here — it caused hip whipping.
            if (HipTracker != null && HipOffset != null)
            {
                HipOffset.position = HipTracker.position;
            }

            // Feet: scale hip->foot vector from the raw hip tracker world position.
            // Anchoring from HipTracker (not from any head-derived position) keeps
            // feet stable even during aggressive head movement.
            Vector3 hipAnchorPos = (HipTracker != null) ? HipTracker.position : Vector3.zero;

            if (FootLTracker != null && FootLOffset != null)
            {
                Vector3 footVector = FootLTracker.position - hipAnchorPos;
                FootLOffset.position = hipAnchorPos + footVector * LegScaleL;
            }

            if (FootRTracker != null && FootROffset != null)
            {
                Vector3 footVector = FootRTracker.position - hipAnchorPos;
                FootROffset.position = hipAnchorPos + footVector * LegScaleR;
            }
        }
    }
}
