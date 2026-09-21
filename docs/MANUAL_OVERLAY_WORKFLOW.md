# Manual overlay and tracking — implementation status

Branch: `refactor/manual-overlay-workflow`. **All 2026-09-21 changes remain unbuilt and untested at the user's request.** Do not infer GUI operation, successful encoding or real-face accuracy from source edits. The pre-existing Auto/legacy manual workflow remains separate from the new GUID-owned target state.

## Intended behavior

A missed face is marked once and tracked independently of unrelated Auto detections and other manually selected faces. Each face has stable identity, its own editable mask, correction keyframes, validated motion samples and failure boundary. A correction to face A must not remove face B's coverage. On insufficient evidence, stop without holding the previous mask in place. Current-frame Auto and all verified manual targets must be composed by preview and export. A missing or corrupt target state must not produce an apparently successful unblurred export.

## Existing verified path — before 2026-09-21

- `FrameMaskProvider` stores current-frame Auto faces and manual rasters independently, retaining overlap alpha and snapshot ownership. The legacy global manual workflow is still available; its stored bitmap is **not automatically assigned to a newly created face ID**.
- `ManualMaskEditorLayer` edits manual-only pixels, and `FramePreviewViewModel.PersistCurrentMask()` uses `SetIndependentManualMask()` for the legacy manual path. Existing Auto overlap preservation has already been implemented; do not repeat this fix.
- `ManualPreviewCompositeCache` caches Auto raster and dirty-region composition in the legacy preview route. The new multi-target preview presently performs additional whole-frame composition; it is **not covered by the old cache performance assertion**.
- `ManualMaskTrackingService` uses feature validation, guarded motion estimation, feature refresh and scene-cut/failure stops. Its output is not verified for real face videos. Legacy `ManualMaskTrackStore` v3 rejects missing contiguous samples and retains a longer still-current run over a shorter retry.
- [Quality Gate #299](https://github.com/doanythingK/FaceShield_/actions/runs/35489593929) on [`554081b`](https://github.com/doanythingK/FaceShield_/commit/554081b7c95c279dee22b5d51f3ad4d447c8d615) passed the configured Windows/macOS builds and synthetic checks. This is **not** verification of the 2026-09-21 target-owned code or packaged GUI/encoded output.

## 2026-09-21 target-owned code — implemented but unverified

- `ManualOverlayTarget` defines stable GUID identity and next **same-target** correction boundary; `ManualOverlayMaskComposer` combines supplied current-frame Auto/legacy alpha with independently resolved target alpha. Missing target samples are omitted rather than replaced with stationary pixels.
- `ManualOverlayStateStore` v2 atomically stores each target's explicit compressed alpha keyframes and validated tracking segments/samples/failure metadata. It reads v1 explicit-only data without inventing tracks. Source video evidence and corrupted/duplicated/invalid target state are rejected. Legacy global masks/tracks are not migrated or reinterpreted.
- `ManualOverlayTargetWorkspace` owns target creation, selection, correction, track storage, same-target correction clipping and per-frame verified resolution. A correction to A does not invalidate B. A shorter retry cannot replace a longer same-source stored segment. `ManualOverlayTargetTrackingService` invokes the established feature tracker with one target's exact source and next same-target boundary, fingerprints individual target alpha, and saves target-owned samples.
- `FramePreviewViewModel.ManualTargets.cs` connects the target workspace to **actual** edit bitmap selection, brush-release persistence, target Undo persistence, targeted tracking command and full multi-target preview composition. `FramePreviewViewModel.ManualKeyframes.cs` skips legacy inherited mask/Undo comparison when a target is selected. `WorkspaceView.axaml.cs` adds a minimal create/select control and routes the visible tracking button to the target-aware command when one is selected. Existing global tracking buttons remain for no-target legacy sessions. These are source connections, **not a claim of working GUI**.
- Target editor changes save the independent workspace on brush-release/Undo; a target's selected bitmap is not passed through the legacy single-raster `PersistCurrentMask` after successful target save. Auto/legacy current-frame coverage plus every verified target and the in-progress selected bitmap are used in the added preview composition. No target ID is inferred from pre-existing global masks.
- `ManualOverlayTargetExportMaskProvider` was added to union the existing export provider's Auto/legacy bitmap with each exact or fully sampled target mask, rejecting malformed state and unresolved target failures. **The existing `WorkspaceExportCoordinator` still constructs `VideoExportService` with the legacy provider and does not invoke this adapter. The attempted coordinator replacement was not accepted by the GitHub tool and is not in the branch.** Consequently target-aware final export is **NOT implemented end-to-end**.
- To avoid a misleading legacy-only video from the standard manual-mode save button, `ToolPanelViewModel` now has a guard invoked before `SaveRequested`, wired by `WorkspaceView.axaml.cs` to `FramePreviewViewModel.CanExportLegacyMask()`. It refuses this UI save when target-owned corrections exist and displays a tracking status explanation. This guard **does not cover direct/programmatic export calls or Auto-run export paths**, which remain a safety gap until the export coordinator is connected. The target workspace is independently saved on editing even when video export is blocked.

## Remaining implementation (priority)

1. **Finish export coordinator integration:** wrap `ManualMaskKeyframeTimeline.CreateExportMaskLease(_maskProvider).Provider` using `ManualOverlayTargetExportMaskProvider.WrapIfPresent(input, lease.Provider)` before creating `VideoExportService`. Extend the actual export preflight to reject unresolved target failure/missing required coverage; do not rely on the UI save guard for programmatic exports. Only then retire the temporary UI guard. This is the most important outstanding functional/safety gap.
2. **Finish source-target continuation workflow:** the new tracking command requires an explicit correction at the current frame; inherited verified frames are displayable but cannot be silently promoted to a new keyframe. Ensure user corrections at a failure boundary allow continuation and a previous verified prefix remains intact; resolve failure metadata on an exact same-target correction without clearing another target.
3. **Lifecycle/compatibility:** restore explicit target selection consistently (currently first stored target is selected on open), handle save/load/close while edits or decoding are active, and define migration of existing global single-bitmap masks only where identity is unambiguous. In-progress edits during abnormal frame navigation may still take the legacy `PersistCurrentMask` path; handle this before calling the UI flow complete.
4. **Preview performance and parity:** target composition currently builds full-sized alpha buffers. Optimize only after correctness; align preview and export around a single target resolver including unfinished selected edits, exact corrections and verified samples. Verify bitmap ownership and undo behavior in GUI.
5. **Deferred verification:** no build, static checks, synthetic/headless tests, real-face tracking, Windows/macOS GUI, memory benchmark, encoded pixel parity or packaged FFmpeg check has been run for the 2026-09-21 commits. Actual behavior remains **확실하지 않음 (unverified)**.

## Change-log convention

Every implementation or fix must update **this document in the same working branch** with why, what, verification status, remaining gaps and related commits. Do not report intended behavior as tested behavior. The user deferred verification runs, not the requirement to keep honest implementation records.

### 2026-09-20 — preserve verified tracking on retry

- **Why:** A shorter or failing retry could erase an existing longer verified global segment.
- **What:** Retain longer same-source coverage and prefer an equal-length successful interval over a failed retry. Commit [`554081b`](https://github.com/doanythingK/FaceShield_/commit/554081b7c95c279dee22b5d51f3ad4d447c8d615).
- **Verification:** [Quality Gate #299](https://github.com/doanythingK/FaceShield_/actions/runs/35489593929) passed configured Windows/macOS build and synthetic regressions. Real-face and encoded-output parity were not established.
- **Remaining:** No target identity in that historical global path.

### 2026-09-21 — per-target ownership, storage, resolver (unverified)

- **Why:** Global manual keyframe boundaries allowed different faces to interrupt one another; target-only prototype did not persist tracking samples.
- **What:** `ManualOverlayTargetWorkspace.cs` added GUID-owned target state, same-target correction clipping, validated segments and complete-frame resolution. `ManualOverlayStateStore.cs` v2 persists keyframes and tracks atomically while reading v1 explicit-only. Commits [`b62ed51`](https://github.com/doanythingK/FaceShield_/commit/b62ed51e63502c7eccf285165a5885f93f6ee90b), [`7dbd230`](https://github.com/doanythingK/FaceShield_/commit/7dbd23030f5ab99c289b5fca4ed544b31d2d6323).
- **Verification:** Deferred; no new check run by request.
- **Remaining:** Actual UI, lifecycle and final export integration.

### 2026-09-21 — target-specific tracker adapter (unverified)

- **Why:** The target workspace did not yet invoke the application motion tracker.
- **What:** `ManualOverlayTargetTrackingService.cs` converts a selected target's exact alpha to a tracker source, applies that target's next correction as boundary and saves samples with its own fingerprint. Commit [`c4ec57e`](https://github.com/doanythingK/FaceShield_/commit/c4ec57eb54e2c062b8509ccd08961bf6ecfddee4).
- **Verification:** Deferred; no build/video run.
- **Remaining:** UI tracking and export linkage (UI command was connected by later commits below).

### 2026-09-21 — selected-target editor, independent tracking command and composite preview (unverified)

- **Why:** A standalone target store/adapter did not let users select A versus B, edit only that face, undo its changes, or run independent tracking through the workspace UI.
- **What:** `FramePreviewViewModel.ManualTargets.cs` wires target workspace open, create/select, selected-alpha bitmap, per-target brush-release/Undo save, selected-source tracking and combined Auto/legacy/all-target preview. `FramePreviewViewModel.ManualKeyframes.cs` prevents global inherited-mask/Undo logic from treating a chosen face as the whole manual bitmap. `WorkspaceView.axaml.cs` adds essential create/select/track controls. `FramePreviewViewModel.ManualTargetUi.cs` exposes forced preview refresh and export readiness. Commits [`329a491`](https://github.com/doanythingK/FaceShield_/commit/329a4917463b6260a74fa07623416d0734e0ebec), [`aff847b`](https://github.com/doanythingK/FaceShield_/commit/aff847be3054cc415dad124b9d4de34bb0432f7f), [`bcff467`](https://github.com/doanythingK/FaceShield_/commit/bcff467b324478fb51e87c054dbaa83b753398f2), [`003c2d7`](https://github.com/doanythingK/FaceShield_/commit/003c2d7dc2316e40b9cb6f9fd9701a74a38fd093).
- **Verification:** Deferred by user. No build, test, GUI or real-face accuracy result claimed.
- **Remaining:** Finish export, abnormal navigation persistence, lifecycle and real GUI verification.

### 2026-09-21 — export adapter and temporary legacy UI save guard (unverified)

- **Why:** A target-aware editor without target-aware final video export could silently omit manually tracked faces. The pre-existing Save command still invoked only the global-timeline export provider.
- **What:** Added `ManualOverlayTargetExportMaskProvider.cs` for target-aware alpha union over Auto/legacy export masks, with source-state checks. Added `ToolPanelViewModel.ManualTargetExportGuard` and bound it to the target editor from `WorkspaceView.axaml.cs`, returning a visible status instead of invoking the legacy UI save when target corrections exist. Commits [`095e18e`](https://github.com/doanythingK/FaceShield_/commit/095e18eb4099410a5cc2bcde1155bd7e08e53332), [`5a8c788`](https://github.com/doanythingK/FaceShield_/commit/5a8c788b40bd06aca7443f586898d8d57950db50), [`9785752`](https://github.com/doanythingK/FaceShield_/commit/9785752cca7c58ce21f13ca6e532e1113d3b8834), [`7d8c663`](https://github.com/doanythingK/FaceShield_/commit/7d8c663f12faa8d7ba0438cb6a523fb582c17448).
- **Verification:** Deferred; no code execution. The attempted update to `WorkspaceExportCoordinator.cs` was blocked by the connector and **was not committed**. Do not claim that the new export adapter is invoked by final export.
- **Remaining:** Connect actual export coordinator and preflight, handle programmatic/Auto export guard, and verify preview/encoded-mask parity later.
