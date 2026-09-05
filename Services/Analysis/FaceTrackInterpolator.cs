using Avalonia;
using FaceShield.Services.Video;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace FaceShield.Services.Analysis
{
    public sealed class FaceTrackInterpolator
    {
        public FaceTrackPostProcessResult Apply(
            FrameMaskProvider maskProvider,
            int totalFrames,
            FaceTrackPostProcessOptions? options = null,
            IReadOnlySet<int>? blockedSceneCutStarts = null,
            int mutableStartFrameIndex = 0,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            options ??= new FaceTrackPostProcessOptions();
            if (totalFrames < 3)
                return FaceTrackPostProcessResult.Empty;
            int firstMutableFrame = Math.Clamp(mutableStartFrameIndex, 0, totalFrames);

            var hasStoredMask = new SparseFrameSet();
            foreach (int index in maskProvider.GetStoredMaskFrameIndices())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (index >= 0 && index < totalFrames)
                    hasStoredMask[index] = true;
            }

            var facesByFrame = new SparseFrameMap<List<Rect>?>(totalFrames);
            var confByFrame = new SparseFrameMap<List<float>?>(totalFrames);
            var sizeByFrame = new SparseFrameMap<PixelSize>(totalFrames);
            var detectionsByFrame = new Dictionary<int, IReadOnlyList<FaceTrackDetection>>();

            foreach (var entry in maskProvider.GetFaceMaskEntries())
            {
                cancellationToken.ThrowIfCancellationRequested();
                int frameIndex = entry.Key;
                if (frameIndex < 0 || frameIndex >= totalFrames || hasStoredMask[frameIndex])
                    continue;

                var data = entry.Value;
                if (data.Faces.Count == 0)
                    continue;

                var faces = new List<Rect>(data.Faces);
                var confs = NormalizeConfidences(data.Faces.Count, data.Confidences, data.MinConfidence);
                facesByFrame[frameIndex] = faces;
                confByFrame[frameIndex] = confs;
                sizeByFrame[frameIndex] = data.Size;

                var detections = new List<FaceTrackDetection>(faces.Count);
                for (int i = 0; i < faces.Count; i++)
                    detections.Add(new FaceTrackDetection(frameIndex, faces[i], data.Size, confs[i]));

                detectionsByFrame[frameIndex] = detections;
            }

            var builder = new FaceTrackBuilder();
            var tracks = builder.Build(detectionsByFrame, options, cancellationToken);
            var removedTrackIds = new HashSet<int>();
            var gapFilledFaces = new List<FaceTrackFilledFace>();
            var lostFillFrameIndices = new List<int>();
            var lostFilledFaces = new List<FaceTrackFilledFace>();
            var initialFilledFaces = new List<FaceTrackFilledFace>();
            int removedUnstableTailFaces = TrimUnstableLowConfidenceTails(tracks, facesByFrame, confByFrame, options, cancellationToken);
            int removedEdgeTailFaces = TrimEdgeLowConfidenceTails(tracks, facesByFrame, confByFrame, options, cancellationToken);
            int removedLowerFrameFaces = RemoveLowerFrameLowConfidenceTracks(tracks, facesByFrame, confByFrame, options, removedTrackIds, cancellationToken);
            int removedLowEvidenceFaces = RemoveLowEvidenceTracks(tracks, facesByFrame, confByFrame, options, removedTrackIds, cancellationToken);
            int removedSparseFaces = RemoveSparseLowConfidenceTracks(tracks, facesByFrame, confByFrame, options, removedTrackIds, cancellationToken);
            int removedFaces = removedLowEvidenceFaces + RemoveShortLowConfidenceTracks(tracks, facesByFrame, confByFrame, options, removedTrackIds, cancellationToken);
            int filledFrames = FillShortTrackGaps(tracks, facesByFrame, confByFrame, sizeByFrame, hasStoredMask, options, removedTrackIds, gapFilledFaces, blockedSceneCutStarts, firstMutableFrame, cancellationToken);
            int lostFilledFrames = FillConfirmedLostFrames(tracks, facesByFrame, confByFrame, sizeByFrame, hasStoredMask, options, removedTrackIds, lostFillFrameIndices, lostFilledFaces, blockedSceneCutStarts, firstMutableFrame, cancellationToken);
            int initialFilledFrames = FillConfirmedInitialFrames(tracks, facesByFrame, confByFrame, sizeByFrame, hasStoredMask, options, removedTrackIds, initialFilledFaces, blockedSceneCutStarts, firstMutableFrame, out int blockedInitialFillTracks, cancellationToken);
            int rewrittenFrames = RewriteMaskProvider(maskProvider, facesByFrame, confByFrame, sizeByFrame, hasStoredMask, firstMutableFrame, cancellationToken);

            return new FaceTrackPostProcessResult(
                tracks.Count,
                filledFrames,
                gapFilledFaces.ToArray(),
                lostFilledFrames,
                lostFillFrameIndices.ToArray(),
                lostFilledFaces.ToArray(),
                initialFilledFrames,
                initialFilledFaces.ToArray(),
                blockedInitialFillTracks,
                removedFaces,
                removedSparseFaces,
                removedUnstableTailFaces,
                removedEdgeTailFaces,
                removedLowerFrameFaces,
                rewrittenFrames);
        }

        private static int TrimUnstableLowConfidenceTails(
            IReadOnlyList<FaceTrack> tracks,
            SparseFrameMap<List<Rect>?> facesByFrame,
            SparseFrameMap<List<float>?> confByFrame,
            FaceTrackPostProcessOptions options,
            CancellationToken cancellationToken)
        {
            if (options.UnstableTailMaxConfidence <= 0)
                return 0;

            int removed = 0;
            foreach (var track in tracks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int keepCount = track.DetectionCount;
                while (keepCount > options.UnstableTailMinStableDetections)
                {
                    var previous = track.Detections[keepCount - 2];
                    var current = track.Detections[keepCount - 1];
                    if (current.Confidence > options.UnstableTailMaxConfidence)
                        break;
                    if (!IsUnstableTailTransition(previous.Bounds, current.Bounds, options))
                        break;

                    RemoveDetectionFromFrame(current, facesByFrame, confByFrame);
                    keepCount--;
                    removed++;
                }

                if (keepCount < track.DetectionCount)
                    track.RemoveDetectionsFrom(keepCount);
            }

            return removed;
        }

        private static int TrimEdgeLowConfidenceTails(
            IReadOnlyList<FaceTrack> tracks,
            SparseFrameMap<List<Rect>?> facesByFrame,
            SparseFrameMap<List<float>?> confByFrame,
            FaceTrackPostProcessOptions options,
            CancellationToken cancellationToken)
        {
            if (options.EdgeTailMaxConfidence <= 0)
                return 0;

            int removed = 0;
            foreach (var track in tracks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int keepCount = track.DetectionCount;
                while (keepCount > options.EdgeTailMinStableDetections)
                {
                    var current = track.Detections[keepCount - 1];
                    if (current.Confidence > options.EdgeTailMaxConfidence ||
                        !TouchesFrameEdge(current, options))
                    {
                        break;
                    }

                    RemoveDetectionFromFrame(current, facesByFrame, confByFrame);
                    keepCount--;
                    removed++;
                }

                if (keepCount < track.DetectionCount)
                    track.RemoveDetectionsFrom(keepCount);
            }

            return removed;
        }

        private static int RemoveLowerFrameLowConfidenceTracks(
            IReadOnlyList<FaceTrack> tracks,
            SparseFrameMap<List<Rect>?> facesByFrame,
            SparseFrameMap<List<float>?> confByFrame,
            FaceTrackPostProcessOptions options,
            ISet<int> removedTrackIds,
            CancellationToken cancellationToken)
        {
            if (options.LowerFrameTrackMaxConfidence <= 0)
                return 0;

            int removed = 0;
            foreach (var track in tracks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (removedTrackIds.Contains(track.Id) ||
                    track.MaxConfidence > options.LowerFrameTrackMaxConfidence ||
                    !IsLowerFrameMediumAreaTrack(track, options))
                {
                    continue;
                }

                removedTrackIds.Add(track.Id);
                removed += RemoveTrackDetections(track, facesByFrame, confByFrame, cancellationToken);
            }

            return removed;
        }

        private static bool IsLowerFrameMediumAreaTrack(FaceTrack track, FaceTrackPostProcessOptions options)
        {
            if (track.DetectionCount == 0)
                return false;

            for (int i = 0; i < track.Detections.Count; i++)
            {
                var detection = track.Detections[i];
                if (detection.Size.Width <= 0 || detection.Size.Height <= 0)
                    return false;

                double frameArea = Math.Max(1.0, detection.Size.Width * (double)detection.Size.Height);
                double areaRatio = Math.Max(0.0, detection.Bounds.Width * detection.Bounds.Height) / frameArea;
                double centerYRatio = (detection.Bounds.Y + detection.Bounds.Height * 0.5) / detection.Size.Height;
                if (centerYRatio < options.LowerFrameTrackMinCenterYRatio ||
                    areaRatio < options.LowerFrameTrackMinAreaRatio ||
                    areaRatio > options.LowerFrameTrackMaxAreaRatio)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsUnstableTailTransition(Rect previous, Rect current, FaceTrackPostProcessOptions options)
        {
            double areaRatio = FaceTrackBuilder.GetAreaRatio(previous, current);
            if (areaRatio > options.UnstableTailMaxAreaChangeRatio ||
                areaRatio < 1.0 / options.UnstableTailMaxAreaChangeRatio)
            {
                return true;
            }

            return FaceTrackBuilder.IoU(previous, current) < options.UnstableTailMinIou;
        }

        private static void RemoveDetectionFromFrame(
            FaceTrackDetection detection,
            SparseFrameMap<List<Rect>?> facesByFrame,
            SparseFrameMap<List<float>?> confByFrame)
        {
            var faces = facesByFrame[detection.FrameIndex];
            var confs = confByFrame[detection.FrameIndex];
            if (faces == null || confs == null)
                return;

            for (int i = faces.Count - 1; i >= 0; i--)
            {
                if (FaceTrackBuilder.IoU(faces[i], detection.Bounds) < 0.95)
                    continue;

                faces.RemoveAt(i);
                if (i < confs.Count)
                    confs.RemoveAt(i);
            }

            if (faces.Count == 0)
            {
                facesByFrame[detection.FrameIndex] = null;
                confByFrame[detection.FrameIndex] = null;
            }
        }

        private static int RemoveLowEvidenceTracks(
            IReadOnlyList<FaceTrack> tracks,
            SparseFrameMap<List<Rect>?> facesByFrame,
            SparseFrameMap<List<float>?> confByFrame,
            FaceTrackPostProcessOptions options,
            ISet<int> removedTrackIds,
            CancellationToken cancellationToken)
        {
            if (!options.EnableWeightedTrackEvidence ||
                options.LowEvidenceRejectMaxDetections <= 0 ||
                options.LowEvidenceRejectMaxConfidence <= 0 ||
                options.LowEvidenceRejectScore <= 0)
            {
                return 0;
            }

            int removed = 0;
            foreach (var track in tracks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (removedTrackIds.Contains(track.Id) ||
                    track.DetectionCount <= 0 ||
                    track.DetectionCount > options.LowEvidenceRejectMaxDetections ||
                    track.MaxConfidence > options.LowEvidenceRejectMaxConfidence ||
                    CouldBePartialFace(track, tracks, options))
                {
                    continue;
                }

                var evidence = FaceTrackEvidenceScorer.Evaluate(track, options);
                if (evidence.Score >= options.LowEvidenceRejectScore)
                    continue;

                removedTrackIds.Add(track.Id);
                int removedOnTrack = RemoveTrackDetections(
                    track,
                    facesByFrame,
                    confByFrame,
                    cancellationToken);
                removed += removedOnTrack;
                Debug.WriteLine(
                    $"[FaceTrackEvidence] track={track.Id} action=reject detections={track.DetectionCount} score={evidence.Score:0.000} meanConfidence={evidence.MeanConfidence:0.000} density={evidence.DetectionDensity:0.000} continuity={evidence.Continuity:0.000} persistence={evidence.Persistence:0.000} removedFaces={removedOnTrack}");
            }

            return removed;
        }

        private static int RemoveShortLowConfidenceTracks(
            IReadOnlyList<FaceTrack> tracks,
            SparseFrameMap<List<Rect>?> facesByFrame,
            SparseFrameMap<List<float>?> confByFrame,
            FaceTrackPostProcessOptions options,
            ISet<int> removedTrackIds,
            CancellationToken cancellationToken)
        {
            int removed = 0;
            foreach (var track in tracks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (removedTrackIds.Contains(track.Id))
                    continue;

                bool couldBePartialFace = CouldBePartialFace(track, tracks, options);
                bool removeShortWeak = track.DetectionCount <= options.DropShortTrackMaxDetections &&
                    track.MaxConfidence < options.ShortTrackMaxConfidence &&
                    !couldBePartialFace;
                bool removeShortSmall = track.DetectionCount <= options.DropShortSmallTrackMaxDetections &&
                    IsSmallTrack(track, options) &&
                    !couldBePartialFace;

                if (!removeShortWeak && !removeShortSmall)
                    continue;

                removedTrackIds.Add(track.Id);
                removed += RemoveTrackDetections(track, facesByFrame, confByFrame, cancellationToken);
            }

            return removed;
        }

        private static int RemoveSparseLowConfidenceTracks(
            IReadOnlyList<FaceTrack> tracks,
            SparseFrameMap<List<Rect>?> facesByFrame,
            SparseFrameMap<List<float>?> confByFrame,
            FaceTrackPostProcessOptions options,
            ISet<int> removedTrackIds,
            CancellationToken cancellationToken)
        {
            if (options.DropSparseTrackMaxDetections <= 0 ||
                options.SparseTrackMaxConfidence <= 0 ||
                options.DropSparseTrackMaxDensity <= 0)
            {
                return 0;
            }

            int removed = 0;
            foreach (var track in tracks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (removedTrackIds.Contains(track.Id) ||
                    !IsSparseLowConfidenceTrack(track, tracks, options))
                {
                    continue;
                }

                removedTrackIds.Add(track.Id);
                removed += RemoveTrackDetections(track, facesByFrame, confByFrame, cancellationToken);
            }

            return removed;
        }

        private static bool IsSparseLowConfidenceTrack(
            FaceTrack track,
            IReadOnlyList<FaceTrack> tracks,
            FaceTrackPostProcessOptions options)
        {
            if (track.DetectionCount <= 0 ||
                track.DetectionCount > options.DropSparseTrackMaxDetections ||
                track.MaxConfidence >= options.SparseTrackMaxConfidence ||
                CouldBePartialFace(track, tracks, options))
            {
                return false;
            }

            int span = track.Detections[^1].FrameIndex - track.Detections[0].FrameIndex + 1;
            if (span < options.DropSparseTrackMinSpanFrames)
                return false;

            double density = track.DetectionCount / (double)Math.Max(1, span);
            return density <= options.DropSparseTrackMaxDensity;
        }

        private static int RemoveTrackDetections(
            FaceTrack track,
            SparseFrameMap<List<Rect>?> facesByFrame,
            SparseFrameMap<List<float>?> confByFrame,
            CancellationToken cancellationToken)
        {
            int removed = 0;
            foreach (var detection in track.Detections)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var faces = facesByFrame[detection.FrameIndex];
                var confs = confByFrame[detection.FrameIndex];
                if (faces == null || confs == null)
                    continue;

                for (int i = faces.Count - 1; i >= 0; i--)
                {
                    if (FaceTrackBuilder.IoU(faces[i], detection.Bounds) < 0.95)
                        continue;

                    faces.RemoveAt(i);
                    if (i < confs.Count)
                        confs.RemoveAt(i);
                    removed++;
                }

                if (faces.Count == 0)
                {
                    facesByFrame[detection.FrameIndex] = null;
                    confByFrame[detection.FrameIndex] = null;
                }
            }

            return removed;
        }

        private static bool IsSmallTrack(FaceTrack track, FaceTrackPostProcessOptions options)
        {
            if (track.DetectionCount == 0)
                return false;

            for (int i = 0; i < track.Detections.Count; i++)
            {
                var detection = track.Detections[i];
                double frameArea = Math.Max(1.0, detection.Size.Width * (double)detection.Size.Height);
                double area = Math.Max(0.0, detection.Bounds.Width * detection.Bounds.Height);
                if (area > frameArea * options.SmallTrackMaxAreaRatio)
                    return false;
            }

            return true;
        }

        private static bool CouldBePartialFace(
            FaceTrack track,
            IReadOnlyList<FaceTrack> tracks,
            FaceTrackPostProcessOptions options)
        {
            if (IsConfirmedSmallTrack(track, options))
                return true;

            for (int i = 0; i < track.Detections.Count; i++)
            {
                var detection = track.Detections[i];
                if (TouchesFrameEdge(detection, options))
                    return true;
            }

            return false;
        }

        private static bool IsConfirmedSmallTrack(FaceTrack track, FaceTrackPostProcessOptions options)
            => track.DetectionCount >= options.ConfirmedTrackMinDetections &&
                track.MaxConfidence >= options.StrongConfidence &&
                IsSmallTrack(track, options);

        private static bool TouchesFrameEdge(FaceTrackDetection detection, FaceTrackPostProcessOptions options)
        {
            if (detection.Size.Width <= 0 || detection.Size.Height <= 0)
                return false;

            double marginX = detection.Size.Width * options.EdgePartialFaceMarginRatio;
            double marginY = detection.Size.Height * options.EdgePartialFaceMarginRatio;
            return detection.Bounds.X <= marginX ||
                detection.Bounds.Y <= marginY ||
                detection.Bounds.Right >= detection.Size.Width - marginX ||
                detection.Bounds.Bottom >= detection.Size.Height - marginY;
        }

        private static int FillShortTrackGaps(
            IReadOnlyList<FaceTrack> tracks,
            SparseFrameMap<List<Rect>?> facesByFrame,
            SparseFrameMap<List<float>?> confByFrame,
            SparseFrameMap<PixelSize> sizeByFrame,
            SparseFrameSet hasStoredMask,
            FaceTrackPostProcessOptions options,
            IReadOnlySet<int> removedTrackIds,
            ICollection<FaceTrackFilledFace> gapFilledFaces,
            IReadOnlySet<int>? blockedSceneCutStarts,
            int firstMutableFrame,
            CancellationToken cancellationToken)
        {
            int filled = 0;
            foreach (var track in tracks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (removedTrackIds.Contains(track.Id))
                    continue;

                var detections = track.Detections;
                for (int i = 1; i < detections.Count; i++)
                {
                    var previous = detections[i - 1];
                    var next = detections[i];
                    int gap = next.FrameIndex - previous.FrameIndex - 1;
                    if (gap <= 0)
                        continue;
                    if (previous.FrameIndex < firstMutableFrame)
                        continue;

                    int maxFillGap = options.MaxFillGap;
                    if (IsConfirmedTrack(track, options))
                        maxFillGap = Math.Max(maxFillGap, options.MaxConfirmedTrackHoldFrames);

                    if (gap > maxFillGap)
                        continue;

                    if (CrossesSceneCut(previous.FrameIndex, next.FrameIndex, blockedSceneCutStarts))
                        continue;

                    if (!CanBridge(previous.Bounds, next.Bounds, gap, options))
                        continue;

                    for (int frameIndex = previous.FrameIndex + 1; frameIndex < next.FrameIndex; frameIndex++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (frameIndex < firstMutableFrame)
                            continue;
                        if (hasStoredMask[frameIndex])
                            continue;

                        double t = (double)(frameIndex - previous.FrameIndex) / (next.FrameIndex - previous.FrameIndex);
                        var interpolated = Interpolate(previous.Bounds, next.Bounds, t);
                        if (interpolated.Width <= 0 || interpolated.Height <= 0)
                            continue;

                        facesByFrame[frameIndex] ??= new List<Rect>();
                        confByFrame[frameIndex] ??= new List<float>();

                        if (HasSimilarFace(interpolated, facesByFrame[frameIndex], options.DuplicateIou))
                            continue;

                        if (sizeByFrame[frameIndex].Width <= 0 || sizeByFrame[frameIndex].Height <= 0)
                        {
                            sizeByFrame[frameIndex] = previous.Size.Width > 0 && previous.Size.Height > 0
                                ? previous.Size
                                : next.Size;
                        }

                        float confidence = ClampSyntheticFillConfidence(
                            Math.Min(previous.Confidence, next.Confidence),
                            options);
                        facesByFrame[frameIndex]!.Add(interpolated);
                        confByFrame[frameIndex]!.Add(confidence);
                        gapFilledFaces.Add(new FaceTrackFilledFace(frameIndex, interpolated, sizeByFrame[frameIndex], confidence, previous.FrameIndex));
                        filled++;
                    }
                }
            }

            return filled;
        }

        private static int FillConfirmedInitialFrames(
            IReadOnlyList<FaceTrack> tracks,
            SparseFrameMap<List<Rect>?> facesByFrame,
            SparseFrameMap<List<float>?> confByFrame,
            SparseFrameMap<PixelSize> sizeByFrame,
            SparseFrameSet hasStoredMask,
            FaceTrackPostProcessOptions options,
            IReadOnlySet<int> removedTrackIds,
            ICollection<FaceTrackFilledFace> initialFilledFaces,
            IReadOnlySet<int>? blockedSceneCutStarts,
            int firstMutableFrame,
            out int blockedInitialFillTracks,
            CancellationToken cancellationToken)
        {
            blockedInitialFillTracks = 0;
            if (options.MaxInitialFillFrames <= 0)
                return 0;

            int filled = 0;
            foreach (var track in tracks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (removedTrackIds.Contains(track.Id) ||
                    !IsConfirmedTrack(track, options) ||
                    track.Detections.Count < 2)
                {
                    continue;
                }

                var first = track.Detections[0];
                if (first.FrameIndex <= 0 ||
                    first.Confidence < options.StrongConfidence ||
                    !TouchesFrameEdge(first, options))
                {
                    continue;
                }

                var second = track.Detections[1];
                if (options.InitialFillRequiresInwardMotion &&
                    !IsMovingInwardFromTouchedEdge(first.Bounds, second.Bounds, first.Size, options))
                {
                    blockedInitialFillTracks++;
                    continue;
                }

                int frameDelta = Math.Max(1, second.FrameIndex - first.FrameIndex);
                double dx = (second.Bounds.X - first.Bounds.X) / frameDelta;
                double dy = (second.Bounds.Y - first.Bounds.Y) / frameDelta;
                double dw = (second.Bounds.Width - first.Bounds.Width) / frameDelta;
                double dh = (second.Bounds.Height - first.Bounds.Height) / frameDelta;

                for (int offset = 1; offset <= options.MaxInitialFillFrames; offset++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int frameIndex = first.FrameIndex - offset;
                    if (frameIndex < firstMutableFrame)
                        break;
                    if (CrossesSceneCut(frameIndex, first.FrameIndex, blockedSceneCutStarts))
                        break;
                    if (hasStoredMask[frameIndex])
                        continue;

                    var predicted = new Rect(
                        first.Bounds.X - dx * offset,
                        first.Bounds.Y - dy * offset,
                        Math.Max(1.0, first.Bounds.Width - dw * offset),
                        Math.Max(1.0, first.Bounds.Height - dh * offset));

                    if (!IsMostlyInside(predicted, first.Size))
                        break;

                    predicted = ClampToSize(predicted, first.Size);
                    if (predicted.Width <= 0 || predicted.Height <= 0)
                        break;

                    facesByFrame[frameIndex] ??= new List<Rect>();
                    confByFrame[frameIndex] ??= new List<float>();

                    if (HasSimilarFace(predicted, facesByFrame[frameIndex], options.DuplicateIou))
                        continue;

                    if (sizeByFrame[frameIndex].Width <= 0 || sizeByFrame[frameIndex].Height <= 0)
                        sizeByFrame[frameIndex] = first.Size;

                    float confidence = ClampSyntheticFillConfidence(first.Confidence, options);
                    facesByFrame[frameIndex]!.Add(predicted);
                    confByFrame[frameIndex]!.Add(confidence);
                    initialFilledFaces.Add(new FaceTrackFilledFace(frameIndex, predicted, first.Size, confidence, first.FrameIndex));
                    filled++;
                }
            }

            return filled;
        }

        private static bool IsMovingInwardFromTouchedEdge(
            Rect first,
            Rect second,
            PixelSize size,
            FaceTrackPostProcessOptions options)
        {
            if (size.Width <= 0 || size.Height <= 0)
                return false;

            double marginX = size.Width * options.EdgePartialFaceMarginRatio;
            double marginY = size.Height * options.EdgePartialFaceMarginRatio;
            double epsilonX = Math.Max(1.0, size.Width * 0.0025);
            double epsilonY = Math.Max(1.0, size.Height * 0.0025);
            bool touchedEdge = false;
            bool inward = false;

            if (first.X <= marginX)
            {
                touchedEdge = true;
                if (second.X < first.X - epsilonX)
                    return false;
                inward |= second.X > first.X + epsilonX;
            }

            double firstRight = first.X + first.Width;
            double secondRight = second.X + second.Width;
            if (firstRight >= size.Width - marginX)
            {
                touchedEdge = true;
                if (secondRight > firstRight + epsilonX)
                    return false;
                inward |= secondRight < firstRight - epsilonX;
            }

            if (first.Y <= marginY)
            {
                touchedEdge = true;
                if (second.Y < first.Y - epsilonY)
                    return false;
                inward |= second.Y > first.Y + epsilonY;
            }

            double firstBottom = first.Y + first.Height;
            double secondBottom = second.Y + second.Height;
            if (firstBottom >= size.Height - marginY)
            {
                touchedEdge = true;
                if (secondBottom > firstBottom + epsilonY)
                    return false;
                inward |= secondBottom < firstBottom - epsilonY;
            }

            return touchedEdge && inward;
        }

        private static int FillConfirmedLostFrames(
            IReadOnlyList<FaceTrack> tracks,
            SparseFrameMap<List<Rect>?> facesByFrame,
            SparseFrameMap<List<float>?> confByFrame,
            SparseFrameMap<PixelSize> sizeByFrame,
            SparseFrameSet hasStoredMask,
            FaceTrackPostProcessOptions options,
            IReadOnlySet<int> removedTrackIds,
            ICollection<int> lostFillFrameIndices,
            ICollection<FaceTrackFilledFace> lostFilledFaces,
            IReadOnlySet<int>? blockedSceneCutStarts,
            int firstMutableFrame,
            CancellationToken cancellationToken)
        {
            if (options.MaxLostFillFrames <= 0)
                return 0;

            int filled = 0;
            foreach (var track in tracks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (removedTrackIds.Contains(track.Id) ||
                    !IsConfirmedTrack(track, options))
                {
                    continue;
                }
                if (!options.AllowSmallTrackLostFill && IsSmallTrack(track, options))
                    continue;

                int maxLostFillFrames = ResolveLostFillFrames(track, options);
                if (maxLostFillFrames <= 0)
                    continue;

                var detections = track.Detections;
                var last = detections[^1];
                int lastFrame = last.FrameIndex;
                if (lastFrame < 0 || lastFrame >= facesByFrame.Length - 1)
                    continue;
                if (lastFrame < firstMutableFrame)
                    continue;
                if (options.EdgeLostFillMaxConfidence > 0 &&
                    last.Confidence <= options.EdgeLostFillMaxConfidence &&
                    TouchesFrameEdge(last, options))
                {
                    continue;
                }

                var previous = detections.Count >= 2 ? detections[^2] : last;
                int frameDelta = Math.Max(1, last.FrameIndex - previous.FrameIndex);
                double dx = (last.Bounds.X - previous.Bounds.X) / frameDelta;
                double dy = (last.Bounds.Y - previous.Bounds.Y) / frameDelta;
                double dw = (last.Bounds.Width - previous.Bounds.Width) / frameDelta;
                double dh = (last.Bounds.Height - previous.Bounds.Height) / frameDelta;

                for (int offset = 1; offset <= maxLostFillFrames; offset++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int frameIndex = lastFrame + offset;
                    if (frameIndex >= facesByFrame.Length)
                        break;
                    if (frameIndex < firstMutableFrame)
                        continue;
                    if (CrossesSceneCut(lastFrame, frameIndex, blockedSceneCutStarts))
                        break;
                    if (hasStoredMask[frameIndex])
                        continue;

                    var predicted = new Rect(
                        last.Bounds.X + dx * offset,
                        last.Bounds.Y + dy * offset,
                        Math.Max(1.0, last.Bounds.Width + dw * offset),
                        Math.Max(1.0, last.Bounds.Height + dh * offset));

                    if (!IntersectsFrame(predicted, last.Size))
                        break;

                    predicted = ClampToSize(predicted, last.Size);
                    if (predicted.Width <= 0 || predicted.Height <= 0)
                        break;

                    facesByFrame[frameIndex] ??= new List<Rect>();
                    confByFrame[frameIndex] ??= new List<float>();

                    if (HasSimilarFace(predicted, facesByFrame[frameIndex], options.DuplicateIou))
                        continue;

                    if (sizeByFrame[frameIndex].Width <= 0 || sizeByFrame[frameIndex].Height <= 0)
                        sizeByFrame[frameIndex] = last.Size;

                    float confidence = ClampSyntheticFillConfidence(last.Confidence, options);
                    facesByFrame[frameIndex]!.Add(predicted);
                    confByFrame[frameIndex]!.Add(confidence);
                    lostFillFrameIndices.Add(frameIndex);
                    lostFilledFaces.Add(new FaceTrackFilledFace(frameIndex, predicted, last.Size, confidence, last.FrameIndex));
                    filled++;
                }
            }

            return filled;
        }

        private static bool CrossesSceneCut(
            int firstFrame,
            int secondFrame,
            IReadOnlySet<int>? blockedSceneCutStarts)
        {
            if (blockedSceneCutStarts == null || blockedSceneCutStarts.Count == 0)
                return false;

            int start = Math.Min(firstFrame, secondFrame) + 1;
            int end = Math.Max(firstFrame, secondFrame);
            for (int frame = start; frame <= end; frame++)
            {
                if (blockedSceneCutStarts.Contains(frame))
                    return true;
            }

            return false;
        }

        private static float ClampSyntheticFillConfidence(float sourceConfidence, FaceTrackPostProcessOptions options)
        {
            float maxConfidence = Math.Clamp(options.SyntheticFillConfidenceMax, options.WeakConfidence, 1.0f);
            return Math.Clamp(sourceConfidence, options.WeakConfidence, maxConfidence);
        }

        private static bool IsConfirmedTrack(FaceTrack track, FaceTrackPostProcessOptions options)
        {
            if (track.DetectionCount < options.ConfirmedTrackMinDetections ||
                track.MaxConfidence < options.StrongConfidence)
            {
                return false;
            }

            if (!options.EnableWeightedTrackEvidence ||
                options.MinConfirmedTrackEvidenceScore <= 0)
            {
                return true;
            }

            return FaceTrackEvidenceScorer.Evaluate(track, options).Score >=
                options.MinConfirmedTrackEvidenceScore;
        }

        private static int ResolveLostFillFrames(
            FaceTrack track,
            FaceTrackPostProcessOptions options)
        {
            int baseFrames = Math.Max(0, options.MaxLostFillFrames);
            if (!options.EnableWeightedTrackEvidence ||
                options.MaxStrongTrackLostFillFrames <= baseFrames ||
                options.StrongTrackEvidenceScore <= 0)
            {
                return baseFrames;
            }

            var evidence = FaceTrackEvidenceScorer.Evaluate(track, options);
            if (evidence.Score < options.StrongTrackEvidenceScore)
                return baseFrames;

            Debug.WriteLine(
                $"[FaceTrackEvidence] track={track.Id} action=extend-hold score={evidence.Score:0.000} baseFrames={baseFrames} strongFrames={options.MaxStrongTrackLostFillFrames}");
            return options.MaxStrongTrackLostFillFrames;
        }

        private static bool CanBridge(Rect previous, Rect next, int gap, FaceTrackPostProcessOptions options)
        {
            double areaRatio = FaceTrackBuilder.GetAreaRatio(previous, next);
            if (areaRatio > options.MaxAreaChangeRatio || areaRatio < 1.0 / options.MaxAreaChangeRatio)
                return false;

            double iou = FaceTrackBuilder.IoU(previous, next);
            double centerShift = FaceTrackBuilder.GetNormalizedCenterShift(previous, next);
            double allowedShift = options.MaxCenterShiftRatio * Math.Max(1, gap + 1);
            if (gap > options.MaxFillGap &&
                options.MaxConfirmedTrackBridgeCenterShiftRatio > 0.0 &&
                centerShift > options.MaxConfirmedTrackBridgeCenterShiftRatio)
            {
                return false;
            }

            return iou >= options.MinTrackIou || centerShift <= allowedShift;
        }

        private static int RewriteMaskProvider(
            FrameMaskProvider maskProvider,
            SparseFrameMap<List<Rect>?> facesByFrame,
            SparseFrameMap<List<float>?> confByFrame,
            SparseFrameMap<PixelSize> sizeByFrame,
            SparseFrameSet hasStoredMask,
            int firstMutableFrame,
            CancellationToken cancellationToken)
        {
            int rewritten = 0;
            foreach (int frameIndex in facesByFrame.TouchedKeys
                         .Where(index => index >= firstMutableFrame)
                         .OrderBy(static index => index))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (hasStoredMask[frameIndex])
                    continue;

                var faces = facesByFrame[frameIndex];
                if (faces == null || faces.Count == 0)
                {
                    maskProvider.RemoveFaceMask(frameIndex);
                    continue;
                }

                var confs = confByFrame[frameIndex] ?? Enumerable.Repeat(1.0f, faces.Count).ToList();
                while (confs.Count < faces.Count)
                    confs.Add(1.0f);
                if (confs.Count > faces.Count)
                    confs.RemoveRange(faces.Count, confs.Count - faces.Count);

                float minConfidence = float.MaxValue;
                for (int i = 0; i < confs.Count; i++)
                    minConfidence = Math.Min(minConfidence, confs[i]);

                maskProvider.SetFaceRects(
                    frameIndex,
                    faces,
                    sizeByFrame[frameIndex],
                    minConfidence == float.MaxValue ? null : minConfidence,
                    confs);
                rewritten++;
            }

            return rewritten;
        }

        private sealed class SparseFrameMap<T>
        {
            private readonly Dictionary<int, T> _values = new();
            private readonly HashSet<int> _touched = new();

            public SparseFrameMap(int length)
            {
                Length = Math.Max(0, length);
            }

            public int Length { get; }
            public IEnumerable<int> TouchedKeys => _touched;

            public T this[int frameIndex]
            {
                get => _values.TryGetValue(frameIndex, out var value)
                    ? value
                    : default!;
                set
                {
                    _touched.Add(frameIndex);
                    if (EqualityComparer<T>.Default.Equals(value, default!))
                        _values.Remove(frameIndex);
                    else
                        _values[frameIndex] = value;
                }
            }
        }

        private sealed class SparseFrameSet
        {
            private readonly HashSet<int> _values = new();

            public bool this[int frameIndex]
            {
                get => _values.Contains(frameIndex);
                set
                {
                    if (value)
                        _values.Add(frameIndex);
                    else
                        _values.Remove(frameIndex);
                }
            }
        }

        private static List<float> NormalizeConfidences(
            int faceCount,
            IReadOnlyList<float> confidences,
            float? minConfidence)
        {
            if (confidences.Count == faceCount)
                return new List<float>(confidences);

            float fallback = minConfidence ?? 1.0f;
            var normalized = new List<float>(faceCount);
            for (int i = 0; i < faceCount; i++)
                normalized.Add(fallback);
            return normalized;
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

        private static bool HasSimilarFace(Rect candidate, IReadOnlyList<Rect>? faces, double minIou)
        {
            if (faces == null || faces.Count == 0)
                return false;

            for (int i = 0; i < faces.Count; i++)
            {
                if (FaceTrackBuilder.IoU(candidate, faces[i]) >= minIou)
                    return true;
            }

            return false;
        }

        private static bool IntersectsFrame(Rect rect, PixelSize size)
        {
            if (size.Width <= 0 || size.Height <= 0 || rect.Width <= 0 || rect.Height <= 0)
                return false;

            return rect.Right > 0 && rect.Bottom > 0 &&
                rect.X < size.Width && rect.Y < size.Height;
        }

        private static bool IsMostlyInside(Rect rect, PixelSize size)
        {
            if (size.Width <= 0 || size.Height <= 0)
                return false;

            double x0 = Math.Max(0, rect.X);
            double y0 = Math.Max(0, rect.Y);
            double x1 = Math.Min(size.Width, rect.Right);
            double y1 = Math.Min(size.Height, rect.Bottom);
            double inside = Math.Max(0.0, x1 - x0) * Math.Max(0.0, y1 - y0);
            double area = Math.Max(1.0, rect.Width * rect.Height);
            return inside / area >= 0.60;
        }

        private static Rect ClampToSize(Rect rect, PixelSize size)
        {
            double x = Math.Max(0, rect.X);
            double y = Math.Max(0, rect.Y);
            double right = Math.Min(size.Width, rect.Right);
            double bottom = Math.Min(size.Height, rect.Bottom);
            return new Rect(x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
        }
    }

    public readonly record struct FaceTrackPostProcessResult(
        int TrackCount,
        int FilledGapFaces,
        IReadOnlyList<FaceTrackFilledFace> FilledGapFacesInfo,
        int FilledLostFaces,
        IReadOnlyList<int> FilledLostFrameIndices,
        IReadOnlyList<FaceTrackFilledFace> FilledLostFacesInfo,
        int FilledInitialFaces,
        IReadOnlyList<FaceTrackFilledFace> FilledInitialFacesInfo,
        int BlockedInitialFillTracks,
        int RemovedShortFaces,
        int RemovedSparseFaces,
        int RemovedUnstableTailFaces,
        int RemovedEdgeTailFaces,
        int RemovedLowerFrameFaces,
        int RewrittenFrames)
    {
        public static FaceTrackPostProcessResult Empty { get; } = new(0, 0, Array.Empty<FaceTrackFilledFace>(), 0, Array.Empty<int>(), Array.Empty<FaceTrackFilledFace>(), 0, Array.Empty<FaceTrackFilledFace>(), 0, 0, 0, 0, 0, 0, 0);
    }

    public readonly record struct FaceTrackFilledFace(
        int FrameIndex,
        Rect Bounds,
        PixelSize Size,
        float Confidence,
        int SourceFrameIndex)
    {
        public FaceTrackFilledFace(
            int FrameIndex,
            Rect Bounds,
            PixelSize Size,
            float Confidence)
            : this(FrameIndex, Bounds, Size, Confidence, FrameIndex)
        {
        }
    }
}
