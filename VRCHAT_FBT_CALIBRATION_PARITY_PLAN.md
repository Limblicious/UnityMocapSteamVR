# VRChat FBT Calibration Parity Plan

## Purpose

This document is the implementation brief for bringing the reusable Unity mocap package into behavioral parity with the user's real VRChat full-body calibration workflow.

The current package is directionally correct but conflates two distinct concepts:

1. **Calibration anchoring** — placing/scaling a fixed reference-pose avatar so its configured avatar viewpoint corresponds to the physical HMD, then capturing tracker-to-avatar offsets.
2. **Post-calibration FBT spine solving** — deciding whether the live IK solution is biased toward the head or the hips after calibration has already been accepted.

These must remain separate.

This document also records mismatches found by comparing the package to the user's actual VRChat calibration video, so an agent working only in this standalone repo does not need access to that video to understand the intended behavior.

## Source of truth / context

Requirements are based on:

- User-recorded VRChat calibration video: `2026-09-06 16-37-39.mkv` (recorded 2026-09-06; not stored in this repo).
- User screenshot of a VRChat avatar's Unity setup showing `VRC Avatar Descriptor -> View -> View Position`.
- User clarification that the avatar is T/reference-posed and anchored using that **View Position** during calibration.
- User clarification that **Lock Head / Lock Hip do not select the calibration anchor**; they are post-calibration live FBT spine-solving policies.
- NUN-TPS source baseline commit: `fe071edd3424ffd426b51a22ac76e5f9d09ce3816396`.
- This repo's corresponding sync baseline: `8c689bdbec601978b467aa08e5f9d09ce3816396` (`Sync FBT mocap system from NUN-TPS: calibration/profile/solver, planar mirror with dynamic resolution, recording + voice commands`).

If an implementation detail conflicts with the conceptual behavior described here, use this document as the requirement for this task.

---

# 1. Observed VRChat workflow

The user's actual VRChat calibration flow is approximately:

1. Enter VRChat with the avatar visible in a mirror.
2. Invoke **Calibrate FBT**.
3. Avatar enters a fixed calibration/reference pose (visually a T-pose in the recording).
4. The avatar is aligned to the performer's HMD using the avatar's configured **View Position**.
5. Calibration/tracker visuals show where the tracked body devices are relative to the fixed avatar.
6. Performer stands in the calibration pose and confirms using both controller triggers.
7. Calibration is accepted and the avatar enters normal live FBT.
8. A live FBT spine mode applies. The user's current setting in the recording is **Lock Hip**.
9. The user enters advanced **Adjust FBT**.
10. VRChat exposes at least:
    - `Move & Rotate`
    - `Move Only`
    - `Rotate Only`
    - `Reset`
11. Smaller blue adjustment handles appear around the live FBT body.
12. The user refines alignment and then tests the result physically: elbows, crouch, hips, feet/legs, turns, front/side mirror inspection.
13. The user leaves adjustment and continues with the accepted calibration.

This means the target workflow has both an automatic reference-pose calibration stage and a separate live manual refinement/validation stage.

---

# 2. Critical conceptual correction: View Position vs. Lock mode

## Calibration anchor

During calibration, the avatar's configured **avatar viewpoint** is the authoritative avatar-space reference for the HMD.

In a VRChat project this is configured at:

`VRC Avatar Descriptor -> View -> View Position`

This standalone package must **not depend on the VRChat SDK** to obtain it. Represent the same concept generically through either:

- an explicit runtime `Transform avatarViewpoint`, or
- a serialized avatar-local view position inside a generic mocap avatar/profile configuration.

If no explicit viewpoint is supplied, Head bone + eye offset may remain as a documented approximation/fallback.

## Lock Hip / Lock Head

Lock mode begins **after calibration is confirmed**. It changes how the live IK/spine solution prioritizes tracked head versus pelvis/hip constraints.

It must not change how the calibration avatar is anchored to the HMD.

Correct conceptual flow:

```text
CALIBRATION
    deterministic reference pose
    + avatar View Position <-> HMD alignment
    + tracker-to-avatar offset capture

        ↓ confirm

LIVE FBT
    stored calibration offsets
    + selected Lock Hip / Lock Head policy
    + optional manual Adjust FBT corrections
```

Do not implement Lock Hip by calibrating relative to the hip.

---

# 3. Current package facts to inspect before editing

At the `8c689bdb` sync baseline and corresponding NUN-TPS embedded package:

## `Runtime/MocapFbtCalibrationProfile.cs`

- Profile already defines `LockHip`, `LockHead`, and `LockBoth` integer constants.
- `lockMode` currently defaults to `LockHead`.
- The field is serialized but is not currently used to drive a real mode-specific solver policy.
- Each binding stores a single `offsetPosition` and `offsetRotationEuler`.
- `MocapFbtRegionUtil.IsAdjustable()` correctly identifies hips, both feet, and both elbows as manual adjustment regions.

## `Runtime/MocapFbtCalibrator.cs`

- Supports explicit `avatarViewpoint` with Head + eye-offset fallback.
- Scales the avatar from HMD eye height vs. avatar viewpoint height.
- Current pinning primarily follows HMD X/Z + yaw while retaining floor/root Y.
- Current calibration path re-enables VRIK for a live preview while still in calibration.
- Both-trigger confirmation and sample averaging already exist.
- Current manual `UpdateAdjusting()` changes position only.

## `Runtime/MocapFbtReflection.cs`

- `ApplyFullTrackingPolicy()` hardcodes a Lock Head-style VRIK policy.

## `Runtime/MocapFbtSolver.cs`

- `ApplyProfile()` ignores `profile.lockMode` and invokes the hardcoded policy.
- Root locomotion is pelvis driven.

## UI / mirror

- Planar mirror support exists.
- Calibration and adjustment controls exist in the current system.
- Adjustment is currently effectively Move Only.

Project-specific tracker-role resolution is currently in NUN-TPS (`Assets/Scripts/TrackerPoseRelay.cs`), not necessarily in this standalone package. This package still needs an API/data model that permits projects to select controller versus wrist-tracker hand spatial sources without embedding NUN-specific device serials or scene logic.

---

# 4. Required state separation

The exact enum names are flexible, but behavior should be equivalent to:

```text
Idle
CalibrationReferencePose
CalibrationConfirming
ActiveFbt
AdjustingFbt
```

Key boundaries:

- **CalibrationReferencePose**: fixed skeleton/reference pose; viewpoint anchors to HMD; no normal post-calibration spine policy driving the body.
- **CalibrationConfirming**: freeze/reference pose and sample tracker-to-avatar offsets.
- **ActiveFbt**: normal live IK with selected Lock Hip / Lock Head policy.
- **AdjustingFbt**: same live solver plus editable manual correction handles.

Recording/performance state is separate from FBT calibration state.

---

# 5. Required changes

## 5.1 Prefer an explicit avatar View Position

The package needs a generic equivalent of VRChat's avatar View Position.

Requirements:

- Preserve support for explicit `avatarViewpoint`.
- Add/standardize a serializable avatar-local view position if needed for saved/profile-based setup.
- Do not reference `VRC_AvatarDescriptor` from this package.
- Keep Head + eye-offset fallback only for generic humanoids lacking explicit view data.
- Log HMD position, avatar viewpoint position, scale, and residual viewpoint/HMD delta during calibration for diagnosis.

Current X/Z-only-style root pinning is not sufficient to describe the requirement as exact viewpoint calibration. Treat View Position <-> HMD coincidence, including vertical alignment, as a first-class calibration invariant.

A floor/root constraint may remain if geometrically compatible, but do not silently hide a conflict between floor placement and exact view alignment.

---

## 5.2 Use a deterministic reference pose during calibration

When calibration begins:

1. Disable normal live FBT solver behavior.
2. Put the avatar in a deterministic calibration pose matching the VRChat-style T/reference pose.
3. Keep the skeleton pose fixed while root/viewpoint alignment follows the calibration anchor as needed.
4. Show calibration tracker/reference visuals.
5. Confirm with both triggers and sample offsets while fixed.
6. Enable normal live FBT only after successful calibration.

For generic Humanoid avatars this can be implemented via a safe known T-pose or a configurable calibration pose/clip.

Do not use a live VRIK solve as the calibration reference pose.

---

## 5.3 Preserve both-trigger confirmation and averaging

The existing both-trigger confirmation and short sample window match the observed workflow and should remain unless testing shows a concrete problem.

---

## 5.4 Implement actual Lock Hip / Lock Head live solver policies

Minimum required modes:

```csharp
LockHip
LockHead
```

`LockBoth` may remain in the profile if desired, but is not required for parity unless separately validated.

Requirements:

- Default the user's reference profile to **Lock Hip**.
- Lock mode applies only in live FBT after calibration / saved-profile activation.
- Lock mode can be changed without recalibration.
- Lock mode never modifies viewpoint calibration data.
- Active mode is logged and persisted.

Refactor `MocapFbtReflection.ApplyFullTrackingPolicy()` into mode-specific behavior or an equivalent API. `MocapFbtSolver.ApplyProfile()` must honor `profile.lockMode`.

Semantics:

- **Lock Head**: prioritize head/HMD relationship and allow more pelvis/spine accommodation.
- **Lock Hip**: prioritize pelvis/hip relationship and allow more upper-spine/head-chain accommodation.

Do not guess the exact Final IK numeric policy solely from these labels. Validate the VRIK parameter sets visually against live tracking behavior.

---

## 5.5 Add a generic hand spatial-source concept

The recorded VRChat workflow uses controllers as hand spatial targets. The production mocap setup may instead use wrist trackers while StretchSense provides finger articulation.

Therefore expose a generic choice such as:

```csharp
public enum MocapHandSpatialSource
{
    Controllers,
    WristTrackers
}
```

or equivalent per-hand binding configuration.

Requirements:

**Controllers mode**
- left VR controller -> left hand target
- right VR controller -> right hand target

**WristTrackers mode**
- left wrist tracker role -> left hand target
- right wrist tracker role -> right hand target

Finger articulation from StretchSense/OSC remains independent from spatial hand source.

This package must not embed user-specific Vive serial numbers or NUN-TPS scene/device maps.

---

## 5.6 Preserve body tracker regions

Target body regions for the user's standard setup are:

- HMD/head
- hand spatial source: controllers or optional wrist trackers
- hips/waist tracker
- left foot tracker
- right foot tracker
- left elbow tracker
- right elbow tracker
- StretchSense OSC for fingers

The manual-adjust set remains hip + both feet + both elbows.

---

## 5.7 Separate automatic calibration from manual adjustment data

Current bindings have one position/rotation offset. Refactor so automatic calibration results are not destroyed by manual edits.

Equivalent target model:

```text
calibratedPositionOffset
calibratedRotationOffset
manualPositionCorrection
manualRotationCorrection
```

Effective target = calibrated offset + manual correction.

Why: VRChat exposes Reset during Adjust FBT. Reset must return to the automatic calibration result, not set the whole binding to zero and not require a fresh calibration.

Version saved JSON accordingly. Migrate safely or reject incompatible old profiles with a clear message; do not silently change field meaning.

---

## 5.8 Implement full Adjust FBT modes

Required manual modes:

```text
Move & Rotate
Move Only
Rotate Only
Reset
```

### Move Only
- translation delta modifies manual position correction only

### Rotate Only
- capture controller rotation at grab start
- controller rotation delta modifies manual rotation correction only

### Move & Rotate
- both deltas applied

### Reset
- reset selected binding manual correction
- reset all manual corrections

Reset preserves automatic calibration data.

Adjust FBT must operate on the already active live solver. Do not return the avatar to the calibration T/reference pose when adjusting.

---

## 5.9 Distinct visuals for calibration and manual adjustment

The user's workflow shows two different visual states:

**Initial calibration**
- fixed/reference-pose avatar
- prominent tracker/reference markers used to align the performer

**Adjust FBT**
- live FBT avatar
- smaller blue correction handles

`MocapFbtUi` should make these states unambiguous. Exact VRChat colors/geometry are not a requirement.

---

## 5.10 Treat the mirror as calibration instrumentation

Mirror support is not cosmetic. The performer uses it continuously to judge alignment.

Requirements:

- mirror usable during initial calibration
- mirror usable during Adjust FBT
- live result visible during validation poses
- calibration must never require operating blind

Functional calibration correctness takes priority over pixel-perfect VRChat mirror appearance.

---

## 5.11 Support deliberate post-calibration validation

The workflow should make it easy to test before accepting a session calibration:

- bend/extend elbows
- arms up/down
- crouch
- rotate/translate hips
- move/lift individual feet/legs
- inspect front and side views in mirror

No separate solver state is strictly required, but the flow should naturally support calibrate -> adjust -> physically test -> finish/save.

---

## 5.12 Persist complete reusable profile data

A saved profile should contain enough information to reconstruct the accepted FBT setup:

- stable avatar/profile identifier
- avatar-local viewpoint data/reference
- scale/measurement result if still required
- tracker region bindings
- hand spatial source
- automatic calibration offsets
- manual adjustment corrections
- selected Lock Hip / Lock Head mode
- floor/tracking-origin data required by live solver
- yaw/root offsets required by final locomotion model

Avoid using only a scene object display name as stable identity if a better profile key can be introduced safely.

---

## 5.13 Keep the standalone package character-agnostic

Search for and remove/rework reusable code that assumes a NUN-TPS avatar name such as:

- `LinnraNunvAnim(Clone)`
- `Linnra`

The package should receive/configure a character root or active mocap avatar profile through generic references.

NUN-TPS may bind its current player-character object externally; that project-specific name must not become package API.

---

# 6. Suggested data/API shape

Names are flexible. Intent should be explicit, e.g.:

```csharp
public enum FbtSpineMode
{
    LockHip,
    LockHead
}

public enum FbtAdjustmentMode
{
    MoveAndRotate,
    MoveOnly,
    RotateOnly
}

public enum MocapHandSpatialSource
{
    Controllers,
    WristTrackers
}

[Serializable]
public class MocapAvatarCalibrationSettings
{
    public string profileId;
    public Vector3 avatarViewPositionLocal;
    public FbtSpineMode spineMode = FbtSpineMode.LockHip;
    public MocapHandSpatialSource handSpatialSource;
}

[Serializable]
public class MocapFbtBinding
{
    // identity / region fields...
    public Vector3 calibratedPositionOffset;
    public Vector3 calibratedRotationEuler;
    public Vector3 manualPositionCorrection;
    public Vector3 manualRotationCorrectionEuler;
}
```

Runtime may still use an explicit `Transform avatarViewpoint`; save a local position/profile representation rather than Unity object references.

---

# 7. Standalone package implementation touchpoints

Inspect current code before editing. Likely files:

- `Runtime/MocapFbtCalibrationProfile.cs`
  - mode enums/settings
  - automatic vs manual correction storage
  - save-version migration
- `Runtime/MocapFbtCalibrator.cs`
  - reference pose
  - exact View Position/HMD anchoring
  - confirmation sampling
  - Move/Rotate/Reset manual adjustment
- `Runtime/MocapFbtSolver.cs`
  - live-only FBT behavior
  - selected spine policy
- `Runtime/MocapFbtReflection.cs`
  - mode-specific Final IK/VRIK configuration
- `Runtime/MocapFbtUi.cs`
  - distinct initial-calibration vs adjustment visuals
  - adjustment mode/status
- `Runtime/MocapFbtXR.cs`
  - generic controller input/pose helpers as needed
- `Runtime/MocapFbtPlanarMirror.cs`
  - preserve mirror behavior; only change if required for state integration
- `Editor/*` FBT/menu/prefab factory code
  - expose adjustment modes/reset if the current prefab/menu is generated here
- recorder/voice-command code
  - remove character-name hardcoding and use configured active mocap avatar references

Do not add NUN-TPS tracker serials or scene-specific object references to this package.

---

# 8. Cross-repo synchronization rule

Current history shows the FBT implementation being developed in NUN-TPS and then synchronized into this standalone package.

Unless intentionally changing ownership for a separate reason, preserve that workflow for this task:

1. Shared behavior is implemented/tested in `NUN-TPS/Packages/com.limblicious.mocap`.
2. Project-specific scene/device glue stays in NUN-TPS.
3. Reusable runtime/editor changes are synchronized into this repo.
4. NUN-specific serials, scene objects, and player-character names are excluded here.
5. Verify matching shared source files are actually identical after sync.

This document is intentionally present in both repositories so an agent entering either repo understands the same target behavior.

---

# 9. Acceptance criteria

Do not mark complete until demonstrated in Play Mode.

## Calibration

- [ ] Calibrate places avatar in a deterministic T/reference-pose-like calibration state.
- [ ] Explicit avatar View Position is preferred when configured.
- [ ] View Position/HMD alignment is a real calibration invariant, including vertical alignment.
- [ ] Calibration is independent of Lock Hip / Lock Head.
- [ ] Calibration visuals show tracked body positions clearly.
- [ ] Both triggers confirm/sample offsets.
- [ ] Successful confirm activates live FBT.

## Spine mode

- [ ] Lock Hip exists and can be the default profile mode.
- [ ] Lock Head exists.
- [ ] Mode can change without recalibration.
- [ ] Mode change does not modify calibration/viewpoint offsets.
- [ ] Active mode persists and is logged.

## Hand source

- [ ] Controller spatial hands supported.
- [ ] Wrist-tracker spatial hands supported.
- [ ] Finger articulation remains independent of spatial hand source.

## Adjust FBT

- [ ] Adjustment acts on live FBT, not T-pose calibration state.
- [ ] Hip, both elbows, both feet are adjustable.
- [ ] Move Only changes position only.
- [ ] Rotate Only changes rotation only.
- [ ] Move & Rotate changes both.
- [ ] Reset restores automatic calibration values.
- [ ] Finish/save persists manual corrections.
- [ ] Saved profile reload reproduces accepted adjustments.

## Mirror / validation

- [ ] Mirror usable during calibration.
- [ ] Mirror usable during adjustment.
- [ ] Live solve can be tested by crouching, elbow bending, leg/foot movement, turning, front/side inspection.

## Reusability

- [ ] No user-specific tracker serials required by package code.
- [ ] No hardcoded NUN-TPS player-character name required by package code.
- [ ] Package remains usable by a generic Unity 6 humanoid consumer.
- [ ] Shared code matches the NUN-TPS embedded package after sync.

---

# 10. Priority order

Implement in this order:

1. Calibration state separation + deterministic reference pose.
2. Exact/generic View Position <-> HMD calibration anchoring.
3. Real Lock Hip / Lock Head post-calibration solver selection; reference default Lock Hip.
4. Generic controller vs wrist-tracker hand spatial source API.
5. Separate automatic calibration offsets from manual corrections.
6. Adjust FBT Move / Rotate / Move+Rotate / Reset.
7. Distinct calibration vs adjustment visuals.
8. Mirror/validation workflow integration.
9. Remove character-name hardcoding and verify NUN-TPS sync.

Do not spend time on cosmetic VRChat UI replication before steps 1-6 work reliably.

---

# 11. Non-goals / constraints

- No VRChat SDK dependency in this reusable package.
- Do not interpret Lock Hip as a hip-based calibration anchor.
- Do not remove wrist-tracker support; make spatial hand source configurable.
- Do not couple StretchSense finger articulation to a specific hand/wrist spatial tracking source.
- Do not regress existing mirror, voice commands, root-motion recording, or OSC finger pipeline.
- Do not silently reinterpret old calibration JSON; version/migrate or invalidate clearly.
- Do not import NUN-TPS-specific tracker serials, scene references, or character names.

---

# 12. Why this matters

This package is production infrastructure for an in-house Unity 6 mocap stage. The performer needs to enter a capture session, embody a game character, calibrate FBT repeatably, refine tracker mounting offsets, validate the solve in a mirror, then record bespoke interactions and weapon/prop performances against actual game-scale geometry.

The user's VRChat workflow is the proven interaction model. The goal is not cosmetic cloning of VRChat; the goal is repeatable, understandable calibration behavior that reproduces the important mechanics of that workflow while remaining a generic Unity mocap package.