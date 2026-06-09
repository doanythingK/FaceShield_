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
        double SourceFps,
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
        public int FinalSceneCutPreGuardPairCount { get; init; } = 0;
        public int FinalSceneCutPreStrongProbePairCount { get; init; } = 0;
        public int FinalSceneCutPostGuardPairCount { get; init; } = 0;
        public int FinalSceneCutPostStrongProbePairCount { get; init; } = 0;
        public int FinalSceneCutCarryPairCount { get; init; } = 0;
        public int FinalSceneCutCarryRemovedCount { get; init; } = 0;
        public int FinalSceneCutProtectedFrameCount { get; init; } = 0;
        public int FinalProtectedSceneCarryFrameCount { get; init; } = 0;
        public bool FinalMaskReviewRequired { get; init; } = false;
        public string FinalMaskReviewReasons { get; init; } = "none";
        public int SampleWindowFrames { get; init; } = 0;
        public int SampleFrameCount { get; init; } = 0;
        public int SampleRowCount { get; init; } = 0;
        public int SampleShortGapCount { get; init; } = 0;
        public int SamplePerFaceShortGapCount { get; init; } = 0;
        public int SampleIsolatedFrameCount { get; init; } = 0;
        public int SampleLargeJumpGapCount { get; init; } = 0;
        public bool SampleReviewRequired { get; init; } = false;
        public string SampleReviewReasons { get; init; } = "none";
        public int SampleProtectedSceneCarryFrameCount { get; init; } = 0;

        public string ToLogLine()
        {
            string run = string.IsNullOrWhiteSpace(RunId) ? "n/a" : RunId;
            string detector = string.IsNullOrWhiteSpace(DetectorName) ? "n/a" : DetectorName;
            return
                $"[AutoRunSummary] runId={run}, detector={detector}, mode={Mode}, totalFrames={TotalFrames}, startFrame={StartFrameIndex}, processed={ProcessedFrames}, decoded={DecodedFrames}, detects={DetectedFrames}, interpolated={InterpolatedFrames}, readMs={ReadMs}, decodeMs={DecodeMs}, detectMs={DetectMs}, maskMs={MaskMs}, totalMs={TotalMs}, srcFps={SourceFps:0.###}, downscale={DownscaleRatio:F3}, quality={DownscaleQuality}, tracking={UseTracking}, everyN={DetectEveryNFrames}, parallel={ParallelDetectorCount}, roi={RoiSummary ?? "n/a"}, post={EnablePostProcessing}, roiPost={EnableRoiPostProcess}, weakIso={EnableYoloWeakIsolatedCleanup}, gapFill={EnableYoloGapFill}, sceneCut={EnableYoloSceneCutCarryCleanup}, smooth={EnableYoloTemporalSmoothing}, finalFrames={FinalMaskFrames}, finalRows={FinalMaskRows}, finalShortGaps={FinalMaskShortGapCount}, finalPerFaceShortGaps={FinalMaskPerFaceShortGapCount}, finalLargeJumps={FinalMaskLargeJumpGapCount}, sceneCutStats=preGuard:{FinalSceneCutPreGuardPairCount},preStrong:{FinalSceneCutPreStrongProbePairCount},postGuard:{FinalSceneCutPostGuardPairCount},postStrong:{FinalSceneCutPostStrongProbePairCount},carryPairs:{FinalSceneCutCarryPairCount},carryRemoved:{FinalSceneCutCarryRemovedCount},carryProtected:{FinalSceneCutProtectedFrameCount}, finalProtectedSceneCarry={FinalProtectedSceneCarryFrameCount}, finalReview={FinalMaskReviewRequired}, finalReviewReasons={FinalMaskReviewReasons}, sampleWindow={SampleWindowFrames}, sampleFrames={SampleFrameCount}, sampleRows={SampleRowCount}, sampleShortGaps={SampleShortGapCount}, samplePerFaceShortGaps={SamplePerFaceShortGapCount}, sampleIsolated={SampleIsolatedFrameCount}, sampleLargeJumps={SampleLargeJumpGapCount}, sampleReview={SampleReviewRequired}, sampleReviewReasons={SampleReviewReasons}, sampleProtectedSceneCarry={SampleProtectedSceneCarryFrameCount}";
        }
    }
}
