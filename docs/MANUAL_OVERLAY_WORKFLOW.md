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
- The existing timeline combines the tracked manual bitmap with the **exact same frame's available Auto rectangle mask** for effective preview and export masks. Missing manually required tracking samples continue to fail closed rather than copying a stationary mask.
- `FrameMaskProvider` now keeps Auto face rectangles and stored manual bitmap data as independent layers on the same frame. `SetMask` no longer deletes Auto, `SetFaceRects` no longer deletes manual, snapshots/persistence carry both, and `GetFinalMask` unions the two layers.
- `ManualMaskLayerIsolation` handles the real editor flow where an Auto-only frame is shown as an editable composite: when the user paints an additional missed subject, the already-existing Auto coverage is stripped before the stored bitmap is accepted. The stored manual layer therefore contains the user addition rather than a duplicate copy of the Auto face. Updating Auto afterward leaves the manual residual intact.
- Manual tracking now clones a stored manual bitmap first and uses that layer alone as the tracking source and fingerprint when Auto also exists on the source frame. Same-frame Auto is composed only for display/export, not fed into the tracker as another target.
- `scripts/frame-mask-layer-regression.cs.txt` exercises Auto-first editor composite → manual addition → storage, proves the stored manual layer excludes Auto coverage, proves final/snapshot masks contain both, and proves an Auto update does not erase the manual residual.
- `scripts/manual-auto-overlap-integration.cs.txt` exercises production FFmpeg tracking on a generated static-texture clip: user-stored source at frame 550, unrelated Auto face entries at 549, 551 and 552; manual tracking generates validated samples for 551 and 552. Actual timeline preview, export bitmap and borrowed-mask paths contain both regions; untracked coverage still fails closed.
- `scripts/manual-translation-regression.cs.txt` exercises consistent three-feature translation, mismatch, ambiguity, invalid numeric values and cancellation. `scripts/manual-moving-tracking-integration.cs.txt` exercises the production FFmpeg decoder + tracker on a generated 160×120 clip: a 56×56 textured target moves (+1,+1) pixels per frame for 16 frames. It asserts all 15 tracking samples exist and each reported translation differs from ground truth by at most 2 pixels.
- [Quality Gate #259](https://github.com/doanythingK/FaceShield_/actions/runs/35319597259) at code commit `269d560e` passed Windows/macOS builds, runtime hardening checks, same-frame layer separation, isolated overlay/persistence tests, pyramid/translation tests, moving-object tracking, scene-cut/cancellation and Auto-crossing preview/export integration. All tracking fixtures are **synthetic** and the macOS tracker integration uses installed FFmpeg 8 libraries, not the packaged app. No real moving-face accuracy, GUI or encoded export pixel parity has been established.

## Still missing — not represented as complete

1. **True per-target state:** the independent target/state-store prototype is not wired to the actual editor, mask edit/Undo, preview/export and workspace lifecycle. Multiple unrelated manually stored keyframes still share a legacy global keyframe sequence, can interrupt each other, and validated tracking samples are not persisted per target. Legacy mask migration needs explicit tests.
2. **Target-specific correction/resume and export safety:** the application still lacks selected-target semantics. Correction keyframes, continuation and unresolved-export checks are still global rather than tied to one manual target. Do not weaken missing-sample fail-closed behavior.
3. **Failure rollback audit:** legacy rollback paths that remove a frame/range need to be rechecked now that Auto and manual layers can coexist, so an error while committing a manual tracking source cannot accidentally remove unrelated Auto data.
4. Measure tracking on real moving faces, small/blurred/low-texture subjects, occlusion, rotation, scene cuts and near-edge cases using missed-blur and false-blur metrics. The translation fallback cannot infer motion from a truly featureless scene.
5. Verify Windows GUI, decoded preview versus **encoded output pixel** parity, video startup latency, packaged macOS FFmpeg dependencies and long-video memory/CPU separately.

## Actions run-record cleanup

A one-shot cleanup completed at [run #232](https://github.com/doanythingK/FaceShield_/actions/runs/35295440490): 55 redundant completed Quality Gate records were deleted (0 failures) while recent/important runs and deployment packaging runs were excluded. Historic deleted runs must not be cited as live CI evidence. The Windows/macOS packaging workflows remain manually dispatched.
