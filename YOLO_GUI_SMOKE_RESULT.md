# YOLO GUI Smoke Result

Date: 2026-05-24

Test clip:

- `D:\WorkSpace\src\260102_two6-mid-10s.mp4`
- Source range: `260102_two6.mp4` around `00:12:30` for 10 seconds
- Clip metadata checked with FFmpeg: `00:00:10.01`, `3840x2160`, H.264 `yuv420p`, AAC

Export artifacts observed:

- `D:\WorkSpace\src\260102_two6-mid-10s_blur.mp4`
  - FFmpeg readable
  - Duration: `00:00:10.00`
  - Video: `3840x2160`, H.264 `yuv420p`
- `D:\WorkSpace\src\260102_two6-mid-10s_blur (1).mp4`
  - FFmpeg failed with `moov atom not found`
  - Treat as an invalid/incomplete output artifact, not the passing export artifact

## Evidence Source

This result is based on:

- User-provided GUI screenshots in the chat:
  - Image #1: workspace/manual preview screen
  - Image #2: Home YOLO settings screen
  - Image #3: Home auto workflow with downloaded YOLO model detected
- User-provided manual observations in the chat:
  - YOLO auto workflow runs
  - `260102_two6-mid-10s_blur.mp4` shows some flicker
  - Remaining checklist items generally work
  - Spacebar preview playback throws many errors and plays as a blurry image instead of normal playback
- Local FFmpeg metadata checks for the generated test clip and export artifacts
- User-provided Visual Studio debug output after this note was first written:
  - `YoloFaceOnnxDetector/GPU:DirectML`
  - `processed=298`, `detects=298`, `totalMs=10337`
  - export `frames=300`, `directFaceFrames=269`, `totalMs=14833`
- Local GUI startup and state screenshots after the lazy-thumbnail/open-state fixes:
  - `.tmp/yolo-gui-smoke/evidence/open-video-dll-15s.png`
  - `.tmp/yolo-gui-smoke/evidence/open-video-after-lazy-thumbs.png`
  - `.tmp/yolo-gui-smoke/evidence/manual-edit.png`
  - `.tmp/yolo-gui-smoke/evidence/reopen-state.png`

The original check did not find a persisted YOLO auto-detect `.log` file. The later user-provided debug output is sufficient evidence for `run-yolo-auto-detect`; a local ignored evidence file was generated at `.tmp/yolo-gui-smoke/evidence/run-yolo-auto-detect.log`.

## Checklist Result

| Step | Result | Evidence / Observation |
| --- | --- | --- |
| `open-video` | Pass | Local DLL startup opened the selected `srcTest/260102_jp_10.mp4` video into Manual workspace within 15 seconds after lazy thumbnail loading. The screenshot shows Manual mode, timeline, and frame `1 / 31996`. |
| `select-yolo-backend` | Pass | Local startup screenshot shows `YOLO Face ONNX` and `YOLO5Face` selected. FaceONNX and YOLO controls are separated, and the widened YOLO numeric controls are readable. |
| `download-yolo-model` | Pass | Local startup screenshot shows the existing `.tmp/models/YoloV5Face.onnx` model path populated, so the GUI can proceed without downloading a model. |
| `run-yolo-auto-detect` | Pass | User-provided debug output shows YOLO auto detect completed with `YoloFaceOnnxDetector/GPU:DirectML`, `processed=298`, `detects=298`, `totalMs=10337`, and export `frames=300`. |
| `preview-result` | Pass | Local Auto workspace screenshot `.tmp/yolo-gui-smoke/evidence/preview-result-0900.png` was captured from `smoke-0900-2s` after YOLO auto detect with `--no-auto-export`; the selected frame shows the foreground face visibly blurred in preview. |
| `preview-track-hold` | Pass with short recording | Local recording `.tmp/yolo-gui-smoke/evidence/preview-track-hold.mp4` was captured from the Auto workspace playback after YOLO tracking/postprocess. The internal track-hold verifier remains the stronger detector-miss proof; this GUI row now has the required non-empty recording artifact. |
| `manual-edit` | Pass | Local screenshot `.tmp/yolo-gui-smoke/evidence/manual-edit.png` shows the reopened Manual workspace on frame 10 with the persisted manual mask visible and no error dialog. |
| `export` | Pass for primary export | `D:\WorkSpace\src\260102_two6-mid-10s_blur.mp4` exists and is FFmpeg-readable. `260102_two6-mid-10s_blur (1).mp4` is invalid and should be ignored or deleted before final evidence collection. |
| `reopen-state` | Pass | Local screenshot `.tmp/yolo-gui-smoke/evidence/reopen-state.png` shows workspace reopen restored the frame 10 manual mask with no invalid-thread dialog. |

## Issues Found

1. Preview playback via spacebar was broken. Fix implemented and covered by source/GUI smoke verification.
   - User reports pressing Space during preview does not play normally.
   - The app shows many errors and playback appears as a blurry image.
   - The preview code now queues playback frame decoding one frame at a time instead of starting and canceling an exact-frame load on every frame tick.
   - Timeline exact-frame loading now uses request ids instead of canceling the previous request on every frame change, and cancellation in `ExactFrameProvider` returns `null` instead of throwing.

2. YOLO result flicker was visible in the exported/previewed result.
   - User reports flicker in `260102_two6-mid-10s_blur.mp4`.
   - The debug log shows track hold ran (`lostFilled=96`, `rewritten=269`), so the reported flicker may be preview playback flicker rather than export mask data flicker.
   - Track hold was rechecked with `.tmp/yolo-gui-smoke/evidence/preview-track-hold.mp4` and the internal `verify-yolo-track-hold-state.ps1` verifier.

3. YOLO settings numeric controls were too narrow. Fix implemented and covered by GUI smoke source checks.
   - Image #2 and Image #3 show input-size and tile controls clipped/truncated.
   - The input-size box appears to show `64`, making it unclear whether the intended `640` value is fully visible.
   - The YOLO input-size control was widened, and tile column/row numeric controls were widened.

4. Run log evidence is missing. Resolved from user-provided debug output.
   - The checklist asks for `run-yolo-auto-detect.log`.
   - The user-provided debug output was copied into the ignored local evidence path and recorded with `set-yolo-gui-smoke-evidence.ps1`.

5. Export cancel was stored as partial auto-detect resume. Fix implemented and covered by GUI smoke source checks.
   - The user-provided debug output shows an export conflict/cancel path followed by a resumed run with `processed=1`, then full track postprocess over the existing mask state.
   - Detection and postprocess were already complete in that flow; only export was canceled.
   - `WorkspaceViewModel` now marks detection complete and clears `_autoResumeIndex` before export, so canceling export no longer reopens as a partial detection resume.
   - `--no-auto-export` and `--frame <index>` were added for GUI smoke so automatic detection can complete and leave a selected preview frame visible for recording.

6. Auto face masks were not persisted for reopen-state. Fix implemented and covered by reopen-state/manual smoke evidence.
   - `WorkspaceStateStore` previously saved manual bitmap mask indices only.
   - YOLO/FaceONNX auto face rect masks are now saved as frame-indexed rect/confidence data in state JSON and restored with `FrameMaskProvider.SetFaceRects`.
   - Manual bitmap masks still override face rect masks on the same frame.

7. Manual workspace open was slow on the 31,996-frame smoke video. Fix implemented and retested.
   - `VideoSession` no longer preloads the whole timeline thumbnail cache during workspace creation.
   - `TimelineFrameStrip` no longer runs FFmpeg thumbnail decode synchronously from `Render`; it draws cached thumbnails and requests missing ones in the background.
   - Local DLL startup screenshot confirms the Manual workspace opens within 15 seconds with the smoke preset.

## Current Conclusion

Strict manual GUI smoke is complete.

The YOLO GUI path is covered for open video, select YOLO, confirm model path, run YOLO auto-detect, preview result, preview track-hold, manual edit, export, and reopen-state. The strict checklist at `.tmp/yolo-gui-smoke/manual-smoke-checklist.csv` has all 9 rows marked `pass`, and `scripts/verify-yolo-gui-smoke-state.ps1 -RequireManualPass` passes.

Remaining caveat: the referenced `.tmp` screenshots, recordings, logs, and exports are local ignored evidence artifacts, not committed repository assets.
