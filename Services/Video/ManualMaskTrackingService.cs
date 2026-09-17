using Avalonia;
using Avalonia.Media.Imaging;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace FaceShield.Services.Video;

// Manual-only classical tracker. No face detector or automatic-blur pipeline is changed.
internal static class ManualMaskTrackingService
{
    private const int MaxTrackingWidth = 480;
    private const int MaxComponents = 32;
    private const int MaxFeaturesPerComponent = 64;
    private const int MinimumFeatures = 6;
    private const double MaximumForwardBackwardError = 2.5;
    private const double MinimumConfidence = 0.52;

    private readonly record struct Bounds(int X0, int Y0, int X1, int Y1, int Pixels)
    {
        internal int Width => X1 - X0 + 1;
        internal int Height => Y1 - Y0 + 1;
    }

    private sealed class Component
    {
        internal required Bounds Source { get; init; }
        internal required List<ManualPoint> Features { get; set; }
        internal ManualMotion Transform { get; set; } = ManualMotion.Identity;
    }

    internal static ManualMaskTrackResult TrackForward(
        string videoPath, int sourceFrameIndex, int endExclusive, WriteableBitmap sourceMask,
        IProgress<int>? progress, CancellationToken cancellationToken)
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
        if (sourceMask.PixelSize.Width != frameSize.Width ||
            sourceMask.PixelSize.Height != frameSize.Height)
            throw new InvalidOperationException("현재 마스크 크기와 원본 영상 프레임 크기가 일치하지 않습니다.");

        int width = Math.Min(MaxTrackingWidth, frameSize.Width);
        int height = Math.Max(1, (int)Math.Round(frameSize.Height * width / (double)frameSize.Width));
        double toSourceX = frameSize.Width / (double)width;
        double toSourceY = frameSize.Height / (double)height;
        int bytes = checked(width * height * 4);
        byte[] previous = ArrayPool<byte>.Shared.Rent(bytes);
        byte[] current = ArrayPool<byte>.Shared.Rent(bytes);
        try
        {
            extractor.StartSequentialRead(sourceFrameIndex, cancellationToken);
            int previousStride = 0, decodedIndex = sourceFrameIndex - 1;
            while (decodedIndex < sourceFrameIndex)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!extractor.TryGetNextFrameRawToBuffer(
                    cancellationToken, width, height, useBilinear: true,
                    previous, out decodedIndex, out previousStride))
                    throw new InvalidOperationException("추적 시작 프레임을 디코딩하지 못했습니다.");
            }
            if (decodedIndex != sourceFrameIndex)
                throw new InvalidOperationException("추적 시작 프레임의 순서가 일치하지 않습니다.");

            ManualImagePyramid previousPyramid = new(previous, previousStride, width, height, cancellationToken);
            List<Bounds> boxes = ReadMaskComponents(sourceMask, width, height, cancellationToken);
            if (boxes.Count == 0)
                throw new InvalidOperationException("현재 마스크가 비어 있어 추적할 영역이 없습니다.");
            if (boxes.Count > MaxComponents)
                throw new InvalidOperationException($"분리된 마스크 영역이 너무 많습니다({boxes.Count}개).");
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
                List<ManualPoint> features = SelectFeatures(
                    sourceMask, box, previous, previousStride, width, height, cancellationToken);
                if (features.Count < MinimumFeatures)
                    throw new InvalidOperationException(
                        "마스크 내부에서 추적 가능한 특징점을 6개 찾지 못했습니다. " +
                        "영역을 조금 넓히거나 다른 프레임에서 마스크를 지정해 주세요.");
                components.Add(new Component { Source = box, Features = features });
                segment.Components.Add(new ManualMaskTrackComponent
                {
                    ComponentIndex = segment.Components.Count,
                    SourceBoundsX = box.X0 * toSourceX,
                    SourceBoundsY = box.Y0 * toSourceY,
                    SourceBoundsWidth = box.Width * toSourceX,
                    SourceBoundsHeight = box.Height * toSourceY
                });
            }

            // Keep exactly two float-buffer sets for the lifetime of this run.
            // The inactive set is reloaded only after the scene-cut check, and
            // the swap happens only after every component passes validation.
            ManualImagePyramid currentPyramid = new(width, height);
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
                if (currentIndex <= lastDecoded) continue;
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

                ManualSceneEvidence scene = ManualSceneCut.Compare(previous, previousStride,
                    current, currentStride, width, height, cancellationToken);
                if (scene.StrongCut)
                {
                    Stop(segment, currentIndex,
                        $"장면 전환 감지(색상={scene.Color:0.000}, 구역={scene.Spatial:0.000}, 구조={scene.Structure:0.000})");
                    break;
                }
                currentPyramid.Reload(current, currentStride, cancellationToken);
                var nextFeatures = new List<ManualPoint>[components.Count];
                var nextTransforms = new ManualMotion[components.Count];
                var confidences = new double[components.Count];
                string? failure = null;
                for (int c = 0; c < components.Count; c++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Component component = components[c];
                    var matches = new List<ManualFeatureMatch>(component.Features.Count);
                    foreach (ManualPoint feature in component.Features)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!previousPyramid.TryTrack(currentPyramid, feature, cancellationToken,
                            out ManualPoint found, out double error) || error > 0.22 ||
                            !currentPyramid.TryTrack(previousPyramid, found, cancellationToken,
                            out ManualPoint returned, out _) ||
                            DistanceSquared(returned, feature) >
                                MaximumForwardBackwardError * MaximumForwardBackwardError)
                            continue;
                        matches.Add(new ManualFeatureMatch(feature, found, error));
                    }
                    if (!ManualSimilarityEstimator.TryEstimate(matches, currentIndex, cancellationToken,
                        out ManualMotion step, out List<ManualPoint> inliers, out double confidence))
                    {
                        failure = scene.Suspicious
                            ? $"장면 전환 의심 및 영역 {c + 1} 특징점 추적 불일치(score={scene.Score:0.000})"
                            : $"영역 {c + 1}의 특징점 일치 또는 역방향 검증 실패";
                        break;
                    }
                    ManualMotion cumulative = step.After(component.Transform);
                    if (cumulative.Scale < 0.25 || cumulative.Scale > 4.0 ||
                        !InsideTransformedBounds(component.Source, cumulative, width, height) ||
                        confidence < MinimumConfidence)
                    {
                        failure = scene.Suspicious
                            ? $"장면 전환 의심 및 영역 {c + 1} 움직임 불일치(score={scene.Score:0.000})"
                            : $"영역 {c + 1}의 움직임 또는 추적 신뢰도가 불안정합니다.";
                        break;
                    }
                    nextFeatures[c] = inliers;
                    nextTransforms[c] = cumulative;
                    confidences[c] = confidence;
                }
                if (failure != null)
                {
                    Stop(segment, currentIndex, failure);
                    break;
                }
                // Commit this frame atomically only after every mask component is validated.
                for (int c = 0; c < components.Count; c++)
                {
                    Component component = components[c];
                    component.Features = nextFeatures[c];
                    component.Transform = nextTransforms[c];
                    ManualMotion transform = component.Transform;
                    ManualPoint origin = transform.Apply(new ManualPoint(component.Source.X0, component.Source.Y0));
                    ManualMaskTrackComponent output = segment.Components[c];
                    output.Samples.Add(new ManualMaskTrackSample
                    {
                        FrameIndex = currentIndex,
                        OffsetX = origin.X * toSourceX - output.SourceBoundsX,
                        OffsetY = origin.Y * toSourceY - output.SourceBoundsY,
                        Scale = transform.Scale,
                        RotationRadians = transform.Angle,
                        Confidence = confidences[c]
                    });
                }
                processed++;
                progress?.Report(processed);
                (previous, current) = (current, previous);
                previousStride = currentStride;
                (previousPyramid, currentPyramid) = (currentPyramid, previousPyramid);
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

    private static bool InsideTransformedBounds(Bounds box, ManualMotion motion, int width, int height)
    {
        ManualPoint[] corners =
        {
            new(box.X0, box.Y0), new(box.X0 + box.Width, box.Y0),
            new(box.X0, box.Y0 + box.Height), new(box.X0 + box.Width, box.Y0 + box.Height)
        };
        foreach (ManualPoint corner in corners)
        {
            ManualPoint p = motion.Apply(corner);
            if (!double.IsFinite(p.X) || !double.IsFinite(p.Y) ||
                p.X < 0 || p.Y < 0 || p.X > width || p.Y > height) return false;
        }
        return true;
    }

    private static double DistanceSquared(ManualPoint a, ManualPoint b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return dx * dx + dy * dy;
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
                    if (a.X0 > b.X1 || b.X0 > a.X1 || a.Y0 > b.Y1 || b.Y0 > a.Y1) continue;
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

    private static List<ManualPoint> SelectFeatures(WriteableBitmap mask, Bounds box,
        byte[] frame, int stride, int width, int height, CancellationToken ct)
    {
        var ranked = new List<(double Strength, ManualPoint Location)>();
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
                    double gx = Luma(frame, stride, x + 2, y) - Luma(frame, stride, x - 2, y);
                    double gy = Luma(frame, stride, x, y + 2) - Luma(frame, stride, x, y - 2);
                    double strength = Math.Abs(gx) + Math.Abs(gy);
                    // Preserve the old strong-feature set. Only supplement it with
                    // lower-contrast candidates when there are fewer than six.
                    if (strength >= 6) ranked.Add((strength, new ManualPoint(x, y)));
                }
            }
        }
        ranked.Sort(static (a, b) => b.Strength.CompareTo(a.Strength));
        var chosen = new List<ManualPoint>(Math.Min(MaxFeaturesPerComponent, ranked.Count));
        foreach (var candidate in ranked)
        {
            if ((chosen.Count & 15) == 0) ct.ThrowIfCancellationRequested();
            if (candidate.Strength < 16 && chosen.Count >= MinimumFeatures) break;
            if (chosen.Any(point => DistanceSquared(point, candidate.Location) < 16)) continue;
            chosen.Add(candidate.Location);
            if (chosen.Count >= MaxFeaturesPerComponent) break;
        }
        return chosen;
    }

    private static double Luma(byte[] frame, int stride, int x, int y)
    {
        int i = y * stride + x * 4;
        return frame[i] * 0.114 + frame[i + 1] * 0.587 + frame[i + 2] * 0.299;
    }
}
