# Mocap Recording Troubleshooting

## Current Issue

Calibration now behaves correctly, but recorded animations can show:

- torso looking compressed or scrunched
- head tilted upward in the saved clip

## What We Observed

- Recent takes are being saved as **Humanoid muscle clips** (not transform curves).
- In a representative take (`Assets/Captures/Raw/Take_20260303_211930.anim`), baseline values are already extreme:
  - `Spine Front-Back` about `-1.34`
  - `Head Nod Down-Up` about `+1.57`
- The recent hip-whip fix intentionally removed torso scaling and keeps hip target driven directly by the hip tracker.
- In Humanoid mode, root motion recording is optional and currently defaults to off.

## Likely Causes

1. **Torso proportion mismatch after hip-whip fix**
   - Hip is now correct and stable, but torso length differences (user vs avatar) are no longer compensated.
   - This can appear as torso compression in solved pose, then in the recorded humanoid clip.

2. **Humanoid conversion amplifies posture bias**
   - Humanoid recording stores normalized muscle curves, not raw bone transforms.
   - If the solved pose is already biased (spine flexed, head pitched), the clip preserves that bias.

3. **Root/body compensation may be missing in Humanoid mode**
   - With root motion off, body/root movement data may be unavailable for playback correction depending on controller settings.

## Fast Validation Steps

1. Record one short motion in **Transform** mode.
2. Record the same motion in **Humanoid** mode with **Record Root Motion ON**.
3. Compare playback:
   - If Transform looks correct but Humanoid does not, issue is in humanoid conversion/settings.
   - If both look wrong, issue is in calibration/solver target setup.

## Recommended Fix Direction

1. Prioritize **same-rig fidelity** first (accurate playback on the source avatar).
2. Add a **hip-anchored torso compensation** path:
   - keep hip decoupled from head (do not reintroduce hip whipping)
   - restore torso proportion handling without head-coupling artifacts
3. Make Humanoid defaults safer for capture troubleshooting:
   - enable root motion by default in Humanoid mode, or
   - warn clearly when root motion is off
4. Add guardrails/log warnings when first-frame muscle values are extreme.

## Practical Workaround Right Now

- Use **Transform mode** for immediate capture fidelity while Humanoid path is being tuned.
- Use **Humanoid mode** only when retargetability is required and after validating root motion plus posture baselines.
