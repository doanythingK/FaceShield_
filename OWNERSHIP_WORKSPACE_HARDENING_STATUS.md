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

## Next active block

Continue the ownership audit around event subscriptions and delegate captures, especially subscriptions whose publisher can outlive the subscriber or retain workspace view models after navigation/cache eviction.

