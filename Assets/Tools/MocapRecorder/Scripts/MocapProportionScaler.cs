using UnityEngine;

namespace MocapTools
{
    /// <summary>
    /// Runtime component that scales tracker-to-offset-child positions each frame
    /// to match avatar proportions. Created and configured by MocapCalibratorRunner.
    ///
    /// Handles the case where the user's body proportions differ from the avatar's.
    /// Positions are scaled per-limb-chain: head->hip (torso) and hip->feet (legs).
    /// Hands are not scaled (hand trackers are used directly).
    /// </summary>
    [DefaultExecutionOrder(-100)] // Run before VRIK in LateUpdate
    public class MocapProportionScaler : MonoBehaviour
    {
        [Header("Tracker References")]
        public Transform HeadTracker;
        public Transform HipTracker;
        public Transform FootLTracker;
        public Transform FootRTracker;

        [Header("Offset Children (VRIK Targets)")]
        public Transform HipOffset;
        public Transform FootLOffset;
        public Transform FootROffset;

        [Header("Scale Factors")]
        public float TorsoScale = 1f;
        public float LegScaleL = 1f;
        public float LegScaleR = 1f;

        private void LateUpdate()
        {
            if (HeadTracker == null) return;

            Vector3 headPos = HeadTracker.position;

            // Scale hip position: head->hip vector scaled to avatar torso length
            if (HipTracker != null && HipOffset != null)
            {
                Vector3 hipVector = HipTracker.position - headPos;
                HipOffset.position = headPos + hipVector * TorsoScale;
            }

            // Reference positions for feet
            Vector3 hipTrackerPos = (HipTracker != null) ? HipTracker.position : headPos;
            Vector3 scaledHipPos = (HipOffset != null) ? HipOffset.position : hipTrackerPos;

            // Scale left foot position: hip->foot vector scaled to avatar leg length
            if (FootLTracker != null && FootLOffset != null)
            {
                Vector3 footVector = FootLTracker.position - hipTrackerPos;
                FootLOffset.position = scaledHipPos + footVector * LegScaleL;
            }

            // Scale right foot position
            if (FootRTracker != null && FootROffset != null)
            {
                Vector3 footVector = FootRTracker.position - hipTrackerPos;
                FootROffset.position = scaledHipPos + footVector * LegScaleR;
            }
        }
    }
}
