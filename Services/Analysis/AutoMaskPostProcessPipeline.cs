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
        private const float TemporalConfidenceStrong = 0.68f;
        private const float TemporalConfidenceWeak = 0.50f;
        private const double TemporalMaxCenterShiftRatio = 0.55;
        private const double TemporalMaxAreaChangeRatio = 3.2;
        private const double TemporalHoleFillIouMin = 0.12;
        private const double TemporalDuplicateIouMin = 0.35;
        private const double TemporalSmoothIouMin = 0.18;
        private const double TemporalSmoothWeight = 0.42;
        private const int TemporalSmoothPasses = 2;
        private const int TemporalSmoothSearchWindowFrames = 2;
        private const int SuspiciousNoFaceMaxGap = 8;
        private const float YoloFinalMaskLowConfidenceThreshold = 0.38f;
        private const int FinalMaskShortGapMaxFrames = 3;
        private const double FinalMaskLargeJumpAreaChangeRatio = 4.0;
        private const double FinalMaskLargeJumpCenterShift = 0.20;
        private const float YoloFinalMaskWeakIsolatedConfidenceMax = 0.50f;
        private const float YoloSceneCutDirectCarryMaxConfidence = 0.98f;
        private const float YoloSceneCutDirectCarryMinSourceConfidence = 0.58f;
        private const float YoloSceneCutPostCutCarryMaxConfidence = 0.78f;
        private const float YoloSceneCutStrongCarryProbeMaxConfidence = 0.995f;
        private const float YoloSceneCutStrongCarryProbeMinConfidence = 0.78f;
        private const float YoloSceneCutStrongCarryProbeMinSourceConfidence = 0.80f;
        private const double YoloSceneCutDifferenceThreshold = 0.15;
        private const double YoloSceneCutDirectDifferenceThreshold = 0.32;
        private const int YoloSceneCutDirectDifferenceMaxCandidates = 96;
        private const int YoloSceneCutMatchingTailMaxFrames = 5;
        private const float YoloSceneCutMatchingTailMaxConfidence = 0.98f;
        private const double YoloSceneCutCandidateMatchMinIou = 0.55;
        private const double YoloSceneCutCandidateMatchMaxCenterShiftRatio = 0.65;
        private const double YoloSceneCutCandidateMatchMaxAreaChangeRatio = 3.0;
        private const int YoloSceneCutPostCutLookbackFrames = 5;
        private const int YoloSceneCutCarryPurgeFrames = 5;
        private const int YoloSceneCutCarryBlockFrames = 8;
        private const float YoloSceneCutExtendedWeakCarryMaxConfidence = 0.78f;
        private const float YoloSceneCutCarryPurgeMaxConfidence = 0.98f;
        private const int YoloFinalMaskStableGapMaxFrames = 5;
        private const double YoloFinalMaskEdgeMarginRatio = 0.02;
        private const double YoloFinalMaskTinyWeakAreaRatio = 0.0012;
        private const float YoloFinalMaskTinyShortConfidenceMax = 0.62f;
        private const double YoloFinalMaskTinyShortAreaRatio = 0.0009;
        private const float YoloFinalMaskUpperWeakConfidenceMax = 0.60f;
        private const double YoloFinalMaskUpperWeakCenterYRatio = 0.10;
        private const double YoloFinalMaskUpperWeakAreaRatio = 0.0065;
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
            var trackPost = ApplyAutoTemporalFixes();
            if (roiSourceDetector != null && detectorOptions != null)
                RefineAutoFacesWithRoi(videoPath, roiSourceDetector, trackPost, detectorOptions, useFaceOnnxRoiDetector);

            YoloFinalMaskCleanupPassResult yoloCleanupPass = YoloFinalMaskCleanupPassResult.Empty;
            if (_options.FilterProfile == FaceFilterProfile.Yolo)
                yoloCleanupPass = RemoveYoloWeakIsolatedFinalMasks(
                    videoPath,
                    cancellationToken,
                    fillStableGaps: false);

            IReadOnlyList<string> yoloPreSmoothCutPairs = Array.Empty<string>();
            IReadOnlyList<string> yoloPreSmoothStrongCarryProbeCutPairs = Array.Empty<string>();
            IReadOnlyList<string> yoloPostSmoothCutPairs = Array.Empty<string>();
            IReadOnlyList<string> yoloStrongCarryProbeCutPairs = Array.Empty<string>();

            if (_options.UseTracking && _options.FilterProfile == FaceFilterProfile.Yolo)
            {
                var preSmoothGuard = RemoveYoloTrackFillAcrossSceneCuts(videoPath, trackPost, cancellationToken, "pre-smooth");
                yoloPreSmoothCutPairs = preSmoothGuard.CutFramePairs;
                var preSmoothStrongCarryProbe = ProbeYoloStrongCarrySceneCuts(videoPath, cancellationToken, "pre-smooth");
                yoloPreSmoothStrongCarryProbeCutPairs = preSmoothStrongCarryProbe.CutFramePairs;
            }

            if (_options.UseTracking)
            {
                ApplyAutoTemporalSmoothing(_options.FilterProfile == FaceFilterProfile.Yolo
                    ? CombineCutFramePairs(yoloPreSmoothCutPairs, yoloPreSmoothStrongCarryProbeCutPairs)
                    : Array.Empty<string>());
            }

            if (_options.UseTracking && _options.FilterProfile == FaceFilterProfile.Yolo)
            {
                var postSmoothGuard = RemoveYoloTrackFillAcrossSceneCuts(videoPath, trackPost, cancellationToken, "post-smooth");
                yoloPostSmoothCutPairs = postSmoothGuard.CutFramePairs;
                var strongCarryProbe = ProbeYoloStrongCarrySceneCuts(videoPath, cancellationToken, "post-smooth");
                yoloStrongCarryProbeCutPairs = strongCarryProbe.CutFramePairs;
            }

            IReadOnlyList<int> yoloProtectedSceneCarryFrames = Array.Empty<int>();
            if (_options.FilterProfile == FaceFilterProfile.Yolo)
            {
                var yoloCutPairs = CombineCutFramePairs(
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
                var yoloSceneCutBlockedFrames = YoloFinalMaskPostProcessor.BuildSceneCutCarryBlockedFrames(
                    yoloCutPairs,
                    YoloSceneCutCarryBlockFrames);
                yoloProtectedSceneCarryFrames = yoloCarryCleanup.ProtectedStrongCarryLikeFrameIndices;
                if (yoloCutPairs.Count > 0)
                {
                    Debug.WriteLine(
                        $"[YoloSceneCutCarryCleanup] cutPairs={FormatTextList(yoloCutPairs)} removed={yoloCarryCleanup.RemovedFaces} removedFrames={FormatFrameList(yoloCarryCleanup.RemovedFrameIndices)} removedUnsupportedStrong={yoloCarryCleanup.RemovedUnsupportedStrongCarryLikeFaces} removedUnsupportedStrongFrames={FormatFrameList(yoloCarryCleanup.RemovedUnsupportedStrongCarryLikeFrameIndices)} protectedStrong={yoloCarryCleanup.ProtectedStrongCarryLikeFaces} protectedStrongFrames={FormatFrameList(yoloCarryCleanup.ProtectedStrongCarryLikeFrameIndices)} blockedFrames={FormatFrameList(yoloSceneCutBlockedFrames)} purgeFrames={YoloSceneCutCarryPurgeFrames} blockFrames={YoloSceneCutCarryBlockFrames} maxConfidence={YoloSceneCutCarryPurgeMaxConfidence:0.###} extendedWeakMaxConfidence={YoloSceneCutExtendedWeakCarryMaxConfidence:0.###}");
                }

                var postSceneCleanupPass = RemoveYoloWeakIsolatedFinalMasks(
                    videoPath,
                    cancellationToken,
                    blockedCutFramePairs: yoloCutPairs,
                    additionalBlockedFaces: CombineFaceInfos(
                        yoloCleanupPass.RemovedFacesInfo,
                        yoloCarryCleanup.RemovedFacesInfo),
                    sceneCarryBlockedFaces: yoloCarryCleanup.RemovedFacesInfo,
                    sceneCarryBlockedFrameIndices: yoloSceneCutBlockedFrames,
                    logLabel: "YoloFinalMaskPostSceneCleanup",
                    logWhenNoRemovals: true,
                    gapFillLogLabel: "YoloFinalMaskPostSceneGapFill",
                    gapFillSceneCutGuardLogLabel: "YoloFinalMaskPostSceneGapFillSceneCutGuard");
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
                    RemoveYoloWeakIsolatedFinalMasks(
                        videoPath,
                        cancellationToken,
                        fillStableGaps: false,
                        logLabel: "YoloFinalMaskPostGapFillCleanup",
                        logWhenNoRemovals: true);
                }
            }

            LogFinalMaskSummary(yoloProtectedSceneCarryFrames);
            return new AutoMaskPostProcessResult(trackPost, yoloProtectedSceneCarryFrames);
        }

        private FaceTrackPostProcessResult ApplyAutoTemporalFixes()
        {
            if (!_options.UseTracking)
                return FaceTrackPostProcessResult.Empty;

            var result = new FaceTrackInterpolator().Apply(
                _maskProvider,
                _totalFrames,
                BuildTrackPostProcessOptions(_options.FilterProfile));

            if (result.FilledGapFaces > 0 ||
                result.FilledLostFaces > 0 ||
                result.FilledInitialFaces > 0 ||
                result.BlockedInitialFillTracks > 0 ||
                result.RemovedShortFaces > 0 ||
                result.RemovedSparseFaces > 0 ||
                result.RemovedUnstableTailFaces > 0 ||
                result.RemovedEdgeTailFaces > 0 ||
                result.RemovedLowerFrameFaces > 0)
            {
                Debug.WriteLine(
                    $"[FaceTrackPost] tracks={result.TrackCount} filled={result.FilledGapFaces} lostFilled={result.FilledLostFaces} initialFilled={result.FilledInitialFaces} blockedInitialFill={result.BlockedInitialFillTracks} lostFrames={FormatFrameList(result.FilledLostFrameIndices)} removedShort={result.RemovedShortFaces} removedSparse={result.RemovedSparseFaces} removedUnstableTail={result.RemovedUnstableTailFaces} removedEdgeTail={result.RemovedEdgeTailFaces} removedLower={result.RemovedLowerFrameFaces} rewritten={result.RewrittenFrames}");
            }

            return result;
        }

        private void ApplyAutoTemporalSmoothing(IReadOnlyList<string>? blockedCutPairs = null)
        {
            int total = _totalFrames;
            if (total < 3)
                return;

            var blockedCutStarts = BuildTemporalSmoothingCutStarts(blockedCutPairs);
            var facesByFrame = new List<Rect>?[total];
            var confByFrame = new List<float>?[total];
            var sizeByFrame = new PixelSize[total];
            var hasStored = new bool[total];

            foreach (int index in _maskProvider.GetStoredMaskFrameIndices())
            {
                if (index >= 0 && index < total)
                    hasStored[index] = true;
            }

            foreach (var entry in _maskProvider.GetFaceMaskEntries())
            {
                int i = entry.Key;
                if (i < 0 || i >= total || hasStored[i])
                    continue;

                var data = entry.Value;
                if (data.Faces.Count > 0)
                {
                    facesByFrame[i] = new List<Rect>(data.Faces);
                    confByFrame[i] = new List<float>(data.Confidences);
                    sizeByFrame[i] = data.Size;
                }
            }

            for (int pass = 0; pass < TemporalSmoothPasses; pass++)
            {
                for (int i = 1; i < total - 1; i++)
                {
                    if (hasStored[i] || facesByFrame[i] == null)
                        continue;

                    var currentFaces = facesByFrame[i]!;
                    var smoothed = new List<Rect>(currentFaces.Count);
                    var prevFaces = FindNearestTemporalFaces(facesByFrame, i, -1, TemporalSmoothSearchWindowFrames, blockedCutStarts);
                    var nextFaces = FindNearestTemporalFaces(facesByFrame, i, 1, TemporalSmoothSearchWindowFrames, blockedCutStarts);

                    for (int j = 0; j < currentFaces.Count; j++)
                    {
                        var current = currentFaces[j];
                        Rect target = current;
                        int targetCount = 0;

                        double prevIou = GetMaxIoU(current, prevFaces, out var prevMatch);
                        if (prevIou >= TemporalSmoothIouMin && IsReasonableTemporalMatch(current, prevMatch))
                        {
                            target = BlendRect(target, prevMatch, 1.0 / ++targetCount);
                        }

                        double nextIou = GetMaxIoU(current, nextFaces, out var nextMatch);
                        if (nextIou >= TemporalSmoothIouMin && IsReasonableTemporalMatch(current, nextMatch))
                        {
                            target = targetCount == 0
                                ? nextMatch
                                : BlendRect(target, nextMatch, 1.0 / (targetCount + 1));
                            targetCount++;
                        }

                        smoothed.Add(targetCount == 0 ? current : BlendRect(current, target, TemporalSmoothWeight));
                    }

                    facesByFrame[i] = smoothed;
                }
            }

            for (int i = 0; i < total; i++)
            {
                if (hasStored[i] || facesByFrame[i] == null || facesByFrame[i]!.Count == 0 || sizeByFrame[i].Width <= 0 || sizeByFrame[i].Height <= 0)
                    continue;

                _maskProvider.SetFaceRects(
                    i,
                    facesByFrame[i]!,
                    sizeByFrame[i],
                    minConfidence: confByFrame[i] == null || confByFrame[i]!.Count == 0 ? null : confByFrame[i]!.Min(),
                    confidences: confByFrame[i]);
            }
        }

        private FaceTrackSceneCutGuardResult RemoveYoloTrackFillAcrossSceneCuts(
            string videoPath,
            FaceTrackPostProcessResult trackPost,
            CancellationToken cancellationToken,
            string stage)
        {
            var guard = new FaceTrackSceneCutGuard();
            var directCandidates = guard.BuildWeakTrackTransitionCandidates(
                _maskProvider,
                BuildTrackPostProcessOptions(FaceFilterProfile.Yolo),
                maxTargetConfidence: YoloSceneCutDirectCarryMaxConfidence,
                maxTransitionGap: SuspiciousNoFaceMaxGap,
                minConfidenceDrop: 0.0f,
                maxPostCutCarryFrames: 5,
                minSourceConfidence: YoloSceneCutDirectCarryMinSourceConfidence);
            var postCutCandidates = guard.BuildWeakPostCutCarryCandidates(
                _maskProvider,
                maxTargetConfidence: YoloSceneCutPostCutCarryMaxConfidence,
                maxCarryFrames: 5,
                sourceLookbackFrames: YoloSceneCutPostCutLookbackFrames,
                includeEdgeCandidates: true);
            var candidates = directCandidates
                .Concat(postCutCandidates)
                .Concat(trackPost.FilledGapFacesInfo)
                .Concat(trackPost.FilledLostFacesInfo)
                .Concat(trackPost.FilledInitialFacesInfo)
                .ToArray();

            if (candidates.Length > 0)
            {
                Debug.WriteLine(
                    $"[FaceTrackSceneCutGuard] stage={stage} start directCandidates={directCandidates.Count} filled={trackPost.FilledGapFacesInfo.Count} lostFilled={trackPost.FilledLostFacesInfo.Count} initialFilled={trackPost.FilledInitialFacesInfo.Count} totalCandidates={candidates.Length} directBudget={YoloSceneCutDirectDifferenceMaxCandidates}");
            }

            var result = guard.Apply(
                _maskProvider,
                videoPath,
                candidates,
                differenceThreshold: YoloSceneCutDifferenceThreshold,
                directDifferenceThreshold: YoloSceneCutDirectDifferenceThreshold,
                directDifferenceMaxChecks: YoloSceneCutDirectDifferenceMaxCandidates,
                removeMatchingTailFrames: YoloSceneCutMatchingTailMaxFrames,
                removeMatchingTailMaxConfidence: YoloSceneCutMatchingTailMaxConfidence,
                candidateMatchMinIou: YoloSceneCutCandidateMatchMinIou,
                candidateMatchMaxCenterShiftRatio: YoloSceneCutCandidateMatchMaxCenterShiftRatio,
                candidateMatchMaxAreaChangeRatio: YoloSceneCutCandidateMatchMaxAreaChangeRatio,
                cancellationToken: cancellationToken);

            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                Debug.WriteLine(
                    $"[FaceTrackSceneCutGuard] stage={stage} skipped directCandidates={directCandidates.Count} postCutCandidates={postCutCandidates.Count} checked={result.Checked} directChecked={result.DirectDifferenceChecks} directSkipped={result.DirectDifferenceSkipped} checkedPairs={FormatTextList(result.CheckedFramePairs)} maxDiff={result.MaxDifference:0.###} cutPairs={FormatTextList(result.CutFramePairs)} removed={result.Removed} removedFrames={FormatFrameList(result.RemovedFrameIndices)} error={result.Error}");
                return result;
            }

            if (result.Checked > 0)
            {
                Debug.WriteLine(
                    $"[FaceTrackSceneCutGuard] stage={stage} directCandidates={directCandidates.Count} postCutCandidates={postCutCandidates.Count} checked={result.Checked} directChecked={result.DirectDifferenceChecks} directSkipped={result.DirectDifferenceSkipped} checkedPairs={FormatTextList(result.CheckedFramePairs)} maxDiff={result.MaxDifference:0.###} cutPairs={FormatTextList(result.CutFramePairs)} removed={result.Removed} removedFrames={FormatFrameList(result.RemovedFrameIndices)} threshold={result.Threshold:0.###} elapsedMs={result.ElapsedMs}");
            }

            return result;
        }

        private FaceTrackSceneCutGuardResult ProbeYoloStrongCarrySceneCuts(
            string videoPath,
            CancellationToken cancellationToken,
            string stage)
        {
            var guard = new FaceTrackSceneCutGuard();
            var candidates = guard.BuildWeakPostCutCarryCandidates(
                _maskProvider,
                maxTargetConfidence: YoloSceneCutStrongCarryProbeMaxConfidence,
                maxCarryFrames: 5,
                sourceLookbackFrames: YoloSceneCutPostCutLookbackFrames,
                minSourceConfidence: YoloSceneCutStrongCarryProbeMinSourceConfidence,
                minTargetConfidence: YoloSceneCutStrongCarryProbeMinConfidence,
                minIou: YoloSceneCutCandidateMatchMinIou,
                maxCenterShiftRatio: YoloSceneCutCandidateMatchMaxCenterShiftRatio,
                maxAreaChangeRatio: YoloSceneCutCandidateMatchMaxAreaChangeRatio,
                includeEdgeCandidates: true);

            if (candidates.Count > 0)
            {
                Debug.WriteLine(
                    $"[YoloStrongCarrySceneCutProbe] stage={stage} start candidates={candidates.Count} minTarget={YoloSceneCutStrongCarryProbeMinConfidence:0.###} maxTarget={YoloSceneCutStrongCarryProbeMaxConfidence:0.###} minSource={YoloSceneCutStrongCarryProbeMinSourceConfidence:0.###}");
            }

            var result = guard.Apply(
                _maskProvider,
                videoPath,
                candidates,
                differenceThreshold: YoloSceneCutDifferenceThreshold,
                directDifferenceThreshold: YoloSceneCutDirectDifferenceThreshold,
                directDifferenceMaxChecks: YoloSceneCutDirectDifferenceMaxCandidates,
                candidateMatchMinIou: YoloSceneCutCandidateMatchMinIou,
                candidateMatchMaxCenterShiftRatio: YoloSceneCutCandidateMatchMaxCenterShiftRatio,
                candidateMatchMaxAreaChangeRatio: YoloSceneCutCandidateMatchMaxAreaChangeRatio,
                removeCandidates: false,
                cancellationToken: cancellationToken);

            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                Debug.WriteLine(
                    $"[YoloStrongCarrySceneCutProbe] stage={stage} skipped candidates={candidates.Count} checked={result.Checked} directChecked={result.DirectDifferenceChecks} directSkipped={result.DirectDifferenceSkipped} checkedPairs={FormatTextList(result.CheckedFramePairs)} maxDiff={result.MaxDifference:0.###} cutPairs={FormatTextList(result.CutFramePairs)} error={result.Error}");
                return result;
            }

            if (result.Checked > 0)
            {
                Debug.WriteLine(
                    $"[YoloStrongCarrySceneCutProbe] stage={stage} candidates={candidates.Count} checked={result.Checked} directChecked={result.DirectDifferenceChecks} directSkipped={result.DirectDifferenceSkipped} checkedPairs={FormatTextList(result.CheckedFramePairs)} maxDiff={result.MaxDifference:0.###} cutPairs={FormatTextList(result.CutFramePairs)} threshold={result.Threshold:0.###} elapsedMs={result.ElapsedMs}");
            }

            return result;
        }

        private YoloFinalMaskCleanupPassResult RemoveYoloWeakIsolatedFinalMasks(
            string videoPath,
            CancellationToken cancellationToken,
            bool fillStableGaps = true,
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
                    $"[{logLabel}] removedWeakIsolated={cleanup.RemovedWeakIsolatedFaces} removedWeakUnsupported={cleanup.RemovedWeakUnsupportedFaces} removedMediumUnsupported={cleanup.RemovedMediumUnsupportedFaces} removedWeakShortClusters={cleanup.RemovedWeakShortClusterFaces} removedWeakTinyClusters={cleanup.RemovedWeakTinyClusterFaces} removedTinyShortClusters={cleanup.RemovedTinyShortClusterFaces} removedTinyIsolated={cleanup.RemovedTinyIsolatedFaces} removedTopEdgeWeakClusters={cleanup.RemovedTopEdgeWeakClusterFaces} removedUpperWeakClusters={cleanup.RemovedUpperWeakClusterFaces} removedLowerWeakClusters={cleanup.RemovedLowerWeakClusterFaces} removedAspectOutliers={cleanup.RemovedAspectOutlierClusterFaces} removedFrames={FormatFrameList(cleanup.RemovedFrameIndices)} maxConf={YoloFinalMaskWeakIsolatedConfidenceMax:0.###}");
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

        private void LogFinalMaskSummary(IReadOnlyCollection<int>? protectedSceneCarryFrameIndices = null)
        {
            if (_options.FilterProfile != FaceFilterProfile.Yolo)
                return;

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
                var emptyReviewReasons = BuildFinalMaskReviewReasons(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, protectedSceneCarryFrames.Length);
                Debug.WriteLine($"[FinalMaskSummary] profile=Yolo frames=0 rows=0 frameRange=none shortGaps=0 shortGapRanges=none largeJumpGaps=0 largeJumpRanges=none isolated=0 isolatedFrames=none lowConf=0 lowConfFrames=none weakNonEdge=0 weakNonEdgeFrames=none edgeWeak=0 edgeWeakFrames=none topEdgeWeak=0 topEdgeWeakFrames=none upperWeak=0 upperWeakFrames=none lowerWeak=0 lowerWeakFrames=none aspectBad=0 aspectBadFrames=none tinyWeak=0 tinyWeakFrames=none tinyShort=0 tinyShortFrames=none protectedSceneCarry={protectedSceneCarryFrames.Length} protectedSceneCarryFrames={FormatFrameList(protectedSceneCarryFrames)} reviewRequired={emptyReviewReasons.Count > 0} reviewReasons={FormatTextList(emptyReviewReasons)}");
                return;
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
            int upperWeakRows = 0;
            int lowerWeakRows = 0;
            int aspectBadRows = 0;
            int tinyWeakRows = 0;
            int tinyShortRows = 0;
            var lowConfidenceFrames = new HashSet<int>();
            var weakNonEdgeFrames = new HashSet<int>();
            var edgeWeakFrames = new HashSet<int>();
            var topEdgeWeakFrames = new HashSet<int>();
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
                largeJumpGapRanges.Count,
                isolatedFrames.Count,
                lowConfidenceRows,
                weakNonEdgeRows,
                edgeWeakRows,
                topEdgeWeakRows,
                upperWeakRows,
                lowerWeakRows,
                aspectBadRows,
                tinyWeakRows,
                tinyShortRows,
                protectedSceneCarryFrames.Length);

            Debug.WriteLine(
                $"[FinalMaskSummary] profile=Yolo frames={frames.Length} rows={rows} frameRange={frames[0]}-{frames[^1]} shortGaps={shortGapCount} shortGapRanges={FormatTextList(shortGapRanges)} largeJumpGaps={largeJumpGapRanges.Count} largeJumpRanges={FormatTextList(largeJumpGapRanges)} isolated={isolatedFrames.Count} isolatedFrames={FormatFrameList(isolatedFrames)} lowConf={lowConfidenceRows} lowConfFrames={FormatFrameList(lowConfidenceFrames.OrderBy(static x => x).ToArray())} weakNonEdge={weakNonEdgeRows} weakNonEdgeFrames={FormatFrameList(weakNonEdgeFrames.OrderBy(static x => x).ToArray())} edgeWeak={edgeWeakRows} edgeWeakFrames={FormatFrameList(edgeWeakFrames.OrderBy(static x => x).ToArray())} topEdgeWeak={topEdgeWeakRows} topEdgeWeakFrames={FormatFrameList(topEdgeWeakFrames.OrderBy(static x => x).ToArray())} upperWeak={upperWeakRows} upperWeakFrames={FormatFrameList(upperWeakFrames.OrderBy(static x => x).ToArray())} lowerWeak={lowerWeakRows} lowerWeakFrames={FormatFrameList(lowerWeakFrames.OrderBy(static x => x).ToArray())} aspectBad={aspectBadRows} aspectBadFrames={FormatFrameList(aspectBadFrames.OrderBy(static x => x).ToArray())} tinyWeak={tinyWeakRows} tinyWeakFrames={FormatFrameList(tinyWeakFrames.OrderBy(static x => x).ToArray())} tinyShort={tinyShortRows} tinyShortFrames={FormatFrameList(tinyShortFrames.OrderBy(static x => x).ToArray())} protectedSceneCarry={protectedSceneCarryFrames.Length} protectedSceneCarryFrames={FormatFrameList(protectedSceneCarryFrames)} reviewRequired={reviewReasons.Count > 0} reviewReasons={FormatTextList(reviewReasons)}");
        }

        private void RefineAutoFacesWithRoi(
            string videoPath,
            IBgraFaceDetector detector,
            FaceTrackPostProcessResult trackPost,
            FaceOnnxDetectorOptions detectorOptions,
            bool useFaceOnnxRoiDetector)
        {
            var candidates = trackPost.FilledGapFacesInfo
                .Concat(trackPost.FilledLostFacesInfo)
                .Concat(trackPost.FilledInitialFacesInfo)
                .ToArray();
            if (candidates.Length == 0)
                return;

            using var roiDetector = useFaceOnnxRoiDetector
                ? new FaceOnnxDetector(CreateRoiRefinerDetectorOptions(detectorOptions))
                : null;
            var refineDetector = roiDetector ?? detector;
            var refine = new FaceTrackRoiRefiner().Apply(
                _maskProvider,
                videoPath,
                refineDetector,
                candidates,
                _options.DownscaleQuality);

            if (refine.Attempts > 0)
            {
                Debug.WriteLine(
                    $"[FaceTrackRoiRefine] attempts={refine.Attempts} hits={refine.Hits} seeks={refine.SeekCount} decoded={refine.DecodedFrames} elapsedMs={refine.ElapsedMs}");
            }
        }

        private static FaceTrackPostProcessOptions BuildTrackPostProcessOptions(FaceFilterProfile profile)
        {
            if (profile == FaceFilterProfile.Yolo)
            {
                return new FaceTrackPostProcessOptions
                {
                    MaxTrackGap = SuspiciousNoFaceMaxGap,
                    MaxFillGap = Math.Min(5, SuspiciousNoFaceMaxGap),
                    MaxLostFillFrames = 0,
                    MaxInitialFillFrames = 3,
                    InitialFillRequiresInwardMotion = true,
                    MaxConfirmedTrackHoldFrames = SuspiciousNoFaceMaxGap,
                    AllowSmallTrackLostFill = true,
                    WeakConfidence = 0.38f,
                    StrongConfidence = 0.58f,
                    SyntheticFillConfidenceMax = YoloSceneCutPostCutCarryMaxConfidence,
                    DropShortTrackMaxDetections = 2,
                    DropShortSmallTrackMaxDetections = 3,
                    ShortTrackMaxConfidence = 0.48f,
                    DropSparseTrackMaxDetections = 3,
                    DropSparseTrackMinSpanFrames = 8,
                    DropSparseTrackMaxDensity = 0.42,
                    SparseTrackMaxConfidence = 0.56f,
                    EdgeTailMaxConfidence = 0.50f,
                    EdgeTailMinStableDetections = 3,
                    EdgeLostFillMaxConfidence = 0.60f,
                    SmallTrackMaxAreaRatio = 0.00070,
                    MinTrackIou = 0.08,
                    MaxCenterShiftRatio = 0.72,
                    MaxAreaChangeRatio = 4.0,
                    DuplicateIou = TemporalDuplicateIouMin,
                    UnstableTailMaxConfidence = 0.40f,
                    UnstableTailMinStableDetections = 3,
                    UnstableTailMinIou = 0.45,
                    UnstableTailMaxAreaChangeRatio = 1.8,
                    LowerFrameTrackMaxConfidence = 0.50f,
                    LowerFrameTrackMinCenterYRatio = 0.58,
                    LowerFrameTrackMinAreaRatio = 0.015,
                    LowerFrameTrackMaxAreaRatio = 0.045
                };
            }

            if (profile == FaceFilterProfile.Scrfd)
            {
                return new FaceTrackPostProcessOptions
                {
                    MaxTrackGap = SuspiciousNoFaceMaxGap,
                    MaxFillGap = Math.Min(5, SuspiciousNoFaceMaxGap),
                    WeakConfidence = 0.35f,
                    StrongConfidence = 0.55f,
                    ShortTrackMaxConfidence = 0.55f,
                    SmallTrackMaxAreaRatio = 0.00075,
                    MinTrackIou = 0.08,
                    MaxCenterShiftRatio = 0.75,
                    MaxAreaChangeRatio = 4.0,
                    DuplicateIou = TemporalDuplicateIouMin
                };
            }

            return new FaceTrackPostProcessOptions
            {
                MaxTrackGap = SuspiciousNoFaceMaxGap,
                MaxFillGap = Math.Min(5, SuspiciousNoFaceMaxGap),
                WeakConfidence = TemporalConfidenceWeak,
                StrongConfidence = TemporalConfidenceStrong,
                ShortTrackMaxConfidence = TemporalConfidenceStrong,
                SmallTrackMaxAreaRatio = 0.00075,
                MinTrackIou = TemporalHoleFillIouMin,
                MaxCenterShiftRatio = TemporalMaxCenterShiftRatio,
                MaxAreaChangeRatio = TemporalMaxAreaChangeRatio,
                DuplicateIou = TemporalDuplicateIouMin
            };
        }

        private static FaceOnnxDetectorOptions CreateRoiRefinerDetectorOptions(FaceOnnxDetectorOptions source)
        {
            var defaults = FaceOnnxDetector.GetDefaultThresholds();
            float detection = source.DetectionThreshold ?? defaults.Detection;
            float confidence = source.ConfidenceThreshold ?? defaults.Confidence;
            float nms = source.NmsThreshold ?? defaults.Nms;

            return new FaceOnnxDetectorOptions
            {
                UseOrtOptimization = true,
                UseGpu = false,
                IntraOpNumThreads = source.IntraOpNumThreads,
                InterOpNumThreads = source.InterOpNumThreads,
                UseParallelExecution = false,
                EnablePreprocessParallelism = true,
                AllowAutoTune = false,
                AllowAutoGpu = false,
                DetectionThreshold = Math.Min(detection, 0.12f),
                ConfidenceThreshold = Math.Min(confidence, 0.12f),
                NmsThreshold = Math.Max(nms, 0.75f)
            };
        }

        private static IReadOnlyList<string> BuildFinalMaskReviewReasons(
            int shortGapCount,
            int largeJumpGapCount,
            int isolatedCount,
            int lowConfidenceRows,
            int weakNonEdgeRows,
            int edgeWeakRows,
            int topEdgeWeakRows,
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

        private static IReadOnlyList<Rect>? FindNearestTemporalFaces(
            IReadOnlyList<Rect>?[] facesByFrame,
            int frameIndex,
            int direction,
            int maxDistanceFrames,
            IReadOnlySet<int>? blockedCutStarts)
        {
            if (maxDistanceFrames <= 0)
                return null;

            int index = frameIndex + direction;
            int searched = 0;
            int previousIndex = frameIndex;
            while (index >= 0 && index < facesByFrame.Length)
            {
                if (IsBlockedTemporalSmoothingStep(previousIndex, index, blockedCutStarts))
                    break;

                searched++;
                if (searched > maxDistanceFrames)
                    break;

                var faces = facesByFrame[index];
                if (faces != null && faces.Count > 0)
                    return faces;

                previousIndex = index;
                index += direction;
            }

            return null;
        }

        private static IReadOnlySet<int> BuildTemporalSmoothingCutStarts(IReadOnlyList<string>? cutPairs)
        {
            if (cutPairs == null || cutPairs.Count == 0)
                return new HashSet<int>();

            var blocked = new HashSet<int>();
            foreach (string pair in cutPairs)
            {
                if (string.IsNullOrWhiteSpace(pair))
                    continue;

                var parts = pair.Split("->", StringSplitOptions.TrimEntries);
                if (parts.Length != 2)
                    continue;

                if (!int.TryParse(parts[0], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int a) ||
                    !int.TryParse(parts[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int b))
                {
                    continue;
                }

                int start = Math.Min(a, b);
                int end = Math.Max(a, b);
                for (int frame = start; frame < end; frame++)
                    blocked.Add(frame);
            }

            return blocked;
        }

        private static bool IsBlockedTemporalSmoothingStep(int fromFrame, int toFrame, IReadOnlySet<int>? blockedCutStarts)
        {
            if (blockedCutStarts == null || blockedCutStarts.Count == 0)
                return false;

            int cutStart = Math.Min(fromFrame, toFrame);
            return blockedCutStarts.Contains(cutStart);
        }

        private static bool IsReasonableTemporalMatch(Rect current, Rect match)
        {
            if (match.Width <= 0 || match.Height <= 0)
                return false;

            double area = Math.Max(1.0, current.Width * current.Height);
            double matchArea = Math.Max(1.0, match.Width * match.Height);
            double ratio = area / matchArea;
            if (ratio > TemporalMaxAreaChangeRatio || ratio < 1.0 / TemporalMaxAreaChangeRatio)
                return false;

            double cx = current.X + current.Width * 0.5;
            double cy = current.Y + current.Height * 0.5;
            double mx = match.X + match.Width * 0.5;
            double my = match.Y + match.Height * 0.5;
            double shift = Math.Sqrt((cx - mx) * (cx - mx) + (cy - my) * (cy - my));
            double maxDim = Math.Max(1.0, Math.Max(match.Width, match.Height));
            return shift / maxDim <= TemporalMaxCenterShiftRatio;
        }

        private static Rect BlendRect(Rect current, Rect target, double weight)
        {
            double keep = 1.0 - weight;
            return new Rect(
                current.X * keep + target.X * weight,
                current.Y * keep + target.Y * weight,
                Math.Max(0.0, current.Width * keep + target.Width * weight),
                Math.Max(0.0, current.Height * keep + target.Height * weight));
        }

        private static double GetMaxIoU(Rect rect, IReadOnlyList<Rect>? others, out Rect match)
        {
            match = default;
            if (others == null || others.Count == 0)
                return 0.0;

            double best = 0.0;
            for (int i = 0; i < others.Count; i++)
            {
                var other = others[i];
                double iou = IoU(rect, other);
                if (iou > best)
                {
                    best = iou;
                    match = other;
                }
            }

            return best;
        }

        private static double IoU(Rect a, Rect b)
        {
            double ax1 = a.X;
            double ay1 = a.Y;
            double ax2 = a.X + a.Width;
            double ay2 = a.Y + a.Height;

            double bx1 = b.X;
            double by1 = b.Y;
            double bx2 = b.X + b.Width;
            double by2 = b.Y + b.Height;

            double ix1 = Math.Max(ax1, bx1);
            double iy1 = Math.Max(ay1, by1);
            double ix2 = Math.Min(ax2, bx2);
            double iy2 = Math.Min(ay2, by2);

            double iw = Math.Max(0.0, ix2 - ix1);
            double ih = Math.Max(0.0, iy2 - iy1);
            double inter = iw * ih;
            if (inter <= 0.0)
                return 0.0;

            double union = a.Width * a.Height + b.Width * b.Height - inter;
            if (union <= 0.0)
                return 0.0;
            return inter / union;
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
        IReadOnlyList<int> ProtectedSceneCarryFrameIndices);
}
