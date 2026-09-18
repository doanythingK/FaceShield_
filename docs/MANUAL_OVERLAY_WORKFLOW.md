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

- Added a separate next-frame tracking button as a bounded option; existing continuous tracking remains. This button is **not** the principal feature or a two-frame limit.
- `ManualMaskTrackingService.cs` now admits three or more candidate landmarks per connected mask component. It first tries the existing six-inlier similarity estimator. If that fails or is insufficiently confident, `ManualTranslationEstimator.cs` can estimate **translation only** from at least three spatially separated, mutually consistent, forward/backward-validated features. It checks displacement, appearance, consensus, spatial support and confidence. Rotation/scale are not guessed from sparse matches. Blank/ambiguous regions may still fail; this is not face detection.
- A false return from the sequential decoder now distinguishes cancellation, explicit decoder error, unexpected termination and genuine EOF. Mid-stream decoder failures are no longer declared normal completion.
- Once there is at least one user-stored bitmap keyframe, `ManualMaskKeyframeTimeline.GetNextExplicitKeyframe` uses only stored bitmap keyframes as correction boundaries: unrelated Auto-only detection frames no longer terminate this **legacy stored-mask track**. Auto-only legacy sessions retain their old behavior.
- The existing timeline now combines the tracked manual bitmap with the **exact same frame's available Auto rectangle mask** for effective preview and export masks. `FramePreviewViewModel.ManualKeyframes.cs` no longer discards this tracked result merely because Auto detected another face on that frame. `ManualKeyframeExportMaskProvider` returns the combined bitmap on both its regular and borrowed-mask paths. Missing manually required tracking samples continue to fail closed rather than copying a stationary mask.
- `scripts/manual-auto-overlap-integration.cs.txt` exercises production FFmpeg tracking on a generated static-texture clip: user-stored source at frame 550, unrelated Auto face entries at 549, 551 and 552; manual tracking generates validated samples for 551 and 552. Actual timeline preview, export bitmap and borrowed-mask paths must contain both regions at 551; next-frame 552 must also contain both, Auto 549 must remain, Auto data must not be mutated, and untracked frame 553 must fail closed.
- `scripts/manual-translation-regression.cs.txt` exercises consistent three-feature translation, mismatch, ambiguity, invalid numeric values and cancellation. `scripts/manual-moving-tracking-integration.cs.txt` exercises the production FFmpeg decoder + tracker on a generated 160×120 clip: a 56×56 textured target moves (+1,+1) pixels per frame for 16 frames. It asserts all 15 tracking samples exist and each reported translation differs from ground truth by at most 2 pixels. The original stationary/scene-cut/cancellation/export-fail-closed synthetic test remains.
- [Quality Gate #246](https://github.com/doanythingK/FaceShield_/actions/runs/35314423538) at code commit `35f523e2` passed Windows/macOS builds, isolated overlay and pyramid tests, moving-object tracking, scene-cut/cancellation, and the new real-timeline Auto-crossing preview/export mask integration test. All tracking fixtures are **synthetic** and the macOS tracker integration uses installed FFmpeg 8 libraries, not the packaged app. No real moving-face accuracy, GUI or encoded export pixel parity has been established.

## Still missing — not represented as complete

1. **Same-frame data preservation:** `FrameMaskProvider.SetMask` removes Auto face data at that same frame; `SetFaceRectsLocked` removes a stored bitmap. Editing Auto and manual masks on the *same* frame can therefore still discard one layer. The verified integration only covers an earlier stored manual keyframe plus Auto entries at later, different frames. Never claim full Auto+manual layering until this is fixed and tested.
2. **True per-target state:** the independent target/state-store prototype is not wired to the actual editor, mask edit/Undo, preview/export and workspace lifecycle. Multiple unrelated manually stored keyframes still share a legacy global keyframe sequence, can interrupt each other, and validated tracking samples are not persisted per target. Legacy mask migration needs explicit tests.
3. **Corrections and safety:** tracking initiated from an Auto-only frame inside an existing manual track can still select the Auto mask as its new source instead of the intended manual target. Need selected-target semantics, correction/resume, mask-edit fingerprint invalidation and target-specific unresolved-export checks. Current export preflight also uses global keyframes. Do not weaken missing-sample fail-closed behavior.
4. Measure tracking on real moving faces, small/blurred/low-texture subjects, occlusion, rotation, scene cuts and near-edge cases using missed-blur and false-blur metrics. The translation fallback cannot infer motion from a truly featureless scene.
5. Verify Windows GUI, decoded preview versus **encoded output pixel** parity, video startup latency, packaged macOS FFmpeg dependencies and long-video memory/CPU separately.

## Actions run-record cleanup

A one-shot cleanup completed at [run #232](https://github.com/doanythingK/FaceShield_/actions/runs/35295440490): 55 redundant completed Quality Gate records were deleted (0 failures) while recent/important runs and deployment packaging runs were excluded. Historic deleted runs must not be cited as live CI evidence. The Windows/macOS packaging workflows remain manually dispatched.
