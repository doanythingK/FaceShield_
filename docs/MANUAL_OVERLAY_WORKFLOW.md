# Manual overlay and tracking — implementation status

Branch: `refactor/manual-overlay-workflow` (based on `refactor/manual-mask-keyframes` at `f9ad497a`).

## Intended workflow

Auto masks must remain intact. A user adds an independent manual mask for a missed subject. An explicit manual mask covers its exact frame without requiring face detection or tracking features. Each manual target must have its own ID, correction keyframes, validated tracking samples and end boundary. A detection of another face must not end its track. On each frame preview and export must combine Auto masks with only valid manual masks; missing coverage must never silently turn into a stationary mask or an unblurred successful export. Corrections must allow continuing a long forward track.

## Implemented in isolated foundation

- `Services/Video/ManualOverlayCore.cs`: independent per-target keyframe/boundary primitive; BGRA alpha union of Auto and multiple manual masks, without holding a missing manual mask stationary.
- `Services/Video/ManualOverlayStateStore.cs`: versioned independent JSON state with stable target IDs and **explicit manually confirmed alpha keyframes only**; compressed alpha, atomic replacement and corruption/source-evidence checks. Validated tracking samples are **not** stored here yet.
- `Services/Video/ManualOverlayWorkspaceStore.cs`: distinct video-path-aware storage paths and video file length + last-modified evidence. This is not a cryptographic content hash. The application's workspace does **not** call this adapter yet.
- Isolated regressions verify alpha union, target boundaries, no stationary hold, independent target/keyframe persistence and source mismatch rejection.

## Changes applied to the existing tracking path

- Existing continuous tracking remains the primary mode. A separate next-frame button exists but is not a two-frame limit.
- `ManualMaskTrackingService.cs` accepts three or more candidate landmarks per connected mask component. It first tries the existing six-inlier similarity estimator. If that fails or is insufficiently confident, `ManualTranslationEstimator.cs` can estimate **translation only** from at least three spatially separated, mutually consistent, forward/backward-validated features. It checks displacement, appearance, consensus, spatial support and confidence. Rotation/scale are not guessed from sparse matches. Blank/ambiguous regions may still fail; this is not face detection.
- Sequential decoder failures are distinguished from cancellation and genuine EOF; mid-stream decoder errors are not declared normal completion.
- Once there is at least one user-stored bitmap keyframe, `ManualMaskKeyframeTimeline.GetNextExplicitKeyframe` uses only stored bitmap keyframes as correction boundaries. Unrelated Auto-only detection frames no longer terminate this **legacy stored-mask track**. Auto-only legacy sessions retain their original behavior.
- The existing timeline combines the tracked manual bitmap with the **same frame's Auto rectangle mask** for effective preview/export; missing manually required samples fail closed rather than copying a stationary mask.
- `FrameMaskProvider` preserves Auto rectangles and stored manual bitmaps as independent layers on the same frame. `SetMask` no longer deletes Auto, `SetFaceRects` no longer deletes manual, snapshots/persistence retain both, and `GetFinalMask` unions both.
- `ManualMaskLayerIsolation` removes previously existing Auto coverage from the editor composite before storing manual additions. Updating Auto afterward leaves the manual residual intact.
- Manual tracking reads the stored manual bitmap alone and fingerprints that source when Auto also exists at the same frame. Auto is added only to display/export masks.
- If a tracking source promotion fails workspace persistence, its in-memory manual source is retained for a subsequent save; the previous destructive frame-range rollback was removed, preserving independent Auto masks.
- **Continuous tracking continuation:** `ManualTrackingSourceSelection` and `FramePreviewViewModel.ManualTracking.cs` select a stored manual correction or a validated inherited manual result when restarting in the middle of a track. An Auto-only detection is never silently used as the next tracking source after a manual mask exists. When no verified manual source exists, the UI asks for a manually specified mask instead of tracking a different face. A successfully tracked inherited source is promoted to an explicit stored correction before its new segment is recorded. Legacy Auto-only workspaces retain their prior source-selection path.
- **Tracking sample integrity:** `ManualMaskTrackStore` now rejects missing middle samples, missing end samples in any component, malformed transforms and duplicate source keyframes before saving and when loading a persisted track. Invalid saves do not replace the previous valid state. Strict export loading rejects corrupt state; permissive preview loading skips invalid segments. This validates stored continuity; it does **not** increase tracking accuracy or prove that a visually incorrect but continuous trajectory is correct.
- The current inherited-source extraction removes current-frame Auto alpha from the validated composite. **Overlapping Auto and manual pixels are not guaranteed to be preserved by this subtraction.** A manual-only raster API is needed for exact overlap handling; current synthetic tracking fixtures use disjoint subjects.

## Verified regressions

- `scripts/frame-mask-layer-regression.cs.txt`: Auto-first editor composite → manual addition → storage; manual stored layer excludes Auto, final/snapshot masks contain both, and Auto updates preserve the manual residual.
- `scripts/manual-auto-overlap-integration.cs.txt`: production FFmpeg on a generated static-texture clip. Manual source and Auto coexist at frame 550, unrelated Auto also occurs at 549, 551, 552. The initial track generates verified samples at 551/552; preview/export and borrowed-mask paths retain both subjects. Continuation at Auto-only frame 551 selects manual-only alpha, tracks another frame, promotes 551 to a correction keyframe, then preview/export at 552 still contain both regions. Untracked frame 553 cannot become a tracking source. This does **not** test two independently tracked manual targets.
- `scripts/manual-track-continuity-regression.cs.txt`: complete multi-component tracking state round-trip; reject missing intermediate sample, incomplete component tail, duplicate keyframe, and manually corrupted persisted state while preserving the last good save.
- `scripts/manual-translation-regression.cs.txt`: consistent three-feature translation, mismatch, ambiguity, nonfinite values, cancellation.
- `scripts/manual-moving-tracking-integration.cs.txt`: generated 160×120 video with a 56×56 textured target moving (+1,+1) pixels/frame for 16 frames; all 15 samples exist and each translation differs from known motion by at most 2 pixels.
- [Quality Gate #268](https://github.com/doanythingK/FaceShield_/actions/runs/35356849656) at code/test commit `d9848edb` passed Windows/macOS builds, hardening checks, new persisted-track continuity regressions, overlay/persistence, pyramid/translation and FFmpeg-based synthetic tracking/continuation integrations. The integration uses installed FFmpeg 8 libraries, **not** packaged-app dependencies. No real-face accuracy, GUI operation or encoded-output pixel parity has been established.

## Still missing — not represented as complete

1. **True per-target state:** the independent target/state-store prototype is not wired to the actual editor, mask edit/Undo, preview/export and workspace lifecycle. Unrelated manually stored keyframes still share a legacy global keyframe sequence and may interrupt each other; verified samples are not stored per target. Legacy mask migration requires tests.
2. **Target-specific correction and export safety:** there is no selected-manual-target UI/identity in the current editor. Continuation avoids accidental Auto-source selection but correction boundaries and unresolved-export checks still use the global legacy manual sequence. Do not weaken missing-sample fail-closed behavior.
3. **Exact overlapping masks:** inherited manual-only source currently relies on stripping Auto from a composite. When subjects overlap, this can remove real manual pixels. The tracker should instead rasterize the selected target independently, and editor strokes need separate manual-layer representation instead of subtracting opaque Auto coverage.
4. Test on real moving faces, small/blurred/low-texture subjects, occlusion, rotation, scene cuts and near-edge cases using missed-blur and false-blur metrics. Truly featureless scenes cannot have motion reliably inferred by the sparse translation fallback.
5. Verify Windows GUI, decoded preview versus **encoded output pixels**, startup latency, packaged macOS FFmpeg dependencies and long-video memory/CPU separately.

## Actions run-record cleanup

A one-shot cleanup completed at [run #232](https://github.com/doanythingK/FaceShield_/actions/runs/35295440490): 55 redundant completed Quality Gate records were deleted (0 failures) while recent/important and packaging runs were excluded. Historic deleted runs must not be cited as live CI evidence. Windows/macOS packaging workflows remain manually dispatched.
