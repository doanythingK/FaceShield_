# Manual overlay workflow — implementation status

Branch: `refactor/manual-overlay-workflow` (based on `refactor/manual-mask-keyframes` at `f9ad497a`).

## User workflow and non-negotiable invariants

1. Auto detects faces and writes Auto masks. A user may add **an independent manual target** for a missed face or another object. The manual target has a stable ID, its own explicit correction keyframes and its own tracking samples.
2. Each explicit manual keyframe is usable on its exact frame even if any optical-flow/feature tracker fails. No face detector or feature count is a prerequisite for manual editing.
3. A target's forward tracking stops at its **own** next correction keyframe, a selected end, EOF, or a documented tracking failure. Another face's Auto detection and another manual target's keyframe must not interrupt it.
4. Compose the Auto mask on the **same frame** with only the manual masks actually confirmed on that frame. Union alpha without mutating either source. A missing manual sample must never silently inherit a stationary mask from an earlier frame.
5. Expose 1-frame, selected-range and continuous tracking. Permit correction at the failure frame followed by a new run. Display verified, proposed and unresolved coverage separately; do not mark proposed coverage as verified.
6. Before exporting a required anonymization range, identify unresolved manual targets/frames and stop with an actionable message, not an unblurred success or fabricated stationary blur.

## Implemented in the isolated foundation

- `Services/Video/ManualOverlayCore.cs`: independent per-target keyframe/boundary primitive and pure BGRA alpha-mask union. A missing manual mask is not replaced with the mask from an earlier frame.
- `Services/Video/ManualOverlayStateStore.cs`: independent JSON state file containing stable target IDs and **explicit manual alpha keyframes only**, with compressed mask alpha, atomic replacement, exact-length decompression and required caller-supplied video source evidence. The automatic-mask store is not modified. Missing state is empty; invalid, corrupt or source-mismatched state throws rather than silently discarding coverage. The caller is responsible for selecting a workspace-specific storage path and producing source evidence; a video-path-aware application adapter is not yet implemented.
- `scripts/manual-overlay-regression.cs.txt` + `scripts/verify-manual-overlay.sh`: isolated tests for target boundaries, Auto+multiple-manual alpha union, no stationary hold, row stride, saved alpha/keyframe/ID round-trip, source mismatch, duplicate IDs/keyframes and corrupt compressed data.
- `.github/workflows/quality-gate.yml`: Windows/macOS builds, isolated manual overlay regression, existing pyramid regression and synthetic decoded tracker integration on the manual branches. CI on development branches cancels superseded runs; documentation-only push commits do not start a new run. `main` runs are not auto-cancelled; PR and push runs use separate concurrency groups. Windows/macOS jobs have 30/45-minute timeouts.

**Not yet integrated:** `FrameMaskProvider`, `ManualMaskKeyframeTimeline`, the editor, preview, export and actual workspace persistence still use their existing single-provider workflow. The new store is not called by the application yet. Existing legacy masks are not migrated. This stage does **not** change the user's current manual-mode behavior. The tracker algorithm has not been altered.

## Remaining implementation and verification gates

**A. Complete persistence integration:** wire a stable workspace storage path and video evidence into the independent store; load/save manual targets without converting Auto detections into manual targets. Persist *validated tracking samples* separately from explicit keyframes. Test legacy workspace loading, undo and mask-edit invalidation. Store/load must not block manual-mode startup with unnecessary decoding.

**B. Preview/export integration:** for frame `f`, resolve Auto(f) plus each validated ManualTarget(f), merge once, and share that resolution between preview and export. Test multiple simultaneous faces, tracker failure, mask edits, and matching rendered/output pixels. Do not discard Auto(f) when a manual mask is edited.

**C. Interaction and intervals:** add selected-target controls for next-frame / bounded-range / continuous tracking, stop/cancel with coherent progress, and correction-to-keyframe / resume. Verify that unrelated Auto results at f+1 cannot truncate a target track.

**D. Tracking quality:** compare the existing optical-flow track with candidate translation-only/patch-search fallback on moving, small, low-texture, near-edge and occluded targets. Use ground-truth mask coverage and false-mask metrics. Do not weaken confidence checks or automatically approve guesses. This gate is not complete until real-video tests are available.

**E. Runtime and privacy:** measure manual entry latency, uncached random seek, long-video memory/CPU, Windows GUI and exported frames. Verify packaged macOS FFmpeg dependencies separately; the current synthetic integration test uses an installed FFmpeg library, not the bundled `.app`.

## Running the isolated regression

`bash scripts/verify-manual-overlay.sh` with .NET 8 installed.

Passing this regression establishes only the isolated target boundaries, mask composition and explicit-keyframe persistence contracts. It does **not** demonstrate a working user-facing manual tracking workflow, real-video accuracy or production export correctness. Historical GitHub Actions runs have not been deleted.
