# Quality Stabilization Review Fixes

## Status

- Branch: `fix/quality-stabilization-debug`
- Review baseline: `37a2717a924a78c3849512fa5905a49bb34239c1`
- Purpose: document the issues found after the first quality/stabilization patch and the corrective changes applied afterward.
- Validation level: source review + invariant/test updates. A real Windows/macOS build or runtime video replay is **not** claimed in this document.

---

## 1. RGB H.264 contract mismatch

### Problem

The encoder policy had been changed from `libx264rgb CRF 0` to `CRF 18`, but the surrounding code still described the path as lossless.

Examples of the stale contract included names such as:

- `requiresLosslessRgbH264`
- `isLosslessX264Rgb`
- `_losslessX264RgbConfigured`
- `CanEncodeLosslessX264Rgb`
- `verify-rgb-lossless-export.ps1`

The E2E test had also moved from byte-exact output verification to a bounded error budget, so the implementation and the naming no longer agreed.

### Fix

The intended product behavior remains high-quality lossy RGB H.264 at `CRF 18`; it was **not** reverted to CRF 0.

The contract was renamed to describe compatibility rather than losslessness:

- `CanEncodeLosslessX264Rgb` -> `CanEncodeCompatibleX264Rgb`
- `requiresLosslessRgbH264` -> `requiresRgbH264Path`
- `isLosslessX264Rgb` -> `isX264Rgb`
- `_losslessX264RgbConfigured` -> `_x264RgbConfigured`
- frame validation state renamed to `x264RgbConfigured`
- RGB validation method renamed to `ValidateDecodedFrameFormatCompatibility`

Error messages that implied byte-exact preservation were rewritten as supported/unsupported RGB H.264 quality-path compatibility messages.

The verifier was renamed:

- deleted: `scripts/verify-rgb-lossless-export.ps1`
- added: `scripts/verify-rgb-quality-export.ps1`

The aggregate auto-mosaic verification script now uses the new quality verifier name.

### Result

The code no longer claims that CRF 18 output is lossless. Pixel format, bit depth, range, matrix, and metadata compatibility checks are still retained.

---

## 2. Tracked box scene-cut boundary was one frame late

### Problem

Scene-cut detection records the first frame of the new scene:

```csharp
_sceneCutStarts.Add(idx);
```

The new tracked stabilizer incorrectly checked:

```csharp
blockedSceneCutStarts?.Contains(frameIndex - 1)
```

That allowed the first frame of a new scene to be blended with a box from the previous scene and blocked stabilization one frame later instead.

The regression test had encoded the same incorrect expectation.

### Fix

The stabilizer now checks the frame currently being processed:

```csharp
blockedSceneCutStarts?.Contains(frameIndex)
```

The regression test was changed so a cut beginning at frame 3 blocks stabilization of frame 3, while frames 1 and 2 are still allowed to stabilize.

### Result

Tracked box stabilization now follows the same scene-cut start semantics already used by `FaceTrackInterpolator.CrossesSceneCut()`.

---

## 3. Tracked stabilizer allocated work proportional to total video length

### Problem

The first implementation allocated four `totalFrames`-sized arrays:

```text
List<Rect>?[totalFrames]
List<float>?[totalFrames]
PixelSize[totalFrames]
bool[totalFrames]
```

It also copied face/confidence lists for every detected frame and did not accept a cancellation token.

For long videos this added avoidable memory pressure and made Auto cancellation wait for the entire stabilization pass.

### Fix

`ApplyTrackedBoxStabilization` was rewritten as a sparse one-pass transform.

The new flow is:

```text
GetFaceMaskFrameIndices()
  -> sort only actual face-mask frame indices
  -> keep only previous stabilized face list
  -> process the next contiguous face frame
  -> rewrite current frame only if a box changed
```

Changes:

- removed all `totalFrames`-sized stabilization arrays
- no full duplicate face/confidence timeline is created
- only actual face-mask indices are sorted
- only a changed current frame allocates a replacement rect array
- current stabilized output becomes the previous state for the next frame
- `CancellationToken` is now accepted
- cancellation is checked once per frame and inside the face matching loop
- `AutoMaskPostProcessPipeline` passes its existing cancellation token into the stabilizer

### Result

Memory now scales primarily with actual face-mask entries rather than total video frame count. Cancellation can stop the new stabilization phase without waiting for the full video-length scan.

---

## 4. Hardware bitrate policy could undershoot low-bitrate high-resolution input

### Problem

The first size-reduction patch changed hardware bitrate targeting from source bitrate x1.5 to source bitrate x1.0.

The policy still calculated a resolution quality floor, but immediately returned source bitrate whenever source bitrate metadata was present.

For example, an 8 Mbps 4K source would have targeted 8 Mbps even though the existing 4K quality floor is 28 Mbps.

Returning all the way to the old floor would reintroduce large output-size inflation.

### Fix

A bounded source-relative guardrail was added.

For a known source bitrate:

```text
guardrail = min(resolutionFloor, sourceBitrate * 1.25)
target    = max(sourceBitrate, guardrail)
target    = max(target, 2 Mbps)
```

This means the policy can move toward the resolution floor but never increases a known source bitrate by more than 25% solely because of the floor.

Examples at 30 fps:

| Source | Resolution | Previous first patch | New target |
|---|---:|---:|---:|
| 1 Mbps | 4K | 1 Mbps | 2 Mbps minimum |
| 8 Mbps | 4K | 8 Mbps | 10 Mbps |
| 30 Mbps | 4K | 30 Mbps | 30 Mbps |

Software constant-quality encoders still use CRF/CQ policy rather than this bitrate target.

The AV1/encoder verifier was updated to cover the 1 Mbps, 8 Mbps, and 30 Mbps cases.

### Result

The policy no longer forces the old x1.5 expansion, while very low bitrate hardware encode paths are not left completely unguarded.

The 25% value is a policy choice that still requires real-video size/quality comparison before it should be considered final tuning.

---

## 5. Export and HDR probe native FFmpeg I/O cancellation

### Problem

`FfFrameExtractor` already used FFmpeg `AVIOInterruptCB`, but `VideoExportService` and `VideoHdrProbePolicy` still called native I/O directly.

Blocking calls included:

- `avformat_open_input`
- `avformat_find_stream_info`
- `av_read_frame`

A managed cancellation token could therefore be observed only after a blocking native call returned.

### Fix

Added:

```text
Services/Video/VideoIoInterruptGuard.cs
```

The helper owns a GCHandle-backed FFmpeg `AVIOInterruptCB` and a `CancellationTokenRegistration`.

`VideoExportService` now:

- pre-allocates the input `AVFormatContext`
- installs the interrupt callback before `avformat_open_input`
- keeps a cancellation registration active for the export input lifetime
- checks cancellation before/after native open, stream-info, and frame-read operations

`VideoHdrProbePolicy` now:

- accepts `CancellationToken`
- pre-allocates/configures its input context
- wraps open, stream-info, and frame-read calls with the same interrupt helper
- checks cancellation while receiving probe frames

`VideoExportService` forwards its existing cancellation token to the HDR probe.

### Result

Export input and HDR metadata probe native I/O can now be interrupted through FFmpeg's native callback instead of relying only on post-call managed checks.

---

## 6. ROI ONNX cancellation boundary

### Problem

The ROI refiner already accepted a `CancellationToken`, but the actual detector interface is:

```csharp
IBgraFaceDetector.DetectFacesBgra(...)
```

and does not accept a cancellation token.

For the FaceONNX implementation the synchronous call ultimately reaches:

```csharp
_detector.Forward(input)
```

The external FaceONNX API does not expose a safe cancellation/RunOptions hook through the current wrapper.

### Fix applied

The cancellation token is now forwarded into `TryRefineCandidate` and checked:

1. at entry
2. immediately before `DetectFacesBgra`
3. immediately after `DetectFacesBgra`

This guarantees that no subsequent ROI candidate processing continues once the synchronous inference returns after cancellation.

The runtime hardening invariant now checks this pre/post inference cancellation boundary.

### Remaining limitation

**Inference already executing inside `FaceONNX.Forward()` still cannot be interrupted mid-call with the current detector API.**

This is intentionally not described as fully fixed.

A true mid-inference cancellation fix requires one of:

- exposing ONNX Runtime `RunOptions`/termination from the detector implementation, or
- replacing/wrapping the external FaceONNX forward path with a cancellation-aware implementation.

Running the inference in a detached task and abandoning it was not used because the ROI call uses pinned frame buffers and detector lifetime/state; returning before inference actually stops would create unsafe lifetime/concurrency risks.

---

## 7. Per-video decoded PTS cache stopped at 500,000 entries before the global budget

### Problem

The decoded timeline had:

```text
per-video: 500,000 frames
global:    1,000,000 frames
```

A single long video could therefore stop extending exact ordinal/PTS mapping even when half of the configured global frame budget was still unused.

### Fix

The per-video cap was raised to the existing global cap:

```text
MaxCachedTimelineFramesPerVideo = 1,000,000
MaxCachedTimelineFramesTotal    = 1,000,000
```

### Result

One active long video can use the full existing global PTS frame budget.

### Remaining limitation

The global 1,000,000-frame resident cap is intentionally retained as a memory safety bound. Exact mapping beyond that limit can still fail closed if the global budget is fully occupied.

Removing the global cap requires a different paging/persistence architecture and was not done as part of this corrective patch.

---

## 8. Verification updates

Updated verification coverage includes:

- tracked stabilizer dead-zone behavior
- correct current-frame scene-cut boundary
- tracked stabilizer cancellation
- sparse/cancellable stabilizer invariants
- RGB compatibility contract naming
- RGB CRF 18 quality E2E verifier naming and bounded quality check
- 4K bitrate guardrail cases
- Export/HDR native I/O cancellation source invariants
- ROI inference pre/post cancellation boundary
- per-video PTS cap matching the global frame budget

Relevant scripts:

- `scripts/verify-face-track-postprocess.ps1`
- `scripts/verify-encoder-quality-options.ps1`
- `scripts/verify-rgb-quality-export.ps1`
- `scripts/verify-av1-encoder-policy.ps1`
- `scripts/verify-hdr-metadata-guard.ps1`
- `scripts/verify-runtime-hardening.ps1`
- `scripts/verify-auto-mosaic-default.ps1`

---

## 9. Validation status

At the time this document was written:

- source changes are pushed to `fix/quality-stabilization-debug`
- repository-side static/invariant checks have been updated to match the corrected contracts
- the branch's normal Quality Gate workflow still does not automatically run on `fix/**` pushes because `.github/workflows/quality-gate.yml` is configured for:
  - `main`
  - `hardening/**`
  - pull requests targeting `main`
- no Windows/macOS build result is claimed here
- no actual user video was rerun by this change set

Real runtime validation should specifically compare:

1. output file size and visual quality for H.264/H.265/NVENC/QSV/AMF/VideoToolbox paths used in practice
2. stationary/small-motion blur jitter before vs after tracked stabilization
3. scene-cut transitions
4. long-video cancellation latency
5. low-bitrate 4K hardware export quality
6. RGB CRF 18 color/range/metadata compatibility
7. long videos approaching the 1,000,000 PTS frame budget
---

## 10. Follow-up review corrections

A second source review found four remaining inconsistencies after the fixes above.

### Face-track interpolation memory and cancellation

`FaceTrackInterpolator` still allocated several arrays sized to `totalFrames` and did not accept the Auto pipeline cancellation token.

The interpolator now:

- stores per-frame faces, confidences, frame sizes, and stored-mask membership in sparse dictionary/set-backed containers
- rewrites only frame indices that were actually touched instead of scanning every frame in the video
- accepts and checks a `CancellationToken` while loading masks, building tracks, filtering tracks, filling gaps/tails, and rewriting masks
- forwards the token through `AutoMaskPostProcessPipeline -> AutoMaskTemporalPostProcessor -> FaceTrackInterpolator -> FaceTrackBuilder`

This makes post-process working memory scale primarily with actual mask/detection entries rather than the declared total video frame count.

### Non-monotonic PTS extent safety

A decoded timeline can remain structurally reliable and complete while `SupportsExactTimestampSeek` is disabled because presentation timestamps are not strictly increasing.

`TryGetDecodedTimelineExtentSeconds` now fails closed when exact timestamp seek is unsafe. This prevents `FrameListViewModel` from promoting `last PTS - first PTS` to a known duration for a timeline that was already rejected for exact timestamp navigation.

### FrameAnalyzer VFR timestamps

`FrameAnalyzer` no longer assigns `TimestampSec = idx / fps` unconditionally.

After decoding each ordinal it now prefers the decoded PTS already recorded by `FfFrameExtractor.TryGetCachedFrameTimestampSeconds`. Average-FPS time is retained only as a fallback when no decoded timestamp is available.

### FFmpeg I/O interrupt implementation

`FfFrameExtractor` no longer carries its own GCHandle, interrupt flag, callback, and cancellation scope implementation.

It now uses the same `VideoIoInterruptGuard` already used by export and HDR probing for both the main format context and the ordinal-index format context.

### Verification updates

The runtime hardening verifier now checks:

- sparse/cancellable face-track interpolation
- absence of `totalFrames`-sized interpolation arrays
- absence of a full-video rewrite scan
- cancellation inside `FaceTrackBuilder`
- fail-closed decoded timeline extent when exact timestamp seek is disabled
- decoded-PTS-first `FrameAnalyzer` timestamps
- removal of the duplicate extractor interrupt callback implementation

The face-track post-process harness also verifies that a pre-cancelled token is observed by `FaceTrackInterpolator`.

### Validation status

These follow-up changes were source-reviewed and the repository verification scripts were updated. The normal GitHub Actions workflow still does not run automatically for `fix/**` pushes, so no Windows/macOS Actions build or real-video runtime result is claimed here.

---

## 11. Temporal smoothing and exception-safety follow-up

A further review of `docs/manual-blur-player-architecture` found three remaining correctness/resource issues.

### Sparse and cancellable temporal smoothing

`ApplyTemporalSmoothing` still allocated four arrays sized to `totalFrames` and scanned the full declared video range on every smoothing pass and rewrite pass.

It now:

- stores only actual face-mask frames in dictionaries
- stores manual/stored-mask frame membership in a `HashSet<int>`
- iterates only actual face-mask frame indices during smoothing and rewrite
- preserves the existing search window in frame-number units, so a face three frames away is still excluded when the configured window is two frames
- preserves step-by-step scene-cut blocking while searching backward or forward
- keeps stored/manual mask frames excluded from smoothing input and output
- accepts a `CancellationToken` and checks it while building cut boundaries, reading mask entries, smoothing frames/faces, and rewriting results
- receives the existing Auto post-process cancellation token from `AutoMaskPostProcessPipeline`

The face-track verification harness now covers normal smoothing, scene-cut blocking, distance-window behavior, and pre-cancelled execution.

### FfFrameExtractor interrupt-guard exception safety

The shared `VideoIoInterruptGuard` previously allocated its GCHandle in a field initializer before the `FfFrameExtractor` constructor entered its cleanup-protected initialization block.

The extractor now keeps a nullable guard field and creates the guard inside the constructor `try` block. Constructor failure after guard creation therefore reaches `Dispose()`, while path/timeline failures that happen earlier occur before any interrupt GCHandle is allocated. Disposal is null-safe for failures during initialization.

### FrameAnalyzer exact timestamp contract

`FrameAnalyzer` no longer silently substitutes `frameIndex / averageFps` when a decoded presentation timestamp is unavailable.

It now records:

- decoded PTS seconds when the extractor has a reliable cached timestamp
- `double.NaN` when no reliable decoded presentation timestamp is available

`FrameAnalysisResult.TimestampSec` documents this exact-time contract explicitly. This avoids presenting an average-FPS estimate as an exact timestamp on VFR or irregular-PTS material.

### Remaining cancellation boundary

`AVIOInterruptCB` still interrupts FFmpeg I/O operations, not arbitrary codec execution or synchronous detector inference already running inside a native call. The FaceONNX path therefore still checks cancellation immediately before and after synchronous detection, but cannot terminate the external synchronous inference call mid-execution with the current detector API.

### Validation status

These changes were pushed to `docs/manual-blur-player-architecture` with source invariants and the face-track harness updated. The branch is not covered by the normal push-triggered Quality Gate, so no Windows/macOS Actions build or real-video runtime validation is claimed by this follow-up.

