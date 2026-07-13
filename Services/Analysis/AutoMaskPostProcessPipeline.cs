using Avalonia;
using FaceShield.Services.FaceDetection;
using FaceShield.Services.Video;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace FaceShield.Services.Analysis
{
    public sealed class AutoMaskPostProcessPipeline
    {
        private const float YoloFinalMaskLowConfidenceThreshold = 0.38f;
        private const int FinalMaskShortGapMaxFrames = 8;
        private const double FinalMaskLargeJumpAreaChangeRatio = 4.0;
        private const double FinalMaskLargeJumpCenterShift = 0.20;
        private const float YoloFinalMaskWeakIsolatedConfidenceMax = 0.50f;
        private const double YoloSceneCutDifferenceThreshold = 0.15;
        private const double YoloSceneCutDirectDifferenceThreshold = 0.20;
        private const double YoloSceneCutCandidateMatchMinIou = 0.55;
        private const double YoloSceneCutCandidateMatchMaxCenterShiftRatio = 0.80;
        private const double YoloSceneCutCandidateMatchMaxAreaChangeRatio = 3.5;
        private const int YoloSceneCutPostCutLookbackFrames = 5;
        private const int YoloSceneCutCarryPurgeFrames = 15;
        private const int YoloSceneCutCarryBlockFrames = 15;
        private const float YoloSceneCutExtendedWeakCarryMaxConfidence = 0.78f;
        private const float YoloSceneCutCarryPurgeMaxConfidence = 0.98f;
        private const int YoloFinalMaskStableGapMaxFrames = 8;
        private const double YoloFinalMaskEdgeMarginRatio = 0.02;
        private const int YoloSceneCutRebuildWindowFrames = 5;
        private const double YoloFinalMaskTinyWeakAreaRatio = 0.0012;
        private const float YoloFinalMaskTinyShortConfidenceMax = 0.62f;
        private const double YoloFinalMaskTinyShortAreaRatio = 0.0009;
        private const float YoloFinalMaskUpperWeakConfidenceMax = 0.60f;
        private const double YoloFinalMaskUpperWeakCenterYRatio = 0.10;
        private const double YoloFinalMaskUpperWeakAreaRatio = 0.0065;
        private const float YoloFinalMaskTopEdgeLargeConfidenceMax = 0.88f;
        private const double YoloFinalMaskTopEdgeLargeCenterYRatio = 0.38;
        private const double YoloFinalMaskTopEdgeLargeMinAreaRatio = 0.035;
        private const double YoloFinalMaskTopEdgeLargeMaxAreaRatio = 0.12;
        private const float YoloFinalMaskLowerWeakConfidenceMax = 0.50f;
        private const double YoloFinalMaskLowerWeakCenterYRatio = 0.58;
        private const double YoloFinalMaskLowerWeakMinAreaRatio = 0.015;
        private const double YoloFinalMaskLowerWeakMaxAreaRatio = 0.045;
        private const double YoloFinalMaskMinAspectRatio = 0.35;
        private const double YoloFinalMaskMaxAspectRatio = 1.65;
        private const double YoloQualitySampleWindowSeconds = 30.0;
        private const int OffModeGapFillWindowFrames = 4;
        private const int OffModeGapFillMaxGapFrames = 4;
        private const float OffModeGapFillMinAnchorConfidence = 0.62f;
        private const float OffModeGapFillSupportedAnchorMinConfidence = 0.58f;

        private readonly FrameMaskProvider _maskProvider;
        private readonly AutoMaskOptions _options;
        private readonly int _totalFrames;
        private readonly double _sourceFps;
        private readonly IReadOnlyDictionary<int, FrameTimingSample> _frameTimings;
        private readonly IReadOnlySet<int> _sceneCutStarts;
        private readonly int _mutableStartFrameIndex;

        public AutoMaskPostProcessPipeline(
            FrameMaskProvider maskProvider,
            AutoMaskOptions options,
            int totalFrames,
            double sourceFps,
            IReadOnlyDictionary<int, FrameTimingSample>? frameTimings = null,
            IReadOnlySet<int>? sceneCutStarts = null,
            int mutableStartFrameIndex = 0)
        {
            _maskProvider = maskProvider ?? throw new ArgumentNullException(nameof(maskProvider));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _totalFrames = totalFrames;
            _sourceFps = sourceFps;
            _frameTimings = frameTimings ?? new Dictionary<int, FrameTimingSample>();
            _sceneCutStarts = sceneCutStarts ?? new HashSet<int>();
            _mutableStartFrameIndex = Math.Clamp(mutableStartFrameIndex, 0, Math.Max(0, totalFrames));
        }

        public AutoMaskPostProcessResult Apply(
            string videoPath,
            CancellationToken cancellationToken,
            IBgraFaceDetector? roiSourceDetector = null,
            FaceOnnxDetectorOptions? detectorOptions = null,
            bool useFaceOnnxRoiDetector = false)
        {
            var swTotal = Stopwatch.StartNew();
            var runId = string.IsNullOrWhiteSpace(_options.RunId) ? "none" : _options.RunId;
            bool enablePostProcessing = _options.EnablePostProcessing;
            bool enableRoiPostProcess = enablePostProcessing && _options.EnableRoiPostProcess;
            bool enableTemporalSmoothing = enablePostProcessing && _options.EnableYoloTemporalSmoothing;
            bool enableWeakIsolationCleanup = enablePostProcessing && _options.EnableYoloWeakIsolatedCleanup;
            bool enableGapFill = enablePostProcessing && _options.EnableYoloGapFill;
            bool enableSceneCutCleanup = _options.UseTracking
                && enablePostProcessing
                && _options.EnableYoloSceneCutCarryCleanup;

            bool runYoloMissRecovery = _options.FilterProfile == FaceFilterProfile.Yolo &&
                _options.UseTracking &&
                _options.ProcessingMode == AutoMaskProcessingMode.Legacy &&
                !_options.EnablePostProcessing;
            bool enableYoloOffModeGapFill = _options.EnableYoloGapFill &&
                _options.UseTracking &&
                !_options.EnablePostProcessing &&
                _options.FilterProfile == FaceFilterProfile.Yolo;
            bool enableYoloOffModeSceneCutCarryGuard = _options.EnableYoloSceneCutCarryCleanup &&
                _options.FilterProfile == FaceFilterProfile.Yolo &&
                !_options.EnablePostProcessing &&
                _options.UseTracking;
            bool enableYoloOffModeWeakIsolationCleanup = _options.EnableYoloWeakIsolatedCleanup &&
                _options.FilterProfile == FaceFilterProfile.Yolo &&
                !_options.EnablePostProcessing &&
                _options.UseTracking;
            bool runTrackedContinuity = _options.UseTracking &&
                _options.ProcessingMode == AutoMaskProcessingMode.Tracked &&
                !_options.EnablePostProcessing &&
                _options.DetectEveryNFrames <= 1;
            bool runYoloOffModeGapFill = enableYoloOffModeGapFill &&
                _options.DetectEveryNFrames > 1;
            bool runYoloPostProcess = _options.FilterProfile == FaceFilterProfile.Yolo &&
                _options.UseTracking &&
                _options.EnablePostProcessing;
            bool hasAnyYoloPostModule = enableRoiPostProcess ||
                enableWeakIsolationCleanup ||
                enableGapFill ||
                enableSceneCutCleanup ||
                enableTemporalSmoothing;
            bool runYoloTrackPost = runYoloMissRecovery ||
                (_options.FilterProfile == FaceFilterProfile.Yolo && runTrackedContinuity) ||
                (runYoloPostProcess &&
                 (enableSceneCutCleanup ||
                  enableTemporalSmoothing ||
                  enableRoiPostProcess ||
                  enableWeakIsolationCleanup ||
                  enableGapFill));
            bool runTrackPost = runTrackedContinuity || (_options.FilterProfile == FaceFilterProfile.Yolo
                ? runYoloTrackPost
                : _options.UseTracking && enablePostProcessing);
            Debug.WriteLine(
                $"[AutoMaskPostProcess] start runId={runId} profile={_options.FilterProfile} processingMode={_options.ProcessingMode} totalFrames={_totalFrames} tracking={_options.UseTracking} everyN={_options.DetectEveryNFrames} post={enablePostProcessing} roi={enableRoiPostProcess} weakIso={enableWeakIsolationCleanup} gapFill={enableGapFill} scene={enableSceneCutCleanup} smooth={enableTemporalSmoothing} trackedContinuity={runTrackedContinuity} offModeGapFill={enableYoloOffModeGapFill} offModeWeakIso={enableYoloOffModeWeakIsolationCleanup} offModeGapFillWindow={OffModeGapFillWindowFrames} offModeGapFillMaxGap={OffModeGapFillMaxGapFrames} offModeGapFillMinAnchor={OffModeGapFillMinAnchorConfidence:0.###} offModeGapFillSupportedAnchorMin={OffModeGapFillSupportedAnchorMinConfidence:0.###} runTrackPost={runTrackPost} runMissRecovery={runYoloMissRecovery}");
            if (_options.FilterProfile == FaceFilterProfile.Yolo && enablePostProcessing && !hasAnyYoloPostModule)
            {
                Debug.WriteLine(
                    $"[AutoMaskPostProcess] 경고: post=true이지만 YOLO 모듈이 비활성 상태로 OFF baseline와 동일 동작 예정 (runTrackPost={runYoloTrackPost})");
            }
            if (_options.FilterProfile == FaceFilterProfile.Yolo && (runYoloOffModeGapFill || enableYoloOffModeWeakIsolationCleanup))
            {
                string offModeRunMode = runYoloOffModeGapFill && enableYoloOffModeWeakIsolationCleanup
                    ? "gap-fill+weak-cleanup"
                    : runYoloOffModeGapFill
                        ? "gap-fill"
                        : "weak-cleanup";
                Debug.WriteLine(
                    $"[AutoMaskPostProcess] off-mode YOLO 보정 활성화 run={offModeRunMode} detectEveryN={_options.DetectEveryNFrames} window={OffModeGapFillWindowFrames} sceneCutGuard={enableYoloOffModeSceneCutCarryGuard}");
            }
            else if (_options.FilterProfile == FaceFilterProfile.Yolo &&
                !_options.EnablePostProcessing &&
                _options.UseTracking &&
                !runTrackedContinuity)
            {
                if (!_options.EnableYoloGapFill)
                {
                    Debug.WriteLine(
                        $"[AutoMaskPostProcess] off-mode gap-fill 비활성화 설정=미사용 (EnableYoloGapFill=false)");
                }
                if (!enableYoloOffModeWeakIsolationCleanup)
                {
                    Debug.WriteLine(
                        $"[AutoMaskPostProcess] off-mode weak-isolation cleanup 비활성화 설정=미사용 (EnableYoloWeakIsolatedCleanup=false)");
                }
                else if (_options.DetectEveryNFrames <= 1)
                {
                    Debug.WriteLine(
                        $"[AutoMaskPostProcess] off-mode gap-fill 비활성화 간격조건 미충족 detectEveryN={_options.DetectEveryNFrames}");
                }
            }

            var temporalPostProcessor = new AutoMaskTemporalPostProcessor();
            bool useTrackingForTemporalFixes = runTrackPost;
            var swTrack = Stopwatch.StartNew();
            var trackPost = runTrackPost
                ? temporalPostProcessor.ApplyTemporalFixes(
                    _maskProvider,
                    _totalFrames,
                    _options.FilterProfile,
                    useTrackingForTemporalFixes,
                    missRecoveryOnly: runYoloMissRecovery,
                    continuityOnly: runTrackedContinuity,
                    blockedSceneCutStarts: _sceneCutStarts,
                    mutableStartFrameIndex: _mutableStartFrameIndex)
                : FaceTrackPostProcessResult.Empty;
            swTrack.Stop();
            Debug.WriteLine(
                $"[AutoMaskPostProcessTiming] runId={runId} phase=track-post run={runTrackPost} continuity={runTrackedContinuity} elapsedMs={swTrack.ElapsedMilliseconds} trackCount={trackPost.TrackCount} fillGap={trackPost.FilledGapFaces} fillLost={trackPost.FilledLostFaces} fillInitial={trackPost.FilledInitialFaces}");
            int finalMissRecoveryFillCount = trackPost.FilledGapFaces + trackPost.FilledLostFaces + trackPost.FilledInitialFaces;
            var finalMissRecoveryFillFrameIndices = new HashSet<int>();
            AddFrameIndices(finalMissRecoveryFillFrameIndices, trackPost.FilledGapFacesInfo);
            AddFrameIndices(finalMissRecoveryFillFrameIndices, trackPost.FilledLostFrameIndices);
            AddFrameIndices(finalMissRecoveryFillFrameIndices, trackPost.FilledInitialFacesInfo);
            var finalFalsePositiveSuppressedFrameIndices = new HashSet<int>();
            var finalOffModeWeakCleanupFrameIndices = new HashSet<int>();
            int finalFalsePositiveSuppressedCount = 0;
            int finalOffModeWeakCleanupCount = 0;
            var finalGapFillRecoveredFrameIndices = new HashSet<int>();
            int finalGapFillRecoveredCount = 0;
            int finalGapFillBlockedCutGapFrames = 0;
            int finalGapFillBlockedCutGapFramesBeforeCut = 0;
            int finalGapFillBlockedCutGapFramesAfterCut = 0;
            int finalGapFillBlockedCleanupGapFrames = 0;
            int finalGapFillBlockedSceneCarryGapFrames = 0;
            var finalGapFillBlockedCutGapFrameIndices = new HashSet<int>();
            var finalGapFillBlockedCutGapBeforeCutFrameIndices = new HashSet<int>();
            var finalGapFillBlockedCutGapAfterCutFrameIndices = new HashSet<int>();
            var finalGapFillBlockedCleanupGapFrameIndices = new HashSet<int>();
            var finalGapFillBlockedSceneCarryFrameIndices = new HashSet<int>();
            int finalGapFillSuppressedWeakGeometryAnchorChecks = 0;
            int finalGapFillSuppressedRiskyGeometryAnchorChecks = 0;
            int finalGapFillUnsupportedWeakAnchorChecks = 0;
            int finalGapFillCutGuardChecked = 0;
            int finalGapFillCutGuardDirectDifferenceChecks = 0;
            int finalGapFillCutGuardDirectDifferenceSkipped = 0;

            var swRoi = Stopwatch.StartNew();
            bool ranRoiRefine = false;
            if (enableRoiPostProcess && roiSourceDetector != null && detectorOptions != null)
            {
                new AutoMaskRoiRefineStep().Apply(
                    _maskProvider,
                    videoPath,
                    roiSourceDetector,
                    trackPost,
                    detectorOptions,
                    _options,
                    useFaceOnnxRoiDetector);
                ranRoiRefine = true;
            }
            swRoi.Stop();
            Debug.WriteLine(
                $"[AutoMaskPostProcessTiming] runId={runId} phase=roi-refine run={ranRoiRefine} elapsedMs={swRoi.ElapsedMilliseconds} enabled={enableRoiPostProcess}");

            YoloGapFillRunSummary yoloOffModeGapFillSummary = YoloGapFillRunSummary.Empty;
            YoloFinalMaskCleanupPassResult yoloOffModeWeakCleanupSummary = YoloFinalMaskCleanupPassResult.Empty;
            IReadOnlyList<string> yoloOffModeGapFillCutPairs = Array.Empty<string>();
            var swOffGapFill = Stopwatch.StartNew();
            bool ranOffModeGapFill = false;
            bool ranOffModeWeakIsolationCleanup = false;
            if (runYoloOffModeGapFill)
            {
                ranOffModeGapFill = true;
                yoloOffModeGapFillSummary = FillYoloStableFinalMaskGaps(
                    new YoloFinalMaskPostProcessor(),
                    videoPath,
                    cancellationToken,
                    blockedCutFramePairs: Array.Empty<string>(),
                    blockedFrameIndices: Array.Empty<int>(),
                    blockedFaces: Array.Empty<FaceTrackFilledFace>(),
                    skipSceneCutGuard: !enableYoloOffModeSceneCutCarryGuard,
                    sceneCarryBlockedFaces: Array.Empty<FaceTrackFilledFace>(),
                    sceneCarryBlockedFrameIndices: Array.Empty<int>(),
                    gapFillLogLabel: "YoloFinalMaskGapFillOffMode",
                    gapFillSceneCutGuardLogLabel: "YoloFinalMaskGapFillSceneCutGuardOffMode",
                    gapFillMaxGapFrames: OffModeGapFillMaxGapFrames,
                    gapFillMinAnchorConfidence: OffModeGapFillMinAnchorConfidence,
                    gapFillSupportedAnchorMinConfidence: OffModeGapFillSupportedAnchorMinConfidence);
                finalGapFillRecoveredCount += yoloOffModeGapFillSummary.FilledFaces;
                AddFrameIndices(finalGapFillRecoveredFrameIndices, yoloOffModeGapFillSummary.FilledFrameIndices);
                finalGapFillBlockedCutGapFrames += yoloOffModeGapFillSummary.BlockedCutGapFrames;
                finalGapFillBlockedCutGapFramesBeforeCut += yoloOffModeGapFillSummary.BlockedCutGapBeforeCutFrames;
                finalGapFillBlockedCutGapFramesAfterCut += yoloOffModeGapFillSummary.BlockedCutGapAfterCutFrames;
                finalGapFillBlockedCleanupGapFrames += yoloOffModeGapFillSummary.BlockedCleanupGapFrames;
                finalGapFillBlockedSceneCarryGapFrames += yoloOffModeGapFillSummary.BlockedSceneCarryGapFrames;
                AddFrameIndices(finalGapFillBlockedCutGapFrameIndices, yoloOffModeGapFillSummary.BlockedCutGapFrameIndices);
                AddFrameIndices(finalGapFillBlockedCutGapBeforeCutFrameIndices, yoloOffModeGapFillSummary.BlockedCutGapBeforeCutFrameIndices);
                AddFrameIndices(finalGapFillBlockedCutGapAfterCutFrameIndices, yoloOffModeGapFillSummary.BlockedCutGapAfterCutFrameIndices);
                AddFrameIndices(finalGapFillBlockedCleanupGapFrameIndices, yoloOffModeGapFillSummary.BlockedCleanupGapFrameIndices);
                AddFrameIndices(finalGapFillBlockedSceneCarryFrameIndices, yoloOffModeGapFillSummary.BlockedSceneCarryFrameIndices);
                finalGapFillSuppressedWeakGeometryAnchorChecks += yoloOffModeGapFillSummary.SuppressedWeakGeometryAnchorChecks;
                finalGapFillSuppressedRiskyGeometryAnchorChecks += yoloOffModeGapFillSummary.SuppressedRiskyGeometryAnchorChecks;
                finalGapFillUnsupportedWeakAnchorChecks += yoloOffModeGapFillSummary.UnsupportedWeakAnchorChecks;
                finalGapFillCutGuardChecked += yoloOffModeGapFillSummary.CutGuardChecked;
                finalGapFillCutGuardDirectDifferenceChecks += yoloOffModeGapFillSummary.CutGuardDirectDifferenceChecks;
                finalGapFillCutGuardDirectDifferenceSkipped += yoloOffModeGapFillSummary.CutGuardDirectDifferenceSkipped;
            }
            swOffGapFill.Stop();

            var swOffWeakCleanup = Stopwatch.StartNew();
            if (enableYoloOffModeWeakIsolationCleanup)
            {
                ranOffModeWeakIsolationCleanup = true;
                yoloOffModeWeakCleanupSummary = RemoveYoloWeakIsolatedFinalMasks(
                    videoPath,
                    cancellationToken,
                    fillStableGaps: false,
                    skipSceneCutGuard: !enableYoloOffModeSceneCutCarryGuard,
                    logLabel: "YoloFinalMaskOffModeWeakIsolation",
                    logWhenNoRemovals: true);
                finalFalsePositiveSuppressedCount += yoloOffModeWeakCleanupSummary.RemovedFacesInfo.Count;
                AddFrameIndices(finalFalsePositiveSuppressedFrameIndices, yoloOffModeWeakCleanupSummary.RemovedFrameIndices);
                finalOffModeWeakCleanupCount += yoloOffModeWeakCleanupSummary.RemovedFacesInfo.Count;
                AddFrameIndices(finalOffModeWeakCleanupFrameIndices, yoloOffModeWeakCleanupSummary.RemovedFrameIndices);
            }
            swOffWeakCleanup.Stop();
            yoloOffModeGapFillCutPairs = yoloOffModeGapFillSummary.CutFramePairs;
            IReadOnlyList<string> offModeGapFillWindowRanges = BuildCutPairWindowRanges(
                yoloOffModeGapFillCutPairs,
                OffModeGapFillWindowFrames);
            Debug.WriteLine(
                $"[AutoMaskPostProcessTiming] runId={runId} phase=yolo-off-mode-gap-fill run={ranOffModeGapFill} elapsedMs={swOffGapFill.ElapsedMilliseconds} enabled={runYoloOffModeGapFill} filled={yoloOffModeGapFillSummary.FilledFaces} blockedCut={yoloOffModeGapFillSummary.BlockedCutGapFrames} blockedCutBefore={yoloOffModeGapFillSummary.BlockedCutGapBeforeCutFrames} blockedCutAfter={yoloOffModeGapFillSummary.BlockedCutGapAfterCutFrames} blockedCleanup={yoloOffModeGapFillSummary.BlockedCleanupGapFrames} blockedSceneCarry={yoloOffModeGapFillSummary.BlockedSceneCarryGapFrames} suppressedWeak={yoloOffModeGapFillSummary.SuppressedWeakGeometryAnchorChecks} suppressedRisky={yoloOffModeGapFillSummary.SuppressedRiskyGeometryAnchorChecks} unsupported={yoloOffModeGapFillSummary.UnsupportedWeakAnchorChecks} gapPairs={FormatTextList(yoloOffModeGapFillCutPairs)} gapWindows={FormatTextList(offModeGapFillWindowRanges)}");
            Debug.WriteLine(
                $"[AutoMaskPostProcessTiming] runId={runId} phase=yolo-off-mode-weak-cleanup run={ranOffModeWeakIsolationCleanup} elapsedMs={swOffWeakCleanup.ElapsedMilliseconds} removed={yoloOffModeWeakCleanupSummary.RemovedFacesInfo.Count} removedFrames={FormatFrameList(yoloOffModeWeakCleanupSummary.RemovedFrameIndices)}");

            YoloFinalMaskCleanupPassResult yoloCleanupPass = YoloFinalMaskCleanupPassResult.Empty;
            var swInitialCleanup = Stopwatch.StartNew();
            bool ranWeakCleanup = false;
            if (_options.FilterProfile == FaceFilterProfile.Yolo && enableWeakIsolationCleanup)
            {
                ranWeakCleanup = true;
                yoloCleanupPass = RemoveYoloWeakIsolatedFinalMasks(
                    videoPath,
                    cancellationToken,
                    fillStableGaps: enableGapFill,
                    skipSceneCutGuard: !enableSceneCutCleanup,
                    logLabel: "YoloFinalMaskCleanup",
                    logWhenNoRemovals: true,
                    gapFillLogLabel: "YoloFinalMaskGapFill",
                    gapFillSceneCutGuardLogLabel: "YoloFinalMaskGapFillSceneCutGuard");
            }
            swInitialCleanup.Stop();
            Debug.WriteLine(
                $"[AutoMaskPostProcessTiming] runId={runId} phase=yolo-initial-cleanup run={ranWeakCleanup} elapsedMs={swInitialCleanup.ElapsedMilliseconds} fillGap={enableGapFill} removed={yoloCleanupPass.RemovedFacesInfo.Count}");
            finalFalsePositiveSuppressedCount += yoloCleanupPass.RemovedFacesInfo.Count;
            AddFrameIndices(finalFalsePositiveSuppressedFrameIndices, yoloCleanupPass.RemovedFrameIndices);
            finalGapFillRecoveredCount += yoloCleanupPass.GapFillSummary.FilledFaces;
            AddFrameIndices(finalGapFillRecoveredFrameIndices, yoloCleanupPass.GapFillSummary.FilledFrameIndices);
                finalGapFillBlockedCutGapFrames += yoloCleanupPass.GapFillSummary.BlockedCutGapFrames;
                finalGapFillBlockedCutGapFramesBeforeCut += yoloCleanupPass.GapFillSummary.BlockedCutGapBeforeCutFrames;
                finalGapFillBlockedCutGapFramesAfterCut += yoloCleanupPass.GapFillSummary.BlockedCutGapAfterCutFrames;
                finalGapFillBlockedCleanupGapFrames += yoloCleanupPass.GapFillSummary.BlockedCleanupGapFrames;
                finalGapFillBlockedSceneCarryGapFrames += yoloCleanupPass.GapFillSummary.BlockedSceneCarryGapFrames;
                AddFrameIndices(finalGapFillBlockedCutGapFrameIndices, yoloCleanupPass.GapFillSummary.BlockedCutGapFrameIndices);
                AddFrameIndices(finalGapFillBlockedCutGapBeforeCutFrameIndices, yoloCleanupPass.GapFillSummary.BlockedCutGapBeforeCutFrameIndices);
                AddFrameIndices(finalGapFillBlockedCutGapAfterCutFrameIndices, yoloCleanupPass.GapFillSummary.BlockedCutGapAfterCutFrameIndices);
                AddFrameIndices(finalGapFillBlockedCleanupGapFrameIndices, yoloCleanupPass.GapFillSummary.BlockedCleanupGapFrameIndices);
                AddFrameIndices(finalGapFillBlockedSceneCarryFrameIndices, yoloCleanupPass.GapFillSummary.BlockedSceneCarryFrameIndices);
                finalGapFillSuppressedWeakGeometryAnchorChecks += yoloCleanupPass.GapFillSummary.SuppressedWeakGeometryAnchorChecks;
                finalGapFillSuppressedRiskyGeometryAnchorChecks += yoloCleanupPass.GapFillSummary.SuppressedRiskyGeometryAnchorChecks;
                finalGapFillUnsupportedWeakAnchorChecks += yoloCleanupPass.GapFillSummary.UnsupportedWeakAnchorChecks;
                finalGapFillCutGuardChecked += yoloCleanupPass.GapFillSummary.CutGuardChecked;
                finalGapFillCutGuardDirectDifferenceChecks += yoloCleanupPass.GapFillSummary.CutGuardDirectDifferenceChecks;
                finalGapFillCutGuardDirectDifferenceSkipped += yoloCleanupPass.GapFillSummary.CutGuardDirectDifferenceSkipped;

            IReadOnlyList<string> yoloPreSmoothCutPairs = Array.Empty<string>();
            IReadOnlyList<string> yoloPreSmoothStrongCarryProbeCutPairs = Array.Empty<string>();
            IReadOnlyList<string> yoloPostSmoothCutPairs = Array.Empty<string>();
            IReadOnlyList<string> yoloStrongCarryProbeCutPairs = Array.Empty<string>();
            IReadOnlyList<string> yoloCutPairs = Array.Empty<string>();
            IReadOnlyList<int> yoloSceneCutBlockedFrames = Array.Empty<int>();
            IReadOnlyCollection<FaceTrackFilledFace> yoloSceneCutBlockedFaces = Array.Empty<FaceTrackFilledFace>();
            int yoloPreSmoothGuardChecked = 0;
            int yoloPreSmoothGuardDirectDifferenceChecks = 0;
            int yoloPreSmoothGuardDirectDifferenceSkipped = 0;
            int yoloPreSmoothStrongCarryProbeChecked = 0;
            int yoloPreSmoothStrongCarryProbeDirectDifferenceChecks = 0;
            int yoloPreSmoothStrongCarryProbeDirectDifferenceSkipped = 0;
            int yoloPostSmoothGuardChecked = 0;
            int yoloPostSmoothGuardDirectDifferenceChecks = 0;
            int yoloPostSmoothGuardDirectDifferenceSkipped = 0;
            int yoloPostStrongCarryProbeChecked = 0;
            int yoloPostStrongCarryProbeDirectDifferenceChecks = 0;
            int yoloPostStrongCarryProbeDirectDifferenceSkipped = 0;
            int sceneCutCarryRemovedCount = 0;
            int sceneCutPostGapFillCarryPairCount = 0;
            int sceneCutPostGapFillCarryRemovedCount = 0;
            int sceneCutPostGapFillProtectedFrameCount = 0;
            int finalSceneCutPreGuardWindowCount = 0;
            int finalSceneCutPreStrongCarryWindowCount = 0;
            int finalSceneCutPostGuardWindowCount = 0;
            int finalSceneCutPostStrongCarryWindowCount = 0;
            int finalSceneCutCarryWindowCount = 0;
            int finalSceneCutPostGapFillWindowCount = 0;
            var yoloSceneCutPostProcessor = new YoloSceneCutPostProcessor();
            var swScenePre = Stopwatch.StartNew();
            bool ranPreSceneGuard = false;

            if (_options.UseTracking && _options.FilterProfile == FaceFilterProfile.Yolo && enableSceneCutCleanup)
            {
                ranPreSceneGuard = true;
                var swScenePreCut = Stopwatch.StartNew();
                var preSmoothGuard = yoloSceneCutPostProcessor.RemoveTrackFillAcrossSceneCuts(
                    _maskProvider,
                    videoPath,
                    trackPost,
                    cancellationToken,
                    "pre-smooth");
                swScenePreCut.Stop();
                yoloPreSmoothCutPairs = NormalizeCutFramePairs(preSmoothGuard.CutFramePairs);
                yoloPreSmoothGuardChecked += preSmoothGuard.Checked;
                yoloPreSmoothGuardDirectDifferenceChecks += preSmoothGuard.DirectDifferenceChecks;
                yoloPreSmoothGuardDirectDifferenceSkipped += preSmoothGuard.DirectDifferenceSkipped;
                var preSmoothCutWindows = BuildCutPairWindowRanges(
                    yoloPreSmoothCutPairs,
                    YoloSceneCutRebuildWindowFrames);
                finalSceneCutPreGuardWindowCount = preSmoothCutWindows.Count;
                Debug.WriteLine(
                    $"[AutoMaskPostProcessTiming] runId={runId} stage=pre-smooth-cutGuard elapsedMs={swScenePreCut.ElapsedMilliseconds} pairs={yoloPreSmoothCutPairs.Count} checked={yoloPreSmoothGuardChecked} directChecks={yoloPreSmoothGuardDirectDifferenceChecks} directSkipped={yoloPreSmoothGuardDirectDifferenceSkipped}" );

                var swScenePreStrongProbe = Stopwatch.StartNew();
                var preSmoothStrongCarryProbe = yoloSceneCutPostProcessor.ProbeStrongCarrySceneCuts(
                    _maskProvider,
                    videoPath,
                    cancellationToken,
                    "pre-smooth");
                swScenePreStrongProbe.Stop();
                yoloPreSmoothStrongCarryProbeCutPairs = NormalizeCutFramePairs(preSmoothStrongCarryProbe.CutFramePairs);
                yoloPreSmoothStrongCarryProbeChecked += preSmoothStrongCarryProbe.Checked;
                yoloPreSmoothStrongCarryProbeDirectDifferenceChecks += preSmoothStrongCarryProbe.DirectDifferenceChecks;
                yoloPreSmoothStrongCarryProbeDirectDifferenceSkipped += preSmoothStrongCarryProbe.DirectDifferenceSkipped;
                var preSmoothStrongCarryProbeWindows = BuildCutPairWindowRanges(
                    yoloPreSmoothStrongCarryProbeCutPairs,
                    YoloSceneCutRebuildWindowFrames);
                finalSceneCutPreStrongCarryWindowCount = preSmoothStrongCarryProbeWindows.Count;
                Debug.WriteLine(
                    $"[AutoMaskPostProcessTiming] runId={runId} stage=pre-smooth-strongProbe elapsedMs={swScenePreStrongProbe.ElapsedMilliseconds} pairs={yoloPreSmoothStrongCarryProbeCutPairs.Count} checked={yoloPreSmoothStrongCarryProbeChecked} directChecks={yoloPreSmoothStrongCarryProbeDirectDifferenceChecks} directSkipped={yoloPreSmoothStrongCarryProbeDirectDifferenceSkipped}");
                if (yoloPreSmoothCutPairs.Count > 0 || yoloPreSmoothStrongCarryProbeCutPairs.Count > 0)
                {
                    Debug.WriteLine(
                        $"[YoloSceneCutPairWindows] stage=pre-smooth preCutPairs={FormatTextList(yoloPreSmoothCutPairs)} preCutWindows={FormatTextList(preSmoothCutWindows)} preStrongPairs={FormatTextList(yoloPreSmoothStrongCarryProbeCutPairs)} preStrongWindows={FormatTextList(preSmoothStrongCarryProbeWindows)}");
                    Debug.WriteLine(
                        $"[YoloSceneCutRebuild] runId={runId} stage=pre-smooth action=plan preCutPairs={FormatTextList(yoloPreSmoothCutPairs)} preCutWindows={FormatTextList(preSmoothCutWindows)} preStrongPairs={FormatTextList(yoloPreSmoothStrongCarryProbeCutPairs)} preStrongWindows={FormatTextList(preSmoothStrongCarryProbeWindows)} rebuildWindowFrames={YoloSceneCutRebuildWindowFrames}");
                }
                else
                {
                    Debug.WriteLine("[YoloSceneCutPairWindows] stage=pre-smooth preCutPairs=none preStrongPairs=none");
                    Debug.WriteLine(
                        $"[YoloSceneCutRebuild] runId={runId} stage=pre-smooth action=plan preCutPairs=none preStrongPairs=none rebuildWindowFrames={YoloSceneCutRebuildWindowFrames}");
                }
            }
            swScenePre.Stop();
            Debug.WriteLine(
                $"[AutoMaskPostProcessTiming] runId={runId} phase=yolo-scene-guard-pre-smooth run={ranPreSceneGuard} elapsedMs={swScenePre.ElapsedMilliseconds} preCutPairs={yoloPreSmoothCutPairs.Count} preProbeCutPairs={yoloPreSmoothStrongCarryProbeCutPairs.Count}");

            var swSmooth = Stopwatch.StartNew();
            bool ranTemporalSmoothing = false;

            if (_options.UseTracking && enableTemporalSmoothing)
            {
                temporalPostProcessor.ApplyTemporalSmoothing(
                    _maskProvider,
                    _totalFrames,
                    _options.FilterProfile == FaceFilterProfile.Yolo
                        ? CombineCutFramePairs(yoloPreSmoothCutPairs, yoloPreSmoothStrongCarryProbeCutPairs)
                        : Array.Empty<string>());
                ranTemporalSmoothing = true;
            }
            swSmooth.Stop();
            Debug.WriteLine(
                $"[AutoMaskPostProcessTiming] runId={runId} phase=yolo-temporal-smoothing run={ranTemporalSmoothing} elapsedMs={swSmooth.ElapsedMilliseconds} enabled={enableTemporalSmoothing}");

            if (_options.UseTracking && _options.FilterProfile == FaceFilterProfile.Yolo && enableTemporalSmoothing && enableSceneCutCleanup)
            {
                var swScenePost = Stopwatch.StartNew();
                var swScenePostCut = Stopwatch.StartNew();
                var postSmoothGuard = yoloSceneCutPostProcessor.RemoveTrackFillAcrossSceneCuts(
                    _maskProvider,
                    videoPath,
                    trackPost,
                    cancellationToken,
                    "post-smooth");
                swScenePostCut.Stop();
                yoloPostSmoothCutPairs = NormalizeCutFramePairs(postSmoothGuard.CutFramePairs);
                yoloPostSmoothGuardChecked += postSmoothGuard.Checked;
                yoloPostSmoothGuardDirectDifferenceChecks += postSmoothGuard.DirectDifferenceChecks;
                yoloPostSmoothGuardDirectDifferenceSkipped += postSmoothGuard.DirectDifferenceSkipped;
                Debug.WriteLine(
                    $"[AutoMaskPostProcessTiming] runId={runId} stage=post-smooth-cutGuard elapsedMs={swScenePostCut.ElapsedMilliseconds} pairs={yoloPostSmoothCutPairs.Count} checked={yoloPostSmoothGuardChecked} directChecks={yoloPostSmoothGuardDirectDifferenceChecks} directSkipped={yoloPostSmoothGuardDirectDifferenceSkipped}");

                var swScenePostStrongProbe = Stopwatch.StartNew();
                var strongCarryProbe = yoloSceneCutPostProcessor.ProbeStrongCarrySceneCuts(
                    _maskProvider,
                    videoPath,
                    cancellationToken,
                    "post-smooth");
                swScenePostStrongProbe.Stop();
                yoloStrongCarryProbeCutPairs = NormalizeCutFramePairs(strongCarryProbe.CutFramePairs);
                yoloPostStrongCarryProbeChecked += strongCarryProbe.Checked;
                yoloPostStrongCarryProbeDirectDifferenceChecks += strongCarryProbe.DirectDifferenceChecks;
                yoloPostStrongCarryProbeDirectDifferenceSkipped += strongCarryProbe.DirectDifferenceSkipped;
                Debug.WriteLine(
                    $"[AutoMaskPostProcessTiming] runId={runId} stage=post-smooth-strongProbe elapsedMs={swScenePostStrongProbe.ElapsedMilliseconds} pairs={yoloStrongCarryProbeCutPairs.Count} checked={yoloPostStrongCarryProbeChecked} directChecks={yoloPostStrongCarryProbeDirectDifferenceChecks} directSkipped={yoloPostStrongCarryProbeDirectDifferenceSkipped}");
                if (yoloPostSmoothCutPairs.Count > 0 || yoloStrongCarryProbeCutPairs.Count > 0)
                {
                    var postSmoothCutWindows = BuildCutPairWindowRanges(yoloPostSmoothCutPairs, YoloSceneCutRebuildWindowFrames);
                    finalSceneCutPostGuardWindowCount = postSmoothCutWindows.Count;
                    var postStrongCarryProbeWindows = BuildCutPairWindowRanges(
                        yoloStrongCarryProbeCutPairs,
                        YoloSceneCutRebuildWindowFrames);
                    finalSceneCutPostStrongCarryWindowCount = postStrongCarryProbeWindows.Count;
                    Debug.WriteLine(
                        $"[YoloSceneCutPairWindows] stage=post-smooth postCutPairs={FormatTextList(yoloPostSmoothCutPairs)} postCutWindows={FormatTextList(postSmoothCutWindows)} postStrongPairs={FormatTextList(yoloStrongCarryProbeCutPairs)} postStrongWindows={FormatTextList(postStrongCarryProbeWindows)}");
                    Debug.WriteLine(
                        $"[YoloSceneCutRebuild] runId={runId} stage=post-smooth action=plan postCutPairs={FormatTextList(yoloPostSmoothCutPairs)} postCutWindows={FormatTextList(postSmoothCutWindows)} postStrongPairs={FormatTextList(yoloStrongCarryProbeCutPairs)} postStrongWindows={FormatTextList(postStrongCarryProbeWindows)} rebuildWindowFrames={YoloSceneCutRebuildWindowFrames}");
                }
                else
                {
                    Debug.WriteLine("[YoloSceneCutPairWindows] stage=post-smooth postCutPairs=none postStrongPairs=none");
                    Debug.WriteLine(
                        $"[YoloSceneCutRebuild] runId={runId} stage=post-smooth action=plan postCutPairs=none postStrongPairs=none rebuildWindowFrames={YoloSceneCutRebuildWindowFrames}");
                }
                swScenePost.Stop();
                Debug.WriteLine(
                    $"[AutoMaskPostProcessTiming] runId={runId} phase=yolo-scene-guard-post-smooth run=true elapsedMs={swScenePost.ElapsedMilliseconds} postCutPairs={yoloPostSmoothCutPairs.Count} postProbePairs={yoloStrongCarryProbeCutPairs.Count}");
            }
            else
            {
                Debug.WriteLine(
                    $"[AutoMaskPostProcessTiming] runId={runId} phase=yolo-scene-guard-post-smooth run=false elapsedMs=0 enabledTemporal={enableTemporalSmoothing} enabledScene={enableSceneCutCleanup}");
            }

            IReadOnlyList<int> yoloProtectedSceneCarryFrames = Array.Empty<int>();
            const string defaultSceneCutPairSourceBreakdown = "preCutOnly=0,preStrongOnly=0,postCutOnly=0,postStrongOnly=0,shared=0,pairOrphans=0";
            string finalSceneCutPairSourceBreakdown = defaultSceneCutPairSourceBreakdown;
            string finalSceneCutPostGapFillPairSourceBreakdown = defaultSceneCutPairSourceBreakdown;
            if (_options.FilterProfile == FaceFilterProfile.Yolo && enableSceneCutCleanup)
            {
                var swSceneFinal = Stopwatch.StartNew();
                yoloCutPairs = CombineCutFramePairs(
                    yoloPreSmoothCutPairs,
                    yoloPreSmoothStrongCarryProbeCutPairs,
                    yoloPostSmoothCutPairs,
                    yoloCleanupPass.CutFramePairs,
                    yoloStrongCarryProbeCutPairs);
                var preSceneWindows = BuildCutPairWindowRanges(
                    yoloPreSmoothCutPairs,
                    YoloSceneCutRebuildWindowFrames);
                var preSceneStrongWindows = BuildCutPairWindowRanges(
                    yoloPreSmoothStrongCarryProbeCutPairs,
                    YoloSceneCutRebuildWindowFrames);
                var postSceneWindows = BuildCutPairWindowRanges(
                    yoloPostSmoothCutPairs,
                    YoloSceneCutRebuildWindowFrames);
                var postSceneStrongWindows = BuildCutPairWindowRanges(
                    yoloStrongCarryProbeCutPairs,
                    YoloSceneCutRebuildWindowFrames);
                var yoloCutWindows = BuildCutPairWindowRanges(yoloCutPairs, YoloSceneCutRebuildWindowFrames);
                finalSceneCutCarryWindowCount = yoloCutWindows.Count;
                finalSceneCutPairSourceBreakdown = BuildSceneCutPairSourceBreakdown(
                    yoloCutPairs,
                    yoloPreSmoothCutPairs,
                    yoloPreSmoothStrongCarryProbeCutPairs,
                    yoloPostSmoothCutPairs,
                    yoloStrongCarryProbeCutPairs);
                if (yoloCutPairs.Count > 0)
                {
                    Debug.WriteLine(
                        $"[YoloSceneCutPairWindows] stage=final cutPairs={FormatTextList(yoloCutPairs)} cutWindows={FormatTextList(yoloCutWindows)} prePairs={FormatTextList(yoloPreSmoothCutPairs)} preWindows={FormatTextList(preSceneWindows)} preStrongPairs={FormatTextList(yoloPreSmoothStrongCarryProbeCutPairs)} preStrongWindows={FormatTextList(preSceneStrongWindows)} postPairs={FormatTextList(yoloPostSmoothCutPairs)} postWindows={FormatTextList(postSceneWindows)} postStrongPairs={FormatTextList(yoloStrongCarryProbeCutPairs)} postStrongWindows={FormatTextList(postSceneStrongWindows)}");
                    Debug.WriteLine(
                        $"[YoloSceneCutRebuild] runId={runId} stage=final action=cleanup carryCutPairs={FormatTextList(yoloCutPairs)} carryWindows={FormatTextList(yoloCutWindows)} preWindowSources=pre:{FormatTextList(preSceneWindows)} preStrong:{FormatTextList(preSceneStrongWindows)} postWindowSources=post:{FormatTextList(postSceneWindows)} postStrong:{FormatTextList(postSceneStrongWindows)} carryPairSourceBreakdown={finalSceneCutPairSourceBreakdown} purgeFrames={YoloSceneCutCarryPurgeFrames} blockFrames={YoloSceneCutCarryBlockFrames} maxConfidence={YoloSceneCutCarryPurgeMaxConfidence:0.###} extendedWeakMaxConfidence={YoloSceneCutExtendedWeakCarryMaxConfidence:0.###}");
                }
                else
                {
                    Debug.WriteLine(
                        $"[YoloSceneCutRebuild] runId={runId} stage=final action=cleanup carryCutPairs=none carryPairSourceBreakdown={finalSceneCutPairSourceBreakdown} purgeFrames={YoloSceneCutCarryPurgeFrames} blockFrames={YoloSceneCutCarryBlockFrames} maxConfidence={YoloSceneCutCarryPurgeMaxConfidence:0.###} extendedWeakMaxConfidence={YoloSceneCutExtendedWeakCarryMaxConfidence:0.###}");
                }
                var yoloCarryCleanup = new YoloFinalMaskPostProcessor().RemoveSceneCutCarryRemnants(
                    _maskProvider,
                    yoloCutPairs,
                    new YoloSceneCutCarryCleanupOptions
                    {
                        MaxCarryFrames = YoloSceneCutCarryPurgeFrames,
                        ExtendedWeakCarryFrames = YoloSceneCutCarryBlockFrames,
                        SourceLookbackFrames = YoloSceneCutPostCutLookbackFrames,
                        MaxConfidence = YoloSceneCutCarryPurgeMaxConfidence,
                        ExtendedWeakMaxConfidence = YoloSceneCutExtendedWeakCarryMaxConfidence,
                        CandidateMatchMinIou = YoloSceneCutCandidateMatchMinIou,
                        CandidateMatchMaxCenterShiftRatio = YoloSceneCutCandidateMatchMaxCenterShiftRatio,
                        CandidateMatchMaxAreaChangeRatio = YoloSceneCutCandidateMatchMaxAreaChangeRatio
                    });
                yoloSceneCutBlockedFrames = YoloFinalMaskPostProcessor.BuildSceneCutCarryBlockedFrames(
                    yoloCutPairs,
                    YoloSceneCutCarryBlockFrames);
                yoloProtectedSceneCarryFrames = yoloCarryCleanup.ProtectedStrongCarryLikeFrameIndices;
                if (yoloCutPairs.Count > 0)
                {
                    Debug.WriteLine(
                        $"[YoloSceneCutCarryCleanup] cutPairs={FormatTextList(yoloCutPairs)} removed={yoloCarryCleanup.RemovedFaces} removedFrames={FormatFrameList(yoloCarryCleanup.RemovedFrameIndices)} removedUnsupportedStrong={yoloCarryCleanup.RemovedUnsupportedStrongCarryLikeFaces} removedUnsupportedStrongFrames={FormatFrameList(yoloCarryCleanup.RemovedUnsupportedStrongCarryLikeFrameIndices)} protectedStrong={yoloCarryCleanup.ProtectedStrongCarryLikeFaces} protectedStrongFrames={FormatFrameList(yoloCarryCleanup.ProtectedStrongCarryLikeFrameIndices)} blockedFrames={FormatFrameList(yoloSceneCutBlockedFrames)} purgeFrames={YoloSceneCutCarryPurgeFrames} blockFrames={YoloSceneCutCarryBlockFrames} maxConfidence={YoloSceneCutCarryPurgeMaxConfidence:0.###} extendedWeakMaxConfidence={YoloSceneCutExtendedWeakCarryMaxConfidence:0.###}");
                }
                sceneCutCarryRemovedCount += yoloCarryCleanup.RemovedFaces;
                finalFalsePositiveSuppressedCount += yoloCarryCleanup.RemovedFaces;
                AddFrameIndices(finalFalsePositiveSuppressedFrameIndices, yoloCarryCleanup.RemovedFrameIndices);

                var postSceneCleanupPass = enableWeakIsolationCleanup
                    ? RemoveYoloWeakIsolatedFinalMasks(
                        videoPath,
                        cancellationToken,
                        fillStableGaps: enableGapFill,
                        blockedCutFramePairs: yoloCutPairs,
                        additionalBlockedFaces: CombineFaceInfos(
                            yoloCleanupPass.RemovedFacesInfo,
                            yoloCarryCleanup.RemovedFacesInfo),
                        sceneCarryBlockedFaces: yoloCarryCleanup.RemovedFacesInfo,
                        sceneCarryBlockedFrameIndices: yoloSceneCutBlockedFrames,
                        logLabel: "YoloFinalMaskPostSceneCleanup",
                        logWhenNoRemovals: true,
                        gapFillLogLabel: "YoloFinalMaskPostSceneGapFill",
                        gapFillSceneCutGuardLogLabel: "YoloFinalMaskPostSceneGapFillSceneCutGuard")
                    : YoloFinalMaskCleanupPassResult.Empty;
                finalFalsePositiveSuppressedCount += postSceneCleanupPass.RemovedFacesInfo.Count;
                AddFrameIndices(finalFalsePositiveSuppressedFrameIndices, postSceneCleanupPass.RemovedFrameIndices);
                finalGapFillRecoveredCount += postSceneCleanupPass.GapFillSummary.FilledFaces;
                AddFrameIndices(finalGapFillRecoveredFrameIndices, postSceneCleanupPass.GapFillSummary.FilledFrameIndices);
                finalGapFillBlockedCutGapFrames += postSceneCleanupPass.GapFillSummary.BlockedCutGapFrames;
                finalGapFillBlockedCutGapFramesBeforeCut += postSceneCleanupPass.GapFillSummary.BlockedCutGapBeforeCutFrames;
                finalGapFillBlockedCutGapFramesAfterCut += postSceneCleanupPass.GapFillSummary.BlockedCutGapAfterCutFrames;
                finalGapFillBlockedCleanupGapFrames += postSceneCleanupPass.GapFillSummary.BlockedCleanupGapFrames;
                finalGapFillBlockedSceneCarryGapFrames += postSceneCleanupPass.GapFillSummary.BlockedSceneCarryGapFrames;
                AddFrameIndices(finalGapFillBlockedCutGapFrameIndices, postSceneCleanupPass.GapFillSummary.BlockedCutGapFrameIndices);
                AddFrameIndices(finalGapFillBlockedCutGapBeforeCutFrameIndices, postSceneCleanupPass.GapFillSummary.BlockedCutGapBeforeCutFrameIndices);
                AddFrameIndices(finalGapFillBlockedCutGapAfterCutFrameIndices, postSceneCleanupPass.GapFillSummary.BlockedCutGapAfterCutFrameIndices);
                AddFrameIndices(finalGapFillBlockedCleanupGapFrameIndices, postSceneCleanupPass.GapFillSummary.BlockedCleanupGapFrameIndices);
                AddFrameIndices(finalGapFillBlockedSceneCarryFrameIndices, postSceneCleanupPass.GapFillSummary.BlockedSceneCarryFrameIndices);
                finalGapFillSuppressedWeakGeometryAnchorChecks += postSceneCleanupPass.GapFillSummary.SuppressedWeakGeometryAnchorChecks;
                finalGapFillSuppressedRiskyGeometryAnchorChecks += postSceneCleanupPass.GapFillSummary.SuppressedRiskyGeometryAnchorChecks;
                finalGapFillUnsupportedWeakAnchorChecks += postSceneCleanupPass.GapFillSummary.UnsupportedWeakAnchorChecks;
                finalGapFillCutGuardChecked += postSceneCleanupPass.GapFillSummary.CutGuardChecked;
                finalGapFillCutGuardDirectDifferenceChecks += postSceneCleanupPass.GapFillSummary.CutGuardDirectDifferenceChecks;
                finalGapFillCutGuardDirectDifferenceSkipped += postSceneCleanupPass.GapFillSummary.CutGuardDirectDifferenceSkipped;
                swSceneFinal.Stop();
                Debug.WriteLine(
                    $"[AutoMaskPostProcessTiming] runId={runId} phase=yolo-scene-cleanup run=true elapsedMs={swSceneFinal.ElapsedMilliseconds} sceneCutPairs={yoloCutPairs.Count} carryRemoved={yoloCarryCleanup.RemovedFaces} postCleanupRemoved={postSceneCleanupPass.RemovedFacesInfo.Count}");
                if (postSceneCleanupPass.CutFramePairs.Count > 0)
                {
                    var postGapFillCutPairs = CombineCutFramePairs(yoloCutPairs, postSceneCleanupPass.CutFramePairs);
                    var postGapFillCutWindows = BuildCutPairWindowRanges(postGapFillCutPairs, YoloSceneCutRebuildWindowFrames);
                    finalSceneCutPostGapFillWindowCount = postGapFillCutPairs.Count > 0
                        ? postGapFillCutWindows.Count
                        : 0;
                    if (postGapFillCutPairs.Count > 0)
                    {
                        finalSceneCutPostGapFillPairSourceBreakdown = BuildSceneCutPairSourceBreakdown(
                            postGapFillCutPairs,
                            yoloCutPairs,
                            Array.Empty<string>(),
                            postSceneCleanupPass.CutFramePairs,
                            Array.Empty<string>());
                        Debug.WriteLine(
                            $"[YoloSceneCutPairWindows] stage=post-gap-fill cutPairs={FormatTextList(postGapFillCutPairs)} cutWindows={FormatTextList(postGapFillCutWindows)}");
                        Debug.WriteLine(
                            $"[YoloSceneCutRebuild] runId={runId} stage=post-gap-fill action=cleanup carryCutPairs={FormatTextList(postGapFillCutPairs)} carryWindows={FormatTextList(postGapFillCutWindows)} carryPairSourceBreakdown={finalSceneCutPostGapFillPairSourceBreakdown} purgeFrames={YoloSceneCutCarryPurgeFrames} blockFrames={YoloSceneCutCarryBlockFrames} maxConfidence={YoloSceneCutCarryPurgeMaxConfidence:0.###} extendedWeakMaxConfidence={YoloSceneCutExtendedWeakCarryMaxConfidence:0.###}");
                    }
                    else
                    {
                        finalSceneCutPostGapFillWindowCount = 0;
                        Debug.WriteLine(
                            $"[YoloSceneCutRebuild] runId={runId} stage=post-gap-fill action=cleanup carryCutPairs=none carryPairSourceBreakdown=preCutOnly=0,preStrongOnly=0,postCutOnly=0,postStrongOnly=0,shared=0,pairOrphans=0 purgeFrames={YoloSceneCutCarryPurgeFrames} blockFrames={YoloSceneCutCarryBlockFrames} maxConfidence={YoloSceneCutCarryPurgeMaxConfidence:0.###} extendedWeakMaxConfidence={YoloSceneCutExtendedWeakCarryMaxConfidence:0.###}");
                    }
                    var postGapFillCarryCleanup = new YoloFinalMaskPostProcessor().RemoveSceneCutCarryRemnants(
                        _maskProvider,
                        postGapFillCutPairs,
                        new YoloSceneCutCarryCleanupOptions
                        {
                            MaxCarryFrames = YoloSceneCutCarryPurgeFrames,
                            ExtendedWeakCarryFrames = YoloSceneCutCarryBlockFrames,
                            SourceLookbackFrames = YoloSceneCutPostCutLookbackFrames,
                            MaxConfidence = YoloSceneCutCarryPurgeMaxConfidence,
                            ExtendedWeakMaxConfidence = YoloSceneCutExtendedWeakCarryMaxConfidence,
                            CandidateMatchMinIou = YoloSceneCutCandidateMatchMinIou,
                            CandidateMatchMaxCenterShiftRatio = YoloSceneCutCandidateMatchMaxCenterShiftRatio,
                            CandidateMatchMaxAreaChangeRatio = YoloSceneCutCandidateMatchMaxAreaChangeRatio
                        });
                    var postGapFillBlockedFrames = YoloFinalMaskPostProcessor.BuildSceneCutCarryBlockedFrames(
                        postGapFillCutPairs,
                        YoloSceneCutCarryBlockFrames);
                    yoloProtectedSceneCarryFrames = CombineFrameIndices(
                        yoloProtectedSceneCarryFrames,
                        postGapFillCarryCleanup.ProtectedStrongCarryLikeFrameIndices);
                    sceneCutPostGapFillCarryPairCount = postGapFillCutPairs.Count;
                    sceneCutPostGapFillCarryRemovedCount = postGapFillCarryCleanup.RemovedFaces;
                    sceneCutPostGapFillProtectedFrameCount = postGapFillCarryCleanup.ProtectedStrongCarryLikeFrameIndices.Count;
                    Debug.WriteLine(
                        $"[YoloSceneCutCarryCleanup] stage=post-gap-fill cutPairs={FormatTextList(postGapFillCutPairs)} removed={postGapFillCarryCleanup.RemovedFaces} removedFrames={FormatFrameList(postGapFillCarryCleanup.RemovedFrameIndices)} removedUnsupportedStrong={postGapFillCarryCleanup.RemovedUnsupportedStrongCarryLikeFaces} removedUnsupportedStrongFrames={FormatFrameList(postGapFillCarryCleanup.RemovedUnsupportedStrongCarryLikeFrameIndices)} protectedStrong={postGapFillCarryCleanup.ProtectedStrongCarryLikeFaces} protectedStrongFrames={FormatFrameList(postGapFillCarryCleanup.ProtectedStrongCarryLikeFrameIndices)} blockedFrames={FormatFrameList(postGapFillBlockedFrames)} purgeFrames={YoloSceneCutCarryPurgeFrames} blockFrames={YoloSceneCutCarryBlockFrames} maxConfidence={YoloSceneCutCarryPurgeMaxConfidence:0.###} extendedWeakMaxConfidence={YoloSceneCutExtendedWeakCarryMaxConfidence:0.###}");
                    finalFalsePositiveSuppressedCount += postGapFillCarryCleanup.RemovedFaces;
                    AddFrameIndices(finalFalsePositiveSuppressedFrameIndices, postGapFillCarryCleanup.RemovedFrameIndices);

                    if (enableWeakIsolationCleanup && enableGapFill)
                    {
                        var postGapFillCleanupPass = RemoveYoloWeakIsolatedFinalMasks(
                            videoPath,
                            cancellationToken,
                            fillStableGaps: false,
                            logLabel: "YoloFinalMaskPostGapFillCleanup",
                            logWhenNoRemovals: true);
                        finalFalsePositiveSuppressedCount += postGapFillCleanupPass.RemovedFacesInfo.Count;
                        AddFrameIndices(finalFalsePositiveSuppressedFrameIndices, postGapFillCleanupPass.RemovedFrameIndices);
                    }

                    sceneCutCarryRemovedCount += postGapFillCarryCleanup.RemovedFaces;
                }

                yoloSceneCutBlockedFaces = yoloCarryCleanup.RemovedFacesInfo;
            }
            else
            {
                Debug.WriteLine(
                    $"[AutoMaskPostProcessTiming] runId={runId} phase=yolo-scene-cleanup run=false elapsedMs=0 enabled={enableSceneCutCleanup} profile={_options.FilterProfile}");
            }

            var swGapFill = Stopwatch.StartNew();
            bool ranStandaloneGapFill = _options.FilterProfile == FaceFilterProfile.Yolo && enableGapFill && !enableWeakIsolationCleanup;
            YoloGapFillRunSummary yoloStandaloneGapFillSummary = YoloGapFillRunSummary.Empty;
            if (_options.FilterProfile == FaceFilterProfile.Yolo && enableGapFill && !enableWeakIsolationCleanup)
            {
                yoloStandaloneGapFillSummary = FillYoloStableFinalMaskGaps(
                    new YoloFinalMaskPostProcessor(),
                    videoPath,
                    cancellationToken,
                    blockedCutFramePairs: yoloCutPairs,
                    blockedFrameIndices: yoloSceneCutBlockedFrames,
                    blockedFaces: yoloSceneCutBlockedFaces,
                    skipSceneCutGuard: !enableSceneCutCleanup,
                    sceneCarryBlockedFaces: yoloSceneCutBlockedFaces,
                    sceneCarryBlockedFrameIndices: yoloSceneCutBlockedFrames,
                    gapFillLogLabel: "YoloFinalMaskGapFill",
                    gapFillSceneCutGuardLogLabel: "YoloFinalMaskGapFillSceneCutGuard");
                finalGapFillRecoveredCount += yoloStandaloneGapFillSummary.FilledFaces;
                AddFrameIndices(finalGapFillRecoveredFrameIndices, yoloStandaloneGapFillSummary.FilledFrameIndices);
                finalGapFillBlockedCutGapFrames += yoloStandaloneGapFillSummary.BlockedCutGapFrames;
                finalGapFillBlockedCutGapFramesBeforeCut += yoloStandaloneGapFillSummary.BlockedCutGapBeforeCutFrames;
                finalGapFillBlockedCutGapFramesAfterCut += yoloStandaloneGapFillSummary.BlockedCutGapAfterCutFrames;
                finalGapFillBlockedCleanupGapFrames += yoloStandaloneGapFillSummary.BlockedCleanupGapFrames;
                finalGapFillBlockedSceneCarryGapFrames += yoloStandaloneGapFillSummary.BlockedSceneCarryGapFrames;
                AddFrameIndices(finalGapFillBlockedCutGapFrameIndices, yoloStandaloneGapFillSummary.BlockedCutGapFrameIndices);
                AddFrameIndices(finalGapFillBlockedCutGapBeforeCutFrameIndices, yoloStandaloneGapFillSummary.BlockedCutGapBeforeCutFrameIndices);
                AddFrameIndices(finalGapFillBlockedCutGapAfterCutFrameIndices, yoloStandaloneGapFillSummary.BlockedCutGapAfterCutFrameIndices);
                AddFrameIndices(finalGapFillBlockedCleanupGapFrameIndices, yoloStandaloneGapFillSummary.BlockedCleanupGapFrameIndices);
                AddFrameIndices(finalGapFillBlockedSceneCarryFrameIndices, yoloStandaloneGapFillSummary.BlockedSceneCarryFrameIndices);
                finalGapFillSuppressedWeakGeometryAnchorChecks += yoloStandaloneGapFillSummary.SuppressedWeakGeometryAnchorChecks;
                finalGapFillSuppressedRiskyGeometryAnchorChecks += yoloStandaloneGapFillSummary.SuppressedRiskyGeometryAnchorChecks;
                finalGapFillUnsupportedWeakAnchorChecks += yoloStandaloneGapFillSummary.UnsupportedWeakAnchorChecks;
                finalGapFillCutGuardChecked += yoloStandaloneGapFillSummary.CutGuardChecked;
                finalGapFillCutGuardDirectDifferenceChecks += yoloStandaloneGapFillSummary.CutGuardDirectDifferenceChecks;
                finalGapFillCutGuardDirectDifferenceSkipped += yoloStandaloneGapFillSummary.CutGuardDirectDifferenceSkipped;
            }
            swGapFill.Stop();
            Debug.WriteLine(
                $"[AutoMaskPostProcessTiming] runId={runId} phase=yolo-standalone-gap-fill run={ranStandaloneGapFill} elapsedMs={swGapFill.ElapsedMilliseconds} enabled={enableGapFill} weakCleanup={enableWeakIsolationCleanup}");

            if (!enablePostProcessing)
            {
                Debug.WriteLine(
                    _options.FilterProfile == FaceFilterProfile.Yolo && runYoloMissRecovery
                        ? "[AutoMaskPostProcess] post-processing disabled · YOLO baseline 보완 모드(추적 기반 미탐 보완)"
                        : "[AutoMaskPostProcess] post-processing disabled");
            }
            var finalSummary = LogFinalMaskSummary(
                yoloProtectedSceneCarryFrames,
                sceneCutPreGuardPairCount: yoloPreSmoothCutPairs.Count,
                sceneCutPreStrongProbePairCount: yoloPreSmoothStrongCarryProbeCutPairs.Count,
                sceneCutPostGuardPairCount: yoloPostSmoothCutPairs.Count,
                sceneCutPostStrongProbePairCount: yoloStrongCarryProbeCutPairs.Count,
                sceneCutCarryPairCount: yoloCutPairs.Count,
                sceneCutCarryRemovedCount: sceneCutCarryRemovedCount,
                sceneCutProtectedFrameCount: yoloProtectedSceneCarryFrames.Count,
                finalMissRecoveryFillCount: finalMissRecoveryFillCount,
                finalMissRecoveryFilledFrameIndices: finalMissRecoveryFillFrameIndices,
                finalFalsePositiveSuppressedCount: finalFalsePositiveSuppressedCount,
                finalFalsePositiveSuppressedFrameIndices: finalFalsePositiveSuppressedFrameIndices,
                finalOffModeWeakCleanupCount: finalOffModeWeakCleanupCount,
                finalOffModeWeakCleanupFrameIndices: finalOffModeWeakCleanupFrameIndices,
                finalGapFillRecoveredCount: finalGapFillRecoveredCount,
                finalGapFillRecoveredFrameIndices: finalGapFillRecoveredFrameIndices,
                finalGapFillBlockedCutGapFrames: finalGapFillBlockedCutGapFrames,
                finalGapFillBlockedCleanupGapFrames: finalGapFillBlockedCleanupGapFrames,
                finalGapFillBlockedSceneCarryGapFrames: finalGapFillBlockedSceneCarryGapFrames,
                finalGapFillBlockedCutGapFramesBeforeCut: finalGapFillBlockedCutGapFramesBeforeCut,
                finalGapFillBlockedCutGapFramesAfterCut: finalGapFillBlockedCutGapFramesAfterCut,
                finalGapFillBlockedCutGapFrameIndices: finalGapFillBlockedCutGapFrameIndices,
                finalGapFillBlockedCutGapBeforeCutFrameIndices: finalGapFillBlockedCutGapBeforeCutFrameIndices,
                finalGapFillBlockedCutGapAfterCutFrameIndices: finalGapFillBlockedCutGapAfterCutFrameIndices,
                finalGapFillBlockedCleanupGapFrameIndices: finalGapFillBlockedCleanupGapFrameIndices,
                finalGapFillBlockedSceneCarryFrameIndices: finalGapFillBlockedSceneCarryFrameIndices,
                finalGapFillSuppressedWeakGeometryAnchorChecks: finalGapFillSuppressedWeakGeometryAnchorChecks,
                finalGapFillSuppressedRiskyGeometryAnchorChecks: finalGapFillSuppressedRiskyGeometryAnchorChecks,
                finalGapFillUnsupportedWeakAnchorChecks: finalGapFillUnsupportedWeakAnchorChecks,
                finalSceneCutPreGuardChecked: yoloPreSmoothGuardChecked,
                finalSceneCutPreGuardDirectDifferenceChecks: yoloPreSmoothGuardDirectDifferenceChecks,
                finalSceneCutPreGuardDirectDifferenceSkipped: yoloPreSmoothGuardDirectDifferenceSkipped,
                finalSceneCutPreStrongCarryProbeChecked: yoloPreSmoothStrongCarryProbeChecked,
                finalSceneCutPreStrongCarryProbeDirectDifferenceChecks: yoloPreSmoothStrongCarryProbeDirectDifferenceChecks,
                finalSceneCutPreStrongCarryProbeDirectDifferenceSkipped: yoloPreSmoothStrongCarryProbeDirectDifferenceSkipped,
                finalSceneCutPostGuardChecked: yoloPostSmoothGuardChecked,
                finalSceneCutPostGuardDirectDifferenceChecks: yoloPostSmoothGuardDirectDifferenceChecks,
                finalSceneCutPostGuardDirectDifferenceSkipped: yoloPostSmoothGuardDirectDifferenceSkipped,
                finalSceneCutPostStrongCarryProbeChecked: yoloPostStrongCarryProbeChecked,
                finalSceneCutPostStrongCarryProbeDirectDifferenceChecks: yoloPostStrongCarryProbeDirectDifferenceChecks,
                finalSceneCutPostStrongCarryProbeDirectDifferenceSkipped: yoloPostStrongCarryProbeDirectDifferenceSkipped,
                finalGapFillCutGuardChecked: finalGapFillCutGuardChecked,
                finalGapFillCutGuardDirectDifferenceChecks: finalGapFillCutGuardDirectDifferenceChecks,
                finalGapFillCutGuardDirectDifferenceSkipped: finalGapFillCutGuardDirectDifferenceSkipped,
                finalSceneCutPreGuardWindowCount: finalSceneCutPreGuardWindowCount,
                finalSceneCutPreStrongCarryWindowCount: finalSceneCutPreStrongCarryWindowCount,
                finalSceneCutPostGuardWindowCount: finalSceneCutPostGuardWindowCount,
                finalSceneCutPostStrongCarryWindowCount: finalSceneCutPostStrongCarryWindowCount,
                finalSceneCutCarryWindowCount: finalSceneCutCarryWindowCount,
                finalSceneCutPostGapFillWindowCount: finalSceneCutPostGapFillWindowCount,
                finalSceneCutPairSourceBreakdown: finalSceneCutPairSourceBreakdown,
                finalSceneCutPostGapFillPairSourceBreakdown: finalSceneCutPostGapFillPairSourceBreakdown);
            swTotal.Stop();
            Debug.WriteLine(
                $"[AutoMaskPostProcessTiming] runId={runId} phase=total elapsedMs={swTotal.ElapsedMilliseconds} profile={_options.FilterProfile} totalFrames={_totalFrames} detectEveryN={_options.DetectEveryNFrames}");
            return new AutoMaskPostProcessResult(
                trackPost,
                yoloProtectedSceneCarryFrames,
                finalSummary,
                (int)swTotal.ElapsedMilliseconds);
        }

        private YoloFinalMaskCleanupPassResult RemoveYoloWeakIsolatedFinalMasks(
            string videoPath,
            CancellationToken cancellationToken,
            bool fillStableGaps = true,
            bool skipSceneCutGuard = false,
            IReadOnlyCollection<string>? blockedCutFramePairs = null,
            IReadOnlyCollection<int>? additionalBlockedFrameIndices = null,
            IReadOnlyCollection<FaceTrackFilledFace>? additionalBlockedFaces = null,
            IReadOnlyCollection<FaceTrackFilledFace>? sceneCarryBlockedFaces = null,
            IReadOnlyCollection<int>? sceneCarryBlockedFrameIndices = null,
            string logLabel = "YoloFinalMaskCleanup",
            bool logWhenNoRemovals = false,
            string gapFillLogLabel = "YoloFinalMaskGapFill",
            string gapFillSceneCutGuardLogLabel = "YoloFinalMaskGapFillSceneCutGuard")
        {
            var postProcessor = new YoloFinalMaskPostProcessor();
            var cleanup = postProcessor.RemoveWeakIsolatedMasks(
                _maskProvider,
                new YoloFinalMaskCleanupOptions
                {
                    NeighborWindowFrames = 1,
                    WeakConfidenceMax = YoloFinalMaskWeakIsolatedConfidenceMax,
                    EdgeMarginRatio = YoloFinalMaskEdgeMarginRatio
                });
            if (cleanup.RemovedWeakIsolatedFaces > 0 || logWhenNoRemovals)
            {
                Debug.WriteLine(
                    $"[{logLabel}] removedWeakIsolated={cleanup.RemovedWeakIsolatedFaces} removedWeakUnsupported={cleanup.RemovedWeakUnsupportedFaces} removedMediumUnsupported={cleanup.RemovedMediumUnsupportedFaces} removedWeakShortClusters={cleanup.RemovedWeakShortClusterFaces} removedWeakTextureClusters={cleanup.RemovedWeakTextureClusterFaces} removedWeakTinyClusters={cleanup.RemovedWeakTinyClusterFaces} removedTinyShortClusters={cleanup.RemovedTinyShortClusterFaces} removedTinyIsolated={cleanup.RemovedTinyIsolatedFaces} removedTopEdgeWeakClusters={cleanup.RemovedTopEdgeWeakClusterFaces} removedTopEdgeLargeDuplicates={cleanup.RemovedTopEdgeLargeDuplicateFaces} removedUpperWeakClusters={cleanup.RemovedUpperWeakClusterFaces} removedLowerWeakClusters={cleanup.RemovedLowerWeakClusterFaces} removedAspectOutliers={cleanup.RemovedAspectOutlierClusterFaces} removedFrames={FormatFrameList(cleanup.RemovedFrameIndices)} maxConf={YoloFinalMaskWeakIsolatedConfidenceMax:0.###}");
            }

            if (cleanup.RemovedWeakIsolatedFaces <= 0)
            {
                IReadOnlyList<string> cutPairs = Array.Empty<string>();
                var gapFillSummary = YoloGapFillRunSummary.Empty;
                if (fillStableGaps)
                {
                    gapFillSummary = FillYoloStableFinalMaskGaps(
                        postProcessor,
                        videoPath,
                        cancellationToken,
                        blockedCutFramePairs,
                        additionalBlockedFrameIndices,
                        additionalBlockedFaces,
                        skipSceneCutGuard: skipSceneCutGuard,
                        sceneCarryBlockedFaces,
                        sceneCarryBlockedFrameIndices,
                        gapFillLogLabel,
                        gapFillSceneCutGuardLogLabel);

                    cutPairs = gapFillSummary.CutFramePairs;
                }
                return new YoloFinalMaskCleanupPassResult(
                    cleanup.RemovedFrameIndices,
                    cleanup.RemovedFacesInfo,
                    cutPairs,
                    gapFillSummary);
            }

            IReadOnlyList<string> gapFillCutPairs = Array.Empty<string>();
            var gapFillSummaryFaces = YoloGapFillRunSummary.Empty;
            if (fillStableGaps)
            {
                gapFillSummaryFaces = FillYoloStableFinalMaskGaps(
                    postProcessor,
                    videoPath,
                    cancellationToken,
                    blockedCutFramePairs,
                    additionalBlockedFrameIndices,
                    CombineFaceInfos(additionalBlockedFaces, cleanup.RemovedFacesInfo),
                    skipSceneCutGuard: skipSceneCutGuard,
                    sceneCarryBlockedFaces,
                    sceneCarryBlockedFrameIndices,
                    gapFillLogLabel,
                    gapFillSceneCutGuardLogLabel);
                gapFillCutPairs = gapFillSummaryFaces.CutFramePairs;
            }

            return new YoloFinalMaskCleanupPassResult(
                cleanup.RemovedFrameIndices,
                cleanup.RemovedFacesInfo,
                gapFillCutPairs,
                gapFillSummaryFaces);
        }

        private YoloGapFillRunSummary FillYoloStableFinalMaskGaps(
            YoloFinalMaskPostProcessor postProcessor,
            string videoPath,
            CancellationToken cancellationToken,
            IReadOnlyCollection<string>? blockedCutFramePairs,
            IReadOnlyCollection<int>? blockedFrameIndices,
            IReadOnlyCollection<FaceTrackFilledFace>? blockedFaces,
            bool skipSceneCutGuard,
            IReadOnlyCollection<FaceTrackFilledFace>? sceneCarryBlockedFaces,
            IReadOnlyCollection<int>? sceneCarryBlockedFrameIndices,
            string gapFillLogLabel,
            string gapFillSceneCutGuardLogLabel,
            int gapFillMaxGapFrames = YoloFinalMaskStableGapMaxFrames,
            float gapFillMinAnchorConfidence = 0.55f,
            float gapFillSupportedAnchorMinConfidence = 0.50f)
        {
            var gapFill = postProcessor.FillShortStableGaps(
                _maskProvider,
                new YoloFinalMaskGapFillOptions
                {
                    MaxGapFrames = gapFillMaxGapFrames,
                    BlockedCutFramePairs = blockedCutFramePairs ?? Array.Empty<string>(),
                    BlockedFrameIndices = blockedFrameIndices ?? Array.Empty<int>(),
                    BlockedFaces = blockedFaces ?? Array.Empty<FaceTrackFilledFace>(),
                    BlockedSceneCarryFaces = sceneCarryBlockedFaces ?? Array.Empty<FaceTrackFilledFace>(),
                    BlockedSceneCarryFrameIndices = sceneCarryBlockedFrameIndices ?? Array.Empty<int>(),
                    MinAnchorConfidence = gapFillMinAnchorConfidence,
                    SupportedAnchorMinConfidence = gapFillSupportedAnchorMinConfidence
                });
            if (gapFill.FilledFaces <= 0 &&
                gapFill.BlockedCutGapFaces <= 0 &&
                gapFill.BlockedCleanupGapFrames <= 0 &&
                gapFill.BlockedSceneCarryGapFrames <= 0 &&
                gapFill.SuppressedWeakGeometryAnchorChecks <= 0 &&
                gapFill.SuppressedRiskyGeometryAnchorChecks <= 0 &&
                gapFill.UnsupportedWeakAnchorChecks <= 0)
            {
                return YoloGapFillRunSummary.Empty;
            }

            Debug.WriteLine(
                $"[{gapFillLogLabel}] filled={gapFill.FilledFaces} frames={FormatFrameList(gapFill.FilledFrameIndices)} blockedByCut={gapFill.BlockedCutGapFaces} cutBlockedFrames={FormatFrameList(gapFill.BlockedCutFrameIndices)} blockedByCleanup={gapFill.BlockedCleanupGapFrames} cleanupBlockedFrames={FormatFrameList(gapFill.BlockedCleanupFrameIndices)} blockedBySceneCarry={gapFill.BlockedSceneCarryGapFrames} sceneCarryBlockedFrames={FormatFrameList(gapFill.BlockedSceneCarryFrameIndices)} suppressedWeakGeometryAnchors={gapFill.SuppressedWeakGeometryAnchorChecks} suppressedRiskyGeometryAnchors={gapFill.SuppressedRiskyGeometryAnchorChecks} unsupportedWeakAnchors={gapFill.UnsupportedWeakAnchorChecks}");

            if (gapFill.CutGuardFacesInfo.Count == 0)
                return new YoloGapFillRunSummary(
                    FilledFaces: gapFill.FilledFaces,
                    FilledFrameIndices: gapFill.FilledFrameIndices,
                    GapCandidateFaces: gapFill.CutGuardFacesInfo.Count,
                    BlockedCutGapFrames: gapFill.BlockedCutGapFaces,
                    BlockedCutGapBeforeCutFrames: gapFill.BlockedCutGapBeforeCutFaces,
                    BlockedCutGapAfterCutFrames: gapFill.BlockedCutGapAfterCutFaces,
                    BlockedCutGapFrameIndices: gapFill.BlockedCutFrameIndices,
                    BlockedCutGapBeforeCutFrameIndices: gapFill.BlockedCutBeforeCutFrameIndices,
                    BlockedCutGapAfterCutFrameIndices: gapFill.BlockedCutAfterCutFrameIndices,
                    BlockedCleanupGapFrames: gapFill.BlockedCleanupGapFrames,
                    BlockedCleanupGapFrameIndices: gapFill.BlockedCleanupFrameIndices,
                    BlockedSceneCarryGapFrames: gapFill.BlockedSceneCarryGapFrames,
                    BlockedSceneCarryFrameIndices: gapFill.BlockedSceneCarryFrameIndices,
                    SuppressedWeakGeometryAnchorChecks: gapFill.SuppressedWeakGeometryAnchorChecks,
                    SuppressedRiskyGeometryAnchorChecks: gapFill.SuppressedRiskyGeometryAnchorChecks,
                    UnsupportedWeakAnchorChecks: gapFill.UnsupportedWeakAnchorChecks,
                    CutGuardChecked: 0,
                    CutGuardDirectDifferenceChecks: 0,
                    CutGuardDirectDifferenceSkipped: 0,
                    CutFramePairs: Array.Empty<string>());

            if (skipSceneCutGuard)
            {
                Debug.WriteLine(
                    $"[{gapFillSceneCutGuardLogLabel}] skipped scene-cut guard candidates={gapFill.CutGuardFacesInfo.Count} blockedCut={gapFill.BlockedCutGapFaces} blockedCutBefore={gapFill.BlockedCutGapBeforeCutFaces} blockedCutAfter={gapFill.BlockedCutGapAfterCutFaces} blockedCleanup={gapFill.BlockedCleanupGapFrames} blockedSceneCarry={gapFill.BlockedSceneCarryGapFrames} filled={gapFill.FilledFaces}");

                return new YoloGapFillRunSummary(
                    FilledFaces: gapFill.FilledFaces,
                    FilledFrameIndices: gapFill.FilledFrameIndices,
                    GapCandidateFaces: gapFill.CutGuardFacesInfo.Count,
                    BlockedCutGapFrames: gapFill.BlockedCutGapFaces,
                    BlockedCutGapBeforeCutFrames: gapFill.BlockedCutGapBeforeCutFaces,
                    BlockedCutGapAfterCutFrames: gapFill.BlockedCutGapAfterCutFaces,
                    BlockedCutGapFrameIndices: gapFill.BlockedCutFrameIndices,
                    BlockedCutGapBeforeCutFrameIndices: gapFill.BlockedCutBeforeCutFrameIndices,
                    BlockedCutGapAfterCutFrameIndices: gapFill.BlockedCutAfterCutFrameIndices,
                    BlockedCleanupGapFrames: gapFill.BlockedCleanupGapFrames,
                    BlockedCleanupGapFrameIndices: gapFill.BlockedCleanupFrameIndices,
                    BlockedSceneCarryGapFrames: gapFill.BlockedSceneCarryGapFrames,
                    BlockedSceneCarryFrameIndices: gapFill.BlockedSceneCarryFrameIndices,
                    SuppressedWeakGeometryAnchorChecks: gapFill.SuppressedWeakGeometryAnchorChecks,
                    SuppressedRiskyGeometryAnchorChecks: gapFill.SuppressedRiskyGeometryAnchorChecks,
                    UnsupportedWeakAnchorChecks: gapFill.UnsupportedWeakAnchorChecks,
                    CutGuardChecked: 0,
                    CutGuardDirectDifferenceChecks: 0,
                    CutGuardDirectDifferenceSkipped: 0,
                    CutFramePairs: NormalizeCutFramePairs(gapFill.CutGuardFacesInfo
                        .Select(x => $"{x.SourceFrameIndex}:{x.FrameIndex}")
                        .ToArray()));
            }

            var guard = new FaceTrackSceneCutGuard().Apply(
                _maskProvider,
                videoPath,
                gapFill.CutGuardFacesInfo,
                differenceThreshold: YoloSceneCutDifferenceThreshold,
                directDifferenceThreshold: YoloSceneCutDirectDifferenceThreshold,
                candidateMatchMinIou: YoloSceneCutCandidateMatchMinIou,
                candidateMatchMaxCenterShiftRatio: YoloSceneCutCandidateMatchMaxCenterShiftRatio,
                candidateMatchMaxAreaChangeRatio: YoloSceneCutCandidateMatchMaxAreaChangeRatio,
                cancellationToken: cancellationToken,
                removeCandidates: true);

            if (!string.IsNullOrWhiteSpace(guard.Error))
            {
                var checkedPairWindows = BuildCutPairWindowRanges(guard.CheckedFramePairs, YoloSceneCutRebuildWindowFrames);
                var rejectedPairWindows = BuildCutPairWindowRanges(guard.CutFramePairs, YoloSceneCutRebuildWindowFrames);
                Debug.WriteLine(
                    $"[{gapFillSceneCutGuardLogLabel}] skipped candidates={gapFill.CutGuardFacesInfo.Count} checked={guard.Checked} checkedPairs={FormatTextList(guard.CheckedFramePairs)} checkedWindows={FormatTextList(checkedPairWindows)} maxDiff={guard.MaxDifference:0.###} cutPairs={FormatTextList(guard.CutFramePairs)} cutWindows={FormatTextList(rejectedPairWindows)} removed={guard.Removed} removedFrames={FormatFrameList(guard.RemovedFrameIndices)} error={guard.Error}");
                return YoloGapFillRunSummary.Empty;
            }

            if (guard.Checked > 0)
            {
                var checkedPairWindows = BuildCutPairWindowRanges(guard.CheckedFramePairs, YoloSceneCutRebuildWindowFrames);
                var keptPairWindows = BuildCutPairWindowRanges(guard.CutFramePairs, YoloSceneCutRebuildWindowFrames);
                Debug.WriteLine(
                    $"[{gapFillSceneCutGuardLogLabel}] candidates={gapFill.CutGuardFacesInfo.Count} checked={guard.Checked} checkedPairs={FormatTextList(guard.CheckedFramePairs)} checkedWindows={FormatTextList(checkedPairWindows)} maxDiff={guard.MaxDifference:0.###} cutPairs={FormatTextList(guard.CutFramePairs)} cutWindows={FormatTextList(keptPairWindows)} removed={guard.Removed} removedFrames={FormatFrameList(guard.RemovedFrameIndices)} threshold={guard.Threshold:0.###} elapsedMs={guard.ElapsedMs}");
                
            }
            else if (gapFill.CutGuardFacesInfo.Count > 0)
            {
                var checkedPairWindows = BuildCutPairWindowRanges(guard.CheckedFramePairs, YoloSceneCutRebuildWindowFrames);
                var rejectedPairWindows = BuildCutPairWindowRanges(guard.CutFramePairs, YoloSceneCutRebuildWindowFrames);
                Debug.WriteLine(
                    $"[{gapFillSceneCutGuardLogLabel}] candidates={gapFill.CutGuardFacesInfo.Count} checked=0 checkedPairs={FormatTextList(guard.CheckedFramePairs)} checkedWindows={FormatTextList(checkedPairWindows)} cutPairs={FormatTextList(guard.CutFramePairs)} cutWindows={FormatTextList(rejectedPairWindows)} removed={guard.Removed} removedFrames={FormatFrameList(guard.RemovedFrameIndices)} error={guard.Error}");
            }

                return new YoloGapFillRunSummary(
                    FilledFaces: gapFill.FilledFaces,
                    FilledFrameIndices: gapFill.FilledFrameIndices,
                    GapCandidateFaces: gapFill.CutGuardFacesInfo.Count,
                    BlockedCutGapFrames: gapFill.BlockedCutGapFaces,
                    BlockedCutGapBeforeCutFrames: gapFill.BlockedCutGapBeforeCutFaces,
                    BlockedCutGapAfterCutFrames: gapFill.BlockedCutGapAfterCutFaces,
                    BlockedCutGapFrameIndices: gapFill.BlockedCutFrameIndices,
                    BlockedCutGapBeforeCutFrameIndices: gapFill.BlockedCutBeforeCutFrameIndices,
                    BlockedCutGapAfterCutFrameIndices: gapFill.BlockedCutAfterCutFrameIndices,
                    BlockedCleanupGapFrames: gapFill.BlockedCleanupGapFrames,
                    BlockedCleanupGapFrameIndices: gapFill.BlockedCleanupFrameIndices,
                    BlockedSceneCarryGapFrames: gapFill.BlockedSceneCarryGapFrames,
                    BlockedSceneCarryFrameIndices: gapFill.BlockedSceneCarryFrameIndices,
                    SuppressedWeakGeometryAnchorChecks: gapFill.SuppressedWeakGeometryAnchorChecks,
                    SuppressedRiskyGeometryAnchorChecks: gapFill.SuppressedRiskyGeometryAnchorChecks,
                    UnsupportedWeakAnchorChecks: gapFill.UnsupportedWeakAnchorChecks,
                    CutGuardChecked: guard.Checked,
                    CutGuardDirectDifferenceChecks: guard.DirectDifferenceChecks,
                    CutGuardDirectDifferenceSkipped: guard.DirectDifferenceSkipped,
                    CutFramePairs: NormalizeCutFramePairs(guard.CutFramePairs));
        }

        private AutoMaskPostProcessFinalSummary LogFinalMaskSummary(
            IReadOnlyCollection<int>? protectedSceneCarryFrameIndices = null,
            int sceneCutPreGuardPairCount = 0,
            int sceneCutPreStrongProbePairCount = 0,
            int sceneCutPostGuardPairCount = 0,
            int sceneCutPostStrongProbePairCount = 0,
            int sceneCutCarryPairCount = 0,
            int sceneCutCarryRemovedCount = 0,
            int sceneCutProtectedFrameCount = 0,
            int finalMissRecoveryFillCount = 0,
            IReadOnlyCollection<int>? finalMissRecoveryFilledFrameIndices = null,
            int finalFalsePositiveSuppressedCount = 0,
            IReadOnlyCollection<int>? finalFalsePositiveSuppressedFrameIndices = null,
            int finalOffModeWeakCleanupCount = 0,
            IReadOnlyCollection<int>? finalOffModeWeakCleanupFrameIndices = null,
            int finalGapFillRecoveredCount = 0,
            IReadOnlyCollection<int>? finalGapFillRecoveredFrameIndices = null,
            int finalGapFillBlockedCutGapFrames = 0,
            int finalGapFillBlockedCutGapFramesBeforeCut = 0,
            int finalGapFillBlockedCutGapFramesAfterCut = 0,
            int finalGapFillBlockedCleanupGapFrames = 0,
            int finalGapFillBlockedSceneCarryGapFrames = 0,
            IReadOnlyCollection<int>? finalGapFillBlockedCutGapFrameIndices = null,
            IReadOnlyCollection<int>? finalGapFillBlockedCutGapBeforeCutFrameIndices = null,
            IReadOnlyCollection<int>? finalGapFillBlockedCutGapAfterCutFrameIndices = null,
            IReadOnlyCollection<int>? finalGapFillBlockedCleanupGapFrameIndices = null,
            IReadOnlyCollection<int>? finalGapFillBlockedSceneCarryFrameIndices = null,
            int finalGapFillSuppressedWeakGeometryAnchorChecks = 0,
            int finalGapFillSuppressedRiskyGeometryAnchorChecks = 0,
            int finalGapFillUnsupportedWeakAnchorChecks = 0,
            int finalSceneCutPreGuardChecked = 0,
            int finalSceneCutPreGuardDirectDifferenceChecks = 0,
            int finalSceneCutPreGuardDirectDifferenceSkipped = 0,
            int finalSceneCutPreStrongCarryProbeChecked = 0,
            int finalSceneCutPreStrongCarryProbeDirectDifferenceChecks = 0,
            int finalSceneCutPreStrongCarryProbeDirectDifferenceSkipped = 0,
            int finalSceneCutPostGuardChecked = 0,
            int finalSceneCutPostGuardDirectDifferenceChecks = 0,
            int finalSceneCutPostGuardDirectDifferenceSkipped = 0,
            int finalSceneCutPostStrongCarryProbeChecked = 0,
            int finalSceneCutPostStrongCarryProbeDirectDifferenceChecks = 0,
            int finalSceneCutPostStrongCarryProbeDirectDifferenceSkipped = 0,
            int finalGapFillCutGuardChecked = 0,
            int finalGapFillCutGuardDirectDifferenceChecks = 0,
            int finalGapFillCutGuardDirectDifferenceSkipped = 0,
            int finalSceneCutPreGuardWindowCount = 0,
            int finalSceneCutPreStrongCarryWindowCount = 0,
            int finalSceneCutPostGuardWindowCount = 0,
            int finalSceneCutPostStrongCarryWindowCount = 0,
            int finalSceneCutCarryWindowCount = 0,
            int finalSceneCutPostGapFillWindowCount = 0,
            int finalSceneCutPostGapFillCarryPairCount = 0,
            int finalSceneCutPostGapFillCarryRemovedCount = 0,
            int finalSceneCutPostGapFillProtectedFrameCount = 0,
            string finalSceneCutPairSourceBreakdown = "preCutOnly=0,preStrongOnly=0,postCutOnly=0,postStrongOnly=0,shared=0,pairOrphans=0",
            string finalSceneCutPostGapFillPairSourceBreakdown = "preCutOnly=0,preStrongOnly=0,postCutOnly=0,postStrongOnly=0,shared=0,pairOrphans=0")
        {
            if (_options.FilterProfile != FaceFilterProfile.Yolo)
            {
                var genericEntries = _maskProvider.GetFaceMaskEntries()
                    .Where(static x => x.Value.Faces.Count > 0)
                    .OrderBy(static x => x.Key)
                    .ToArray();
                int genericRows = genericEntries.Sum(static x => x.Value.Faces.Count);
                int genericShortGaps = 0;
                for (int i = 1; i < genericEntries.Length; i++)
                {
                    int gap = genericEntries[i].Key - genericEntries[i - 1].Key - 1;
                    if (gap > 0 && gap <= FinalMaskShortGapMaxFrames)
                        genericShortGaps++;
                }

                int genericPerFaceShortGaps = FindPerFaceShortGapRanges(genericEntries).Count;
                var genericReviewReasons = new List<string>();
                if (genericShortGaps > 0)
                    genericReviewReasons.Add("short-gap");
                if (genericPerFaceShortGaps > 0)
                    genericReviewReasons.Add("per-face-short-gap");
                string genericReviewReason = FormatTextList(genericReviewReasons);
                string genericEvidenceLogLine =
                    $"[FinalMaskSummary] profile={_options.FilterProfile} frames={genericEntries.Length} rows={genericRows} shortGaps={genericShortGaps} perFaceShortGaps={genericPerFaceShortGaps} reviewRequired={(genericReviewReasons.Count > 0).ToString().ToLowerInvariant()} reviewReasons={genericReviewReason} finalMissRecovery={finalMissRecoveryFillCount}";
                Debug.WriteLine(genericEvidenceLogLine);

                return AutoMaskPostProcessFinalSummary.Empty with
                {
                    FinalFrameCount = genericEntries.Length,
                    FinalRowCount = genericRows,
                    FinalShortGapCount = genericShortGaps,
                    FinalPerFaceShortGapCount = genericPerFaceShortGaps,
                    FinalReviewRequired = genericReviewReasons.Count > 0,
                    FinalReviewReasons = genericReviewReason,
                    FinalMissRecoveryFillCount = finalMissRecoveryFillCount,
                    FinalFalsePositiveSuppressedCount = finalFalsePositiveSuppressedCount,
                    FinalOffModeWeakCleanupCount = finalOffModeWeakCleanupCount,
                    FinalGapFillRecoveredCount = finalGapFillRecoveredCount,
                    SampleWindowFrames = _totalFrames,
                    SampleFrameCount = genericEntries.Length,
                    SampleRowCount = genericRows,
                    SampleShortGapCount = genericShortGaps,
                    SamplePerFaceShortGapCount = genericPerFaceShortGaps,
                    SampleReviewRequired = genericReviewReasons.Count > 0,
                    SampleReviewReasons = genericReviewReason,
                    SampleMissRecoveryFillCount = finalMissRecoveryFillCount,
                    EvidenceLogLine = genericEvidenceLogLine
                };
            }

            var protectedSceneCarryFrames = protectedSceneCarryFrameIndices?
                .Distinct()
                .OrderBy(static x => x)
                .ToArray() ?? Array.Empty<int>();
            var entries = _maskProvider.GetFaceMaskEntries()
                .Where(static x => x.Value.Faces.Count > 0)
                .OrderBy(static x => x.Key)
                .ToArray();
            if (entries.Length == 0)
            {
                var emptyReviewReasons = BuildFinalMaskReviewReasons(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, protectedSceneCarryFrames.Length);
                string noneReviewReasons = FormatTextList(emptyReviewReasons);
                string emptyEvidenceLogLine =
                    $"[FinalMaskSummary] profile=Yolo frames=0 rows=0 frameRange=none shortGaps=0 shortGapRanges=none perFaceShortGaps=0 perFaceShortGapRanges=none largeJumpGaps=0 largeJumpRanges=none isolated=0 isolatedFrames=none lowConf=0 lowConfFrames=none weakNonEdge=0 weakNonEdgeFrames=none edgeWeak=0 edgeWeakFrames=none topEdgeWeak=0 topEdgeWeakFrames=none topEdgeLarge=0 topEdgeLargeFrames=none upperWeak=0 upperWeakFrames=none lowerWeak=0 lowerWeakFrames=none aspectBad=0 aspectBadFrames=none tinyWeak=0 tinyWeakFrames=none tinyShort=0 tinyShortFrames=none protectedSceneCarry={protectedSceneCarryFrames.Length} protectedSceneCarryFrames={FormatFrameList(protectedSceneCarryFrames)} sceneCutControl=preGuard={sceneCutPreGuardPairCount},preStrong={sceneCutPreStrongProbePairCount},postGuard={sceneCutPostGuardPairCount},postStrong={sceneCutPostStrongProbePairCount},carryPairs={sceneCutCarryPairCount},carryRemoved={sceneCutCarryRemovedCount},carryProtected={sceneCutProtectedFrameCount},carryWindows=pre:{finalSceneCutPreGuardWindowCount},preStrong:{finalSceneCutPreStrongCarryWindowCount},post:{finalSceneCutPostGuardWindowCount},postStrong:{finalSceneCutPostStrongCarryWindowCount},final:{finalSceneCutCarryWindowCount},postGapFill:{finalSceneCutPostGapFillWindowCount},postGapFillCarryPairs={finalSceneCutPostGapFillCarryPairCount},postGapFillRemoved={finalSceneCutPostGapFillCarryRemovedCount},postGapFillProtected={finalSceneCutPostGapFillProtectedFrameCount},postGapFillRemovalRate={FormatRate(finalSceneCutPostGapFillCarryPairCount > 0 ? (double)finalSceneCutPostGapFillCarryRemovedCount / finalSceneCutPostGapFillCarryPairCount : 0.0)},postGapFillProtectedRate={FormatRate(finalSceneCutPostGapFillCarryPairCount > 0 ? (double)finalSceneCutPostGapFillProtectedFrameCount / finalSceneCutPostGapFillCarryPairCount : 0.0)}, sceneCutPairSourceBreakdown={finalSceneCutPairSourceBreakdown}, postGapFillPairSourceBreakdown={finalSceneCutPostGapFillPairSourceBreakdown} reviewRequired={emptyReviewReasons.Count > 0} reviewReasons={noneReviewReasons}";
                Debug.WriteLine(emptyEvidenceLogLine);
                return new AutoMaskPostProcessFinalSummary(
                    FinalFrameCount: 0,
                    FinalRowCount: 0,
                    FinalShortGapCount: 0,
                    FinalPerFaceShortGapCount: 0,
                    FinalLargeJumpGapCount: 0,
                    FinalReviewRequired: emptyReviewReasons.Count > 0,
                    FinalReviewReasons: noneReviewReasons,
                    ProtectedSceneCarryFrameCount: protectedSceneCarryFrames.Length,
                    FinalSceneCutPreGuardPairCount: sceneCutPreGuardPairCount,
                    FinalSceneCutPreStrongProbePairCount: sceneCutPreStrongProbePairCount,
                    FinalSceneCutPostGuardPairCount: sceneCutPostGuardPairCount,
                    FinalSceneCutPostStrongProbePairCount: sceneCutPostStrongProbePairCount,
                    FinalSceneCutCarryPairCount: sceneCutCarryPairCount,
                    FinalSceneCutCarryRemovedCount: sceneCutCarryRemovedCount,
                    FinalSceneCutProtectedFrameCount: sceneCutProtectedFrameCount > 0
                        ? sceneCutProtectedFrameCount
                        : protectedSceneCarryFrames.Length,
                    FinalMissRecoveryFillCount: finalMissRecoveryFillCount,
                    FinalFalsePositiveSuppressedCount: finalFalsePositiveSuppressedCount,
                    FinalOffModeWeakCleanupCount: finalOffModeWeakCleanupCount,
                    FinalGapFillRecoveredCount: finalGapFillRecoveredCount,
                    FinalGapFillBlockedCutGapFrames: finalGapFillBlockedCutGapFrames,
                    FinalGapFillBlockedCutGapFramesBeforeCut: finalGapFillBlockedCutGapFramesBeforeCut,
                    FinalGapFillBlockedCutGapFramesAfterCut: finalGapFillBlockedCutGapFramesAfterCut,
                    FinalGapFillBlockedCleanupGapFrames: finalGapFillBlockedCleanupGapFrames,
                    FinalGapFillBlockedSceneCarryGapFrames: finalGapFillBlockedSceneCarryGapFrames,
                    FinalGapFillSuppressedWeakGeometryAnchorChecks: finalGapFillSuppressedWeakGeometryAnchorChecks,
                    FinalGapFillSuppressedRiskyGeometryAnchorChecks: finalGapFillSuppressedRiskyGeometryAnchorChecks,
                    FinalGapFillUnsupportedWeakAnchorChecks: finalGapFillUnsupportedWeakAnchorChecks,
                    FinalGapFillCutGuardChecked: finalGapFillCutGuardChecked,
                    FinalGapFillCutGuardDirectDifferenceChecks: finalGapFillCutGuardDirectDifferenceChecks,
                    FinalGapFillCutGuardDirectDifferenceSkipped: finalGapFillCutGuardDirectDifferenceSkipped,
                    FinalSceneCutPreGuardWindowCount: finalSceneCutPreGuardWindowCount,
                    FinalSceneCutPreStrongCarryWindowCount: finalSceneCutPreStrongCarryWindowCount,
                    FinalSceneCutPostGuardWindowCount: finalSceneCutPostGuardWindowCount,
                    FinalSceneCutPostStrongCarryWindowCount: finalSceneCutPostStrongCarryWindowCount,
                    FinalSceneCutCarryWindowCount: finalSceneCutCarryWindowCount,
                    FinalSceneCutPostGapFillWindowCount: finalSceneCutPostGapFillWindowCount,
                    FinalSceneCutPairSourceBreakdown: finalSceneCutPairSourceBreakdown,
                    FinalSceneCutPostGapFillPairSourceBreakdown: finalSceneCutPostGapFillPairSourceBreakdown,
                    FinalSceneCutPreGuardChecked: finalSceneCutPreGuardChecked,
                    FinalSceneCutPreGuardDirectDifferenceChecks: finalSceneCutPreGuardDirectDifferenceChecks,
                    FinalSceneCutPreGuardDirectDifferenceSkipped: finalSceneCutPreGuardDirectDifferenceSkipped,
                    FinalSceneCutPreStrongCarryProbeChecked: finalSceneCutPreStrongCarryProbeChecked,
                    FinalSceneCutPreStrongCarryProbeDirectDifferenceChecks: finalSceneCutPreStrongCarryProbeDirectDifferenceChecks,
                    FinalSceneCutPreStrongCarryProbeDirectDifferenceSkipped: finalSceneCutPreStrongCarryProbeDirectDifferenceSkipped,
                    FinalSceneCutPostGuardChecked: finalSceneCutPostGuardChecked,
                    FinalSceneCutPostGuardDirectDifferenceChecks: finalSceneCutPostGuardDirectDifferenceChecks,
                    FinalSceneCutPostGuardDirectDifferenceSkipped: finalSceneCutPostGuardDirectDifferenceSkipped,
                    FinalSceneCutPostStrongCarryProbeChecked: finalSceneCutPostStrongCarryProbeChecked,
                    FinalSceneCutPostStrongCarryProbeDirectDifferenceChecks: finalSceneCutPostStrongCarryProbeDirectDifferenceChecks,
                    FinalSceneCutPostStrongCarryProbeDirectDifferenceSkipped: finalSceneCutPostStrongCarryProbeDirectDifferenceSkipped,
                    SampleWindowFrames: 0,
                    SampleFrameCount: 0,
                    SampleRowCount: 0,
                    SampleShortGapCount: 0,
                    SamplePerFaceShortGapCount: 0,
                    SampleIsolatedFrameCount: 0,
                    SampleLargeJumpGapCount: 0,
                    SampleReviewRequired: false,
                SampleReviewReasons: noneReviewReasons,
                SampleProtectedSceneCarryFrameCount: 0,
                SampleMissRecoveryFillCount: 0,
                SampleFalsePositiveSuppressionCount: 0,
                SampleOffModeWeakCleanupSuppressionCount: 0,
                SampleGapFillBlockedCutGapFrames: 0,
                SampleGapFillBlockedCutGapFramesBeforeCut: 0,
                SampleGapFillBlockedCutGapFramesAfterCut: 0,
                SampleGapFillBlockedCleanupGapFrames: 0,
                SampleGapFillBlockedSceneCarryGapFrames: 0,
                SampleWindowIssueFrameCount: 0,
                SampleWindowIssueCandidateCount: 0,
                SampleWindowStartReason: "none",
                FinalSceneCutPostGapFillCarryPairCount: finalSceneCutPostGapFillCarryPairCount,
                FinalSceneCutPostGapFillCarryRemovedCount: finalSceneCutPostGapFillCarryRemovedCount,
                FinalSceneCutPostGapFillProtectedFrameCount: finalSceneCutPostGapFillProtectedFrameCount)
                {
                    EvidenceLogLine = emptyEvidenceLogLine
                };
            }

            var frames = entries.Select(static x => x.Key).ToArray();
            int rows = entries.Sum(static x => x.Value.Faces.Count);
            var shortGapRanges = new List<string>();
            var largeJumpGapRanges = new List<string>();
            int shortGapCount = 0;
            for (int i = 1; i < frames.Length; i++)
            {
                int missing = frames[i] - frames[i - 1] - 1;
                if (missing <= 0 || missing > FinalMaskShortGapMaxFrames)
                    continue;

                shortGapCount++;
                int start = frames[i - 1] + 1;
                int end = frames[i] - 1;
                string range = FormatFrameRange(start, end);
                shortGapRanges.Add(range);

                if (TryGetBestFinalMaskFace(entries[i - 1].Value, out var previousFace) &&
                    TryGetBestFinalMaskFace(entries[i].Value, out var nextFace))
                {
                    double areaChange = GetFinalMaskAreaChange(previousFace, nextFace);
                    double centerShift = GetFinalMaskCenterShift(previousFace, nextFace);
                    if (areaChange >= FinalMaskLargeJumpAreaChangeRatio ||
                        centerShift >= FinalMaskLargeJumpCenterShift)
                    {
                        largeJumpGapRanges.Add(range);
                    }
                }
            }
            var perFaceShortGapRanges = FindPerFaceShortGapRanges(entries);
            var sampleWindowIssueFrames = new HashSet<int>();
            AddFrameRangeIssues(sampleWindowIssueFrames, shortGapRanges);
            AddFrameRangeIssues(sampleWindowIssueFrames, perFaceShortGapRanges);
            AddFrameRangeIssues(sampleWindowIssueFrames, largeJumpGapRanges);
            AddFrameIndices(sampleWindowIssueFrames, finalGapFillBlockedCutGapFrameIndices);
            AddFrameIndices(sampleWindowIssueFrames, finalGapFillBlockedCutGapBeforeCutFrameIndices);
            AddFrameIndices(sampleWindowIssueFrames, finalGapFillBlockedCutGapAfterCutFrameIndices);
            AddFrameIndices(sampleWindowIssueFrames, finalGapFillBlockedCleanupGapFrameIndices);
            AddFrameIndices(sampleWindowIssueFrames, finalGapFillBlockedSceneCarryFrameIndices);

            var isolatedFrames = new List<int>();
            for (int i = 0; i < frames.Length; i++)
            {
                bool hasPreviousNeighbor = i > 0 && frames[i] - frames[i - 1] <= 1;
                bool hasNextNeighbor = i < frames.Length - 1 && frames[i + 1] - frames[i] <= 1;
                if (!hasPreviousNeighbor && !hasNextNeighbor)
                {
                    isolatedFrames.Add(frames[i]);
                    sampleWindowIssueFrames.Add(frames[i]);
                }
            }

            int lowConfidenceRows = 0;
            int weakNonEdgeRows = 0;
            int edgeWeakRows = 0;
            int topEdgeWeakRows = 0;
            int topEdgeLargeRows = 0;
            int upperWeakRows = 0;
            int lowerWeakRows = 0;
            int aspectBadRows = 0;
            int tinyWeakRows = 0;
            int tinyShortRows = 0;
            var lowConfidenceFrames = new HashSet<int>();
            var weakNonEdgeFrames = new HashSet<int>();
            var edgeWeakFrames = new HashSet<int>();
            var topEdgeWeakFrames = new HashSet<int>();
            var topEdgeLargeFrames = new HashSet<int>();
            var upperWeakFrames = new HashSet<int>();
            var lowerWeakFrames = new HashSet<int>();
            var aspectBadFrames = new HashSet<int>();
            var tinyWeakFrames = new HashSet<int>();
            var tinyShortFrames = new HashSet<int>();
            foreach (var entry in entries)
            {
                int frameIndex = entry.Key;
                var data = entry.Value;
                for (int i = 0; i < data.Faces.Count; i++)
                {
                    var face = data.Faces[i];
                    float confidence = i < data.Confidences.Count
                        ? data.Confidences[i]
                        : data.MinConfidence ?? 1.0f;
                    if (confidence <= YoloFinalMaskLowConfidenceThreshold)
                    {
                        lowConfidenceRows++;
                        lowConfidenceFrames.Add(frameIndex);
                        sampleWindowIssueFrames.Add(frameIndex);
                    }

                    bool touchesEdge = TouchesFinalMaskFrameEdge(face, data.Size);
                    if (confidence <= YoloFinalMaskWeakIsolatedConfidenceMax)
                    {
                        if (touchesEdge)
                        {
                            edgeWeakRows++;
                            edgeWeakFrames.Add(frameIndex);
                            sampleWindowIssueFrames.Add(frameIndex);
                            if (IsUpperWeakFinalMaskFace(face, data.Size))
                            {
                                topEdgeWeakRows++;
                                topEdgeWeakFrames.Add(frameIndex);
                                sampleWindowIssueFrames.Add(frameIndex);
                            }
                        }
                        else
                        {
                            weakNonEdgeRows++;
                            weakNonEdgeFrames.Add(frameIndex);
                            sampleWindowIssueFrames.Add(frameIndex);
                            if (IsTinyFinalMaskFace(face, data.Size, YoloFinalMaskTinyWeakAreaRatio))
                            {
                                tinyWeakRows++;
                                tinyWeakFrames.Add(frameIndex);
                                sampleWindowIssueFrames.Add(frameIndex);
                            }
                        }
                    }

                    if (confidence <= YoloFinalMaskTopEdgeLargeConfidenceMax &&
                        IsTopEdgeLargeFinalMaskFace(face, data.Size))
                    {
                        topEdgeLargeRows++;
                        topEdgeLargeFrames.Add(frameIndex);
                        sampleWindowIssueFrames.Add(frameIndex);
                    }

                    if (confidence <= YoloFinalMaskUpperWeakConfidenceMax &&
                        !touchesEdge &&
                        IsUpperWeakFinalMaskFace(face, data.Size))
                    {
                        upperWeakRows++;
                        upperWeakFrames.Add(frameIndex);
                        sampleWindowIssueFrames.Add(frameIndex);
                    }

                    if (confidence <= YoloFinalMaskLowerWeakConfidenceMax &&
                        !touchesEdge &&
                        IsLowerWeakFinalMaskFace(face, data.Size))
                    {
                        lowerWeakRows++;
                        lowerWeakFrames.Add(frameIndex);
                        sampleWindowIssueFrames.Add(frameIndex);
                    }

                    if (IsAbnormalFinalMaskAspect(face))
                    {
                        aspectBadRows++;
                        aspectBadFrames.Add(frameIndex);
                        sampleWindowIssueFrames.Add(frameIndex);
                    }

                    if (confidence <= YoloFinalMaskTinyShortConfidenceMax &&
                        !touchesEdge &&
                        IsTinyFinalMaskFace(face, data.Size, YoloFinalMaskTinyShortAreaRatio))
                    {
                        tinyShortRows++;
                        tinyShortFrames.Add(frameIndex);
                        sampleWindowIssueFrames.Add(frameIndex);
                    }
                }
            }

            foreach (int frameIndex in protectedSceneCarryFrames)
                sampleWindowIssueFrames.Add(frameIndex);

            var reviewReasons = BuildFinalMaskReviewReasons(
                shortGapCount,
                perFaceShortGapRanges.Count,
                largeJumpGapRanges.Count,
                isolatedFrames.Count,
                lowConfidenceRows,
                weakNonEdgeRows,
                edgeWeakRows,
                topEdgeWeakRows,
                topEdgeLargeRows,
                upperWeakRows,
                lowerWeakRows,
                aspectBadRows,
                tinyWeakRows,
                tinyShortRows,
                protectedSceneCarryFrames.Length,
                sceneCutCarryPairCount,
                sceneCutCarryRemovedCount,
                sceneCutProtectedFrameCount > 0
                    ? sceneCutProtectedFrameCount
                    : protectedSceneCarryFrames.Length);

            Debug.WriteLine(
                $"[FinalMaskSummary] profile=Yolo frames={frames.Length} rows={rows} frameRange={frames[0]}-{frames[^1]} shortGaps={shortGapCount} shortGapRanges={FormatTextList(shortGapRanges)} perFaceShortGaps={perFaceShortGapRanges.Count} perFaceShortGapRanges={FormatTextList(perFaceShortGapRanges)} largeJumpGaps={largeJumpGapRanges.Count} largeJumpRanges={FormatTextList(largeJumpGapRanges)} isolated={isolatedFrames.Count} isolatedFrames={FormatFrameList(isolatedFrames)} lowConf={lowConfidenceRows} lowConfFrames={FormatFrameList(lowConfidenceFrames.OrderBy(static x => x).ToArray())} weakNonEdge={weakNonEdgeRows} weakNonEdgeFrames={FormatFrameList(weakNonEdgeFrames.OrderBy(static x => x).ToArray())} edgeWeak={edgeWeakRows} edgeWeakFrames={FormatFrameList(edgeWeakFrames.OrderBy(static x => x).ToArray())} topEdgeWeak={topEdgeWeakRows} topEdgeWeakFrames={FormatFrameList(topEdgeWeakFrames.OrderBy(static x => x).ToArray())} topEdgeLarge={topEdgeLargeRows} topEdgeLargeFrames={FormatFrameList(topEdgeLargeFrames.OrderBy(static x => x).ToArray())} upperWeak={upperWeakRows} upperWeakFrames={FormatFrameList(upperWeakFrames.OrderBy(static x => x).ToArray())} lowerWeak={lowerWeakRows} lowerWeakFrames={FormatFrameList(lowerWeakFrames.OrderBy(static x => x).ToArray())} aspectBad={aspectBadRows} aspectBadFrames={FormatFrameList(aspectBadFrames.OrderBy(static x => x).ToArray())} tinyWeak={tinyWeakRows} tinyWeakFrames={FormatFrameList(tinyWeakFrames.OrderBy(static x => x).ToArray())} tinyShort={tinyShortRows} tinyShortFrames={FormatFrameList(tinyShortFrames.OrderBy(static x => x).ToArray())} protectedSceneCarry={protectedSceneCarryFrames.Length} protectedSceneCarryFrames={FormatFrameList(protectedSceneCarryFrames)} sceneCutControl=preGuard={sceneCutPreGuardPairCount},preStrong={sceneCutPreStrongProbePairCount},postGuard={sceneCutPostGuardPairCount},postStrong={sceneCutPostStrongProbePairCount},carryPairs={sceneCutCarryPairCount},carryRemoved={sceneCutCarryRemovedCount},carryProtected={sceneCutProtectedFrameCount},carryWindows=pre:{finalSceneCutPreGuardWindowCount},preStrong:{finalSceneCutPreStrongCarryWindowCount},post:{finalSceneCutPostGuardWindowCount},postStrong:{finalSceneCutPostStrongCarryWindowCount},final:{finalSceneCutCarryWindowCount},postGapFill:{finalSceneCutPostGapFillWindowCount},postGapFillCarryPairs={finalSceneCutPostGapFillCarryPairCount},postGapFillRemoved={finalSceneCutPostGapFillCarryRemovedCount},postGapFillProtected={finalSceneCutPostGapFillProtectedFrameCount},postGapFillRemovalRate={FormatRate(finalSceneCutPostGapFillCarryPairCount > 0 ? (double)finalSceneCutPostGapFillCarryRemovedCount / finalSceneCutPostGapFillCarryPairCount : 0.0)},postGapFillProtectedRate={FormatRate(finalSceneCutPostGapFillCarryPairCount > 0 ? (double)finalSceneCutPostGapFillProtectedFrameCount / finalSceneCutPostGapFillCarryPairCount : 0.0)}, reviewRequired={reviewReasons.Count > 0} reviewReasons={FormatTextList(reviewReasons)}");
            string reviewReasonText = FormatTextList(reviewReasons);
            var sampleEntries = entries;
            int sampleWindowLengthBase = Math.Max(
                1,
                _sourceFps > 0
                    ? (int)Math.Ceiling(_sourceFps * YoloQualitySampleWindowSeconds)
                    : (int)YoloQualitySampleWindowSeconds);
            int sampleWindowLength = Math.Min(sampleWindowLengthBase, Math.Max(1, _totalFrames - frames[0]));
            int sampleWindowIssueCandidateCount;
            string sampleWindowStartReason;
            int sampleWindowStart;
            int sampleWindowEnd;
            string sampleWindowTimingSource;
            double sampleWindowStartSeconds;
            double sampleWindowEndSeconds;
            if (TryResolvePtsSampleWindow(
                frames[0],
                _totalFrames - 1,
                sampleWindowIssueFrames,
                out sampleWindowStart,
                out sampleWindowEnd,
                out sampleWindowStartSeconds,
                out sampleWindowEndSeconds,
                out sampleWindowIssueCandidateCount,
                out sampleWindowStartReason))
            {
                sampleWindowLength = Math.Max(1, sampleWindowEnd - sampleWindowStart + 1);
                sampleWindowTimingSource = "pts";
            }
            else
            {
                sampleWindowStart = ResolveSampleWindowStart(
                    frames[0],
                    _totalFrames - 1,
                    sampleWindowLength,
                    sampleWindowIssueFrames,
                    out sampleWindowIssueCandidateCount,
                    out sampleWindowStartReason);
                sampleWindowEnd = Math.Min(_totalFrames - 1, sampleWindowStart + sampleWindowLength - 1);
                sampleWindowTimingSource = "fps-fallback";
                sampleWindowStartSeconds = _sourceFps > 0 ? sampleWindowStart / _sourceFps : 0.0;
                sampleWindowEndSeconds = _sourceFps > 0
                    ? (sampleWindowEnd + 1) / _sourceFps
                    : sampleWindowStartSeconds;
            }
            int sampleWindowFrames = Math.Max(0, sampleWindowEnd - sampleWindowStart + 1);
            double sampleWindowDurationSeconds = Math.Max(0.0, sampleWindowEndSeconds - sampleWindowStartSeconds);
            if (sampleWindowFrames <= 0)
            {
                sampleEntries = Array.Empty<KeyValuePair<int, FrameMaskProvider.FaceMaskData>>();
                sampleWindowFrames = 0;
            }
            else
            {
                sampleEntries = entries.Where(x => x.Key >= sampleWindowStart && x.Key <= sampleWindowEnd).ToArray();
                if (sampleEntries.Count() == 0)
                    sampleEntries = Array.Empty<KeyValuePair<int, FrameMaskProvider.FaceMaskData>>();
            }

            int sampleFrameCount = sampleEntries.Length;
            int sampleRows = sampleEntries.Sum(static x => x.Value.Faces.Count);
            Debug.WriteLine($"[FinalMaskSampleWindow] profile=Yolo candidateIssueFrames={sampleWindowIssueFrames.Count} selectedIssueFrames={sampleWindowIssueCandidateCount} reason={sampleWindowStartReason} timing={sampleWindowTimingSource} totalFrames={_totalFrames} baseFirstFrame={frames[0]} baseLastFrame={frames[^1]} windowLength={sampleWindowLength} start={sampleWindowStart} end={sampleWindowEnd} startSec={sampleWindowStartSeconds:0.###} endSec={sampleWindowEndSeconds:0.###} durationSec={sampleWindowDurationSeconds:0.###} entriesInWindow={sampleEntries.Length} sampleWindowRows={sampleRows}");
            var sampleShortGapRanges = new List<string>();
            var sampleShortGapFrames = sampleEntries.Select(static x => x.Key).ToArray();
            int sampleShortGapCount = 0;
            for (int i = 1; i < sampleShortGapFrames.Length; i++)
            {
                int missing = sampleShortGapFrames[i] - sampleShortGapFrames[i - 1] - 1;
                if (missing <= 0 || missing > FinalMaskShortGapMaxFrames)
                    continue;
                sampleShortGapCount++;
                string range = FormatFrameRange(sampleShortGapFrames[i - 1] + 1, sampleShortGapFrames[i] - 1);
                sampleShortGapRanges.Add(range);
            }
            var samplePerFaceShortGapRanges = FindPerFaceShortGapRanges(sampleEntries);
            int sampleIsolatedFrames = 0;
            for (int i = 0; i < sampleShortGapFrames.Length; i++)
            {
                bool hasPreviousNeighbor = i > 0 && sampleShortGapFrames[i] - sampleShortGapFrames[i - 1] <= 1;
                bool hasNextNeighbor = i < sampleShortGapFrames.Length - 1 && sampleShortGapFrames[i + 1] - sampleShortGapFrames[i] <= 1;
                if (!hasPreviousNeighbor && !hasNextNeighbor)
                    sampleIsolatedFrames++;
            }

            int sampleLargeJumpGapCount = 0;
            var sampleLargeJumpGapRanges = new List<string>();
            for (int i = 1; i < sampleShortGapFrames.Length; i++)
            {
                int sampleMissing = sampleShortGapFrames[i] - sampleShortGapFrames[i - 1] - 1;
                if (sampleMissing <= 0 || sampleMissing > FinalMaskShortGapMaxFrames)
                    continue;

                if (!TryGetBestFinalMaskFace(sampleEntries[i - 1].Value, out var samplePreviousFace) ||
                    !TryGetBestFinalMaskFace(sampleEntries[i].Value, out var sampleNextFace))
                {
                    continue;
                }

                double sampleAreaChange = GetFinalMaskAreaChange(samplePreviousFace, sampleNextFace);
                double sampleCenterShift = GetFinalMaskCenterShift(samplePreviousFace, sampleNextFace);
                if (sampleAreaChange >= FinalMaskLargeJumpAreaChangeRatio ||
                    sampleCenterShift >= FinalMaskLargeJumpCenterShift)
                {
                    sampleLargeJumpGapCount++;
                    string range = FormatFrameRange(sampleShortGapFrames[i - 1] + 1, sampleShortGapFrames[i] - 1);
                    sampleLargeJumpGapRanges.Add(range);
                }
            }

            int sampleLowConfidenceRows = 0;
            int sampleWeakNonEdgeRows = 0;
            int sampleEdgeWeakRows = 0;
            int sampleTopEdgeWeakRows = 0;
            int sampleTopEdgeLargeRows = 0;
            int sampleUpperWeakRows = 0;
            int sampleLowerWeakRows = 0;
            int sampleAspectBadRows = 0;
            int sampleTinyWeakRows = 0;
            int sampleTinyShortRows = 0;
            var sampleProtectedCarryFrames = protectedSceneCarryFrames.Count(x => x >= sampleWindowStart && x <= sampleWindowEnd);
            foreach (var entry in sampleEntries)
            {
                int frameIndex = entry.Key;
                var data = entry.Value;
                for (int i = 0; i < data.Faces.Count; i++)
                {
                    var face = data.Faces[i];
                    float confidence = i < data.Confidences.Count
                        ? data.Confidences[i]
                        : data.MinConfidence ?? 1.0f;
                    if (confidence <= YoloFinalMaskLowConfidenceThreshold)
                        sampleLowConfidenceRows++;

                    bool touchesEdge = TouchesFinalMaskFrameEdge(face, data.Size);
                    if (confidence <= YoloFinalMaskWeakIsolatedConfidenceMax)
                    {
                        if (touchesEdge)
                        {
                            sampleEdgeWeakRows++;
                            if (IsUpperWeakFinalMaskFace(face, data.Size))
                                sampleTopEdgeWeakRows++;
                        }
                        else
                        {
                            sampleWeakNonEdgeRows++;
                            if (IsTinyFinalMaskFace(face, data.Size, YoloFinalMaskTinyWeakAreaRatio))
                                sampleTinyWeakRows++;
                        }
                    }

                    if (confidence <= YoloFinalMaskTopEdgeLargeConfidenceMax &&
                        IsTopEdgeLargeFinalMaskFace(face, data.Size))
                        sampleTopEdgeLargeRows++;

                    if (confidence <= YoloFinalMaskUpperWeakConfidenceMax &&
                        !touchesEdge &&
                        IsUpperWeakFinalMaskFace(face, data.Size))
                        sampleUpperWeakRows++;

                    if (confidence <= YoloFinalMaskLowerWeakConfidenceMax &&
                        !touchesEdge &&
                        IsLowerWeakFinalMaskFace(face, data.Size))
                        sampleLowerWeakRows++;

                    if (IsAbnormalFinalMaskAspect(face))
                        sampleAspectBadRows++;

                    if (confidence <= YoloFinalMaskTinyShortConfidenceMax &&
                        !touchesEdge &&
                        IsTinyFinalMaskFace(face, data.Size, YoloFinalMaskTinyShortAreaRatio))
                        sampleTinyShortRows++;
                }
            }

            var sampleReviewReasons = BuildFinalMaskReviewReasons(
                sampleShortGapCount,
                samplePerFaceShortGapRanges.Count,
                sampleLargeJumpGapCount,
                sampleIsolatedFrames,
                sampleLowConfidenceRows,
                sampleWeakNonEdgeRows,
                sampleEdgeWeakRows,
                sampleTopEdgeWeakRows,
                sampleTopEdgeLargeRows,
                sampleUpperWeakRows,
                sampleLowerWeakRows,
                sampleAspectBadRows,
                sampleTinyWeakRows,
                sampleTinyShortRows,
                protectedSceneCarryFrames.Length,
                sceneCutCarryPairCount,
                sceneCutCarryRemovedCount,
                sceneCutProtectedFrameCount > 0
                    ? sceneCutProtectedFrameCount
                    : protectedSceneCarryFrames.Length);
            int sampleMissRecoveryFillCount = CountFrameIndicesInRange(
                finalMissRecoveryFilledFrameIndices,
                sampleWindowStart,
                sampleWindowEnd);
            int sampleFalsePositiveSuppressionCount = CountFrameIndicesInRange(
                finalFalsePositiveSuppressedFrameIndices,
                sampleWindowStart,
                sampleWindowEnd);
            int sampleOffModeWeakCleanupSuppressionCount = CountFrameIndicesInRange(
                finalOffModeWeakCleanupFrameIndices,
                sampleWindowStart,
                sampleWindowEnd);
            int sampleGapFillBlockedCutGapFrames = CountFrameIndicesInRange(
                finalGapFillBlockedCutGapFrameIndices,
                sampleWindowStart,
                sampleWindowEnd);
            int sampleGapFillBlockedCutGapFramesBeforeCut = CountFrameIndicesInRange(
                finalGapFillBlockedCutGapBeforeCutFrameIndices,
                sampleWindowStart,
                sampleWindowEnd);
            int sampleGapFillBlockedCutGapFramesAfterCut = CountFrameIndicesInRange(
                finalGapFillBlockedCutGapAfterCutFrameIndices,
                sampleWindowStart,
                sampleWindowEnd);
            int sampleGapFillBlockedCleanupGapFrames = CountFrameIndicesInRange(
                finalGapFillBlockedCleanupGapFrameIndices,
                sampleWindowStart,
                sampleWindowEnd);
            int sampleGapFillBlockedSceneCarryGapFrames = CountFrameIndicesInRange(
                finalGapFillBlockedSceneCarryFrameIndices,
                sampleWindowStart,
                sampleWindowEnd);
            string sampleReviewReason = FormatTextList(sampleReviewReasons);

            string evidenceLogLine =
                $"[FinalMaskSummary] profile=Yolo frames={frames.Length} rows={rows} frameRange={frames[0]}-{frames[^1]} shortGaps={shortGapCount} shortGapRanges={FormatTextList(shortGapRanges)} perFaceShortGaps={perFaceShortGapRanges.Count} perFaceShortGapRanges={FormatTextList(perFaceShortGapRanges)} largeJumpGaps={largeJumpGapRanges.Count} largeJumpRanges={FormatTextList(largeJumpGapRanges)} isolated={isolatedFrames.Count} isolatedFrames={FormatFrameList(isolatedFrames)} lowConf={lowConfidenceRows} lowConfFrames={FormatFrameList(lowConfidenceFrames.OrderBy(static x => x).ToArray())} weakNonEdge={weakNonEdgeRows} weakNonEdgeFrames={FormatFrameList(weakNonEdgeFrames.OrderBy(static x => x).ToArray())} edgeWeak={edgeWeakRows} edgeWeakFrames={FormatFrameList(edgeWeakFrames.OrderBy(static x => x).ToArray())} topEdgeWeak={topEdgeWeakRows} topEdgeWeakFrames={FormatFrameList(topEdgeWeakFrames.OrderBy(static x => x).ToArray())} topEdgeLarge={topEdgeLargeRows} topEdgeLargeFrames={FormatFrameList(topEdgeLargeFrames.OrderBy(static x => x).ToArray())} upperWeak={upperWeakRows} upperWeakFrames={FormatFrameList(upperWeakFrames.OrderBy(static x => x).ToArray())} lowerWeak={lowerWeakRows} lowerWeakFrames={FormatFrameList(lowerWeakFrames.OrderBy(static x => x).ToArray())} aspectBad={aspectBadRows} aspectBadFrames={FormatFrameList(aspectBadFrames.OrderBy(static x => x).ToArray())} tinyWeak={tinyWeakRows} tinyWeakFrames={FormatFrameList(tinyWeakFrames.OrderBy(static x => x).ToArray())} tinyShort={tinyShortRows} tinyShortFrames={FormatFrameList(tinyShortFrames.OrderBy(static x => x).ToArray())} protectedSceneCarry={protectedSceneCarryFrames.Length} protectedSceneCarryFrames={FormatFrameList(protectedSceneCarryFrames)} sceneCutControl=preGuard={sceneCutPreGuardPairCount},preStrong={sceneCutPreStrongProbePairCount},postGuard={sceneCutPostGuardPairCount},postStrong={sceneCutPostStrongProbePairCount},carryPairs={sceneCutCarryPairCount},carryRemoved={sceneCutCarryRemovedCount},carryProtected={sceneCutProtectedFrameCount},carryWindows=pre:{finalSceneCutPreGuardWindowCount},preStrong:{finalSceneCutPreStrongCarryWindowCount},post:{finalSceneCutPostGuardWindowCount},postStrong:{finalSceneCutPostStrongCarryWindowCount},final:{finalSceneCutCarryWindowCount},postGapFill:{finalSceneCutPostGapFillWindowCount},postGapFillCarryPairs={finalSceneCutPostGapFillCarryPairCount},postGapFillRemoved={finalSceneCutPostGapFillCarryRemovedCount},postGapFillProtected={finalSceneCutPostGapFillProtectedFrameCount},postGapFillRemovalRate={FormatRate(finalSceneCutPostGapFillCarryPairCount > 0 ? (double)finalSceneCutPostGapFillCarryRemovedCount / finalSceneCutPostGapFillCarryPairCount : 0.0)},postGapFillProtectedRate={FormatRate(finalSceneCutPostGapFillCarryPairCount > 0 ? (double)finalSceneCutPostGapFillProtectedFrameCount / finalSceneCutPostGapFillCarryPairCount : 0.0)}, sceneCutPairSourceBreakdown={finalSceneCutPairSourceBreakdown}, postGapFillPairSourceBreakdown={finalSceneCutPostGapFillPairSourceBreakdown} reviewRequired={reviewReasons.Count > 0} reviewReasons={FormatTextList(reviewReasons)} sampleWindowFrames={sampleWindowFrames} sampleWindowStart={sampleWindowStart} sampleWindowEnd={sampleWindowEnd} sampleWindowTiming={sampleWindowTimingSource} sampleWindowStartSec={sampleWindowStartSeconds:0.###} sampleWindowEndSec={sampleWindowEndSeconds:0.###} sampleWindowDurationSec={sampleWindowDurationSeconds:0.###} sampleFrames={sampleFrameCount} sampleRows={sampleRows} sampleShortGaps={sampleShortGapCount} sampleShortGapRanges={FormatTextList(sampleShortGapRanges)} samplePerFaceShortGaps={samplePerFaceShortGapRanges.Count} samplePerFaceShortGapRanges={FormatTextList(samplePerFaceShortGapRanges)} sampleIsolated={sampleIsolatedFrames} sampleLargeJumps={sampleLargeJumpGapCount} sampleLargeJumpRanges={FormatTextList(sampleLargeJumpGapRanges)} sampleReviewReasons={sampleReviewReason} sampleMissRecovery={sampleMissRecoveryFillCount} sampleFpSuppressed={sampleFalsePositiveSuppressionCount} sampleOffModeWeakCleanupSuppressed={sampleOffModeWeakCleanupSuppressionCount} finalMissRecovery={finalMissRecoveryFillCount} finalFpSuppressed={finalFalsePositiveSuppressedCount} finalOffModeWeakCleanupSuppressed={finalOffModeWeakCleanupCount} finalGapFillRecovered={finalGapFillRecoveredCount} finalGapFillBlocked={finalGapFillBlockedCutGapFrames}/{finalGapFillBlockedCutGapFramesBeforeCut}/{finalGapFillBlockedCutGapFramesAfterCut}/{finalGapFillBlockedCleanupGapFrames}/{finalGapFillBlockedSceneCarryGapFrames} sampleGapFillBlocked={sampleGapFillBlockedCutGapFrames}/{sampleGapFillBlockedCutGapFramesBeforeCut}/{sampleGapFillBlockedCutGapFramesAfterCut}/{sampleGapFillBlockedCleanupGapFrames}/{sampleGapFillBlockedSceneCarryGapFrames}";
            Debug.WriteLine(evidenceLogLine);

            return new AutoMaskPostProcessFinalSummary(
                FinalFrameCount: frames.Length,
                FinalRowCount: rows,
                FinalShortGapCount: shortGapCount,
                FinalPerFaceShortGapCount: perFaceShortGapRanges.Count,
                FinalLargeJumpGapCount: largeJumpGapRanges.Count,
                FinalReviewRequired: reviewReasons.Count > 0,
                FinalReviewReasons: reviewReasonText,
                ProtectedSceneCarryFrameCount: protectedSceneCarryFrames.Length,
                FinalSceneCutPreGuardPairCount: sceneCutPreGuardPairCount,
                FinalSceneCutPreStrongProbePairCount: sceneCutPreStrongProbePairCount,
                FinalSceneCutPostGuardPairCount: sceneCutPostGuardPairCount,
                FinalSceneCutPostStrongProbePairCount: sceneCutPostStrongProbePairCount,
                FinalSceneCutCarryPairCount: sceneCutCarryPairCount,
                FinalSceneCutCarryRemovedCount: sceneCutCarryRemovedCount,
                FinalSceneCutProtectedFrameCount: sceneCutProtectedFrameCount > 0
                    ? sceneCutProtectedFrameCount
                    : protectedSceneCarryFrames.Length,
                FinalMissRecoveryFillCount: finalMissRecoveryFillCount,
                FinalFalsePositiveSuppressedCount: finalFalsePositiveSuppressedCount,
                FinalOffModeWeakCleanupCount: finalOffModeWeakCleanupCount,
                FinalGapFillRecoveredCount: finalGapFillRecoveredCount,
                FinalGapFillBlockedCutGapFrames: finalGapFillBlockedCutGapFrames,
                FinalGapFillBlockedCutGapFramesBeforeCut: finalGapFillBlockedCutGapFramesBeforeCut,
                FinalGapFillBlockedCutGapFramesAfterCut: finalGapFillBlockedCutGapFramesAfterCut,
                FinalGapFillBlockedCleanupGapFrames: finalGapFillBlockedCleanupGapFrames,
                FinalGapFillBlockedSceneCarryGapFrames: finalGapFillBlockedSceneCarryGapFrames,
                FinalGapFillSuppressedWeakGeometryAnchorChecks: finalGapFillSuppressedWeakGeometryAnchorChecks,
                FinalGapFillSuppressedRiskyGeometryAnchorChecks: finalGapFillSuppressedRiskyGeometryAnchorChecks,
                FinalGapFillUnsupportedWeakAnchorChecks: finalGapFillUnsupportedWeakAnchorChecks,
                FinalGapFillCutGuardChecked: finalGapFillCutGuardChecked,
                FinalGapFillCutGuardDirectDifferenceChecks: finalGapFillCutGuardDirectDifferenceChecks,
                FinalGapFillCutGuardDirectDifferenceSkipped: finalGapFillCutGuardDirectDifferenceSkipped,
                FinalSceneCutPreGuardWindowCount: finalSceneCutPreGuardWindowCount,
                FinalSceneCutPreStrongCarryWindowCount: finalSceneCutPreStrongCarryWindowCount,
                FinalSceneCutPostGuardWindowCount: finalSceneCutPostGuardWindowCount,
                FinalSceneCutPostStrongCarryWindowCount: finalSceneCutPostStrongCarryWindowCount,
                FinalSceneCutCarryWindowCount: finalSceneCutCarryWindowCount,
                FinalSceneCutPostGapFillWindowCount: finalSceneCutPostGapFillWindowCount,
                FinalSceneCutPairSourceBreakdown: finalSceneCutPairSourceBreakdown,
                FinalSceneCutPostGapFillPairSourceBreakdown: finalSceneCutPostGapFillPairSourceBreakdown,
                FinalSceneCutPreGuardChecked: finalSceneCutPreGuardChecked,
                FinalSceneCutPreGuardDirectDifferenceChecks: finalSceneCutPreGuardDirectDifferenceChecks,
                FinalSceneCutPreGuardDirectDifferenceSkipped: finalSceneCutPreGuardDirectDifferenceSkipped,
                FinalSceneCutPreStrongCarryProbeChecked: finalSceneCutPreStrongCarryProbeChecked,
                FinalSceneCutPreStrongCarryProbeDirectDifferenceChecks: finalSceneCutPreStrongCarryProbeDirectDifferenceChecks,
                FinalSceneCutPreStrongCarryProbeDirectDifferenceSkipped: finalSceneCutPreStrongCarryProbeDirectDifferenceSkipped,
                FinalSceneCutPostGuardChecked: finalSceneCutPostGuardChecked,
                FinalSceneCutPostGuardDirectDifferenceChecks: finalSceneCutPostGuardDirectDifferenceChecks,
                FinalSceneCutPostGuardDirectDifferenceSkipped: finalSceneCutPostGuardDirectDifferenceSkipped,
                FinalSceneCutPostStrongCarryProbeChecked: finalSceneCutPostStrongCarryProbeChecked,
                FinalSceneCutPostStrongCarryProbeDirectDifferenceChecks: finalSceneCutPostStrongCarryProbeDirectDifferenceChecks,
                FinalSceneCutPostStrongCarryProbeDirectDifferenceSkipped: finalSceneCutPostStrongCarryProbeDirectDifferenceSkipped,
                SampleWindowFrames: sampleWindowFrames,
                SampleFrameCount: sampleFrameCount,
                SampleRowCount: sampleRows,
                SampleShortGapCount: sampleShortGapCount,
                SamplePerFaceShortGapCount: samplePerFaceShortGapRanges.Count,
                SampleIsolatedFrameCount: sampleIsolatedFrames,
                SampleLargeJumpGapCount: sampleLargeJumpGapCount,
                SampleReviewRequired: sampleReviewReasons.Count > 0,
                SampleReviewReasons: sampleReviewReason,
                SampleProtectedSceneCarryFrameCount: sampleProtectedCarryFrames,
                SampleMissRecoveryFillCount: sampleMissRecoveryFillCount,
                SampleFalsePositiveSuppressionCount: sampleFalsePositiveSuppressionCount,
                SampleOffModeWeakCleanupSuppressionCount: sampleOffModeWeakCleanupSuppressionCount,
                SampleGapFillBlockedCutGapFrames: sampleGapFillBlockedCutGapFrames,
                SampleGapFillBlockedCutGapFramesBeforeCut: sampleGapFillBlockedCutGapFramesBeforeCut,
                SampleGapFillBlockedCutGapFramesAfterCut: sampleGapFillBlockedCutGapFramesAfterCut,
                SampleGapFillBlockedCleanupGapFrames: sampleGapFillBlockedCleanupGapFrames,
                SampleGapFillBlockedSceneCarryGapFrames: sampleGapFillBlockedSceneCarryGapFrames,
                SampleWindowIssueFrameCount: sampleWindowIssueFrames.Count,
                SampleWindowIssueCandidateCount: sampleWindowIssueCandidateCount,
                SampleWindowStartReason: sampleWindowStartReason,
                FinalSceneCutPostGapFillCarryPairCount: finalSceneCutPostGapFillCarryPairCount,
                FinalSceneCutPostGapFillCarryRemovedCount: finalSceneCutPostGapFillCarryRemovedCount,
                FinalSceneCutPostGapFillProtectedFrameCount: finalSceneCutPostGapFillProtectedFrameCount)
            {
                SampleWindowStartFrame = sampleWindowStart,
                SampleWindowEndFrame = sampleWindowEnd,
                SampleWindowStartSeconds = sampleWindowStartSeconds,
                SampleWindowEndSeconds = sampleWindowEndSeconds,
                SampleWindowDurationSeconds = sampleWindowDurationSeconds,
                SampleWindowTimingSource = sampleWindowTimingSource,
                EvidenceLogLine = evidenceLogLine
            };
        }

        private static IReadOnlyList<string> BuildFinalMaskReviewReasons(
            int shortGapCount,
            int perFaceShortGapCount,
            int largeJumpGapCount,
            int isolatedCount,
            int lowConfidenceRows,
            int weakNonEdgeRows,
            int edgeWeakRows,
            int topEdgeWeakRows,
            int topEdgeLargeRows,
            int upperWeakRows,
            int lowerWeakRows,
            int aspectBadRows,
            int tinyWeakRows,
            int tinyShortRows,
            int protectedSceneCarryRows = 0,
            int sceneCutCarryPairCount = 0,
            int sceneCutCarryRemovedCount = 0,
            int sceneCutProtectedFrameCount = 0)
        {
            var reasons = new List<string>();
            if (shortGapCount > 0)
                reasons.Add("short-gap");
            if (perFaceShortGapCount > 0)
                reasons.Add("per-face-short-gap");
            if (largeJumpGapCount > 0)
                reasons.Add("large-jump-gap");
            if (isolatedCount > 0)
                reasons.Add("isolated-mask");
            if (lowConfidenceRows > 0)
                reasons.Add("low-confidence-review");
            if (weakNonEdgeRows > 0)
                reasons.Add("weak-non-edge");
            if (edgeWeakRows > 0)
                reasons.Add("edge-weak-review");
            if (topEdgeWeakRows > 0)
                reasons.Add("top-edge-weak-review");
            if (topEdgeLargeRows > 0)
                reasons.Add("top-edge-large-review");
            if (upperWeakRows > 0)
                reasons.Add("upper-weak");
            if (lowerWeakRows > 0)
                reasons.Add("lower-weak");
            if (aspectBadRows > 0)
                reasons.Add("aspect-outlier");
            if (tinyWeakRows > 0)
                reasons.Add("tiny-weak");
            if (tinyShortRows > 0)
                reasons.Add("tiny-short");
            if (protectedSceneCarryRows > 0)
                reasons.Add("scene-carry-protected");
            if (sceneCutCarryPairCount > 0)
                reasons.Add("scene-carry-pairs");
            if (sceneCutCarryRemovedCount > 0)
                reasons.Add("scene-carry-removed");
            if (sceneCutProtectedFrameCount > 0)
                reasons.Add("scene-carry-keep");

            return reasons;
        }

        private static IReadOnlyList<string> FindPerFaceShortGapRanges(
            IReadOnlyList<KeyValuePair<int, FrameMaskProvider.FaceMaskData>> entries)
        {
            if (entries.Count < 2)
                return Array.Empty<string>();

            var byFrame = entries.ToDictionary(static x => x.Key, static x => x.Value);
            var ranges = new SortedSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < entries.Count - 1; i++)
            {
                int startFrame = entries[i].Key;
                var startData = entries[i].Value;
                if (startData.Faces.Count == 0)
                    continue;

                for (int faceIndex = 0; faceIndex < startData.Faces.Count; faceIndex++)
                {
                    var startFace = startData.Faces[faceIndex];
                    for (int j = i + 1; j < entries.Count; j++)
                    {
                        int endFrame = entries[j].Key;
                        int missingFrames = endFrame - startFrame - 1;
                        if (missingFrames <= 0)
                            continue;
                        if (missingFrames > FinalMaskShortGapMaxFrames)
                            break;

                        var endData = entries[j].Value;
                        if (!endData.Faces.Any(endFace => IsSameFinalMaskFace(startFace, endFace)))
                            continue;

                        bool hasIntermediateSameFace = false;
                        for (int frameIndex = startFrame + 1; frameIndex < endFrame; frameIndex++)
                        {
                            if (!byFrame.TryGetValue(frameIndex, out var intermediateData))
                                continue;

                            if (intermediateData.Faces.Any(face => IsSameFinalMaskFace(startFace, face)))
                            {
                                hasIntermediateSameFace = true;
                                break;
                            }
                        }

                        if (!hasIntermediateSameFace)
                            ranges.Add(FormatFrameRange(startFrame + 1, endFrame - 1));
                        break;
                    }
                }
            }

            return ranges.Count == 0 ? Array.Empty<string>() : ranges.ToArray();
        }

        private static bool IsSameFinalMaskFace(Rect first, Rect second)
        {
            double areaRatio = FaceTrackBuilder.GetAreaRatio(first, second);
            if (areaRatio > FinalMaskLargeJumpAreaChangeRatio ||
                areaRatio < 1.0 / FinalMaskLargeJumpAreaChangeRatio)
            {
                return false;
            }

            return FaceTrackBuilder.IoU(first, second) >= YoloSceneCutCandidateMatchMinIou ||
                FaceTrackBuilder.GetNormalizedCenterShift(first, second) <= YoloSceneCutCandidateMatchMaxCenterShiftRatio;
        }

        private static bool TouchesFinalMaskFrameEdge(Rect face, PixelSize size)
        {
            if (size.Width <= 0 || size.Height <= 0)
                return false;

            double marginX = size.Width * YoloFinalMaskEdgeMarginRatio;
            double marginY = size.Height * YoloFinalMaskEdgeMarginRatio;
            return face.X <= marginX ||
                face.Y <= marginY ||
                face.Right >= size.Width - marginX ||
                face.Bottom >= size.Height - marginY;
        }

        private static bool IsTinyFinalMaskFace(Rect face, PixelSize size, double maxAreaRatio)
        {
            if (size.Width <= 0 || size.Height <= 0)
                return false;

            double frameArea = Math.Max(1.0, size.Width * (double)size.Height);
            double areaRatio = Math.Max(0.0, face.Width * face.Height) / frameArea;
            return areaRatio <= maxAreaRatio;
        }

        private static bool IsUpperWeakFinalMaskFace(Rect face, PixelSize size)
        {
            if (size.Width <= 0 || size.Height <= 0)
                return false;

            double frameArea = Math.Max(1.0, size.Width * (double)size.Height);
            double areaRatio = Math.Max(0.0, face.Width * face.Height) / frameArea;
            double centerYRatio = (face.Y + face.Height * 0.5) / size.Height;
            return centerYRatio <= YoloFinalMaskUpperWeakCenterYRatio &&
                areaRatio <= YoloFinalMaskUpperWeakAreaRatio;
        }

        private static bool IsTopEdgeLargeFinalMaskFace(Rect face, PixelSize size)
        {
            if (size.Width <= 0 || size.Height <= 0)
                return false;

            double frameArea = Math.Max(1.0, size.Width * (double)size.Height);
            double areaRatio = Math.Max(0.0, face.Width * face.Height) / frameArea;
            double centerYRatio = (face.Y + face.Height * 0.5) / size.Height;
            return face.Y <= size.Height * YoloFinalMaskEdgeMarginRatio &&
                centerYRatio <= YoloFinalMaskTopEdgeLargeCenterYRatio &&
                areaRatio >= YoloFinalMaskTopEdgeLargeMinAreaRatio &&
                areaRatio <= YoloFinalMaskTopEdgeLargeMaxAreaRatio;
        }

        private static bool IsLowerWeakFinalMaskFace(Rect face, PixelSize size)
        {
            if (size.Width <= 0 || size.Height <= 0)
                return false;

            double frameArea = Math.Max(1.0, size.Width * (double)size.Height);
            double areaRatio = Math.Max(0.0, face.Width * face.Height) / frameArea;
            double centerYRatio = (face.Y + face.Height * 0.5) / size.Height;
            return centerYRatio >= YoloFinalMaskLowerWeakCenterYRatio &&
                areaRatio >= YoloFinalMaskLowerWeakMinAreaRatio &&
                areaRatio <= YoloFinalMaskLowerWeakMaxAreaRatio;
        }

        private static bool IsAbnormalFinalMaskAspect(Rect face)
        {
            if (face.Width <= 0 || face.Height <= 0)
                return false;

            double aspectRatio = face.Width / face.Height;
            return aspectRatio < YoloFinalMaskMinAspectRatio ||
                aspectRatio > YoloFinalMaskMaxAspectRatio;
        }

        private static bool TryGetBestFinalMaskFace(
            FrameMaskProvider.FaceMaskData data,
            out (double CenterX, double CenterY, double AreaRatio, double Confidence) evidence)
        {
            evidence = default;
            if (data.Faces.Count == 0 || data.Size.Width <= 0 || data.Size.Height <= 0)
                return false;

            double frameArea = Math.Max(1.0, data.Size.Width * (double)data.Size.Height);
            int bestIndex = 0;
            double bestConfidence = double.NegativeInfinity;
            for (int i = 0; i < data.Faces.Count; i++)
            {
                double confidence = i < data.Confidences.Count
                    ? data.Confidences[i]
                    : data.MinConfidence ?? 1.0f;
                if (confidence > bestConfidence)
                {
                    bestConfidence = confidence;
                    bestIndex = i;
                }
            }

            var face = data.Faces[bestIndex];
            evidence = (
                (face.X + face.Width * 0.5) / data.Size.Width,
                (face.Y + face.Height * 0.5) / data.Size.Height,
                Math.Max(0.0, face.Width * face.Height) / frameArea,
                bestConfidence);
            return true;
        }

        private static double GetFinalMaskAreaChange(
            (double CenterX, double CenterY, double AreaRatio, double Confidence) previous,
            (double CenterX, double CenterY, double AreaRatio, double Confidence) next)
        {
            double a = Math.Max(0.000001, previous.AreaRatio);
            double b = Math.Max(0.000001, next.AreaRatio);
            return Math.Max(a / b, b / a);
        }

        private static double GetFinalMaskCenterShift(
            (double CenterX, double CenterY, double AreaRatio, double Confidence) previous,
            (double CenterX, double CenterY, double AreaRatio, double Confidence) next)
        {
            double dx = next.CenterX - previous.CenterX;
            double dy = next.CenterY - previous.CenterY;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static string FormatFrameRange(int start, int end)
        {
            return start == end
                ? start.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{start}-{end}");
        }

        private static string FormatRate(double value)
        {
            return value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string FormatFrameList(IReadOnlyList<int> frames)
        {
            if (frames.Count == 0)
                return "none";

            const int maxFrames = 16;
            var selected = frames
                .Take(maxFrames)
                .Select(static x => x.ToString(System.Globalization.CultureInfo.InvariantCulture));
            string text = string.Join(",", selected);
            return frames.Count > maxFrames
                ? $"{text},+{frames.Count - maxFrames}"
                : text;
        }

        private static IReadOnlyList<int> CombineFrameIndices(params IReadOnlyCollection<int>?[] sources)
        {
            if (sources.Length == 0 || sources.All(static x => x == null || x.Count == 0))
                return Array.Empty<int>();

            return sources
                .Where(static x => x != null && x.Count > 0)
                .SelectMany(static x => x!)
                .Distinct()
                .OrderBy(static frame => frame)
                .ToArray();
        }

        private static IReadOnlyList<FaceTrackFilledFace> CombineFaceInfos(
            params IReadOnlyCollection<FaceTrackFilledFace>?[] sources)
        {
            if (sources.Length == 0 || sources.All(static x => x == null || x.Count == 0))
                return Array.Empty<FaceTrackFilledFace>();

            return sources
                .Where(static x => x != null && x.Count > 0)
                .SelectMany(static x => x!)
                .Distinct()
                .OrderBy(static x => x.FrameIndex)
                .ToArray();
        }

        private static IReadOnlyList<string> CombineCutFramePairs(params IReadOnlyCollection<string>?[] sources)
        {
            if (sources.Length == 0 || sources.All(static x => x == null || x.Count == 0))
                return Array.Empty<string>();

            return sources
                .Where(static x => x != null && x.Count > 0)
                .SelectMany(static x => x!)
                .Select(NormalizeCutFramePair)
                .Where(static x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        private static void AddFrameIndices(
            ICollection<int> destination,
            IReadOnlyCollection<int>? frameIndices)
        {
            if (destination == null || frameIndices == null || frameIndices.Count == 0)
                return;

            foreach (int frameIndex in frameIndices)
                destination.Add(frameIndex);
        }

        private static void AddFrameRangeIssues(
            ICollection<int> destination,
            IReadOnlyCollection<string>? frameRanges)
        {
            if (destination == null || frameRanges == null || frameRanges.Count == 0)
                return;

            foreach (string rangeText in frameRanges)
            {
                if (TryParseFrameRange(rangeText, out int start, out int end))
                {
                    destination.Add(start);
                    destination.Add(end);
                }
            }
        }

        private static int ResolveSampleWindowStart(
            int firstFrame,
            int lastFrame,
            int sampleWindowLength,
            IReadOnlyCollection<int> issueFrames,
            out int issueCandidateCount,
            out string sampleWindowStartReason)
        {
            issueCandidateCount = 0;
            sampleWindowStartReason = "fallback:unknown";
            if (firstFrame < 0 || sampleWindowLength <= 0 || firstFrame > lastFrame)
            {
                sampleWindowStartReason = $"fallback:invalidInput firstFrame={firstFrame},lastFrame={lastFrame},windowLength={sampleWindowLength}";
                return Math.Max(0, firstFrame);
            }

            if (issueFrames.Count == 0)
            {
                sampleWindowStartReason = $"fallback:noIssueFrames firstFrame={firstFrame},lastFrame={lastFrame},windowLength={sampleWindowLength}";
                return firstFrame;
            }

            var orderedIssueFrames = issueFrames
                .Distinct()
                .Where(frame => frame >= firstFrame && frame <= lastFrame)
                .OrderBy(static frame => frame)
                .ToArray();
            issueCandidateCount = orderedIssueFrames.Length;

            if (issueCandidateCount == 0)
            {
                sampleWindowStartReason = $"fallback:noIssueFramesInRange issueFrames={issueFrames.Count},firstFrame={firstFrame},lastFrame={lastFrame}";
                return firstFrame;
            }

            int selectedIndex = issueCandidateCount / 2;
            int targetFrame = orderedIssueFrames[selectedIndex];
            int halfWindow = Math.Max(0, sampleWindowLength - 1) / 2;
            int maxStart = lastFrame - sampleWindowLength + 1;
            if (maxStart < firstFrame)
                maxStart = firstFrame;
            int selectedStart = targetFrame - halfWindow;
            if (selectedStart < firstFrame)
            {
                sampleWindowStartReason = $"clampedToFirstFrame firstFrame={firstFrame},targetFrame={targetFrame},issueIndex={selectedIndex}/{issueCandidateCount - 1},windowLength={sampleWindowLength}";
                return firstFrame;
            }

            if (selectedStart > maxStart)
            {
                sampleWindowStartReason = $"clampedToMaxStart lastFrame={lastFrame},maxStart={maxStart},targetFrame={targetFrame},issueIndex={selectedIndex}/{issueCandidateCount - 1},windowLength={sampleWindowLength}";
                return maxStart;
            }

            sampleWindowStartReason = $"medianIssue frame={targetFrame} index={selectedIndex}/{issueCandidateCount - 1} windowLength={sampleWindowLength}";
            return selectedStart;
        }

        private bool TryResolvePtsSampleWindow(
            int firstFrame,
            int lastFrame,
            IReadOnlyCollection<int> issueFrames,
            out int startFrame,
            out int endFrame,
            out double startSeconds,
            out double endSeconds,
            out int issueCandidateCount,
            out string startReason)
        {
            startFrame = Math.Max(0, firstFrame);
            endFrame = startFrame;
            startSeconds = 0.0;
            endSeconds = 0.0;
            issueCandidateCount = 0;
            startReason = "fallback:ptsUnavailable";

            if (!_frameTimings.TryGetValue(0, out var timelineStart) ||
                timelineStart.Source != FrameTimingSource.PresentationTimestamp ||
                !double.IsFinite(timelineStart.TimestampSeconds) ||
                !_frameTimings.TryGetValue(lastFrame, out var timelineEnd) ||
                timelineEnd.Source != FrameTimingSource.PresentationTimestamp ||
                !double.IsFinite(timelineEnd.TimestampSeconds))
            {
                startReason = "fallback:ptsTimelineIncomplete";
                return false;
            }

            var ptsFrames = _frameTimings
                .Where(entry => entry.Key >= firstFrame &&
                    entry.Key <= lastFrame &&
                    entry.Value.Source == FrameTimingSource.PresentationTimestamp &&
                    double.IsFinite(entry.Value.TimestampSeconds))
                .OrderBy(static entry => entry.Key)
                .ToArray();
            if (ptsFrames.Length == 0)
                return false;

            int firstAvailableFrame = ptsFrames[0].Key;
            int lastAvailableFrame = ptsFrames[^1].Key;
            double previousTimestamp = ptsFrames[0].Value.TimestampSeconds;
            var positiveDeltas = new List<double>();
            for (int frameIndex = firstAvailableFrame; frameIndex <= lastAvailableFrame; frameIndex++)
            {
                if (!_frameTimings.TryGetValue(frameIndex, out var timing) ||
                    timing.Source != FrameTimingSource.PresentationTimestamp ||
                    !double.IsFinite(timing.TimestampSeconds) ||
                    timing.TimestampSeconds < previousTimestamp)
                {
                    return false;
                }

                double delta = timing.TimestampSeconds - previousTimestamp;
                if (delta > 0)
                    positiveDeltas.Add(delta);
                previousTimestamp = timing.TimestampSeconds;
            }

            double frameDurationSeconds = _sourceFps > 0 ? 1.0 / _sourceFps : 1.0 / 30.0;
            if (positiveDeltas.Count > 0)
            {
                positiveDeltas.Sort();
                frameDurationSeconds = positiveDeltas[positiveDeltas.Count / 2];
            }

            var orderedIssueFrames = issueFrames
                .Distinct()
                .Where(frame => frame >= firstAvailableFrame && frame <= lastAvailableFrame)
                .OrderBy(static frame => frame)
                .ToArray();
            issueCandidateCount = orderedIssueFrames.Length;

            double firstTimestamp = ptsFrames[0].Value.TimestampSeconds;
            double lastTimestamp = ptsFrames[^1].Value.TimestampSeconds;
            double videoEndExclusive = lastTimestamp + Math.Max(0.000001, frameDurationSeconds);
            double desiredStartSeconds = firstTimestamp;
            int targetFrame = firstAvailableFrame;
            if (orderedIssueFrames.Length > 0)
            {
                targetFrame = orderedIssueFrames[orderedIssueFrames.Length / 2];
                if (!_frameTimings.TryGetValue(targetFrame, out var targetTiming))
                    return false;
                desiredStartSeconds = targetTiming.TimestampSeconds - YoloQualitySampleWindowSeconds * 0.5;
            }

            double maxStartSeconds = Math.Max(firstTimestamp, videoEndExclusive - YoloQualitySampleWindowSeconds);
            desiredStartSeconds = Math.Clamp(desiredStartSeconds, firstTimestamp, maxStartSeconds);
            int selectedStartIndex = Array.FindIndex(
                ptsFrames,
                entry => entry.Value.TimestampSeconds >= desiredStartSeconds);
            var selectedStart = selectedStartIndex >= 0
                ? ptsFrames[selectedStartIndex]
                : ptsFrames[^1];

            startFrame = selectedStart.Key;
            startSeconds = selectedStart.Value.TimestampSeconds;
            endSeconds = Math.Min(startSeconds + YoloQualitySampleWindowSeconds, videoEndExclusive);
            endFrame = startFrame;
            foreach (var entry in ptsFrames)
            {
                if (entry.Key < startFrame)
                    continue;
                if (entry.Value.TimestampSeconds >= endSeconds)
                    break;
                endFrame = entry.Key;
            }

            startReason = orderedIssueFrames.Length > 0
                ? $"medianIssuePts frame={targetFrame} candidates={orderedIssueFrames.Length}"
                : $"fallback:noIssueFramesPts firstFrame={firstAvailableFrame}";
            return endFrame >= startFrame && endSeconds > startSeconds;
        }

        private static bool TryParseFrameRange(string? rangeText, out int startFrame, out int endFrame)
        {
            startFrame = 0;
            endFrame = 0;

            if (string.IsNullOrWhiteSpace(rangeText))
                return false;

            int separatorIndex = rangeText.IndexOf('-');
            if (separatorIndex <= 0)
            {
                if (!int.TryParse(rangeText, out int frame))
                    return false;

                startFrame = frame;
                endFrame = frame;
                return true;
            }

            if (!int.TryParse(rangeText.AsSpan(0, separatorIndex), out startFrame))
                return false;

            if (!int.TryParse(rangeText.AsSpan(separatorIndex + 1), out endFrame))
                return false;

            return true;
        }

        private static void AddFrameIndices(
            ICollection<int> destination,
            IReadOnlyCollection<FaceTrackFilledFace>? faceInfos)
        {
            if (destination == null || faceInfos == null || faceInfos.Count == 0)
                return;

            foreach (var faceInfo in faceInfos)
                destination.Add(faceInfo.FrameIndex);
        }

        private static int CountFrameIndicesInRange(
            IReadOnlyCollection<int>? frameIndices,
            int rangeStart,
            int rangeEnd)
        {
            if (frameIndices == null || frameIndices.Count == 0 || rangeEnd < rangeStart)
                return 0;

            int count = 0;
            foreach (int frameIndex in frameIndices)
            {
                if (frameIndex >= rangeStart && frameIndex <= rangeEnd)
                    count++;
            }

            return count;
        }

        private static string FormatTextList(IReadOnlyList<string> values)
        {
            if (values.Count == 0)
                return "none";

            const int maxValues = 16;
            string text = string.Join(",", values.Take(maxValues));
            return values.Count > maxValues
                ? $"{text},+{values.Count - maxValues}"
                : text;
        }

        private static string BuildSceneCutPairSourceBreakdown(
            IReadOnlyCollection<string> finalCutPairs,
            IReadOnlyCollection<string>? preCutPairs,
            IReadOnlyCollection<string>? preStrongCutPairs,
            IReadOnlyCollection<string>? postCutPairs,
            IReadOnlyCollection<string>? postStrongCutPairs)
        {
            if (finalCutPairs == null || finalCutPairs.Count == 0)
            {
                return "preCutOnly=0,preStrongOnly=0,postCutOnly=0,postStrongOnly=0,shared=0,pairOrphans=0";
            }

            var preCutSet = new HashSet<string>(preCutPairs?
                .Select(NormalizeCutFramePair)
                .Where(static pair => !string.IsNullOrWhiteSpace(pair))
                ?? Array.Empty<string>());
            var preStrongCutSet = new HashSet<string>(preStrongCutPairs?
                .Select(NormalizeCutFramePair)
                .Where(static pair => !string.IsNullOrWhiteSpace(pair))
                ?? Array.Empty<string>());
            var postCutSet = new HashSet<string>(postCutPairs?
                .Select(NormalizeCutFramePair)
                .Where(static pair => !string.IsNullOrWhiteSpace(pair))
                ?? Array.Empty<string>());
            var postStrongCutSet = new HashSet<string>(postStrongCutPairs?
                .Select(NormalizeCutFramePair)
                .Where(static pair => !string.IsNullOrWhiteSpace(pair))
                ?? Array.Empty<string>());
            finalCutPairs = finalCutPairs
                .Select(NormalizeCutFramePair)
                .Where(static pair => !string.IsNullOrWhiteSpace(pair))
                .ToArray();

            int preCutOnly = 0;
            int preStrongOnly = 0;
            int postCutOnly = 0;
            int postStrongOnly = 0;
            int shared = 0;
            int pairOrphans = 0;

            foreach (var pair in finalCutPairs)
            {
                bool fromPreCut = preCutSet.Contains(pair);
                bool fromPreStrongCut = preStrongCutSet.Contains(pair);
                bool fromPostCut = postCutSet.Contains(pair);
                bool fromPostStrongCut = postStrongCutSet.Contains(pair);

                int sourceCount = 0;
                if (fromPreCut) sourceCount++;
                if (fromPreStrongCut) sourceCount++;
                if (fromPostCut) sourceCount++;
                if (fromPostStrongCut) sourceCount++;

                if (sourceCount == 0)
                {
                    pairOrphans++;
                }
                else if (sourceCount > 1)
                {
                    shared++;
                }
                else if (fromPreCut)
                {
                    preCutOnly++;
                }
                else if (fromPreStrongCut)
                {
                    preStrongOnly++;
                }
                else if (fromPostCut)
                {
                    postCutOnly++;
                }
                else
                {
                    postStrongOnly++;
                }
            }

            return $"preCutOnly={preCutOnly},preStrongOnly={preStrongOnly},postCutOnly={postCutOnly},postStrongOnly={postStrongOnly},shared={shared},pairOrphans={pairOrphans}";
        }

        private static IReadOnlyList<string> BuildCutPairWindowRanges(
            IReadOnlyCollection<string>? framePairs,
            int windowFrames)
        {
            if (framePairs == null || framePairs.Count == 0 || windowFrames <= 0)
                return Array.Empty<string>();

            return framePairs
                .Select(pair => TryParseCutFramePair(pair, out int sourceFrame, out int targetFrame)
                    ? FormatFrameRange(Math.Max(0, sourceFrame - windowFrames), targetFrame + windowFrames)
                    : string.Empty)
                .Where(range => !string.IsNullOrWhiteSpace(range))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static range => range)
                .ToArray();
        }

        private static bool TryParseCutFramePair(string? pairText, out int sourceFrame, out int targetFrame)
        {
            sourceFrame = 0;
            targetFrame = 0;

            if (string.IsNullOrWhiteSpace(pairText))
                return false;

            pairText = pairText.Trim();
            int separatorIndex = pairText.IndexOf("->", StringComparison.Ordinal);
            if (separatorIndex >= 0)
            {
                if (!int.TryParse(pairText.AsSpan(0, separatorIndex), out sourceFrame))
                    return false;

                if (!int.TryParse(pairText.AsSpan(separatorIndex + 2), out targetFrame))
                    return false;

                return true;
            }

            separatorIndex = pairText.IndexOf(':');
            if (separatorIndex <= 0 || separatorIndex >= pairText.Length - 1)
                return false;

            if (!int.TryParse(pairText.AsSpan(0, separatorIndex), out sourceFrame))
                return false;

            if (!int.TryParse(pairText.AsSpan(separatorIndex + 1), out targetFrame))
                return false;

            return true;
        }

        private static string NormalizeCutFramePair(string? pairText)
        {
            if (TryParseCutFramePair(pairText, out int sourceFrame, out int targetFrame))
                return $"{sourceFrame}->{targetFrame}";

            return string.Empty;
        }

        private static IReadOnlyList<string> NormalizeCutFramePairs(IReadOnlyCollection<string>? framePairs)
        {
            if (framePairs == null || framePairs.Count == 0)
                return Array.Empty<string>();

            return framePairs
                .Select(NormalizeCutFramePair)
                .Where(static pair => !string.IsNullOrWhiteSpace(pair))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        private readonly record struct YoloFinalMaskCleanupPassResult(
            IReadOnlyList<int> RemovedFrameIndices,
            IReadOnlyList<FaceTrackFilledFace> RemovedFacesInfo,
            IReadOnlyList<string> CutFramePairs,
            YoloGapFillRunSummary GapFillSummary)
        {
            public static YoloFinalMaskCleanupPassResult Empty { get; } = new(
                Array.Empty<int>(),
                Array.Empty<FaceTrackFilledFace>(),
                Array.Empty<string>(),
                YoloGapFillRunSummary.Empty);
        }

        private readonly record struct YoloGapFillRunSummary(
            int FilledFaces,
            IReadOnlyList<int> FilledFrameIndices,
            int GapCandidateFaces,
            int BlockedCutGapFrames,
            int BlockedCutGapBeforeCutFrames,
            int BlockedCutGapAfterCutFrames,
            IReadOnlyList<int> BlockedCutGapFrameIndices,
            IReadOnlyList<int> BlockedCutGapBeforeCutFrameIndices,
            IReadOnlyList<int> BlockedCutGapAfterCutFrameIndices,
            int BlockedCleanupGapFrames,
            IReadOnlyList<int> BlockedCleanupGapFrameIndices,
            int BlockedSceneCarryGapFrames,
            IReadOnlyList<int> BlockedSceneCarryFrameIndices,
            int SuppressedWeakGeometryAnchorChecks,
            int SuppressedRiskyGeometryAnchorChecks,
            int UnsupportedWeakAnchorChecks,
            int CutGuardChecked,
            int CutGuardDirectDifferenceChecks,
            int CutGuardDirectDifferenceSkipped,
            IReadOnlyList<string> CutFramePairs)
        {
            public static YoloGapFillRunSummary Empty { get; } = new(
                FilledFaces: 0,
                FilledFrameIndices: Array.Empty<int>(),
                GapCandidateFaces: 0,
                BlockedCutGapFrames: 0,
                BlockedCutGapBeforeCutFrames: 0,
                BlockedCutGapAfterCutFrames: 0,
                BlockedCutGapFrameIndices: Array.Empty<int>(),
                BlockedCutGapBeforeCutFrameIndices: Array.Empty<int>(),
                BlockedCutGapAfterCutFrameIndices: Array.Empty<int>(),
                BlockedCleanupGapFrames: 0,
                BlockedCleanupGapFrameIndices: Array.Empty<int>(),
                BlockedSceneCarryGapFrames: 0,
                BlockedSceneCarryFrameIndices: Array.Empty<int>(),
                SuppressedWeakGeometryAnchorChecks: 0,
                SuppressedRiskyGeometryAnchorChecks: 0,
                UnsupportedWeakAnchorChecks: 0,
                CutGuardChecked: 0,
                CutGuardDirectDifferenceChecks: 0,
                CutGuardDirectDifferenceSkipped: 0,
                CutFramePairs: Array.Empty<string>());
        }
    }

    public sealed record AutoMaskPostProcessResult(
        FaceTrackPostProcessResult TrackPostProcess,
        IReadOnlyList<int> ProtectedSceneCarryFrameIndices,
        AutoMaskPostProcessFinalSummary FinalSummary,
        int PostProcessElapsedMs)
    {
        public static AutoMaskPostProcessResult Empty { get; } = new(
            FaceTrackPostProcessResult.Empty,
            Array.Empty<int>(),
            AutoMaskPostProcessFinalSummary.Empty,
            0);
    }

    public sealed record AutoMaskPostProcessFinalSummary(
        int FinalFrameCount,
        int FinalRowCount,
        int FinalShortGapCount,
        int FinalPerFaceShortGapCount,
        int FinalLargeJumpGapCount,
        bool FinalReviewRequired,
        string FinalReviewReasons,
        int ProtectedSceneCarryFrameCount,
        int FinalSceneCutPreGuardPairCount,
        int FinalSceneCutPreStrongProbePairCount,
        int FinalSceneCutPostGuardPairCount,
        int FinalSceneCutPostStrongProbePairCount,
        int FinalSceneCutCarryPairCount,
        int FinalSceneCutCarryRemovedCount,
        int FinalSceneCutProtectedFrameCount,
        int FinalMissRecoveryFillCount,
        int FinalFalsePositiveSuppressedCount,
        int FinalOffModeWeakCleanupCount,
        int FinalGapFillRecoveredCount,
        int FinalGapFillBlockedCutGapFrames,
        int FinalGapFillBlockedCutGapFramesBeforeCut,
        int FinalGapFillBlockedCutGapFramesAfterCut,
        int FinalGapFillBlockedCleanupGapFrames,
        int FinalGapFillBlockedSceneCarryGapFrames,
        int FinalGapFillSuppressedWeakGeometryAnchorChecks,
        int FinalGapFillSuppressedRiskyGeometryAnchorChecks,
        int FinalGapFillUnsupportedWeakAnchorChecks,
        int FinalGapFillCutGuardChecked,
        int FinalGapFillCutGuardDirectDifferenceChecks,
        int FinalGapFillCutGuardDirectDifferenceSkipped,
        int FinalSceneCutPreGuardChecked,
        int FinalSceneCutPreGuardDirectDifferenceChecks,
        int FinalSceneCutPreGuardDirectDifferenceSkipped,
        int FinalSceneCutPreStrongCarryProbeChecked,
        int FinalSceneCutPreStrongCarryProbeDirectDifferenceChecks,
        int FinalSceneCutPreStrongCarryProbeDirectDifferenceSkipped,
        int FinalSceneCutPostGuardChecked,
        int FinalSceneCutPostGuardDirectDifferenceChecks,
        int FinalSceneCutPostGuardDirectDifferenceSkipped,
        int FinalSceneCutPostStrongCarryProbeChecked,
        int FinalSceneCutPostStrongCarryProbeDirectDifferenceChecks,
        int FinalSceneCutPostStrongCarryProbeDirectDifferenceSkipped,
        int SampleWindowFrames,
        int SampleFrameCount,
        int SampleRowCount,
        int SampleShortGapCount,
        int SamplePerFaceShortGapCount,
        int SampleIsolatedFrameCount,
        int SampleLargeJumpGapCount,
        bool SampleReviewRequired,
        string SampleReviewReasons,
        int SampleProtectedSceneCarryFrameCount,
        int SampleMissRecoveryFillCount,
        int SampleFalsePositiveSuppressionCount,
        int SampleOffModeWeakCleanupSuppressionCount,
        int SampleGapFillBlockedCutGapFrames,
        int SampleGapFillBlockedCutGapFramesBeforeCut,
        int SampleGapFillBlockedCutGapFramesAfterCut,
        int SampleGapFillBlockedCleanupGapFrames,
        int SampleGapFillBlockedSceneCarryGapFrames,
        int SampleWindowIssueFrameCount,
        int SampleWindowIssueCandidateCount,
        string SampleWindowStartReason,
        int FinalSceneCutPostGapFillCarryPairCount,
        int FinalSceneCutPostGapFillCarryRemovedCount,
        int FinalSceneCutPostGapFillProtectedFrameCount,
        int FinalSceneCutPreGuardWindowCount,
        int FinalSceneCutPreStrongCarryWindowCount,
        int FinalSceneCutPostGuardWindowCount,
        int FinalSceneCutPostStrongCarryWindowCount,
        int FinalSceneCutCarryWindowCount,
        int FinalSceneCutPostGapFillWindowCount,
        string FinalSceneCutPairSourceBreakdown,
        string FinalSceneCutPostGapFillPairSourceBreakdown)
    {
        public int SampleWindowStartFrame { get; init; } = -1;
        public int SampleWindowEndFrame { get; init; } = -1;
        public double SampleWindowStartSeconds { get; init; } = 0.0;
        public double SampleWindowEndSeconds { get; init; } = 0.0;
        public double SampleWindowDurationSeconds { get; init; } = 0.0;
        public string SampleWindowTimingSource { get; init; } = "fps-fallback";
        public string? EvidenceLogLine { get; init; }

        public static AutoMaskPostProcessFinalSummary Empty { get; } = new(
            FinalFrameCount: 0,
            FinalRowCount: 0,
            FinalShortGapCount: 0,
            FinalPerFaceShortGapCount: 0,
            FinalLargeJumpGapCount: 0,
            FinalReviewRequired: false,
            FinalReviewReasons: "none",
            ProtectedSceneCarryFrameCount: 0,
            FinalSceneCutPreGuardPairCount: 0,
            FinalSceneCutPreStrongProbePairCount: 0,
            FinalSceneCutPostGuardPairCount: 0,
            FinalSceneCutPostStrongProbePairCount: 0,
            FinalSceneCutCarryPairCount: 0,
            FinalSceneCutCarryRemovedCount: 0,
            FinalSceneCutProtectedFrameCount: 0,
            FinalMissRecoveryFillCount: 0,
            FinalFalsePositiveSuppressedCount: 0,
            FinalOffModeWeakCleanupCount: 0,
            FinalGapFillRecoveredCount: 0,
            FinalGapFillBlockedCutGapFrames: 0,
            FinalGapFillBlockedCutGapFramesBeforeCut: 0,
            FinalGapFillBlockedCutGapFramesAfterCut: 0,
            FinalGapFillBlockedCleanupGapFrames: 0,
            FinalGapFillBlockedSceneCarryGapFrames: 0,
            FinalGapFillSuppressedWeakGeometryAnchorChecks: 0,
            FinalGapFillSuppressedRiskyGeometryAnchorChecks: 0,
            FinalGapFillUnsupportedWeakAnchorChecks: 0,
            FinalGapFillCutGuardChecked: 0,
            FinalGapFillCutGuardDirectDifferenceChecks: 0,
            FinalGapFillCutGuardDirectDifferenceSkipped: 0,
            FinalSceneCutPreGuardWindowCount: 0,
            FinalSceneCutPreStrongCarryWindowCount: 0,
            FinalSceneCutPostGuardWindowCount: 0,
            FinalSceneCutPostStrongCarryWindowCount: 0,
            FinalSceneCutCarryWindowCount: 0,
            FinalSceneCutPostGapFillWindowCount: 0,
            FinalSceneCutPreGuardChecked: 0,
            FinalSceneCutPreGuardDirectDifferenceChecks: 0,
            FinalSceneCutPreGuardDirectDifferenceSkipped: 0,
            FinalSceneCutPreStrongCarryProbeChecked: 0,
            FinalSceneCutPreStrongCarryProbeDirectDifferenceChecks: 0,
            FinalSceneCutPreStrongCarryProbeDirectDifferenceSkipped: 0,
            FinalSceneCutPostGuardChecked: 0,
            FinalSceneCutPostGuardDirectDifferenceChecks: 0,
            FinalSceneCutPostGuardDirectDifferenceSkipped: 0,
            FinalSceneCutPostStrongCarryProbeChecked: 0,
            FinalSceneCutPostStrongCarryProbeDirectDifferenceChecks: 0,
            FinalSceneCutPostStrongCarryProbeDirectDifferenceSkipped: 0,
            SampleWindowFrames: 0,
            SampleFrameCount: 0,
            SampleRowCount: 0,
            SampleShortGapCount: 0,
            SamplePerFaceShortGapCount: 0,
            SampleIsolatedFrameCount: 0,
            SampleLargeJumpGapCount: 0,
            SampleReviewRequired: false,
            SampleReviewReasons: "none",
            SampleProtectedSceneCarryFrameCount: 0,
            SampleMissRecoveryFillCount: 0,
            SampleFalsePositiveSuppressionCount: 0,
            SampleOffModeWeakCleanupSuppressionCount: 0,
            SampleGapFillBlockedCutGapFrames: 0,
            SampleGapFillBlockedCutGapFramesBeforeCut: 0,
            SampleGapFillBlockedCutGapFramesAfterCut: 0,
            SampleGapFillBlockedCleanupGapFrames: 0,
            SampleGapFillBlockedSceneCarryGapFrames: 0,
            SampleWindowIssueFrameCount: 0,
            SampleWindowIssueCandidateCount: 0,
            SampleWindowStartReason: "none",
            FinalSceneCutPostGapFillCarryPairCount: 0,
            FinalSceneCutPostGapFillCarryRemovedCount: 0,
            FinalSceneCutPostGapFillProtectedFrameCount: 0,
            FinalSceneCutPairSourceBreakdown: "preCutOnly=0,preStrongOnly=0,postCutOnly=0,postStrongOnly=0,shared=0,pairOrphans=0",
            FinalSceneCutPostGapFillPairSourceBreakdown: "preCutOnly=0,preStrongOnly=0,postCutOnly=0,postStrongOnly=0,shared=0,pairOrphans=0");
    }
}
