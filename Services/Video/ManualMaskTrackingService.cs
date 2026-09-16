using Avalonia;
using Avalonia.Media.Imaging;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace FaceShield.Services.Video;

internal static class ManualMaskTrackingService
{
    private const int MaxTrackingWidth = 480;
    private const int MaxComponents = 32;
    private const int MaxSamplesPerComponent = 96;
    private const double SceneCutThreshold = 0.34;
    private const double MinimumTrackingConfidence = 0.58;
    private static readonly double[] CandidateScales = { 0.94, 0.97, 1.0, 1.03, 1.06 };

    private readonly record struct SamplePoint(double U, double V);
    private readonly record struct TrackRect(double X, double Y, double Width, double Height)
    {
        internal double CenterX => X + Width * 0.5;
        internal double CenterY => Y + Height * 0.5;
    }

    private sealed class ComponentState
    {
        internal required int ComponentIndex { get; init; }
        internal required Rect SourceBounds { get; init; }
        internal required TrackRect SourceRect { get; init; }
        internal required List<SamplePoint> Points { get; init; }
        internal required double[] Template { get; init; }
        internal TrackRect PreviousRect { get; set; }
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

        cancellationToken.ThrowIfCancellationRequested();
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

        int[] labels = BuildComponentLabels(
            sourceMask,
            sampleWidth,
            sampleHeight,
            cancellationToken,
            out List<(int MinX, int MinY, int MaxX, int MaxY, int PixelCount)> componentBoxes);
        if (componentBoxes.Count == 0)
            throw new InvalidOperationException("현재 마스크가 비어 있어 추적할 영역이 없습니다.");
        if (componentBoxes.Count > MaxComponents)
        {
            throw new InvalidOperationException(
                $"분리된 마스크 영역이 너무 많습니다({componentBoxes.Count}개). " +
                $"한 번에 최대 {MaxComponents}개 영역을 추적할 수 있습니다.");
        }

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

            cancellationToken.ThrowIfCancellationRequested();
            List<ComponentState> states = BuildComponentStates(
                labels,
                componentBoxes,
                previous,
                previousStride,
                sampleWidth,
                sampleHeight,
                sampleScaleX,
                sampleScaleY,
                cancellationToken);
            if (states.Count == 0)
                throw new InvalidOperationException("추적 가능한 마스크 영역을 만들지 못했습니다.");

            int safeEndExclusive = endExclusive <= sourceFrameIndex
                ? int.MaxValue
                : endExclusive;
            var segment = new ManualMaskTrackSegment
            {
                SourceKeyframe = sourceFrameIndex,
                EndExclusive = safeEndExclusive,
                Components = states
                    .Select(static state => new ManualMaskTrackComponent
                    {
                        ComponentIndex = state.ComponentIndex,
                        SourceBoundsX = state.SourceBounds.X,
                        SourceBoundsY = state.SourceBounds.Y,
                        SourceBoundsWidth = state.SourceBounds.Width,
                        SourceBoundsHeight = state.SourceBounds.Height
                    })
                    .ToList()
            };

            int processed = 0;
            int lastDecoded = sourceFrameIndex;
            bool reachedBoundary = false;
            var candidateRects = new TrackRect[states.Count];
            var candidateConfidences = new double[states.Count];

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
                    continue;

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
                    sampleHeight,
                    cancellationToken);
                if (sceneDifference >= SceneCutThreshold)
                {
                    StopSegment(
                        segment,
                        currentIndex,
                        $"장면 전환 감지(diff={sceneDifference:0.000})");
                    break;
                }

                bool allTracked = true;
                string? failureReason = null;
                for (int i = 0; i < states.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ComponentState state = states[i];
                    if (!TryFindBestTransform(
                            current,
                            currentStride,
                            sampleWidth,
                            sampleHeight,
                            state.PreviousRect,
                            state.Points,
                            state.Template,
                            cancellationToken,
                            out TrackRect bestRect,
                            out double confidence))
                    {
                        allTracked = false;
                        failureReason = $"영역 {state.ComponentIndex + 1}의 추적 후보를 찾지 못했습니다.";
                        break;
                    }

                    if (confidence < MinimumTrackingConfidence)
                    {
                        allTracked = false;
                        failureReason =
                            $"영역 {state.ComponentIndex + 1} 추적 신뢰도 부족({confidence:0.000})";
                        break;
                    }

                    candidateRects[i] = bestRect;
                    candidateConfidences[i] = confidence;
                }

                if (!allTracked)
                {
                    StopSegment(
                        segment,
                        currentIndex,
                        failureReason ?? "추적 신뢰도 부족");
                    break;
                }

                for (int i = 0; i < states.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ComponentState state = states[i];
                    TrackRect bestRect = candidateRects[i];
                    double confidence = candidateConfidences[i];
                    double scaleX = bestRect.Width / state.SourceRect.Width;
                    double scaleY = bestRect.Height / state.SourceRect.Height;
                    double scale = Math.Clamp((scaleX + scaleY) * 0.5, 0.25, 4.0);
                    double destinationX = bestRect.X / sampleScaleX;
                    double destinationY = bestRect.Y / sampleScaleY;

                    ManualMaskTrackComponent outputComponent = segment.Components[i];
                    outputComponent.Samples.Add(new ManualMaskTrackSample
                    {
                        FrameIndex = currentIndex,
                        OffsetX = destinationX - outputComponent.SourceBoundsX,
                        OffsetY = destinationY - outputComponent.SourceBoundsY,
                        Scale = scale,
                        Confidence = confidence
                    });

                    UpdateTemplate(
                        current,
                        currentStride,
                        sampleWidth,
                        sampleHeight,
                        bestRect,
                        state.Points,
                        state.Template,
                        cancellationToken);
                    state.PreviousRect = bestRect;
                }

                processed++;
                progress?.Report(processed);
                Swap(ref previous, ref current);
                previousStride = currentStride;
            }

            cancellationToken.ThrowIfCancellationRequested();
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

    private static void StopSegment(
        ManualMaskTrackSegment segment,
        int frameIndex,
        string reason)
    {
        segment.EndExclusive = frameIndex;
        segment.StoppedByFailure = true;
        segment.StopFrame = frameIndex;
        segment.StopReason = reason;
    }

    private static int[] BuildComponentLabels(
        WriteableBitmap mask,
        int sampleWidth,
        int sampleHeight,
        CancellationToken cancellationToken,
        out List<(int MinX, int MinY, int MaxX, int MaxY, int PixelCount)> boxes)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int count = checked(sampleWidth * sampleHeight);
        var active = new bool[count];
        var labels = new int[count];
        Array.Fill(labels, -1);

        using (var fb = mask.Lock())
        {
            unsafe
            {
                byte* basePtr = (byte*)fb.Address;
                for (int y = 0; y < sampleHeight; y++)
                {
                    if ((y & 15) == 0)
                        cancellationToken.ThrowIfCancellationRequested();

                    int sourceY = Math.Clamp(
                        (int)Math.Round((y + 0.5) * fb.Size.Height / sampleHeight - 0.5),
                        0,
                        fb.Size.Height - 1);
                    byte* row = basePtr + sourceY * fb.RowBytes;
                    for (int x = 0; x < sampleWidth; x++)
                    {
                        int sourceX = Math.Clamp(
                            (int)Math.Round((x + 0.5) * fb.Size.Width / sampleWidth - 0.5),
                            0,
                            fb.Size.Width - 1);
                        active[y * sampleWidth + x] = row[sourceX * 4 + 3] > 24;
                    }
                }
            }
        }

        boxes = new List<(int MinX, int MinY, int MaxX, int MaxY, int PixelCount)>();
        int[] queue = ArrayPool<int>.Shared.Rent(count);
        try
        {
            for (int y = 0; y < sampleHeight; y++)
            {
                if ((y & 15) == 0)
                    cancellationToken.ThrowIfCancellationRequested();

                for (int x = 0; x < sampleWidth; x++)
                {
                    int seed = y * sampleWidth + x;
                    if (!active[seed] || labels[seed] >= 0)
                        continue;

                    int label = boxes.Count;
                    int head = 0;
                    int tail = 0;
                    queue[tail++] = seed;
                    labels[seed] = label;
                    int minX = x;
                    int minY = y;
                    int maxX = x;
                    int maxY = y;
                    int pixelCount = 0;

                    while (head < tail)
                    {
                        if ((head & 1023) == 0)
                            cancellationToken.ThrowIfCancellationRequested();

                        int index = queue[head++];
                        int cy = index / sampleWidth;
                        int cx = index - cy * sampleWidth;
                        pixelCount++;
                        minX = Math.Min(minX, cx);
                        minY = Math.Min(minY, cy);
                        maxX = Math.Max(maxX, cx);
                        maxY = Math.Max(maxY, cy);

                        TryVisit(cx - 1, cy);
                        TryVisit(cx + 1, cy);
                        TryVisit(cx, cy - 1);
                        TryVisit(cx, cy + 1);
                    }

                    boxes.Add((minX, minY, maxX, maxY, pixelCount));

                    void TryVisit(int nx, int ny)
                    {
                        if (nx < 0 || ny < 0 || nx >= sampleWidth || ny >= sampleHeight)
                            return;
                        int next = ny * sampleWidth + nx;
                        if (!active[next] || labels[next] >= 0)
                            return;
                        labels[next] = label;
                        queue[tail++] = next;
                    }
                }
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(queue);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return labels;
    }

    private static List<ComponentState> BuildComponentStates(
        int[] labels,
        IReadOnlyList<(int MinX, int MinY, int MaxX, int MaxY, int PixelCount)> boxes,
        byte[] sourceFrame,
        int stride,
        int sampleWidth,
        int sampleHeight,
        double sampleScaleX,
        double sampleScaleY,
        CancellationToken cancellationToken)
    {
        List<int[]> groups = BuildOverlappingBoxGroups(boxes, cancellationToken);
        var states = new List<ComponentState>(groups.Count);
        foreach (int[] group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int minX = group.Min(index => boxes[index].MinX);
            int minY = group.Min(index => boxes[index].MinY);
            int maxX = group.Max(index => boxes[index].MaxX);
            int maxY = group.Max(index => boxes[index].MaxY);
            int pixelCount = group.Sum(index => boxes[index].PixelCount);
            int boxWidth = maxX - minX + 1;
            int boxHeight = maxY - minY + 1;
            var includedLabels = new bool[boxes.Count];
            foreach (int index in group)
                includedLabels[index] = true;

            var points = new List<SamplePoint>(Math.Min(MaxSamplesPerComponent, pixelCount));
            int step = Math.Max(1, (int)Math.Sqrt(Math.Max(1, pixelCount / MaxSamplesPerComponent)));
            for (int y = minY; y <= maxY && points.Count < MaxSamplesPerComponent; y += step)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int x = minX; x <= maxX && points.Count < MaxSamplesPerComponent; x += step)
                {
                    int label = labels[y * sampleWidth + x];
                    if (label < 0 || label >= includedLabels.Length || !includedLabels[label])
                        continue;
                    double u = boxWidth <= 1 ? 0.5 : (x - minX) / (double)(boxWidth - 1);
                    double v = boxHeight <= 1 ? 0.5 : (y - minY) / (double)(boxHeight - 1);
                    points.Add(new SamplePoint(u, v));
                }
            }

            if (points.Count == 0)
                continue;

            var sampleRect = new TrackRect(
                minX,
                minY,
                Math.Max(2.0, boxWidth),
                Math.Max(2.0, boxHeight));
            var sourceBounds = new Rect(
                minX / sampleScaleX,
                minY / sampleScaleY,
                Math.Max(1.0, boxWidth / sampleScaleX),
                Math.Max(1.0, boxHeight / sampleScaleY));

            states.Add(new ComponentState
            {
                ComponentIndex = states.Count,
                SourceBounds = sourceBounds,
                SourceRect = sampleRect,
                PreviousRect = sampleRect,
                Points = points,
                Template = ReadTemplate(
                    sourceFrame,
                    stride,
                    sampleWidth,
                    sampleHeight,
                    sampleRect,
                    points,
                    cancellationToken)
            });
        }

        return states;
    }

    private static List<int[]> BuildOverlappingBoxGroups(
        IReadOnlyList<(int MinX, int MinY, int MaxX, int MaxY, int PixelCount)> boxes,
        CancellationToken cancellationToken)
    {
        var remaining = new SortedSet<int>(Enumerable.Range(0, boxes.Count));
        var groups = new List<int[]>();

        while (remaining.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int seed = remaining.Min;
            remaining.Remove(seed);
            var group = new List<int> { seed };
            var envelope = boxes[seed];

            bool expanded;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                expanded = false;
                foreach (int index in remaining.ToArray())
                {
                    if (!BoxesOverlap(envelope, boxes[index]))
                        continue;

                    group.Add(index);
                    remaining.Remove(index);
                    envelope = MergeBoxes(envelope, boxes[index]);
                    expanded = true;
                }
            }
            while (expanded);

            group.Sort();
            groups.Add(group.ToArray());
        }

        return groups;
    }

    private static (int MinX, int MinY, int MaxX, int MaxY, int PixelCount) MergeBoxes(
        (int MinX, int MinY, int MaxX, int MaxY, int PixelCount) a,
        (int MinX, int MinY, int MaxX, int MaxY, int PixelCount) b)
        => (
            Math.Min(a.MinX, b.MinX),
            Math.Min(a.MinY, b.MinY),
            Math.Max(a.MaxX, b.MaxX),
            Math.Max(a.MaxY, b.MaxY),
            a.PixelCount + b.PixelCount);

    private static bool BoxesOverlap(
        (int MinX, int MinY, int MaxX, int MaxY, int PixelCount) a,
        (int MinX, int MinY, int MaxX, int MaxY, int PixelCount) b)
        => a.MinX <= b.MaxX &&
           b.MinX <= a.MaxX &&
           a.MinY <= b.MaxY &&
           b.MinY <= a.MaxY;

    private static double[] ReadTemplate(
        byte[] frame,
        int stride,
        int width,
        int height,
        TrackRect rect,
        IReadOnlyList<SamplePoint> points,
        CancellationToken cancellationToken)
    {
        var result = new double[points.Count];
        for (int i = 0; i < points.Count; i++)
        {
            if ((i & 15) == 0)
                cancellationToken.ThrowIfCancellationRequested();

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
        CancellationToken cancellationToken,
        out TrackRect bestRect,
        out double confidence)
    {
        bestRect = previousRect;
        confidence = double.NegativeInfinity;
        double radiusX = Math.Clamp(previousRect.Width * 0.28, 4.0, 42.0);
        double radiusY = Math.Clamp(previousRect.Height * 0.28, 4.0, 42.0);
        int stepX = Math.Max(1, (int)Math.Round(radiusX / 7.0));
        int stepY = Math.Max(1, (int)Math.Round(radiusY / 7.0));

        foreach (double scaleDelta in CandidateScales)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double candidateWidth = Math.Max(2.0, previousRect.Width * scaleDelta);
            double candidateHeight = Math.Max(2.0, previousRect.Height * scaleDelta);
            for (int dy = -(int)radiusY; dy <= radiusY; dy += stepY)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int dx = -(int)radiusX; dx <= radiusX; dx += stepX)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    double centerX = previousRect.CenterX + dx;
                    double centerY = previousRect.CenterY + dy;
                    var candidate = new TrackRect(
                        centerX - candidateWidth * 0.5,
                        centerY - candidateHeight * 0.5,
                        candidateWidth,
                        candidateHeight);
                    if (candidate.X < 0 || candidate.Y < 0 ||
                        candidate.X + candidate.Width > width ||
                        candidate.Y + candidate.Height > height)
                    {
                        continue;
                    }

                    double error = 0.0;
                    for (int i = 0; i < points.Count; i++)
                    {
                        if ((i & 31) == 0)
                            cancellationToken.ThrowIfCancellationRequested();

                        double x = candidate.X + points[i].U * Math.Max(1.0, candidate.Width - 1.0);
                        double y = candidate.Y + points[i].V * Math.Max(1.0, candidate.Height - 1.0);
                        double luma = ReadLuma(current, stride, width, height, x, y);
                        error += Math.Abs(luma - template[i]);
                    }

                    double meanError = error / Math.Max(1, points.Count) / 255.0;
                    double motionPenalty =
                        (Math.Abs(dx) / Math.Max(1.0, radiusX) +
                         Math.Abs(dy) / Math.Max(1.0, radiusY)) * 0.010;
                    double scalePenalty = Math.Abs(scaleDelta - 1.0) * 0.10;
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
        double[] template,
        CancellationToken cancellationToken)
    {
        const double oldWeight = 0.45;
        const double newWeight = 1.0 - oldWeight;
        for (int i = 0; i < points.Count; i++)
        {
            if ((i & 15) == 0)
                cancellationToken.ThrowIfCancellationRequested();

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
        int height,
        CancellationToken cancellationToken)
    {
        int step = Math.Max(4, Math.Min(width, height) / 32);
        double total = 0.0;
        int count = 0;
        for (int y = step / 2; y < height; y += step)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
