using Avalonia;
using FaceShield.Services.FaceDetection;
using FaceShield.Services.Video;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace FaceShield.Services.Analysis
{
    public sealed class FaceTrackRoiRefiner
    {
        private const int MaxSequentialFrameGap = 12;

        public unsafe FaceTrackRoiRefineResult Apply(
            FrameMaskProvider maskProvider,
            string videoPath,
            IBgraFaceDetector detector,
            IReadOnlyList<FaceTrackFilledFace> candidates,
            DownscaleQuality quality,
            int maxCandidates = 32,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (maskProvider == null)
                throw new ArgumentNullException(nameof(maskProvider));
            if (string.IsNullOrWhiteSpace(videoPath) || detector == null || candidates.Count == 0)
                return FaceTrackRoiRefineResult.Empty;

            var sw = Stopwatch.StartNew();
            int attempts = 0;
            int hits = 0;
            int seekCount = 0;
            int decodedFrames = 0;
            using var extractor = new FfFrameExtractor(
                videoPath,
                enableHardware: false,
                cancellationToken: cancellationToken);
            var frameSize = extractor.FrameSize;
            int width = frameSize.Width;
            int height = frameSize.Height;
            if (width <= 0 || height <= 0)
                return FaceTrackRoiRefineResult.Empty;

            var pool = ArrayPool<byte>.Shared;
            byte[] buffer = pool.Rent(width * height * 4);
            try
            {
                int currentFrameIndex = -1;
                bool sequentialActive = false;
                var candidateGroups = candidates
                    .Where(x => x.FrameIndex >= 0)
                    .OrderBy(x => x.FrameIndex)
                    .Take(Math.Max(1, maxCandidates))
                    .GroupBy(x => x.FrameIndex)
                    .OrderBy(x => x.Key);

                foreach (var group in candidateGroups)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    int targetFrameIndex = group.Key;
                    if (!sequentialActive ||
                        targetFrameIndex < currentFrameIndex ||
                        targetFrameIndex - currentFrameIndex > MaxSequentialFrameGap)
                    {
                        extractor.StartSequentialRead(
                            targetFrameIndex,
                            cancellationToken);
                        seekCount++;
                        currentFrameIndex = targetFrameIndex - 1;
                        sequentialActive = true;
                    }

                    int decodedFrameIndex = currentFrameIndex;
                    int stride = 0;
                    bool found = false;
                    while (decodedFrameIndex < targetFrameIndex)
                    {
                        if (!extractor.TryGetNextFrameRawToBuffer(
                                cancellationToken,
                                width,
                                height,
                                quality == DownscaleQuality.BalancedBilinear,
                                buffer,
                                out decodedFrameIndex,
                                out stride))
                        {
                            sequentialActive = false;
                            break;
                        }

                        currentFrameIndex = decodedFrameIndex;
                        decodedFrames++;
                        if (decodedFrameIndex == targetFrameIndex)
                            found = true;
                    }

                    if (!found)
                        continue;

                    foreach (var candidate in group)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (TryRefineCandidate(maskProvider, detector, candidate, frameSize, buffer, stride, quality))
                            hits++;
                        attempts++;
                    }
                }
            }
            finally
            {
                pool.Return(buffer);
            }

            sw.Stop();
            return new FaceTrackRoiRefineResult(attempts, hits, seekCount, decodedFrames, sw.ElapsedMilliseconds);
        }

        private static unsafe bool TryRefineCandidate(
            FrameMaskProvider maskProvider,
            IBgraFaceDetector detector,
            FaceTrackFilledFace candidate,
            PixelSize frameSize,
            byte[] buffer,
            int stride,
            DownscaleQuality quality)
        {
            var roi = BuildRefineRoi(candidate.Bounds, frameSize);
            int rx = (int)Math.Floor(roi.X);
            int ry = (int)Math.Floor(roi.Y);
            int rw = (int)Math.Ceiling(roi.Width);
            int rh = (int)Math.Ceiling(roi.Height);
            if (rw < 32 || rh < 32)
                return false;

            fixed (byte* basePtr = buffer)
            {
                IntPtr roiPtr = (IntPtr)(basePtr + ry * stride + rx * 4);
                var roiFaces = detector.DetectFacesBgra(
                    roiPtr,
                    stride,
                    rw,
                    rh,
                    1.0,
                    quality);
                if (roiFaces.Count == 0)
                    return false;

                var refined = ChooseRoiRefinement(candidate.Bounds, roiFaces, rx, ry);
                if (refined == null)
                    return false;

                if (!maskProvider.TryGetFaceMaskData(candidate.FrameIndex, out var data))
                    return false;

                var faces = data.Faces.ToList();
                var confs = data.Confidences.ToList();
                int replaceIndex = FindSimilarFaceIndex(faces, candidate.Bounds);
                if (replaceIndex < 0)
                    return false;

                faces[replaceIndex] = refined.Bounds;
                while (confs.Count < faces.Count)
                    confs.Add(data.MinConfidence ?? 0.50f);
                confs[replaceIndex] = refined.Confidence;
                maskProvider.SetFaceRects(
                    candidate.FrameIndex,
                    faces,
                    data.Size,
                    confs.Count == 0 ? null : confs.Min(),
                    confs);
                return true;
            }
        }

        private static Rect BuildRefineRoi(Rect bounds, PixelSize size)
        {
            double padX = Math.Max(bounds.Width * 0.90, 80);
            double padY = Math.Max(bounds.Height * 0.90, 80);
            double x = Math.Max(0, bounds.X - padX);
            double y = Math.Max(0, bounds.Y - padY);
            double right = Math.Min(size.Width, bounds.Right + padX);
            double bottom = Math.Min(size.Height, bounds.Bottom + padY);
            return new Rect(x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
        }

        private static FaceDetectionResult? ChooseRoiRefinement(
            Rect expected,
            IReadOnlyList<FaceDetectionResult> roiFaces,
            double offsetX,
            double offsetY)
        {
            FaceDetectionResult? best = null;
            double bestScore = double.NegativeInfinity;
            double expectedMaxDim = Math.Max(1.0, Math.Max(expected.Width, expected.Height));

            foreach (var face in roiFaces)
            {
                var shifted = new Rect(
                    face.Bounds.X + offsetX,
                    face.Bounds.Y + offsetY,
                    face.Bounds.Width,
                    face.Bounds.Height);
                double iou = IoU(expected, shifted);
                double centerShift = GetCenterShift(expected, shifted) / expectedMaxDim;
                if (iou < 0.05 && centerShift > 1.6)
                    continue;

                double score = iou * 2.0 + face.Confidence - centerShift * 0.15;
                if (score <= bestScore)
                    continue;

                bestScore = score;
                best = new FaceDetectionResult
                {
                    Bounds = shifted,
                    Confidence = face.Confidence
                };
            }

            return best;
        }

        private static int FindSimilarFaceIndex(IReadOnlyList<Rect> faces, Rect expected)
        {
            int bestIndex = -1;
            double bestIou = 0.0;
            for (int i = 0; i < faces.Count; i++)
            {
                double iou = IoU(faces[i], expected);
                if (iou > bestIou)
                {
                    bestIou = iou;
                    bestIndex = i;
                }
            }

            return bestIou >= 0.50 ? bestIndex : -1;
        }

        private static double GetCenterShift(Rect a, Rect b)
        {
            double ax = a.X + a.Width * 0.5;
            double ay = a.Y + a.Height * 0.5;
            double bx = b.X + b.Width * 0.5;
            double by = b.Y + b.Height * 0.5;
            return Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));
        }

        private static double IoU(Rect a, Rect b)
        {
            double x0 = Math.Max(a.X, b.X);
            double y0 = Math.Max(a.Y, b.Y);
            double x1 = Math.Min(a.Right, b.Right);
            double y1 = Math.Min(a.Bottom, b.Bottom);
            double intersection = Math.Max(0.0, x1 - x0) * Math.Max(0.0, y1 - y0);
            if (intersection <= 0.0)
                return 0.0;

            double areaA = Math.Max(0.0, a.Width) * Math.Max(0.0, a.Height);
            double areaB = Math.Max(0.0, b.Width) * Math.Max(0.0, b.Height);
            double union = areaA + areaB - intersection;
            return union <= 0.0 ? 0.0 : intersection / union;
        }
    }

    public readonly record struct FaceTrackRoiRefineResult(
        int Attempts,
        int Hits,
        int SeekCount,
        int DecodedFrames,
        long ElapsedMs)
    {
        public static FaceTrackRoiRefineResult Empty { get; } = new(0, 0, 0, 0, 0);
    }
}
