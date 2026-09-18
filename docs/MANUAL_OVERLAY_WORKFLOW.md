# Manual overlay and tracking — implementation status

Branch: `refactor/manual-overlay-workflow` (based on `refactor/manual-mask-keyframes` at `f9ad497a`).

## Intended workflow

Auto masks must remain intact. A user adds an independent manual mask for a missed subject. An explicit manual mask covers its exact frame without requiring face detection or tracking features. Each manual target must have its own ID, correction keyframes, validated tracking samples and end boundary. A detection of some other face must not end its track. On each frame preview and export must combine Auto masks with only valid manual masks; missing coverage must never silently turn into a stationary mask or an unblurred successful export. Corrections must allow continuing a long forward track.

## Implemented in isolated foundation

- `Services/Video/ManualOverlayCore.cs`: independent per-target keyframe/boundary primitive; BGRA alpha union of Auto and multiple manual masks, without holding a missing manual mask stationary.
- `Services/Video/ManualOverlayStateStore.cs`: versioned independent JSON state with stable target IDs and **explicit manually confirmed alpha keyframes only**; compressed alpha, atomic replacement and corruption/source-evidence checks. Validated tracking samples are **not** stored here yet.
- `Services/Video/ManualOverlayWorkspaceStore.cs`: distinct video-path-aware storage paths and video file length + last-modified evidence. This is not a cryptographic content hash. The application's workspace does **not** call this adapter yet.
- Isolated regressions verify alpha union, target boundaries, no stationary hold, independent target/keyframe persistence and source mismatch rejection.

## Changes applied to the existing tracking path

- Added a separate next-frame tracking button as a bounded option; existing continuous tracking remains. This button is **not** the principal feature or a two-frame limit.
- `ManualMaskTrackingService.cs` now admits three or more candidate landmarks per connected mask component. It first tries the existing six-inlier similarity estimator. If that fails or is insufficiently confident, `ManualTranslationEstimator.cs` can estimate **translation only** from at least three spatially separated, mutually consistent, forward/backward-validated features. It checks displacement, appearance, consensus, spatial support and confidence. Rotation/scale are not guessed from sparse matches. Blank/ambiguous regions may still fail; this is not face detection.
- A false return from the sequential decoder now distinguishes cancellation, explicit decoder error, unexpected termination and genuine EOF. Mid-stream decoder failures are no longer declared normal completion.
- Tests: `scripts/manual-translation-regression.cs.txt` exercises consistent three-feature translation, mismatch, ambiguity, invalid numeric values and cancellation. `scripts/manual-moving-tracking-integration.cs.txt` exercises the production FFmpeg decoder + tracker on a generated 160×120 clip: a 56×56 textured target moves (+1,+1) pixels per frame for 16 frames. It asserts all 15 tracking samples exist and each reported translation differs from ground truth by at most 2 pixels. The original stationary/scene-cut/cancellation/export-fail-closed synthetic test remains.
- Verified on [Quality Gate #242](https://github.com/doanythingK/FaceShield_/actions/runs/35310802611): Windows/macOS builds and configured regressions passed. The moving fixture is **synthetic**, not a real moving face or packaged macOS app test. This test does not prove a generalized tracking success rate or blur pixel-perfect accuracy.

## Still missing — not represented as complete

1. Wire the independent target store into the actual workspace editor, mask edit/Undo, preview and export; preserve automatic masks. At present the real app still uses the legacy single-provider workflow, and another face's Auto keyframe can terminate a manual tracking segment.
2. Save and reload validated *per-target tracking samples*, handle mask edits and source fingerprint invalidation, and migrate legacy masks safely without increasing manual-mode startup latency.
3. Support continuous per-target tracking, bounded user-selected intervals and correction/resume without unrelated automatic keyframes; surface verified vs unresolved frames and block export for unresolved required coverage.
4. Measure tracking on real moving faces, small/blurred/low-texture subjects, occlusion, rotation, scene cuts and near-edge cases using missed-blur and false-blur metrics. The translation fallback cannot infer motion from a truly featureless scene.
5. Verify Windows GUI, preview/export pixel parity, video startup latency, packaged macOS FFmpeg dependencies and long-video memory/CPU separately.

## Actions run-record cleanup

A one-shot cleanup completed at [run #232](https://github.com/doanythingK/FaceShield_/actions/runs/35295440490): 55 redundant completed Quality Gate records were deleted (0 failures) while recent/important runs and deployment packaging runs were excluded. Historic deleted runs must not be cited as live CI evidence. The Windows/macOS packaging workflows remain manually dispatched.
