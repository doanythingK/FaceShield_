using Avalonia;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace FaceShield.Services.Analysis
{
    public sealed class FaceTrackBuilder
    {
        public IReadOnlyList<FaceTrack> Build(
            IReadOnlyDictionary<int, IReadOnlyList<FaceTrackDetection>> detectionsByFrame,
            FaceTrackPostProcessOptions options,
            CancellationToken cancellationToken = default)
        {
            var tracks = new List<FaceTrack>();
            int nextTrackId = 1;

            foreach (var frame in detectionsByFrame.OrderBy(static x => x.Key))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var frameDetections = frame.Value
                    .OrderByDescending(static x => x.Confidence)
                    .ToArray();
                if (frameDetections.Length == 0)
                    continue;

                var usedTracks = new HashSet<int>();
                foreach (var detection in frameDetections)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    FaceTrack? bestTrack = null;
                    double bestScore = double.NegativeInfinity;

                    foreach (var track in tracks)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (usedTracks.Contains(track.Id))
                            continue;

                        var last = track.LastDetection;
                        if (last == null)
                            continue;

                        int gap = detection.FrameIndex - last.Value.FrameIndex;
                        if (gap <= 0 || gap > options.MaxTrackGap)
                            continue;

                        double score = ScoreTransition(last.Value.Bounds, detection.Bounds, gap, options);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestTrack = track;
                        }
                    }

                    if (bestTrack == null || bestScore < options.MinTrackMatchScore)
                    {
                        bestTrack = new FaceTrack(nextTrackId++);
                        tracks.Add(bestTrack);
                    }
                    else
                    {
                        usedTracks.Add(bestTrack.Id);
                    }

                    bestTrack.Add(detection);
                }
            }

            return tracks;
        }

        private static double ScoreTransition(Rect previous, Rect current, int gap, FaceTrackPostProcessOptions options)
        {
            double iou = IoU(previous, current);
            double areaRatio = GetAreaRatio(previous, current);
            if (areaRatio > options.MaxAreaChangeRatio || areaRatio < 1.0 / options.MaxAreaChangeRatio)
                return double.NegativeInfinity;

            double centerShift = GetNormalizedCenterShift(previous, current);
            double allowedShift = options.MaxCenterShiftRatio * Math.Max(1, gap);
            if (centerShift > allowedShift)
                return double.NegativeInfinity;

            double continuityBonus = Math.Max(0.0, 1.0 - centerShift / Math.Max(allowedShift, 0.001));
            if (iou < options.MinTrackIou && continuityBonus < options.MinCenterContinuity)
                return double.NegativeInfinity;

            return (iou * 2.0) + continuityBonus - (gap - 1) * 0.08;
        }

        internal static double IoU(Rect a, Rect b)
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
            return union <= 0.0 ? 0.0 : inter / union;
        }

        internal static double GetAreaRatio(Rect a, Rect b)
        {
            double areaA = Math.Max(1.0, a.Width * a.Height);
            double areaB = Math.Max(1.0, b.Width * b.Height);
            return areaA / areaB;
        }

        internal static double GetNormalizedCenterShift(Rect a, Rect b)
        {
            double ax = a.X + a.Width * 0.5;
            double ay = a.Y + a.Height * 0.5;
            double bx = b.X + b.Width * 0.5;
            double by = b.Y + b.Height * 0.5;
            double shift = Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));
            double maxDim = Math.Max(1.0, Math.Max(a.Width, a.Height));
            return shift / maxDim;
        }
    }

    internal readonly record struct FaceTrackEvidence(
        double Score,
        float MeanConfidence,
        double DetectionDensity,
        double Continuity,
        double Persistence);

    internal static class FaceTrackEvidenceScorer
    {
        internal static FaceTrackEvidence Evaluate(
            FaceTrack track,
            FaceTrackPostProcessOptions options)
        {
            if (track.DetectionCount <= 0)
                return default;

            double confidenceSum = 0.0;
            foreach (var detection in track.Detections)
                confidenceSum += Math.Clamp(detection.Confidence, 0.0f, 1.0f);

            float meanConfidence = (float)(confidenceSum / track.DetectionCount);
            int span = Math.Max(
                1,
                track.Detections[^1].FrameIndex -
                track.Detections[0].FrameIndex + 1);
            double density = Math.Clamp(
                track.DetectionCount / (double)span,
                0.0,
                1.0);
            double persistence = Math.Clamp(
                track.DetectionCount /
                (double)Math.Max(1, options.EvidencePersistenceDetections),
                0.0,
                1.0);
            double continuity = ComputeContinuity(track, options);

            // Privacy-oriented evidence: detector confidence is the largest term,
            // while temporal stability must still support weak/moderate detections.
            double score = Math.Clamp(
                meanConfidence * 0.50 +
                density * 0.15 +
                continuity * 0.20 +
                persistence * 0.15,
                0.0,
                1.0);

            return new FaceTrackEvidence(
                score,
                meanConfidence,
                density,
                continuity,
                persistence);
        }

        private static double ComputeContinuity(
            FaceTrack track,
            FaceTrackPostProcessOptions options)
        {
            if (track.DetectionCount < 2)
                return 0.0;

            double sum = 0.0;
            int transitions = 0;
            double areaLimit = Math.Max(1.01, options.MaxAreaChangeRatio);
            double areaLogLimit = Math.Log(areaLimit);
            double iouTarget = Math.Max(0.20, options.MinTrackIou * 2.0);

            for (int i = 1; i < track.Detections.Count; i++)
            {
                var previous = track.Detections[i - 1];
                var current = track.Detections[i];
                int gap = Math.Max(1, current.FrameIndex - previous.FrameIndex);
                double allowedShift = Math.Max(
                    0.001,
                    options.MaxCenterShiftRatio * gap);
                double centerShift = FaceTrackBuilder.GetNormalizedCenterShift(
                    previous.Bounds,
                    current.Bounds);
                double centerScore = Math.Clamp(
                    1.0 - centerShift / allowedShift,
                    0.0,
                    1.0);

                double rawAreaRatio = FaceTrackBuilder.GetAreaRatio(
                    previous.Bounds,
                    current.Bounds);
                double symmetricAreaRatio = rawAreaRatio <= 0.0
                    ? areaLimit
                    : Math.Max(rawAreaRatio, 1.0 / rawAreaRatio);
                double areaScore = Math.Clamp(
                    1.0 - Math.Log(Math.Max(1.0, symmetricAreaRatio)) / areaLogLimit,
                    0.0,
                    1.0);

                double iouScore = Math.Clamp(
                    FaceTrackBuilder.IoU(previous.Bounds, current.Bounds) / iouTarget,
                    0.0,
                    1.0);
                double gapWeight = 0.75 + 0.25 / gap;
                sum += (
                    centerScore * 0.45 +
                    areaScore * 0.30 +
                    iouScore * 0.25) * gapWeight;
                transitions++;
            }

            return transitions == 0
                ? 0.0
                : Math.Clamp(sum / transitions, 0.0, 1.0);
        }
    }

    public sealed record FaceTrackPostProcessOptions
    {
        public int MaxTrackGap { get; init; } = 8;
        public int MaxFillGap { get; init; } = 5;
        public int MaxLostFillFrames { get; init; } = 3;
        public int MaxInitialFillFrames { get; init; } = 0;
        public bool InitialFillRequiresInwardMotion { get; init; } = false;
        public int MaxConfirmedTrackHoldFrames { get; init; } = 3;
        public int ConfirmedTrackMinDetections { get; init; } = 3;
        public bool EnableWeightedTrackEvidence { get; init; } = false;
        public double MinConfirmedTrackEvidenceScore { get; init; } = 0.0;
        public double LowEvidenceRejectScore { get; init; } = 0.0;
        public float LowEvidenceRejectMaxConfidence { get; init; } = 0.0f;
        public int LowEvidenceRejectMaxDetections { get; init; } = 0;
        public int EvidencePersistenceDetections { get; init; } = 5;
        public double StrongTrackEvidenceScore { get; init; } = 1.01;
        public int MaxStrongTrackLostFillFrames { get; init; } = 0;
        public bool AllowSmallTrackLostFill { get; init; } = false;
        public int DropShortTrackMaxDetections { get; init; } = 1;
        public int DropShortSmallTrackMaxDetections { get; init; } = 2;
        public int DropSparseTrackMaxDetections { get; init; } = 0;
        public int DropSparseTrackMinSpanFrames { get; init; } = 8;
        public double DropSparseTrackMaxDensity { get; init; } = 0.45;
        public float WeakConfidence { get; init; } = 0.50f;
        public float StrongConfidence { get; init; } = 0.68f;
        public float SyntheticFillConfidenceMax { get; init; } = 1.0f;
        public float ShortTrackMaxConfidence { get; init; } = 0.68f;
        public float SparseTrackMaxConfidence { get; init; } = 0.0f;
        public float EdgeTailMaxConfidence { get; init; } = 0.0f;
        public int EdgeTailMinStableDetections { get; init; } = 3;
        public float EdgeLostFillMaxConfidence { get; init; } = 0.0f;
        public double SmallTrackMaxAreaRatio { get; init; } = 0.00075;
        public double EdgePartialFaceMarginRatio { get; init; } = 0.06;
        public double MinTrackIou { get; init; } = 0.12;
        public double MinTrackMatchScore { get; init; } = 0.20;
        public double MinCenterContinuity { get; init; } = 0.25;
        public double MaxCenterShiftRatio { get; init; } = 0.62;
        public double MaxConfirmedTrackBridgeCenterShiftRatio { get; init; } = 0.0;
        public double MaxAreaChangeRatio { get; init; } = 3.2;
        public double DuplicateIou { get; init; } = 0.35;
        public float UnstableTailMaxConfidence { get; init; } = 0.0f;
        public int UnstableTailMinStableDetections { get; init; } = 3;
        public double UnstableTailMinIou { get; init; } = 0.45;
        public double UnstableTailMaxAreaChangeRatio { get; init; } = 1.8;
        public float LowerFrameTrackMaxConfidence { get; init; } = 0.0f;
        public double LowerFrameTrackMinCenterYRatio { get; init; } = 0.58;
        public double LowerFrameTrackMinAreaRatio { get; init; } = 0.015;
        public double LowerFrameTrackMaxAreaRatio { get; init; } = 0.045;
    }
}
