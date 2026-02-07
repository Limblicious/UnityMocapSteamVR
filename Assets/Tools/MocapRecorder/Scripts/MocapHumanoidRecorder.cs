using UnityEngine;
using System.Collections.Generic;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace MocapTools
{
    /// <summary>
    /// Records humanoid muscle curves from a character with an Animator (Humanoid avatar).
    /// Produces retargetable animation clips that work across different humanoid characters.
    /// Records in LateUpdate to capture post-IK transforms.
    /// </summary>
    [DefaultExecutionOrder(10000)] // Run very late to capture post-IK transforms
    public class MocapHumanoidRecorder : MonoBehaviour
    {
#if UNITY_EDITOR
        // Recording state
        private Animator _animator;
        private HumanPoseHandler _poseHandler;
        private HumanPose _humanPose;
        private int _targetFps;
        private int _previousCaptureFramerate;
        private bool _isRecording;
        private float _countdownRemaining;
        private bool _isCountingDown;
        private float _recordingStartTime;
        private bool _recordRootMotion;

        // Curve data
        private Dictionary<int, AnimationCurve> _muscleCurves;
        private AnimationCurve _rootTX, _rootTY, _rootTZ;
        private AnimationCurve _rootQX, _rootQY, _rootQZ, _rootQW;
        private float _currentTime;

        // Initial pose for relative root motion
        private Vector3 _initialRootPosition;
        private Quaternion _initialRootRotation;

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
        public float RecordingDuration => _isRecording ? _currentTime : 0f;

        /// <summary>
        /// Begins recording humanoid muscle curves after a countdown delay.
        /// </summary>
        /// <param name="animator">The Animator component (must have a Humanoid avatar).</param>
        /// <param name="fps">Target framerate for recording (deterministic sampling).</param>
        /// <param name="startDelaySeconds">Countdown before recording starts.</param>
        /// <param name="recordRootMotion">Whether to record RootT/RootQ curves for root motion.</param>
        public void BeginRecordingHumanoid(Animator animator, int fps, float startDelaySeconds, bool recordRootMotion)
        {
            if (_isRecording || _isCountingDown)
            {
                Debug.LogWarning("[MocapHumanoid] Already recording or counting down. Call EndRecording first.");
                return;
            }

            if (animator == null)
            {
                Debug.LogError("[MocapHumanoid] Animator is null. Cannot begin recording.");
                return;
            }

            if (!animator.isHuman)
            {
                Debug.LogError("[MocapHumanoid] Animator does not have a Humanoid avatar. Cannot record muscle curves.");
                return;
            }

            if (animator.avatar == null)
            {
                Debug.LogError("[MocapHumanoid] Animator has no Avatar assigned. Cannot record muscle curves.");
                return;
            }

            if (!Application.isPlaying)
            {
                Debug.LogError("[MocapHumanoid] Recording requires Play Mode. Enter Play Mode first.");
                return;
            }

            _animator = animator;
            _targetFps = Mathf.Max(1, fps);
            _recordRootMotion = recordRootMotion;

            if (startDelaySeconds > 0f)
            {
                _countdownRemaining = startDelaySeconds;
                _isCountingDown = true;
                Debug.Log($"[MocapHumanoid] Countdown started: {startDelaySeconds:F1} seconds...");
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

            // Create the HumanPoseHandler
            _poseHandler = new HumanPoseHandler(_animator.avatar, _animator.transform);
            _humanPose = new HumanPose();

            // Initialize muscle curves
            int muscleCount = HumanTrait.MuscleCount;
            _muscleCurves = new Dictionary<int, AnimationCurve>(muscleCount);
            for (int i = 0; i < muscleCount; i++)
            {
                _muscleCurves[i] = new AnimationCurve();
            }

            // Initialize root motion curves if enabled
            if (_recordRootMotion)
            {
                _rootTX = new AnimationCurve();
                _rootTY = new AnimationCurve();
                _rootTZ = new AnimationCurve();
                _rootQX = new AnimationCurve();
                _rootQY = new AnimationCurve();
                _rootQZ = new AnimationCurve();
                _rootQW = new AnimationCurve();
            }

            // Capture initial pose for relative root motion
            _poseHandler.GetHumanPose(ref _humanPose);
            _initialRootPosition = _humanPose.bodyPosition;
            _initialRootRotation = _humanPose.bodyRotation;

            // Reset time accumulator
            _currentTime = 0f;

            // Take initial snapshot at t=0
            TakeSnapshot();

            _isRecording = true;
            _recordingStartTime = Time.time;

            Debug.Log($"[MocapHumanoid] Recording started at {_targetFps} FPS. " +
                      $"Animator: {_animator.name}, Muscles: {muscleCount}, RootMotion: {_recordRootMotion}");
            OnRecordingStarted?.Invoke();
        }

        private void TakeSnapshot()
        {
            if (_poseHandler == null) return;

            // Get current human pose
            _poseHandler.GetHumanPose(ref _humanPose);

            // Record muscle values
            for (int i = 0; i < HumanTrait.MuscleCount; i++)
            {
                if (_muscleCurves.TryGetValue(i, out AnimationCurve curve))
                {
                    curve.AddKey(_currentTime, _humanPose.muscles[i]);
                }
            }

            // Record root motion if enabled
            if (_recordRootMotion)
            {
                // Calculate relative position (relative to initial position)
                Vector3 relativePos = _humanPose.bodyPosition - _initialRootPosition;

                // Calculate relative rotation (relative to initial rotation)
                Quaternion relativeRot = Quaternion.Inverse(_initialRootRotation) * _humanPose.bodyRotation;

                _rootTX.AddKey(_currentTime, relativePos.x);
                _rootTY.AddKey(_currentTime, relativePos.y);
                _rootTZ.AddKey(_currentTime, relativePos.z);
                _rootQX.AddKey(_currentTime, relativeRot.x);
                _rootQY.AddKey(_currentTime, relativeRot.y);
                _rootQZ.AddKey(_currentTime, relativeRot.z);
                _rootQW.AddKey(_currentTime, relativeRot.w);
            }
        }

        /// <summary>
        /// Ends recording and creates an AnimationClip with the captured humanoid data.
        /// </summary>
        /// <param name="clipName">Name for the new animation clip.</param>
        /// <returns>The created AnimationClip, or null if recording failed.</returns>
        public AnimationClip EndRecordingAndCreateClip(string clipName)
        {
            if (_isCountingDown)
            {
                Debug.Log("[MocapHumanoid] Countdown cancelled.");
                _isCountingDown = false;
                _countdownRemaining = 0f;
                OnRecordingStopped?.Invoke();
                return null;
            }

            if (!_isRecording)
            {
                Debug.LogWarning("[MocapHumanoid] Not currently recording.");
                return null;
            }

            if (_muscleCurves == null || _muscleCurves.Count == 0)
            {
                Debug.LogError("[MocapHumanoid] No muscle data captured. Recording may have failed.");
                _isRecording = false;
                Time.captureFramerate = _previousCaptureFramerate;
                OnRecordingStopped?.Invoke();
                return null;
            }

            // Create the animation clip
            AnimationClip clip = new AnimationClip();
            clip.name = string.IsNullOrEmpty(clipName) ? "HumanoidClip" : clipName;
            clip.frameRate = _targetFps;

            // Write muscle curves
            int muscleCount = HumanTrait.MuscleCount;
            int curvesWritten = 0;

            for (int i = 0; i < muscleCount; i++)
            {
                if (_muscleCurves.TryGetValue(i, out AnimationCurve curve) && curve.keys.Length > 0)
                {
                    string muscleName = HumanTrait.MuscleName[i];
                    clip.SetCurve("", typeof(Animator), muscleName, curve);
                    curvesWritten++;
                }
            }

            // Write root motion curves if enabled
            if (_recordRootMotion)
            {
                if (_rootTX != null && _rootTX.keys.Length > 0)
                {
                    clip.SetCurve("", typeof(Animator), "RootT.x", _rootTX);
                    clip.SetCurve("", typeof(Animator), "RootT.y", _rootTY);
                    clip.SetCurve("", typeof(Animator), "RootT.z", _rootTZ);
                    clip.SetCurve("", typeof(Animator), "RootQ.x", _rootQX);
                    clip.SetCurve("", typeof(Animator), "RootQ.y", _rootQY);
                    clip.SetCurve("", typeof(Animator), "RootQ.z", _rootQZ);
                    clip.SetCurve("", typeof(Animator), "RootQ.w", _rootQW);
                    curvesWritten += 7;
                }
            }

            // Ensure quaternion continuity to avoid rotation glitches
            clip.EnsureQuaternionContinuity();

            // Reset capture framerate
            Time.captureFramerate = _previousCaptureFramerate;

            float duration = _currentTime;
            int frameCount = Mathf.RoundToInt(duration * _targetFps);

            Debug.Log($"[MocapHumanoid] Recording stopped. Duration: {duration:F2}s, ~{frameCount} frames, " +
                      $"Curves: {curvesWritten}, Clip length: {clip.length:F2}s");

            // Cleanup
            Cleanup();

            OnRecordingStopped?.Invoke();

            return clip;
        }

        /// <summary>
        /// Trims the beginning and end of a humanoid animation clip.
        /// Note: Does NOT remove scale curves (humanoid clips don't have them).
        /// </summary>
        /// <param name="clip">The clip to trim.</param>
        /// <param name="trimStartSeconds">Seconds to trim from the beginning.</param>
        /// <param name="trimEndSeconds">Seconds to trim from the end.</param>
        public void TrimClip(AnimationClip clip, float trimStartSeconds, float trimEndSeconds)
        {
            if (clip == null)
            {
                Debug.LogError("[MocapHumanoid] Cannot trim null clip.");
                return;
            }

            float originalLength = clip.length;
            float newLength = originalLength - trimStartSeconds - trimEndSeconds;

            if (newLength <= 0f)
            {
                Debug.LogWarning($"[MocapHumanoid] Trim values ({trimStartSeconds:F2}s + {trimEndSeconds:F2}s) exceed clip length ({originalLength:F2}s). Skipping trim.");
                return;
            }

            // Get all curve bindings
            EditorCurveBinding[] curveBindings = AnimationUtility.GetCurveBindings(clip);
            int trimmedCurves = 0;

            foreach (var binding in curveBindings)
            {
                // Get the curve
                AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, binding);
                if (curve == null || curve.keys.Length == 0) continue;

                // Trim the curve
                AnimationCurve trimmedCurve = TrimCurve(curve, trimStartSeconds, originalLength - trimEndSeconds);

                // Set the trimmed curve back
                AnimationUtility.SetEditorCurve(clip, binding, trimmedCurve);
                trimmedCurves++;
            }

            Debug.Log($"[MocapHumanoid] Clip trimmed: {originalLength:F2}s -> {clip.length:F2}s " +
                      $"(removed {trimStartSeconds:F2}s start, {trimEndSeconds:F2}s end). Processed {trimmedCurves} curves.");
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
            // Ensure we have proper start/end keys
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

        private void Cleanup()
        {
            if (_poseHandler != null)
            {
                _poseHandler.Dispose();
                _poseHandler = null;
            }

            _muscleCurves = null;
            _rootTX = _rootTY = _rootTZ = null;
            _rootQX = _rootQY = _rootQZ = _rootQW = null;
            _isRecording = false;
            _animator = null;
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
            if (_isRecording && _poseHandler != null)
            {
                float deltaTime = _targetFps > 0 ? (1f / _targetFps) : Time.deltaTime;
                _currentTime += deltaTime;
                TakeSnapshot();
            }
        }

        private void OnDestroy()
        {
            // Cleanup if destroyed while recording
            if (_isRecording || _isCountingDown)
            {
                Time.captureFramerate = _previousCaptureFramerate;
                Cleanup();
                Debug.LogWarning("[MocapHumanoid] Recorder destroyed while recording. Recording aborted.");
            }
        }

        private void OnApplicationQuit()
        {
            // Ensure clean shutdown
            if (_isRecording)
            {
                Time.captureFramerate = _previousCaptureFramerate;
                Cleanup();
            }
        }
#else
        // Stub for builds - this component does nothing outside the editor
        public bool IsRecording => false;
        public bool IsCountingDown => false;
        public float CountdownRemaining => 0f;
        public float RecordingDuration => 0f;

        public System.Action<float> OnCountdownTick;
        public System.Action OnRecordingStarted;
        public System.Action OnRecordingStopped;

        public void BeginRecordingHumanoid(Animator animator, int fps, float startDelaySeconds, bool recordRootMotion)
        {
            Debug.LogWarning("[MocapHumanoid] Recording is only available in the Unity Editor.");
        }

        public object EndRecordingAndCreateClip(string clipName)
        {
            Debug.LogWarning("[MocapHumanoid] Recording is only available in the Unity Editor.");
            return null;
        }

        public void TrimClip(object clip, float trimStartSeconds, float trimEndSeconds)
        {
            Debug.LogWarning("[MocapHumanoid] Recording is only available in the Unity Editor.");
        }
#endif
    }
}
