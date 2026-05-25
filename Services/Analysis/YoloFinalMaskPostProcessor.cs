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

            var frames = entries.Select(static x => x.Key).ToArray();
            var removedFrames = new List<int>();
            int removed = 0;
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
                        !TouchesFrameEdge(face, data.Size, options.EdgeMarginRatio) &&
                        !HasMatchingTemporalNeighbor(entries, i, face, options))
                    {
                        removed++;
                        continue;
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
                : new YoloFinalMaskCleanupResult(removed, removedFrames.ToArray());
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
                if (FaceTrackBuilder.IoU(candidate, face) >= options.NeighborMinIou)
                    return true;

                double areaRatio = FaceTrackBuilder.GetAreaRatio(candidate, face);
                if (areaRatio > options.NeighborMaxAreaChangeRatio ||
                    areaRatio < 1.0 / options.NeighborMaxAreaChangeRatio)
                {
                    continue;
                }

                if (FaceTrackBuilder.GetNormalizedCenterShift(candidate, face) <= options.NeighborMaxCenterShiftRatio)
                    return true;
            }

            return false;
        }

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
    }

    public sealed record YoloFinalMaskCleanupOptions
    {
        public int NeighborWindowFrames { get; init; } = 1;
        public float WeakConfidenceMax { get; init; } = 0.50f;
        public double EdgeMarginRatio { get; init; } = 0.02;
        public double NeighborMinIou { get; init; } = 0.15;
        public double NeighborMaxCenterShiftRatio { get; init; } = 0.65;
        public double NeighborMaxAreaChangeRatio { get; init; } = 3.0;
    }

    public readonly record struct YoloFinalMaskCleanupResult(
        int RemovedWeakIsolatedFaces,
        IReadOnlyList<int> RemovedFrameIndices)
    {
        public static YoloFinalMaskCleanupResult Empty { get; } = new(0, Array.Empty<int>());
    }
}
