using UnityEngine;
using System.Collections.Generic;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Animations;
#endif

namespace MocapTools
{
    /// <summary>
    /// Records a skeleton transform hierarchy into an AnimationClip using GameObjectRecorder.
    /// Attach to any GameObject in the scene. Records in LateUpdate to capture post-IK transforms.
    /// </summary>
    [DefaultExecutionOrder(10000)] // Run very late to capture post-IK transforms
    public class MocapSkeletonRecorder : MonoBehaviour
    {
#if UNITY_EDITOR
        // Recording state
        private GameObjectRecorder _recorder;
        private Transform _characterRoot;  // Top-level character (Animator root) - recorder is relative to this
        private Transform _boneRoot;       // Bone hierarchy root (e.g., Armature) - only these transforms are recorded
        private int _targetFps;
        private int _previousCaptureFramerate;
        private bool _isRecording;
        private float _countdownRemaining;
        private bool _isCountingDown;
        private float _recordingStartTime;

        // Events for UI feedback
        public System.Action<float> OnCountdownTick;
        public System.Action OnRecordingStarted;
        public System.Action OnRecordingStopped;

        /// <summary>
        /// True if currently recording (not counting down).
        /// </summary>
        public bool IsRecording => _isRecording;

        /// <summary>
        /// True if in countdown phase before recording.
        /// </summary>
        public bool IsCountingDown => _isCountingDown;

        /// <summary>
        /// Remaining countdown time in seconds.
        /// </summary>
        public float CountdownRemaining => _countdownRemaining;

        /// <summary>
        /// Duration of the current recording in seconds.
        /// </summary>
        public float RecordingDuration => _isRecording ? (Time.time - _recordingStartTime) : 0f;

        /// <summary>
        /// Begins recording after a countdown delay.
        /// </summary>
        /// <param name="characterRoot">Top-level character transform (Animator root). Clip paths are relative to this.</param>
        /// <param name="boneRoot">Root of the bone hierarchy to record (e.g., Armature). Only transforms under this are recorded.</param>
        /// <param name="fps">Target framerate for recording (deterministic sampling).</param>
        /// <param name="startDelaySeconds">Countdown before recording starts.</param>
        public void BeginRecording(Transform characterRoot, Transform boneRoot, int fps, float startDelaySeconds)
        {
            if (_isRecording || _isCountingDown)
            {
                Debug.LogWarning("[MocapRecorder] Already recording or counting down. Call EndRecording first.");
                return;
            }

            if (characterRoot == null)
            {
                Debug.LogError("[MocapRecorder] Character root is null. Cannot begin recording.");
                return;
            }

            if (boneRoot == null)
            {
                Debug.LogError("[MocapRecorder] Bone root is null. Cannot begin recording.");
                return;
            }

            if (!Application.isPlaying)
            {
                Debug.LogError("[MocapRecorder] Recording requires Play Mode. Enter Play Mode first.");
                return;
            }

            _characterRoot = characterRoot;
            _boneRoot = boneRoot;
            _targetFps = Mathf.Max(1, fps);

            if (startDelaySeconds > 0f)
            {
                _countdownRemaining = startDelaySeconds;
                _isCountingDown = true;
                Debug.Log($"[MocapRecorder] Countdown started: {startDelaySeconds:F1} seconds...");
            }
            else
            {
                StartRecordingInternal();
            }
        }

        private void StartRecordingInternal()
        {
            _isCountingDown = false;

            // Store and set capture framerate for deterministic sampling
            _previousCaptureFramerate = Time.captureFramerate;
            Time.captureFramerate = _targetFps;

            // Create the GameObjectRecorder relative to CHARACTER ROOT
            // This ensures curve paths include "Armature/Hips/..." instead of just "Hips/..."
            _recorder = new GameObjectRecorder(_characterRoot.gameObject);

            // Bind ONLY transforms under BONE ROOT (not tracker objects or other children)
            BindTransformsRecursive(_boneRoot);

            // Take initial snapshot at t=0 for clean first keyframe
            _recorder.TakeSnapshot(0f);

            _isRecording = true;
            _recordingStartTime = Time.time;

            Debug.Log($"[MocapRecorder] Recording started at {_targetFps} FPS. " +
                      $"Character: {_characterRoot.name}, Bones: {_boneRoot.name}");
            OnRecordingStarted?.Invoke();
        }

        private void BindTransformsRecursive(Transform root)
        {
            if (root == null || _recorder == null) return;

            // Bind this transform
            _recorder.BindComponentsOfType<Transform>(root.gameObject, recursive: false);

            // Recursively bind children
            foreach (Transform child in root)
            {
                BindTransformsRecursive(child);
            }
        }

        /// <summary>
        /// Ends recording and creates an AnimationClip with the captured data.
        /// </summary>
        /// <param name="clipName">Name for the new animation clip.</param>
        /// <returns>The created AnimationClip, or null if recording failed.</returns>
        public AnimationClip EndRecordingAndCreateClip(string clipName)
        {
            if (_isCountingDown)
            {
                Debug.Log("[MocapRecorder] Countdown cancelled.");
                _isCountingDown = false;
                _countdownRemaining = 0f;
                OnRecordingStopped?.Invoke();
                return null;
            }

            if (!_isRecording)
            {
                Debug.LogWarning("[MocapRecorder] Not currently recording.");
                return null;
            }

            if (_recorder == null)
            {
                Debug.LogError("[MocapRecorder] Recorder is null. Recording may have failed.");
                _isRecording = false;
                Time.captureFramerate = _previousCaptureFramerate;
                OnRecordingStopped?.Invoke();
                return null;
            }

            // Create the animation clip
            AnimationClip clip = new AnimationClip();
            clip.name = string.IsNullOrEmpty(clipName) ? "RecordedClip" : clipName;
            clip.frameRate = _targetFps;

            // Save recorded data to clip
            _recorder.SaveToClip(clip);

            // Ensure quaternion continuity to avoid rotation glitches
            clip.EnsureQuaternionContinuity();

            // Reset capture framerate
            Time.captureFramerate = _previousCaptureFramerate;

            float duration = Time.time - _recordingStartTime;
            int frameCount = Mathf.RoundToInt(duration * _targetFps);

            Debug.Log($"[MocapRecorder] Recording stopped. Duration: {duration:F2}s, ~{frameCount} frames, Clip length: {clip.length:F2}s");

            // Cleanup
            _recorder = null;
            _isRecording = false;
            _characterRoot = null;
            _boneRoot = null;

            OnRecordingStopped?.Invoke();

            return clip;
        }

        /// <summary>
        /// Trims the beginning and end of an animation clip and removes scale curves.
        /// </summary>
        /// <param name="clip">The clip to trim.</param>
        /// <param name="trimStartSeconds">Seconds to trim from the beginning.</param>
        /// <param name="trimEndSeconds">Seconds to trim from the end.</param>
        public void TrimClip(AnimationClip clip, float trimStartSeconds, float trimEndSeconds)
        {
            if (clip == null)
            {
                Debug.LogError("[MocapRecorder] Cannot trim null clip.");
                return;
            }

            float originalLength = clip.length;
            float newLength = originalLength - trimStartSeconds - trimEndSeconds;

            if (newLength <= 0f)
            {
                Debug.LogWarning($"[MocapRecorder] Trim values ({trimStartSeconds:F2}s + {trimEndSeconds:F2}s) exceed clip length ({originalLength:F2}s). Skipping trim.");
                return;
            }

            // Get all curve bindings
            EditorCurveBinding[] curveBindings = AnimationUtility.GetCurveBindings(clip);
            List<EditorCurveBinding> bindingsToRemove = new List<EditorCurveBinding>();
            int trimmedCurves = 0;
            int removedScaleCurves = 0;

            foreach (var binding in curveBindings)
            {
                // Remove scale curves (localScale.x, localScale.y, localScale.z)
                if (binding.propertyName.StartsWith("m_LocalScale"))
                {
                    bindingsToRemove.Add(binding);
                    removedScaleCurves++;
                    continue;
                }

                // Get the curve
                AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, binding);
                if (curve == null || curve.keys.Length == 0) continue;

                // Trim the curve
                AnimationCurve trimmedCurve = TrimCurve(curve, trimStartSeconds, originalLength - trimEndSeconds);

                // Set the trimmed curve back
                AnimationUtility.SetEditorCurve(clip, binding, trimmedCurve);
                trimmedCurves++;
            }

            // Remove scale curves
            foreach (var binding in bindingsToRemove)
            {
                AnimationUtility.SetEditorCurve(clip, binding, null);
            }

            // Also handle object reference curves (if any)
            EditorCurveBinding[] objectBindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
            foreach (var binding in objectBindings)
            {
                if (binding.propertyName.StartsWith("m_LocalScale"))
                {
                    AnimationUtility.SetObjectReferenceCurve(clip, binding, null);
                }
            }

            Debug.Log($"[MocapRecorder] Clip trimmed: {originalLength:F2}s -> {clip.length:F2}s (removed {trimStartSeconds:F2}s start, {trimEndSeconds:F2}s end). " +
                      $"Processed {trimmedCurves} curves, removed {removedScaleCurves} scale curves.");
        }

        private AnimationCurve TrimCurve(AnimationCurve original, float trimStart, float trimEnd)
        {
            List<Keyframe> newKeys = new List<Keyframe>();

            foreach (var key in original.keys)
            {
                // Skip keys outside the trim range
                if (key.time < trimStart || key.time > trimEnd)
                    continue;

                // Create new key with shifted time
                Keyframe newKey = new Keyframe(
                    key.time - trimStart,  // Shift time to start at 0
                    key.value,
                    key.inTangent,
                    key.outTangent,
                    key.inWeight,
                    key.outWeight
                );
                newKey.weightedMode = key.weightedMode;
                newKeys.Add(newKey);
            }

            // If we lost all keys, try to interpolate values at trim boundaries
            if (newKeys.Count == 0 && original.keys.Length > 0)
            {
                float startValue = original.Evaluate(trimStart);
                float endValue = original.Evaluate(trimEnd);
                newKeys.Add(new Keyframe(0f, startValue));
                if (trimEnd > trimStart)
                {
                    newKeys.Add(new Keyframe(trimEnd - trimStart, endValue));
                }
            }
            // If we only have one key or lost boundary keys, ensure we have proper start/end
            else if (newKeys.Count > 0)
            {
                // Ensure we have a key at time 0
                if (newKeys[0].time > 0.001f)
                {
                    float startValue = original.Evaluate(trimStart);
                    newKeys.Insert(0, new Keyframe(0f, startValue));
                }

                // Ensure we have a key at the end
                float expectedEnd = trimEnd - trimStart;
                if (newKeys[newKeys.Count - 1].time < expectedEnd - 0.001f)
                {
                    float endValue = original.Evaluate(trimEnd);
                    newKeys.Add(new Keyframe(expectedEnd, endValue));
                }
            }

            AnimationCurve result = new AnimationCurve(newKeys.ToArray());

            // Copy pre/post wrap modes
            result.preWrapMode = original.preWrapMode;
            result.postWrapMode = original.postWrapMode;

            return result;
        }

        private void LateUpdate()
        {
            // Handle countdown
            if (_isCountingDown)
            {
                _countdownRemaining -= Time.deltaTime;
                OnCountdownTick?.Invoke(_countdownRemaining);

                if (_countdownRemaining <= 0f)
                {
                    StartRecordingInternal();
                }
                return;
            }

            // Record frame with deterministic time step
            if (_isRecording && _recorder != null)
            {
                float deltaTime = _targetFps > 0 ? (1f / _targetFps) : Time.deltaTime;
                _recorder.TakeSnapshot(deltaTime);
            }
        }

        private void OnDestroy()
        {
            // Cleanup if destroyed while recording
            if (_isRecording || _isCountingDown)
            {
                Time.captureFramerate = _previousCaptureFramerate;
                _isRecording = false;
                _isCountingDown = false;
                Debug.LogWarning("[MocapRecorder] Recorder destroyed while recording. Recording aborted.");
            }
        }

        private void OnApplicationQuit()
        {
            // Ensure clean shutdown
            if (_isRecording)
            {
                Time.captureFramerate = _previousCaptureFramerate;
            }
        }
#else
        // Stub for builds - this component does nothing outside the editor
        public bool IsRecording => false;
        public bool IsCountingDown => false;
        public float CountdownRemaining => 0f;
        public float RecordingDuration => 0f;

        public void BeginRecording(Transform characterRoot, Transform boneRoot, int fps, float startDelaySeconds)
        {
            Debug.LogWarning("[MocapRecorder] Recording is only available in the Unity Editor.");
        }

        public object EndRecordingAndCreateClip(string clipName)
        {
            Debug.LogWarning("[MocapRecorder] Recording is only available in the Unity Editor.");
            return null;
        }

        public void TrimClip(object clip, float trimStartSeconds, float trimEndSeconds)
        {
            Debug.LogWarning("[MocapRecorder] Recording is only available in the Unity Editor.");
        }
#endif
    }
}
