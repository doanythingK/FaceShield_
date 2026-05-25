using Avalonia;
using FaceShield.Services.Video;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FaceShield.Services.Analysis
{
    public sealed class YoloFinalMaskPostProcessor
    {
        public YoloFinalMaskCleanupResult RemoveWeakIsolatedMasks(
            FrameMaskProvider maskProvider,
            YoloFinalMaskCleanupOptions? options = null)
        {
            options ??= new YoloFinalMaskCleanupOptions();
            if (options.NeighborWindowFrames <= 0 || options.WeakConfidenceMax <= 0)
                return YoloFinalMaskCleanupResult.Empty;

            var entries = maskProvider.GetFaceMaskEntries()
                .Where(static x => x.Value.Faces.Count > 0)
                .OrderBy(static x => x.Key)
                .ToArray();
            if (entries.Length == 0)
                return YoloFinalMaskCleanupResult.Empty;

            var removedFrames = new List<int>();
            int removed = 0;
            int removedUnsupported = 0;
            int removedShortClusters = 0;
            int removedTinyClusters = 0;
            int removedTinyShortClusters = 0;
            int removedTinyIsolated = 0;
            int removedUpperWeakClusters = 0;
            for (int i = 0; i < entries.Length; i++)
            {
                int frameIndex = entries[i].Key;
                var data = entries[i].Value;
                var faces = new List<Rect>(data.Faces.Count);
                var confidences = new List<float>(data.Faces.Count);
                for (int faceIndex = 0; faceIndex < data.Faces.Count; faceIndex++)
                {
                    var face = data.Faces[faceIndex];
                    float confidence = faceIndex < data.Confidences.Count
                        ? data.Confidences[faceIndex]
                        : data.MinConfidence ?? 1.0f;
                    if (confidence <= options.WeakConfidenceMax &&
                        !TouchesFrameEdge(face, data.Size, options.EdgeMarginRatio))
                    {
                        bool hasMatchingNeighbor = HasMatchingTemporalNeighbor(entries, i, face, options);
                        bool removeUnsupported = !hasMatchingNeighbor;
                        bool removeShortCluster = hasMatchingNeighbor &&
                            IsWeakShortTemporalCluster(entries, i, faceIndex, face, confidence, options);
                        bool removeTinyCluster = hasMatchingNeighbor &&
                            !removeShortCluster &&
                            IsWeakTinyTemporalCluster(entries, i, faceIndex, face, confidence, options);
                        bool removeUpperWeakCluster = hasMatchingNeighbor &&
                            !removeShortCluster &&
                            !removeTinyCluster &&
                            IsUpperWeakTemporalCluster(entries, i, faceIndex, face, confidence, options);

                        if (removeUnsupported || removeShortCluster || removeTinyCluster || removeUpperWeakCluster)
                        {
                            removed++;
                            if (removeUnsupported)
                                removedUnsupported++;
                            if (removeShortCluster)
                                removedShortClusters++;
                            if (removeTinyCluster)
                                removedTinyClusters++;
                            if (removeUpperWeakCluster)
                                removedUpperWeakClusters++;
                            continue;
                        }
                    }
                    if (confidence <= options.TinyShortClusterMaxConfidence &&
                        !TouchesFrameEdge(face, data.Size, options.EdgeMarginRatio))
                    {
                        bool isTinyShortClusterCandidate = IsTinyFace(
                            face,
                            data.Size,
                            options.TinyShortClusterMaxAreaRatio);
                        bool isTinyIsolatedCandidate = IsTinyFace(
                            face,
                            data.Size,
                            options.TinyIsolatedMaxAreaRatio);
                        if (!isTinyShortClusterCandidate && !isTinyIsolatedCandidate)
                        {
                            faces.Add(face);
                            confidences.Add(confidence);
                            continue;
                        }

                        bool hasMatchingNeighbor = HasMatchingTemporalNeighbor(entries, i, face, options);
                        bool removeTinyShortCluster = isTinyShortClusterCandidate &&
                            hasMatchingNeighbor &&
                            IsTinyShortTemporalCluster(entries, i, faceIndex, face, confidence, options);
                        bool removeTinyIsolated = isTinyIsolatedCandidate &&
                            !hasMatchingNeighbor &&
                            confidence <= options.TinyIsolatedMaxConfidence;

                        if (removeTinyShortCluster || removeTinyIsolated)
                        {
                            removed++;
                            if (removeTinyShortCluster)
                                removedTinyShortClusters++;
                            if (removeTinyIsolated)
                                removedTinyIsolated++;
                            continue;
                        }
                    }

                    faces.Add(face);
                    confidences.Add(confidence);
                }

                if (faces.Count == data.Faces.Count)
                    continue;

                removedFrames.Add(frameIndex);
                if (faces.Count == 0)
                {
                    maskProvider.RemoveFaceMask(frameIndex);
                    continue;
                }

                float minConfidence = confidences.Count == 0 ? 1.0f : confidences.Min();
                maskProvider.SetFaceRects(frameIndex, faces, data.Size, minConfidence, confidences);
            }

            return removed == 0
                ? YoloFinalMaskCleanupResult.Empty
                : new YoloFinalMaskCleanupResult(
                    removed,
                    removedUnsupported,
                    removedShortClusters,
                    removedTinyClusters,
                    removedTinyShortClusters,
                    removedTinyIsolated,
                    removedUpperWeakClusters,
                    removedFrames.ToArray());
        }

        public YoloFinalMaskGapFillResult FillShortStableGaps(
            FrameMaskProvider maskProvider,
            YoloFinalMaskGapFillOptions? options = null)
        {
            options ??= new YoloFinalMaskGapFillOptions();
            if (options.MaxGapFrames <= 0 || options.MinAnchorConfidence <= 0)
                return YoloFinalMaskGapFillResult.Empty;

            var entries = maskProvider.GetFaceMaskEntries()
                .Where(static x => x.Value.Faces.Count > 0)
                .OrderBy(static x => x.Key)
                .ToArray();
            if (entries.Length < 2)
                return YoloFinalMaskGapFillResult.Empty;

            var storedFrames = new HashSet<int>(maskProvider.GetStoredMaskFrameIndices());
            var fills = new Dictionary<int, (PixelSize Size, List<Rect> Faces, List<float> Confidences)>();
            var filledFaceInfo = new List<FaceTrackFilledFace>();
            var cutGuardFaceInfo = new List<FaceTrackFilledFace>();
            int filledFaces = 0;

            for (int entryIndex = 0; entryIndex < entries.Length - 1; entryIndex++)
            {
                int previousFrame = entries[entryIndex].Key;
                var previous = entries[entryIndex].Value;
                if (previous.Size.Width <= 0 || previous.Size.Height <= 0)
                    continue;

                for (int faceIndex = 0; faceIndex < previous.Faces.Count; faceIndex++)
                {
                    float previousConfidence = GetConfidence(previous, faceIndex);
                    if (previousConfidence < options.MinAnchorConfidence)
                        continue;

                    var previousFace = previous.Faces[faceIndex];
                    for (int nextEntryIndex = entryIndex + 1; nextEntryIndex < entries.Length; nextEntryIndex++)
                    {
                        int nextFrame = entries[nextEntryIndex].Key;
                        int gap = nextFrame - previousFrame - 1;
                        if (gap > options.MaxGapFrames)
                            break;

                        if (gap <= 0)
                            continue;

                        var next = entries[nextEntryIndex].Value;
                        if (!TryFindStableGapMatch(
                                next,
                                previousFace,
                                options,
                                out var nextFace,
                                out float nextConfidence))
                        {
                            continue;
                        }

                        float fillConfidence = Math.Clamp(
                            Math.Min(previousConfidence, nextConfidence),
                            options.FillConfidenceFloor,
                            1.0f);

                        for (int frameIndex = previousFrame + 1; frameIndex < nextFrame; frameIndex++)
                        {
                            if (storedFrames.Contains(frameIndex))
                                continue;

                            double t = (frameIndex - previousFrame) / (double)(nextFrame - previousFrame);
                            var interpolated = Interpolate(previousFace, nextFace, t);
                            if (interpolated.Width <= 0 || interpolated.Height <= 0)
                                continue;

                            if (!fills.TryGetValue(frameIndex, out var fill))
                            {
                                fill = CreateFillEntry(maskProvider, frameIndex, previous.Size);
                                fills[frameIndex] = fill;
                            }

                            if (HasMatchingFace(fill.Faces, interpolated, options.DuplicateIou))
                                continue;

                            fill.Faces.Add(interpolated);
                            fill.Confidences.Add(fillConfidence);
                            filledFaceInfo.Add(new FaceTrackFilledFace(
                                frameIndex,
                                interpolated,
                                previous.Size,
                                fillConfidence,
                                previousFrame));
                            cutGuardFaceInfo.Add(new FaceTrackFilledFace(
                                frameIndex,
                                interpolated,
                                previous.Size,
                                fillConfidence,
                                previousFrame));
                            cutGuardFaceInfo.Add(new FaceTrackFilledFace(
                                frameIndex,
                                interpolated,
                                previous.Size,
                                fillConfidence,
                                nextFrame));
                            filledFaces++;
                        }

                        break;
                    }
                }
            }

            if (filledFaces == 0)
                return YoloFinalMaskGapFillResult.Empty;

            foreach (var entry in fills.OrderBy(static x => x.Key))
            {
                if (entry.Value.Faces.Count == 0)
                    continue;

                maskProvider.SetFaceRects(
                    entry.Key,
                    entry.Value.Faces,
                    entry.Value.Size,
                    entry.Value.Confidences.Min(),
                    entry.Value.Confidences);
            }

            return new YoloFinalMaskGapFillResult(
                filledFaces,
                fills.Keys.OrderBy(static x => x).ToArray(),
                filledFaceInfo.ToArray(),
                cutGuardFaceInfo.ToArray());
        }

        private static (PixelSize Size, List<Rect> Faces, List<float> Confidences) CreateFillEntry(
            FrameMaskProvider maskProvider,
            int frameIndex,
            PixelSize fallbackSize)
        {
            if (!maskProvider.TryGetFaceMaskData(frameIndex, out var existing) ||
                existing.Faces.Count == 0)
            {
                return (fallbackSize, new List<Rect>(), new List<float>());
            }

            var faces = new List<Rect>(existing.Faces);
            var confidences = new List<float>(faces.Count);
            for (int i = 0; i < faces.Count; i++)
                confidences.Add(GetConfidence(existing, i));

            var size = existing.Size.Width > 0 && existing.Size.Height > 0
                ? existing.Size
                : fallbackSize;
            return (size, faces, confidences);
        }

        private static bool IsWeakTinyTemporalCluster(
            IReadOnlyList<KeyValuePair<int, FrameMaskProvider.FaceMaskData>> entries,
            int entryIndex,
            int faceIndex,
            Rect face,
            float confidence,
            YoloFinalMaskCleanupOptions options)
        {
            if (options.TinyClusterMaxFrames <= 0 ||
                options.TinyClusterMaxConfidence <= 0 ||
                options.TinyClusterMaxAreaRatio <= 0 ||
                confidence > options.TinyClusterMaxConfidence)
            {
                return false;
            }

            var visited = new HashSet<(int EntryIndex, int FaceIndex)>();
            var pending = new Stack<(int EntryIndex, int FaceIndex, Rect Face)>();
            pending.Push((entryIndex, faceIndex, face));

            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (!visited.Add((current.EntryIndex, current.FaceIndex)))
                    continue;
                if (visited.Count > options.TinyClusterMaxFrames)
                    return false;

                var currentData = entries[current.EntryIndex].Value;
                float currentConfidence = GetConfidence(currentData, current.FaceIndex);
                if (currentConfidence > options.TinyClusterMaxConfidence ||
                    !IsTinyFace(current.Face, currentData.Size, options.TinyClusterMaxAreaRatio) ||
                    TouchesFrameEdge(current.Face, currentData.Size, options.EdgeMarginRatio))
                {
                    return false;
                }
                if (HasStrongAdjacentContinuation(
                        entries,
                        current.EntryIndex,
                        current.Face,
                        options,
                        options.TinyClusterMaxConfidence))
                {
                    return false;
                }

                AddMatchingWeakClusterNeighbors(entries, current.EntryIndex, current.Face, options, visited, pending);
            }

            return visited.Count > 0 && visited.Count <= options.TinyClusterMaxFrames;
        }

        private static bool IsTinyShortTemporalCluster(
            IReadOnlyList<KeyValuePair<int, FrameMaskProvider.FaceMaskData>> entries,
            int entryIndex,
            int faceIndex,
            Rect face,
            float confidence,
            YoloFinalMaskCleanupOptions options)
        {
            if (options.TinyShortClusterMaxFrames <= 0 ||
                options.TinyShortClusterMaxConfidence <= 0 ||
                options.TinyShortClusterMaxAreaRatio <= 0 ||
                confidence > options.TinyShortClusterMaxConfidence)
            {
                return false;
            }

            var visited = new HashSet<(int EntryIndex, int FaceIndex)>();
            var pending = new Stack<(int EntryIndex, int FaceIndex, Rect Face)>();
            pending.Push((entryIndex, faceIndex, face));

            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (!visited.Add((current.EntryIndex, current.FaceIndex)))
                    continue;
                if (visited.Count > options.TinyShortClusterMaxFrames)
                    return false;

                var currentData = entries[current.EntryIndex].Value;
                float currentConfidence = GetConfidence(currentData, current.FaceIndex);
                if (currentConfidence > options.TinyShortClusterMaxConfidence ||
                    !IsTinyFace(current.Face, currentData.Size, options.TinyShortClusterMaxAreaRatio) ||
                    TouchesFrameEdge(current.Face, currentData.Size, options.EdgeMarginRatio))
                {
                    return false;
                }
                if (HasStrongAdjacentContinuation(
                        entries,
                        current.EntryIndex,
                        current.Face,
                        options,
                        options.TinyShortClusterMaxConfidence))
                {
                    return false;
                }

                AddMatchingWeakClusterNeighbors(entries, current.EntryIndex, current.Face, options, visited, pending);
            }

            return visited.Count > 0 && visited.Count <= options.TinyShortClusterMaxFrames;
        }

        private static bool IsUpperWeakTemporalCluster(
            IReadOnlyList<KeyValuePair<int, FrameMaskProvider.FaceMaskData>> entries,
            int entryIndex,
            int faceIndex,
            Rect face,
            float confidence,
            YoloFinalMaskCleanupOptions options)
        {
            if (options.UpperWeakClusterMaxFrames <= 0 ||
                options.UpperWeakClusterMaxConfidence <= 0 ||
                options.UpperWeakClusterMaxCenterYRatio <= 0 ||
                options.UpperWeakClusterMaxAreaRatio <= 0 ||
                confidence > options.UpperWeakClusterMaxConfidence)
            {
                return false;
            }

            var visited = new HashSet<(int EntryIndex, int FaceIndex)>();
            var pending = new Stack<(int EntryIndex, int FaceIndex, Rect Face)>();
            pending.Push((entryIndex, faceIndex, face));

            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (!visited.Add((current.EntryIndex, current.FaceIndex)))
                    continue;
                if (visited.Count > options.UpperWeakClusterMaxFrames)
                    return false;

                var currentData = entries[current.EntryIndex].Value;
                float currentConfidence = GetConfidence(currentData, current.FaceIndex);
                if (currentConfidence > options.UpperWeakClusterMaxConfidence ||
                    !IsUpperWeakFace(current.Face, currentData.Size, options) ||
                    TouchesFrameEdge(current.Face, currentData.Size, options.EdgeMarginRatio))
                {
                    return false;
                }
                if (HasStrongAdjacentContinuation(
                        entries,
                        current.EntryIndex,
                        current.Face,
                        options,
                        options.UpperWeakClusterMaxConfidence))
                {
                    return false;
                }

                AddMatchingWeakClusterNeighbors(entries, current.EntryIndex, current.Face, options, visited, pending);
            }

            return visited.Count > 0 && visited.Count <= options.UpperWeakClusterMaxFrames;
        }

        private static bool TryFindStableGapMatch(
            FrameMaskProvider.FaceMaskData data,
            Rect reference,
            YoloFinalMaskGapFillOptions options,
            out Rect match,
            out float confidence)
        {
            match = default;
            confidence = 0.0f;
            double bestScore = double.NegativeInfinity;
            for (int i = 0; i < data.Faces.Count; i++)
            {
                float candidateConfidence = GetConfidence(data, i);
                if (candidateConfidence < options.MinAnchorConfidence)
                    continue;

                var candidate = data.Faces[i];
                if (!IsStableGapMatch(reference, candidate, options))
                    continue;

                double score = FaceTrackBuilder.IoU(reference, candidate) * 2.0 + candidateConfidence;
                if (score <= bestScore)
                    continue;

                bestScore = score;
                match = candidate;
                confidence = candidateConfidence;
            }

            return bestScore > double.NegativeInfinity;
        }

        private static bool IsStableGapMatch(
            Rect previous,
            Rect next,
            YoloFinalMaskGapFillOptions options)
        {
            double areaRatio = FaceTrackBuilder.GetAreaRatio(previous, next);
            if (areaRatio > options.MaxAreaChangeRatio ||
                areaRatio < 1.0 / options.MaxAreaChangeRatio)
            {
                return false;
            }

            if (FaceTrackBuilder.IoU(previous, next) >= options.MinIou)
                return true;

            return FaceTrackBuilder.GetNormalizedCenterShift(previous, next) <= options.MaxCenterShiftRatio;
        }

        private static Rect Interpolate(Rect previous, Rect next, double t)
        {
            double inverse = 1.0 - t;
            return new Rect(
                previous.X * inverse + next.X * t,
                previous.Y * inverse + next.Y * t,
                Math.Max(0.0, previous.Width * inverse + next.Width * t),
                Math.Max(0.0, previous.Height * inverse + next.Height * t));
        }

        private static bool IsWeakShortTemporalCluster(
            IReadOnlyList<KeyValuePair<int, FrameMaskProvider.FaceMaskData>> entries,
            int entryIndex,
            int faceIndex,
            Rect face,
            float confidence,
            YoloFinalMaskCleanupOptions options)
        {
            if (options.WeakClusterMaxFrames <= 0 ||
                options.WeakClusterMaxConfidence <= 0 ||
                confidence > options.WeakClusterMaxConfidence)
            {
                return false;
            }

            var visited = new HashSet<(int EntryIndex, int FaceIndex)>();
            var pending = new Stack<(int EntryIndex, int FaceIndex, Rect Face)>();
            pending.Push((entryIndex, faceIndex, face));

            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (!visited.Add((current.EntryIndex, current.FaceIndex)))
                    continue;
                if (visited.Count > options.WeakClusterMaxFrames)
                    return false;

                var currentData = entries[current.EntryIndex].Value;
                float currentConfidence = GetConfidence(currentData, current.FaceIndex);
                if (currentConfidence > options.WeakClusterMaxConfidence ||
                    TouchesFrameEdge(current.Face, currentData.Size, options.EdgeMarginRatio))
                {
                    return false;
                }

                AddMatchingWeakClusterNeighbors(entries, current.EntryIndex, current.Face, options, visited, pending);
            }

            return visited.Count > 0 && visited.Count <= options.WeakClusterMaxFrames;
        }

        private static bool HasStrongAdjacentContinuation(
            IReadOnlyList<KeyValuePair<int, FrameMaskProvider.FaceMaskData>> entries,
            int entryIndex,
            Rect face,
            YoloFinalMaskCleanupOptions options,
            float strongConfidenceThreshold)
        {
            int frameIndex = entries[entryIndex].Key;
            for (int i = entryIndex - 1; i >= 0; i--)
            {
                if (frameIndex - entries[i].Key > options.NeighborWindowFrames + 1)
                    break;
                if (HasMatchingStrongFace(entries[i].Value, face, options, strongConfidenceThreshold))
                    return true;
            }

            for (int i = entryIndex + 1; i < entries.Count; i++)
            {
                if (entries[i].Key - frameIndex > options.NeighborWindowFrames + 1)
                    break;
                if (HasMatchingStrongFace(entries[i].Value, face, options, strongConfidenceThreshold))
                    return true;
            }

            return false;
        }

        private static bool HasMatchingStrongFace(
            FrameMaskProvider.FaceMaskData data,
            Rect face,
            YoloFinalMaskCleanupOptions options,
            float strongConfidenceThreshold)
        {
            for (int i = 0; i < data.Faces.Count; i++)
            {
                if (GetConfidence(data, i) <= strongConfidenceThreshold)
                    continue;
                if (IsMatchingFace(data.Faces[i], face, options))
                    return true;
            }

            return false;
        }

        private static void AddMatchingWeakClusterNeighbors(
            IReadOnlyList<KeyValuePair<int, FrameMaskProvider.FaceMaskData>> entries,
            int entryIndex,
            Rect face,
            YoloFinalMaskCleanupOptions options,
            ISet<(int EntryIndex, int FaceIndex)> visited,
            Stack<(int EntryIndex, int FaceIndex, Rect Face)> pending)
        {
            int frameIndex = entries[entryIndex].Key;
            for (int i = entryIndex - 1; i >= 0; i--)
            {
                if (frameIndex - entries[i].Key > options.NeighborWindowFrames)
                    break;
                AddMatchingFaces(entries[i].Value.Faces, i, face, options, visited, pending);
            }

            for (int i = entryIndex + 1; i < entries.Count; i++)
            {
                if (entries[i].Key - frameIndex > options.NeighborWindowFrames)
                    break;
                AddMatchingFaces(entries[i].Value.Faces, i, face, options, visited, pending);
            }
        }

        private static void AddMatchingFaces(
            IReadOnlyList<Rect> candidates,
            int entryIndex,
            Rect face,
            YoloFinalMaskCleanupOptions options,
            ISet<(int EntryIndex, int FaceIndex)> visited,
            Stack<(int EntryIndex, int FaceIndex, Rect Face)> pending)
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                if (visited.Contains((entryIndex, i)))
                    continue;
                if (IsMatchingFace(candidates[i], face, options))
                    pending.Push((entryIndex, i, candidates[i]));
            }
        }

        private static bool HasMatchingTemporalNeighbor(
            IReadOnlyList<KeyValuePair<int, FrameMaskProvider.FaceMaskData>> entries,
            int entryIndex,
            Rect face,
            YoloFinalMaskCleanupOptions options)
        {
            int frameIndex = entries[entryIndex].Key;
            for (int i = entryIndex - 1; i >= 0; i--)
            {
                if (frameIndex - entries[i].Key > options.NeighborWindowFrames)
                    break;
                if (HasMatchingFace(entries[i].Value.Faces, face, options))
                    return true;
            }

            for (int i = entryIndex + 1; i < entries.Count; i++)
            {
                if (entries[i].Key - frameIndex > options.NeighborWindowFrames)
                    break;
                if (HasMatchingFace(entries[i].Value.Faces, face, options))
                    return true;
            }

            return false;
        }

        private static bool HasMatchingFace(
            IReadOnlyList<Rect> candidates,
            Rect face,
            YoloFinalMaskCleanupOptions options)
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                if (IsMatchingFace(candidate, face, options))
                    return true;
            }

            return false;
        }

        private static bool HasMatchingFace(
            IReadOnlyList<Rect> candidates,
            Rect face,
            double minIou)
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                if (FaceTrackBuilder.IoU(candidates[i], face) >= minIou)
                    return true;
            }

            return false;
        }

        private static bool IsMatchingFace(Rect candidate, Rect face, YoloFinalMaskCleanupOptions options)
        {
            if (FaceTrackBuilder.IoU(candidate, face) >= options.NeighborMinIou)
                return true;

            double areaRatio = FaceTrackBuilder.GetAreaRatio(candidate, face);
            if (areaRatio > options.NeighborMaxAreaChangeRatio ||
                areaRatio < 1.0 / options.NeighborMaxAreaChangeRatio)
            {
                return false;
            }

            return FaceTrackBuilder.GetNormalizedCenterShift(candidate, face) <= options.NeighborMaxCenterShiftRatio;
        }

        private static float GetConfidence(FrameMaskProvider.FaceMaskData data, int faceIndex)
            => faceIndex < data.Confidences.Count
                ? data.Confidences[faceIndex]
                : data.MinConfidence ?? 1.0f;

        private static bool TouchesFrameEdge(Rect face, PixelSize size, double marginRatio)
        {
            if (size.Width <= 0 || size.Height <= 0 || marginRatio <= 0)
                return false;

            double marginX = size.Width * marginRatio;
            double marginY = size.Height * marginRatio;
            return face.X <= marginX ||
                face.Y <= marginY ||
                face.Right >= size.Width - marginX ||
                face.Bottom >= size.Height - marginY;
        }

        private static bool IsTinyFace(Rect face, PixelSize size, double maxAreaRatio)
        {
            if (size.Width <= 0 || size.Height <= 0)
                return false;

            double frameArea = Math.Max(1.0, size.Width * (double)size.Height);
            double areaRatio = Math.Max(0.0, face.Width * face.Height) / frameArea;
            return areaRatio <= maxAreaRatio;
        }

        private static bool IsUpperWeakFace(Rect face, PixelSize size, YoloFinalMaskCleanupOptions options)
        {
            if (size.Width <= 0 || size.Height <= 0)
                return false;

            double frameArea = Math.Max(1.0, size.Width * (double)size.Height);
            double areaRatio = Math.Max(0.0, face.Width * face.Height) / frameArea;
            double centerYRatio = (face.Y + face.Height * 0.5) / size.Height;
            return centerYRatio <= options.UpperWeakClusterMaxCenterYRatio &&
                areaRatio <= options.UpperWeakClusterMaxAreaRatio;
        }
    }

    public sealed record YoloFinalMaskCleanupOptions
    {
        public int NeighborWindowFrames { get; init; } = 1;
        public float WeakConfidenceMax { get; init; } = 0.50f;
        public double EdgeMarginRatio { get; init; } = 0.02;
        public double NeighborMinIou { get; init; } = 0.15;
        public double NeighborMaxCenterShiftRatio { get; init; } = 0.65;
        public double NeighborMaxAreaChangeRatio { get; init; } = 3.0;
        public int WeakClusterMaxFrames { get; init; } = 2;
        public float WeakClusterMaxConfidence { get; init; } = 0.38f;
        public int TinyClusterMaxFrames { get; init; } = 3;
        public float TinyClusterMaxConfidence { get; init; } = 0.45f;
        public double TinyClusterMaxAreaRatio { get; init; } = 0.0012;
        public int TinyShortClusterMaxFrames { get; init; } = 2;
        public float TinyShortClusterMaxConfidence { get; init; } = 0.62f;
        public double TinyShortClusterMaxAreaRatio { get; init; } = 0.0009;
        public float TinyIsolatedMaxConfidence { get; init; } = 0.62f;
        public double TinyIsolatedMaxAreaRatio { get; init; } = 0.0009;
        public int UpperWeakClusterMaxFrames { get; init; } = 6;
        public float UpperWeakClusterMaxConfidence { get; init; } = 0.50f;
        public double UpperWeakClusterMaxCenterYRatio { get; init; } = 0.12;
        public double UpperWeakClusterMaxAreaRatio { get; init; } = 0.0035;
    }

    public sealed record YoloFinalMaskGapFillOptions
    {
        public int MaxGapFrames { get; init; } = 3;
        public float MinAnchorConfidence { get; init; } = 0.55f;
        public float FillConfidenceFloor { get; init; } = 0.48f;
        public double MinIou { get; init; } = 0.15;
        public double MaxCenterShiftRatio { get; init; } = 0.65;
        public double MaxAreaChangeRatio { get; init; } = 2.5;
        public double DuplicateIou { get; init; } = 0.50;
    }

    public readonly record struct YoloFinalMaskCleanupResult(
        int RemovedWeakIsolatedFaces,
        int RemovedWeakUnsupportedFaces,
        int RemovedWeakShortClusterFaces,
        int RemovedWeakTinyClusterFaces,
        int RemovedTinyShortClusterFaces,
        int RemovedTinyIsolatedFaces,
        int RemovedUpperWeakClusterFaces,
        IReadOnlyList<int> RemovedFrameIndices)
    {
        public static YoloFinalMaskCleanupResult Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, Array.Empty<int>());
    }

    public readonly record struct YoloFinalMaskGapFillResult(
        int FilledFaces,
        IReadOnlyList<int> FilledFrameIndices,
        IReadOnlyList<FaceTrackFilledFace> FilledFacesInfo,
        IReadOnlyList<FaceTrackFilledFace> CutGuardFacesInfo)
    {
        public static YoloFinalMaskGapFillResult Empty { get; } = new(0, Array.Empty<int>(), Array.Empty<FaceTrackFilledFace>(), Array.Empty<FaceTrackFilledFace>());
    }
}
