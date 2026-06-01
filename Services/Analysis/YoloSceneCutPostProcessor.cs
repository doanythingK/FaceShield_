using FaceShield.Services.Video;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace FaceShield.Services.Analysis
{
    public sealed class YoloSceneCutPostProcessor
    {
        private const int SuspiciousNoFaceMaxGap = 8;
        private const float YoloSceneCutDirectCarryMaxConfidence = 0.98f;
        private const float YoloSceneCutDirectCarryMinSourceConfidence = 0.58f;
        private const float YoloSceneCutPostCutCarryMaxConfidence = 0.78f;
        private const float YoloSceneCutStrongCarryProbeMaxConfidence = 0.995f;
        private const float YoloSceneCutStrongCarryProbeMinConfidence = 0.78f;
        private const float YoloSceneCutStrongCarryProbeMinSourceConfidence = 0.80f;
        private const double YoloSceneCutDifferenceThreshold = 0.15;
        private const double YoloSceneCutDirectDifferenceThreshold = 0.32;
        private const int YoloSceneCutDirectDifferenceMaxCandidates = 96;
        private const int YoloSceneCutCarryProbeFrames = SuspiciousNoFaceMaxGap;
        private const int YoloSceneCutMatchingTailMaxFrames = SuspiciousNoFaceMaxGap;
        private const float YoloSceneCutMatchingTailMaxConfidence = 0.98f;
        private const double YoloSceneCutCandidateMatchMinIou = 0.55;
        private const double YoloSceneCutCandidateMatchMaxCenterShiftRatio = 0.65;
        private const double YoloSceneCutCandidateMatchMaxAreaChangeRatio = 3.0;
        private const int YoloSceneCutPostCutLookbackFrames = 5;

        public FaceTrackSceneCutGuardResult RemoveTrackFillAcrossSceneCuts(
            FrameMaskProvider maskProvider,
            string videoPath,
            FaceTrackPostProcessResult trackPost,
            CancellationToken cancellationToken,
            string stage)
        {
            var guard = new FaceTrackSceneCutGuard();
            var directCandidates = guard.BuildWeakTrackTransitionCandidates(
                maskProvider,
                AutoMaskTemporalPostProcessor.BuildTrackPostProcessOptions(FaceFilterProfile.Yolo),
                maxTargetConfidence: YoloSceneCutDirectCarryMaxConfidence,
                maxTransitionGap: SuspiciousNoFaceMaxGap,
                minConfidenceDrop: 0.0f,
                maxPostCutCarryFrames: YoloSceneCutCarryProbeFrames,
                minSourceConfidence: YoloSceneCutDirectCarryMinSourceConfidence);
            var postCutCandidates = guard.BuildWeakPostCutCarryCandidates(
                maskProvider,
                maxTargetConfidence: YoloSceneCutPostCutCarryMaxConfidence,
                maxCarryFrames: YoloSceneCutCarryProbeFrames,
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
                maskProvider,
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
                    $"[FaceTrackSceneCutGuard] stage={stage} skipped directCandidates={directCandidates.Count} postCutCandidates={postCutCandidates.Count} checked={result.Checked} directChecked={result.DirectDifferenceChecks} directSkipped={result.DirectDifferenceSkipped} checkedPairs={AutoMaskPostProcessLog.FormatTextList(result.CheckedFramePairs)} maxDiff={result.MaxDifference:0.###} cutPairs={AutoMaskPostProcessLog.FormatTextList(result.CutFramePairs)} removed={result.Removed} removedFrames={AutoMaskPostProcessLog.FormatFrameList(result.RemovedFrameIndices)} error={result.Error}");
                return result;
            }

            if (result.Checked > 0)
            {
                Debug.WriteLine(
                    $"[FaceTrackSceneCutGuard] stage={stage} directCandidates={directCandidates.Count} postCutCandidates={postCutCandidates.Count} checked={result.Checked} directChecked={result.DirectDifferenceChecks} directSkipped={result.DirectDifferenceSkipped} checkedPairs={AutoMaskPostProcessLog.FormatTextList(result.CheckedFramePairs)} maxDiff={result.MaxDifference:0.###} cutPairs={AutoMaskPostProcessLog.FormatTextList(result.CutFramePairs)} removed={result.Removed} removedFrames={AutoMaskPostProcessLog.FormatFrameList(result.RemovedFrameIndices)} threshold={result.Threshold:0.###} elapsedMs={result.ElapsedMs}");
            }

            return result;
        }

        public FaceTrackSceneCutGuardResult ProbeStrongCarrySceneCuts(
            FrameMaskProvider maskProvider,
            string videoPath,
            CancellationToken cancellationToken,
            string stage)
        {
            var guard = new FaceTrackSceneCutGuard();
            var candidates = guard.BuildWeakPostCutCarryCandidates(
                maskProvider,
                maxTargetConfidence: YoloSceneCutStrongCarryProbeMaxConfidence,
                maxCarryFrames: YoloSceneCutCarryProbeFrames,
                sourceLookbackFrames: YoloSceneCutPostCutLookbackFrames,
                minSourceConfidence: YoloSceneCutStrongCarryProbeMinSourceConfidence,
                minTargetConfidence: YoloSceneCutStrongCarryProbeMinConfidence,
                minIou: YoloSceneCutCandidateMatchMinIou,
                maxCenterShiftRatio: YoloSceneCutCandidateMatchMaxCenterShiftRatio,
                maxAreaChangeRatio: YoloSceneCutCandidateMatchMaxAreaChangeRatio,
                includeEdgeCandidates: true,
                includeIndependentStrongContinuation: true);

            if (candidates.Count > 0)
            {
                Debug.WriteLine(
                    $"[YoloStrongCarrySceneCutProbe] stage={stage} start candidates={candidates.Count} minTarget={YoloSceneCutStrongCarryProbeMinConfidence:0.###} maxTarget={YoloSceneCutStrongCarryProbeMaxConfidence:0.###} minSource={YoloSceneCutStrongCarryProbeMinSourceConfidence:0.###}");
            }

            var result = guard.Apply(
                maskProvider,
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
                    $"[YoloStrongCarrySceneCutProbe] stage={stage} skipped candidates={candidates.Count} checked={result.Checked} directChecked={result.DirectDifferenceChecks} directSkipped={result.DirectDifferenceSkipped} checkedPairs={AutoMaskPostProcessLog.FormatTextList(result.CheckedFramePairs)} maxDiff={result.MaxDifference:0.###} cutPairs={AutoMaskPostProcessLog.FormatTextList(result.CutFramePairs)} error={result.Error}");
                return result;
            }

            if (result.Checked > 0)
            {
                Debug.WriteLine(
                    $"[YoloStrongCarrySceneCutProbe] stage={stage} candidates={candidates.Count} checked={result.Checked} directChecked={result.DirectDifferenceChecks} directSkipped={result.DirectDifferenceSkipped} checkedPairs={AutoMaskPostProcessLog.FormatTextList(result.CheckedFramePairs)} maxDiff={result.MaxDifference:0.###} cutPairs={AutoMaskPostProcessLog.FormatTextList(result.CutFramePairs)} threshold={result.Threshold:0.###} elapsedMs={result.ElapsedMs}");
            }

            return result;
        }
    }
}
