using Avalonia;
using Avalonia.Media.Imaging;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace FaceShield.Services.Video;

internal static class ManualMaskTrackingService
{
    private const int MaxTrackingWidth = 480;
    private const double SceneCutThreshold = 0.34;
    private const double MinimumTrackingConfidence = 0.58;
    private static readonly double[] CandidateScales = { 0.94, 0.97, 1.0, 1.03, 1.06 };

    private readonly record struct SamplePoint(double U, double V);
    private readonly record struct TrackRect(double X, double Y, double Width, double Height)
    {
        internal double CenterX => X + Width * 0.5;
        internal double CenterY => Y + Height * 0.5;
    }

    internal static ManualMaskTrackResult TrackForward(
        string videoPath,
        int sourceFrameIndex,
        int endExclusive,
        WriteableBitmap sourceMask,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(videoPath))
            throw new ArgumentException("Video path is required.", nameof(videoPath));
        if (sourceMask == null)
            throw new ArgumentNullException(nameof(sourceMask));
        if (sourceFrameIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(sourceFrameIndex));

        Rect sourceBounds = FindMaskBounds(sourceMask);
        if (sourceBounds.Width <= 0 || sourceBounds.Height <= 0)
            throw new InvalidOperationException("현재 마스크가 비어 있어 추적할 영역이 없습니다.");

        using var extractor = new FfFrameExtractor(
            videoPath,
            enableHardware: false,
            cancellationToken: cancellationToken);
        PixelSize frameSize = extractor.FrameSize;
        if (frameSize.Width <= 0 || frameSize.Height <= 0)
            throw new InvalidOperationException("영상 프레임 크기를 확인할 수 없습니다.");
        if (sourceMask.PixelSize.Width != frameSize.Width ||
            sourceMask.PixelSize.Height != frameSize.Height)
        {
            throw new InvalidOperationException(
                "현재 마스크 크기와 원본 영상 프레임 크기가 일치하지 않습니다.");
        }

        int sampleWidth = Math.Min(MaxTrackingWidth, frameSize.Width);
        int sampleHeight = Math.Max(
            1,
            (int)Math.Round(frameSize.Height * (sampleWidth / (double)frameSize.Width)));
        double sampleScaleX = sampleWidth / (double)frameSize.Width;
        double sampleScaleY = sampleHeight / (double)frameSize.Height;
        int bufferSize = checked(sampleWidth * sampleHeight * 4);

        var points = BuildSamplePoints(sourceMask, sourceBounds);
        if (points.Count < 8)
            throw new InvalidOperationException("추적에 사용할 마스크 영역이 너무 작습니다.");

        var pool = ArrayPool<byte>.Shared;
        byte[] previous = pool.Rent(bufferSize);
        byte[] current = pool.Rent(bufferSize);
        try
        {
            extractor.StartSequentialRead(sourceFrameIndex, cancellationToken);
            int previousStride = 0;
            int decodedIndex = sourceFrameIndex - 1;
            bool sourceLoaded = false;
            while (decodedIndex < sourceFrameIndex)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!extractor.TryGetNextFrameRawToBuffer(
                        cancellationToken,
                        sampleWidth,
                        sampleHeight,
                        useBilinear: true,
                        previous,
                        out decodedIndex,
                        out previousStride))
                {
                    break;
                }

                if (decodedIndex == sourceFrameIndex)
                    sourceLoaded = true;
            }

            if (!sourceLoaded)
                throw new InvalidOperationException("추적 시작 프레임을 디코딩하지 못했습니다.");

            var sourceRect = new TrackRect(
                sourceBounds.X * sampleScaleX,
                sourceBounds.Y * sampleScaleY,
                Math.Max(2.0, sourceBounds.Width * sampleScaleX),
                Math.Max(2.0, sourceBounds.Height * sampleScaleY));
            TrackRect previousRect = sourceRect;
            double[] template = ReadTemplate(
                previous,
                previousStride,
                sampleWidth,
                sampleHeight,
                previousRect,
                points);

            int safeEndExclusive = endExclusive <= sourceFrameIndex
                ? int.MaxValue
                : endExclusive;
            var segment = new ManualMaskTrackSegment
            {
                SourceKeyframe = sourceFrameIndex,
                SourceBoundsX = sourceBounds.X,
                SourceBoundsY = sourceBounds.Y,
                SourceBoundsWidth = sourceBounds.Width,
                SourceBoundsHeight = sourceBounds.Height,
                EndExclusive = safeEndExclusive
            };

            int processed = 0;
            int lastDecoded = sourceFrameIndex;
            bool reachedBoundary = false;
            while (lastDecoded + 1 < safeEndExclusive)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!extractor.TryGetNextFrameRawToBuffer(
                        cancellationToken,
                        sampleWidth,
                        sampleHeight,
                        useBilinear: true,
                        current,
                        out int currentIndex,
                        out int currentStride))
                {
                    segment.EndExclusive = lastDecoded + 1;
                    reachedBoundary = true;
                    break;
                }

                if (currentIndex <= lastDecoded)
                {
                    Swap(ref previous, ref current);
                    previousStride = currentStride;
                    continue;
                }
                lastDecoded = currentIndex;
                if (currentIndex >= safeEndExclusive)
                {
                    segment.EndExclusive = safeEndExclusive;
                    reachedBoundary = true;
                    break;
                }

                double sceneDifference = ComputeSceneDifference(
                    previous,
                    previousStride,
                    current,
                    currentStride,
                    sampleWidth,
                    sampleHeight);
                if (sceneDifference >= SceneCutThreshold)
                {
                    segment.EndExclusive = currentIndex;
                    segment.StoppedByFailure = true;
                    segment.StopFrame = currentIndex;
                    segment.StopReason = $"장면 전환 감지(diff={sceneDifference:0.000})";
                    break;
                }

                if (!TryFindBestTransform(
                        current,
                        currentStride,
                        sampleWidth,
                        sampleHeight,
                        previousRect,
                        points,
                        template,
                        out TrackRect bestRect,
                        out double confidence))
                {
                    segment.EndExclusive = currentIndex;
                    segment.StoppedByFailure = true;
                    segment.StopFrame = currentIndex;
                    segment.StopReason = "추적 후보를 찾지 못했습니다.";
                    break;
                }

                if (confidence < MinimumTrackingConfidence)
                {
                    segment.EndExclusive = currentIndex;
                    segment.StoppedByFailure = true;
                    segment.StopFrame = currentIndex;
                    segment.StopReason = $"추적 신뢰도 부족({confidence:0.000})";
                    break;
                }

                double scaleX = bestRect.Width / sourceRect.Width;
                double scaleY = bestRect.Height / sourceRect.Height;
                double scale = Math.Max(0.25, Math.Min(4.0, (scaleX + scaleY) * 0.5));
                double destinationX = bestRect.X / sampleScaleX;
                double destinationY = bestRect.Y / sampleScaleY;
                segment.Samples.Add(new ManualMaskTrackSample
                {
                    FrameIndex = currentIndex,
                    OffsetX = destinationX - sourceBounds.X,
                    OffsetY = destinationY - sourceBounds.Y,
                    Scale = scale,
                    Confidence = confidence
                });

                UpdateTemplate(
                    current,
                    currentStride,
                    sampleWidth,
                    sampleHeight,
                    bestRect,
                    points,
                    template);
                previousRect = bestRect;
                processed++;
                progress?.Report(processed);

                Swap(ref previous, ref current);
                previousStride = currentStride;
            }

            if (!segment.StoppedByFailure)
            {
                if (safeEndExclusive != int.MaxValue)
                {
                    segment.EndExclusive = safeEndExclusive;
                    reachedBoundary = true;
                }
                else if (segment.EndExclusive == int.MaxValue)
                {
                    segment.EndExclusive = lastDecoded + 1;
                }
            }

            return new ManualMaskTrackResult(segment, processed, reachedBoundary);
        }
        finally
        {
            pool.Return(previous);
            pool.Return(current);
        }
    }

    private static Rect FindMaskBounds(WriteableBitmap mask)
    {
        using var fb = mask.Lock();
        int minX = fb.Size.Width;
        int minY = fb.Size.Height;
        int maxX = -1;
        int maxY = -1;
        unsafe
        {
            byte* basePtr = (byte*)fb.Address;
            for (int y = 0; y < fb.Size.Height; y++)
            {
                byte* row = basePtr + y * fb.RowBytes;
                for (int x = 0; x < fb.Size.Width; x++)
                {
                    if (row[x * 4 + 3] <= 8)
                        continue;
                    minX = Math.Min(minX, x);
                    minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x);
                    maxY = Math.Max(maxY, y);
                }
            }
        }

        if (maxX < minX || maxY < minY)
            return default;
        return new Rect(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    private static List<SamplePoint> BuildSamplePoints(
        WriteableBitmap mask,
        Rect bounds)
    {
        var points = new List<SamplePoint>(256);
        int left = (int)Math.Floor(bounds.X);
        int top = (int)Math.Floor(bounds.Y);
        int width = Math.Max(1, (int)Math.Ceiling(bounds.Width));
        int height = Math.Max(1, (int)Math.Ceiling(bounds.Height));
        int step = Math.Max(1, Math.Min(width, height) / 18);

        using var fb = mask.Lock();
        unsafe
        {
            byte* basePtr = (byte*)fb.Address;
            for (int y = top; y < top + height && points.Count < 256; y += step)
            {
                if (y < 0 || y >= fb.Size.Height)
                    continue;
                byte* row = basePtr + y * fb.RowBytes;
                for (int x = left; x < left + width && points.Count < 256; x += step)
                {
                    if (x < 0 || x >= fb.Size.Width || row[x * 4 + 3] <= 32)
                        continue;
                    double u = width <= 1 ? 0.5 : (x - left) / (double)(width - 1);
                    double v = height <= 1 ? 0.5 : (y - top) / (double)(height - 1);
                    points.Add(new SamplePoint(u, v));
                }
            }
        }

        return points;
    }

    private static double[] ReadTemplate(
        byte[] frame,
        int stride,
        int width,
        int height,
        TrackRect rect,
        IReadOnlyList<SamplePoint> points)
    {
        var result = new double[points.Count];
        for (int i = 0; i < points.Count; i++)
        {
            double x = rect.X + points[i].U * Math.Max(1.0, rect.Width - 1.0);
            double y = rect.Y + points[i].V * Math.Max(1.0, rect.Height - 1.0);
            result[i] = ReadLuma(frame, stride, width, height, x, y);
        }
        return result;
    }

    private static bool TryFindBestTransform(
        byte[] current,
        int stride,
        int width,
        int height,
        TrackRect previousRect,
        IReadOnlyList<SamplePoint> points,
        IReadOnlyList<double> template,
        out TrackRect bestRect,
        out double confidence)
    {
        bestRect = previousRect;
        confidence = double.NegativeInfinity;
        double radiusX = Math.Clamp(previousRect.Width * 0.20, 4.0, 36.0);
        double radiusY = Math.Clamp(previousRect.Height * 0.20, 4.0, 36.0);
        int stepX = Math.Max(1, (int)Math.Round(radiusX / 6.0));
        int stepY = Math.Max(1, (int)Math.Round(radiusY / 6.0));

        foreach (double scaleDelta in CandidateScales)
        {
            double candidateWidth = Math.Max(2.0, previousRect.Width * scaleDelta);
            double candidateHeight = Math.Max(2.0, previousRect.Height * scaleDelta);
            for (int dy = -(int)radiusY; dy <= radiusY; dy += stepY)
            {
                for (int dx = -(int)radiusX; dx <= radiusX; dx += stepX)
                {
                    double centerX = previousRect.CenterX + dx;
                    double centerY = previousRect.CenterY + dy;
                    var candidate = new TrackRect(
                        centerX - candidateWidth * 0.5,
                        centerY - candidateHeight * 0.5,
                        candidateWidth,
                        candidateHeight);
                    if (candidate.X < 0 || candidate.Y < 0 ||
                        candidate.X + candidate.Width >= width ||
                        candidate.Y + candidate.Height >= height)
                    {
                        continue;
                    }

                    double error = 0.0;
                    for (int i = 0; i < points.Count; i++)
                    {
                        double x = candidate.X + points[i].U * Math.Max(1.0, candidate.Width - 1.0);
                        double y = candidate.Y + points[i].V * Math.Max(1.0, candidate.Height - 1.0);
                        double luma = ReadLuma(current, stride, width, height, x, y);
                        error += Math.Abs(luma - template[i]);
                    }

                    double meanError = error / Math.Max(1, points.Count) / 255.0;
                    double motionPenalty =
                        (Math.Abs(dx) / Math.Max(1.0, radiusX) +
                         Math.Abs(dy) / Math.Max(1.0, radiusY)) * 0.012;
                    double scalePenalty = Math.Abs(scaleDelta - 1.0) * 0.12;
                    double score = 1.0 - meanError - motionPenalty - scalePenalty;
                    if (score <= confidence)
                        continue;
                    confidence = score;
                    bestRect = candidate;
                }
            }
        }

        return double.IsFinite(confidence);
    }

    private static void UpdateTemplate(
        byte[] frame,
        int stride,
        int width,
        int height,
        TrackRect rect,
        IReadOnlyList<SamplePoint> points,
        double[] template)
    {
        const double oldWeight = 0.35;
        const double newWeight = 1.0 - oldWeight;
        for (int i = 0; i < points.Count; i++)
        {
            double x = rect.X + points[i].U * Math.Max(1.0, rect.Width - 1.0);
            double y = rect.Y + points[i].V * Math.Max(1.0, rect.Height - 1.0);
            double luma = ReadLuma(frame, stride, width, height, x, y);
            template[i] = template[i] * oldWeight + luma * newWeight;
        }
    }

    private static double ComputeSceneDifference(
        byte[] previous,
        int previousStride,
        byte[] current,
        int currentStride,
        int width,
        int height)
    {
        int step = Math.Max(4, Math.Min(width, height) / 32);
        double total = 0.0;
        int count = 0;
        for (int y = step / 2; y < height; y += step)
        {
            for (int x = step / 2; x < width; x += step)
            {
                double a = ReadLuma(previous, previousStride, width, height, x, y);
                double b = ReadLuma(current, currentStride, width, height, x, y);
                total += Math.Abs(a - b) / 255.0;
                count++;
            }
        }
        return count == 0 ? 0.0 : total / count;
    }

    private static double ReadLuma(
        byte[] frame,
        int stride,
        int width,
        int height,
        double x,
        double y)
    {
        int ix = Math.Clamp((int)Math.Round(x), 0, width - 1);
        int iy = Math.Clamp((int)Math.Round(y), 0, height - 1);
        int offset = iy * stride + ix * 4;
        if (offset < 0 || offset + 2 >= frame.Length)
            return 0.0;
        double b = frame[offset];
        double g = frame[offset + 1];
        double r = frame[offset + 2];
        return r * 0.299 + g * 0.587 + b * 0.114;
    }

    private static void Swap(ref byte[] left, ref byte[] right)
    {
        (left, right) = (right, left);
    }
}
