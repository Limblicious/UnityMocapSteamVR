using UnityEngine;
using UnityEditor;
using System;
using System.IO;
using System.Collections.Generic;

namespace MocapTools
{
    /// <summary>
    /// Editor window for simplifying (key-reducing) AnimationClips without overwriting the original.
    /// Access via Tools > Mocap > Clip Simplifier
    ///
    /// USAGE:
    /// 1. Select a source AnimationClip (recorded from mocap or other source)
    /// 2. Choose output folder and name
    /// 3. Select mode (Auto detects Humanoid vs Generic) or choose manually
    /// 4. Adjust tolerance preset or set custom values
    /// 5. Click "Simplify -> Save New Clip"
    ///
    /// The tool creates a NEW simplified clip - the original is never modified.
    /// </summary>
    public class AnimationClipSimplifierWindow : EditorWindow
    {
        // Source and output settings
        private AnimationClip _sourceClip;
        private string _outputFolder = "Assets/Captures/Simplified";
        private string _outputName = "";
        private bool _copyClipSettings = true;
        private bool _copyEvents = true;

        // Mode settings
        private enum ClipMode { Auto, Humanoid, Generic }
        private ClipMode _mode = ClipMode.Auto;
        private ClipMode _detectedMode = ClipMode.Generic;

        // Tolerance presets
        private enum TolerancePreset { Locomotion, Action, Custom }
        private TolerancePreset _tolerancePreset = TolerancePreset.Action;

        // Custom tolerances
        private float _muscleTolerance = 0.005f;
        private float _rootTTolerance = 0.001f;
        private float _rootQTolerance = 0.0005f;
        private float _genericPositionTolerance = 0.001f;
        private float _genericRotationTolerance = 0.0005f;
        private float _genericEulerTolerance = 0.1f;

        // Options
        private bool _removeScaleCurves = true;
        private bool _ensureQuaternionContinuity = true;
        private int _maxPasses = 3;
        private bool _overrideFrameRate = false;
        private float _customFrameRate = 60f;

        // Statistics
        private int _sourceCurveCount;
        private int _sourceKeyCount;
        private int _outputCurveCount;
        private int _outputKeyCount;
        private bool _hasAnalyzedSource;
        private string _lastOperationResult = "";

        // UI state
        private Vector2 _scrollPosition;
        private GUIStyle _headerStyle;
        private GUIStyle _resultStyle;
        private bool _stylesInitialized;

        // Preset values
        private static readonly Dictionary<TolerancePreset, (float muscle, float rootT, float rootQ, float pos, float rot, float euler)> PresetValues =
            new Dictionary<TolerancePreset, (float, float, float, float, float, float)>
        {
            { TolerancePreset.Locomotion, (0.01f, 0.002f, 0.001f, 0.002f, 0.001f, 0.2f) },
            { TolerancePreset.Action, (0.005f, 0.001f, 0.0005f, 0.001f, 0.0005f, 0.1f) },
            { TolerancePreset.Custom, (0.005f, 0.001f, 0.0005f, 0.001f, 0.0005f, 0.1f) }
        };

        // Muscle name cache
        private static HashSet<string> _muscleNames;

        // Constants
        private const string PREFS_OUTPUT_FOLDER = "ClipSimplifier_OutputFolder";
        private const string PREFS_PRESET = "ClipSimplifier_Preset";
        private const string PREFS_REMOVE_SCALE = "ClipSimplifier_RemoveScale";
        private const string PREFS_QUATERNION_CONT = "ClipSimplifier_QuatContinuity";
        private const string PREFS_MAX_PASSES = "ClipSimplifier_MaxPasses";

        [MenuItem("Tools/Mocap/Clip Simplifier")]
        public static void ShowWindow()
        {
            var window = GetWindow<AnimationClipSimplifierWindow>("Clip Simplifier");
            window.minSize = new Vector2(380, 550);
            window.Show();
        }

        private void OnEnable()
        {
            LoadPreferences();
            BuildMuscleNameCache();

            // Try to use selected clip
            if (Selection.activeObject is AnimationClip clip)
            {
                _sourceClip = clip;
                AnalyzeSourceClip();
            }
        }

        private void OnDisable()
        {
            SavePreferences();
        }

        private void OnSelectionChange()
        {
            // Auto-select clip when user selects one in Project
            if (Selection.activeObject is AnimationClip clip && clip != _sourceClip)
            {
                _sourceClip = clip;
                AnalyzeSourceClip();
                Repaint();
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

            _resultStyle = new GUIStyle(EditorStyles.helpBox)
            {
                padding = new RectOffset(10, 10, 10, 10),
                richText = true
            };

            _stylesInitialized = true;
        }

        private void OnGUI()
        {
            InitializeStyles();

            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);

            DrawHeader();
            EditorGUILayout.Space(10);

            DrawSourceSection();
            EditorGUILayout.Space(10);

            DrawOutputSection();
            EditorGUILayout.Space(10);

            DrawModeSection();
            EditorGUILayout.Space(10);

            DrawToleranceSection();
            EditorGUILayout.Space(10);

            DrawOptionsSection();
            EditorGUILayout.Space(10);

            DrawStatisticsSection();
            EditorGUILayout.Space(10);

            DrawActionButtons();
            EditorGUILayout.Space(10);

            DrawResultSection();

            EditorGUILayout.EndScrollView();
        }

        private void DrawHeader()
        {
            EditorGUILayout.LabelField("Animation Clip Simplifier", _headerStyle);
            EditorGUILayout.HelpBox(
                "Reduces keyframe count in AnimationClips while preserving motion quality.\n\n" +
                "1. Select a source clip (recorded from mocap)\n" +
                "2. Adjust tolerances as needed\n" +
                "3. Click 'Simplify -> Save New Clip'\n\n" +
                "The original clip is NEVER modified. A new asset is created.",
                MessageType.Info);
        }

        private void DrawSourceSection()
        {
            EditorGUILayout.LabelField("Source", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            _sourceClip = (AnimationClip)EditorGUILayout.ObjectField(
                new GUIContent("Source Clip", "The AnimationClip to simplify"),
                _sourceClip,
                typeof(AnimationClip),
                false);
            if (EditorGUI.EndChangeCheck())
            {
                AnalyzeSourceClip();
            }

            if (_sourceClip != null)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.LabelField($"Duration: {_sourceClip.length:F2}s");
                EditorGUILayout.LabelField($"Frame Rate: {_sourceClip.frameRate} FPS");
                EditorGUILayout.LabelField($"Detected Mode: {_detectedMode}");
                EditorGUI.indentLevel--;
            }
        }

        private void DrawOutputSection()
        {
            EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);

            // Output folder
            EditorGUILayout.BeginHorizontal();
            _outputFolder = EditorGUILayout.TextField(
                new GUIContent("Output Folder", "Where to save the simplified clip"),
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

            // Output name
            EditorGUILayout.BeginHorizontal();
            _outputName = EditorGUILayout.TextField(
                new GUIContent("Output Name", "Leave empty for auto-generated name"),
                _outputName);
            if (GUILayout.Button("Auto", GUILayout.Width(50)))
            {
                _outputName = GenerateOutputName();
            }
            EditorGUILayout.EndHorizontal();

            if (string.IsNullOrEmpty(_outputName) && _sourceClip != null)
            {
                EditorGUILayout.HelpBox($"Auto name: {GenerateOutputName()}", MessageType.None);
            }
        }

        private void DrawModeSection()
        {
            EditorGUILayout.LabelField("Mode", EditorStyles.boldLabel);

            _mode = (ClipMode)EditorGUILayout.EnumPopup(
                new GUIContent("Clip Mode",
                    "Auto: Detect based on curve types\n" +
                    "Humanoid: Muscle curves (Animator)\n" +
                    "Generic: Transform curves"),
                _mode);

            ClipMode effectiveMode = _mode == ClipMode.Auto ? _detectedMode : _mode;
            if (_mode == ClipMode.Auto)
            {
                EditorGUILayout.HelpBox($"Detected as: {_detectedMode}", MessageType.None);
            }
        }

        private void DrawToleranceSection()
        {
            EditorGUILayout.LabelField("Tolerance Settings", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            _tolerancePreset = (TolerancePreset)EditorGUILayout.EnumPopup(
                new GUIContent("Preset",
                    "Locomotion: More aggressive reduction (larger loops)\n" +
                    "Action: Less aggressive (detailed motions)\n" +
                    "Custom: Manual control"),
                _tolerancePreset);
            if (EditorGUI.EndChangeCheck() && _tolerancePreset != TolerancePreset.Custom)
            {
                ApplyPreset(_tolerancePreset);
            }

            ClipMode effectiveMode = _mode == ClipMode.Auto ? _detectedMode : _mode;

            using (new EditorGUI.DisabledGroupScope(_tolerancePreset != TolerancePreset.Custom))
            {
                EditorGUI.indentLevel++;

                if (effectiveMode == ClipMode.Humanoid)
                {
                    _muscleTolerance = EditorGUILayout.FloatField(
                        new GUIContent("Muscles", "Tolerance for muscle curves (0.001 - 0.05)"),
                        _muscleTolerance);
                    _rootTTolerance = EditorGUILayout.FloatField(
                        new GUIContent("Root Position (RootT)", "Tolerance for root position curves"),
                        _rootTTolerance);
                    _rootQTolerance = EditorGUILayout.FloatField(
                        new GUIContent("Root Rotation (RootQ)", "Tolerance for root quaternion curves"),
                        _rootQTolerance);
                }
                else
                {
                    _genericPositionTolerance = EditorGUILayout.FloatField(
                        new GUIContent("Position", "Tolerance for localPosition curves"),
                        _genericPositionTolerance);
                    _genericRotationTolerance = EditorGUILayout.FloatField(
                        new GUIContent("Rotation (Quat)", "Tolerance for localRotation quaternion curves"),
                        _genericRotationTolerance);
                    _genericEulerTolerance = EditorGUILayout.FloatField(
                        new GUIContent("Rotation (Euler)", "Tolerance for localEulerAnglesRaw curves (degrees)"),
                        _genericEulerTolerance);
                }

                EditorGUI.indentLevel--;
            }

            // Tolerance help text
            if (_tolerancePreset == TolerancePreset.Custom)
            {
                EditorGUILayout.HelpBox(
                    "Lower values = more precise but fewer keys removed\n" +
                    "Higher values = more aggressive reduction",
                    MessageType.None);
            }
        }

        private void DrawOptionsSection()
        {
            EditorGUILayout.LabelField("Options", EditorStyles.boldLabel);

            _copyClipSettings = EditorGUILayout.Toggle(
                new GUIContent("Copy Clip Settings", "Copy loopTime, wrapMode, etc. from source"),
                _copyClipSettings);

            _copyEvents = EditorGUILayout.Toggle(
                new GUIContent("Copy Animation Events", "Copy AnimationEvents from source clip"),
                _copyEvents);

            _removeScaleCurves = EditorGUILayout.Toggle(
                new GUIContent("Remove Scale Curves", "Remove localScale curves (usually static)"),
                _removeScaleCurves);

            _ensureQuaternionContinuity = EditorGUILayout.Toggle(
                new GUIContent("Ensure Quaternion Continuity", "Prevent rotation flips in output clip"),
                _ensureQuaternionContinuity);

            _maxPasses = EditorGUILayout.IntSlider(
                new GUIContent("Max Reduction Passes", "More passes = more reduction but slower"),
                _maxPasses, 1, 10);

            EditorGUILayout.Space(5);

            _overrideFrameRate = EditorGUILayout.Toggle(
                new GUIContent("Override Frame Rate", "Use a custom frame rate instead of source"),
                _overrideFrameRate);

            if (_overrideFrameRate)
            {
                EditorGUI.indentLevel++;
                _customFrameRate = EditorGUILayout.FloatField(
                    new GUIContent("Frame Rate", "Custom frame rate for output clip"),
                    _customFrameRate);
                EditorGUI.indentLevel--;
            }
        }

        private void DrawStatisticsSection()
        {
            if (!_hasAnalyzedSource || _sourceClip == null) return;

            EditorGUILayout.LabelField("Statistics", EditorStyles.boldLabel);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.LabelField("Source Clip:");
            EditorGUI.indentLevel++;
            EditorGUILayout.LabelField($"Curves: {_sourceCurveCount}");
            EditorGUILayout.LabelField($"Total Keys: {_sourceKeyCount}");
            if (_sourceCurveCount > 0)
            {
                EditorGUILayout.LabelField($"Avg Keys/Curve: {_sourceKeyCount / _sourceCurveCount:F1}");
            }
            EditorGUI.indentLevel--;

            if (_outputKeyCount > 0)
            {
                EditorGUILayout.Space(5);
                EditorGUILayout.LabelField("Last Output:");
                EditorGUI.indentLevel++;
                EditorGUILayout.LabelField($"Curves: {_outputCurveCount}");
                EditorGUILayout.LabelField($"Total Keys: {_outputKeyCount}");
                float reduction = 100f * (1f - (float)_outputKeyCount / _sourceKeyCount);
                EditorGUILayout.LabelField($"Reduction: {reduction:F1}%");
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawActionButtons()
        {
            EditorGUILayout.LabelField("Actions", EditorStyles.boldLabel);

            bool canSimplify = _sourceClip != null;

            EditorGUILayout.BeginHorizontal();

            using (new EditorGUI.DisabledGroupScope(!canSimplify))
            {
                GUI.backgroundColor = new Color(0.3f, 0.8f, 0.3f);
                if (GUILayout.Button("Simplify -> Save New Clip", GUILayout.Height(35)))
                {
                    PerformSimplification();
                }
                GUI.backgroundColor = Color.white;
            }

            if (GUILayout.Button("Open Output Folder", GUILayout.Height(35), GUILayout.Width(130)))
            {
                OpenOutputFolder();
            }

            EditorGUILayout.EndHorizontal();

            // Refresh button
            if (_sourceClip != null)
            {
                if (GUILayout.Button("Refresh Source Analysis"))
                {
                    AnalyzeSourceClip();
                }
            }
        }

        private void DrawResultSection()
        {
            if (!string.IsNullOrEmpty(_lastOperationResult))
            {
                EditorGUILayout.LabelField("Result", EditorStyles.boldLabel);
                EditorGUILayout.TextArea(_lastOperationResult, _resultStyle);
            }
        }

        private void AnalyzeSourceClip()
        {
            _hasAnalyzedSource = false;
            _sourceCurveCount = 0;
            _sourceKeyCount = 0;
            _detectedMode = ClipMode.Generic;

            if (_sourceClip == null) return;

            EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(_sourceClip);
            _sourceCurveCount = bindings.Length;

            bool hasMuscleCurves = false;
            bool hasRootCurves = false;

            foreach (var binding in bindings)
            {
                AnimationCurve curve = AnimationUtility.GetEditorCurve(_sourceClip, binding);
                if (curve != null)
                {
                    _sourceKeyCount += curve.length;
                }

                // Check for humanoid indicators
                if (binding.type == typeof(Animator))
                {
                    if (IsMuscleProperty(binding.propertyName))
                    {
                        hasMuscleCurves = true;
                    }
                    else if (binding.propertyName.StartsWith("RootT.") ||
                             binding.propertyName.StartsWith("RootQ."))
                    {
                        hasRootCurves = true;
                    }
                }
            }

            // Detect mode
            if (hasMuscleCurves || hasRootCurves)
            {
                _detectedMode = ClipMode.Humanoid;
            }
            else
            {
                _detectedMode = ClipMode.Generic;
            }

            _hasAnalyzedSource = true;

            // Generate default output name
            if (string.IsNullOrEmpty(_outputName))
            {
                _outputName = "";  // Will use auto-generated name
            }
        }

        private void PerformSimplification()
        {
            if (_sourceClip == null)
            {
                _lastOperationResult = "ERROR: No source clip selected.";
                return;
            }

            try
            {
                EditorUtility.DisplayProgressBar("Simplifying Clip", "Reading source curves...", 0f);

                // Ensure output folder exists
                EnsureFolderExists(_outputFolder);

                // Create new clip
                AnimationClip outputClip = new AnimationClip();
                string clipName = string.IsNullOrEmpty(_outputName) ? GenerateOutputName() : _outputName;
                outputClip.name = clipName;

                // Set frame rate
                outputClip.frameRate = _overrideFrameRate ? _customFrameRate : _sourceClip.frameRate;

                // Get effective mode
                ClipMode effectiveMode = _mode == ClipMode.Auto ? _detectedMode : _mode;

                // Get all curve bindings
                EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(_sourceClip);
                int totalBindings = bindings.Length;
                int processedBindings = 0;

                _outputCurveCount = 0;
                _outputKeyCount = 0;

                int originalKeys = 0;
                int simplifiedKeys = 0;
                int curvesRemoved = 0;

                foreach (var binding in bindings)
                {
                    processedBindings++;
                    EditorUtility.DisplayProgressBar("Simplifying Clip",
                        $"Processing curve {processedBindings}/{totalBindings}...",
                        (float)processedBindings / totalBindings);

                    AnimationCurve sourceCurve = AnimationUtility.GetEditorCurve(_sourceClip, binding);
                    if (sourceCurve == null || sourceCurve.length == 0)
                    {
                        continue;
                    }

                    originalKeys += sourceCurve.length;

                    // Determine if we should skip this curve
                    if (ShouldSkipCurve(binding, effectiveMode))
                    {
                        curvesRemoved++;
                        continue;
                    }

                    // Get tolerance for this curve
                    float tolerance = GetToleranceForBinding(binding, effectiveMode);

                    // Simplify the curve
                    AnimationCurve simplifiedCurve = AnimationClipSimplifier.SimplifyCurve(
                        sourceCurve,
                        tolerance,
                        _maxPasses);

                    // Write to output clip
                    AnimationUtility.SetEditorCurve(outputClip, binding, simplifiedCurve);

                    _outputCurveCount++;
                    _outputKeyCount += simplifiedCurve.length;
                    simplifiedKeys += simplifiedCurve.length;
                }

                EditorUtility.DisplayProgressBar("Simplifying Clip", "Finalizing...", 0.9f);

                // Copy clip settings if requested
                if (_copyClipSettings)
                {
                    CopyClipSettings(_sourceClip, outputClip);
                }

                // Copy events if requested
                if (_copyEvents)
                {
                    CopyAnimationEvents(_sourceClip, outputClip);
                }

                // Ensure quaternion continuity
                if (_ensureQuaternionContinuity)
                {
                    outputClip.EnsureQuaternionContinuity();
                }

                // Save the clip
                string outputPath = $"{_outputFolder}/{clipName}.anim";
                outputPath = AssetDatabase.GenerateUniqueAssetPath(outputPath);

                AssetDatabase.CreateAsset(outputClip, outputPath);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                // Select the new clip
                AnimationClip savedClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(outputPath);
                if (savedClip != null)
                {
                    Selection.activeObject = savedClip;
                    EditorGUIUtility.PingObject(savedClip);
                }

                // Calculate statistics
                float reductionPercent = 100f * (1f - (float)simplifiedKeys / originalKeys);

                _lastOperationResult =
                    $"SUCCESS: Clip saved to:\n{outputPath}\n\n" +
                    $"Original Keys: {originalKeys}\n" +
                    $"Simplified Keys: {simplifiedKeys}\n" +
                    $"Reduction: {reductionPercent:F1}%\n" +
                    $"Curves Removed: {curvesRemoved}";

                Debug.Log($"[ClipSimplifier] {_lastOperationResult}");
            }
            catch (Exception ex)
            {
                _lastOperationResult = $"ERROR: {ex.Message}";
                Debug.LogError($"[ClipSimplifier] Error during simplification: {ex}");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private bool ShouldSkipCurve(EditorCurveBinding binding, ClipMode mode)
        {
            // Skip scale curves if option enabled
            if (_removeScaleCurves && binding.propertyName.Contains("localScale"))
            {
                return true;
            }

            return false;
        }

        private float GetToleranceForBinding(EditorCurveBinding binding, ClipMode mode)
        {
            if (mode == ClipMode.Humanoid)
            {
                // Animator/muscle curves
                if (binding.type == typeof(Animator))
                {
                    if (IsMuscleProperty(binding.propertyName))
                    {
                        return _muscleTolerance;
                    }
                    else if (binding.propertyName.StartsWith("RootT."))
                    {
                        return _rootTTolerance;
                    }
                    else if (binding.propertyName.StartsWith("RootQ."))
                    {
                        return _rootQTolerance;
                    }
                }

                // Default for humanoid
                return _muscleTolerance;
            }
            else
            {
                // Generic/Transform curves
                if (binding.type == typeof(Transform))
                {
                    if (binding.propertyName.StartsWith("localPosition") ||
                        binding.propertyName.StartsWith("m_LocalPosition"))
                    {
                        return _genericPositionTolerance;
                    }
                    else if (binding.propertyName.StartsWith("localRotation") ||
                             binding.propertyName.StartsWith("m_LocalRotation"))
                    {
                        return _genericRotationTolerance;
                    }
                    else if (binding.propertyName.Contains("localEulerAngles") ||
                             binding.propertyName.Contains("EulerAngles"))
                    {
                        return _genericEulerTolerance;
                    }
                    else if (binding.propertyName.StartsWith("localScale") ||
                             binding.propertyName.StartsWith("m_LocalScale"))
                    {
                        return _genericPositionTolerance;  // Use position tolerance for scale
                    }
                }

                // Default for generic
                return _genericPositionTolerance;
            }
        }

        private bool IsMuscleProperty(string propertyName)
        {
            if (_muscleNames == null)
            {
                BuildMuscleNameCache();
            }

            return _muscleNames.Contains(propertyName);
        }

        private static void BuildMuscleNameCache()
        {
            _muscleNames = new HashSet<string>();
            for (int i = 0; i < HumanTrait.MuscleCount; i++)
            {
                _muscleNames.Add(HumanTrait.MuscleName[i]);
            }
        }

        private void CopyClipSettings(AnimationClip source, AnimationClip dest)
        {
            try
            {
                // Get settings using AnimationUtility
                AnimationClipSettings sourceSettings = AnimationUtility.GetAnimationClipSettings(source);
                AnimationUtility.SetAnimationClipSettings(dest, sourceSettings);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ClipSimplifier] Could not copy all clip settings: {ex.Message}");
            }

            // Manually copy legacy settings as fallback
            dest.wrapMode = source.wrapMode;
            dest.legacy = source.legacy;
        }

        private void CopyAnimationEvents(AnimationClip source, AnimationClip dest)
        {
            try
            {
                AnimationEvent[] events = AnimationUtility.GetAnimationEvents(source);
                if (events != null && events.Length > 0)
                {
                    AnimationUtility.SetAnimationEvents(dest, events);
                    Debug.Log($"[ClipSimplifier] Copied {events.Length} animation events.");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ClipSimplifier] Could not copy animation events: {ex.Message}");
            }
        }

        private void ApplyPreset(TolerancePreset preset)
        {
            if (PresetValues.TryGetValue(preset, out var values))
            {
                _muscleTolerance = values.muscle;
                _rootTTolerance = values.rootT;
                _rootQTolerance = values.rootQ;
                _genericPositionTolerance = values.pos;
                _genericRotationTolerance = values.rot;
                _genericEulerTolerance = values.euler;
            }
        }

        private string GenerateOutputName()
        {
            if (_sourceClip == null) return "Clip_simplified";

            string sourceName = _sourceClip.name;

            // Remove any existing "_simplified" suffix
            if (sourceName.EndsWith("_simplified", StringComparison.OrdinalIgnoreCase))
            {
                sourceName = sourceName.Substring(0, sourceName.Length - "_simplified".Length);
            }

            // Also remove any timestamp suffix for cleaner naming
            int underscoreIdx = sourceName.LastIndexOf('_');
            if (underscoreIdx > 0)
            {
                string suffix = sourceName.Substring(underscoreIdx + 1);
                // Check if it looks like a timestamp (8+ digits)
                if (suffix.Length >= 8 && long.TryParse(suffix.Replace("_", ""), out _))
                {
                    // Keep the timestamp in the simplified name
                }
            }

            return $"{sourceName}_simplified_{DateTime.Now:yyyyMMdd_HHmmss}";
        }

        private void OpenOutputFolder()
        {
            EnsureFolderExists(_outputFolder);

            // Ping the folder in Project window
            UnityEngine.Object folder = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(_outputFolder);
            if (folder != null)
            {
                Selection.activeObject = folder;
                EditorGUIUtility.PingObject(folder);
            }

            // Also reveal in OS file browser
            string fullPath = Path.GetFullPath(_outputFolder);
            if (Directory.Exists(fullPath))
            {
                EditorUtility.RevealInFinder(fullPath);
            }
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
                string currentPath = parts[0];  // "Assets"

                for (int i = 1; i < parts.Length; i++)
                {
                    string nextPath = currentPath + "/" + parts[i];
                    if (!AssetDatabase.IsValidFolder(nextPath))
                    {
                        AssetDatabase.CreateFolder(currentPath, parts[i]);
                        Debug.Log($"[ClipSimplifier] Created folder: {nextPath}");
                    }
                    currentPath = nextPath;
                }
            }
        }

        private void LoadPreferences()
        {
            _outputFolder = EditorPrefs.GetString(PREFS_OUTPUT_FOLDER, "Assets/Captures/Simplified");
            _tolerancePreset = (TolerancePreset)EditorPrefs.GetInt(PREFS_PRESET, (int)TolerancePreset.Action);
            _removeScaleCurves = EditorPrefs.GetBool(PREFS_REMOVE_SCALE, true);
            _ensureQuaternionContinuity = EditorPrefs.GetBool(PREFS_QUATERNION_CONT, true);
            _maxPasses = EditorPrefs.GetInt(PREFS_MAX_PASSES, 3);

            ApplyPreset(_tolerancePreset);
        }

        private void SavePreferences()
        {
            EditorPrefs.SetString(PREFS_OUTPUT_FOLDER, _outputFolder);
            EditorPrefs.SetInt(PREFS_PRESET, (int)_tolerancePreset);
            EditorPrefs.SetBool(PREFS_REMOVE_SCALE, _removeScaleCurves);
            EditorPrefs.SetBool(PREFS_QUATERNION_CONT, _ensureQuaternionContinuity);
            EditorPrefs.SetInt(PREFS_MAX_PASSES, _maxPasses);
        }
    }
}
