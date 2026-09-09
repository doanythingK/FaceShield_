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

### Cached workspace UI-thread ownership

- [x] Keep cache lookup/adoption under the cache gate free of UI-observable workspace mutations.
- [x] Apply cached/new workspace runtime options only after background construction returns and marshal the update explicitly to the Avalonia UI thread.
- [x] Guard the option application itself against shutdown and fail fast if a future caller attempts it off the UI thread.
- [x] Prevent `ToolPanel.BlurRadius` from triggering preview bitmap reset/recomposition on a `Task.Run` worker thread.

### Auto analysis staged post-processing transaction

- [x] Verify sequential, single-pipeline, parallel-pipeline, and sparse-pipeline strategies all converge on `FinalizeRunAfterDecode` before final risk-cascade/post-processing.
- [x] Keep completed detection/resume results on the live `FrameMaskProvider` while risk cascade and post-processing operate on a detached provider snapshot.
- [x] Keep version-checked `CommitFaceMasksFrom` as the single live-provider commit after the staged post-processing phase succeeds.
- [x] Treat a `YoloRiskCascadeStep` soft failure (`Enabled=true` with a non-empty `Error`) as a failed staged transaction: record the cascade diagnostics, skip downstream post-processing, and discard the working provider without committing it.
- [x] Preserve cancellation/exception behavior: staged mutations remain isolated and the live provider is unchanged unless the final commit is reached.

## Deferred low-priority follow-up

- [ ] **DEFERRED:** Home/application-root cleanup for the final blur-example bitmap set, timer detachment, and exit idempotency. These are shutdown/resource hygiene items and are not currently tied to incorrect analysis/export results, data loss, or a reproduced crash.

### Export correctness audit

- [x] Confirm export works from a detached mask-provider snapshot so editing cannot change masks mid-export.
- [x] Confirm analysis/preview/export use sequential decoded-frame ordinals for frame-mask lookup, while presentation timestamps remain a separate encoding-timing concern.
- [x] Keep expected blur-frame coverage fail-closed: any mask frame not actually blurred aborts the staged output before final commit.
- [x] Keep cancellation/failure output isolated in a same-directory staging file; expose the final path only after successful staging commit.
- [x] Keep final packet/frame/timestamp integrity fail-closed through `VideoExportIntegrityPolicy`.
- [x] Restore the RGB H.264 fidelity contract: compatible RGB H.264 sources use `libx264rgb` with required `crf=0` lossless encoding rather than lossy CRF 18.
- [x] Preserve required encoder-option failure handling so an unavailable `crf=0`/preset contract rejects that encoder path rather than silently degrading quality.

### Decoder seek cancellation classification

- [x] Keep ordinal-index cancellation separate from `_ordinalDecoderFailed`; cancellation does not permanently degrade later timestamp/ordinal resolution.
- [x] Treat cancellation that wins during the initial exact-frame seek as cancellation rather than converting the interrupted seek into an `InvalidOperationException`.
- [x] Treat cancellation that wins during exact-timestamp fallback-to-beginning as cancellation rather than recording a sequential decode error.
- [x] Preserve genuine seek failures as decode errors; only cancellation-requested failures take the cancellation path.

### VFR / missing-PTS frame selection

- [x] Preserve valid source frame rates below 1 fps instead of clamping the extractor fallback rate to 1 fps; missing-PTS fallback timestamps therefore retain the source cadence.
- [x] Reject decoded frames with `AV_NOPTS_VALUE` while resolving a timestamp thumbnail instead of treating an uncomparable post-seek frame as a match for the requested time.
- [x] Keep duplicate/non-monotonic PTS fail-closed: exact timestamp seek remains disabled and ordinal selection falls back to decode-from-beginning.
- [x] Keep exact-frame callers ordinal-based; the missing-PTS change applies only to timestamp-targeted thumbnail selection.

### Decoded timestamp origin normalization

- [x] Keep `LastDecodedTimestampSeconds` on the same video-relative origin as decoded timeline lookups instead of exposing raw absolute stream PTS seconds.
- [x] Prevent playback from mixing absolute PTS time with zero-origin FPS fallback when a decoded frame is missing PTS.
- [x] Keep analysis `FrameTimingSample` values on the same relative timeline used by playback/timeline navigation.
- [x] If the first decoded frame has no trustworthy PTS origin, remain on deterministic ordinal/FPS fallback rather than switching origins mid-stream.

### Manual edit frame ownership guard

- [x] Persist the current dirty mask before a normal frame-selection transition, then invalidate the editable frame until the requested exact frame and matching mask are applied.
- [x] Do not let the previous frame's `MaskBitmap` remain editable after `_currentFrameIndex` has moved to a different requested ordinal.
- [x] Block brush/eraser/undo mutation while sequential playback is active so playback frame replacement cannot silently discard a manual stroke.
- [x] Invalidate editable mask state immediately when playback stops; editing resumes only after the exact stopped frame is reloaded.

## Next active block

Continue manual-mask persistence correctness through persistence serialization/restore and export snapshot handoff. Focus on code-confirmed cases that can lose a stored manual override or apply it to the wrong frame; keep UI-only polish and low-impact resource cleanup deferred.

