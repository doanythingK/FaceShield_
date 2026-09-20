# Manual overlay and tracking — implementation status

Branch: `refactor/manual-overlay-workflow`. This document distinguishes the existing application path from the separate, not-yet-integrated multi-target prototype.

## Intended workflow

Auto masks and manually selected masks must remain independently owned. A manually confirmed mask must apply on its exact frame without detector or feature-point requirements. Each manual target ultimately needs a stable ID, its own correction keyframes, validated tracking intervals and failure boundaries. Preview and export must use current-frame Auto plus only validated manual coverage. Missing manual samples must not become a stationary held mask or a falsely successful unblurred export.

## Existing application path — implemented

- `FrameMaskProvider` retains Auto face rectangles and stored manual bitmap layers on the same frame, including workspace snapshots. Auto updates do not remove a stored manual mask; the final mask unions their alpha.
- `ManualMaskEditorLayer` creates the editor's **manual-only** bitmap: an exact stored manual correction, a validated inherited manual raster, or an empty manual raster if neither exists. Auto pixels are never made writable manual pixels. Its `ComposeWithAutomatic` remains a disposable, non-cached compositor for other callers and tests.
- `FramePreviewViewModel` edits the manual-only bitmap when manual keyframes are enabled. It saves using `SetIndependentManualMask`, which does **not** strip overlapping manually painted alpha. `FramePreviewViewModel.ManualKeyframes` compares Undo and inherited editing against manual-only baselines. The pre-existing composite `SetMask` behavior remains for non-manual-keyframe callers; this is not a blanket migration of every legacy editor.
- **Manual preview cache:** the actual view-model uses `ManualPreviewCompositeCache`, not a new disposable full-frame Auto bitmap on each brush refresh. It caches the current frame's Auto raster and preview-only Auto/manual alpha union, updates only the dirty rectangle for an in-place brush edit, and forces a full redraw when the frame, Auto face collection, manual bitmap reference or session changes. Cached masks are released on frame/session replacement and disposal. Neither the editable manual bitmap nor export mask semantics are changed. This targets repetitive brush-preview work; it does **not** eliminate exact-frame seek/indexing or tracking decode time, and no real-video speedup percentage has been measured.
- `ManualTrackingSourceSelection` restarts from an exact stored manual correction or a validated, independently rasterized manual track result. Once manual keyframes exist, unrelated Auto-only detections are not used as continuation sources. If there is no verified manual source, continuation fails rather than selecting another Auto face.
- `ManualMaskTrackingService` performs forward/backward feature validation, similarity estimation or guarded sparse translation, scene-cut detection, and stops on missing evidence or decoder failure. It refreshes spatially distributed features inside the **current transformed manual mask** after a validated frame, avoiding an always-shrinking feature set. This is not a detector and cannot infer reliable motion from genuinely featureless frames.
- `ManualMaskTrackStore` v3 persists continuous per-component samples and rejects missing middle/tail samples, invalid transforms and duplicate source keyframes. Strict export loading rejects corrupt state. Failed continuation only retains verified samples; missing required samples are not silently held in place.
- The existing manual tracking timeline uses stored manual bitmap keyframes as correction boundaries when manual masks exist; Auto-only legacy sessions retain their previous source path. It combines verified tracked manual alpha with same-frame Auto in preview and export.

## Independent multi-target foundation — not yet wired into application

- `ManualOverlayTarget` has a stable nonempty GUID and same-target-only correction boundaries. `ManualOverlayMaskComposer` unions same-frame Auto and multiple verified manual alpha masks without filling missing frames.
- `ManualOverlayStateStore` saves **explicit manually confirmed alpha keyframes only**, grouped by target ID. Compressed alpha, atomic replacement, malformed-data checks and video source-evidence validation are implemented. It does not yet save validated per-target tracking samples.
- `ManualOverlayWorkspaceStore` places state at a video-path-derived independent location and checks the video file's length and modification time. That is inexpensive metadata evidence, not a cryptographic content fingerprint. The application workspace/editor/export does not yet call this adapter.

## Verified checks

- `scripts/frame-mask-layer-regression.cs.txt`: legacy composite residual, independent Auto/manual storage and snapshot, plus the manual-only editor route. A manual stroke is painted **inside an already opaque Auto face** and outside it, saved, reloaded, and tested again after Auto moves elsewhere. The overlap stroke remains present and Auto-only pixels are not persisted as manual. The preview-cache regression verifies reusable bitmap identity for an in-place dirty edit, manual/Auto alpha preservation, fresh full recomposition on Auto relocation, fresh output on frame change, and no modification of the manual source.
- `scripts/verify-manual-overlay.sh`: validates the production view-model connections for manual-only editing, cached preview composition, independent persistence and manual-only inherited masks. Runs target boundary, union, alpha persistence, video identity and track continuity checks plus headless bitmap regression.
- `scripts/verify-manual-tracking-integration.sh`: generated-video tests for tracking, scene-cut handling, manual/Auto same-frame overlap and continuation, and a moving target whose supporting texture shifts from one side to the other. Uses runner-installed FFmpeg libraries rather than validating packaged app dylibs.
- [Quality Gate #296](https://github.com/doanythingK/FaceShield_/actions/runs/35488391695) on implementation commit [`50f886c`](https://github.com/doanythingK/FaceShield_/commit/50f886c1e20cb5342fe6c299364ea3844d20139e): Windows runtime invariants, restore and build; macOS restore, build, manual-overlay/cache headless regression, pyramid tests and synthetic FFmpeg integrations all succeeded. This does not establish Windows GUI operation, measurable real-video frame-rate improvement or output-file pixel parity.

## Remaining work and verification limits

1. **Multi-target identity:** the application still stores one manual bitmap per frame and uses a global manual correction-keyframe sequence. There is no selected-target UI or per-target persisted tracking sample set. Adding a correction for a different manually masked face can still interrupt an earlier face's track. Do not remove the global boundary without identifying which target a correction belongs to.
2. **Per-target lifecycle:** connect explicit target creation/selection, correction/Undo, persistence, validated sample invalidation and a shared preview/export resolver. Migrate legacy workspaces only with dedicated tests and fail-closed export checks.
3. **Runtime/performance:** the preview cache reduces repetitive Auto rasterization and full-frame composition during brush edits, but actual Windows/macOS GUI responsiveness, high-resolution memory use, cold uncached-frame seek and long-video tracking latency remain unmeasured or unresolved. No speedup percentage is claimed.
4. **Visual accuracy:** test real moving faces, small or blurred targets, occlusion, rotation, scene cuts and near-edge cases. Synthetic fixtures do not establish real-face tracking accuracy or a missed-blur rate.
5. **Shipping parity:** validate Windows/macOS GUI operation, decoded preview versus encoded output pixels, startup latency and packaged macOS FFmpeg dependencies separately. A successful application build and synthetic decoder integration do not prove packaged runtime or encoded output correctness.

## Actions organization

`quality-gate.yml` runs Windows/macOS CI on code changes and cancels superseded development-branch runs; documentation-only pushes are excluded. Windows/macOS packaging workflows remain manual (`workflow_dispatch`). The temporary write-enabled editor and preview-cache verification workflows and their patch scripts were deleted by their successful runs; neither remains in branch HEAD.
