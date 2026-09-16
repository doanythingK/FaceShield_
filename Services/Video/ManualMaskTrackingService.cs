using Avalonia;
using Avalonia.Media.Imaging;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace FaceShield.Services.Video;

// A classical computer-vision tracker, not a face detector. It deliberately fails
// closed rather than accepting a plausible-looking brightness match on a new scene.
internal static class ManualMaskTrackingService
{
    private const int MaxTrackingWidth = 480;
    private const int MaxComponents = 32;
    private const int MaxFeaturesPerComponent = 64;
    private const int MinimumFeatures = 6;
    private const double MaximumForwardBackwardError = 2.5;
    private const double MaximumInlierError = 3.0;
    private const double MinimumInlierFraction = 0.60;
    private const double MinimumConfidence = 0.52;

    private readonly record struct Point(double X, double Y);
    private readonly record struct Motion(double Scale, double X, double Y)
    {
        internal Point Apply(Point p) => new(p.X * Scale + X, p.Y * Scale + Y);
    }
    private readonly record struct Match(Point From, Point To, double Error);
    private readonly record struct Bounds(int X0, int Y0, int X1, int Y1, int Pixels)
    {
        internal int Width => X1 - X0 + 1;
        internal int Height => Y1 - Y0 + 1;
    }

    private sealed class Component
    {
        internal required Bounds Source { get; init; }
        internal required List<Point> Features { get; set; }
        internal double X { get; set; }
        internal double Y { get; set; }
        internal double Scale { get; set; } = 1.0;
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
        ArgumentNullException.ThrowIfNull(sourceMask);
        if (sourceFrameIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(sourceFrameIndex));

        cancellationToken.ThrowIfCancellationRequested();
        using var extractor = new FfFrameExtractor(
            videoPath, enableHardware: false, cancellationToken: cancellationToken);
        PixelSize frameSize = extractor.FrameSize;
        if (frameSize.Width <= 0 || frameSize.Height <= 0)
            throw new InvalidOperationException("영상 프레임 크기를 확인할 수 없습니다.");
        if (sourceMask.PixelSize != frameSize)
            throw new InvalidOperationException("현재 마스크 크기와 원본 영상 프레임 크기가 일치하지 않습니다.");

        int width = Math.Min(MaxTrackingWidth, frameSize.Width);
        int height = Math.Max(1, (int)Math.Round(frameSize.Height * width / (double)frameSize.Width));
        double toSourceX = frameSize.Width / (double)width;
        double toSourceY = frameSize.Height / (double)height;
        int size = checked(width * height * 4);
        byte[] previous = ArrayPool<byte>.Shared.Rent(size);
        byte[] current = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            extractor.StartSequentialRead(sourceFrameIndex, cancellationToken);
            int previousStride = 0;
            int index = sourceFrameIndex - 1;
            while (index < sourceFrameIndex)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!extractor.TryGetNextFrameRawToBuffer(
                        cancellationToken, width, height, useBilinear: true,
                        previous, out index, out previousStride))
                {
                    throw new InvalidOperationException("추적 시작 프레임을 디코딩하지 못했습니다.");
                }
            }
            if (index != sourceFrameIndex)
                throw new InvalidOperationException("추적 시작 프레임의 순서가 일치하지 않습니다.");

            List<Bounds> boxes = ReadMaskComponents(sourceMask, width, height, cancellationToken);
            if (boxes.Count == 0)
                throw new InvalidOperationException("현재 마스크가 비어 있어 추적할 영역이 없습니다.");
            if (boxes.Count > MaxComponents)
                throw new InvalidOperationException($"분리된 마스크 영역이 너무 많습니다({boxes.Count}개).");

            // Group touching/overlapping envelopes so pixels from separate source
            // regions cannot be moved independently into one another's source bounds.
            boxes = MergeOverlappingBoxes(boxes, cancellationToken);
            var components = new List<Component>(boxes.Count);
            var segment = new ManualMaskTrackSegment
            {
                SourceKeyframe = sourceFrameIndex,
                EndExclusive = endExclusive > sourceFrameIndex ? endExclusive : int.MaxValue
            };
            foreach (Bounds box in boxes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                List<Point> features = SelectFeatures(
                    sourceMask, box, previous, previousStride, width, height, cancellationToken);
                if (features.Count < MinimumFeatures)
                {
                    throw new InvalidOperationException(
                        "마스크 내부의 추적 가능한 특징점이 부족합니다. 다른 프레임에서 마스크를 지정해 주세요.");
                }

                components.Add(new Component
                {
                    Source = box,
                    Features = features,
                    X = box.X0,
                    Y = box.Y0
                });
                segment.Components.Add(new ManualMaskTrackComponent
                {
                    ComponentIndex = segment.Components.Count,
                    SourceBoundsX = box.X0 * toSourceX,
                    SourceBoundsY = box.Y0 * toSourceY,
                    SourceBoundsWidth = box.Width * toSourceX,
                    SourceBoundsHeight = box.Height * toSourceY
                });
            }

            int lastDecoded = sourceFrameIndex;
            int processed = 0;
            bool reachedBoundary = false;
            while (lastDecoded < segment.EndExclusive - 1)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!extractor.TryGetNextFrameRawToBuffer(
                        cancellationToken, width, height, useBilinear: true,
                        current, out int currentIndex, out int currentStride))
                {
                    segment.EndExclusive = checked(lastDecoded + 1);
                    reachedBoundary = true;
                    break;
                }
                if (currentIndex <= lastDecoded)
                    continue;
                // Missing decoded ordinals are not evidence that their masks are valid.
                if (currentIndex != lastDecoded + 1)
                {
                    Stop(segment, lastDecoded + 1, "프레임 순서가 연속되지 않아 추적을 중단했습니다.");
                    break;
                }
                lastDecoded = currentIndex;
                if (currentIndex >= segment.EndExclusive)
                {
                    reachedBoundary = true;
                    break;
                }

                if (IsSceneCut(previous, previousStride, current, currentStride,
                        width, height, cancellationToken, out double cutScore))
                {
                    Stop(segment, currentIndex, $"장면 전환 감지(score={cutScore:0.000})");
                    break;
                }

                // Do not update any component until *all* components pass validation.
                var nextFeatures = new List<Point>[components.Count];
                var nextPositions = new Point[components.Count];
                var nextScales = new double[components.Count];
                var confidences = new double[components.Count];
                string? failure = null;
                for (int c = 0; c < components.Count; c++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Component component = components[c];
                    var matches = new List<Match>(component.Features.Count);
                    foreach (Point feature in component.Features)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!TryMatch(previous, previousStride, current, currentStride,
                                width, height, feature, cancellationToken,
                                out Point found, out double error) ||
                            error > 0.22 ||
                            !TryMatch(current, currentStride, previous, previousStride,
                                width, height, found, cancellationToken,
                                out Point returned, out _) ||
                            DistanceSquared(returned, feature) >
                                MaximumForwardBackwardError * MaximumForwardBackwardError)
                        {
                            continue;
                        }
                        matches.Add(new Match(feature, found, error));
                    }

                    if (!TryEstimateMotion(matches, currentIndex, cancellationToken,
                            out Motion motion, out List<Point> inliers, out double confidence))
                    {
                        failure = $"영역 {c + 1}의 특징점 일치 또는 역방향 검증 실패";
                        break;
                    }

                    double scale = component.Scale * motion.Scale;
                    Point origin = motion.Apply(new Point(component.X, component.Y));
                    double rectWidth = component.Source.Width * scale;
                    double rectHeight = component.Source.Height * scale;
                    if (scale < 0.25 || scale > 4.0 ||
                        origin.X < 0 || origin.Y < 0 ||
                        origin.X + rectWidth > width ||
                        origin.Y + rectHeight > height ||
                        confidence < MinimumConfidence)
                    {
                        failure = $"영역 {c + 1}의 움직임 또는 추적 신뢰도가 불안정합니다.";
                        break;
                    }
                    nextFeatures[c] = inliers;
                    nextPositions[c] = origin;
                    nextScales[c] = scale;
                    confidences[c] = confidence;
                }

                if (failure != null)
                {
                    Stop(segment, currentIndex, failure);
                    break;
                }

                for (int c = 0; c < components.Count; c++)
                {
                    Component component = components[c];
                    Point origin = nextPositions[c];
                    component.X = origin.X;
                    component.Y = origin.Y;
                    component.Scale = nextScales[c];
                    component.Features = nextFeatures[c];
                    ManualMaskTrackComponent output = segment.Components[c];
                    output.Samples.Add(new ManualMaskTrackSample
                    {
                        FrameIndex = currentIndex,
                        OffsetX = origin.X * toSourceX - output.SourceBoundsX,
                        OffsetY = origin.Y * toSourceY - output.SourceBoundsY,
                        Scale = component.Scale,
                        Confidence = confidences[c]
                    });
                }
                processed++;
                progress?.Report(processed);
                (previous, current) = (current, previous);
                previousStride = currentStride;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!segment.StoppedByFailure && segment.EndExclusive == int.MaxValue)
                segment.EndExclusive = checked(lastDecoded + 1);
            else if (!segment.StoppedByFailure)
                reachedBoundary = true;
            return new ManualMaskTrackResult(segment, processed, reachedBoundary);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(previous);
            ArrayPool<byte>.Shared.Return(current);
        }
    }

    private static void Stop(ManualMaskTrackSegment segment, int frame, string reason)
    {
        segment.EndExclusive = frame;
        segment.StoppedByFailure = true;
        segment.StopFrame = frame;
        segment.StopReason = reason;
    }

    private static List<Bounds> ReadMaskComponents(
        WriteableBitmap mask, int width, int height, CancellationToken ct)
    {
        int count = checked(width * height);
        var active = new bool[count];
        using (var fb = mask.Lock())
        {
            unsafe
            {
                byte* origin = (byte*)fb.Address;
                for (int y = 0; y < height; y++)
                {
                    if ((y & 15) == 0) ct.ThrowIfCancellationRequested();
                    int sy = Math.Clamp((int)Math.Round((y + 0.5) * fb.Size.Height / height - 0.5), 0, fb.Size.Height - 1);
                    byte* row = origin + sy * fb.RowBytes;
                    for (int x = 0; x < width; x++)
                    {
                        int sx = Math.Clamp((int)Math.Round((x + 0.5) * fb.Size.Width / width - 0.5), 0, fb.Size.Width - 1);
                        active[y * width + x] = row[sx * 4 + 3] > 24;
                    }
                }
            }
        }

        var visited = new bool[count];
        int[] queue = ArrayPool<int>.Shared.Rent(count);
        var boxes = new List<Bounds>();
        try
        {
            for (int seed = 0; seed < count; seed++)
            {
                if ((seed & 8191) == 0) ct.ThrowIfCancellationRequested();
                if (!active[seed] || visited[seed]) continue;
                int first = 0, last = 0;
                queue[last++] = seed;
                visited[seed] = true;
                int x0 = seed % width, x1 = x0, y0 = seed / width, y1 = y0, pixels = 0;
                while (first < last)
                {
                    if ((first & 1023) == 0) ct.ThrowIfCancellationRequested();
                    int position = queue[first++];
                    int x = position % width, y = position / width;
                    x0 = Math.Min(x0, x); x1 = Math.Max(x1, x);
                    y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
                    pixels++;
                    Visit(x - 1, y); Visit(x + 1, y); Visit(x, y - 1); Visit(x, y + 1);
                }
                boxes.Add(new Bounds(x0, y0, x1, y1, pixels));
                if (boxes.Count > MaxComponents) return boxes;

                void Visit(int x, int y)
                {
                    if (x < 0 || y < 0 || x >= width || y >= height) return;
                    int p = y * width + x;
                    if (visited[p] || !active[p]) return;
                    visited[p] = true;
                    queue[last++] = p;
                }
            }
        }
        finally { ArrayPool<int>.Shared.Return(queue); }
        return boxes;
    }

    private static List<Bounds> MergeOverlappingBoxes(List<Bounds> boxes, CancellationToken ct)
    {
        var result = new List<Bounds>();
        while (boxes.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            Bounds a = boxes[0];
            boxes.RemoveAt(0);
            bool expanded;
            do
            {
                expanded = false;
                for (int i = boxes.Count - 1; i >= 0; i--)
                {
                    Bounds b = boxes[i];
                    if (a.X0 > b.X1 || b.X0 > a.X1 || a.Y0 > b.Y1 || b.Y0 > a.Y1)
                        continue;
                    a = new Bounds(Math.Min(a.X0, b.X0), Math.Min(a.Y0, b.Y0),
                        Math.Max(a.X1, b.X1), Math.Max(a.Y1, b.Y1), a.Pixels + b.Pixels);
                    boxes.RemoveAt(i);
                    expanded = true;
                }
            } while (expanded);
            result.Add(a);
        }
        return result;
    }

    private static List<Point> SelectFeatures(
        WriteableBitmap mask, Bounds box, byte[] frame, int stride,
        int width, int height, CancellationToken ct)
    {
        var ranked = new List<(double Strength, Point Location)>();
        using var fb = mask.Lock();
        unsafe
        {
            byte* origin = (byte*)fb.Address;
            for (int y = Math.Max(4, box.Y0); y <= Math.Min(height - 5, box.Y1); y += 2)
            {
                ct.ThrowIfCancellationRequested();
                int sy = Math.Clamp((int)Math.Round((y + 0.5) * fb.Size.Height / height - 0.5), 0, fb.Size.Height - 1);
                byte* row = origin + sy * fb.RowBytes;
                for (int x = Math.Max(4, box.X0); x <= Math.Min(width - 5, box.X1); x += 2)
                {
                    int sx = Math.Clamp((int)Math.Round((x + 0.5) * fb.Size.Width / width - 0.5), 0, fb.Size.Width - 1);
                    if (row[sx * 4 + 3] <= 24) continue;
                    double gx = Luma(frame, stride, width, height, x + 2, y) -
                                Luma(frame, stride, width, height, x - 2, y);
                    double gy = Luma(frame, stride, width, height, x, y + 2) -
                                Luma(frame, stride, width, height, x, y - 2);
                    double strength = Math.Abs(gx) + Math.Abs(gy);
                    if (strength >= 16) ranked.Add((strength, new Point(x, y)));
                }
            }
        }
        ranked.Sort(static (a, b) => b.Strength.CompareTo(a.Strength));
        var chosen = new List<Point>(Math.Min(MaxFeaturesPerComponent, ranked.Count));
        foreach (var item in ranked)
        {
            if ((chosen.Count & 15) == 0) ct.ThrowIfCancellationRequested();
            if (chosen.Any(point => DistanceSquared(point, item.Location) < 16)) continue;
            chosen.Add(item.Location);
            if (chosen.Count >= MaxFeaturesPerComponent) break;
        }
        return chosen;
    }

    private static bool TryMatch(
        byte[] from, int fromStride, byte[] to, int toStride,
        int width, int height, Point point, CancellationToken ct,
        out Point result, out double error)
    {
        result = point;
        error = double.PositiveInfinity;
        if (point.X < 5 || point.Y < 5 || point.X >= width - 5 || point.Y >= height - 5)
            return false;
        int radius = 11;
        Point best = point;
        for (int dy = -radius; dy <= radius; dy += 2)
        {
            ct.ThrowIfCancellationRequested();
            for (int dx = -radius; dx <= radius; dx += 2)
            {
                Point candidate = new(point.X + dx, point.Y + dy);
                double score = PatchError(from, fromStride, to, toStride,
                    width, height, point, candidate, step: 2);
                if (score < error) { error = score; best = candidate; }
            }
        }
        Point coarse = best;
        for (int dy = -2; dy <= 2; dy++)
        {
            ct.ThrowIfCancellationRequested();
            for (int dx = -2; dx <= 2; dx++)
            {
                Point candidate = new(coarse.X + dx, coarse.Y + dy);
                double score = PatchError(from, fromStride, to, toStride,
                    width, height, point, candidate, step: 1);
                if (score < error) { error = score; best = candidate; }
            }
        }
        result = best;
        return double.IsFinite(error);
    }

    private static double PatchError(
        byte[] from, int fromStride, byte[] to, int toStride,
        int width, int height, Point a, Point b, int step)
    {
        if (a.X < 4 || a.Y < 4 || a.X >= width - 4 || a.Y >= height - 4 ||
            b.X < 4 || b.Y < 4 || b.X >= width - 4 || b.Y >= height - 4)
            return double.PositiveInfinity;
        double sumA = 0, sumB = 0;
        int count = 0;
        for (int y = -2; y <= 2; y++)
            for (int x = -2; x <= 2; x++)
            {
                sumA += Luma(from, fromStride, width, height, a.X + x * step, a.Y + y * step);
                sumB += Luma(to, toStride, width, height, b.X + x * step, b.Y + y * step);
                count++;
            }
        double meanA = sumA / count, meanB = sumB / count;
        double total = 0;
        for (int y = -2; y <= 2; y++)
            for (int x = -2; x <= 2; x++)
            {
                double first = Luma(from, fromStride, width, height, a.X + x * step, a.Y + y * step);
                double second = Luma(to, toStride, width, height, b.X + x * step, b.Y + y * step);
                total += Math.Abs((first - meanA) - (second - meanB));
            }
        return total / (count * 255.0);
    }

    private static bool TryEstimateMotion(
        IReadOnlyList<Match> matches, int frame, CancellationToken ct,
        out Motion result, out List<Point> inliers, out double confidence)
    {
        result = new Motion(1, 0, 0);
        inliers = new List<Point>();
        confidence = 0;
        if (matches.Count < MinimumFeatures) return false;
        int bestCount = 0;
        double bestError = double.PositiveInfinity;
        Motion best = result;
        // Deterministic two-point RANSAC for uniform scale + translation. The
        // renderer cannot rotate, so rotation is not silently approximated here.
        for (int attempt = 0; attempt < 128; attempt++)
        {
            if ((attempt & 15) == 0) ct.ThrowIfCancellationRequested();
            int i = (attempt * 17 + frame) % matches.Count;
            int j = (attempt * 29 + 7 + frame / 3) % matches.Count;
            if (i == j) continue;
            Point a = matches[i].From, b = matches[j].From;
            Point c = matches[i].To, d = matches[j].To;
            double denominator = DistanceSquared(a, b);
            if (denominator < 36) continue;
            double scale = ((d.X - c.X) * (b.X - a.X) +
                            (d.Y - c.Y) * (b.Y - a.Y)) / denominator;
            if (scale < 0.90 || scale > 1.10) continue;
            var motion = new Motion(scale, c.X - a.X * scale, c.Y - a.Y * scale);
            int count = 0;
            double error = 0;
            foreach (Match match in matches)
            {
                double distance = DistanceSquared(motion.Apply(match.From), match.To);
                if (distance > MaximumInlierError * MaximumInlierError) continue;
                count++;
                error += distance;
            }
            if (count > bestCount || (count == bestCount && error < bestError))
            {
                best = motion;
                bestCount = count;
                bestError = error;
            }
        }
        if (bestCount < MinimumFeatures || bestCount <
            (int)Math.Ceiling(matches.Count * MinimumInlierFraction)) return false;

        // Refine on the consensus set; keep only geometrically consistent points.
        var accepted = new List<Match>();
        foreach (Match match in matches)
            if (DistanceSquared(best.Apply(match.From), match.To) <=
                MaximumInlierError * MaximumInlierError) accepted.Add(match);
        if (accepted.Count < MinimumFeatures) return false;
        double fromX = accepted.Average(static match => match.From.X);
        double fromY = accepted.Average(static match => match.From.Y);
        double toX = accepted.Average(static match => match.To.X);
        double toY = accepted.Average(static match => match.To.Y);
        double numerator = 0, denominatorRefined = 0;
        foreach (Match match in accepted)
        {
            double fx = match.From.X - fromX, fy = match.From.Y - fromY;
            numerator += fx * (match.To.X - toX) + fy * (match.To.Y - toY);
            denominatorRefined += fx * fx + fy * fy;
        }
        if (denominatorRefined < 36) return false;
        double refinedScale = numerator / denominatorRefined;
        if (refinedScale < 0.90 || refinedScale > 1.10) return false;
        result = new Motion(refinedScale, toX - fromX * refinedScale, toY - fromY * refinedScale);
        double residual = 0, photometric = 0;
        foreach (Match match in accepted)
        {
            double d = DistanceSquared(result.Apply(match.From), match.To);
            if (d > MaximumInlierError * MaximumInlierError) continue;
            inliers.Add(match.To);
            residual += d;
            photometric += match.Error;
        }
        if (inliers.Count < MinimumFeatures || inliers.Count <
            (int)Math.Ceiling(matches.Count * MinimumInlierFraction)) return false;
        double ratio = inliers.Count / (double)matches.Count;
        double rms = Math.Sqrt(residual / inliers.Count);
        confidence = ratio * Math.Max(0, 1 - rms / 6) *
                     Math.Max(0, 1 - photometric / inliers.Count);
        return double.IsFinite(confidence);
    }

    private static bool IsSceneCut(
        byte[] previous, int previousStride, byte[] current, int currentStride,
        int width, int height, CancellationToken ct, out double score)
    {
        // A sole full-frame mean-luma threshold missed cuts between similarly
        // lit scenes. Combine global luminance histograms, spatial histograms,
        // and direct difference; never rely on brightness alone.
        var globalA = new int[16]; var globalB = new int[16];
        var spatialA = new int[16 * 8]; var spatialB = new int[16 * 8];
        int[] perCell = new int[16];
        int step = Math.Max(2, Math.Min(width, height) / 64);
        double sumDifference = 0;
        int samples = 0;
        for (int y = step / 2; y < height; y += step)
        {
            ct.ThrowIfCancellationRequested();
            for (int x = step / 2; x < width; x += step)
            {
                double a = Luma(previous, previousStride, width, height, x, y);
                double b = Luma(current, currentStride, width, height, x, y);
                int cell = Math.Min(3, y * 4 / height) * 4 + Math.Min(3, x * 4 / width);
                globalA[Math.Min(15, (int)a / 16)]++;
                globalB[Math.Min(15, (int)b / 16)]++;
                spatialA[cell * 8 + Math.Min(7, (int)a / 32)]++;
                spatialB[cell * 8 + Math.Min(7, (int)b / 32)]++;
                perCell[cell]++;
                sumDifference += Math.Abs(a - b) / 255.0;
                samples++;
            }
        }
        if (samples == 0) { score = 0; return false; }
        double histogram = 0, spatial = 0;
        for (int i = 0; i < globalA.Length; i++)
            histogram += Math.Abs(globalA[i] - globalB[i]);
        histogram /= 2.0 * samples;
        for (int cell = 0; cell < 16; cell++)
        {
            if (perCell[cell] == 0) continue;
            for (int bin = 0; bin < 8; bin++)
                spatial += Math.Abs(spatialA[cell * 8 + bin] - spatialB[cell * 8 + bin]);
        }
        spatial /= 2.0 * samples;
        double difference = sumDifference / samples;
        score = Math.Max(histogram, Math.Min(difference, spatial));
        return histogram >= 0.32 ||
               (difference >= 0.20 && spatial >= 0.22) ||
               (difference >= 0.32 && spatial >= 0.13);
    }

    private static double DistanceSquared(Point a, Point b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    private static double Luma(byte[] frame, int stride, int width, int height,
        double x, double y)
    {
        int ix = Math.Clamp((int)Math.Round(x), 0, width - 1);
        int iy = Math.Clamp((int)Math.Round(y), 0, height - 1);
        int offset = iy * stride + ix * 4;
        if (offset < 0 || offset + 2 >= frame.Length) return 0;
        return frame[offset] * 0.114 + frame[offset + 1] * 0.587 + frame[offset + 2] * 0.299;
    }
}
