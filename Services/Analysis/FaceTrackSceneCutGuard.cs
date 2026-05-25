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

        public IReadOnlyList<FaceTrackFilledFace> BuildWeakTrackTransitionCandidates(
            FrameMaskProvider maskProvider,
            FaceTrackPostProcessOptions options,
            float maxTargetConfidence,
            int maxTransitionGap,
            float minConfidenceDrop = 0.10f,
            int maxPostCutCarryFrames = 3)
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

        public FaceTrackSceneCutGuardResult Apply(
            FrameMaskProvider maskProvider,
            IReadOnlyList<FaceTrackFilledFace> candidates,
            Func<int, int, double> frameDifferenceProvider,
            double differenceThreshold = DefaultDifferenceThreshold)
        {
            if (maskProvider == null)
                throw new ArgumentNullException(nameof(maskProvider));
            if (frameDifferenceProvider == null)
                throw new ArgumentNullException(nameof(frameDifferenceProvider));
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
                maxDifference = Math.Max(maxDifference, difference);

                if (difference < differenceThreshold)
                    continue;

                cutFramePairs.Add(cutFramePair);
                if (RemoveFaceCandidate(maskProvider, candidate.FrameIndex, candidate.Bounds))
                {
                    removedCandidates++;
                    removedFrameIndices.Add(candidate.FrameIndex);
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
                null);
        }

        public FaceTrackSceneCutGuardResult Apply(
            FrameMaskProvider maskProvider,
            string videoPath,
            IReadOnlyList<FaceTrackFilledFace> candidates,
            double differenceThreshold = DefaultDifferenceThreshold)
        {
            if (maskProvider == null)
                throw new ArgumentNullException(nameof(maskProvider));
            if (string.IsNullOrWhiteSpace(videoPath) || candidates.Count == 0)
                return FaceTrackSceneCutGuardResult.Empty;

            var sw = Stopwatch.StartNew();
            int checkedCandidates = 0;
            int removedCandidates = 0;
            double maxDifference = 0.0;
            var checkedFramePairs = new List<string>();
            var removedFrameIndices = new List<int>();
            var cutFramePairs = new List<string>();
            var differenceByPair = new Dictionary<(int Source, int Target), double>();

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
                    foreach (var candidate in candidates)
                    {
                        int sourceFrame = Math.Min(candidate.SourceFrameIndex, candidate.FrameIndex);
                        int targetFrame = Math.Max(candidate.SourceFrameIndex, candidate.FrameIndex);
                        if (sourceFrame < 0 || targetFrame <= sourceFrame)
                            continue;

                        checkedCandidates++;
                        checkedFramePairs.Add(FormatFramePair(sourceFrame, targetFrame));
                        if (!TryComputeMaxSequentialDifference(
                                    extractor,
                                    sourceFrame,
                                    targetFrame,
                                    sampleWidth,
                                    sampleHeight,
                                    sourceBuffer,
                                    targetBuffer,
                                    differenceByPair,
                                    out double difference,
                                    out string cutFramePair))
                        {
                            continue;
                        }
                        maxDifference = Math.Max(maxDifference, difference);

                        if (difference < differenceThreshold)
                            continue;

                        cutFramePairs.Add(cutFramePair);
                        if (RemoveFaceCandidate(maskProvider, candidate.FrameIndex, candidate.Bounds))
                        {
                            removedCandidates++;
                            removedFrameIndices.Add(candidate.FrameIndex);
                        }
                    }
                }
                finally
                {
                    pool.Return(sourceBuffer);
                    pool.Return(targetBuffer);
                }
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
                null);
        }

        private static string FormatFramePair(int sourceFrame, int targetFrame)
            => string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{sourceFrame}->{targetFrame}");

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
                if (!differenceByPair.TryGetValue(pair, out double difference))
                {
                    difference = frameDifferenceProvider(pair.Item1, pair.Item2);
                    differenceByPair[pair] = difference;
                }

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

        private static bool RemoveFaceCandidate(FrameMaskProvider maskProvider, int frameIndex, Rect candidate)
        {
            if (!maskProvider.TryGetFaceMaskData(frameIndex, out var data) || data.Faces.Count == 0)
                return false;

            int removeIndex = -1;
            double bestIou = 0.0;
            for (int i = 0; i < data.Faces.Count; i++)
            {
                double iou = IoU(data.Faces[i], candidate);
                if (iou > bestIou)
                {
                    bestIou = iou;
                    removeIndex = i;
                }
            }

            if (removeIndex < 0 || bestIou < 0.80)
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
        string? Error)
    {
        public static FaceTrackSceneCutGuardResult Empty { get; } = new(0, 0, Array.Empty<int>(), Array.Empty<string>(), 0, 0, Array.Empty<string>(), 0, null);
    }
}
