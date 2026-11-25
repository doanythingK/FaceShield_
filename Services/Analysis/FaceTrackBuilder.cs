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
            CancellationToken cancellationToken = default,
            IReadOnlySet<int>? blockedSceneCutStarts = null,
            bool preferMotionContinuity = false)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int[] sceneCuts = blockedSceneCutStarts == null
                ? Array.Empty<int>()
                : blockedSceneCutStarts.OrderBy(static index => index).ToArray();
            var tracks = new List<FaceTrack>();
            int nextTrackId = 1;

            var orderedFrames = detectionsByFrame.OrderBy(static x => x.Key).ToArray();
            // Only expire tracks when the dictionary keys really describe
            // detection order. Preserve the historical search for callers
            // supplying inconsistent or negative frame indices.
            bool canExpire = true;
            foreach (var frame in orderedFrames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var detection in frame.Value)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (frame.Key < 0 || detection.FrameIndex != frame.Key)
                    {
                        canExpire = false;
                        break;
                    }
                }
                if (!canExpire)
                    break;
            }

            // Keep the full list for downstream interpolation and stable IDs.
            // Candidate order remains creation order, including score ties.
            var activeTracks = new List<FaceTrack>();
            List<FaceTrack> candidates = canExpire ? activeTracks : tracks;
            foreach (var frame in orderedFrames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var frameDetections = frame.Value
                    .OrderByDescending(static x => x.Confidence)
                    .ToArray();
                if (frameDetections.Length == 0)
                    continue;

                if (canExpire)
                {
                    activeTracks.RemoveAll(track =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return frame.Key - track.EndFrame > options.MaxTrackGap ||
                            track.EndFrame < LastSceneStart(frame.Key, sceneCuts);
                    });
                }

                var usedTracks = new HashSet<int>();
                foreach (var detection in frameDetections)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    FaceTrack? bestTrack = null;
                    double bestScore = double.NegativeInfinity;
                    int sceneStart = LastSceneStart(detection.FrameIndex, sceneCuts);
                    // Motion is a ranking hint only among candidates already
                    // admitted by the existing geometry/area/score gates.
                    var eligible = preferMotionContinuity && canExpire
                        ? new List<(FaceTrack Track, double Score)>() : null;

                    foreach (var track in candidates)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (usedTracks.Contains(track.Id))
                            continue;

                        var last = track.LastDetection;
                        if (last == null || last.Value.FrameIndex < sceneStart)
                            continue;
                        if (preferMotionContinuity && last.Value.Size != detection.Size)
                            continue;

                        int gap = detection.FrameIndex - last.Value.FrameIndex;
                        if (gap <= 0 || gap > options.MaxTrackGap)
                            continue;

                        double score = ScoreTransition(last.Value.Bounds, detection.Bounds, gap, options);
                        if (eligible != null && double.IsFinite(score) &&
                            score >= options.MinTrackMatchScore)
                            eligible.Add((track, score));
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestTrack = track;
                        }
                    }

                    // Re-rank only an actual ambiguous choice. Require all
                    // candidates to have reliable motion history so a new
                    // or stationary candidate is not unfairly disadvantaged.
                    if (eligible is { Count: > 1 })
                    {
                        FaceTrack? motionWinner = null;
                        double bestMotionScore = double.NegativeInfinity;
                        bool allReliable = true;
                        foreach (var candidate in eligible)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (!TryPredictBounds(candidate.Track, detection.FrameIndex, options,
                                    out Rect predicted))
                            {
                                allReliable = false;
                                break;
                            }

                            double residual = GetNormalizedCenterShift(predicted, detection.Bounds);
                            double motionScore = 2.0 * IoU(predicted, detection.Bounds) +
                                Math.Max(0.0, 1.0 - residual);
                            double rank = 0.25 * candidate.Score + 0.75 * motionScore;
                            if (rank > bestMotionScore)
                            {
                                bestMotionScore = rank;
                                motionWinner = candidate.Track;
                            }
                        }
                        if (allReliable && motionWinner != null)
                            bestTrack = motionWinner;
                    }

                    if (bestTrack == null || bestScore < options.MinTrackMatchScore)
                    {
                        bestTrack = new FaceTrack(nextTrackId++);
                        tracks.Add(bestTrack);
                        if (canExpire)
                            activeTracks.Add(bestTrack);
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

        private static int LastSceneStart(int frameIndex, int[] cuts)
        {
            int position = Array.BinarySearch(cuts, frameIndex);
            if (position < 0)
                position = ~position - 1;
            return position >= 0 ? cuts[position] : int.MinValue;
        }

        private static bool TryPredictBounds(FaceTrack track, int nextFrame,
            FaceTrackPostProcessOptions options, out Rect predicted)
        {
            predicted = default;
            var history = track.Detections;
            if (history.Count < 3)
                return false;
            var a = history[^3];
            var b = history[^2];
            var c = history[^1];
            int ab = b.FrameIndex - a.FrameIndex;
            int bc = c.FrameIndex - b.FrameIndex;
            int gap = nextFrame - c.FrameIndex;
            // Do not extrapolate farther than the recent sampling interval.
            if (ab <= 0 || bc <= 0 || gap <= 0 || gap > Math.Min(ab, bc) ||
                a.Size != b.Size || b.Size != c.Size)
                return false;
            double ax = a.Bounds.X + a.Bounds.Width * 0.5;
            double ay = a.Bounds.Y + a.Bounds.Height * 0.5;
            double bx = b.Bounds.X + b.Bounds.Width * 0.5;
            double by = b.Bounds.Y + b.Bounds.Height * 0.5;
            double cx = c.Bounds.X + c.Bounds.Width * 0.5;
            double cy = c.Bounds.Y + c.Bounds.Height * 0.5;
            double vx0 = (bx - ax) / ab, vy0 = (by - ay) / ab;
            double vx1 = (cx - bx) / bc, vy1 = (cy - by) / bc;
            double dim = Math.Max(1.0, Math.Max(c.Bounds.Width, c.Bounds.Height));
            double speed = Math.Sqrt(vx1 * vx1 + vy1 * vy1);
            double disagreement = Math.Sqrt(
                (vx1 - vx0) * (vx1 - vx0) + (vy1 - vy0) * (vy1 - vy0));
            double dx = vx1 * gap, dy = vy1 * gap;
            if (!double.IsFinite(speed) || !double.IsFinite(disagreement) ||
                disagreement > dim * 0.15 ||
                Math.Sqrt(dx * dx + dy * dy) > dim * options.MaxCenterShiftRatio)
                return false;
            predicted = new Rect(c.Bounds.X + dx, c.Bounds.Y + dy,
                c.Bounds.Width, c.Bounds.Height);
            return true;
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
