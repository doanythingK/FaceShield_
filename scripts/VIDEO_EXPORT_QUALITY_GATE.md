# Video Export Quality Gate

`verify-video-export-quality.ps1` independently checks an exported video against
its original source. It uses `ffprobe` for stream, pixel format, and packet timing
evidence, then uses `ffmpeg` to decode the entire output and reject corrupt files.

## Usage

Run from the repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File scripts\verify-video-export-quality.ps1 `
  -SourcePath D:\videos\source.mp4 `
  -OutputPath D:\videos\source_blur.mp4 `
  -ReportPath .tmp\quality-gates\source_blur.json
```

`ffprobe` and `ffmpeg` must be on `PATH`. A specific installation can be used with
`-FfprobePath` and `-FfmpegPath`.

The default gate requires:

- equal video and audio stream counts;
- equal resolution and sample aspect ratio;
- frame-rate delta no larger than `0.001` fps;
- exact frame count;
- output bit depth not lower than the source;
- output chroma component count and sampling not lower than the source;
- preservation of specified range, space, transfer, and primaries metadata;
- equal audio codec, sample rate, channels, and channel layout;
- duration and packet span delta no larger than half a source frame plus 1 ms;
- audio/video start and end skew change no larger than 10 ms;
- successful full decode of the exported video.

Any failed check exits non-zero. The JSON report is written even when a comparison
fails, so CI and benchmark runs can retain the exact evidence. Use
`-MaxTimelineDeltaSeconds`, `-MaxAvSkewDeltaSeconds`, `-MaxFpsDelta`, or
`-MaxFrameCountDelta` only when the source format has a documented container-level
tolerance. `-SkipDecodeCheck` is available for a metadata-only diagnostic run; it
should not be used as the final export gate.
