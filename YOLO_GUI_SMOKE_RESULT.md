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

No recent YOLO auto-detect `.log` file was found under the repository `.tmp` tree or the source video folder during this check. The run-auto step is therefore recorded from user observation and export artifact presence, not from a persisted log file.

## Checklist Result

| Step | Result | Evidence / Observation |
| --- | --- | --- |
| `open-video` | Pass | Image #1 shows the 10 second clip opened in Workspace with preview/timeline visible. |
| `select-yolo-backend` | Pass with UI issue | Image #2 shows `YOLO Face ONNX` and `YOLO5Face` selected. FaceONNX and YOLO controls are separated. The input-size and tile numeric controls are too narrow; the input size appears truncated as `64` instead of a clearly readable value. |
| `download-yolo-model` | Pass | Image #3 shows the YOLO model path populated and status text `이미 다운로드됨: YoloV5Face.onnx`. |
| `run-yolo-auto-detect` | Pass, evidence incomplete | User reports the workflow runs. `D:\WorkSpace\src\260102_two6-mid-10s_blur.mp4` exists and is FFmpeg-readable. A persisted run log was not found. |
| `preview-result` | Fail / needs fix | User reports `260102_two6-mid-10s_blur.mp4` shows some flicker. This should not be marked clean pass until visually reviewed after a fix. |
| `preview-track-hold` | Needs focused review | User says remaining items generally work, but the reported flicker means track-hold behavior should be rechecked specifically around masked people in the plaza clip. |
| `manual-edit` | Pass by user observation | User reports remaining items are normal. No committed screenshot artifact was available in the repository at the time of this note. |
| `export` | Pass for primary export | `D:\WorkSpace\src\260102_two6-mid-10s_blur.mp4` exists and is FFmpeg-readable. `260102_two6-mid-10s_blur (1).mp4` is invalid and should be ignored or deleted before final evidence collection. |
| `reopen-state` | Pass by user observation | User reports remaining items are normal. No committed screenshot artifact was available in the repository at the time of this note. |

## Issues Found

1. Preview playback via spacebar is broken.
   - User reports pressing Space during preview does not play normally.
   - The app shows many errors and playback appears as a blurry image.
   - This is a functional GUI playback bug and should be fixed before final GUI smoke completion.

2. YOLO result flicker is visible in the exported/previewed result.
   - User reports flicker in `260102_two6-mid-10s_blur.mp4`.
   - This affects `preview-result` and possibly `preview-track-hold`.
   - Track hold should be re-verified after addressing the flicker.

3. YOLO settings numeric controls are too narrow.
   - Image #2 and Image #3 show input-size and tile controls clipped/truncated.
   - The input-size box appears to show `64`, making it unclear whether the intended `640` value is fully visible.
   - Widen the numeric controls for input size, tile columns, and tile rows.

4. Run log evidence is missing.
   - The checklist asks for `run-yolo-auto-detect.log`.
   - No recent YOLO run log was found during this check.
   - Either the app should persist a log for manual smoke, or the final evidence should include a screenshot/recording accepted by the checklist tooling.

## Current Conclusion

Do not mark the strict manual GUI smoke as fully complete yet.

The basic YOLO GUI path is usable: open video, select YOLO, detect/export, manual edit, and reopen were observed as working. However, the smoke result is not clean because preview playback has a Space-key playback bug, the YOLO output shows flicker, and strict evidence artifacts are not yet collected into `.tmp/yolo-gui-smoke/manual-smoke-checklist.csv`.

Recommended next fixes:

1. Fix preview Space-key playback errors and blurry playback state.
2. Investigate flicker/track-hold behavior on `260102_two6-mid-10s_blur.mp4`.
3. Widen YOLO input-size and tile numeric controls.
4. Add or capture durable evidence for `run-yolo-auto-detect`.
