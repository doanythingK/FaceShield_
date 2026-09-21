# Manual overlay and tracking — implementation status

Working branch: `refactor/manual-overlay-workflow`. **All 2026-09-21 changes are unbuilt and untested at the user's request.** A source connection is not evidence of a successful GUI run, accurate real-face tracking or correct encoded output. No merge to `main` was requested.

## Required user workflow

A manually marked missed face is tracked independently of Auto detections and other manually marked faces. Each face owns a stable GUID, editable mask, correction keyframes, verified motion samples and failure boundary. Correcting A must not delete B's coverage. Unverified frames must not reuse the last mask. Preview/export must combine current-frame Auto, pre-existing legacy manual coverage and all verified face-specific masks. An unresolved target gap or corrupt saved state must fail closed rather than emit a silently unblurred face.

## Existing implementation verified before 2026-09-21

- `FrameMaskProvider` owns separate same-frame Auto face rectangles and legacy manual bitmap pixels; Auto overlap does not erase independently painted manual alpha. The editor uses `ManualMaskEditorLayer.CreateEditableMask()` and saves legacy manual-only pixels through `SetIndependentManualMask()`. This earlier overlap issue is already fixed.
- The legacy `ManualPreviewCompositeCache` caches Auto and dirty-region blending. The new per-target preview adds another full-frame composite, so the old cache results are not a measured performance claim for the new workflow.
- `ManualMaskTrackingService` estimates and validates feature motion, refreshes features inside the transformed mask and stops on scene cut, decoder failure or inadequate evidence. The legacy global `ManualMaskTrackStore` v3 validates contiguous samples and retains a longer valid same-source retry.
- [Quality Gate #299](https://github.com/doanythingK/FaceShield_/actions/runs/35489593929) on [`554081b`](https://github.com/doanythingK/FaceShield_/commit/554081b7c95c279dee22b5d51f3ad4d447c8d615) passed configured Windows/macOS builds and synthetic regressions **for the earlier code only**. Neither gate nor synthetic video establishes real-face accuracy, GUI behavior, packaged runtime or encoded pixel parity.

## Target-owned implementation introduced 2026-09-21 — NOT VERIFIED

- `ManualOverlayTarget` defines stable GUID and same-target correction boundary. `ManualOverlayTargetWorkspace` owns selected target, distinct alpha keyframes, validated segments, same-target truncation and no-hold frame resolution. A correction to A does not alter B's motion samples. `ManualOverlayStateStore` v2 atomically saves per-target compressed alpha, samples and stop metadata, accepts v1 explicit-only files as empty-track targets and rejects malformed/source-mismatched state. Legacy global masks/tracks are **not automatically mapped** to face IDs.
- `ManualOverlayTargetTrackingService` passes exactly one selected face's explicit mask and next same-target correction bound to the existing tracker, fingerprints that face's alpha and saves only its validated result. GUI `FramePreviewViewModel.ManualTargets.cs` now calls that adapter from `TrackSelectedManualTargetCommand` for the selected face; it is no longer just a standalone entry point.
- `WorkspaceView.axaml.cs` builds a minimal face creation/selection control and switches the visible tracking button to the selected-target command. The target editor reads only selected target alpha; brush-release and Undo save target-owned keyframes. `FramePreviewViewModel.ManualKeyframes.cs` does not apply legacy/global inherited-mask and Undo comparisons to selected faces. Source-level functionality only: GUI interaction remains **확실하지 않음 (unverified)**.
- `FramePreviewViewModel.ManualTargets.cs` composites current-frame Auto plus the existing legacy manual mask, all other targets' exact/verified masks and the selected editable bitmap for a preview. The added composition is whole-frame and may incur substantial memory/CPU cost. Auto/legacy handling is retained rather than assigning their pixels to a new target.
- `WorkspaceExportCoordinator.cs` now wraps its existing `ManualMaskKeyframeTimeline.CreateExportMaskLease(_maskProvider).Provider` with `ManualOverlayTargetExportMaskProvider.WrapIfPresent(input, lease.Provider)` before `VideoExportService`. Target export rereads the independent workspace with source evidence and composes target-owned exact or fully sampled alpha onto the unchanged Auto/legacy export snapshot. The earlier failed large-file tool update was **not committed**; the subsequent smaller replacement succeeded at [`2c73ed2`](https://github.com/doanythingK/FaceShield_/commit/2c73ed2ca6779a45af6b1da80a2be037785ddbb5).
- `ManualOverlayTargetExportMaskProvider` rejects invalid/mismatched persisted target state, any unresolved target tracking failure, inconsistent dimensions and any frame after a nonempty target keyframe without a verified target sample. It allows an explicit zero-alpha correction to mark that **same target absent** from a given frame until its next explicit nonempty correction. An exact same-target correction at a failure frame resolves that boundary, not a different face's correction. Missing target frames are not filled with stationary masks. The per-target editor now saves fully erased masks as explicit zero-alpha keyframes instead of removing the boundary. Tracking is disabled on a zero-alpha source.
- The earlier temporary `ManualTargetExportGuard` hook remains present in `ToolPanelViewModel` but is assigned `null` in `WorkspaceView.axaml.cs` now that standard save invokes the target-aware export provider. The unused legacy-only status method remains in `FramePreviewViewModel.ManualTargetUi.cs`; these are cleanup candidates, **not evidence of export testing**.

## Remaining work / known limitations

1. **Deferred verification:** No build, tests, Actions checks, GUI runs, decoded/encoded frame comparisons, real-face benchmark or memory measurements have been performed on any 2026-09-21 commit. A GitHub push may automatically trigger existing CI, but no CI result is being claimed. Compile/runtime correctness remains unknown.
2. **Persistence on unusual navigation:** Normally brush release/Undo saves the target workspace before the legacy frame persistence path. An in-progress dirty target editor during abnormal frame navigation, direct disposal or save failure may still reach the old global `PersistCurrentMask` path. Eliminate this possibility with a direct target-aware persistence hook before declaring the workflow complete.
3. **Selected target and source lifetimes:** The target ID persists but selection currently defaults to the first target when reopening. Explicitly persist the last selected ID, resolve source switches/late initialization safely and ensure tracking cancellation/disposal cannot publish a stale result. A shorter retained retrack can still produce a misleading progress/status message because the adapter returns the attempted result rather than the retained interval.
4. **Legacy compatibility:** Preserve existing global masks/tracks separately; do not guess which face owns them. A dedicated, opt-in migration is needed where face identity is unambiguous. Review programmatic export entry points outside the standard `WorkspaceExportCoordinator` and confirm they cannot bypass target-aware resolution.
5. **Accuracy and performance:** Evaluate real moving faces, motion blur, occlusion, rotation, scene cuts and near-edge targets. New full-frame target preview and per-frame alpha fingerprinting may be expensive. Check Windows/macOS UI, long-video responsiveness, packaged FFmpeg and actual encoded output parity only when the user resumes verification.

## Change-log convention

Record **why**, **what source was changed**, **verification performed or explicitly deferred**, **remaining gaps** and **relevant commit SHA/URL** for every implementation change in this document, on the same branch. Do not mark planned or untested behavior verified.

### 2026-09-20 — preserve verified tracking on retry

- **Why:** A shorter or failing retry could erase a longer verified global segment.
- **What:** Retain longer same-source coverage; prefer equal-length successful over failed retry. [`554081b`](https://github.com/doanythingK/FaceShield_/commit/554081b7c95c279dee22b5d51f3ad4d447c8d615).
- **Verification:** [Quality Gate #299](https://github.com/doanythingK/FaceShield_/actions/runs/35489593929) passed prior Windows/macOS build/synthetic regressions.
- **Remaining:** That historical global path has no independent target identity and was not real-face validated.

### 2026-09-21 — target-owned state, segments and frame resolver

- **Why:** A global manual timeline allowed another face's correction to interrupt tracking; the prototype saved only explicit keyframes.
- **What:** Added `ManualOverlayTargetWorkspace.cs` and extended `ManualOverlayStateStore.cs` to v2 per-target validated segment storage. [`b62ed51`](https://github.com/doanythingK/FaceShield_/commit/b62ed51e63502c7eccf285165a5885f93f6ee90b), [`7dbd230`](https://github.com/doanythingK/FaceShield_/commit/7dbd23030f5ab99c289b5fca4ed544b31d2d6323).
- **Verification:** Deferred at user request; no builds/tests.
- **Remaining:** Application integration, persisted selection and legacy migration.

### 2026-09-21 — target-specific tracking adapter

- **Why:** The target workspace could store samples but did not call the application tracker.
- **What:** Added `ManualOverlayTargetTrackingService.cs` calling `ManualMaskTrackingService` from one target's exact mask with its own correction boundary. [`c4ec57e`](https://github.com/doanythingK/FaceShield_/commit/c4ec57eb54e2c062b8509ccd08961bf6ecfddee4).
- **Verification:** Deferred; no real-video or synthetic result claimed.
- **Remaining:** GUI correctness and shorter-retry status reconciliation.

### 2026-09-21 — selectable editor, target Undo, tracking button and multi-face preview

- **Why:** The separate store/adapter did not permit A versus B selection or editing, Undo and track execution in the app.
- **What:** Added `FramePreviewViewModel.ManualTargets.cs`, `FramePreviewViewModel.ManualTargetUi.cs`; updated `FramePreviewViewModel.ManualKeyframes.cs` and `WorkspaceView.axaml.cs`. Tracks are initiated from the selected GUID; preview combines Auto/legacy/all-target alpha. Commits [`329a491`](https://github.com/doanythingK/FaceShield_/commit/329a4917463b6260a74fa07623416d0734e0ebec), [`aff847b`](https://github.com/doanythingK/FaceShield_/commit/aff847be3054cc415dad124b9d4de34bb0432f7f), [`bcff467`](https://github.com/doanythingK/FaceShield_/commit/bcff467b324478fb51e87c054dbaa83b753398f2), [`003c2d7`](https://github.com/doanythingK/FaceShield_/commit/003c2d7dc2316e40b9cb6f9fd9701a74a38fd093).
- **Verification:** Deferred; no build or GUI tests.
- **Remaining:** Abnormal-navigation edit persistence, GUI/preview parity and performance.

### 2026-09-21 — target-aware final export and fail-closed gaps

- **Why:** Target editing without final export would silently omit faces; allowing a tracked face's unsampled future frames would also create an apparently successful unblurred video.
- **What:** Added `ManualOverlayTargetExportMaskProvider.cs` ([`095e18e`](https://github.com/doanythingK/FaceShield_/commit/095e18eb4099410a5cc2bcde1155bd7e08e53332)) and connected it in `WorkspaceExportCoordinator.cs` ([`2c73ed2`](https://github.com/doanythingK/FaceShield_/commit/2c73ed2ca6779a45af6b1da80a2be037785ddbb5)). Target failure correction at the exact boundary is recognized ([`2325381`](https://github.com/doanythingK/FaceShield_/commit/2325381b04ffbd931c0644c3dacf2154d191c5e1)); missing per-target samples now throw during export ([`9853678`](https://github.com/doanythingK/FaceShield_/commit/9853678a99d9fa1dcc3a98c2346faa025f348c14)). Fully erased target masks now persist as explicit absence keyframes and percentage progress uses the applicable correction/EOF bound ([`8ffcbcd`](https://github.com/doanythingK/FaceShield_/commit/8ffcbcd98c1e6d963ed85b378fc31d43e70e6e4d)). The temporary legacy save guard was added during integration ([`5a8c788`](https://github.com/doanythingK/FaceShield_/commit/5a8c788b40bd06aca7443f586898d8d57950db50)) then disabled when export was connected ([`3402bfb`](https://github.com/doanythingK/FaceShield_/commit/3402bfb73eaca75a73351f4f7aa2516dff823c99)). UI readiness now uses a live binding rather than a one-time disabled state ([`5bb0b8b`](https://github.com/doanythingK/FaceShield_/commit/5bb0b8b3012c3a495874e74e51958e8b25692e04)).
- **Verification:** Entire step deferred at user request; no build, regression, Actions result or encoded video is claimed. First attempted oversized coordinator write was blocked and did not change the branch; the smaller follow-up succeeded.
- **Remaining:** Verify actual compilation and GUI/export operation later, handle abnormal dirty-editor navigation, source switching, selection persistence and all export entry points. Visual tracking accuracy remains unknown.
