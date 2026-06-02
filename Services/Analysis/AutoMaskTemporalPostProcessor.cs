using Avalonia;
using FaceShield.Services.Video;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

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
            bool useTracking)
        {
            if (!useTracking)
                return FaceTrackPostProcessResult.Empty;

            var result = new FaceTrackInterpolator().Apply(
                maskProvider,
                totalFrames,
                BuildTrackPostProcessOptions(profile));

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

        public void ApplyTemporalSmoothing(
            FrameMaskProvider maskProvider,
            int totalFrames,
            IReadOnlyList<string>? blockedCutPairs = null)
        {
            if (totalFrames < 3)
                return;

            var blockedCutStarts = BuildTemporalSmoothingCutStarts(blockedCutPairs);
            var facesByFrame = new List<Rect>?[totalFrames];
            var confByFrame = new List<float>?[totalFrames];
            var sizeByFrame = new PixelSize[totalFrames];
            var hasStored = new bool[totalFrames];

            foreach (int index in maskProvider.GetStoredMaskFrameIndices())
            {
                if (index >= 0 && index < totalFrames)
                    hasStored[index] = true;
            }

            foreach (var entry in maskProvider.GetFaceMaskEntries())
            {
                int i = entry.Key;
                if (i < 0 || i >= totalFrames || hasStored[i])
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
                for (int i = 1; i < totalFrames - 1; i++)
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

            for (int i = 0; i < totalFrames; i++)
            {
                if (hasStored[i] || facesByFrame[i] == null || facesByFrame[i]!.Count == 0 || sizeByFrame[i].Width <= 0 || sizeByFrame[i].Height <= 0)
                    continue;

                maskProvider.SetFaceRects(
                    i,
                    facesByFrame[i]!,
                    sizeByFrame[i],
                    minConfidence: confByFrame[i] == null || confByFrame[i]!.Count == 0 ? null : confByFrame[i]!.Min(),
                    confidences: confByFrame[i]);
            }
        }

        public static FaceTrackPostProcessOptions BuildTrackPostProcessOptions(FaceFilterProfile profile)
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
                    EdgeLostFillMaxConfidence = 0.60f,
                    SmallTrackMaxAreaRatio = 0.00070,
                    MinTrackIou = 0.08,
                    MaxCenterShiftRatio = 0.72,
                    MaxConfirmedTrackBridgeCenterShiftRatio = 1.20,
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
    }
}
