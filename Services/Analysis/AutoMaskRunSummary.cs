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
        public bool EnablePostProcessing { get; init; } = false;
        public bool EnableRoiPostProcess { get; init; } = false;
        public bool EnableYoloWeakIsolatedCleanup { get; init; } = false;
        public bool EnableYoloGapFill { get; init; } = false;
        public bool EnableYoloSceneCutCarryCleanup { get; init; } = false;
        public bool EnableYoloTemporalSmoothing { get; init; } = false;
        public int FinalMaskFrames { get; init; } = 0;
        public int FinalMaskRows { get; init; } = 0;
        public int FinalMaskShortGapCount { get; init; } = 0;
        public int FinalMaskPerFaceShortGapCount { get; init; } = 0;
        public int FinalMaskLargeJumpGapCount { get; init; } = 0;
        public int FinalProtectedSceneCarryFrameCount { get; init; } = 0;
        public bool FinalMaskReviewRequired { get; init; } = false;
        public string FinalMaskReviewReasons { get; init; } = "none";

        public string ToLogLine()
        {
            string run = string.IsNullOrWhiteSpace(RunId) ? "n/a" : RunId;
            string detector = string.IsNullOrWhiteSpace(DetectorName) ? "n/a" : DetectorName;
            return
                $"[AutoRunSummary] runId={run}, detector={detector}, mode={Mode}, totalFrames={TotalFrames}, startFrame={StartFrameIndex}, processed={ProcessedFrames}, decoded={DecodedFrames}, detects={DetectedFrames}, interpolated={InterpolatedFrames}, readMs={ReadMs}, decodeMs={DecodeMs}, detectMs={DetectMs}, maskMs={MaskMs}, totalMs={TotalMs}, downscale={DownscaleRatio:F3}, quality={DownscaleQuality}, tracking={UseTracking}, everyN={DetectEveryNFrames}, parallel={ParallelDetectorCount}, roi={RoiSummary ?? "n/a"}, post={EnablePostProcessing}, roiPost={EnableRoiPostProcess}, weakIso={EnableYoloWeakIsolatedCleanup}, gapFill={EnableYoloGapFill}, sceneCut={EnableYoloSceneCutCarryCleanup}, smooth={EnableYoloTemporalSmoothing}, finalFrames={FinalMaskFrames}, finalRows={FinalMaskRows}, finalShortGaps={FinalMaskShortGapCount}, finalPerFaceShortGaps={FinalMaskPerFaceShortGapCount}, finalLargeJumps={FinalMaskLargeJumpGapCount}, finalProtectedSceneCarry={FinalProtectedSceneCarryFrameCount}, finalReview={FinalMaskReviewRequired}, finalReviewReasons={FinalMaskReviewReasons}";
        }
    }
}
