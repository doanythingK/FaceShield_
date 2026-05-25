using Avalonia;
using FaceShield.Services.Video;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace FaceShield.Services.Analysis
{
    public sealed class FaceTrackSceneCutGuard
    {
        public const double DefaultDifferenceThreshold = 0.32;
        public const double DefaultDirectDifferenceThreshold = 0.36;

        public IReadOnlyList<FaceTrackFilledFace> BuildWeakTrackTransitionCandidates(
            FrameMaskProvider maskProvider,
            FaceTrackPostProcessOptions options,
            float maxTargetConfidence,
            int maxTransitionGap,
            float minConfidenceDrop = 0.10f,
            int maxPostCutCarryFrames = 3,
            float minSourceConfidence = 0.0f)
        {
            if (maskProvider == null)
                throw new ArgumentNullException(nameof(maskProvider));
            if (maxTargetConfidence <= 0 || maxTransitionGap <= 0)
                return Array.Empty<FaceTrackFilledFace>();

            var hasStoredMask = new HashSet<int>(maskProvider.GetStoredMaskFrameIndices());
            var detectionsByFrame = new Dictionary<int, IReadOnlyList<FaceTrackDetection>>();

            foreach (var entry in maskProvider.GetFaceMaskEntries())
            {
                int frameIndex = entry.Key;
                if (hasStoredMask.Contains(frameIndex))
                    continue;

                var data = entry.Value;
                if (data.Faces.Count == 0)
                    continue;

                var detections = new List<FaceTrackDetection>(data.Faces.Count);
                for (int i = 0; i < data.Faces.Count; i++)
                {
                    float confidence = i < data.Confidences.Count
                        ? data.Confidences[i]
                        : data.MinConfidence ?? 1.0f;
                    detections.Add(new FaceTrackDetection(frameIndex, data.Faces[i], data.Size, confidence));
                }

                detectionsByFrame[frameIndex] = detections;
            }

            if (detectionsByFrame.Count == 0)
                return Array.Empty<FaceTrackFilledFace>();

            var tracks = new FaceTrackBuilder().Build(detectionsByFrame, options);
            var candidates = new List<FaceTrackFilledFace>();
            foreach (var track in tracks)
            {
                var detections = track.Detections;
                for (int i = 1; i < detections.Count; i++)
                {
                    var previous = detections[i - 1];
                    var current = detections[i];
                    int gap = current.FrameIndex - previous.FrameIndex;
                    if (gap <= 0 || gap > maxTransitionGap)
                        continue;
                    if (current.Confidence > maxTargetConfidence)
                        continue;
                    if (minSourceConfidence > 0 &&
                        previous.Confidence < minSourceConfidence)
                    {
                        continue;
                    }
                    if (minConfidenceDrop > 0 &&
                        previous.Confidence - current.Confidence < minConfidenceDrop)
                    {
                        continue;
                    }

                    int carryEndFrame = current.FrameIndex + Math.Max(0, maxPostCutCarryFrames);
                    int lastAddedIndex = i - 1;
                    for (int j = i; j < detections.Count; j++)
                    {
                        var carried = detections[j];
                        if (carried.FrameIndex > carryEndFrame ||
                            carried.Confidence > maxTargetConfidence)
                        {
                            break;
                        }

                        candidates.Add(new FaceTrackFilledFace(
                            carried.FrameIndex,
                            carried.Bounds,
                            carried.Size,
                            carried.Confidence,
                            previous.FrameIndex));
                        lastAddedIndex = j;
                    }

                    i = Math.Max(i, lastAddedIndex);
                }
            }

            return candidates;
        }

        public IReadOnlyList<FaceTrackFilledFace> BuildWeakPostCutCarryCandidates(
            FrameMaskProvider maskProvider,
            float maxTargetConfidence,
            int maxCarryFrames,
            int sourceLookbackFrames = 1,
            double edgeMarginRatio = 0.02,
            double minIou = 0.15,
            double maxCenterShiftRatio = 0.65,
            double maxAreaChangeRatio = 3.0,
            bool includeEdgeCandidates = false)
        {
            if (maskProvider == null)
                throw new ArgumentNullException(nameof(maskProvider));
            if (maxTargetConfidence <= 0 || maxCarryFrames <= 0)
                return Array.Empty<FaceTrackFilledFace>();

            var entries = maskProvider.GetFaceMaskEntries()
                .Where(static x => x.Value.Faces.Count > 0)
                .OrderBy(static x => x.Key)
                .ToDictionary(static x => x.Key, static x => x.Value);
            if (entries.Count == 0)
                return Array.Empty<FaceTrackFilledFace>();

            var candidates = new List<FaceTrackFilledFace>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var seenTargets = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                int frameIndex = entry.Key;
                if (frameIndex <= 0)
                    continue;

                var data = entry.Value;
                for (int i = 0; i < data.Faces.Count; i++)
                {
                    var face = data.Faces[i];
                    float confidence = GetConfidence(data, i);
                    if (!IsWeakCandidate(face, data.Size, confidence, maxTargetConfidence, edgeMarginRatio, includeEdgeCandidates))
                        continue;

                    var run = BuildWeakCarryRun(
                        entries,
                        frameIndex,
                        face,
                        maxTargetConfidence,
                        maxCarryFrames,
                        edgeMarginRatio,
                        minIou,
                        maxCenterShiftRatio,
                        maxAreaChangeRatio,
                        includeEdgeCandidates,
                        out _);
                    if (run.Count == 0)
                        continue;

                    // Long weak runs are still worth checking when they begin
                    // immediately after a scene cut. Skipping them entirely lets
                    // a carried blur survive just because it lasted past the
                    // conservative carry window. A strong continuation still
                    // protects real after-cut detections.
                    if (HasStrongContinuation(
                            entries,
                            run[^1].FrameIndex + 1,
                            run[^1].Bounds,
                            maxTargetConfidence,
                            minIou,
                            maxCenterShiftRatio,
                            maxAreaChangeRatio))
                    {
                        continue;
                    }

                    int lookbackFrames = Math.Max(1, sourceLookbackFrames);
                    foreach (var item in run)
                    {
                        string targetKey = $"{item.FrameIndex}:{Math.Round(item.Bounds.X, 2)}:{Math.Round(item.Bounds.Y, 2)}:{Math.Round(item.Bounds.Width, 2)}:{Math.Round(item.Bounds.Height, 2)}";

                        for (int offset = lookbackFrames; offset >= 1; offset--)
                        {
                            int sourceFrame = frameIndex - offset;
                            if (sourceFrame < 0)
                                continue;
                            if (!HasMatchingSourceFace(
                                    entries,
                                    sourceFrame,
                                    item.Bounds,
                                    maxTargetConfidence,
                                    minIou,
                                    maxCenterShiftRatio,
                                    maxAreaChangeRatio))
                            {
                                continue;
                            }

                            string key = $"{sourceFrame}:{targetKey}";
                            if (!seenTargets.Add(targetKey))
                                continue;
                            if (!seen.Add(key))
                                continue;

                            candidates.Add(new FaceTrackFilledFace(
                                item.FrameIndex,
                                item.Bounds,
                                item.Size,
                                item.Confidence,
                                sourceFrame));
                        }
                    }
                }
            }

            return candidates;
        }

        public FaceTrackSceneCutGuardResult Apply(
            FrameMaskProvider maskProvider,
            IReadOnlyList<FaceTrackFilledFace> candidates,
            Func<int, int, double> frameDifferenceProvider,
            double differenceThreshold = DefaultDifferenceThreshold,
            double directDifferenceThreshold = DefaultDirectDifferenceThreshold,
            int directDifferenceMaxChecks = int.MaxValue,
            int removeMatchingTailFrames = 0,
            float removeMatchingTailMaxConfidence = 0.0f,
            double candidateMatchMinIou = 0.80,
            double candidateMatchMaxCenterShiftRatio = 0.35,
            double candidateMatchMaxAreaChangeRatio = 1.8)
        {
            if (maskProvider == null)
                throw new ArgumentNullException(nameof(maskProvider));
            if (frameDifferenceProvider == null)
                throw new ArgumentNullException(nameof(frameDifferenceProvider));
            if (candidates.Count == 0)
                return FaceTrackSceneCutGuardResult.Empty;

            candidates = DeduplicateCandidates(candidates);
            if (candidates.Count == 0)
                return FaceTrackSceneCutGuardResult.Empty;

            var sw = Stopwatch.StartNew();
            int checkedCandidates = 0;
            int removedCandidates = 0;
            double maxDifference = 0.0;
            var checkedFramePairs = new List<string>();
            var removedFrameIndices = new List<int>();
            var cutFramePairs = new List<string>();
            var differenceByPair = new Dictionary<(int Source, int Target), double>();
            int directDifferenceChecks = 0;
            int directDifferenceSkipped = 0;
            int directDifferenceBudget = Math.Max(0, directDifferenceMaxChecks);

            foreach (var candidate in candidates)
            {
                int sourceFrame = Math.Min(candidate.SourceFrameIndex, candidate.FrameIndex);
                int targetFrame = Math.Max(candidate.SourceFrameIndex, candidate.FrameIndex);
                if (sourceFrame < 0 || targetFrame <= sourceFrame)
                    continue;

                checkedCandidates++;
                checkedFramePairs.Add(FormatFramePair(sourceFrame, targetFrame));
                double difference = GetMaxFrameDifference(
                    sourceFrame,
                    targetFrame,
                    frameDifferenceProvider,
                    differenceByPair,
                    out string cutFramePair);

                bool isCut = difference >= differenceThreshold;
                if (!isCut &&
                    targetFrame > sourceFrame + 1 &&
                    directDifferenceThreshold > 0)
                {
                    if (directDifferenceChecks < directDifferenceBudget)
                    {
                        directDifferenceChecks++;
                        double directDifference = GetFrameDifference(
                            sourceFrame,
                            targetFrame,
                            frameDifferenceProvider,
                            differenceByPair);
                        if (directDifference > difference)
                        {
                            difference = directDifference;
                            cutFramePair = FormatFramePair(sourceFrame, targetFrame);
                        }

                        isCut = directDifference >= directDifferenceThreshold;
                    }
                    else
                    {
                        directDifferenceSkipped++;
                    }
                }

                maxDifference = Math.Max(maxDifference, difference);

                if (!isCut)
                    continue;

                cutFramePairs.Add(cutFramePair);
                if (RemoveFaceCandidate(
                        maskProvider,
                        candidate.FrameIndex,
                        candidate.Bounds,
                        candidateMatchMinIou,
                        candidateMatchMaxCenterShiftRatio,
                        candidateMatchMaxAreaChangeRatio))
                {
                    removedCandidates++;
                    removedFrameIndices.Add(candidate.FrameIndex);
                    removedCandidates += RemoveWeakMatchingTail(
                        maskProvider,
                        candidate.FrameIndex,
                        candidate.Bounds,
                        removeMatchingTailFrames,
                        removeMatchingTailMaxConfidence,
                        removedFrameIndices);
                }
            }

            sw.Stop();
            return new FaceTrackSceneCutGuardResult(
                checkedCandidates,
                removedCandidates,
                removedFrameIndices.ToArray(),
                checkedFramePairs.ToArray(),
                differenceThreshold,
                maxDifference,
                cutFramePairs.ToArray(),
                sw.ElapsedMilliseconds,
                directDifferenceChecks,
                directDifferenceSkipped,
                null);
        }

        public FaceTrackSceneCutGuardResult Apply(
            FrameMaskProvider maskProvider,
            string videoPath,
            IReadOnlyList<FaceTrackFilledFace> candidates,
            double differenceThreshold = DefaultDifferenceThreshold,
            double directDifferenceThreshold = DefaultDirectDifferenceThreshold,
            int directDifferenceMaxChecks = int.MaxValue,
            int removeMatchingTailFrames = 0,
            float removeMatchingTailMaxConfidence = 0.0f,
            double candidateMatchMinIou = 0.80,
            double candidateMatchMaxCenterShiftRatio = 0.35,
            double candidateMatchMaxAreaChangeRatio = 1.8,
            CancellationToken cancellationToken = default)
        {
            if (maskProvider == null)
                throw new ArgumentNullException(nameof(maskProvider));
            if (string.IsNullOrWhiteSpace(videoPath) || candidates.Count == 0)
                return FaceTrackSceneCutGuardResult.Empty;

            candidates = DeduplicateCandidates(candidates);
            if (candidates.Count == 0)
                return FaceTrackSceneCutGuardResult.Empty;

            var sw = Stopwatch.StartNew();
            int checkedCandidates = 0;
            int removedCandidates = 0;
            double maxDifference = 0.0;
            var checkedFramePairs = new List<string>();
            var removedFrameIndices = new List<int>();
            var cutFramePairs = new List<string>();
            var differenceByPair = new Dictionary<(int Source, int Target), double>();
            int directDifferenceChecks = 0;
            int directDifferenceSkipped = 0;
            int directDifferenceBudget = Math.Max(0, directDifferenceMaxChecks);

            try
            {
                using var extractor = new FfFrameExtractor(videoPath, enableHardware: false);
                var frameSize = extractor.FrameSize;
                if (frameSize.Width <= 0 || frameSize.Height <= 0)
                    return FaceTrackSceneCutGuardResult.Empty;

                int sampleWidth = Math.Min(320, frameSize.Width);
                int sampleHeight = Math.Max(1, (int)Math.Round(frameSize.Height * (sampleWidth / (double)frameSize.Width)));
                int sampleBytes = sampleWidth * sampleHeight * 4;
                var pool = ArrayPool<byte>.Shared;
                byte[] sourceBuffer = pool.Rent(sampleBytes);
                byte[] targetBuffer = pool.Rent(sampleBytes);
                try
                {
                    var ranges = BuildFrameDifferenceRanges(candidates);
                    PrecomputeFrameDifferences(
                        extractor,
                        ranges,
                        sampleWidth,
                        sampleHeight,
                        sourceBuffer,
                        targetBuffer,
                        differenceByPair,
                        cancellationToken);

                    foreach (var candidate in candidates)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        int sourceFrame = Math.Min(candidate.SourceFrameIndex, candidate.FrameIndex);
                        int targetFrame = Math.Max(candidate.SourceFrameIndex, candidate.FrameIndex);
                        if (sourceFrame < 0 || targetFrame <= sourceFrame)
                            continue;

                        checkedCandidates++;
                        checkedFramePairs.Add(FormatFramePair(sourceFrame, targetFrame));
                        if (!TryGetMaxFrameDifferenceFromCache(
                                sourceFrame,
                                targetFrame,
                                differenceByPair,
                                out double difference,
                                out string cutFramePair))
                        {
                            continue;
                        }

                        bool isCut = difference >= differenceThreshold;
                        if (!isCut &&
                            targetFrame > sourceFrame + 1 &&
                            directDifferenceThreshold > 0)
                        {
                            if (directDifferenceChecks < directDifferenceBudget)
                            {
                                directDifferenceChecks++;
                                if (TryGetFramePairDifference(
                                        extractor,
                                        sourceFrame,
                                        targetFrame,
                                        sampleWidth,
                                        sampleHeight,
                                        sourceBuffer,
                                        targetBuffer,
                                        differenceByPair,
                                        cancellationToken,
                                        out double directDifference))
                                {
                                    if (directDifference > difference)
                                    {
                                        difference = directDifference;
                                        cutFramePair = FormatFramePair(sourceFrame, targetFrame);
                                    }

                                    isCut = directDifference >= directDifferenceThreshold;
                                }
                            }
                            else
                            {
                                directDifferenceSkipped++;
                            }
                        }

                        maxDifference = Math.Max(maxDifference, difference);

                        if (!isCut)
                            continue;

                        cutFramePairs.Add(cutFramePair);
                        if (RemoveFaceCandidate(
                                maskProvider,
                                candidate.FrameIndex,
                                candidate.Bounds,
                                candidateMatchMinIou,
                                candidateMatchMaxCenterShiftRatio,
                                candidateMatchMaxAreaChangeRatio))
                        {
                            removedCandidates++;
                            removedFrameIndices.Add(candidate.FrameIndex);
                            removedCandidates += RemoveWeakMatchingTail(
                                maskProvider,
                                candidate.FrameIndex,
                                candidate.Bounds,
                                removeMatchingTailFrames,
                                removeMatchingTailMaxConfidence,
                                removedFrameIndices);
                        }
                    }
                }
                finally
                {
                    pool.Return(sourceBuffer);
                    pool.Return(targetBuffer);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                sw.Stop();
                return new FaceTrackSceneCutGuardResult(
                    checkedCandidates,
                    removedCandidates,
                    removedFrameIndices.ToArray(),
                    checkedFramePairs.ToArray(),
                    differenceThreshold,
                    maxDifference,
                    cutFramePairs.ToArray(),
                    sw.ElapsedMilliseconds,
                    directDifferenceChecks,
                    directDifferenceSkipped,
                    ex.Message);
            }

            sw.Stop();
            return new FaceTrackSceneCutGuardResult(
                checkedCandidates,
                removedCandidates,
                removedFrameIndices.ToArray(),
                checkedFramePairs.ToArray(),
                differenceThreshold,
                maxDifference,
                cutFramePairs.ToArray(),
                sw.ElapsedMilliseconds,
                directDifferenceChecks,
                directDifferenceSkipped,
                null);
        }

        private static string FormatFramePair(int sourceFrame, int targetFrame)
            => string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{sourceFrame}->{targetFrame}");

        private static IReadOnlyList<FaceTrackFilledFace> DeduplicateCandidates(
            IReadOnlyList<FaceTrackFilledFace> candidates)
        {
            if (candidates.Count <= 1)
                return candidates;

            var unique = new List<FaceTrackFilledFace>(candidates.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var candidate in candidates)
            {
                if (!seen.Add(GetCandidateKey(candidate)))
                    continue;

                unique.Add(candidate);
            }

            return unique;
        }

        private static string GetCandidateKey(FaceTrackFilledFace candidate)
            => string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{candidate.SourceFrameIndex}:{candidate.FrameIndex}:{Math.Round(candidate.Bounds.X, 2)}:{Math.Round(candidate.Bounds.Y, 2)}:{Math.Round(candidate.Bounds.Width, 2)}:{Math.Round(candidate.Bounds.Height, 2)}");

        private static List<WeakCarryCandidate> BuildWeakCarryRun(
            IReadOnlyDictionary<int, FrameMaskProvider.FaceMaskData> entries,
            int startFrame,
            Rect startFace,
            float maxTargetConfidence,
            int maxCarryFrames,
            double edgeMarginRatio,
            double minIou,
            double maxCenterShiftRatio,
            double maxAreaChangeRatio,
            bool includeEdgeCandidates,
            out bool exceededLimit)
        {
            var run = new List<WeakCarryCandidate>();
            Rect current = startFace;
            exceededLimit = false;
            for (int offset = 0; offset <= maxCarryFrames; offset++)
            {
                int frameIndex = startFrame + offset;
                if (!entries.TryGetValue(frameIndex, out var data))
                    break;

                if (!TryFindMatchingWeakNonEdgeFace(
                        data,
                        current,
                        maxTargetConfidence,
                        edgeMarginRatio,
                        minIou,
                        maxCenterShiftRatio,
                        maxAreaChangeRatio,
                        includeEdgeCandidates,
                        out var match,
                        out float confidence))
                {
                    break;
                }

                if (offset == maxCarryFrames)
                {
                    exceededLimit = true;
                    break;
                }

                run.Add(new WeakCarryCandidate(frameIndex, match, data.Size, confidence));
                current = match;
            }

            return run;
        }

        private static bool TryFindMatchingWeakNonEdgeFace(
            FrameMaskProvider.FaceMaskData data,
            Rect reference,
            float maxTargetConfidence,
            double edgeMarginRatio,
            double minIou,
            double maxCenterShiftRatio,
            double maxAreaChangeRatio,
            bool includeEdgeCandidates,
            out Rect match,
            out float confidence)
        {
            match = default;
            confidence = 0.0f;
            double bestScore = double.NegativeInfinity;
            for (int i = 0; i < data.Faces.Count; i++)
            {
                var candidate = data.Faces[i];
                float candidateConfidence = GetConfidence(data, i);
                if (!IsWeakCandidate(candidate, data.Size, candidateConfidence, maxTargetConfidence, edgeMarginRatio, includeEdgeCandidates))
                    continue;
                if (!IsMatchingFace(reference, candidate, minIou, maxCenterShiftRatio, maxAreaChangeRatio))
                    continue;

                double score = IoU(reference, candidate);
                if (score > bestScore)
                {
                    bestScore = score;
                    match = candidate;
                    confidence = candidateConfidence;
                }
            }

            return bestScore > double.NegativeInfinity;
        }

        private static bool HasStrongContinuation(
            IReadOnlyDictionary<int, FrameMaskProvider.FaceMaskData> entries,
            int frameIndex,
            Rect face,
            float maxTargetConfidence,
            double minIou,
            double maxCenterShiftRatio,
            double maxAreaChangeRatio)
        {
            if (!entries.TryGetValue(frameIndex, out var data))
                return false;

            for (int i = 0; i < data.Faces.Count; i++)
            {
                if (GetConfidence(data, i) <= maxTargetConfidence)
                    continue;
                if (IsMatchingFace(face, data.Faces[i], minIou, maxCenterShiftRatio, maxAreaChangeRatio))
                    return true;
            }

            return false;
        }

        private static bool HasMatchingFace(
            IReadOnlyDictionary<int, FrameMaskProvider.FaceMaskData> entries,
            int frameIndex,
            Rect face,
            double minIou,
            double maxCenterShiftRatio,
            double maxAreaChangeRatio)
        {
            if (!entries.TryGetValue(frameIndex, out var data))
                return false;

            for (int i = 0; i < data.Faces.Count; i++)
            {
                if (IsMatchingFace(face, data.Faces[i], minIou, maxCenterShiftRatio, maxAreaChangeRatio))
                    return true;
            }

            return false;
        }

        private static bool HasMatchingSourceFace(
            IReadOnlyDictionary<int, FrameMaskProvider.FaceMaskData> entries,
            int frameIndex,
            Rect face,
            float minConfidenceExclusive,
            double minIou,
            double maxCenterShiftRatio,
            double maxAreaChangeRatio)
        {
            if (!entries.TryGetValue(frameIndex, out var data))
                return false;

            for (int i = 0; i < data.Faces.Count; i++)
            {
                if (GetConfidence(data, i) <= minConfidenceExclusive)
                    continue;
                if (IsMatchingFace(face, data.Faces[i], minIou, maxCenterShiftRatio, maxAreaChangeRatio))
                    return true;
            }

            return false;
        }

        private static bool IsWeakCandidate(
            Rect face,
            PixelSize size,
            float confidence,
            float maxTargetConfidence,
            double edgeMarginRatio,
            bool includeEdgeCandidates)
            => confidence <= maxTargetConfidence &&
                (includeEdgeCandidates || !TouchesFrameEdge(face, size, edgeMarginRatio));

        private static bool TouchesFrameEdge(Rect face, PixelSize size, double edgeMarginRatio)
        {
            if (size.Width <= 0 || size.Height <= 0)
                return false;

            double marginX = size.Width * Math.Max(0.0, edgeMarginRatio);
            double marginY = size.Height * Math.Max(0.0, edgeMarginRatio);
            return face.X <= marginX ||
                face.Y <= marginY ||
                face.Right >= size.Width - marginX ||
                face.Bottom >= size.Height - marginY;
        }

        private static bool IsMatchingFace(
            Rect a,
            Rect b,
            double minIou,
            double maxCenterShiftRatio,
            double maxAreaChangeRatio)
        {
            double areaRatio = GetAreaRatio(a, b);
            if (areaRatio > maxAreaChangeRatio || areaRatio < 1.0 / maxAreaChangeRatio)
                return false;
            if (IoU(a, b) >= minIou)
                return true;
            return GetNormalizedCenterShift(a, b) <= maxCenterShiftRatio;
        }

        private static float GetConfidence(FrameMaskProvider.FaceMaskData data, int faceIndex)
            => faceIndex >= 0 && faceIndex < data.Confidences.Count
                ? data.Confidences[faceIndex]
                : data.MinConfidence ?? 1.0f;

        private readonly record struct FrameDifferenceRange(int SourceFrame, int TargetFrame);

        private static IReadOnlyList<FrameDifferenceRange> BuildFrameDifferenceRanges(
            IReadOnlyList<FaceTrackFilledFace> candidates)
        {
            var ranges = new List<FrameDifferenceRange>();
            foreach (var candidate in candidates)
            {
                int sourceFrame = Math.Min(candidate.SourceFrameIndex, candidate.FrameIndex);
                int targetFrame = Math.Max(candidate.SourceFrameIndex, candidate.FrameIndex);
                if (sourceFrame < 0 || targetFrame <= sourceFrame)
                    continue;

                ranges.Add(new FrameDifferenceRange(sourceFrame, targetFrame));
            }

            if (ranges.Count == 0)
                return Array.Empty<FrameDifferenceRange>();

            ranges.Sort(static (a, b) =>
            {
                int sourceCompare = a.SourceFrame.CompareTo(b.SourceFrame);
                return sourceCompare != 0
                    ? sourceCompare
                    : a.TargetFrame.CompareTo(b.TargetFrame);
            });

            var merged = new List<FrameDifferenceRange>();
            foreach (var range in ranges)
            {
                if (merged.Count == 0)
                {
                    merged.Add(range);
                    continue;
                }

                var last = merged[^1];
                if (range.SourceFrame <= last.TargetFrame + 1)
                {
                    merged[^1] = new FrameDifferenceRange(
                        last.SourceFrame,
                        Math.Max(last.TargetFrame, range.TargetFrame));
                }
                else
                {
                    merged.Add(range);
                }
            }

            return merged;
        }

        private static void PrecomputeFrameDifferences(
            FfFrameExtractor extractor,
            IReadOnlyList<FrameDifferenceRange> ranges,
            int width,
            int height,
            byte[] sourceBuffer,
            byte[] targetBuffer,
            IDictionary<(int Source, int Target), double> differenceByPair,
            CancellationToken cancellationToken)
        {
            foreach (var range in ranges)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PrecomputeFrameDifferencesForRange(
                    extractor,
                    range.SourceFrame,
                    range.TargetFrame,
                    width,
                    height,
                    sourceBuffer,
                    targetBuffer,
                    differenceByPair,
                    cancellationToken);
            }
        }

        private static void PrecomputeFrameDifferencesForRange(
            FfFrameExtractor extractor,
            int sourceFrame,
            int targetFrame,
            int width,
            int height,
            byte[] sourceBuffer,
            byte[] targetBuffer,
            IDictionary<(int Source, int Target), double> differenceByPair,
            CancellationToken cancellationToken)
        {
            if (targetFrame <= sourceFrame)
                return;

            extractor.StartSequentialRead(sourceFrame);
            bool previousRead = false;
            int previousFrame = -1;
            int decodedFrame = sourceFrame - 1;
            int stride = 0;
            byte[] previousBuffer = sourceBuffer;
            byte[] currentBuffer = targetBuffer;

            while (decodedFrame <= targetFrame)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!extractor.TryGetNextFrameRawToBuffer(
                        cancellationToken,
                        width,
                        height,
                        useBilinear: true,
                        previousRead ? currentBuffer : previousBuffer,
                        out decodedFrame,
                        out stride))
                {
                    return;
                }

                if (decodedFrame < sourceFrame)
                    continue;

                if (!previousRead)
                {
                    previousRead = true;
                    previousFrame = decodedFrame;
                    continue;
                }

                var pair = (previousFrame, decodedFrame);
                if (!differenceByPair.ContainsKey(pair))
                {
                    differenceByPair[pair] = ComputeFrameDifference(
                        previousBuffer,
                        currentBuffer,
                        width,
                        height,
                        stride);
                }

                if (decodedFrame >= targetFrame)
                    return;

                previousFrame = decodedFrame;
                (previousBuffer, currentBuffer) = (currentBuffer, previousBuffer);
            }
        }

        private static bool TryGetMaxFrameDifferenceFromCache(
            int sourceFrame,
            int targetFrame,
            IDictionary<(int Source, int Target), double> differenceByPair,
            out double maxDifference,
            out string cutFramePair)
        {
            maxDifference = 0.0;
            int maxSource = sourceFrame;
            int maxTarget = targetFrame;
            bool found = false;

            for (int frame = sourceFrame; frame < targetFrame; frame++)
            {
                if (!differenceByPair.TryGetValue((frame, frame + 1), out double difference))
                    continue;

                found = true;
                if (difference > maxDifference)
                {
                    maxDifference = difference;
                    maxSource = frame;
                    maxTarget = frame + 1;
                }
            }

            cutFramePair = FormatFramePair(maxSource, maxTarget);
            return found;
        }

        private static double GetMaxFrameDifference(
            int sourceFrame,
            int targetFrame,
            Func<int, int, double> frameDifferenceProvider,
            IDictionary<(int Source, int Target), double> differenceByPair,
            out string cutFramePair)
        {
            double maxDifference = 0.0;
            int maxSource = sourceFrame;
            int maxTarget = targetFrame;

            for (int frame = sourceFrame; frame < targetFrame; frame++)
            {
                var pair = (frame, frame + 1);
                double difference = GetFrameDifference(
                    pair.Item1,
                    pair.Item2,
                    frameDifferenceProvider,
                    differenceByPair);

                if (difference > maxDifference)
                {
                    maxDifference = difference;
                    maxSource = pair.Item1;
                    maxTarget = pair.Item2;
                }
            }

            cutFramePair = FormatFramePair(maxSource, maxTarget);
            return maxDifference;
        }

        private static double GetFrameDifference(
            int sourceFrame,
            int targetFrame,
            Func<int, int, double> frameDifferenceProvider,
            IDictionary<(int Source, int Target), double> differenceByPair)
        {
            var pair = (Source: sourceFrame, Target: targetFrame);
            if (!differenceByPair.TryGetValue(pair, out double difference))
            {
                difference = frameDifferenceProvider(pair.Source, pair.Target);
                differenceByPair[pair] = difference;
            }

            return difference;
        }

        private static bool TryGetFramePairDifference(
            FfFrameExtractor extractor,
            int sourceFrame,
            int targetFrame,
            int width,
            int height,
            byte[] sourceBuffer,
            byte[] targetBuffer,
            IDictionary<(int Source, int Target), double> differenceByPair,
            CancellationToken cancellationToken,
            out double difference)
        {
            difference = 0.0;
            if (targetFrame <= sourceFrame)
                return false;

            var pair = (sourceFrame, targetFrame);
            if (differenceByPair.TryGetValue(pair, out difference))
                return true;

            extractor.StartSequentialRead(sourceFrame);
            bool sourceRead = false;
            int decodedFrame = sourceFrame - 1;
            int stride = 0;
            while (decodedFrame <= targetFrame)
            {
                cancellationToken.ThrowIfCancellationRequested();

                byte[] buffer = sourceRead ? targetBuffer : sourceBuffer;
                if (!extractor.TryGetNextFrameRawToBuffer(
                        cancellationToken,
                        width,
                        height,
                        useBilinear: true,
                        buffer,
                        out decodedFrame,
                        out stride))
                {
                    return false;
                }

                if (decodedFrame < sourceFrame)
                    continue;

                if (!sourceRead)
                {
                    if (decodedFrame != sourceFrame)
                        return false;

                    sourceRead = true;
                    if (decodedFrame == targetFrame)
                    {
                        difference = 0.0;
                        differenceByPair[pair] = difference;
                        return true;
                    }

                    continue;
                }

                if (decodedFrame != targetFrame)
                    continue;

                difference = ComputeFrameDifference(
                    sourceBuffer,
                    targetBuffer,
                    width,
                    height,
                    stride);
                differenceByPair[pair] = difference;
                return true;
            }

            return false;
        }

        private static bool TryComputeMaxSequentialDifference(
            FfFrameExtractor extractor,
            int sourceFrame,
            int targetFrame,
            int width,
            int height,
            byte[] sourceBuffer,
            byte[] targetBuffer,
            IDictionary<(int Source, int Target), double> differenceByPair,
            out double maxDifference,
            out string cutFramePair)
        {
            maxDifference = 0.0;
            int maxSource = sourceFrame;
            int maxTarget = targetFrame;
            cutFramePair = FormatFramePair(sourceFrame, targetFrame);

            if (targetFrame <= sourceFrame)
                return false;

            extractor.StartSequentialRead(sourceFrame);
            bool previousRead = false;
            int previousFrame = -1;
            int decodedFrame = sourceFrame - 1;
            int stride = 0;
            byte[] previousBuffer = sourceBuffer;
            byte[] currentBuffer = targetBuffer;
            while (decodedFrame <= targetFrame)
            {
                if (!extractor.TryGetNextFrameRawToBuffer(
                        CancellationToken.None,
                        width,
                        height,
                        useBilinear: true,
                        previousRead ? currentBuffer : previousBuffer,
                        out decodedFrame,
                        out stride))
                {
                    return false;
                }

                if (decodedFrame < sourceFrame)
                    continue;

                if (!previousRead)
                {
                    previousRead = true;
                    previousFrame = decodedFrame;
                    continue;
                }

                var pair = (previousFrame, decodedFrame);
                if (!differenceByPair.TryGetValue(pair, out double difference))
                {
                    difference = ComputeFrameDifference(
                        previousBuffer,
                        currentBuffer,
                        width,
                        height,
                        stride);
                    differenceByPair[pair] = difference;
                }

                if (difference > maxDifference)
                {
                    maxDifference = difference;
                    maxSource = previousFrame;
                    maxTarget = decodedFrame;
                }

                if (decodedFrame >= targetFrame)
                {
                    cutFramePair = FormatFramePair(maxSource, maxTarget);
                    return true;
                }

                previousFrame = decodedFrame;
                (previousBuffer, currentBuffer) = (currentBuffer, previousBuffer);
            }

            return false;
        }

        private static bool RemoveFaceCandidate(
            FrameMaskProvider maskProvider,
            int frameIndex,
            Rect candidate,
            double minIou,
            double maxCenterShiftRatio,
            double maxAreaChangeRatio)
        {
            if (!maskProvider.TryGetFaceMaskData(frameIndex, out var data) || data.Faces.Count == 0)
                return false;

            int bestIouIndex = -1;
            int fallbackIndex = -1;
            double bestIou = 0.0;
            double bestFallbackScore = double.NegativeInfinity;
            for (int i = 0; i < data.Faces.Count; i++)
            {
                var face = data.Faces[i];
                double iou = IoU(face, candidate);
                if (iou > bestIou)
                {
                    bestIou = iou;
                    bestIouIndex = i;
                }

                if (iou >= minIou)
                    continue;

                if (!IsMatchingFace(candidate, face, minIou, maxCenterShiftRatio, maxAreaChangeRatio))
                    continue;

                double centerShift = GetNormalizedCenterShift(candidate, face);
                double score = iou - centerShift;
                if (score > bestFallbackScore)
                {
                    bestFallbackScore = score;
                    fallbackIndex = i;
                }
            }

            int removeIndex = bestIou >= minIou
                ? bestIouIndex
                : fallbackIndex;
            if (removeIndex < 0)
                return false;

            var faces = data.Faces.ToList();
            var confs = data.Confidences.ToList();
            faces.RemoveAt(removeIndex);
            if (removeIndex < confs.Count)
                confs.RemoveAt(removeIndex);

            maskProvider.SetFaceRects(
                frameIndex,
                faces,
                data.Size,
                confs.Count == 0 ? null : confs.Min(),
                confs);
            return true;
        }

        private static int RemoveWeakMatchingTail(
            FrameMaskProvider maskProvider,
            int startFrame,
            Rect removedFace,
            int maxTailFrames,
            float maxTailConfidence,
            ICollection<int> removedFrameIndices)
        {
            if (maxTailFrames <= 0 || maxTailConfidence <= 0.0f)
                return 0;

            int removed = 0;
            var reference = removedFace;
            for (int frameIndex = startFrame + 1; frameIndex <= startFrame + maxTailFrames; frameIndex++)
            {
                if (!TryRemoveWeakMatchingFace(
                        maskProvider,
                        frameIndex,
                        reference,
                        maxTailConfidence,
                        out var removedBounds))
                {
                    break;
                }

                reference = removedBounds;
                removed++;
                removedFrameIndices.Add(frameIndex);
            }

            return removed;
        }

        private static bool TryRemoveWeakMatchingFace(
            FrameMaskProvider maskProvider,
            int frameIndex,
            Rect reference,
            float maxConfidence,
            out Rect removedBounds)
        {
            removedBounds = default;
            if (!maskProvider.TryGetFaceMaskData(frameIndex, out var data) || data.Faces.Count == 0)
                return false;

            int removeIndex = -1;
            double bestScore = double.NegativeInfinity;
            for (int i = 0; i < data.Faces.Count; i++)
            {
                float confidence = GetConfidence(data, i);
                if (confidence > maxConfidence)
                    continue;

                var face = data.Faces[i];
                if (!IsMatchingFace(reference, face, minIou: 0.15, maxCenterShiftRatio: 0.65, maxAreaChangeRatio: 3.0))
                    continue;

                double score = IoU(reference, face);
                if (score > bestScore)
                {
                    bestScore = score;
                    removeIndex = i;
                    removedBounds = face;
                }
            }

            if (removeIndex < 0)
                return false;

            var faces = data.Faces.ToList();
            var confs = data.Confidences.ToList();
            faces.RemoveAt(removeIndex);
            if (removeIndex < confs.Count)
                confs.RemoveAt(removeIndex);

            maskProvider.SetFaceRects(
                frameIndex,
                faces,
                data.Size,
                confs.Count == 0 ? null : confs.Min(),
                confs);
            return true;
        }

        private static unsafe double ComputeFrameDifference(
            byte[] a,
            byte[] b,
            int width,
            int height,
            int stride)
        {
            if (width <= 0 || height <= 0)
                return 0.0;

            const int sampleColumns = 24;
            const int sampleRows = 14;
            fixed (byte* aBase = a)
            fixed (byte* bBase = b)
            {
                long total = 0;
                int samples = 0;

                for (int sy = 0; sy < sampleRows; sy++)
                {
                    int y = Math.Clamp((int)Math.Round((sy + 0.5) * height / sampleRows), 0, height - 1);
                    byte* aRow = aBase + y * stride;
                    byte* bRow = bBase + y * stride;
                    for (int sx = 0; sx < sampleColumns; sx++)
                    {
                        int x = Math.Clamp((int)Math.Round((sx + 0.5) * width / sampleColumns), 0, width - 1);
                        byte* ap = aRow + x * 4;
                        byte* bp = bRow + x * 4;
                        total += Math.Abs(ap[0] - bp[0]);
                        total += Math.Abs(ap[1] - bp[1]);
                        total += Math.Abs(ap[2] - bp[2]);
                        samples++;
                    }
                }

                return samples == 0 ? 0.0 : total / (samples * 255.0 * 3.0);
            }
        }

        private static double IoU(Rect a, Rect b)
        {
            double ix1 = Math.Max(a.X, b.X);
            double iy1 = Math.Max(a.Y, b.Y);
            double ix2 = Math.Min(a.Right, b.Right);
            double iy2 = Math.Min(a.Bottom, b.Bottom);
            double iw = Math.Max(0.0, ix2 - ix1);
            double ih = Math.Max(0.0, iy2 - iy1);
            double inter = iw * ih;
            if (inter <= 0.0)
                return 0.0;

            double union = a.Width * a.Height + b.Width * b.Height - inter;
            return union <= 0.0 ? 0.0 : inter / union;
        }

        private static double GetAreaRatio(Rect a, Rect b)
        {
            double aa = Math.Max(1.0, a.Width * a.Height);
            double ba = Math.Max(1.0, b.Width * b.Height);
            return Math.Max(aa, ba) / Math.Min(aa, ba);
        }

        private static double GetNormalizedCenterShift(Rect a, Rect b)
        {
            double ax = a.X + a.Width * 0.5;
            double ay = a.Y + a.Height * 0.5;
            double bx = b.X + b.Width * 0.5;
            double by = b.Y + b.Height * 0.5;
            double dx = ax - bx;
            double dy = ay - by;
            double shift = Math.Sqrt(dx * dx + dy * dy);
            double maxDim = Math.Max(Math.Max(a.Width, a.Height), Math.Max(b.Width, b.Height));
            return maxDim <= 0.0 ? double.MaxValue : shift / maxDim;
        }

        private readonly record struct WeakCarryCandidate(
            int FrameIndex,
            Rect Bounds,
            PixelSize Size,
            float Confidence);
    }

    public readonly record struct FaceTrackSceneCutGuardResult(
        int Checked,
        int Removed,
        IReadOnlyList<int> RemovedFrameIndices,
        IReadOnlyList<string> CheckedFramePairs,
        double Threshold,
        double MaxDifference,
        IReadOnlyList<string> CutFramePairs,
        long ElapsedMs,
        int DirectDifferenceChecks,
        int DirectDifferenceSkipped,
        string? Error)
    {
        public static FaceTrackSceneCutGuardResult Empty { get; } = new(0, 0, Array.Empty<int>(), Array.Empty<string>(), 0, 0, Array.Empty<string>(), 0, 0, 0, null);
    }
}
