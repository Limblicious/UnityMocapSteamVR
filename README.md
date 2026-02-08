# MoCap Tools for Unity

A suite of Unity Editor tools for VR motion capture recording, tracker calibration, and animation clip post-processing. All tools live under `Assets/Tools/MocapRecorder/` and are accessed from the **Tools > Mocap** menu.

## Tools Overview

| Tool | Menu Path | Purpose |
|------|-----------|---------|
| [Take Recorder](#mocap-take-recorder) | Tools > Mocap > Take Recorder | Record live mocap into AnimationClips |
| [Calibrator](#mocap-calibrator) | Tools > Mocap > Calibrate | Calibrate VR tracker offsets via T-pose |
| [Clip Simplifier](#clip-simplifier) | Tools > Mocap > Clip Simplifier | Reduce keyframe count in recorded clips |

---

## MoCap Take Recorder

Records motion from a character in Play Mode into a `.anim` AnimationClip. Supports two recording modes.

### Recording Modes

**Transform mode** records raw bone transforms using Unity's `GameObjectRecorder`. Produces generic clips tied to a specific skeleton hierarchy. Works with any rig type.

**Humanoid mode** records muscle curves via `HumanPoseHandler`. Produces clips that are retargetable across any Humanoid avatar. Optionally records root motion (`RootT`/`RootQ` curves).

### Usage

1. Enter Play Mode (VR tracking and IK must be active).
2. Open **Tools > Mocap > Take Recorder**.
3. Assign the top-level character GameObject to **Character Root**.
4. Select **Record Mode** (Transform or Humanoid).
5. Click **ARM** to start a countdown, then perform your motion.
6. Click **STOP** to save the clip to disk.

### Settings

- **FPS** (24-120) &mdash; deterministic sampling rate via `Time.captureFramerate`.
- **Countdown** &mdash; delay before recording begins.
- **Trim Start / End** &mdash; seconds to remove from the beginning and end of the clip. Transform mode also strips scale curves during trim.
- **Clip Name** &mdash; leave empty for auto-generated timestamped names (`Take_YYYYMMDD_HHmmss`).
- **Output Folder** &mdash; default `Assets/Captures/Raw`.
- **Record Root Motion** (Humanoid only) &mdash; records `RootT`/`RootQ` curves for root motion. Usually off for in-place animations.
- **Auto Export FBX** (Transform only) &mdash; optional FBX export via reflection on the `com.unity.formats.fbx` package. No hard dependency; gracefully skips if the package is not installed.

### Bone Root Auto-Detection (Transform Mode)

When you assign a Character Root, the tool automatically resolves the bone hierarchy root using this priority:

1. Direct child named `Armature` or `Rig`.
2. Humanoid Animator's `Hips` bone traced up to the nearest direct child of the character.
3. `SkinnedMeshRenderer.rootBone` traced up.
4. Descendant search for names containing "armature" or "rig".

### Files

| File | Type | Description |
|------|------|-------------|
| `Editor/MocapTakeRecorderWindow.cs` | Editor Window | Main UI. Manages settings, arm/stop controls, clip saving, FBX export. |
| `Scripts/MocapSkeletonRecorder.cs` | Runtime Component | Transform mode worker. Uses `GameObjectRecorder` to capture bone transforms in `LateUpdate` at `DefaultExecutionOrder(10000)` (post-IK). |
| `Scripts/MocapHumanoidRecorder.cs` | Runtime Component | Humanoid mode worker. Uses `HumanPoseHandler` to capture all muscle curves in `LateUpdate` at `DefaultExecutionOrder(10000)`. |

---

## MoCap Calibrator

Calibrates VR tracker-to-bone offsets by having the user hold a T-pose. Aligns the tracking coordinate space to the avatar and computes per-tracker offset transforms.

### How It Works

1. VRIK should be **disabled before calibration**. The user holds a T-pose during a countdown.
2. Tracker and bone positions/rotations are sampled over a configurable window.
3. **Head-anchored yaw alignment**: the `TrackingRoot` transform is repositioned and yaw-rotated so the head tracker lines up with the avatar's Head bone. Only yaw (Y-axis rotation) is applied to TrackingRoot to avoid tilting the entire tracking space -- any head pitch/roll during the T-pose is handled per-tracker instead.
4. **Proportion scaling**: the calibrator measures the user's body proportions (head-to-hip, hip-to-foot distances from tracker positions) and the avatar's proportions (bone-to-bone distances). Per-limb scale factors are computed so that tracker positions are mapped to the avatar's limb lengths at runtime, regardless of the user's actual body size.
5. For each tracker, a local rotation offset (`*_Off` child transform) is computed so the offset transform's orientation matches the corresponding avatar bone. **Hands, hips, and feet get zero position offsets** -- hand trackers are used directly for their high accuracy, while hip and foot positions are handled by the proportion scaler at runtime.
6. A `MocapProportionScaler` component is created to run every frame (before VRIK). It scales the head-to-hip vector by the torso ratio and hip-to-foot vectors by the leg ratios, positioning the hip and foot offset children at proportion-corrected locations.
7. VRIK is **automatically enabled** after calibration completes. VRIK targets should point to the `*_Off` transforms instead of raw tracked objects.

### Usage

1. Enter Play Mode with VR headset and trackers active. **Ensure VRIK is disabled.**
2. Open **Tools > Mocap > Calibrate**.
3. Assign **Character Root** (GameObject with Humanoid Animator) and **Tracking Root** (parent of `Tracked_Head`, `Tracked_HandL`, etc.).
4. Click **Calibrate** and hold a T-pose for the countdown duration.
5. Results are displayed with per-tracker rotation offsets and proportion scale factors. VRIK is enabled automatically.

### Default Tracker Mappings

| Tracked Object | Offset Child | Avatar Bone | Notes |
|----------------|-------------|-------------|-------|
| `Tracked_Head` | `Head_Off` | Head | Yaw anchor; offset handles remaining pitch/roll |
| `Tracked_HandL` | `HandL_Off` | LeftHand | Zero position offset (tracker-direct) |
| `Tracked_HandR` | `HandR_Off` | RightHand | Zero position offset (tracker-direct) |
| `Tracked_Hips` | `Hips_Off` | Hips | Also accepts `Tracked_Waist`; position via proportion scaler |
| `Tracked_FootL` | `FootL_Off` | LeftFoot | Position via proportion scaler; foot rotation toggle |
| `Tracked_FootR` | `FootR_Off` | RightFoot | Position via proportion scaler; foot rotation toggle |
| `Tracked_ElbowL` | `ElbowL_Off` | LeftLowerArm | Position only |
| `Tracked_ElbowR` | `ElbowR_Off` | RightLowerArm | Position only |

### Settings

- **Countdown** (1-10s) &mdash; time to prepare and hold T-pose.
- **Sample Duration** (0.1-2s) &mdash; how long poses are sampled after countdown.
- **Enable VRIK After Calibration** &mdash; automatically enables VRIK after calibration completes. VRIK should be disabled before starting calibration. Recommended on.
- **Freeze Animator** &mdash; disables the Animator component during calibration.
- **Apply Rotation Offsets** &mdash; applies rotation offsets for head, hands, and pelvis.
- **Apply Foot Rotation** &mdash; separate toggle for foot rotation offsets.

### External Library References

The calibrator detects **FinalIK** components (`VRIK`, `FullBodyBipedIK`) by type name string only, not by direct `using` import. This means:

- The tool compiles and runs even if FinalIK is not installed.
- When FinalIK is present and "Enable VRIK After Calibration" is on, it enables VRIK after calibration completes. VRIK should be disabled before starting calibration.

Tracker naming follows **SteamVR** conventions (`Tracked_Head`, `Tracked_HandL`, etc.), but the tool has no compile-time dependency on SteamVR.

### Files

| File | Type | Description |
|------|------|-------------|
| `Editor/MocapCalibratorWindow.cs` | Editor Window | UI for setup, settings, mapping preview, calibration control, and results display. |
| `Scripts/MocapCalibratorRunner.cs` | Runtime Component | Coroutine-based calibration engine. Handles countdown, sampling, TrackingRoot alignment, proportion measurement, and offset computation. |
| `Scripts/MocapProportionScaler.cs` | Runtime Component | Per-frame proportion scaling. Positions hip and foot offset children at scaled locations to match avatar limb lengths. Runs at `DefaultExecutionOrder(-100)` (before VRIK). |

---

## Clip Simplifier

Reduces keyframe count in AnimationClips while preserving motion quality. Creates a new simplified clip without modifying the original.

### How It Works

The simplifier uses a deterministic key-reduction algorithm: for each intermediate keyframe, it checks whether removing that key would cause the curve to deviate beyond a tolerance threshold (compared to linear interpolation between neighboring keys). Keys within tolerance are removed. Multiple passes allow progressively more aggressive reduction.

### Usage

1. Open **Tools > Mocap > Clip Simplifier** (or select an AnimationClip in the Project window; the tool auto-detects it).
2. The tool auto-detects whether the clip is **Humanoid** (muscle curves) or **Generic** (transform curves).
3. Choose a tolerance preset or set custom values.
4. Click **Simplify -> Save New Clip**.

### Tolerance Presets

| Preset | Muscles | Root Pos | Root Rot | Generic Pos | Generic Rot | Euler |
|--------|---------|----------|----------|-------------|-------------|-------|
| Locomotion | 0.01 | 0.002 | 0.001 | 0.002 | 0.001 | 0.2 |
| Action | 0.005 | 0.001 | 0.0005 | 0.001 | 0.0005 | 0.1 |
| Custom | User-defined | | | | | |

**Locomotion** is more aggressive (good for repetitive cycles). **Action** preserves more detail (better for one-off motions).

### Options

- **Copy Clip Settings** &mdash; carries over `loopTime`, `wrapMode`, etc.
- **Copy Animation Events** &mdash; preserves AnimationEvents from the source.
- **Remove Scale Curves** &mdash; strips `localScale` curves (usually static in mocap data).
- **Ensure Quaternion Continuity** &mdash; prevents rotation flip artifacts.
- **Max Reduction Passes** (1-10) &mdash; more passes = more reduction.
- **Override Frame Rate** &mdash; custom frame rate for the output clip.

### Files

| File | Type | Description |
|------|------|-------------|
| `Editor/AnimationClipSimplifierWindow.cs` | Editor Window | UI for source selection, mode detection, tolerance controls, statistics, and output. |
| `Scripts/AnimationClipSimplifier.cs` | Static Utility | Pure curve simplification logic. No Editor dependency; usable at runtime. Provides `SimplifyCurve`, `SimplifyCurveWithTangents`, and batch operations. |

---

## Project Structure

```
Assets/Tools/MocapRecorder/
├── Editor/
│   ├── MocapTakeRecorderWindow.cs      # Take Recorder UI
│   ├── MocapCalibratorWindow.cs        # Calibrator UI
│   └── AnimationClipSimplifierWindow.cs # Clip Simplifier UI
└── Scripts/
    ├── MocapSkeletonRecorder.cs        # Transform-mode recording engine
    ├── MocapHumanoidRecorder.cs        # Humanoid-mode recording engine
    ├── MocapCalibratorRunner.cs        # Calibration engine
    ├── MocapProportionScaler.cs        # Runtime proportion scaling
    └── AnimationClipSimplifier.cs      # Curve simplification utility
```

All scripts are in the `MocapTools` namespace.

## Dependencies

| Dependency | Required | How Referenced |
|------------|----------|----------------|
| Unity Editor API | Yes | Direct `using UnityEditor` |
| Unity `GameObjectRecorder` | Yes (Transform mode) | `UnityEditor.Animations.GameObjectRecorder` |
| Unity `HumanPoseHandler` | Yes (Humanoid mode) | `UnityEngine.HumanPoseHandler` |
| FinalIK (VRIK) | No | Detected by type name string at runtime (`"VRIK"`, `"FullBodyBipedIK"`) |
| SteamVR | No | Tracker naming convention only (`Tracked_Head`, etc.) |
| FBX Exporter (`com.unity.formats.fbx`) | No | Optional; accessed via reflection |

## Output Folders

| Folder | Contents | Default |
|--------|----------|---------|
| `Assets/Captures/Raw/` | Recorded `.anim` clips | Take Recorder |
| `Assets/Captures/FBX/` | Exported `.fbx` files | Take Recorder (FBX export) |
| `Assets/Captures/Simplified/` | Simplified `.anim` clips | Clip Simplifier |

Folders are created automatically if they do not exist.
