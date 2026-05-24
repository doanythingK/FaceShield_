using Avalonia;
using FaceShield.Services.Video;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FaceShield.Services.Analysis
{
    public sealed class FaceTrackInterpolator
    {
        public FaceTrackPostProcessResult Apply(
            FrameMaskProvider maskProvider,
            int totalFrames,
            FaceTrackPostProcessOptions? options = null)
        {
            options ??= new FaceTrackPostProcessOptions();
            if (totalFrames < 3)
                return FaceTrackPostProcessResult.Empty;

            var hasStoredMask = new bool[totalFrames];
            foreach (int index in maskProvider.GetStoredMaskFrameIndices())
            {
                if (index >= 0 && index < totalFrames)
                    hasStoredMask[index] = true;
            }

            var facesByFrame = new List<Rect>?[totalFrames];
            var confByFrame = new List<float>?[totalFrames];
            var sizeByFrame = new PixelSize[totalFrames];
            var detectionsByFrame = new Dictionary<int, IReadOnlyList<FaceTrackDetection>>();

            foreach (var entry in maskProvider.GetFaceMaskEntries())
            {
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
            var tracks = builder.Build(detectionsByFrame, options);
            var removedTrackIds = new HashSet<int>();
            var gapFilledFaces = new List<FaceTrackFilledFace>();
            var lostFillFrameIndices = new List<int>();
            var lostFilledFaces = new List<FaceTrackFilledFace>();
            TrimUnstableLowConfidenceTails(tracks, facesByFrame, confByFrame, options);
            int removedLowerFrameFaces = RemoveLowerFrameLowConfidenceTracks(tracks, facesByFrame, confByFrame, options, removedTrackIds);
            int removedFaces = RemoveShortLowConfidenceTracks(tracks, facesByFrame, confByFrame, options, removedTrackIds);
            int filledFrames = FillShortTrackGaps(tracks, facesByFrame, confByFrame, sizeByFrame, hasStoredMask, options, removedTrackIds, gapFilledFaces);
            int lostFilledFrames = FillConfirmedLostFrames(tracks, facesByFrame, confByFrame, sizeByFrame, hasStoredMask, options, removedTrackIds, lostFillFrameIndices, lostFilledFaces);
            int rewrittenFrames = RewriteMaskProvider(maskProvider, facesByFrame, confByFrame, sizeByFrame, hasStoredMask);

            return new FaceTrackPostProcessResult(
                tracks.Count,
                filledFrames,
                gapFilledFaces.ToArray(),
                lostFilledFrames,
                lostFillFrameIndices.ToArray(),
                lostFilledFaces.ToArray(),
                removedFaces,
                removedLowerFrameFaces,
                rewrittenFrames);
        }

        private static void TrimUnstableLowConfidenceTails(
            IReadOnlyList<FaceTrack> tracks,
            List<Rect>?[] facesByFrame,
            List<float>?[] confByFrame,
            FaceTrackPostProcessOptions options)
        {
            if (options.UnstableTailMaxConfidence <= 0)
                return;

            foreach (var track in tracks)
            {
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
                }

                if (keepCount < track.DetectionCount)
                    track.RemoveDetectionsFrom(keepCount);
            }
        }

        private static int RemoveLowerFrameLowConfidenceTracks(
            IReadOnlyList<FaceTrack> tracks,
            List<Rect>?[] facesByFrame,
            List<float>?[] confByFrame,
            FaceTrackPostProcessOptions options,
            ISet<int> removedTrackIds)
        {
            if (options.LowerFrameTrackMaxConfidence <= 0)
                return 0;

            int removed = 0;
            foreach (var track in tracks)
            {
                if (removedTrackIds.Contains(track.Id) ||
                    track.MaxConfidence > options.LowerFrameTrackMaxConfidence ||
                    !IsLowerFrameMediumAreaTrack(track, options))
                {
                    continue;
                }

                removedTrackIds.Add(track.Id);
                removed += RemoveTrackDetections(track, facesByFrame, confByFrame);
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
            List<Rect>?[] facesByFrame,
            List<float>?[] confByFrame)
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

        private static int RemoveShortLowConfidenceTracks(
            IReadOnlyList<FaceTrack> tracks,
            List<Rect>?[] facesByFrame,
            List<float>?[] confByFrame,
            FaceTrackPostProcessOptions options,
            ISet<int> removedTrackIds)
        {
            int removed = 0;
            foreach (var track in tracks)
            {
                bool removeShortWeak = track.DetectionCount <= options.DropShortTrackMaxDetections &&
                    track.MaxConfidence < options.ShortTrackMaxConfidence;
                bool removeShortSmall = track.DetectionCount <= options.DropShortSmallTrackMaxDetections &&
                    IsSmallTrack(track, options) &&
                    !CouldBePartialFace(track, tracks, options);

                if (!removeShortWeak && !removeShortSmall)
                    continue;

                removedTrackIds.Add(track.Id);
                removed += RemoveTrackDetections(track, facesByFrame, confByFrame);
            }

            return removed;
        }

        private static int RemoveTrackDetections(
            FaceTrack track,
            List<Rect>?[] facesByFrame,
            List<float>?[] confByFrame)
        {
            int removed = 0;
            foreach (var detection in track.Detections)
            {
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
            for (int i = 0; i < track.Detections.Count; i++)
            {
                var detection = track.Detections[i];
                if (TouchesFrameEdge(detection, options))
                    return true;
            }

            return false;
        }

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
            List<Rect>?[] facesByFrame,
            List<float>?[] confByFrame,
            PixelSize[] sizeByFrame,
            bool[] hasStoredMask,
            FaceTrackPostProcessOptions options,
            IReadOnlySet<int> removedTrackIds,
            ICollection<FaceTrackFilledFace> gapFilledFaces)
        {
            int filled = 0;
            foreach (var track in tracks)
            {
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

                    int maxFillGap = options.MaxFillGap;
                    if (IsConfirmedTrack(track, options))
                        maxFillGap = Math.Max(maxFillGap, options.MaxConfirmedTrackHoldFrames);

                    if (gap > maxFillGap)
                        continue;

                    if (!CanBridge(previous.Bounds, next.Bounds, gap, options))
                        continue;

                    for (int frameIndex = previous.FrameIndex + 1; frameIndex < next.FrameIndex; frameIndex++)
                    {
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

                        float confidence = Math.Clamp(
                            Math.Min(previous.Confidence, next.Confidence),
                            options.WeakConfidence,
                            1.0f);
                        facesByFrame[frameIndex]!.Add(interpolated);
                        confByFrame[frameIndex]!.Add(confidence);
                        gapFilledFaces.Add(new FaceTrackFilledFace(frameIndex, interpolated, sizeByFrame[frameIndex], confidence));
                        filled++;
                    }
                }
            }

            return filled;
        }

        private static int FillConfirmedLostFrames(
            IReadOnlyList<FaceTrack> tracks,
            List<Rect>?[] facesByFrame,
            List<float>?[] confByFrame,
            PixelSize[] sizeByFrame,
            bool[] hasStoredMask,
            FaceTrackPostProcessOptions options,
            IReadOnlySet<int> removedTrackIds,
            ICollection<int> lostFillFrameIndices,
            ICollection<FaceTrackFilledFace> lostFilledFaces)
        {
            if (options.MaxLostFillFrames <= 0)
                return 0;

            int filled = 0;
            foreach (var track in tracks)
            {
                if (removedTrackIds.Contains(track.Id) ||
                    !IsConfirmedTrack(track, options))
                {
                    continue;
                }
                if (!options.AllowSmallTrackLostFill && IsSmallTrack(track, options))
                    continue;

                var detections = track.Detections;
                var last = detections[^1];
                int lastFrame = last.FrameIndex;
                if (lastFrame < 0 || lastFrame >= facesByFrame.Length - 1)
                    continue;

                var previous = detections.Count >= 2 ? detections[^2] : last;
                int frameDelta = Math.Max(1, last.FrameIndex - previous.FrameIndex);
                double dx = (last.Bounds.X - previous.Bounds.X) / frameDelta;
                double dy = (last.Bounds.Y - previous.Bounds.Y) / frameDelta;
                double dw = (last.Bounds.Width - previous.Bounds.Width) / frameDelta;
                double dh = (last.Bounds.Height - previous.Bounds.Height) / frameDelta;

                for (int offset = 1; offset <= options.MaxLostFillFrames; offset++)
                {
                    int frameIndex = lastFrame + offset;
                    if (frameIndex >= facesByFrame.Length)
                        break;
                    if (hasStoredMask[frameIndex])
                        continue;

                    var predicted = new Rect(
                        last.Bounds.X + dx * offset,
                        last.Bounds.Y + dy * offset,
                        Math.Max(1.0, last.Bounds.Width + dw * offset),
                        Math.Max(1.0, last.Bounds.Height + dh * offset));

                    if (!IsMostlyInside(predicted, last.Size))
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

                    facesByFrame[frameIndex]!.Add(predicted);
                    confByFrame[frameIndex]!.Add(Math.Clamp(last.Confidence, options.WeakConfidence, 1.0f));
                    lostFillFrameIndices.Add(frameIndex);
                    lostFilledFaces.Add(new FaceTrackFilledFace(frameIndex, predicted, last.Size, last.Confidence));
                    filled++;
                }
            }

            return filled;
        }

        private static bool IsConfirmedTrack(FaceTrack track, FaceTrackPostProcessOptions options)
            => track.DetectionCount >= options.ConfirmedTrackMinDetections &&
                track.MaxConfidence >= options.StrongConfidence;

        private static bool CanBridge(Rect previous, Rect next, int gap, FaceTrackPostProcessOptions options)
        {
            double areaRatio = FaceTrackBuilder.GetAreaRatio(previous, next);
            if (areaRatio > options.MaxAreaChangeRatio || areaRatio < 1.0 / options.MaxAreaChangeRatio)
                return false;

            double iou = FaceTrackBuilder.IoU(previous, next);
            double centerShift = FaceTrackBuilder.GetNormalizedCenterShift(previous, next);
            double allowedShift = options.MaxCenterShiftRatio * Math.Max(1, gap + 1);
            return iou >= options.MinTrackIou || centerShift <= allowedShift;
        }

        private static int RewriteMaskProvider(
            FrameMaskProvider maskProvider,
            List<Rect>?[] facesByFrame,
            List<float>?[] confByFrame,
            PixelSize[] sizeByFrame,
            bool[] hasStoredMask)
        {
            int rewritten = 0;
            for (int frameIndex = 0; frameIndex < facesByFrame.Length; frameIndex++)
            {
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
        int RemovedShortFaces,
        int RemovedLowerFrameFaces,
        int RewrittenFrames)
    {
        public static FaceTrackPostProcessResult Empty { get; } = new(0, 0, Array.Empty<FaceTrackFilledFace>(), 0, Array.Empty<int>(), Array.Empty<FaceTrackFilledFace>(), 0, 0, 0);
    }

    public readonly record struct FaceTrackFilledFace(
        int FrameIndex,
        Rect Bounds,
        PixelSize Size,
        float Confidence);
}
