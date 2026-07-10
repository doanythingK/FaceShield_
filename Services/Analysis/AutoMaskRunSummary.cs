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
        long PostProcessMs,
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
        public AutoMaskProcessingMode ProcessingMode { get; init; } = AutoMaskProcessingMode.Legacy;
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
        public int FinalOffModeSceneCutResetPairCount { get; init; } = 0;
        public int FinalOffModeSceneCutResetRemovedFrameCount { get; init; } = 0;
        public int FinalOffModeSceneCutResetBeforeWindowFrameCount { get; init; } = 0;
        public int FinalOffModeSceneCutResetAfterWindowFrameCount { get; init; } = 0;
        public int FinalOffModeSceneCutResetRemovedBeforeFrameCount { get; init; } = 0;
        public int FinalOffModeSceneCutResetRemovedAfterFrameCount { get; init; } = 0;
        public int FinalSceneCutPreGuardWindowCount { get; init; } = 0;
        public int FinalSceneCutPreStrongCarryWindowCount { get; init; } = 0;
        public int FinalSceneCutPostGuardWindowCount { get; init; } = 0;
        public int FinalSceneCutPostStrongCarryWindowCount { get; init; } = 0;
        public int FinalSceneCutCarryWindowCount { get; init; } = 0;
        public int FinalSceneCutPostGapFillWindowCount { get; init; } = 0;
        public int FinalSceneCutPostGapFillCarryPairCount { get; init; } = 0;
        public int FinalSceneCutPostGapFillCarryRemovedCount { get; init; } = 0;
        public int FinalSceneCutPostGapFillProtectedFrameCount { get; init; } = 0;
        public string FinalSceneCutPairSourceBreakdown { get; init; } = "preCutOnly=0,preStrongOnly=0,postCutOnly=0,postStrongOnly=0,shared=0,pairOrphans=0";
        public string FinalSceneCutPostGapFillPairSourceBreakdown { get; init; } = "preCutOnly=0,preStrongOnly=0,postCutOnly=0,postStrongOnly=0,shared=0,pairOrphans=0";
        public int FinalProtectedSceneCarryFrameCount { get; init; } = 0;
        public bool FinalMaskReviewRequired { get; init; } = false;
        public string FinalMaskReviewReasons { get; init; } = "none";
        public int FinalMissRecoveryFillCount { get; init; } = 0;
        public int FinalFalsePositiveSuppressedCount { get; init; } = 0;
        public int FinalOffModeWeakCleanupCount { get; init; } = 0;
        public int FinalGapFillRecoveredCount { get; init; } = 0;
        public int FinalGapFillBlockedCutGapFrames { get; init; } = 0;
        public int FinalGapFillBlockedCutGapFramesBeforeCut { get; init; } = 0;
        public int FinalGapFillBlockedCutGapFramesAfterCut { get; init; } = 0;
        public int FinalGapFillBlockedCleanupGapFrames { get; init; } = 0;
        public int FinalGapFillBlockedSceneCarryGapFrames { get; init; } = 0;
        public int FinalGapFillSuppressedWeakGeometryAnchorChecks { get; init; } = 0;
        public int FinalGapFillSuppressedRiskyGeometryAnchorChecks { get; init; } = 0;
        public int FinalGapFillUnsupportedWeakAnchorChecks { get; init; } = 0;
        public int SampleWindowFrames { get; init; } = 0;
        public int SampleWindowStartFrame { get; init; } = -1;
        public int SampleWindowEndFrame { get; init; } = -1;
        public double SampleWindowStartSeconds { get; init; } = 0.0;
        public double SampleWindowEndSeconds { get; init; } = 0.0;
        public double SampleWindowDurationSeconds { get; init; } = 0.0;
        public string SampleWindowTimingSource { get; init; } = "fps-fallback";
        public int SampleFrameCount { get; init; } = 0;
        public int SampleRowCount { get; init; } = 0;
        public int SampleShortGapCount { get; init; } = 0;
        public int SamplePerFaceShortGapCount { get; init; } = 0;
        public int SampleIsolatedFrameCount { get; init; } = 0;
        public int SampleLargeJumpGapCount { get; init; } = 0;
        public bool SampleReviewRequired { get; init; } = false;
        public string SampleReviewReasons { get; init; } = "none";
        public int SampleMissRecoveryFillCount { get; init; } = 0;
        public int SampleFalsePositiveSuppressionCount { get; init; } = 0;
        public int SampleFalsePositiveSuppressedCount => SampleFalsePositiveSuppressionCount;
        public int SampleOffModeWeakCleanupSuppressionCount { get; init; } = 0;
        public int SampleGapFillBlockedCutGapFrames { get; init; } = 0;
        public int SampleGapFillBlockedCutGapFramesBeforeCut { get; init; } = 0;
        public int SampleGapFillBlockedCutGapFramesAfterCut { get; init; } = 0;
        public int SampleGapFillBlockedCleanupGapFrames { get; init; } = 0;
        public int SampleGapFillBlockedSceneCarryGapFrames { get; init; } = 0;
        public int SampleProtectedSceneCarryFrameCount { get; init; } = 0;
        public int SampleWindowIssueFrameCount { get; init; } = 0;
        public int SampleWindowIssueCandidateCount { get; init; } = 0;
        public string SampleWindowStartReason { get; init; } = "none";
        public bool YoloRiskCascadeEnabled { get; init; } = false;
        public int YoloRiskFrameCount { get; init; } = 0;
        public int YoloPeriodicGlobalFrameCount { get; init; } = 0;
        public int YoloSecondaryAttemptCount { get; init; } = 0;
        public int YoloProtectedStoredMaskFrameCount { get; init; } = 0;
        public int YoloSecondaryHitFrameCount { get; init; } = 0;
        public int YoloSecondaryCandidateFaceCount { get; init; } = 0;
        public int YoloSecondaryAcceptedFrameCount { get; init; } = 0;
        public int YoloSecondaryAcceptedFaceCount { get; init; } = 0;
        public int YoloSecondaryRejectedFaceCount { get; init; } = 0;
        public long YoloCascadeDecodeMs { get; init; } = 0;
        public long YoloCascadeDetectMs { get; init; } = 0;
        public long YoloCascadeTotalMs { get; init; } = 0;
        public string YoloCascadeReasonBreakdown { get; init; } = "none";
        public string YoloCascadeError { get; init; } = "none";
        public long AnalysisTotalMs => TotalMs + PostProcessMs + YoloCascadeTotalMs;

        public string ToLogLine()
        {
            string run = string.IsNullOrWhiteSpace(RunId) ? "n/a" : RunId;
            string detector = string.IsNullOrWhiteSpace(DetectorName) ? "n/a" : DetectorName;
            double sceneCutRemovalRate = FinalSceneCutCarryPairCount > 0
                ? FinalSceneCutCarryRemovedCount / (double)FinalSceneCutCarryPairCount
                : 0.0;
            double sceneCutProtectedRate = FinalSceneCutCarryPairCount > 0
                ? FinalSceneCutProtectedFrameCount / (double)FinalSceneCutCarryPairCount
                : 0.0;
            double sceneCutPostGapFillRemovalRate = FinalSceneCutPostGapFillCarryPairCount > 0
                ? FinalSceneCutPostGapFillCarryRemovedCount / (double)FinalSceneCutPostGapFillCarryPairCount
                : 0.0;
            double sceneCutPostGapFillProtectedRate = FinalSceneCutPostGapFillCarryPairCount > 0
                ? FinalSceneCutPostGapFillProtectedFrameCount / (double)FinalSceneCutPostGapFillCarryPairCount
                : 0.0;
            double offModeSceneCutResetRate = FinalOffModeSceneCutResetPairCount > 0
                ? FinalOffModeSceneCutResetRemovedFrameCount / (double)FinalOffModeSceneCutResetPairCount
                : 0.0;
            double offModeSceneCutResetBeforeRate = FinalOffModeSceneCutResetBeforeWindowFrameCount > 0
                ? FinalOffModeSceneCutResetRemovedBeforeFrameCount / (double)FinalOffModeSceneCutResetBeforeWindowFrameCount
                : 0.0;
            double offModeSceneCutResetAfterRate = FinalOffModeSceneCutResetAfterWindowFrameCount > 0
                ? FinalOffModeSceneCutResetRemovedAfterFrameCount / (double)FinalOffModeSceneCutResetAfterWindowFrameCount
                : 0.0;
            double sampleMissRecoveryRate = SampleWindowFrames > 0
                ? SampleMissRecoveryFillCount / (double)SampleWindowFrames
                : 0.0;
            double sampleFpSuppressedRate = SampleWindowFrames > 0
                ? SampleFalsePositiveSuppressionCount / (double)SampleWindowFrames
                : 0.0;

            return
                $"[AutoRunSummary] runId={run}, detector={detector}, mode={Mode}, totalFrames={TotalFrames}, startFrame={StartFrameIndex}, processed={ProcessedFrames}, decoded={DecodedFrames}, detects={DetectedFrames}, interpolated={InterpolatedFrames}, readMs={ReadMs}, decodeMs={DecodeMs}, detectMs={DetectMs}, maskMs={MaskMs}, postProcessMs={PostProcessMs}, totalMs={TotalMs}, analysisTotalMs={AnalysisTotalMs}, srcFps={SourceFps:0.###}, downscale={DownscaleRatio:F3}, quality={DownscaleQuality}, tracking={UseTracking}, everyN={DetectEveryNFrames}, parallel={ParallelDetectorCount}, roi={RoiSummary ?? "n/a"}, post={EnablePostProcessing}, roiPost={EnableRoiPostProcess}, weakIso={EnableYoloWeakIsolatedCleanup}, gapFill={EnableYoloGapFill}, sceneCut={EnableYoloSceneCutCarryCleanup}, smooth={EnableYoloTemporalSmoothing}, processingMode={ProcessingMode}, finalFrames={FinalMaskFrames}, finalRows={FinalMaskRows}, finalShortGaps={FinalMaskShortGapCount}, finalPerFaceShortGaps={FinalMaskPerFaceShortGapCount}, finalLargeJumps={FinalMaskLargeJumpGapCount}, sceneCutStats=preGuard:{FinalSceneCutPreGuardPairCount},preStrong:{FinalSceneCutPreStrongProbePairCount},postGuard:{FinalSceneCutPostGuardPairCount},postStrong:{FinalSceneCutPostStrongProbePairCount},carryPairs:{FinalSceneCutCarryPairCount},carryRemoved:{FinalSceneCutCarryRemovedCount},carryProtected:{FinalSceneCutProtectedFrameCount},carryWindows=pre:{FinalSceneCutPreGuardWindowCount},preStrong:{FinalSceneCutPreStrongCarryWindowCount},post:{FinalSceneCutPostGuardWindowCount},postStrong:{FinalSceneCutPostStrongCarryWindowCount},final:{FinalSceneCutCarryWindowCount},postGapFill:{FinalSceneCutPostGapFillWindowCount},offModeResetPairs:{FinalOffModeSceneCutResetPairCount},offModeResetRemoved:{FinalOffModeSceneCutResetRemovedFrameCount},offModeResetWindows={FinalOffModeSceneCutResetBeforeWindowFrameCount}/{FinalOffModeSceneCutResetAfterWindowFrameCount},offModeResetRemovedWindows={FinalOffModeSceneCutResetRemovedBeforeFrameCount}/{FinalOffModeSceneCutResetRemovedAfterFrameCount},finalProtectedSceneCarry={FinalProtectedSceneCarryFrameCount}, sceneCutStats2=pairSourceBreakdown:{FinalSceneCutPairSourceBreakdown},postGapFillPairSourceBreakdown:{FinalSceneCutPostGapFillPairSourceBreakdown}, finalReview={FinalMaskReviewRequired}, finalReviewReasons={FinalMaskReviewReasons}, finalMissRecovery={FinalMissRecoveryFillCount}, finalFpSuppressed={FinalFalsePositiveSuppressedCount}, finalOffModeWeakCleanupSuppressed={FinalOffModeWeakCleanupCount}, finalGapFillRecovered={FinalGapFillRecoveredCount}, finalGapFillBlocked={FinalGapFillBlockedCutGapFrames}/{FinalGapFillBlockedCutGapFramesBeforeCut}/{FinalGapFillBlockedCutGapFramesAfterCut}/{FinalGapFillBlockedCleanupGapFrames}/{FinalGapFillBlockedSceneCarryGapFrames}, gapFillAnchorChecks={FinalGapFillSuppressedWeakGeometryAnchorChecks}/{FinalGapFillSuppressedRiskyGeometryAnchorChecks}/{FinalGapFillUnsupportedWeakAnchorChecks}, sceneCutRemovalRate={sceneCutRemovalRate:0.0000}, sceneCutProtectedRate={sceneCutProtectedRate:0.0000}, sceneCutPostGapFillRemovalRate={sceneCutPostGapFillRemovalRate:0.0000}, sceneCutPostGapFillProtectedRate={sceneCutPostGapFillProtectedRate:0.0000}, offModeSceneCutResetRate={offModeSceneCutResetRate:0.0000}, offModeSceneCutResetBeforeRate={offModeSceneCutResetBeforeRate:0.0000}, offModeSceneCutResetAfterRate={offModeSceneCutResetAfterRate:0.0000}, sampleWindow={SampleWindowFrames}, sampleFrames={SampleFrameCount}, sampleRows={SampleRowCount}, sampleShortGaps={SampleShortGapCount}, samplePerFaceShortGaps={SamplePerFaceShortGapCount}, sampleIsolated={SampleIsolatedFrameCount}, sampleLargeJumps={SampleLargeJumpGapCount}, sampleReview={SampleReviewRequired}, sampleReviewReasons={SampleReviewReasons}, sampleWindowIssueFrameCount={SampleWindowIssueFrameCount}, sampleWindowIssueCandidateCount={SampleWindowIssueCandidateCount}, sampleWindowStartReason={SampleWindowStartReason}, sampleMissRecovery={SampleMissRecoveryFillCount}, sampleMissRecoveryRate={sampleMissRecoveryRate:0.0000}, sampleFpSuppressed={SampleFalsePositiveSuppressedCount}, sampleFpSuppressedRate={sampleFpSuppressedRate:0.0000}, sampleOffModeWeakCleanupSuppression={SampleOffModeWeakCleanupSuppressionCount}, sampleProtectedSceneCarry={SampleProtectedSceneCarryFrameCount}, sampleGapFillBlocked={SampleGapFillBlockedCutGapFrames}/{SampleGapFillBlockedCutGapFramesBeforeCut}/{SampleGapFillBlockedCutGapFramesAfterCut}/{SampleGapFillBlockedCleanupGapFrames}/{SampleGapFillBlockedSceneCarryGapFrames}";
        }

        public string ToSampleTimingLogLine()
        {
            string run = string.IsNullOrWhiteSpace(RunId) ? "n/a" : RunId;
            return $"[AutoRunSampleTiming] runId={run}, processingMode={ProcessingMode}, timing={SampleWindowTimingSource}, startFrame={SampleWindowStartFrame}, endFrame={SampleWindowEndFrame}, frames={SampleWindowFrames}, startSec={SampleWindowStartSeconds:0.###}, endSec={SampleWindowEndSeconds:0.###}, durationSec={SampleWindowDurationSeconds:0.###}";
        }

        public string ToYoloCascadeLogLine()
        {
            string run = string.IsNullOrWhiteSpace(RunId) ? "n/a" : RunId;
            return $"[AutoRunYoloCascade] runId={run}, enabled={YoloRiskCascadeEnabled.ToString().ToLowerInvariant()}, riskFrames={YoloRiskFrameCount}, periodicFrames={YoloPeriodicGlobalFrameCount}, attempts={YoloSecondaryAttemptCount}, protectedStoredMaskFrames={YoloProtectedStoredMaskFrameCount}, hitFrames={YoloSecondaryHitFrameCount}, candidates={YoloSecondaryCandidateFaceCount}, acceptedFrames={YoloSecondaryAcceptedFrameCount}, acceptedFaces={YoloSecondaryAcceptedFaceCount}, rejectedFaces={YoloSecondaryRejectedFaceCount}, decodeMs={YoloCascadeDecodeMs}, detectMs={YoloCascadeDetectMs}, totalMs={YoloCascadeTotalMs}, analysisTotalMs={AnalysisTotalMs}, reasons={YoloCascadeReasonBreakdown}, error={YoloCascadeError}";
        }
    }
}
