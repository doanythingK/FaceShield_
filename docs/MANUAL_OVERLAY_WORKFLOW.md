# Manual overlay and tracking — implementation status

Branch: `refactor/manual-overlay-workflow`. This document distinguishes the existing application path from the separate, not-yet-integrated multi-target prototype.

## Intended workflow

Auto masks and manually selected masks must remain independently owned. A manually confirmed mask must apply on its exact frame without detector or feature-point requirements. Each manual target ultimately needs a stable ID, its own correction keyframes, validated tracking intervals and failure boundaries. Preview and export must use current-frame Auto plus only validated manual coverage. Missing manual samples must not become a stationary held mask or a falsely successful unblurred export.

## Existing application path — implemented

- `FrameMaskProvider` retains Auto face rectangles and stored manual bitmap layers on the same frame, including workspace snapshots. Auto updates do not remove a stored manual mask; the final mask unions their alpha.
- `ManualMaskEditorLayer` creates the editor's **manual-only** bitmap: an exact stored manual correction, a validated inherited manual raster, or an empty manual raster if neither exists. Auto pixels are never made writable manual pixels. Its `ComposeWithAutomatic` returns a disposable preview-only alpha union without changing either source.
- `FramePreviewViewModel` edits that manual-only bitmap when manual keyframes are enabled. It composes same-frame Auto only for the displayed preview and saves with `SetIndependentManualMask`, which does **not** strip overlapping manually painted alpha. `FramePreviewViewModel.ManualKeyframes` compares Undo and inherited editing against manual-only baselines. The pre-existing composite `SetMask` behavior remains for non-manual-keyframe callers; this is not a blanket migration of every legacy editor.
- `ManualTrackingSourceSelection` restarts from an exact stored manual correction or a validated, independently rasterized manual track result. Once manual keyframes exist, unrelated Auto-only detections are not used as continuation sources. If there is no verified manual source, continuation fails rather than selecting another Auto face.
- The existing `ManualMaskTrackingService` performs forward/backward feature validation, similarity estimation or guarded sparse translation, scene-cut detection, and stops on missing evidence or decoder failure. It now refreshes spatially distributed features inside the **current transformed manual mask** after a validated frame, avoiding an always-shrinking feature set. This is not a detector and cannot infer reliable motion from genuinely featureless frames.
- `ManualMaskTrackStore` v3 persists continuous per-component samples and rejects missing middle/tail samples, invalid transforms and duplicate source keyframes. Strict export loading rejects corrupt state. Failed continuation only retains verified samples; missing required samples are not silently held in place.
- The existing manual tracking timeline uses stored manual bitmap keyframes as correction boundaries when manual masks exist; Auto-only legacy sessions retain their previous source path. It combines verified tracked manual alpha with same-frame Auto in preview and export.

## Independent multi-target foundation — not yet wired into application

- `ManualOverlayTarget` has a stable nonempty GUID and same-target-only correction boundaries. `ManualOverlayMaskComposer` unions same-frame Auto and multiple verified manual alpha masks without filling missing frames.
- `ManualOverlayStateStore` saves **explicit manually confirmed alpha keyframes only**, grouped by target ID. Compressed alpha, atomic replacement, malformed-data checks and video source-evidence validation are implemented. It does not yet save validated per-target tracking samples.
- `ManualOverlayWorkspaceStore` places state at a video-path-derived independent location and checks the video file's length and modification time. That is inexpensive metadata evidence, not a cryptographic content fingerprint. The application workspace/editor/export does not yet call this adapter.

## Verified checks

- `scripts/frame-mask-layer-regression.cs.txt`: legacy composite residual, independent Auto/manual storage and snapshot, plus the new manual-only editor route. A manual stroke is painted **inside an already opaque Auto face** and outside it, saved, reloaded, and tested again after Auto moves elsewhere. The overlap stroke remains present and Auto-only pixels are not persisted as manual.
- `scripts/verify-manual-overlay.sh`: validates the production view-model connections for manual-only editing, preview composition, independent persistence and manual-only inherited masks. Runs target boundary, union, alpha persistence, video identity and track continuity checks plus headless bitmap regression.
- `scripts/verify-manual-tracking-integration.sh`: FFmpeg 8.1.1-based generated-video tests for tracking, scene-cut handling, manual/Auto same-frame overlap and continuation, and a 16-frame moving target whose supporting texture shifts from one side to the other. It uses installed FFmpeg 8 libraries rather than validating packaged app dylibs.
- [Quality Gate #292](https://github.com/doanythingK/FaceShield_/actions/runs/35447980202), commit `eef2b06dc729194905a02910ff83c78db21541b0`: Windows runtime invariants/restore/build and macOS restore/build, manual overlay/bitmap regression, pyramid regression and FFmpeg integration all succeeded. The actual manual editor integration commit is [`40bba18`](https://github.com/doanythingK/FaceShield_/commit/40bba18fa3fd2c213f90afcd214792b34c80a8c3).

## Remaining work and verification limits

1. **Multi-target identity:** the application still stores one manual bitmap per frame and uses a global manual correction-keyframe sequence. There is no selected-target UI or per-target persisted tracking sample set. Adding a correction for a different manually masked face can still interrupt an earlier face's track. Do not remove the global boundary without identifying which target a correction belongs to.
2. **Per-target lifecycle:** connect explicit target creation/selection, correction/Undo, persistence, validated sample invalidation and a shared preview/export resolver. Migrate legacy workspaces only with dedicated tests and fail-closed export checks.
3. **Runtime/performance:** full-frame preview union during manual editing may allocate additional bitmaps and requires long-video/large-frame memory and CPU measurement; the headless alpha test does not establish GUI responsiveness.
4. **Visual accuracy:** test real moving faces, small or blurred targets, occlusion, rotation, scene cuts and near-edge cases. The synthetic fixtures do not establish real-face tracking accuracy or a missed-blur rate.
5. **Shipping parity:** validate Windows/macOS GUI operation, decoded preview versus encoded output pixels, startup latency and packaged macOS FFmpeg dependencies separately. A successful application build and synthetic decoder integration do not prove packaged runtime or encoded output correctness.

## Actions organization

`quality-gate.yml` runs Windows/macOS CI on code changes and cancels superseded development-branch runs; documentation-only pushes are excluded. Windows/macOS packaging workflows remain manual (`workflow_dispatch`). The temporary write-enabled editor verification workflow and its guarded patch script were deleted by their successful run; no persistent self-modifying build workflow remains from this change.
