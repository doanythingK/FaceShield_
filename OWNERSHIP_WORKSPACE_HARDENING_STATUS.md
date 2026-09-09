# Ownership / Workspace Hardening Status

## Current direction

Branch: `refactor/ownership-workspace-hardening`

The active hardening priority is user-visible correctness, resource/lifetime safety, persistence ordering, and responsibility/policy boundaries. Filesystem path edge cases should not consume additional implementation time unless a reproduced failure makes them relevant again.

## PATH identity hardening

Status: **DEFERRED / 보류**

### Core work completed

- [x] Separate filesystem access path from workspace identity key.
- [x] Use filesystem case policy for the current v4 identity.
- [x] Keep case-only rename and stale-save/removal identity consistent under normal case-insensitive filesystem conditions.
- [x] Preserve v3/v2/pre-identity/legacy workspace lookup compatibility.
- [x] Move filesystem identity calculation outside `GlobalStateGate`.
- [x] Reuse operation-scoped path identity in workspace save/remove and Home recent/cache matching.

### Deferred residual work

- [ ] **DEFERRED:** avoid synchronous filesystem/native case-policy queries on latency-sensitive caller/UI paths. Slow network or external storage can still delay `CreatePathContext()`.
- [ ] **DEFERRED:** add a recovery policy for v4 state when filesystem case policy changes after save or native case-policy lookup falls back/fails. The current v4 match is intentionally strict and can fail before payload lookup in this condition.
- [ ] **DEFERRED:** decide whether symlink/junction aliases should resolve to one physical workspace identity. Current policy keeps alias paths distinct.
- [ ] **DEFERRED:** define/test behavior for exotic Unicode case-folding and normalization semantics across supported filesystems.

Resume this block only when higher-priority hardening work is complete or a real reproduced workspace-path failure requires it.

## Existing architectural deferrals

These are documented limitations rather than the next active implementation block.

- [ ] **DEFERRED:** cancel an already-running synchronous FaceONNX `FaceDetector.Forward()` call. The current detector interface does not expose a safe mid-inference cancellation contract; pre/post-call cooperative cancellation remains in place.
- [ ] **DEFERRED:** replace the global 1,000,000 decoded-PTS resident-frame limit with paging/persistent indexing. This requires a different cache architecture rather than another cap adjustment.

## Completed responsibility / policy-boundary blocks

### Export quality-gate diagnostics extraction

- [x] Move export quality/risk calculation and logging out of `WorkspaceViewModel` into `RunMetricsLog`.
- [x] Remove the quality-log callback dependency from `WorkspaceExportCoordinator`; the coordinator now calls the diagnostics service directly.
- [x] Keep export behavior and log payloads unchanged while reducing page ViewModel responsibility.

### Dead hybrid export policy cleanup

- [x] Remove unused hybrid-policy serialization/parsing helpers from `WorkspaceViewModel`.
- [x] Remove the unused `EvaluateAutoExportHybridPolicy` implementation instead of preserving a policy that is no longer reachable.
- [x] Keep the active export contract unchanged: hybrid copy remains disabled by `WorkspaceExportCoordinator.HybridCopyDisabledReason` until bitstream compatibility is verified.

### Workspace operation lifetime extraction

- [x] Move operation admission/drain/dispose-claim synchronization out of `WorkspaceViewModel` into `WorkspaceOperationLifetime`.
- [x] Preserve the existing disposal ordering: close admission first, request cancellation second, and dispose shared resources only after active operations drain.
- [x] Keep coordinator lifetime callbacks on the same boolean begin / void end contract.

### Terminal shutdown persistence boundary

- [x] Close workspace operation admission and request cancellation before terminal `SaveNow` persistence starts.
- [x] Cancel Home-owned auto/load/model-download tokens before the workspace terminal-save loop.
- [x] Move queued persistence lifetime admission ahead of preview/snapshot capture so a closed/disposed workspace does not touch owned resources before being rejected.
- [x] Preserve the final ordering as: close admission/cancel -> terminal save -> dispose/drain.

### Late UI callback / dialog lifetime hardening

- [x] Audit workspace-owned dispatcher posts and fire-and-forget callbacks for owner-lifetime checks.
- [x] Register error dialogs with `WorkspaceOperationLifetime`, suppressing late playback/auto error UI once shutdown admission is closed and keeping resources alive while an accepted dialog is active.
- [x] Reject queued playback starts after `FramePreviewViewModel` disposal.
- [x] Dispose exact/playback bitmaps instead of applying them if a queued bitmap callback reaches the UI after preview disposal.

### Owned event subscription cleanup

- [x] Replace anonymous `ToolPanel` workspace command subscriptions with named handlers so they can be detached deterministically.
- [x] Detach `ToolPanel`, `FramePreview`, and issue-review event handlers when shutdown admission closes and again idempotently at resource disposal.
- [x] Replace the anonymous `ToolPanel.PropertyChanged` subscription in `FramePreviewViewModel` with a named handler and unsubscribe on preview disposal.
- [x] Guard the property-change handler against disposed preview state.

### Home workspace cache / navigation ownership

- [x] Serialize Home workspace-cache lookup, adoption, eviction, shutdown snapshot, persistence snapshot, and drain operations behind one ownership gate.
- [x] Close cache admission before shutdown cancellation and reject/dispose a newly constructed candidate if shutdown or cancellation wins before adoption.
- [x] Capture the requested video path before background construction so a later Home selection change cannot redirect an in-flight workspace creation.
- [x] Prevent recent-list trimming from disposing the workspace that is still the current page; defer that eviction and persistent-state removal until navigation returns Home.
- [x] Recheck cancellation/shutdown before manual/auto navigation and after deferred session initialization.

### Home asynchronous UI lifetime

- [x] Register Home-owned load/auto/download cancellation sources under the same shutdown gate so no new operation can escape cancellation after shutdown admission closes.
- [x] Gate queued workspace-load, auto-run, export, and model-download progress callbacks by the exact operation generation before mutating Home UI state.
- [x] Prevent stale operation finalizers from clearing busy/download state owned by a newer operation.
- [x] Suppress file-picker, resume/error/blur-dialog, clipboard, startup-continuation, and workspace-navigation mutations after shutdown begins.
- [x] Stop the Home auto-status timer during shutdown and detach the current-page reference from a workspace before terminal persistence/disposal.

## Next active block

Audit Home/application-root resource disposal (generated blur-preview bitmaps, timer/event lifetime, and app-exit idempotency) and fix only resources that remain rooted or can be disposed twice across shutdown paths.

