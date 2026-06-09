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

        private readonly FrameMaskProvider _maskProvider;
        private readonly AutoMaskOptions _options;
        private readonly int _totalFrames;

        public AutoMaskPostProcessPipeline(
            FrameMaskProvider maskProvider,
            AutoMaskOptions options,
            int totalFrames)
        {
            _maskProvider = maskProvider ?? throw new ArgumentNullException(nameof(maskProvider));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _totalFrames = totalFrames;
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
                !_options.EnablePostProcessing;
            bool runYoloPostProcess = _options.FilterProfile == FaceFilterProfile.Yolo &&
                _options.UseTracking &&
                _options.EnablePostProcessing;
            bool runYoloTrackPost = runYoloMissRecovery ||
                (runYoloPostProcess &&
                 (_options.EnableYoloSceneCutCarryCleanup ||
                  _options.EnableYoloTemporalSmoothing ||
                  _options.EnableRoiPostProcess ||
                  _options.EnableYoloWeakIsolatedCleanup));
            Debug.WriteLine(
                $"[AutoMaskPostProcess] start runId={runId} profile={_options.FilterProfile} totalFrames={_totalFrames} tracking={_options.UseTracking} everyN={_options.DetectEveryNFrames} post={enablePostProcessing} roi={_options.EnableRoiPostProcess} weakIso={_options.EnableYoloWeakIsolatedCleanup} gapFill={_options.EnableYoloGapFill} scene={_options.EnableYoloSceneCutCarryCleanup} smooth={_options.EnableYoloTemporalSmoothing} runTrackPost={runYoloTrackPost} runMissRecovery={runYoloMissRecovery}");

            var temporalPostProcessor = new AutoMaskTemporalPostProcessor();
            bool runTrackPost = _options.FilterProfile == FaceFilterProfile.Yolo
                ? runYoloTrackPost
                : _options.UseTracking && enablePostProcessing;
            bool useTrackingForTemporalFixes = _options.FilterProfile == FaceFilterProfile.Yolo
                ? runYoloTrackPost
                : _options.UseTracking && enablePostProcessing;
            var swTrack = Stopwatch.StartNew();
            var trackPost = runTrackPost
                ? temporalPostProcessor.ApplyTemporalFixes(
                    _maskProvider,
                    _totalFrames,
                    _options.FilterProfile,
                    useTrackingForTemporalFixes,
                    missRecoveryOnly: runYoloMissRecovery)
                : FaceTrackPostProcessResult.Empty;
            swTrack.Stop();
            Debug.WriteLine(
                $"[AutoMaskPostProcessTiming] runId={runId} phase=track-post run={runTrackPost} elapsedMs={swTrack.ElapsedMilliseconds} trackCount={trackPost.TrackCount} fillGap={trackPost.FilledGapFaces} fillLost={trackPost.FilledLostFaces} fillInitial={trackPost.FilledInitialFaces}");

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

            IReadOnlyList<string> yoloPreSmoothCutPairs = Array.Empty<string>();
            IReadOnlyList<string> yoloPreSmoothStrongCarryProbeCutPairs = Array.Empty<string>();
            IReadOnlyList<string> yoloPostSmoothCutPairs = Array.Empty<string>();
            IReadOnlyList<string> yoloStrongCarryProbeCutPairs = Array.Empty<string>();
            IReadOnlyList<string> yoloCutPairs = Array.Empty<string>();
            IReadOnlyList<int> yoloSceneCutBlockedFrames = Array.Empty<int>();
            IReadOnlyCollection<FaceTrackFilledFace> yoloSceneCutBlockedFaces = Array.Empty<FaceTrackFilledFace>();
            var yoloSceneCutPostProcessor = new YoloSceneCutPostProcessor();
            var swScenePre = Stopwatch.StartNew();
            bool ranPreSceneGuard = false;

            if (_options.UseTracking && _options.FilterProfile == FaceFilterProfile.Yolo && enableSceneCutCleanup)
            {
                ranPreSceneGuard = true;
                var preSmoothGuard = yoloSceneCutPostProcessor.RemoveTrackFillAcrossSceneCuts(
                    _maskProvider,
                    videoPath,
                    trackPost,
                    cancellationToken,
                    "pre-smooth");
                yoloPreSmoothCutPairs = preSmoothGuard.CutFramePairs;
                var preSmoothStrongCarryProbe = yoloSceneCutPostProcessor.ProbeStrongCarrySceneCuts(
                    _maskProvider,
                    videoPath,
                    cancellationToken,
                    "pre-smooth");
                yoloPreSmoothStrongCarryProbeCutPairs = preSmoothStrongCarryProbe.CutFramePairs;
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
                var postSmoothGuard = yoloSceneCutPostProcessor.RemoveTrackFillAcrossSceneCuts(
                    _maskProvider,
                    videoPath,
                    trackPost,
                    cancellationToken,
                    "post-smooth");
                yoloPostSmoothCutPairs = postSmoothGuard.CutFramePairs;
                var strongCarryProbe = yoloSceneCutPostProcessor.ProbeStrongCarrySceneCuts(
                    _maskProvider,
                    videoPath,
                    cancellationToken,
                    "post-smooth");
                yoloStrongCarryProbeCutPairs = strongCarryProbe.CutFramePairs;
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
            if (_options.FilterProfile == FaceFilterProfile.Yolo && enableSceneCutCleanup)
            {
                var swSceneFinal = Stopwatch.StartNew();
                yoloCutPairs = CombineCutFramePairs(
                    yoloPreSmoothCutPairs,
                    yoloPreSmoothStrongCarryProbeCutPairs,
                    yoloPostSmoothCutPairs,
                    yoloCleanupPass.CutFramePairs,
                    yoloStrongCarryProbeCutPairs);
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
                swSceneFinal.Stop();
                Debug.WriteLine(
                    $"[AutoMaskPostProcessTiming] runId={runId} phase=yolo-scene-cleanup run=true elapsedMs={swSceneFinal.ElapsedMilliseconds} sceneCutPairs={yoloCutPairs.Count} carryRemoved={yoloCarryCleanup.RemovedFaces} postCleanupRemoved={postSceneCleanupPass.RemovedFacesInfo.Count}");
                if (postSceneCleanupPass.CutFramePairs.Count > 0)
                {
                    var postGapFillCutPairs = CombineCutFramePairs(yoloCutPairs, postSceneCleanupPass.CutFramePairs);
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
                    Debug.WriteLine(
                        $"[YoloSceneCutCarryCleanup] stage=post-gap-fill cutPairs={FormatTextList(postGapFillCutPairs)} removed={postGapFillCarryCleanup.RemovedFaces} removedFrames={FormatFrameList(postGapFillCarryCleanup.RemovedFrameIndices)} removedUnsupportedStrong={postGapFillCarryCleanup.RemovedUnsupportedStrongCarryLikeFaces} removedUnsupportedStrongFrames={FormatFrameList(postGapFillCarryCleanup.RemovedUnsupportedStrongCarryLikeFrameIndices)} protectedStrong={postGapFillCarryCleanup.ProtectedStrongCarryLikeFaces} protectedStrongFrames={FormatFrameList(postGapFillCarryCleanup.ProtectedStrongCarryLikeFrameIndices)} blockedFrames={FormatFrameList(postGapFillBlockedFrames)} purgeFrames={YoloSceneCutCarryPurgeFrames} blockFrames={YoloSceneCutCarryBlockFrames} maxConfidence={YoloSceneCutCarryPurgeMaxConfidence:0.###} extendedWeakMaxConfidence={YoloSceneCutExtendedWeakCarryMaxConfidence:0.###}");

                    if (enableWeakIsolationCleanup && enableGapFill)
                    {
                        RemoveYoloWeakIsolatedFinalMasks(
                            videoPath,
                            cancellationToken,
                            fillStableGaps: false,
                            logLabel: "YoloFinalMaskPostGapFillCleanup",
                            logWhenNoRemovals: true);
                    }
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
            if (_options.FilterProfile == FaceFilterProfile.Yolo && enableGapFill && !enableWeakIsolationCleanup)
            {
                FillYoloStableFinalMaskGaps(
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
            }
            swGapFill.Stop();
            Debug.WriteLine(
                $"[AutoMaskPostProcessTiming] runId={runId} phase=yolo-standalone-gap-fill run={ranStandaloneGapFill} elapsedMs={swGapFill.ElapsedMilliseconds} enabled={enableGapFill} weakCleanup={enableWeakIsolationCleanup}");

            swTotal.Stop();
            if (!enablePostProcessing)
            {
                Debug.WriteLine(
                    _options.FilterProfile == FaceFilterProfile.Yolo && runYoloMissRecovery
                        ? "[AutoMaskPostProcess] post-processing disabled · YOLO baseline 보완 모드(추적 기반 미탐 보완)"
                        : "[AutoMaskPostProcess] post-processing disabled");
            }
            Debug.WriteLine(
                $"[AutoMaskPostProcessTiming] runId={runId} phase=total elapsedMs={swTotal.ElapsedMilliseconds} profile={_options.FilterProfile} totalFrames={_totalFrames} detectEveryN={_options.DetectEveryNFrames}");
            var finalSummary = LogFinalMaskSummary(yoloProtectedSceneCarryFrames);
            return new AutoMaskPostProcessResult(
                trackPost,
                yoloProtectedSceneCarryFrames,
                finalSummary);
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
                var cutPairs = Array.Empty<string>();
                if (fillStableGaps)
                    cutPairs = FillYoloStableFinalMaskGaps(
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
                return new YoloFinalMaskCleanupPassResult(cleanup.RemovedFrameIndices, cleanup.RemovedFacesInfo, cutPairs);
            }

            var gapFillCutPairs = Array.Empty<string>();
            if (fillStableGaps)
                gapFillCutPairs = FillYoloStableFinalMaskGaps(
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

            return new YoloFinalMaskCleanupPassResult(cleanup.RemovedFrameIndices, cleanup.RemovedFacesInfo, gapFillCutPairs);
        }

        private string[] FillYoloStableFinalMaskGaps(
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
            string gapFillSceneCutGuardLogLabel)
        {
            var gapFill = postProcessor.FillShortStableGaps(
                _maskProvider,
                new YoloFinalMaskGapFillOptions
                {
                    MaxGapFrames = YoloFinalMaskStableGapMaxFrames,
                    BlockedCutFramePairs = blockedCutFramePairs ?? Array.Empty<string>(),
                    BlockedFrameIndices = blockedFrameIndices ?? Array.Empty<int>(),
                    BlockedFaces = blockedFaces ?? Array.Empty<FaceTrackFilledFace>(),
                    BlockedSceneCarryFaces = sceneCarryBlockedFaces ?? Array.Empty<FaceTrackFilledFace>(),
                    BlockedSceneCarryFrameIndices = sceneCarryBlockedFrameIndices ?? Array.Empty<int>()
                });
            if (gapFill.FilledFaces <= 0 &&
                gapFill.BlockedCutGapFaces <= 0 &&
                gapFill.BlockedCleanupGapFrames <= 0 &&
                gapFill.BlockedSceneCarryGapFrames <= 0 &&
                gapFill.SuppressedWeakGeometryAnchorChecks <= 0 &&
                gapFill.SuppressedRiskyGeometryAnchorChecks <= 0 &&
                gapFill.UnsupportedWeakAnchorChecks <= 0)
            {
                return Array.Empty<string>();
            }

            Debug.WriteLine(
                $"[{gapFillLogLabel}] filled={gapFill.FilledFaces} frames={FormatFrameList(gapFill.FilledFrameIndices)} blockedByCut={gapFill.BlockedCutGapFaces} cutBlockedFrames={FormatFrameList(gapFill.BlockedCutFrameIndices)} blockedByCleanup={gapFill.BlockedCleanupGapFrames} cleanupBlockedFrames={FormatFrameList(gapFill.BlockedCleanupFrameIndices)} blockedBySceneCarry={gapFill.BlockedSceneCarryGapFrames} sceneCarryBlockedFrames={FormatFrameList(gapFill.BlockedSceneCarryFrameIndices)} suppressedWeakGeometryAnchors={gapFill.SuppressedWeakGeometryAnchorChecks} suppressedRiskyGeometryAnchors={gapFill.SuppressedRiskyGeometryAnchorChecks} unsupportedWeakAnchors={gapFill.UnsupportedWeakAnchorChecks}");

            if (gapFill.CutGuardFacesInfo.Count == 0)
                return Array.Empty<string>();

            if (skipSceneCutGuard)
                return gapFill.CutGuardFacesInfo.Select(x => $"{x.SourceFrameIndex}:{x.FrameIndex}").ToArray();

            var guard = new FaceTrackSceneCutGuard().Apply(
                _maskProvider,
                videoPath,
                gapFill.CutGuardFacesInfo,
                differenceThreshold: YoloSceneCutDifferenceThreshold,
                directDifferenceThreshold: YoloSceneCutDirectDifferenceThreshold,
                candidateMatchMinIou: YoloSceneCutCandidateMatchMinIou,
                candidateMatchMaxCenterShiftRatio: YoloSceneCutCandidateMatchMaxCenterShiftRatio,
                candidateMatchMaxAreaChangeRatio: YoloSceneCutCandidateMatchMaxAreaChangeRatio,
                cancellationToken: cancellationToken);

            if (!string.IsNullOrWhiteSpace(guard.Error))
            {
                Debug.WriteLine(
                    $"[{gapFillSceneCutGuardLogLabel}] skipped candidates={gapFill.CutGuardFacesInfo.Count} checked={guard.Checked} checkedPairs={FormatTextList(guard.CheckedFramePairs)} maxDiff={guard.MaxDifference:0.###} cutPairs={FormatTextList(guard.CutFramePairs)} removed={guard.Removed} removedFrames={FormatFrameList(guard.RemovedFrameIndices)} error={guard.Error}");
                return Array.Empty<string>();
            }

            if (guard.Checked > 0)
            {
                Debug.WriteLine(
                    $"[{gapFillSceneCutGuardLogLabel}] candidates={gapFill.CutGuardFacesInfo.Count} checked={guard.Checked} checkedPairs={FormatTextList(guard.CheckedFramePairs)} maxDiff={guard.MaxDifference:0.###} cutPairs={FormatTextList(guard.CutFramePairs)} removed={guard.Removed} removedFrames={FormatFrameList(guard.RemovedFrameIndices)} threshold={guard.Threshold:0.###} elapsedMs={guard.ElapsedMs}");
            }

            return guard.CutFramePairs.ToArray();
        }

        private AutoMaskPostProcessFinalSummary LogFinalMaskSummary(IReadOnlyCollection<int>? protectedSceneCarryFrameIndices = null)
        {
            if (_options.FilterProfile != FaceFilterProfile.Yolo)
                return AutoMaskPostProcessFinalSummary.Empty;

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
                Debug.WriteLine($"[FinalMaskSummary] profile=Yolo frames=0 rows=0 frameRange=none shortGaps=0 shortGapRanges=none perFaceShortGaps=0 perFaceShortGapRanges=none largeJumpGaps=0 largeJumpRanges=none isolated=0 isolatedFrames=none lowConf=0 lowConfFrames=none weakNonEdge=0 weakNonEdgeFrames=none edgeWeak=0 edgeWeakFrames=none topEdgeWeak=0 topEdgeWeakFrames=none topEdgeLarge=0 topEdgeLargeFrames=none upperWeak=0 upperWeakFrames=none lowerWeak=0 lowerWeakFrames=none aspectBad=0 aspectBadFrames=none tinyWeak=0 tinyWeakFrames=none tinyShort=0 tinyShortFrames=none protectedSceneCarry={protectedSceneCarryFrames.Length} protectedSceneCarryFrames={FormatFrameList(protectedSceneCarryFrames)} reviewRequired={emptyReviewReasons.Count > 0} reviewReasons={noneReviewReasons}");
                return new AutoMaskPostProcessFinalSummary(
                    0,
                    0,
                    0,
                    0,
                    0,
                    emptyReviewReasons.Count > 0,
                    noneReviewReasons,
                    protectedSceneCarryFrames.Length);
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

            var isolatedFrames = new List<int>();
            for (int i = 0; i < frames.Length; i++)
            {
                bool hasPreviousNeighbor = i > 0 && frames[i] - frames[i - 1] <= 1;
                bool hasNextNeighbor = i < frames.Length - 1 && frames[i + 1] - frames[i] <= 1;
                if (!hasPreviousNeighbor && !hasNextNeighbor)
                    isolatedFrames.Add(frames[i]);
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
                    }

                    bool touchesEdge = TouchesFinalMaskFrameEdge(face, data.Size);
                    if (confidence <= YoloFinalMaskWeakIsolatedConfidenceMax)
                    {
                        if (touchesEdge)
                        {
                            edgeWeakRows++;
                            edgeWeakFrames.Add(frameIndex);
                            if (IsUpperWeakFinalMaskFace(face, data.Size))
                            {
                                topEdgeWeakRows++;
                                topEdgeWeakFrames.Add(frameIndex);
                            }
                        }
                        else
                        {
                            weakNonEdgeRows++;
                            weakNonEdgeFrames.Add(frameIndex);
                            if (IsTinyFinalMaskFace(face, data.Size, YoloFinalMaskTinyWeakAreaRatio))
                            {
                                tinyWeakRows++;
                                tinyWeakFrames.Add(frameIndex);
                            }
                        }
                    }

                    if (confidence <= YoloFinalMaskTopEdgeLargeConfidenceMax &&
                        IsTopEdgeLargeFinalMaskFace(face, data.Size))
                    {
                        topEdgeLargeRows++;
                        topEdgeLargeFrames.Add(frameIndex);
                    }

                    if (confidence <= YoloFinalMaskUpperWeakConfidenceMax &&
                        !touchesEdge &&
                        IsUpperWeakFinalMaskFace(face, data.Size))
                    {
                        upperWeakRows++;
                        upperWeakFrames.Add(frameIndex);
                    }

                    if (confidence <= YoloFinalMaskLowerWeakConfidenceMax &&
                        !touchesEdge &&
                        IsLowerWeakFinalMaskFace(face, data.Size))
                    {
                        lowerWeakRows++;
                        lowerWeakFrames.Add(frameIndex);
                    }

                    if (IsAbnormalFinalMaskAspect(face))
                    {
                        aspectBadRows++;
                        aspectBadFrames.Add(frameIndex);
                    }

                    if (confidence <= YoloFinalMaskTinyShortConfidenceMax &&
                        !touchesEdge &&
                        IsTinyFinalMaskFace(face, data.Size, YoloFinalMaskTinyShortAreaRatio))
                    {
                        tinyShortRows++;
                        tinyShortFrames.Add(frameIndex);
                    }
                }
            }

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
                protectedSceneCarryFrames.Length);

            Debug.WriteLine(
                $"[FinalMaskSummary] profile=Yolo frames={frames.Length} rows={rows} frameRange={frames[0]}-{frames[^1]} shortGaps={shortGapCount} shortGapRanges={FormatTextList(shortGapRanges)} perFaceShortGaps={perFaceShortGapRanges.Count} perFaceShortGapRanges={FormatTextList(perFaceShortGapRanges)} largeJumpGaps={largeJumpGapRanges.Count} largeJumpRanges={FormatTextList(largeJumpGapRanges)} isolated={isolatedFrames.Count} isolatedFrames={FormatFrameList(isolatedFrames)} lowConf={lowConfidenceRows} lowConfFrames={FormatFrameList(lowConfidenceFrames.OrderBy(static x => x).ToArray())} weakNonEdge={weakNonEdgeRows} weakNonEdgeFrames={FormatFrameList(weakNonEdgeFrames.OrderBy(static x => x).ToArray())} edgeWeak={edgeWeakRows} edgeWeakFrames={FormatFrameList(edgeWeakFrames.OrderBy(static x => x).ToArray())} topEdgeWeak={topEdgeWeakRows} topEdgeWeakFrames={FormatFrameList(topEdgeWeakFrames.OrderBy(static x => x).ToArray())} topEdgeLarge={topEdgeLargeRows} topEdgeLargeFrames={FormatFrameList(topEdgeLargeFrames.OrderBy(static x => x).ToArray())} upperWeak={upperWeakRows} upperWeakFrames={FormatFrameList(upperWeakFrames.OrderBy(static x => x).ToArray())} lowerWeak={lowerWeakRows} lowerWeakFrames={FormatFrameList(lowerWeakFrames.OrderBy(static x => x).ToArray())} aspectBad={aspectBadRows} aspectBadFrames={FormatFrameList(aspectBadFrames.OrderBy(static x => x).ToArray())} tinyWeak={tinyWeakRows} tinyWeakFrames={FormatFrameList(tinyWeakFrames.OrderBy(static x => x).ToArray())} tinyShort={tinyShortRows} tinyShortFrames={FormatFrameList(tinyShortFrames.OrderBy(static x => x).ToArray())} protectedSceneCarry={protectedSceneCarryFrames.Length} protectedSceneCarryFrames={FormatFrameList(protectedSceneCarryFrames)} reviewRequired={reviewReasons.Count > 0} reviewReasons={FormatTextList(reviewReasons)}");
            string reviewReasonText = FormatTextList(reviewReasons);

            return new AutoMaskPostProcessFinalSummary(
                frames.Length,
                rows,
                shortGapCount,
                perFaceShortGapRanges.Count,
                largeJumpGapRanges.Count,
                reviewReasons.Count > 0,
                reviewReasonText,
                protectedSceneCarryFrames.Length);
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
            int protectedSceneCarryRows = 0)
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
                .Where(static x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
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

        private readonly record struct YoloFinalMaskCleanupPassResult(
            IReadOnlyList<int> RemovedFrameIndices,
            IReadOnlyList<FaceTrackFilledFace> RemovedFacesInfo,
            IReadOnlyList<string> CutFramePairs)
        {
            public static YoloFinalMaskCleanupPassResult Empty { get; } = new(Array.Empty<int>(), Array.Empty<FaceTrackFilledFace>(), Array.Empty<string>());
        }
    }

    public sealed record AutoMaskPostProcessResult(
        FaceTrackPostProcessResult TrackPostProcess,
        IReadOnlyList<int> ProtectedSceneCarryFrameIndices,
        AutoMaskPostProcessFinalSummary FinalSummary)
    {
        public static AutoMaskPostProcessResult Empty { get; } = new(
            FaceTrackPostProcessResult.Empty,
            Array.Empty<int>(),
            AutoMaskPostProcessFinalSummary.Empty);
    }

    public sealed record AutoMaskPostProcessFinalSummary(
        int FinalFrameCount,
        int FinalRowCount,
        int FinalShortGapCount,
        int FinalPerFaceShortGapCount,
        int FinalLargeJumpGapCount,
        bool FinalReviewRequired,
        string FinalReviewReasons,
        int ProtectedSceneCarryFrameCount)
    {
        public static AutoMaskPostProcessFinalSummary Empty { get; } = new(
            0,
            0,
            0,
            0,
            0,
            false,
            "none",
            0);
    }
}
