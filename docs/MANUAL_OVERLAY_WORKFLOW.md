# Manual overlay and tracking — implementation status

Branch: `refactor/manual-overlay-workflow`. Current review updated 2026-11-02. Windows/macOS Release builds and the configured regression suites passed [Quality Gate #41370435186](https://github.com/doanythingK/FaceShield_/actions/runs/41370435186) for code commit `5990139` (2026-11-02). The earlier Undo gate [#41030155508](https://github.com/doanythingK/FaceShield_/actions/runs/41030155508) also passed. Actual GUI close/retry behavior, native resource lifetime under all races, real footage/output quality and performance remain unverified. Earlier sections retain historical notes. This branch remains separate from `main`.

## Required user workflow

The user must be able to find a face missed by Auto, draw a mask for that particular face, track its motion, correct or explicitly end only that face, and export the union of Auto, legacy/global manual and independently verified face-specific coverage. Each face has a stable GUID and independent explicit-alpha keyframes, motion samples and failure boundaries. Correcting A must not erase B. Missing verified motion must fail closed in export, never hold a previous frame's pixels. An all-zero keyframe marks absence **only for its selected face**, not a zero-size blur request. See [manual face acceptance scenarios](MANUAL_FACE_WORKFLOW_ACCEPTANCE.md).

## Earlier implementation and persistence (history)

- Target-owned GUID state, v1/v2 persistence and validated motion: [state](https://github.com/doanythingK/FaceShield_/commit/b62ed51e63502c7eccf285165a5885f93f6ee90b), [v2 tracking](https://github.com/doanythingK/FaceShield_/commit/7dbd23030f5ab99c289b5fca4ed544b31d2d6323), [service](https://github.com/doanythingK/FaceShield_/commit/c4ec57eb54e2c062b8509ccd08961bf6ecfddee4).
- Per-face toolbar, isolated editor and multi-layer preview: [UI](https://github.com/doanythingK/FaceShield_/commit/aff847b), [editor](https://github.com/doanythingK/FaceShield_/commit/bcff467), [preview](https://github.com/doanythingK/FaceShield_/commit/003c2d7).
- Strict export adapter, tracked failure/correction boundary and all-zero absence: [adapter](https://github.com/doanythingK/FaceShield_/commit/095e18eb4099410a5cc2bcde1155bd7e08e53332), [failure](https://github.com/doanythingK/FaceShield_/commit/2325381b04ffbd931c0644c3dacf2154d191c5e1), [absence](https://github.com/doanythingK/FaceShield_/commit/8ffcbcd98c1e6d963ed85b378fc31d43e70e6e4d).
- Face-specific Undo, transition/save guards and persistence routing: [Undo](https://github.com/doanythingK/FaceShield_/commit/426b3c3), [navigation](https://github.com/doanythingK/FaceShield_/commit/88521b3), [export guard](https://github.com/doanythingK/FaceShield_/commit/61d4a32), [manual-only editor persistence](https://github.com/doanythingK/FaceShield_/commit/47cb3a9ea5b1a367566aa3b0a346e42c480357e8), [save/back handling](https://github.com/doanythingK/FaceShield_/commit/0648b10), [window close](https://github.com/doanythingK/FaceShield_/commit/fc20a8e).
- Disk-first compare-and-commit with sidecar locking, idempotent updates, seed-only legacy writer and persisted-snapshot baseline: [conflict protection](https://github.com/doanythingK/FaceShield_/commit/9d5d88a), [source correction](https://github.com/doanythingK/FaceShield_/commit/df8065f), [target tracking](https://github.com/doanythingK/FaceShield_/commit/b843ea6), [target creation](https://github.com/doanythingK/FaceShield_/commit/8a90b7c), [direct Save baseline](https://github.com/doanythingK/FaceShield_/commit/575e2b2), [idempotence](https://github.com/doanythingK/FaceShield_/commit/3a2702a), [seed-only API](https://github.com/doanythingK/FaceShield_/commit/c1513e9). A writer bypassing this protocol can still race; conflicts require reload or manual reconciliation.
- Selected-face absence toolbar and persistence: [absence command](https://github.com/doanythingK/FaceShield_/commit/72436af), [UI](https://github.com/doanythingK/FaceShield_/commit/b95d2ad). Regression sources under `scripts/manual-overlay-*-regression.cs.txt` remain unexecuted.

## Tracking, EOF, composition and gate fixes — 2026-09-22

- Tracking does **not** use an estimated container frame count as a hard boundary. Only the next explicit correction of the **same face** is a finite endpoint; otherwise it decodes to verified EOF. [Tracking](https://github.com/doanythingK/FaceShield_/commit/d723f6b), [preview](https://github.com/doanythingK/FaceShield_/commit/2fca882).
- An unbounded decoder-EOF result, distinct from a correction boundary, alone updates `FrameList.UpdateActualTotalFrames`. Both target-specific and original legacy manual-tracking paths now use the callback. Failure, cancellation, next-keyframe boundary and one-frame stepping do not establish a total. [Target callback](https://github.com/doanythingK/FaceShield_/commit/85531c5), [workspace owner](https://github.com/doanythingK/FaceShield_/commit/1e9b144), [legacy path](https://github.com/doanythingK/FaceShield_/commit/a1528c287fa475cef1a8fe1e0a29fafb86e85c21).
- Target keyframes use binary floor lookup; contiguous tracked samples use validated ordinal indexing. Strict export resolves and unions each target once; nonempty unverified gaps throw. [Indexed resolver](https://github.com/doanythingK/FaceShield_/commit/5eed014), [export adapter](https://github.com/doanythingK/FaceShield_/commit/99fd3ab), [cached absence](https://github.com/doanythingK/FaceShield_/commit/815310b).
- The Auto coordinator and manual tracking share an atomic exclusive-processing lease separate from ordinary operation lifetime. Auto-owned chained export does not reenter that gate. Public Auto entry points check session/editability flags. [Gate](https://github.com/doanythingK/FaceShield_/commit/7adf968), [manual](https://github.com/doanythingK/FaceShield_/commit/f96d717), [Auto](https://github.com/doanythingK/FaceShield_/commit/aef6fae).
- An all-zero explicit target is skipped even at its own correction frame. With no Auto/legacy or other face mask the export adapter receives `false` and returns `null`, not an all-zero bitmap that would fail blur bounds. Other faces/Auto remain available. [Fix](https://github.com/doanythingK/FaceShield_/commit/4b60dcfc031f9c56ab2dc0fc7ff5a36fc1e840d6).
- Removed two obsolete one-shot Python source-patching scripts; default-branch search and examined verification scripts had no references, but this is not an exhaustive branch-wide reference audit. [First](https://github.com/doanythingK/FaceShield_/commit/6b25c12), [second](https://github.com/doanythingK/FaceShield_/commit/9f6be73).

## Remaining code changes — 2026-09-23

### Direct preview/session disposal

**Problem:** `FramePreviewViewModel.Dispose()` canceled frame loads and playback but could dispose `VideoSession` while tracking/load/playback tasks still referenced native decoders; the legacy keyframe PropertyChanged handler only covered some configurations.

**Change:** The session coordinator installs a separate direct-dispose handler before initialization for both Auto and Manual. `Dispose()`'s unconditional `PreviewBitmap` notification captures outstanding tracking, manual-frame-load and playback Tasks, cancels tracking, and registers `Task.WhenAll` with the session **before** `_session` is cleared. The session independently closes timeline requests and asynchronously drains ExactFrameProvider, ThumbnailProvider and ManualFramePlayer before freeing native resources. It does not synchronously wait on the UI thread. [Preview drain](https://github.com/doanythingK/FaceShield_/commit/c04e54935267304da7cc8d29742c018c5ccb4ca9), [early installation](https://github.com/doanythingK/FaceShield_/commit/f6bee1f588685f55843b716ce7695365e6f1d93a), [session drain](https://github.com/doanythingK/FaceShield_/commit/850cb4e1e76f6d3a653853afac5f9a16369f8174). This is a source-level fix, not proof that all timing interleavings are safe. Forced process termination and failed final persistence cannot guarantee unsaved memory-only pixels.

### Export cancellation, allocation and exclusion

**Problem:** `IFrameMaskProvider.GetFinalMask(int)` has no CancellationToken; the target adapter did not pass the export token to `TryComposeFrame`. Legacy BGRA, tracked full-frame alpha and combined BGRA buffers were newly allocated for every export frame; standalone export did not hold Auto/manual's processing gate.

**Change:** The workspace export coordinator passes its `exportToken` to the face-specific wrapper. Its scoped token is checked during mask reads, each target/scanline, geometric transform and final bitmap copy; the broad `IFrameMaskProvider` signature is unchanged. Legacy BGRA and composition BGRA are rented from `ArrayPool<byte>` per invocation and returned in `finally` **only after bitmap copy**, preventing reuse while a caller owns pixels. Manual-only composition clears the rented destination to prevent previous-frame alpha leakage; Auto input overwrites every pixel. Explicit corrections union directly from alpha, and verified tracked samples rasterize directly into the destination BGRA buffer without constructing a separate full-frame tracked alpha array. Missing samples continue to throw on strict export. A new `WriteableBitmap` is still returned for each nonempty mask to honor caller-ownership semantics. [Token/legacy scratch](https://github.com/doanythingK/FaceShield_/commit/bbfd3203cdcf647b838dd0b034a5ed7d1d741737), [export call](https://github.com/doanythingK/FaceShield_/commit/b2fdc4359d647228a343bfc3fd051fd8806e1e71), [direct rasterization](https://github.com/doanythingK/FaceShield_/commit/58771e72c32a3d83441fd640dd05b65bcb572361), [composition scratch](https://github.com/doanythingK/FaceShield_/commit/ebf6f5e89548096cf8840cdf9fbbb0a534cf77d0), [nullable guards](https://github.com/doanythingK/FaceShield_/commit/98468c963affa54d5489ed7c2b2658799ed878ec).

**Export exclusion:** A standalone `WorkspaceExportCoordinator.ExportAsync` obtains the same workspace exclusive gate as Auto/manual; Auto-owned chained export instead obtains an ordinary lifetime lease, avoiding recursive gate acquisition. Existing injected lifetime delegate consumers keep their injected behavior. [Standalone gate](https://github.com/doanythingK/FaceShield_/commit/2c672b003fec8517e1e72135755b8d5e4ee50b3e). This protects the workspace coordinator, not arbitrary external callers that directly instantiate `VideoExportService` without the target wrapper.

### 2026-09-23 regression source (historical status)

[`scripts/manual-overlay-export-index-regression.cs.txt`](../scripts/manual-overlay-export-index-regression.cs.txt) now includes explicit/track/Auto union, missing-sample fail-closed, absence-only export, caller-owned pooled destination, stale-alpha clearing and canceled composition cases. At the time of writing (2026-09-23), this was **source only**, not executed. Later CI ran the tests included by its verification scripts; this statement is not the current branch-wide validation status. [Added scenarios](https://github.com/doanythingK/FaceShield_/commit/481ed9d27c81da93616f1e45338ed5fc33f1afc4).

## 2026-10-30: face-transaction Undo and shutdown saving

- A face correction/absence used to truncate validated tracking segments while
  its Undo stored only BGRA pixels. Restoring the pixels through a new correction
  could not recover the verified samples. Undo entries now carry the selected
  face's full pre-edit and committed post-edit state. The target Undo atomically
  compare-and-commits the previous keyframes and track segments before restoring
  editor pixels. Concurrent edits to the same face are rejected rather than
  overwritten; failures retain the undo entry and edited pixels. Existing
  legacy/global Undo remains pixel-based.
- Window Closing preflights saves of *all* cached workspaces while cancellation
  remains possible. Exit skips the redundant save after successful preflight;
  programmatic shutdown saves before disposal and does not unconditionally
  dispose following a failed save. Permanent disk failure and forced process/OS
  termination cannot guarantee recovery of unsaved memory-only edits.
- **Automated validation (2026-10-30):** [Hardening Quality Gate #41030155508](https://github.com/doanythingK/FaceShield_/actions/runs/41030155508) passed Windows and macOS Release builds, Windows runtime invariants, the manual overlay regression suite (including the new transactional Undo fixture), manual pyramid checks, and FFmpeg-backed continuous tracking/Auto-overlap integration. The isolated test project now compiles the full target workspace and committer. Existing source tests verify tracked-frame absence Undo, unchanged other-face state, stale Undo rejection, and a simulated locked-state-file write failure/retry. These results do **not** establish GUI Undo behavior, real multi-face footage quality, or actual close-cancellation UX. Those remain deferred, as do forced-termination and disk-exhaustion recovery tests.

## 2026-11-02: retryable persistence, quiescent shutdown and safe cache eviction

- The previous terminal `WorkspacePersistenceCoordinator.SaveNow` made `_finalizing` permanent even after failed I/O/capture, so canceled window close could not retry or queue saves. Terminal save is now a temporary ordered barrier: clear finalizing in a `finally` and replace a failed task tail with a completed tail **after** its failure has been observed. A later snapshot retries rather than rethrowing the prior failed request.
- The window-close preflight is now asynchronous and cancellable: the Home shutdown coordinator prevents new starts, cancels Home/Auto operations and waits for each workspace lifetime to drain **before** capturing the final workspace/face state. A failed save reopens admission and leaves the live workspace available; successful close proceeds without a second late write in Exit.
- Recents/cache pruning preflights an **idle** workspace's save before removing it from cache. A running operation, storage failure or already closing admission retains its in-memory state and defers eviction. No resource disposal is initiated on this failed preflight. This covers the previously reported user-facing cache eviction path, not arbitrary external calls to `Dispose()`.
- Window-close preflight also marks workspace navigation in progress so the user cannot make fresh brush edits while workers drain; failure restores the prior UI edit/navigation state. A successful asynchronous close posts a fresh Close event to the dispatcher rather than entering it recursively. Home Auto-running UI state is reset if shutdown preparation is later aborted.
- Recents trimming now treats the recent-file list as navigation history rather than permission to delete durable workspace/resume state. Deferred cache eviction likewise retains the saved workspace data.
- **Automated validation (2026-11-02):** [Hardening Quality Gate #41370435186](https://github.com/doanythingK/FaceShield_/actions/runs/41370435186), code `5990139`, **passed** Windows runtime invariants and Release build, macOS Release build, manual overlay/Undo regression, isolated SaveNow failure/retry and lifecycle regression, manual pyramid checks, and continuous tracking plus Auto/manual overlap integration. The isolated persistence fixture verifies I/O failure, capture failure, retry, queued save after preflight, and idle-only eviction; wiring checks cover production call sites. **Not verified:** real GUI shutdown interaction, actual disk-full/OS termination, unusual native-disposal races, or video-output fidelity. Direct programmatic `Dispose()` without the window/cache preflight remains outside these guarantees.

## 2026-11-02: manual-tracking cancellation admission race

- Both legacy/global manual tracking and per-face manual tracking previously
  acquired the workspace exclusive-operation lifetime before publishing
  `_manualTrackingCts`. An independent shutdown thread could close admission
  and dispatch `AdmissionClosed` in that interval, find a null source, and
  then wait for uncanceled tracking to finish.
- Both entrypoints now publish their cancellation source and acquire workspace
  lifetime under the existing `_manualTrackingCommitGate` through the shared
  `ManualTrackingCancellationGate.TryBegin` helper. The shutdown cancellation
  subscriber uses the same gate, and the workspace lifetime implementation
  releases its own lock before dispatching that subscriber. Therefore a
  successful admission cannot be invisible to a concurrent close; if close
  wins first, admission fails and the source is removed without starting work.
- Source cleanup also runs under the shared gate before releasing lifetime,
  preventing concurrent cancel-after-dispose. Early returns and failed
  admission release the source; the established commit-in-progress protection
  still prevents cancellation of a partially published track.
- **Automated verification (2026-11-02):** [Hardening Quality Gate #41378696470](https://github.com/doanythingK/FaceShield_/actions/runs/41378696470) passed Windows/macOS builds, Windows runtime hardening checks, manual overlay and Undo regression, the isolated production-helper concurrency fixture, pyramid checks, and continuous tracking/Auto overlap integration for code commit `641e9a7`. The fixture forces a close to contend with a successful lifetime admission, then tests cancellation, lifetime drain, already-closed admission and callback exceptions; source wiring checks verify both tracking commands use the shared helper. This is **not** an actual GUI close/native video timing test.

## Still unverified or outside this change's scope

1. **Still needed:** GUI/native interleaving tests for preview/session disposal, cancellation during native decode, pooled-buffer lifetime, Auto→export and standalone export. Windows/macOS Release builds and available regression suites **were run and passed** in [#41030155508](https://github.com/doanythingK/FaceShield_/actions/runs/41030155508), but do not cover all these runtime interleavings.
2. End-to-end manual missed-face workflow, moving multi-face GUI, saved-state recovery, preview-versus-encoded output, Windows/macOS packaging, actual VFR/large-file behavior and performance/GC measurements are unverified. No measured speedup or native-disposal safety claim is made.
3. External/direct `VideoExportService` consumers do not automatically enforce workspace-only face-state persistence or adapter wrapping. The workspace's production coordinator uses the wrapper; universal third-party/direct export protection requires an explicit API contract and exhaustive call-site audit. The general `IFrameMaskProvider` interface remains tokenless; cancellation is scoped to the workspace-created target export wrapper.
4. Forced process/OS termination, permanent disk write failure, uncooperative writers bypassing the sidecar lock and direct programmatic timeline setter/save rejection can still require manual recovery. Source evidence is file length/modification time rather than a video content hash; face-friendly names/thumbnails are not persisted.

Subsequent changes must remain on this branch, include reason/commit and disclose actual validation or explicit deferral. Do not equate source intent with tested behavior.
