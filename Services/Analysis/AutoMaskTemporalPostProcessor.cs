using Avalonia;
using FaceShield.Services.Video;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace FaceShield.Services.Analysis
{
    public sealed class AutoMaskTemporalPostProcessor
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
        private const int YoloConfirmedTrackHoldFrames = 5;
        public const float YoloSyntheticFillConfidenceMax = 0.78f;

        public FaceTrackPostProcessResult ApplyTemporalFixes(
            FrameMaskProvider maskProvider,
            int totalFrames,
            FaceFilterProfile profile,
            bool useTracking,
            bool missRecoveryOnly = false,
            bool continuityOnly = false,
            IReadOnlySet<int>? blockedSceneCutStarts = null,
            int mutableStartFrameIndex = 0,
            CancellationToken cancellationToken = default)
        {
            if (!useTracking)
                return FaceTrackPostProcessResult.Empty;

            var result = new FaceTrackInterpolator().Apply(
                maskProvider,
                totalFrames,
                BuildTrackPostProcessOptions(profile, missRecoveryOnly, continuityOnly),
                blockedSceneCutStarts,
                mutableStartFrameIndex,
                cancellationToken);

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
                    $"[FaceTrackPost] tracks={result.TrackCount} filled={result.FilledGapFaces} lostFilled={result.FilledLostFaces} initialFilled={result.FilledInitialFaces} blockedInitialFill={result.BlockedInitialFillTracks} lostFrames={AutoMaskPostProcessLog.FormatFrameList(result.FilledLostFrameIndices)} removedShort={result.RemovedShortFaces} removedSparse={result.RemovedSparseFaces} removedUnstableTail={result.RemovedUnstableTailFaces} removedEdgeTail={result.RemovedEdgeTailFaces} removedLower={result.RemovedLowerFrameFaces} rewritten={result.RewrittenFrames}");
            }

            return result;
        }

        public int ApplyTrackedBoxStabilization(
            FrameMaskProvider maskProvider,
            int totalFrames,
            IReadOnlySet<int>? blockedSceneCutStarts = null,
            int mutableStartFrameIndex = 0,
            CancellationToken cancellationToken = default)
        {
            if (totalFrames < 2)
                return 0;

            const double deadZoneCenterRatio = 0.035;
            const double deadZoneSizeRatio = 0.05;
            const double baseFollowWeight = 0.25;
            const double fullFollowMovementRatio = 0.35;
            const double matchIouMin = 0.08;
            const double matchCenterShiftMax = 0.70;
            const double matchAreaRatioMax = 2.8;

            int[] faceFrameIndices = maskProvider.GetFaceMaskFrameIndices();
            if (faceFrameIndices.Length < 2)
                return 0;

            Array.Sort(faceFrameIndices);
            var storedMaskFrames =
                new HashSet<int>(maskProvider.GetStoredMaskFrameIndices());

            int firstMutableFrame =
                Math.Clamp(mutableStartFrameIndex, 0, totalFrames);
            int rewrittenFrames = 0;
            int previousFrameIndex = -1;
            IReadOnlyList<Rect>? previousFaces = null;

            foreach (int frameIndex in faceFrameIndices)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (frameIndex < 0 || frameIndex >= totalFrames)
                    continue;
                if (!maskProvider.TryGetFaceMaskData(
                        frameIndex,
                        out var currentData) ||
                    currentData.Faces.Count == 0)
                {
                    previousFrameIndex = -1;
                    previousFaces = null;
                    continue;
                }

                IReadOnlyList<Rect> currentFaces = currentData.Faces;
                bool canStabilize =
                    frameIndex >= Math.Max(1, firstMutableFrame) &&
                    previousFrameIndex == frameIndex - 1 &&
                    previousFaces != null &&
                    previousFaces.Count > 0 &&
                    !storedMaskFrames.Contains(frameIndex) &&
                    !(blockedSceneCutStarts?.Contains(frameIndex) ?? false);

                if (!canStabilize)
                {
                    previousFrameIndex = frameIndex;
                    previousFaces = currentFaces;
                    continue;
                }

                Rect[]? stabilized = null;
                var usedPrevious = new bool[previousFaces!.Count];

                for (int currentIndex = 0;
                     currentIndex < currentFaces.Count;
                     currentIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    Rect current = currentFaces[currentIndex];
                    int bestPreviousIndex = -1;
                    double bestScore = double.NegativeInfinity;

                    for (int previousIndex = 0;
                         previousIndex < previousFaces.Count;
                         previousIndex++)
                    {
                        if (usedPrevious[previousIndex])
                            continue;

                        Rect previous = previousFaces[previousIndex];
                        double iou = IoU(current, previous);
                        double centerShiftRatio =
                            GetCenterShiftRatio(previous, current);
                        double areaRatio =
                            GetAreaRatio(previous, current);
                        if (iou < matchIouMin &&
                            centerShiftRatio > matchCenterShiftMax)
                        {
                            continue;
                        }
                        if (areaRatio > matchAreaRatioMax ||
                            areaRatio < 1.0 / matchAreaRatioMax)
                        {
                            continue;
                        }

                        double score =
                            iou * 2.0 -
                            centerShiftRatio * 0.35 -
                            Math.Abs(
                                Math.Log(
                                    Math.Max(0.0001, areaRatio))) * 0.15;
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestPreviousIndex = previousIndex;
                        }
                    }

                    if (bestPreviousIndex < 0)
                        continue;

                    usedPrevious[bestPreviousIndex] = true;
                    Rect previousMatch =
                        previousFaces[bestPreviousIndex];
                    double centerMovement =
                        GetCenterShiftRatio(previousMatch, current);
                    double sizeMovement =
                        GetSizeChangeRatio(previousMatch, current);

                    Rect next;
                    if (centerMovement <= deadZoneCenterRatio &&
                        sizeMovement <= deadZoneSizeRatio)
                    {
                        next = previousMatch;
                    }
                    else
                    {
                        double movement = Math.Max(
                            centerMovement,
                            sizeMovement * 0.75);
                        double normalized = Math.Clamp(
                            (movement - deadZoneCenterRatio) /
                            Math.Max(
                                0.0001,
                                fullFollowMovementRatio -
                                deadZoneCenterRatio),
                            0.0,
                            1.0);
                        double followWeight =
                            baseFollowWeight +
                            (1.0 - baseFollowWeight) * normalized;
                        next = BlendRect(
                            previousMatch,
                            current,
                            followWeight);
                    }

                    next = ClampRectToFrame(
                        next,
                        currentData.Size);
                    if (RectNearlyEquals(next, current))
                        continue;

                    stabilized ??= currentFaces.ToArray();
                    stabilized[currentIndex] = next;
                }

                if (stabilized != null)
                {
                    maskProvider.SetFaceRects(
                        frameIndex,
                        stabilized,
                        currentData.Size,
                        currentData.MinConfidence,
                        currentData.Confidences);
                    rewrittenFrames++;
                    previousFaces = stabilized;
                }
                else
                {
                    previousFaces = currentFaces;
                }

                previousFrameIndex = frameIndex;
            }

            return rewrittenFrames;
        }

        private static double GetCenterShiftRatio(Rect previous, Rect current)
        {
            double px = previous.X + previous.Width * 0.5;
            double py = previous.Y + previous.Height * 0.5;
            double cx = current.X + current.Width * 0.5;
            double cy = current.Y + current.Height * 0.5;
            double distance = Math.Sqrt(
                (cx - px) * (cx - px) +
                (cy - py) * (cy - py));
            return distance /
                Math.Max(1.0, Math.Max(previous.Width, previous.Height));
        }

        private static double GetSizeChangeRatio(Rect previous, Rect current)
        {
            double widthChange =
                Math.Abs(current.Width - previous.Width) /
                Math.Max(1.0, previous.Width);
            double heightChange =
                Math.Abs(current.Height - previous.Height) /
                Math.Max(1.0, previous.Height);
            return Math.Max(widthChange, heightChange);
        }

        private static double GetAreaRatio(Rect a, Rect b)
        {
            double areaA = Math.Max(1.0, a.Width * a.Height);
            double areaB = Math.Max(1.0, b.Width * b.Height);
            return areaB / areaA;
        }

        private static Rect ClampRectToFrame(Rect rect, PixelSize size)
        {
            if (size.Width <= 0 || size.Height <= 0)
                return rect;

            double x = Math.Clamp(rect.X, 0, Math.Max(0, size.Width - 1));
            double y = Math.Clamp(rect.Y, 0, Math.Max(0, size.Height - 1));
            double right = Math.Clamp(
                rect.Right,
                x + 1,
                Math.Max(x + 1, size.Width));
            double bottom = Math.Clamp(
                rect.Bottom,
                y + 1,
                Math.Max(y + 1, size.Height));
            return new Rect(x, y, right - x, bottom - y);
        }

        private static bool RectNearlyEquals(Rect a, Rect b)
            => Math.Abs(a.X - b.X) < 0.01 &&
               Math.Abs(a.Y - b.Y) < 0.01 &&
               Math.Abs(a.Width - b.Width) < 0.01 &&
               Math.Abs(a.Height - b.Height) < 0.01;

        public void ApplyTemporalSmoothing(
            FrameMaskProvider maskProvider,
            int totalFrames,
            IReadOnlyList<string>? blockedCutPairs = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (totalFrames < 3)
                return;

            var blockedCutStarts = BuildTemporalSmoothingCutStarts(
                blockedCutPairs,
                cancellationToken);
            var storedFrames = new HashSet<int>();
            foreach (int index in maskProvider.GetStoredMaskFrameIndices())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (index >= 0 && index < totalFrames)
                    storedFrames.Add(index);
            }

            var facesByFrame = new Dictionary<int, List<Rect>>();
            var confByFrame = new Dictionary<int, List<float>>();
            var sizeByFrame = new Dictionary<int, PixelSize>();

            foreach (var entry in maskProvider.GetFaceMaskEntries())
            {
                cancellationToken.ThrowIfCancellationRequested();
                int frameIndex = entry.Key;
                if (frameIndex < 0 ||
                    frameIndex >= totalFrames ||
                    storedFrames.Contains(frameIndex))
                {
                    continue;
                }

                var data = entry.Value;
                if (data.Faces.Count == 0)
                    continue;

                facesByFrame[frameIndex] = new List<Rect>(data.Faces);
                confByFrame[frameIndex] = new List<float>(data.Confidences);
                sizeByFrame[frameIndex] = data.Size;
            }

            int[] smoothingFrameIndices = facesByFrame.Keys
                .Where(static frameIndex => frameIndex > 0)
                .Where(frameIndex => frameIndex < totalFrames - 1)
                .OrderBy(static frameIndex => frameIndex)
                .ToArray();

            for (int pass = 0; pass < TemporalSmoothPasses; pass++)
            {
                foreach (int frameIndex in smoothingFrameIndices)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!facesByFrame.TryGetValue(frameIndex, out var currentFaces))
                        continue;

                    var smoothed = new List<Rect>(currentFaces.Count);
                    var prevFaces = FindNearestTemporalFaces(
                        facesByFrame,
                        frameIndex,
                        -1,
                        TemporalSmoothSearchWindowFrames,
                        totalFrames,
                        blockedCutStarts);
                    var nextFaces = FindNearestTemporalFaces(
                        facesByFrame,
                        frameIndex,
                        1,
                        TemporalSmoothSearchWindowFrames,
                        totalFrames,
                        blockedCutStarts);

                    for (int faceIndex = 0; faceIndex < currentFaces.Count; faceIndex++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var current = currentFaces[faceIndex];
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

                        smoothed.Add(
                            targetCount == 0
                                ? current
                                : BlendRect(current, target, TemporalSmoothWeight));
                    }

                    facesByFrame[frameIndex] = smoothed;
                }
            }

            foreach (int frameIndex in facesByFrame.Keys.OrderBy(static index => index))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!facesByFrame.TryGetValue(frameIndex, out var faces) ||
                    faces.Count == 0 ||
                    !sizeByFrame.TryGetValue(frameIndex, out var size) ||
                    size.Width <= 0 ||
                    size.Height <= 0)
                {
                    continue;
                }

                confByFrame.TryGetValue(frameIndex, out var confidences);
                maskProvider.SetFaceRects(
                    frameIndex,
                    faces,
                    size,
                    minConfidence: confidences == null || confidences.Count == 0
                        ? null
                        : confidences.Min(),
                    confidences: confidences);
            }
        }

        public static FaceTrackPostProcessOptions BuildTrackPostProcessOptions(FaceFilterProfile profile)
        {
            return BuildTrackPostProcessOptions(profile, missRecoveryOnly: false, continuityOnly: false);
        }

        public static FaceTrackPostProcessOptions BuildTrackPostProcessOptions(
            FaceFilterProfile profile,
            bool missRecoveryOnly = false,
            bool continuityOnly = false)
        {
            if (continuityOnly)
            {
                bool cleanupYoloFalsePositives = profile == FaceFilterProfile.Yolo;
                return new FaceTrackPostProcessOptions
                {
                    MaxTrackGap = 9,
                    MaxFillGap = 4,
                    MaxLostFillFrames = cleanupYoloFalsePositives ? 8 : 3,
                    MaxInitialFillFrames = 0,
                    InitialFillRequiresInwardMotion = false,
                    MaxConfirmedTrackHoldFrames = 8,
                    AllowSmallTrackLostFill = cleanupYoloFalsePositives,
                    WeakConfidence = 0.18f,
                    StrongConfidence = 0.50f,
                    SyntheticFillConfidenceMax = YoloSyntheticFillConfidenceMax,
                    DropShortTrackMaxDetections = cleanupYoloFalsePositives ? 2 : 0,
                    DropShortSmallTrackMaxDetections = cleanupYoloFalsePositives ? 3 : 0,
                    ShortTrackMaxConfidence = cleanupYoloFalsePositives ? 0.48f : 0f,
                    DropSparseTrackMaxDetections = cleanupYoloFalsePositives ? 3 : 0,
                    DropSparseTrackMinSpanFrames = 8,
                    DropSparseTrackMaxDensity = cleanupYoloFalsePositives ? 0.42 : 0.0,
                    SparseTrackMaxConfidence = cleanupYoloFalsePositives ? 0.56f : 0f,
                    EdgeTailMaxConfidence = 0f,
                    EdgeTailMinStableDetections = 3,
                    EdgeLostFillMaxConfidence = cleanupYoloFalsePositives ? 0f : 0.60f,
                    SmallTrackMaxAreaRatio = 0.00070,
                    MinTrackIou = 0.08,
                    MaxCenterShiftRatio = cleanupYoloFalsePositives ? 0.72 : 0.55,
                    MaxConfirmedTrackBridgeCenterShiftRatio = cleanupYoloFalsePositives ? 1.20 : 0.90,
                    MaxAreaChangeRatio = 4.0,
                    DuplicateIou = TemporalDuplicateIouMin,
                    UnstableTailMaxConfidence = 0f,
                    UnstableTailMinStableDetections = 3,
                    UnstableTailMinIou = 0.45,
                    UnstableTailMaxAreaChangeRatio = 1.8,
                    LowerFrameTrackMaxConfidence = 0f,
                    LowerFrameTrackMinCenterYRatio = 0.58,
                    LowerFrameTrackMinAreaRatio = 0.015,
                    LowerFrameTrackMaxAreaRatio = 0.045,
                    MinTrackMatchScore = 0.35,
                    MinCenterContinuity = 0.30,
                    EdgePartialFaceMarginRatio = 0.06,
                    ConfirmedTrackMinDetections = 3,
                    EnableWeightedTrackEvidence = cleanupYoloFalsePositives,
                    MinConfirmedTrackEvidenceScore = cleanupYoloFalsePositives ? 0.66 : 0.0,
                    LowEvidenceRejectScore = cleanupYoloFalsePositives ? 0.64 : 0.0,
                    LowEvidenceRejectMaxConfidence = cleanupYoloFalsePositives ? 0.60f : 0f,
                    LowEvidenceRejectMaxDetections = cleanupYoloFalsePositives ? 3 : 0,
                    EvidencePersistenceDetections = 5,
                    StrongTrackEvidenceScore = cleanupYoloFalsePositives ? 0.80 : 1.01,
                    MaxStrongTrackLostFillFrames = cleanupYoloFalsePositives ? 12 : 0
                };
            }

            if (profile == FaceFilterProfile.Yolo)
            {
                if (missRecoveryOnly)
                {
                    return new FaceTrackPostProcessOptions
                    {
                        MaxTrackGap = 2,
                        MaxFillGap = 0,
                        MaxLostFillFrames = 1,
                        MaxInitialFillFrames = 0,
                        InitialFillRequiresInwardMotion = false,
                        MaxConfirmedTrackHoldFrames = 0,
                        AllowSmallTrackLostFill = false,
                        WeakConfidence = 0.38f,
                        StrongConfidence = 0.58f,
                        SyntheticFillConfidenceMax = YoloSyntheticFillConfidenceMax,
                        DropShortTrackMaxDetections = 0,
                        DropShortSmallTrackMaxDetections = 0,
                        ShortTrackMaxConfidence = 0f,
                        DropSparseTrackMaxDetections = 0,
                        DropSparseTrackMinSpanFrames = 8,
                        DropSparseTrackMaxDensity = 0f,
                        SparseTrackMaxConfidence = 0f,
                        EdgeTailMaxConfidence = 0f,
                        EdgeTailMinStableDetections = 3,
                        EdgeLostFillMaxConfidence = 0f,
                        SmallTrackMaxAreaRatio = 0.00075,
                        MinTrackIou = 0.12,
                        MaxCenterShiftRatio = 0.45,
                        MaxConfirmedTrackBridgeCenterShiftRatio = 0.9,
                        MaxAreaChangeRatio = 2.4,
                        DuplicateIou = TemporalDuplicateIouMin,
                        UnstableTailMaxConfidence = 0f,
                        UnstableTailMinStableDetections = 3,
                        UnstableTailMinIou = 0.48,
                        UnstableTailMaxAreaChangeRatio = 1.8,
                        LowerFrameTrackMaxConfidence = 0f,
                        LowerFrameTrackMinCenterYRatio = 0.58,
                        LowerFrameTrackMinAreaRatio = 0.015,
                        LowerFrameTrackMaxAreaRatio = 0.045,
                        MinTrackMatchScore = 0.35,
                        MinCenterContinuity = 0.30,
                        EdgePartialFaceMarginRatio = 0.06,
                        ConfirmedTrackMinDetections = 3
                    };
                }

                return new FaceTrackPostProcessOptions
                {
                    MaxTrackGap = SuspiciousNoFaceMaxGap,
                    MaxFillGap = Math.Min(5, SuspiciousNoFaceMaxGap),
                    MaxLostFillFrames = YoloConfirmedTrackHoldFrames,
                    MaxInitialFillFrames = 3,
                    InitialFillRequiresInwardMotion = true,
                    MaxConfirmedTrackHoldFrames = YoloConfirmedTrackHoldFrames,
                    AllowSmallTrackLostFill = true,
                    WeakConfidence = 0.38f,
                    StrongConfidence = 0.58f,
                    SyntheticFillConfidenceMax = YoloSyntheticFillConfidenceMax,
                    DropShortTrackMaxDetections = 2,
                    DropShortSmallTrackMaxDetections = 3,
                    ShortTrackMaxConfidence = 0.48f,
                    DropSparseTrackMaxDetections = 3,
                    DropSparseTrackMinSpanFrames = 8,
                    DropSparseTrackMaxDensity = 0.42,
                    SparseTrackMaxConfidence = 0.56f,
                    EdgeTailMaxConfidence = 0.50f,
                    EdgeTailMinStableDetections = 3,
                    EdgeLostFillMaxConfidence = 0f,
                    SmallTrackMaxAreaRatio = 0.00070,
                    MinTrackIou = 0.08,
                    MaxCenterShiftRatio = 0.72,
                    MaxConfirmedTrackBridgeCenterShiftRatio = 1.20,
                    MaxAreaChangeRatio = 4.0,
                    DuplicateIou = TemporalDuplicateIouMin,
                    UnstableTailMaxConfidence = 0.45f,
                    UnstableTailMinStableDetections = 3,
                    UnstableTailMinIou = 0.45,
                    UnstableTailMaxAreaChangeRatio = 1.8,
                    LowerFrameTrackMaxConfidence = 0.50f,
                    LowerFrameTrackMinCenterYRatio = 0.58,
                    LowerFrameTrackMinAreaRatio = 0.015,
                    LowerFrameTrackMaxAreaRatio = 0.045,
                    ConfirmedTrackMinDetections = 3,
                    EnableWeightedTrackEvidence = true,
                    MinConfirmedTrackEvidenceScore = 0.66,
                    LowEvidenceRejectScore = 0.64,
                    LowEvidenceRejectMaxConfidence = 0.60f,
                    LowEvidenceRejectMaxDetections = 3,
                    EvidencePersistenceDetections = 5,
                    StrongTrackEvidenceScore = 0.80,
                    MaxStrongTrackLostFillFrames = 10
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

        private static IReadOnlyList<Rect>? FindNearestTemporalFaces(
            IReadOnlyDictionary<int, List<Rect>> facesByFrame,
            int frameIndex,
            int direction,
            int maxDistanceFrames,
            int totalFrames,
            IReadOnlySet<int>? blockedCutStarts)
        {
            if (maxDistanceFrames <= 0 || direction == 0)
                return null;

            int index = frameIndex + direction;
            int searched = 0;
            int previousIndex = frameIndex;
            while (index >= 0 && index < totalFrames)
            {
                if (IsBlockedTemporalSmoothingStep(
                        previousIndex,
                        index,
                        blockedCutStarts))
                {
                    break;
                }

                searched++;
                if (searched > maxDistanceFrames)
                    break;

                if (facesByFrame.TryGetValue(index, out var faces) &&
                    faces.Count > 0)
                {
                    return faces;
                }

                previousIndex = index;
                index += direction;
            }

            return null;
        }

        private static IReadOnlySet<int> BuildTemporalSmoothingCutStarts(
            IReadOnlyList<string>? cutPairs,
            CancellationToken cancellationToken)
        {
            if (cutPairs == null || cutPairs.Count == 0)
                return new HashSet<int>();

            var blocked = new HashSet<int>();
            foreach (string pair in cutPairs)
            {
                cancellationToken.ThrowIfCancellationRequested();
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
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    blocked.Add(frame);
                }
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
    }
}
