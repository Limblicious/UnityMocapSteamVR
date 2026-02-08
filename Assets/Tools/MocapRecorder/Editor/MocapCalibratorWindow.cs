using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

namespace MocapTools
{
    /// <summary>
    /// Editor window for T-Pose calibration of VRIK mocap setups.
    /// Access via Tools > Mocap > Calibrate
    ///
    /// USAGE:
    /// 1. Enter Play Mode
    /// 2. Open this window (Tools > Mocap > Calibrate)
    /// 3. Assign your Character Root (top-level GameObject with Animator + VRIK)
    /// 4. Assign Tracking Root (parent of Tracked_Head, Tracked_HandL, etc.)
    /// 5. Click "Calibrate (5s T-Pose)" and hold a T-pose in real life
    /// 6. After completion, tracker offsets are applied so VRIK targets align with avatar bones
    ///
    /// The tool creates *_Off child transforms under each tracked object if they don't exist.
    /// These offset children should be used as VRIK targets instead of the raw tracked objects.
    /// </summary>
    public class MocapCalibratorWindow : EditorWindow
    {
        // References
        private Transform _characterRoot;
        private Transform _trackingRoot;

        // Settings
        private float _countdownSeconds = 5f;
        private float _sampleDurationSeconds = 0.5f;
        private bool _enableVRIKAfterCalibration = true;
        private bool _freezeAnimatorDuringCalibration = false;
        private bool _applyRotationForHeadHandsFeetPelvis = true;
        private bool _applyRotationForFeet = true;

        // UI State
        private Vector2 _scrollPosition;
        private bool _showAdvancedSettings = false;
        private bool _showMappingPreview = true;
        private List<MocapCalibratorRunner.TrackerMapping> _previewMappings;

        // Runtime
        private MocapCalibratorRunner _runner;

        // Styles
        private GUIStyle _headerStyle;
        private GUIStyle _statusStyle;
        private GUIStyle _countdownStyle;
        private bool _stylesInitialized;

        // Constants
        private const string RUNNER_OBJECT_NAME = "_MocapCalibrator";
        private const string PREFS_COUNTDOWN = "MocapCalibrator_Countdown";
        private const string PREFS_SAMPLE_DURATION = "MocapCalibrator_SampleDuration";
        private const string PREFS_DISABLE_VRIK = "MocapCalibrator_DisableVRIK";
        private const string PREFS_FREEZE_ANIMATOR = "MocapCalibrator_FreezeAnimator";
        private const string PREFS_APPLY_ROT = "MocapCalibrator_ApplyRot";
        private const string PREFS_APPLY_ROT_FEET = "MocapCalibrator_ApplyRotFeet";

        [MenuItem("Tools/Mocap/Calibrate")]
        public static void ShowWindow()
        {
            var window = GetWindow<MocapCalibratorWindow>("Mocap Calibrator");
            window.minSize = new Vector2(380, 500);
            window.Show();
        }

        private void OnEnable()
        {
            LoadPreferences();
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.update += OnEditorUpdate;

            // Try to find existing runner
            if (Application.isPlaying)
            {
                FindOrCreateRunner();
            }
        }

        private void OnDisable()
        {
            SavePreferences();
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.update -= OnEditorUpdate;

            // Unsubscribe from runner events
            if (_runner != null)
            {
                _runner.OnStateChanged -= OnRunnerStateChanged;
                _runner.OnCountdownTick -= OnRunnerCountdownTick;
                _runner.OnCalibrationComplete -= OnRunnerCalibrationComplete;
            }
        }

        private void InitializeStyles()
        {
            if (_stylesInitialized) return;

            _headerStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 14,
                margin = new RectOffset(0, 0, 10, 5)
            };

            _statusStyle = new GUIStyle(EditorStyles.helpBox)
            {
                fontSize = 12,
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(10, 10, 10, 10)
            };

            _countdownStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 32,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(1f, 0.5f, 0f) }
            };

            _stylesInitialized = true;
        }

        private void OnGUI()
        {
            InitializeStyles();

            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);

            DrawHeader();
            EditorGUILayout.Space(10);

            DrawStatusSection();
            EditorGUILayout.Space(10);

            DrawSetupSection();
            EditorGUILayout.Space(10);

            DrawSettingsSection();
            EditorGUILayout.Space(10);

            DrawMappingPreview();
            EditorGUILayout.Space(10);

            DrawActionButtons();
            EditorGUILayout.Space(10);

            DrawResultsSection();

            EditorGUILayout.EndScrollView();
        }

        private void DrawHeader()
        {
            EditorGUILayout.LabelField("T-Pose Calibrator", _headerStyle);
            EditorGUILayout.HelpBox(
                "Calibrates VRIK tracker offsets by matching to avatar bones during T-pose.\n\n" +
                "1. Enter Play Mode with VR headset active\n" +
                "2. Assign Character Root and Tracking Root\n" +
                "3. Click 'Calibrate' and hold a T-pose for 5 seconds\n" +
                "4. Offsets are computed and applied to *_Off child transforms\n\n" +
                "Your VRIK targets should point to the *_Off transforms, not raw trackers.",
                MessageType.Info);
        }

        private void DrawStatusSection()
        {
            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox(
                    "Enter Play Mode to use the calibrator.\n" +
                    "VR tracking must be active.",
                    MessageType.Warning);
                return;
            }

            if (_runner == null)
            {
                EditorGUILayout.HelpBox("Calibrator not initialized. Click a button to initialize.", MessageType.None);
                return;
            }

            // Show current state
            var state = _runner.State;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            switch (state)
            {
                case MocapCalibratorRunner.CalibrationState.Idle:
                    EditorGUILayout.LabelField("Ready to calibrate.", EditorStyles.centeredGreyMiniLabel);
                    break;

                case MocapCalibratorRunner.CalibrationState.CountingDown:
                    GUI.backgroundColor = new Color(1f, 0.8f, 0.3f);
                    EditorGUILayout.LabelField($"{_runner.CountdownRemaining:F1}", _countdownStyle);
                    EditorGUILayout.LabelField("HOLD T-POSE", EditorStyles.centeredGreyMiniLabel);
                    GUI.backgroundColor = Color.white;
                    break;

                case MocapCalibratorRunner.CalibrationState.Sampling:
                    GUI.backgroundColor = new Color(0.3f, 0.8f, 1f);
                    EditorGUILayout.LabelField($"Sampling: {_runner.SamplingProgress * 100f:F0}%", _statusStyle);
                    EditorGUILayout.LabelField("Keep holding T-pose...", EditorStyles.centeredGreyMiniLabel);
                    GUI.backgroundColor = Color.white;
                    break;

                case MocapCalibratorRunner.CalibrationState.Applying:
                    EditorGUILayout.LabelField("Applying offsets...", _statusStyle);
                    break;

                case MocapCalibratorRunner.CalibrationState.Completed:
                    GUI.backgroundColor = new Color(0.3f, 0.9f, 0.3f);
                    EditorGUILayout.LabelField("✓ Calibration Complete", _statusStyle);
                    GUI.backgroundColor = Color.white;
                    break;

                case MocapCalibratorRunner.CalibrationState.Failed:
                    GUI.backgroundColor = new Color(0.9f, 0.3f, 0.3f);
                    EditorGUILayout.LabelField("✗ Calibration Failed", _statusStyle);
                    EditorGUILayout.LabelField(_runner.StatusMessage, EditorStyles.wordWrappedMiniLabel);
                    GUI.backgroundColor = Color.white;
                    break;

                case MocapCalibratorRunner.CalibrationState.Cancelled:
                    EditorGUILayout.LabelField("Calibration cancelled.", EditorStyles.centeredGreyMiniLabel);
                    break;
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawSetupSection()
        {
            EditorGUILayout.LabelField("Setup", EditorStyles.boldLabel);

            // Character Root
            EditorGUI.BeginChangeCheck();
            _characterRoot = (Transform)EditorGUILayout.ObjectField(
                new GUIContent("Character Root",
                    "Top-level GameObject with Animator and VRIK.\n" +
                    "The Animator must be configured as Humanoid."),
                _characterRoot,
                typeof(Transform),
                true);

            if (EditorGUI.EndChangeCheck())
            {
                UpdateMappingPreview();
            }

            // Show character info
            if (_characterRoot != null)
            {
                EditorGUI.indentLevel++;

                var animator = _characterRoot.GetComponentInChildren<Animator>();
                if (animator == null)
                {
                    EditorGUILayout.HelpBox("No Animator found on character.", MessageType.Error);
                }
                else if (!animator.isHuman)
                {
                    EditorGUILayout.HelpBox("Animator is not Humanoid.", MessageType.Error);
                }
                else
                {
                    EditorGUILayout.LabelField($"Animator: {animator.name} (Humanoid)");
                }

                // Check for VRIK
                var vrik = FindVRIKComponent(_characterRoot);
                if (vrik == null)
                {
                    EditorGUILayout.HelpBox("No VRIK component found.", MessageType.Warning);
                }
                else
                {
                    EditorGUILayout.LabelField($"VRIK: {vrik.name}");
                }

                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(5);

            // Tracking Root
            EditorGUI.BeginChangeCheck();
            _trackingRoot = (Transform)EditorGUILayout.ObjectField(
                new GUIContent("Tracking Root",
                    "Parent object containing tracked transforms:\n" +
                    "Tracked_Head, Tracked_HandL, Tracked_HandR, etc.\n\n" +
                    "Leave empty to search the entire scene."),
                _trackingRoot,
                typeof(Transform),
                true);

            if (EditorGUI.EndChangeCheck())
            {
                UpdateMappingPreview();
            }

            // Auto-find tracking root button
            if (_trackingRoot == null)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("", GUILayout.Width(EditorGUIUtility.labelWidth));
                if (GUILayout.Button("Auto-Find TrackingRoot"))
                {
                    AutoFindTrackingRoot();
                }
                EditorGUILayout.EndHorizontal();
            }
        }

        private void DrawSettingsSection()
        {
            _showAdvancedSettings = EditorGUILayout.Foldout(_showAdvancedSettings, "Settings", true);

            if (!_showAdvancedSettings) return;

            EditorGUI.indentLevel++;

            _countdownSeconds = EditorGUILayout.Slider(
                new GUIContent("Countdown (seconds)", "Time to prepare and hold T-pose before sampling."),
                _countdownSeconds, 1f, 10f);

            _sampleDurationSeconds = EditorGUILayout.Slider(
                new GUIContent("Sample Duration (seconds)", "Duration of pose sampling after countdown."),
                _sampleDurationSeconds, 0.1f, 2f);

            EditorGUILayout.Space(5);

            _enableVRIKAfterCalibration = EditorGUILayout.Toggle(
                new GUIContent("Enable VRIK After Calibration",
                    "Automatically enable VRIK after calibration completes.\n" +
                    "VRIK should already be disabled before starting calibration.\n" +
                    "Recommended: ON"),
                _enableVRIKAfterCalibration);

            _freezeAnimatorDuringCalibration = EditorGUILayout.Toggle(
                new GUIContent("Freeze Animator During Calibration",
                    "Disable Animator to prevent any animation.\n" +
                    "Usually not needed if VRIK is disabled."),
                _freezeAnimatorDuringCalibration);

            EditorGUILayout.Space(5);

            _applyRotationForHeadHandsFeetPelvis = EditorGUILayout.Toggle(
                new GUIContent("Apply Rotation Offsets",
                    "Apply rotation offsets for head, hands, pelvis.\n" +
                    "Recommended: ON for better alignment."),
                _applyRotationForHeadHandsFeetPelvis);

            _applyRotationForFeet = EditorGUILayout.Toggle(
                new GUIContent("Apply Foot Rotation",
                    "Apply rotation offsets for feet.\n" +
                    "Turn OFF if feet orientation looks wrong after calibration."),
                _applyRotationForFeet);

            EditorGUI.indentLevel--;
        }

        private void DrawMappingPreview()
        {
            _showMappingPreview = EditorGUILayout.Foldout(_showMappingPreview, "Tracker Mappings", true);

            if (!_showMappingPreview) return;

            if (_previewMappings == null || _previewMappings.Count == 0)
            {
                EditorGUILayout.HelpBox("Assign Character Root to see mapping status.", MessageType.None);
                return;
            }

            EditorGUI.indentLevel++;

            foreach (var mapping in _previewMappings)
            {
                EditorGUILayout.BeginHorizontal();

                // Status icon
                if (mapping.TrackerTransform != null && mapping.BoneTransform != null)
                {
                    GUI.color = Color.green;
                    EditorGUILayout.LabelField("✓", GUILayout.Width(20));
                }
                else if (mapping.TrackerTransform != null || mapping.BoneTransform != null)
                {
                    GUI.color = Color.yellow;
                    EditorGUILayout.LabelField("⚠", GUILayout.Width(20));
                }
                else
                {
                    GUI.color = Color.red;
                    EditorGUILayout.LabelField("✗", GUILayout.Width(20));
                }
                GUI.color = Color.white;

                // Mapping info
                string trackerStatus = mapping.TrackerTransform != null ? "Found" : "Missing";
                string boneStatus = mapping.BoneTransform != null ? "Found" : "Missing";

                EditorGUILayout.LabelField($"{mapping.TrackerName} → {mapping.Bone}");

                EditorGUILayout.EndHorizontal();

                // Show details if something is missing
                if (mapping.TrackerTransform == null || mapping.BoneTransform == null)
                {
                    EditorGUI.indentLevel++;
                    if (mapping.TrackerTransform == null)
                    {
                        EditorGUILayout.LabelField($"Tracker: {trackerStatus}", EditorStyles.miniLabel);
                    }
                    if (mapping.BoneTransform == null)
                    {
                        EditorGUILayout.LabelField($"Bone: {boneStatus}", EditorStyles.miniLabel);
                    }
                    EditorGUI.indentLevel--;
                }
            }

            EditorGUI.indentLevel--;

            // Refresh button
            if (GUILayout.Button("Refresh Mappings"))
            {
                UpdateMappingPreview();
            }
        }

        private void DrawActionButtons()
        {
            EditorGUILayout.LabelField("Actions", EditorStyles.boldLabel);

            bool canCalibrate = Application.isPlaying && _characterRoot != null;
            bool isCalibrating = _runner != null &&
                (_runner.State == MocapCalibratorRunner.CalibrationState.CountingDown ||
                 _runner.State == MocapCalibratorRunner.CalibrationState.Sampling);

            EditorGUILayout.BeginHorizontal();

            // Calibrate button
            using (new EditorGUI.DisabledGroupScope(!canCalibrate || isCalibrating))
            {
                GUI.backgroundColor = new Color(0.3f, 0.8f, 0.3f);
                if (GUILayout.Button($"Calibrate ({_countdownSeconds:F0}s T-Pose)", GUILayout.Height(40)))
                {
                    StartCalibration();
                }
                GUI.backgroundColor = Color.white;
            }

            // Cancel button
            using (new EditorGUI.DisabledGroupScope(!isCalibrating))
            {
                GUI.backgroundColor = new Color(0.9f, 0.5f, 0.3f);
                if (GUILayout.Button("Cancel", GUILayout.Height(40), GUILayout.Width(80)))
                {
                    CancelCalibration();
                }
                GUI.backgroundColor = Color.white;
            }

            EditorGUILayout.EndHorizontal();

            // Validation messages
            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox("Enter Play Mode to calibrate.", MessageType.Warning);
            }
            else if (_characterRoot == null)
            {
                EditorGUILayout.HelpBox("Assign Character Root to calibrate.", MessageType.Warning);
            }
        }

        private void DrawResultsSection()
        {
            if (_runner == null || _runner.Results == null || _runner.Results.Count == 0)
                return;

            if (_runner.State != MocapCalibratorRunner.CalibrationState.Completed &&
                _runner.State != MocapCalibratorRunner.CalibrationState.Failed)
                return;

            EditorGUILayout.LabelField("Results", EditorStyles.boldLabel);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            int successCount = 0;
            int failCount = 0;

            foreach (var result in _runner.Results)
            {
                EditorGUILayout.BeginHorizontal();

                if (result.Success)
                {
                    successCount++;
                    GUI.color = Color.green;
                    EditorGUILayout.LabelField("✓", GUILayout.Width(20));
                    GUI.color = Color.white;

                    EditorGUILayout.LabelField(result.TrackerName);

                    // Show applied offset
                    EditorGUILayout.LabelField(
                        $"pos: {result.AppliedLocalPosition.x:F3}, {result.AppliedLocalPosition.y:F3}, {result.AppliedLocalPosition.z:F3}",
                        EditorStyles.miniLabel);
                }
                else
                {
                    failCount++;
                    GUI.color = Color.red;
                    EditorGUILayout.LabelField("✗", GUILayout.Width(20));
                    GUI.color = Color.white;

                    EditorGUILayout.LabelField($"{result.TrackerName}: {result.ErrorMessage}");
                }

                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.Space(5);
            EditorGUILayout.LabelField($"Success: {successCount} / Failed: {failCount}", EditorStyles.centeredGreyMiniLabel);

            EditorGUILayout.EndVertical();
        }

        #region Calibration Control

        private void StartCalibration()
        {
            FindOrCreateRunner();

            if (_runner == null)
            {
                Debug.LogError("[MocapCalibrator] Failed to create calibrator runner.");
                return;
            }

            var request = new MocapCalibratorRunner.CalibrationRequest
            {
                CharacterRoot = _characterRoot,
                TrackingRoot = _trackingRoot,
                CountdownSeconds = _countdownSeconds,
                SampleDurationSeconds = _sampleDurationSeconds,
                EnableVRIKAfterCalibration = _enableVRIKAfterCalibration,
                FreezeAnimatorDuringCalibration = _freezeAnimatorDuringCalibration,
                ApplyRotationForHeadHandsFeetPelvis = _applyRotationForHeadHandsFeetPelvis,
                ApplyRotationForFeet = _applyRotationForFeet
            };

            _runner.StartCalibration(request);
        }

        private void CancelCalibration()
        {
            if (_runner != null)
            {
                _runner.CancelCalibration();
            }
        }

        private void FindOrCreateRunner()
        {
            if (_runner != null) return;

            // Find existing
            _runner = Object.FindObjectOfType<MocapCalibratorRunner>();

            if (_runner == null)
            {
                // Create new
                var go = new GameObject(RUNNER_OBJECT_NAME);
                go.hideFlags = HideFlags.HideInHierarchy;
                _runner = go.AddComponent<MocapCalibratorRunner>();
                Debug.Log("[MocapCalibrator] Created calibrator runner.");
            }

            // Subscribe to events
            _runner.OnStateChanged += OnRunnerStateChanged;
            _runner.OnCountdownTick += OnRunnerCountdownTick;
            _runner.OnCalibrationComplete += OnRunnerCalibrationComplete;
        }

        #endregion

        #region Event Handlers

        private void OnRunnerStateChanged(MocapCalibratorRunner.CalibrationState state)
        {
            Repaint();
        }

        private void OnRunnerCountdownTick(float remaining)
        {
            Repaint();
        }

        private void OnRunnerCalibrationComplete(List<MocapCalibratorRunner.CalibrationResult> results)
        {
            Repaint();
        }

        private void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode)
            {
                // Clean up
                if (_runner != null)
                {
                    _runner.OnStateChanged -= OnRunnerStateChanged;
                    _runner.OnCountdownTick -= OnRunnerCountdownTick;
                    _runner.OnCalibrationComplete -= OnRunnerCalibrationComplete;
                }
                _runner = null;
            }
            else if (state == PlayModeStateChange.EnteredPlayMode)
            {
                // Try to find runner
                FindOrCreateRunner();
                UpdateMappingPreview();
            }

            Repaint();
        }

        private void OnEditorUpdate()
        {
            // Force repaint during calibration
            if (_runner != null &&
                (_runner.State == MocapCalibratorRunner.CalibrationState.CountingDown ||
                 _runner.State == MocapCalibratorRunner.CalibrationState.Sampling))
            {
                Repaint();
            }
        }

        #endregion

        #region Helper Methods

        private void UpdateMappingPreview()
        {
            if (_characterRoot == null)
            {
                _previewMappings = null;
                return;
            }

            // Create a temporary request for validation
            var request = new MocapCalibratorRunner.CalibrationRequest
            {
                CharacterRoot = _characterRoot,
                TrackingRoot = _trackingRoot
            };

            // Use runner to validate (or create temporary one)
            var runner = _runner;
            bool createdTemp = false;

            if (runner == null && Application.isPlaying)
            {
                FindOrCreateRunner();
                runner = _runner;
            }

            if (runner != null)
            {
                _previewMappings = runner.ValidateMappings(request);
            }
            else
            {
                // Create default mappings without validation
                _previewMappings = MocapCalibratorRunner.GetDefaultMappings();

                // Try to validate manually
                var animator = _characterRoot.GetComponentInChildren<Animator>();
                foreach (var mapping in _previewMappings)
                {
                    // Find tracker
                    mapping.TrackerTransform = FindTrackerInScene(mapping.TrackerName);

                    // Find bone
                    if (animator != null && animator.isHuman)
                    {
                        mapping.BoneTransform = animator.GetBoneTransform(mapping.Bone);
                    }

                    mapping.IsValid = mapping.TrackerTransform != null && mapping.BoneTransform != null;
                }
            }

            Repaint();
        }

        private Transform FindTrackerInScene(string trackerName)
        {
            if (_trackingRoot != null)
            {
                return FindChildRecursive(_trackingRoot, trackerName);
            }

            var go = GameObject.Find(trackerName);
            return go?.transform;
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

        private void AutoFindTrackingRoot()
        {
            // Try common names
            string[] commonNames = { "TrackingRoot", "Trackers", "VRTrackers", "SteamVRObjects" };

            foreach (var name in commonNames)
            {
                var go = GameObject.Find(name);
                if (go != null)
                {
                    _trackingRoot = go.transform;
                    UpdateMappingPreview();
                    Debug.Log($"[MocapCalibrator] Found tracking root: {name}");
                    return;
                }
            }

            // Try to find by looking for Tracked_ objects
            var tracked = GameObject.Find("Tracked_Head");
            if (tracked != null && tracked.transform.parent != null)
            {
                _trackingRoot = tracked.transform.parent;
                UpdateMappingPreview();
                Debug.Log($"[MocapCalibrator] Found tracking root (parent of Tracked_Head): {_trackingRoot.name}");
                return;
            }

            Debug.LogWarning("[MocapCalibrator] Could not auto-find tracking root. Please assign manually.");
        }

        private MonoBehaviour FindVRIKComponent(Transform root)
        {
            if (root == null) return null;

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

        private void LoadPreferences()
        {
            _countdownSeconds = EditorPrefs.GetFloat(PREFS_COUNTDOWN, 5f);
            _sampleDurationSeconds = EditorPrefs.GetFloat(PREFS_SAMPLE_DURATION, 0.5f);
            _enableVRIKAfterCalibration = EditorPrefs.GetBool(PREFS_DISABLE_VRIK, true);
            _freezeAnimatorDuringCalibration = EditorPrefs.GetBool(PREFS_FREEZE_ANIMATOR, false);
            _applyRotationForHeadHandsFeetPelvis = EditorPrefs.GetBool(PREFS_APPLY_ROT, true);
            _applyRotationForFeet = EditorPrefs.GetBool(PREFS_APPLY_ROT_FEET, true);
        }

        private void SavePreferences()
        {
            EditorPrefs.SetFloat(PREFS_COUNTDOWN, _countdownSeconds);
            EditorPrefs.SetFloat(PREFS_SAMPLE_DURATION, _sampleDurationSeconds);
            EditorPrefs.SetBool(PREFS_DISABLE_VRIK, _enableVRIKAfterCalibration);
            EditorPrefs.SetBool(PREFS_FREEZE_ANIMATOR, _freezeAnimatorDuringCalibration);
            EditorPrefs.SetBool(PREFS_APPLY_ROT, _applyRotationForHeadHandsFeetPelvis);
            EditorPrefs.SetBool(PREFS_APPLY_ROT_FEET, _applyRotationForFeet);
        }

        #endregion
    }
}
