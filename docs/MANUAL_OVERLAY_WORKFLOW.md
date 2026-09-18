# Manual overlay workflow — implementation status

Branch: `refactor/manual-overlay-workflow` (based on `refactor/manual-mask-keyframes` at `f9ad497a`).

## User workflow and non-negotiable invariants

1. Auto detects faces and writes Auto masks. A user may add **an independent manual target** for a missed face or another object. The manual target has a stable ID, its own explicit correction keyframes and its own tracking samples.
2. Each explicit manual keyframe is usable on its exact frame even if any optical-flow/feature tracker fails. No face detector or feature count is a prerequisite for manual editing.
3. A target's forward tracking stops at its **own** next correction keyframe, a selected end, EOF, or a documented tracking failure. Another face's Auto detection and another manual target's keyframe must not interrupt it.
4. Compose the Auto mask on the **same frame** with only the manual masks actually confirmed on that frame. Union alpha without mutating either source. A missing manual sample must never silently inherit a stationary mask from an earlier frame.
5. Expose 1-frame, selected-range and continuous tracking. Permit correction at the failure frame followed by a new run. Display verified, proposed and unresolved coverage separately; do not mark proposed coverage as verified.
6. Before exporting a required anonymization range, identify unresolved manual targets/frames and stop with an actionable message, not an unblurred success or fabricated stationary blur.

## Completed in this initial foundation commit

- `Services/Video/ManualOverlayCore.cs`: independent per-target keyframe/boundary primitive and pure BGRA alpha-mask union. No inference is made when a target's tracking sample is missing.
- `scripts/manual-overlay-regression.cs.txt` + `scripts/verify-manual-overlay.sh`: isolated checks for unrelated targets, keyframe correction/removal, Auto+multiple-manual alpha union, missing-sample/no-hold, padded rows, and malformed pixel buffers.
- `.github/workflows/quality-gate.yml`: build the branch and run the independent overlay regression along with existing checks.

**Not yet integrated:** the existing `FrameMaskProvider`, `ManualMaskKeyframeTimeline`, preview, GUI, export and persistence still run their original single-provider workflow. Adding the new core alone does **not** fix the user's application behavior. The existing tracker algorithm is untouched in this phase.

## Required next implementation and verification gates

**A. Persistence and identity:** store manual target IDs, per-target masks, keyframes and verified samples separately from Auto. Support loading existing workspaces without incorrectly assigning automatic detections to a manual target. Round-trip, undo and mask-edit invalidation tests.

**B. Preview/export integration:** for frame `f`, resolve Auto(f) plus each validated ManualTarget(f), merge once, and share that resolution between preview and export. Test multiple simultaneous faces, one tracker failure, mask edits, and matching rendered/output pixels. Do not discard Auto(f) when a manual mask is edited.

**C. Interaction and intervals:** add selected-target controls for next-frame / bounded-range / continuous tracking, stop/cancel with coherent progress, and correction-to-keyframe / resume. Verify that an unrelated Auto result at f+1 cannot truncate the target track.

**D. Tracking quality:** compare the existing optical-flow track with candidate translation-only/patch-search fallback on moving, small, low-texture, near-edge and occluded targets. Use ground-truth mask coverage and false-mask metrics. Do not weaken confidence checks or automatically approve guesses. This gate is not complete until real-video tests are available.

**E. Runtime and privacy:** measure manual entry latency, uncached random seek, long-video memory/CPU, Windows GUI and exported frames. Verify packaged macOS FFmpeg dependencies separately; the current synthetic integration test uses an installed FFmpeg library, not the bundled `.app`.

## How to run the new isolated regression

`bash scripts/verify-manual-overlay.sh` with .NET 8 installed.

Passing this regression establishes only the pure target-boundary and alpha-union contract, **not** working user-facing manual tracking, real-video accuracy or production export correctness.
