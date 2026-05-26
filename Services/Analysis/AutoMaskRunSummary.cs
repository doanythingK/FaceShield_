using System;

namespace FaceShield.Services.Analysis
{
    public sealed record AutoMaskRunSummary(
        string Mode,
        int TotalFrames,
        int StartFrameIndex,
        int ProcessedFrames,
        int DecodedFrames,
        int DetectedFrames,
        int InterpolatedFrames,
        long ReadMs,
        long DecodeMs,
        long DetectMs,
        long MaskMs,
        long TotalMs,
        double DownscaleRatio,
        DownscaleQuality DownscaleQuality,
        bool UseTracking,
        int DetectEveryNFrames,
        int ParallelDetectorCount,
        string? RoiSummary,
        string? RunId,
        string? DetectorName)
    {
        public string ToLogLine()
        {
            string run = string.IsNullOrWhiteSpace(RunId) ? "n/a" : RunId;
            string detector = string.IsNullOrWhiteSpace(DetectorName) ? "n/a" : DetectorName;
            return
                $"[AutoRunSummary] runId={run}, detector={detector}, mode={Mode}, totalFrames={TotalFrames}, startFrame={StartFrameIndex}, processed={ProcessedFrames}, decoded={DecodedFrames}, detects={DetectedFrames}, interpolated={InterpolatedFrames}, readMs={ReadMs}, decodeMs={DecodeMs}, detectMs={DetectMs}, maskMs={MaskMs}, totalMs={TotalMs}, downscale={DownscaleRatio:F3}, quality={DownscaleQuality}, tracking={UseTracking}, everyN={DetectEveryNFrames}, parallel={ParallelDetectorCount}, roi={RoiSummary ?? "n/a"}";
        }
    }
}
