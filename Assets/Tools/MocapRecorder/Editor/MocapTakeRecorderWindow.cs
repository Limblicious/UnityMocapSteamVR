using UnityEngine;
using UnityEditor;
using System;
using System.IO;
using System.Reflection;

namespace MocapTools
{
    /// <summary>
    /// Recording mode for the mocap tool.
    /// </summary>
    public enum RecordMode
    {
        Transform,  // Generic transform recording (GameObjectRecorder)
        Humanoid    // Humanoid muscle recording (HumanPoseHandler)
    }

    /// <summary>
    /// Editor window for recording mocap takes from a VRIK-solved skeleton.
    /// Access via Tools > Mocap > Take Recorder
    /// </summary>
    public class MocapTakeRecorderWindow : EditorWindow
    {
        // Settings
        private Transform _characterRoot;  // User-assigned character root (top-level)
        private Transform _resolvedBoneRoot;  // Auto-detected bone root for recording
        private string _outputFolder = "Assets/Captures/Raw";
        private string _fbxOutputFolder = "Assets/Captures/FBX";
        private int _fps = 60;
        private float _countdownSeconds = 2.0f;
        private float _trimStartSeconds = 0.15f;
        private float _trimEndSeconds = 0.10f;
        private string _clipName = "";
        private bool _autoExportFbx = false;

        // Record mode settings
        private RecordMode _recordMode = RecordMode.Transform;
        private bool _recordRootMotion = false;
        private Animator _resolvedAnimator;  // Auto-detected animator for humanoid mode

        // Runtime state
        private MocapSkeletonRecorder _recorder;
        private MocapHumanoidRecorder _humanoidRecorder;
        private bool _isArmed = false;
        private AnimationClip _lastCreatedClip;
        private Vector2 _scrollPosition;

        // Styles
        private GUIStyle _headerStyle;
        private GUIStyle _recordingStyle;
        private GUIStyle _countdownStyle;
        private bool _stylesInitialized = false;

        // Constants
        private const string RECORDER_OBJECT_NAME = "_MocapRecorder";
        private const string HUMANOID_RECORDER_OBJECT_NAME = "_MocapHumanoidRecorder";
        private const string PREFS_SKELETON_ROOT = "MocapRecorder_SkeletonRoot";
        private const string PREFS_OUTPUT_FOLDER = "MocapRecorder_OutputFolder";
        private const string PREFS_FBX_FOLDER = "MocapRecorder_FbxFolder";
        private const string PREFS_FPS = "MocapRecorder_FPS";
        private const string PREFS_COUNTDOWN = "MocapRecorder_Countdown";
        private const string PREFS_TRIM_START = "MocapRecorder_TrimStart";
        private const string PREFS_TRIM_END = "MocapRecorder_TrimEnd";
        private const string PREFS_AUTO_FBX = "MocapRecorder_AutoFbx";
        private const string PREFS_RECORD_MODE = "MocapRecorder_RecordMode";
        private const string PREFS_ROOT_MOTION = "MocapRecorder_RootMotion";

        [MenuItem("Tools/Mocap/Take Recorder")]
        public static void ShowWindow()
        {
            var window = GetWindow<MocapTakeRecorderWindow>("Mocap Take Recorder");
            window.minSize = new Vector2(350, 450);
            window.Show();
        }

        private void OnEnable()
        {
            LoadPreferences();
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            SavePreferences();
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.update -= OnEditorUpdate;

            // Unsubscribe from recorder events
            if (_recorder != null)
            {
                _recorder.OnCountdownTick -= OnCountdownTick;
                _recorder.OnRecordingStarted -= OnRecordingStarted;
                _recorder.OnRecordingStopped -= OnRecordingStopped;
            }
            if (_humanoidRecorder != null)
            {
                _humanoidRecorder.OnCountdownTick -= OnCountdownTick;
                _humanoidRecorder.OnRecordingStarted -= OnRecordingStarted;
                _humanoidRecorder.OnRecordingStopped -= OnRecordingStopped;
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

            _recordingStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 18,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.red }
            };

            _countdownStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 24,
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

            DrawRecordingStatus();
            EditorGUILayout.Space(10);

            DrawSettings();
            EditorGUILayout.Space(10);

            DrawTrimSettings();
            EditorGUILayout.Space(10);

            DrawFbxExportSettings();
            EditorGUILayout.Space(10);

            DrawRecordingButtons();
            EditorGUILayout.Space(10);

            DrawLastClipInfo();

            EditorGUILayout.EndScrollView();
        }

        private void DrawHeader()
        {
            EditorGUILayout.LabelField("Mocap Take Recorder", _headerStyle);
            EditorGUILayout.HelpBox(
                "Records motion into an AnimationClip.\n" +
                "• Transform mode: Generic bone transforms (works with any rig)\n" +
                "• Humanoid mode: Muscle curves (retargetable across humanoids)\n\n" +
                "1. Enter Play Mode\n" +
                "2. Assign your character's top-level GameObject\n" +
                "3. Select Record Mode and press ARM",
                MessageType.Info);
        }

        private void DrawRecordingStatus()
        {
            // Check both recorder types for status
            bool isCountingDown = (_recorder != null && _recorder.IsCountingDown) ||
                                  (_humanoidRecorder != null && _humanoidRecorder.IsCountingDown);
            bool isRecording = (_recorder != null && _recorder.IsRecording) ||
                               (_humanoidRecorder != null && _humanoidRecorder.IsRecording);

            float countdownRemaining = 0f;
            float recordingDuration = 0f;

            if (_recorder != null && _recorder.IsCountingDown)
                countdownRemaining = _recorder.CountdownRemaining;
            else if (_humanoidRecorder != null && _humanoidRecorder.IsCountingDown)
                countdownRemaining = _humanoidRecorder.CountdownRemaining;

            if (_recorder != null && _recorder.IsRecording)
                recordingDuration = _recorder.RecordingDuration;
            else if (_humanoidRecorder != null && _humanoidRecorder.IsRecording)
                recordingDuration = _humanoidRecorder.RecordingDuration;

            if (isCountingDown)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField($"COUNTDOWN: {countdownRemaining:F1}s", _countdownStyle);
                EditorGUILayout.EndVertical();
            }
            else if (isRecording)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                string modeLabel = _recordMode == RecordMode.Humanoid ? "● RECORDING (Humanoid)" : "● RECORDING (Transform)";
                EditorGUILayout.LabelField(modeLabel, _recordingStyle);
                EditorGUILayout.LabelField($"Duration: {recordingDuration:F1}s", EditorStyles.centeredGreyMiniLabel);
                EditorGUILayout.EndVertical();
            }
        }

        private void DrawSettings()
        {
            EditorGUILayout.LabelField("Recording Settings", EditorStyles.boldLabel);

            using (new EditorGUI.DisabledGroupScope(_isArmed))
            {
                // Record Mode
                EditorGUI.BeginChangeCheck();
                _recordMode = (RecordMode)EditorGUILayout.EnumPopup(
                    new GUIContent("Record Mode", "Transform: Generic bone recording\nHumanoid: Muscle curves (retargetable)"),
                    _recordMode);
                if (EditorGUI.EndChangeCheck())
                {
                    SavePreferences();
                }

                EditorGUILayout.Space(5);

                // Character Root (user assigns top-level character)
                EditorGUI.BeginChangeCheck();
                _characterRoot = (Transform)EditorGUILayout.ObjectField(
                    new GUIContent("Character Root (Top-level)", "Top-level character GameObject. Bone root will be auto-detected."),
                    _characterRoot,
                    typeof(Transform),
                    true);
                if (EditorGUI.EndChangeCheck())
                {
                    // Update resolved bone root and animator when character changes
                    _resolvedBoneRoot = ResolveBoneRoot(_characterRoot);
                    _resolvedAnimator = _characterRoot != null ? _characterRoot.GetComponentInChildren<Animator>() : null;
                    SavePreferences();
                }

                // Show mode-specific status
                if (_recordMode == RecordMode.Transform)
                {
                    DrawBoneRootStatus();
                }
                else
                {
                    DrawHumanoidStatus();
                }

                // Output Folder
                EditorGUILayout.BeginHorizontal();
                _outputFolder = EditorGUILayout.TextField(
                    new GUIContent("Output Folder", "Folder where .anim clips will be saved"),
                    _outputFolder);
                if (GUILayout.Button("...", GUILayout.Width(30)))
                {
                    string selected = EditorUtility.OpenFolderPanel("Select Output Folder", "Assets", "");
                    if (!string.IsNullOrEmpty(selected))
                    {
                        _outputFolder = MakeRelativePath(selected);
                    }
                }
                EditorGUILayout.EndHorizontal();

                // FPS
                _fps = EditorGUILayout.IntSlider(
                    new GUIContent("FPS", "Recording framerate (deterministic sampling)"),
                    _fps, 24, 120);

                // Countdown
                _countdownSeconds = EditorGUILayout.Slider(
                    new GUIContent("Countdown (s)", "Delay before recording starts"),
                    _countdownSeconds, 0f, 10f);

                // Humanoid-specific options
                if (_recordMode == RecordMode.Humanoid)
                {
                    _recordRootMotion = EditorGUILayout.Toggle(
                        new GUIContent("Record Root Motion", "Record RootT/RootQ curves for root motion. Usually OFF for in-place animations."),
                        _recordRootMotion);
                }

                // Clip Name
                EditorGUILayout.BeginHorizontal();
                _clipName = EditorGUILayout.TextField(
                    new GUIContent("Clip Name", "Leave empty for auto-generated name"),
                    _clipName);
                if (GUILayout.Button("Auto", GUILayout.Width(50)))
                {
                    _clipName = GenerateClipName();
                }
                EditorGUILayout.EndHorizontal();

                if (string.IsNullOrEmpty(_clipName))
                {
                    EditorGUILayout.HelpBox($"Auto name: {GenerateClipName()}", MessageType.None);
                }
            }
        }

        private void DrawTrimSettings()
        {
            EditorGUILayout.LabelField("Trim Settings", EditorStyles.boldLabel);

            using (new EditorGUI.DisabledGroupScope(_isArmed))
            {
                _trimStartSeconds = EditorGUILayout.Slider(
                    new GUIContent("Trim Start (s)", "Seconds to remove from the beginning"),
                    _trimStartSeconds, 0f, 2f);

                _trimEndSeconds = EditorGUILayout.Slider(
                    new GUIContent("Trim End (s)", "Seconds to remove from the end"),
                    _trimEndSeconds, 0f, 2f);
            }
        }

        private void DrawFbxExportSettings()
        {
            EditorGUILayout.LabelField("FBX Export (Optional)", EditorStyles.boldLabel);

            using (new EditorGUI.DisabledGroupScope(_isArmed))
            {
                _autoExportFbx = EditorGUILayout.Toggle(
                    new GUIContent("Auto Export FBX", "Export FBX after recording (requires Unity Recorder + FBX Exporter)"),
                    _autoExportFbx);

                if (_autoExportFbx)
                {
                    EditorGUI.indentLevel++;

                    EditorGUILayout.BeginHorizontal();
                    _fbxOutputFolder = EditorGUILayout.TextField(
                        new GUIContent("FBX Folder", "Folder where FBX files will be saved"),
                        _fbxOutputFolder);
                    if (GUILayout.Button("...", GUILayout.Width(30)))
                    {
                        string selected = EditorUtility.OpenFolderPanel("Select FBX Output Folder", "Assets", "");
                        if (!string.IsNullOrEmpty(selected))
                        {
                            _fbxOutputFolder = MakeRelativePath(selected);
                        }
                    }
                    EditorGUILayout.EndHorizontal();

                    // Check if required packages are available
                    bool hasRecorder = IsFbxRecorderAvailable();
                    if (!hasRecorder)
                    {
                        EditorGUILayout.HelpBox(
                            "FBX export requires:\n" +
                            "• com.unity.recorder\n" +
                            "• com.unity.formats.fbx\n\n" +
                            "Install via Package Manager to enable this feature.",
                            MessageType.Warning);
                    }
                    else
                    {
                        EditorGUILayout.HelpBox("FBX Recorder packages detected.", MessageType.Info);
                    }

                    EditorGUI.indentLevel--;
                }
            }
        }

        private void DrawRecordingButtons()
        {
            EditorGUILayout.LabelField("Controls", EditorStyles.boldLabel);

            // Determine if we can record based on mode
            bool canRecord = false;
            if (_recordMode == RecordMode.Transform)
            {
                canRecord = Application.isPlaying && _characterRoot != null && _resolvedBoneRoot != null;
            }
            else // Humanoid mode
            {
                canRecord = Application.isPlaying && _characterRoot != null &&
                            _resolvedAnimator != null && _resolvedAnimator.isHuman;
            }

            // Validation messages
            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox("Enter Play Mode to record.", MessageType.Warning);
            }
            else if (_characterRoot == null)
            {
                EditorGUILayout.HelpBox("Assign a Character Root to record.", MessageType.Warning);
            }
            else if (_recordMode == RecordMode.Transform && _resolvedBoneRoot == null)
            {
                EditorGUILayout.HelpBox("Could not resolve bone root. See error above.", MessageType.Error);
            }
            else if (_recordMode == RecordMode.Humanoid && (_resolvedAnimator == null || !_resolvedAnimator.isHuman))
            {
                EditorGUILayout.HelpBox("Humanoid mode requires an Animator with a Humanoid avatar. See error above.", MessageType.Error);
            }

            EditorGUILayout.BeginHorizontal();

            // ARM Button
            using (new EditorGUI.DisabledGroupScope(!canRecord || _isArmed))
            {
                GUI.backgroundColor = new Color(0.3f, 0.8f, 0.3f);
                if (GUILayout.Button("ARM (Countdown)", GUILayout.Height(40)))
                {
                    ArmRecording();
                }
            }

            // STOP Button
            using (new EditorGUI.DisabledGroupScope(!_isArmed))
            {
                GUI.backgroundColor = new Color(0.9f, 0.3f, 0.3f);
                if (GUILayout.Button("STOP (Save Clip)", GUILayout.Height(40)))
                {
                    StopRecording();
                }
            }

            GUI.backgroundColor = Color.white;
            EditorGUILayout.EndHorizontal();
        }

        private void DrawLastClipInfo()
        {
            if (_lastCreatedClip != null)
            {
                EditorGUILayout.LabelField("Last Created Clip", EditorStyles.boldLabel);

                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.ObjectField("Clip", _lastCreatedClip, typeof(AnimationClip), false);
                EditorGUILayout.LabelField($"Duration: {_lastCreatedClip.length:F2}s");
                EditorGUILayout.LabelField($"Path: {AssetDatabase.GetAssetPath(_lastCreatedClip)}");

                if (GUILayout.Button("Select in Project"))
                {
                    Selection.activeObject = _lastCreatedClip;
                    EditorGUIUtility.PingObject(_lastCreatedClip);
                }
                EditorGUILayout.EndVertical();
            }
        }

        private void ArmRecording()
        {
            if (!Application.isPlaying)
            {
                Debug.LogError("[MocapRecorder] Cannot arm: Not in Play Mode.");
                return;
            }

            if (_characterRoot == null)
            {
                Debug.LogError("[MocapRecorder] Cannot arm: Character Root not assigned.");
                return;
            }

            // Ensure output folder exists
            EnsureFolderExists(_outputFolder);

            if (_recordMode == RecordMode.Transform)
            {
                ArmTransformRecording();
            }
            else
            {
                ArmHumanoidRecording();
            }

            Repaint();
        }

        private void ArmTransformRecording()
        {
            // Resolve bone root (re-resolve in case hierarchy changed)
            _resolvedBoneRoot = ResolveBoneRoot(_characterRoot);
            if (_resolvedBoneRoot == null)
            {
                string errorMsg = "[MocapRecorder] Cannot arm: Could not resolve bone root. " +
                                  "Ensure the model has an Animator (Humanoid), SkinnedMeshRenderer, or an Armature child.";
                Debug.LogError(errorMsg);
                EditorUtility.DisplayDialog("Mocap Recorder", errorMsg, "OK");
                return;
            }

            Debug.Log($"[MocapRecorder] Transform mode - Clip paths relative to: {_characterRoot.name}");
            Debug.Log($"[MocapRecorder] Recording bones under: {GetRelativePath(_characterRoot, _resolvedBoneRoot)}");

            // Get or create the recorder
            _recorder = GetOrCreateRecorder();
            if (_recorder == null)
            {
                Debug.LogError("[MocapRecorder] Failed to create recorder component.");
                return;
            }

            // Subscribe to events
            _recorder.OnCountdownTick += OnCountdownTick;
            _recorder.OnRecordingStarted += OnRecordingStarted;
            _recorder.OnRecordingStopped += OnRecordingStopped;

            // Start recording: characterRoot for path relativity, boneRoot for which transforms to record
            _isArmed = true;
            _recorder.BeginRecording(_characterRoot, _resolvedBoneRoot, _fps, _countdownSeconds);
        }

        private void ArmHumanoidRecording()
        {
            // Resolve animator (re-resolve in case hierarchy changed)
            _resolvedAnimator = _characterRoot.GetComponentInChildren<Animator>();
            if (_resolvedAnimator == null)
            {
                string errorMsg = "[MocapRecorder] Cannot arm: No Animator found on character.";
                Debug.LogError(errorMsg);
                EditorUtility.DisplayDialog("Mocap Recorder", errorMsg, "OK");
                return;
            }

            if (!_resolvedAnimator.isHuman)
            {
                string errorMsg = "[MocapRecorder] Cannot arm: Animator does not have a Humanoid avatar. " +
                                  "Humanoid mode requires a Humanoid rig. Use Transform mode for Generic rigs.";
                Debug.LogError(errorMsg);
                EditorUtility.DisplayDialog("Mocap Recorder", errorMsg, "OK");
                return;
            }

            Debug.Log($"[MocapRecorder] Humanoid mode - Animator: {_resolvedAnimator.name}, RootMotion: {_recordRootMotion}");

            // Get or create the humanoid recorder
            _humanoidRecorder = GetOrCreateHumanoidRecorder();
            if (_humanoidRecorder == null)
            {
                Debug.LogError("[MocapRecorder] Failed to create humanoid recorder component.");
                return;
            }

            // Subscribe to events
            _humanoidRecorder.OnCountdownTick += OnCountdownTick;
            _humanoidRecorder.OnRecordingStarted += OnRecordingStarted;
            _humanoidRecorder.OnRecordingStopped += OnRecordingStopped;

            // Start recording
            _isArmed = true;
            _humanoidRecorder.BeginRecordingHumanoid(_resolvedAnimator, _fps, _countdownSeconds, _recordRootMotion);
        }

        private void StopRecording()
        {
            if (_recordMode == RecordMode.Transform)
            {
                StopTransformRecording();
            }
            else
            {
                StopHumanoidRecording();
            }

            Repaint();
        }

        private void StopTransformRecording()
        {
            if (_recorder == null)
            {
                Debug.LogWarning("[MocapRecorder] No active transform recorder.");
                _isArmed = false;
                return;
            }

            // Unsubscribe from events
            _recorder.OnCountdownTick -= OnCountdownTick;
            _recorder.OnRecordingStarted -= OnRecordingStarted;
            _recorder.OnRecordingStopped -= OnRecordingStopped;

            // Generate clip name
            string clipName = string.IsNullOrEmpty(_clipName) ? GenerateClipName() : _clipName;

            // End recording and get clip
            AnimationClip clip = _recorder.EndRecordingAndCreateClip(clipName);
            _isArmed = false;

            if (clip == null)
            {
                Debug.Log("[MocapRecorder] Recording cancelled or no data captured.");
                return;
            }

            // Trim the clip (includes scale curve removal for transform mode)
            if (_trimStartSeconds > 0 || _trimEndSeconds > 0)
            {
                _recorder.TrimClip(clip, _trimStartSeconds, _trimEndSeconds);
            }

            // Save and finalize
            FinalizeClip(clip, clipName);
        }

        private void StopHumanoidRecording()
        {
            if (_humanoidRecorder == null)
            {
                Debug.LogWarning("[MocapRecorder] No active humanoid recorder.");
                _isArmed = false;
                return;
            }

            // Unsubscribe from events
            _humanoidRecorder.OnCountdownTick -= OnCountdownTick;
            _humanoidRecorder.OnRecordingStarted -= OnRecordingStarted;
            _humanoidRecorder.OnRecordingStopped -= OnRecordingStopped;

            // Generate clip name
            string clipName = string.IsNullOrEmpty(_clipName) ? GenerateClipName() : _clipName;

            // End recording and get clip
            AnimationClip clip = _humanoidRecorder.EndRecordingAndCreateClip(clipName);
            _isArmed = false;

            if (clip == null)
            {
                Debug.Log("[MocapRecorder] Recording cancelled or no data captured.");
                return;
            }

            // Trim the clip (humanoid mode does NOT remove scale curves - they don't exist)
            if (_trimStartSeconds > 0 || _trimEndSeconds > 0)
            {
                _humanoidRecorder.TrimClip(clip, _trimStartSeconds, _trimEndSeconds);
            }

            // Save and finalize
            FinalizeClip(clip, clipName);
        }

        private void FinalizeClip(AnimationClip clip, string clipName)
        {
            // Save the clip as an asset
            string clipPath = SaveClipAsset(clip, clipName);

            if (!string.IsNullOrEmpty(clipPath))
            {
                _lastCreatedClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);

                // Select in project
                if (_lastCreatedClip != null)
                {
                    Selection.activeObject = _lastCreatedClip;
                    EditorGUIUtility.PingObject(_lastCreatedClip);
                }

                // Auto export FBX if enabled (Transform mode only - humanoid clips don't export well to FBX)
                if (_autoExportFbx && _characterRoot != null && _recordMode == RecordMode.Transform)
                {
                    ExportToFbx(clipName, _lastCreatedClip);
                }
            }
        }

        private string SaveClipAsset(AnimationClip clip, string clipName)
        {
            EnsureFolderExists(_outputFolder);

            string path = $"{_outputFolder}/{clipName}.anim";
            path = AssetDatabase.GenerateUniqueAssetPath(path);

            AssetDatabase.CreateAsset(clip, path);
            AssetDatabase.SaveAssets();

            Debug.Log($"[MocapRecorder] Animation clip saved: {path}");

            return path;
        }

        private void ExportToFbx(string clipName, AnimationClip clip)
        {
            if (!IsFbxRecorderAvailable())
            {
                Debug.LogWarning("[MocapRecorder] FBX export skipped: Required packages not installed. " +
                                "Install com.unity.recorder + com.unity.formats.fbx to enable FBX export.");
                return;
            }

            EnsureFolderExists(_fbxOutputFolder);

            try
            {
                // Use reflection to access FBX Exporter
                // First try the simpler ModelExporter approach
                Type modelExporterType = GetTypeFromAssemblies("UnityEditor.Formats.Fbx.Exporter.ModelExporter");

                if (modelExporterType != null)
                {
                    // Use the character root for FBX export (includes full hierarchy)
                    GameObject targetObject = _characterRoot.gameObject;

                    string fbxPath = $"{_fbxOutputFolder}/{clipName}.fbx";
                    fbxPath = AssetDatabase.GenerateUniqueAssetPath(fbxPath);

                    // Convert to absolute path
                    string absolutePath = Path.GetFullPath(fbxPath);

                    // Try to use ModelExporter.ExportObject
                    MethodInfo exportMethod = modelExporterType.GetMethod("ExportObject",
                        BindingFlags.Public | BindingFlags.Static,
                        null,
                        new Type[] { typeof(string), typeof(UnityEngine.Object) },
                        null);

                    if (exportMethod != null)
                    {
                        object result = exportMethod.Invoke(null, new object[] { absolutePath, targetObject });
                        if (result != null && !string.IsNullOrEmpty(result.ToString()))
                        {
                            AssetDatabase.Refresh();
                            Debug.Log($"[MocapRecorder] FBX exported: {fbxPath}");
                            return;
                        }
                    }

                    // Alternative: Try ExportObjects with array
                    MethodInfo exportObjectsMethod = modelExporterType.GetMethod("ExportObjects",
                        BindingFlags.Public | BindingFlags.Static);

                    if (exportObjectsMethod != null)
                    {
                        object result = exportObjectsMethod.Invoke(null, new object[] { absolutePath, new UnityEngine.Object[] { targetObject } });
                        if (result != null)
                        {
                            AssetDatabase.Refresh();
                            Debug.Log($"[MocapRecorder] FBX exported: {fbxPath}");
                            return;
                        }
                    }
                }

                Debug.LogWarning("[MocapRecorder] FBX export failed: Could not invoke FBX Exporter. " +
                               "You can manually export from: Assets > Export To FBX...");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MocapRecorder] FBX export failed: {ex.Message}. " +
                               "You can manually export from: Assets > Export To FBX...");
            }
        }

        private bool IsFbxRecorderAvailable()
        {
            // Check for FBX Exporter package
            Type modelExporterType = GetTypeFromAssemblies("UnityEditor.Formats.Fbx.Exporter.ModelExporter");
            return modelExporterType != null;
        }

        private Type GetTypeFromAssemblies(string typeName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type type = assembly.GetType(typeName);
                    if (type != null) return type;
                }
                catch
                {
                    // Ignore assemblies that can't be searched
                }
            }
            return null;
        }

        private MocapSkeletonRecorder GetOrCreateRecorder()
        {
            // Find existing recorder
            var existing = FindObjectOfType<MocapSkeletonRecorder>();
            if (existing != null) return existing;

            // Create new recorder object
            var go = new GameObject(RECORDER_OBJECT_NAME);
            go.hideFlags = HideFlags.HideInHierarchy;

            var recorder = go.AddComponent<MocapSkeletonRecorder>();

            Debug.Log("[MocapRecorder] Created transform recorder component.");

            return recorder;
        }

        private MocapHumanoidRecorder GetOrCreateHumanoidRecorder()
        {
            // Find existing recorder
            var existing = FindObjectOfType<MocapHumanoidRecorder>();
            if (existing != null) return existing;

            // Create new recorder object
            var go = new GameObject(HUMANOID_RECORDER_OBJECT_NAME);
            go.hideFlags = HideFlags.HideInHierarchy;

            var recorder = go.AddComponent<MocapHumanoidRecorder>();

            Debug.Log("[MocapRecorder] Created humanoid recorder component.");

            return recorder;
        }

        private void OnCountdownTick(float remaining)
        {
            Repaint();
        }

        private void OnRecordingStarted()
        {
            Repaint();
        }

        private void OnRecordingStopped()
        {
            Repaint();
        }

        private void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode)
            {
                // Clean up when exiting play mode
                _isArmed = false;
                _recorder = null;
                _humanoidRecorder = null;
            }

            Repaint();
        }

        private void OnEditorUpdate()
        {
            // Force repaint during recording for live updates
            bool isActive = (_isArmed && _recorder != null && (_recorder.IsRecording || _recorder.IsCountingDown)) ||
                            (_isArmed && _humanoidRecorder != null && (_humanoidRecorder.IsRecording || _humanoidRecorder.IsCountingDown));
            if (isActive)
            {
                Repaint();
            }
        }

        private string GenerateClipName()
        {
            return $"Take_{DateTime.Now:yyyyMMdd_HHmmss}";
        }

        private string MakeRelativePath(string absolutePath)
        {
            string dataPath = Application.dataPath;
            if (absolutePath.StartsWith(dataPath))
            {
                return "Assets" + absolutePath.Substring(dataPath.Length);
            }
            return absolutePath;
        }

        private void EnsureFolderExists(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath)) return;

            if (!AssetDatabase.IsValidFolder(folderPath))
            {
                string[] parts = folderPath.Split('/');
                string currentPath = parts[0]; // "Assets"

                for (int i = 1; i < parts.Length; i++)
                {
                    string nextPath = currentPath + "/" + parts[i];
                    if (!AssetDatabase.IsValidFolder(nextPath))
                    {
                        AssetDatabase.CreateFolder(currentPath, parts[i]);
                        Debug.Log($"[MocapRecorder] Created folder: {nextPath}");
                    }
                    currentPath = nextPath;
                }
            }
        }

        private void LoadPreferences()
        {
            _outputFolder = EditorPrefs.GetString(PREFS_OUTPUT_FOLDER, "Assets/Captures/Raw");
            _fbxOutputFolder = EditorPrefs.GetString(PREFS_FBX_FOLDER, "Assets/Captures/FBX");
            _fps = EditorPrefs.GetInt(PREFS_FPS, 60);
            _countdownSeconds = EditorPrefs.GetFloat(PREFS_COUNTDOWN, 2.0f);
            _trimStartSeconds = EditorPrefs.GetFloat(PREFS_TRIM_START, 0.15f);
            _trimEndSeconds = EditorPrefs.GetFloat(PREFS_TRIM_END, 0.10f);
            _autoExportFbx = EditorPrefs.GetBool(PREFS_AUTO_FBX, false);
            _recordMode = (RecordMode)EditorPrefs.GetInt(PREFS_RECORD_MODE, 0);
            _recordRootMotion = EditorPrefs.GetBool(PREFS_ROOT_MOTION, false);

            // Try to restore character root reference
            string rootPath = EditorPrefs.GetString(PREFS_SKELETON_ROOT, "");
            if (!string.IsNullOrEmpty(rootPath))
            {
                var go = GameObject.Find(rootPath);
                if (go != null)
                {
                    _characterRoot = go.transform;
                    _resolvedBoneRoot = ResolveBoneRoot(_characterRoot);
                    _resolvedAnimator = _characterRoot.GetComponentInChildren<Animator>();
                }
            }
        }

        private void SavePreferences()
        {
            EditorPrefs.SetString(PREFS_OUTPUT_FOLDER, _outputFolder);
            EditorPrefs.SetString(PREFS_FBX_FOLDER, _fbxOutputFolder);
            EditorPrefs.SetInt(PREFS_FPS, _fps);
            EditorPrefs.SetFloat(PREFS_COUNTDOWN, _countdownSeconds);
            EditorPrefs.SetFloat(PREFS_TRIM_START, _trimStartSeconds);
            EditorPrefs.SetFloat(PREFS_TRIM_END, _trimEndSeconds);
            EditorPrefs.SetBool(PREFS_AUTO_FBX, _autoExportFbx);
            EditorPrefs.SetInt(PREFS_RECORD_MODE, (int)_recordMode);
            EditorPrefs.SetBool(PREFS_ROOT_MOTION, _recordRootMotion);

            if (_characterRoot != null)
            {
                EditorPrefs.SetString(PREFS_SKELETON_ROOT, GetGameObjectPath(_characterRoot.gameObject));
            }
        }

        private string GetGameObjectPath(GameObject obj)
        {
            string path = obj.name;
            Transform parent = obj.transform.parent;
            while (parent != null)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }
            return path;
        }

        #region Bone Root Resolution

        /// <summary>
        /// Draws the bone root resolution status in the UI (Transform mode).
        /// </summary>
        private void DrawBoneRootStatus()
        {
            if (_characterRoot == null)
            {
                // No need to show anything - the main validation handles this
                return;
            }

            if (_resolvedBoneRoot == null)
            {
                EditorGUILayout.HelpBox(
                    "Could not resolve bone root.\n" +
                    "Ensure the model has:\n" +
                    "• An Animator component (Humanoid preferred), or\n" +
                    "• A SkinnedMeshRenderer, or\n" +
                    "• A child named 'Armature' or 'Rig'",
                    MessageType.Error);
            }
            else
            {
                string charRootName = _characterRoot.name;

                // Show the relative path from character to bone root
                string relativePath = GetRelativePath(_characterRoot, _resolvedBoneRoot);

                EditorGUILayout.HelpBox(
                    $"Clip paths relative to: {charRootName}\n" +
                    $"Recording bones under: {relativePath}\n" +
                    $"Example curve path: {relativePath}/Hips/Spine/...",
                    MessageType.Info);
            }
        }

        /// <summary>
        /// Draws the humanoid animator status in the UI (Humanoid mode).
        /// </summary>
        private void DrawHumanoidStatus()
        {
            if (_characterRoot == null)
            {
                // No need to show anything - the main validation handles this
                return;
            }

            // Re-resolve animator if needed
            if (_resolvedAnimator == null)
            {
                _resolvedAnimator = _characterRoot.GetComponentInChildren<Animator>();
            }

            if (_resolvedAnimator == null)
            {
                EditorGUILayout.HelpBox(
                    "No Animator found on character.\n" +
                    "Humanoid mode requires an Animator component with a Humanoid avatar.",
                    MessageType.Error);
            }
            else if (!_resolvedAnimator.isHuman)
            {
                EditorGUILayout.HelpBox(
                    $"Animator '{_resolvedAnimator.name}' is not Humanoid.\n" +
                    "Humanoid mode requires a Humanoid avatar.\n" +
                    "Use Transform mode for Generic rigs.",
                    MessageType.Error);
            }
            else if (_resolvedAnimator.avatar == null)
            {
                EditorGUILayout.HelpBox(
                    $"Animator '{_resolvedAnimator.name}' has no Avatar assigned.\n" +
                    "Assign a Humanoid Avatar to the Animator.",
                    MessageType.Error);
            }
            else
            {
                int muscleCount = HumanTrait.MuscleCount;
                EditorGUILayout.HelpBox(
                    $"Animator: {_resolvedAnimator.name}\n" +
                    $"Avatar: {_resolvedAnimator.avatar.name} (Humanoid)\n" +
                    $"Recording {muscleCount} muscle curves\n" +
                    $"Clip is retargetable to any Humanoid rig",
                    MessageType.Info);
            }
        }

        /// <summary>
        /// Gets the relative path from ancestor to descendant.
        /// </summary>
        private string GetRelativePath(Transform ancestor, Transform descendant)
        {
            if (ancestor == descendant)
                return descendant.name;

            string path = descendant.name;
            Transform current = descendant.parent;

            while (current != null && current != ancestor)
            {
                path = current.name + "/" + path;
                current = current.parent;
            }

            return path;
        }

        /// <summary>
        /// Resolves the bone root transform from a character root.
        /// Resolution order:
        /// 1. If characterRoot itself looks like a bone root (contains "armature"/"rig" and has many descendants)
        /// 2. Direct child named "Armature"
        /// 3. Animator's humanoid hips -> highest ancestor under characterRoot
        /// 4. SkinnedMeshRenderer's rootBone or bones[0] -> highest ancestor under characterRoot
        /// 5. Any descendant with "armature" in the name
        /// </summary>
        private Transform ResolveBoneRoot(Transform characterRoot)
        {
            if (characterRoot == null)
                return null;

            // 1. Check if characterRoot itself is the bone root
            string rootNameLower = characterRoot.name.ToLowerInvariant();
            if ((rootNameLower.Contains("armature") || rootNameLower.Contains("rig"))
                && CountDescendants(characterRoot) > 10)
            {
                return characterRoot;
            }

            // 2. Direct child named "Armature" (common convention)
            Transform armatureChild = characterRoot.Find("Armature");
            if (armatureChild != null)
                return armatureChild;

            // Also check case-insensitive
            foreach (Transform child in characterRoot)
            {
                string childNameLower = child.name.ToLowerInvariant();
                if (childNameLower == "armature" || childNameLower == "rig")
                    return child;
            }

            // 3. Try Animator with humanoid rig
            Animator animator = characterRoot.GetComponentInChildren<Animator>();
            if (animator != null && animator.isHuman)
            {
                Transform hips = animator.GetBoneTransform(HumanBodyBones.Hips);
                if (hips != null)
                {
                    Transform boneRoot = HighestAncestorUnder(hips, characterRoot);
                    if (boneRoot != null)
                        return boneRoot;
                }
            }

            // 4. Try SkinnedMeshRenderer
            SkinnedMeshRenderer smr = characterRoot.GetComponentInChildren<SkinnedMeshRenderer>();
            if (smr != null)
            {
                Transform bone = smr.rootBone;
                if (bone == null && smr.bones != null && smr.bones.Length > 0)
                {
                    bone = smr.bones[0];
                }
                if (bone != null)
                {
                    Transform boneRoot = HighestAncestorUnder(bone, characterRoot);
                    if (boneRoot != null)
                        return boneRoot;
                }
            }

            // 5. Fallback: search all descendants for "armature" or "rig" in name
            Transform found = FindDescendantByNameContains(characterRoot, "armature");
            if (found != null)
                return found;

            found = FindDescendantByNameContains(characterRoot, "rig");
            if (found != null)
                return found;

            // Could not resolve
            return null;
        }

        /// <summary>
        /// Walks up from transform t until reaching stopRoot, returns the direct child of stopRoot.
        /// </summary>
        private Transform HighestAncestorUnder(Transform t, Transform stopRoot)
        {
            if (t == null || stopRoot == null)
                return null;

            // If t is already a direct child of stopRoot, return t
            if (t.parent == stopRoot)
                return t;

            // Walk up the hierarchy
            Transform current = t;
            Transform previous = null;

            while (current != null && current != stopRoot)
            {
                previous = current;
                current = current.parent;
            }

            // If we reached stopRoot, previous is the direct child
            if (current == stopRoot && previous != null)
                return previous;

            // t is not under stopRoot
            return null;
        }

        /// <summary>
        /// Counts total descendants under a transform.
        /// </summary>
        private int CountDescendants(Transform root)
        {
            int count = 0;
            foreach (Transform child in root)
            {
                count++;
                count += CountDescendants(child);
            }
            return count;
        }

        /// <summary>
        /// Finds the first descendant whose name contains the search string (case-insensitive).
        /// </summary>
        private Transform FindDescendantByNameContains(Transform root, string search)
        {
            string searchLower = search.ToLowerInvariant();
            return FindDescendantByNameContainsRecursive(root, searchLower);
        }

        private Transform FindDescendantByNameContainsRecursive(Transform parent, string searchLower)
        {
            foreach (Transform child in parent)
            {
                if (child.name.ToLowerInvariant().Contains(searchLower))
                    return child;

                Transform found = FindDescendantByNameContainsRecursive(child, searchLower);
                if (found != null)
                    return found;
            }
            return null;
        }

        #endregion
    }
}
