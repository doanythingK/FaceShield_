# Manual overlay and tracking — implementation status

Working branch: `refactor/manual-overlay-workflow`. **All 2026-09-21 changes remain unbuilt and untested at the user's request.** Source connections do not establish successful GUI operation, accurate real-face tracking or correct encoded output. No merge to `main` was requested.

## Required user workflow

A missed face manually marked by the user is tracked independently of Auto detections and other manually marked faces. Each face owns a stable GUID, editable mask, correction keyframes, verified motion samples and failure boundary. Correcting A must not delete B's coverage. Unverified frames must not reuse a stationary prior mask. Preview/export must combine current-frame Auto, pre-existing legacy manual coverage and verified face-specific masks. An unresolved target gap or corrupt saved state must not yield a silently unblurred export.

## Existing implementation verified before 2026-09-21

- `FrameMaskProvider` owns separate same-frame Auto face rectangles and legacy manual bitmap pixels; Auto overlap does not erase independently painted manual alpha. The editor uses `ManualMaskEditorLayer.CreateEditableMask()` and saves legacy manual-only pixels through `SetIndependentManualMask()`. This earlier overlap issue was already fixed.
- The legacy `ManualPreviewCompositeCache` caches Auto and dirty-region blending. The new per-target preview adds a whole-frame composition; the prior cache result is not a measured performance claim for the new workflow.
- `ManualMaskTrackingService` estimates and validates feature motion, refreshes points within the transformed mask, and stops on scene cuts, decoder failure or insufficient evidence. Legacy global `ManualMaskTrackStore` v3 validates contiguous samples and retains longer valid same-source retries.
- [Quality Gate #299](https://github.com/doanythingK/FaceShield_/actions/runs/35489593929) on [`554081b`](https://github.com/doanythingK/FaceShield_/commit/554081b7c95c279dee22b5d51f3ad4d447c8d615) passed configured Windows/macOS builds and synthetic regressions **for the earlier code only**. Neither those checks nor synthetic video establishes real-face accuracy, GUI behavior, packaged runtime or encoded pixel parity.

## Target-owned implementation introduced 2026-09-21 — NOT VERIFIED

- `ManualOverlayTarget` defines stable GUID and same-target correction boundary. `ManualOverlayTargetWorkspace` owns independent alpha keyframes, validated segments, per-target correction truncation and a frame resolver without stationary fallback. Correcting A does not alter B's samples. `ManualOverlayStateStore` v2 atomically saves each target's compressed alpha, samples and stop metadata; reads v1 explicit-only files without inventing tracks; rejects malformed/source-mismatched data. Legacy global masks/tracks are **not assigned to new face IDs**.
- `ManualOverlayTargetTrackingService` passes one selected face's explicit mask and next same-target boundary to the existing tracker, fingerprints that face's alpha and saves its validated results. `FramePreviewViewModel.ManualTargets.cs` invokes it via `TrackSelectedManualTargetCommand`.
- `WorkspaceView.axaml.cs` adds essential target creation, selection and selected-face tracking controls. The editor reads selected-target alpha; brush-release and Undo save that target's corrections. `FramePreviewViewModel.ManualKeyframes.cs` bypasses legacy inherited-mask/Undo comparisons for selected targets. `FramePreviewViewModel.ManualTargetUndo.cs` retains bounded, in-memory Undo snapshots keyed by (target GUID, frame index); the selector/new-face UI preserves and restores them around target changes. The new UI handler also restores an archived stack when a completed preview returns to its frame, without blindly restoring on an exact-frame thumbnail. These connections are **not verified GUI behavior**.
- `FramePreviewViewModel.ManualTargets.cs` composites current-frame Auto and legacy manual coverage, other targets' exact/verified masks and the selected editable bitmap. On initial manual workspace attachment, `WorkspaceView.axaml.cs` forces a target-aware preview refresh. The composite is whole-frame and its memory/CPU costs are unmeasured.
- `WorkspaceExportCoordinator.cs` wraps its existing legacy/Auto export-mask lease with `ManualOverlayTargetExportMaskProvider.WrapIfPresent(input, lease.Provider)` before constructing `VideoExportService`. The adapter reloads the independent target state and combines only exact or fully sampled face-specific alpha. The first oversized coordinator update was rejected by the GitHub connector and not committed; the smaller follow-up succeeded in [`2c73ed2`](https://github.com/doanythingK/FaceShield_/commit/2c73ed2ca6779a45af6b1da80a2be037785ddbb5).
- `ManualOverlayTargetExportMaskProvider` rejects malformed or mismatched saved state, unresolved failure boundaries, inconsistent dimensions and frames following a nonempty target source with missing verified samples. An explicitly erased zero-alpha correction ends that **same face** until its next nonempty correction. An exact same-target correction at a failure frame resolves that boundary, not another face's correction. `FramePreviewViewModel.ManualTargets.cs` persists fully erased masks as explicit absence keyframes and disables tracking on zero-alpha sources. Missing masks are never filled by holding prior pixels.
- `ManualOverlayTargetSelectionStore.cs` now saves the last selected face GUID in **optional, separate UI metadata**, keyed by the workspace path and guarded by video length/mtime evidence. `FramePreviewViewModel.ManualTargetLifecycle.cs` restores it only if that face still exists; missing, corrupt or stale metadata falls back to the workspace's existing first target without modifying its masks/tracks. `WorkspaceView.axaml.cs` invokes restoration on manual workspace attachment and persists actual selection after face creation/selection. This does not migrate a global/legacy face or prove selection survives a real GUI restart.
- The UI's tunneling pointer and keyboard handlers call the target-owned edit commit before ordinary workspace input/navigation, and view detachment also flushes a selected dirty target when still usable. This reduces the chance of a pending target bitmap being written through legacy `PersistCurrentMask` on those paths. **Direct programmatic frame changes, disposal ordering and failed target saves are still not fully covered**; do not mark the persistence lifecycle finished.
- Temporary `ToolPanelViewModel.ManualTargetExportGuard` and `FramePreviewViewModel.ManualTargetUi.CanExportLegacyMask()` remain in source but are unused: the view assigns the guard `null` after target-aware export was wired. This is cleanup debt, not evidence of export testing.

## Remaining work / known limitations

1. **Verification deferred:** No build, regression tests, Actions checks, GUI runs, decoded/encoded pixel comparisons, real-face benchmark or memory measurements have been performed on the 2026-09-21 changes. A push may automatically trigger pre-existing CI, but no result is claimed. Compilation and runtime correctness are unknown.
2. **Persistence on programmatic/abnormal navigation:** UI pointer/key paths and view detachment now flush selected target edits; direct `FramePreviewViewModel.OnFrameIndexChanged`/playback or disposal may still reach legacy `PersistCurrentMask` with a dirty target. Move ownership of persistence into the central view-model path, not only UI event hooks. Review save-failure recovery; do not discard dirty pixels after an exception.
3. **Selection and Undo lifecycle:** The last selected ID has an optional, source-evidence-checked metadata file, but writes can fail independently; a nonexistent target safely falls back to the first. Undo archives are in-memory, GUID/frame-keyed and capped at 128 MiB. Direct programmatic target selection and all frame-transition paths still need consistent stack ownership. A shorter retained retrack may report attempted-run progress instead of retained coverage. Review source switches and cancellation/disposal ownership.
4. **Legacy compatibility and entry points:** Keep legacy global masks/tracks separate. Migration requires unambiguous face identity; do not guess. Review other export entry points for paths that bypass `WorkspaceExportCoordinator`.
5. **Accuracy and performance:** Real moving faces, motion blur, occlusion, rotation, scene cuts and edge cases need future checks. New whole-frame target preview and per-frame alpha processing may be expensive. Windows/macOS GUI, packaged FFmpeg and actual encoded-output parity remain unverified.

## Change-log convention

For **each** implementation or fix, record why, affected source and behavior, verification performed or deferred, remaining gaps and confirmed commit link in this document on the same branch. Do not mark planned or untested behavior as verified.

### 2026-09-20 — preserve verified tracking on retry

- **Why:** A shorter/failing retry could erase a longer verified global segment.
- **What:** Retain longer same-source coverage; prefer an equally long successful over failed retry. [`554081b`](https://github.com/doanythingK/FaceShield_/commit/554081b7c95c279dee22b5d51f3ad4d447c8d615).
- **Verification:** Earlier [Quality Gate #299](https://github.com/doanythingK/FaceShield_/actions/runs/35489593929) passed Windows/macOS build and synthetic regressions.
- **Remaining:** Historical global path has no independent target ID and no real-face validation.

### 2026-09-21 — target-owned state, samples and resolver

- **Why:** Global manual boundaries allowed different faces to interrupt one another; target prototype saved only explicit keyframes.
- **What:** Added `ManualOverlayTargetWorkspace.cs`; extended `ManualOverlayStateStore.cs` to v2 with validated per-target segments. [`b62ed51`](https://github.com/doanythingK/FaceShield_/commit/b62ed51e63502c7eccf285165a5885f93f6ee90b), [`7dbd230`](https://github.com/doanythingK/FaceShield_/commit/7dbd23030f5ab99c289b5fca4ed544b31d2d6323).
- **Verification:** Deferred by user; no build or tests.
- **Remaining:** Target lifecycle/selection persistence and safe legacy migration.

### 2026-09-21 — target-specific tracking adapter

- **Why:** Independent target state did not yet invoke the application's real motion tracker.
- **What:** `ManualOverlayTargetTrackingService.cs` calls `ManualMaskTrackingService` using one target's exact source and correction boundary. [`c4ec57e`](https://github.com/doanythingK/FaceShield_/commit/c4ec57eb54e2c062b8509ccd08961bf6ecfddee4).
- **Verification:** Deferred; no synthetic/real-video run.
- **Remaining:** GUI accuracy and shorter-retry status reconciliation.

### 2026-09-21 — selectable editor, target Undo, tracking button and multi-face preview

- **Why:** Store/adapter did not support choosing A versus B, independent editing/Undo or running their tracks from the workspace.
- **What:** Added `FramePreviewViewModel.ManualTargets.cs` and `FramePreviewViewModel.ManualTargetUi.cs`; modified `FramePreviewViewModel.ManualKeyframes.cs` and `WorkspaceView.axaml.cs` to connect the selected target and preview. [`329a491`](https://github.com/doanythingK/FaceShield_/commit/329a4917463b6260a74fa07623416d0734e0ebec), [`aff847b`](https://github.com/doanythingK/FaceShield_/commit/aff847be3054cc415dad124b9d4de34bb0432f7f), [`bcff467`](https://github.com/doanythingK/FaceShield_/commit/bcff467b324478fb51e87c054dbaa83b753398f2), [`003c2d7`](https://github.com/doanythingK/FaceShield_/commit/003c2d7dc2316e40b9cb6f9fd9701a74a38fd093).
- **Verification:** Deferred; no build or GUI checks.
- **Remaining:** Abnormal-navigation edit persistence and performance.

### 2026-09-21 — target-aware final export and fail-closed gaps

- **Why:** A per-face editor without matching final export could omit faces; unsampled later frames could be silently unblurred.
- **What:** Added `ManualOverlayTargetExportMaskProvider.cs` ([`095e18e`](https://github.com/doanythingK/FaceShield_/commit/095e18eb4099410a5cc2bcde1155bd7e08e53332)), integrated in `WorkspaceExportCoordinator.cs` ([`2c73ed2`](https://github.com/doanythingK/FaceShield_/commit/2c73ed2ca6779a45af6b1da80a2be037785ddbb5)). Recognized correction at failure boundary ([`2325381`](https://github.com/doanythingK/FaceShield_/commit/2325381b04ffbd931c0644c3dacf2154d191c5e1)); missing target samples now throw ([`9853678`](https://github.com/doanythingK/FaceShield_/commit/9853678a99d9fa1dcc3a98c2346faa025f348c14)). Erased masks persist as explicit absence boundaries and percentage uses source boundary ([`8ffcbcd`](https://github.com/doanythingK/FaceShield_/commit/8ffcbcd98c1e6d963ed85b378fc31d43e70e6e4d)). Temporary legacy save guard added ([`5a8c788`](https://github.com/doanythingK/FaceShield_/commit/5a8c788b40bd06aca7443f586898d8d57950db50)), later disabled after export integration ([`3402bfb`](https://github.com/doanythingK/FaceShield_/commit/3402bfb73eaca75a73351f4f7aa2516dff823c99)). UI readiness now bound live ([`5bb0b8b`](https://github.com/doanythingK/FaceShield_/commit/5bb0b8b3012c3a495874e74e51958e8b25692e04)).
- **Verification:** Deferred; no build, regression, CI result or encoded video claimed. Initial oversized export-coordinator update was rejected; smaller later write succeeded.
- **Remaining:** Actual GUI/export correctness, unexpected dirty edits, selection persistence and alternate export entry points.

### 2026-09-21 — preserve per-face Undo across target selection and refresh loaded preview

- **Why:** The shared `_maskUndo` stack was cleared on face selection, discarding A's undo history and risking wrong-face restoration if reused. Initial target replacement could refresh while its composition guard was active, leaving the first preview without all saved targets.
- **What:** Added bounded in-memory, GUID-and-frame-keyed undo archives in `ViewModels/Workspace/FramePreviewViewModel.ManualTargetUndo.cs` ([`426b3c3`](https://github.com/doanythingK/FaceShield_/commit/426b3c346af890bf47839374b9ee5de4ec2e5223)); `Views/Pages/WorkspaceView.axaml.cs` preserves old stack and restores the selected face's stack around UI face selection/creation, then explicitly refreshes composite preview after opening a manual workspace ([`7f1ce34`](https://github.com/doanythingK/FaceShield_/commit/7f1ce340cba96ba6a2a17b5ad346cda89b79a17e)). Archives are session-only and have a combined 128 MiB budget.
- **Verification:** Deferred by user; no compile/test/GUI/export result claimed.
- **Remaining:** Direct/programmatic selection and frame navigation must share the same undo ownership; unexpected dirty-editor persistence and selection restoration remain incomplete.

### 2026-09-21 — selected-face UI metadata and pre-navigation edit flush

- **Why:** Reopening always selected the first face, and a dirty selected-target bitmap could reach the global `PersistCurrentMask` during certain UI navigation actions. A saved per-target Undo archive also needed a restore trigger after the new frame's bitmap had been installed.
- **What:** Added source-evidence-checked optional `Services/Video/ManualOverlayTargetSelectionStore.cs` ([`d5751c0`](https://github.com/doanythingK/FaceShield_/commit/d5751c01db73997e3e6a9f5596c205da0d82d784)); added `ViewModels/Workspace/FramePreviewViewModel.ManualTargetLifecycle.cs` for first-load ID restoration and target-only pending-edit commit ([`b6e2b5f`](https://github.com/doanythingK/FaceShield_/commit/b6e2b5f9ceb4d245e2114a49097570728030b595), [`05fac19`](https://github.com/doanythingK/FaceShield_/commit/05fac1966a19f72d31b594320ea7a3fa4a954580)). `Views/Pages/WorkspaceView.axaml.cs` now saves actual selected ID on create/select, restores a still-present ID on open, commits pending target alpha in tunneling pointer/key handling and on view detachment, and restores an archived Undo stack only when the selected target and loaded frame match ([`5168bb5`](https://github.com/doanythingK/FaceShield_/commit/5168bb5f943e793ad6c59b2b301072aa7b5579f1)). Selection metadata is not authoritative mask state; missing/stale/corrupt selection does not rewrite target ownership.
- **Verification:** **Deferred by user request.** No build, automated/manual check, Actions result, actual GUI interaction or output validation performed. The initial UI update with an old blob SHA was rejected (HTTP 409) without changing the file; the retry used the current SHA and succeeded.
- **Remaining:** Central `FramePreviewViewModel.PersistCurrentMask` and all programmatic frame/disposal routes still need target-aware ownership. Selection metadata writes can fail; direct non-UI target selection and save-failure semantics need further treatment. Real GUI Undo/selection/preview/export parity is unverified.
