using Avalonia;
using Avalonia.Media.Imaging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace FaceShield.Services.Video
{
    public sealed class FrameMaskProvider : IFrameMaskProvider, IDisposable
{
    private readonly ConcurrentDictionary<int, WriteableBitmap> _masks = new();
    private readonly ConcurrentDictionary<int, FaceMaskData> _faceMasks = new();

    public void SetMask(int frameIndex, WriteableBitmap mask)
    {
        if (mask == null)
            throw new ArgumentNullException(nameof(mask));

        if (_masks.TryRemove(frameIndex, out var previous) &&
            !ReferenceEquals(previous, mask))
        {
            previous.Dispose();
        }

        _masks[frameIndex] = mask;
        _faceMasks.TryRemove(frameIndex, out _);
    }

    public void SetFaceRects(
        int frameIndex,
        IReadOnlyList<Rect> faces,
        PixelSize size,
        float? minConfidence = null,
        IReadOnlyList<float>? confidences = null)
    {
        RemoveStoredMask(frameIndex);

        if (faces == null || faces.Count == 0 || size.Width <= 0 || size.Height <= 0)
        {
            _faceMasks.TryRemove(frameIndex, out _);
            return;
        }

        var faceArray = faces as Rect[] ?? faces.ToArray();
        var confArray = NormalizeConfidences(faceArray.Length, minConfidence, confidences);
        _faceMasks[frameIndex] = new FaceMaskData(size, faceArray, minConfidence, confArray);
    }

    public void SetFaceRects(
        int frameIndex,
        Rect[] faces,
        PixelSize size,
        float? minConfidence = null,
        IReadOnlyList<float>? confidences = null)
    {
        RemoveStoredMask(frameIndex);

        if (faces == null || faces.Length == 0 || size.Width <= 0 || size.Height <= 0)
        {
            _faceMasks.TryRemove(frameIndex, out _);
            return;
        }

        var confArray = NormalizeConfidences(faces.Length, minConfidence, confidences);
        _faceMasks[frameIndex] = new FaceMaskData(size, faces, minConfidence, confArray);
    }

    public WriteableBitmap? GetFinalMask(int frameIndex)
    {
        if (_masks.TryGetValue(frameIndex, out var m))
            return m;

        if (_faceMasks.TryGetValue(frameIndex, out var faces))
            return CreateMaskFromFaceRects(faces.Size, faces.Faces);

        return null;
    }

    public bool HasEntry(int frameIndex)
        => _masks.ContainsKey(frameIndex) || _faceMasks.ContainsKey(frameIndex);

    public bool TryGetStoredMask(int frameIndex, out WriteableBitmap mask)
        => _masks.TryGetValue(frameIndex, out mask!);

    public bool TryGetFaceMaskData(int frameIndex, out FaceMaskData data)
        => _faceMasks.TryGetValue(frameIndex, out data);

    public void RemoveFaceMask(int frameIndex)
    {
        _faceMasks.TryRemove(frameIndex, out _);
    }

    public int RemoveFaceMasksFrom(int startFrameIndex)
    {
        int removed = 0;
        foreach (int frameIndex in _faceMasks.Keys)
        {
            if (frameIndex < startFrameIndex)
                continue;

            if (_faceMasks.TryRemove(frameIndex, out _))
                removed++;
        }

        return removed;
    }

    public int RemoveFaceMasksRange(int startFrameIndex, int endExclusive)
    {
        int start = Math.Max(0, startFrameIndex);
        int end = Math.Max(start, endExclusive);
        int removedFaceMasks = 0;
        int removedStoredMasks = 0;
        for (int frameIndex = start; frameIndex < end; frameIndex++)
        {
            if (_faceMasks.TryRemove(frameIndex, out _))
                removedFaceMasks++;
            if (_masks.TryRemove(frameIndex, out var storedMask))
            {
                storedMask.Dispose();
                removedStoredMasks++;
            }
        }

        return removedFaceMasks + removedStoredMasks;
    }

    public void ClearFaceMasks()
    {
        _faceMasks.Clear();
    }

    public bool HasAnyMaskEntries()
        => !_masks.IsEmpty || !_faceMasks.IsEmpty;

    public int[] GetStoredMaskFrameIndices()
        => _masks.Keys.ToArray();

    public int[] GetFaceMaskFrameIndices()
        => _faceMasks.Keys.ToArray();

    public IReadOnlyCollection<KeyValuePair<int, FaceMaskData>> GetFaceMaskEntries()
        => _faceMasks.ToArray();

    public IReadOnlyCollection<KeyValuePair<int, WriteableBitmap>> GetMaskEntries()
        => _masks.ToArray();

    public void Clear()
    {
        foreach (int frameIndex in _masks.Keys)
            RemoveStoredMask(frameIndex);

        _faceMasks.Clear();
    }

    public void Dispose()
    {
        Clear();
    }

    private void RemoveStoredMask(int frameIndex)
    {
        if (_masks.TryRemove(frameIndex, out var mask))
            mask.Dispose();
    }

    public readonly record struct FaceMaskData(
        PixelSize Size,
        IReadOnlyList<Rect> Faces,
        float? MinConfidence,
        IReadOnlyList<float> Confidences);

    private static IReadOnlyList<float> NormalizeConfidences(
        int faceCount,
        float? minConfidence,
        IReadOnlyList<float>? confidences)
    {
        if (faceCount <= 0)
            return Array.Empty<float>();

        if (confidences != null && confidences.Count == faceCount)
            return confidences is float[] arr ? arr : confidences.ToArray();

        float fill = minConfidence ?? 1.0f;
        var fallback = new float[faceCount];
        for (int i = 0; i < faceCount; i++)
            fallback[i] = fill;
        return fallback;
    }

    public static WriteableBitmap CreateMaskFromFaceRects(PixelSize size, IReadOnlyList<Rect> faces)
    {
        var mask = new WriteableBitmap(
            size,
            new Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888,
            Avalonia.Platform.AlphaFormat.Premul);

        RenderMaskFromFaceRects(mask, size, faces);
        return mask;
    }

    public static void RenderMaskFromFaceRects(WriteableBitmap mask, PixelSize size, IReadOnlyList<Rect> faces)
    {
        if (mask == null)
            throw new ArgumentNullException(nameof(mask));
        if (size.Width <= 0 || size.Height <= 0)
            return;

        if (mask.PixelSize.Width != size.Width || mask.PixelSize.Height != size.Height)
            throw new ArgumentException("Mask bitmap size does not match target size.", nameof(mask));

        using var fb = mask.Lock();

        unsafe
        {
            byte* basePtr = (byte*)fb.Address;
            int stride = fb.RowBytes;
            int w = size.Width;
            int h = size.Height;

            for (int y = 0; y < h; y++)
            {
                var row = new Span<byte>(basePtr + y * stride, w * 4);
                row.Clear();
            }

            foreach (var r in faces)
            {
                Rect padded = FaceBlurGeometry.GetPaddedRect(r, w, h);
                int x0 = Math.Clamp((int)Math.Floor(padded.X), 0, Math.Max(0, w - 1));
                int y0 = Math.Clamp((int)Math.Floor(padded.Y), 0, Math.Max(0, h - 1));
                int x1 = Math.Clamp((int)Math.Ceiling(padded.Right), 0, w);
                int y1 = Math.Clamp((int)Math.Ceiling(padded.Bottom), 0, h);

                double strength = 1.0;

                double cx = (x0 + x1 - 1) / 2.0;
                double cy = (y0 + y1 - 1) / 2.0;
                double rx = Math.Max(1.0, (x1 - x0) / 2.0);
                double ry = Math.Max(1.0, (y1 - y0) / 2.0);
                double rx2 = rx * rx;
                double ry2 = ry * ry;
                double inner = 1.0 - FaceBlurGeometry.SoftEdgeRatio;
                if (inner < 0.0)
                    inner = 0.0;
                double inner2 = inner * inner;
                bool softEdge = FaceBlurGeometry.SoftEdgeRatio > 0.0 && inner2 < 0.999;

                for (int y = y0; y < y1; y++)
                {
                    byte* row = basePtr + y * stride;
                    double dy = y - cy;
                    double dy2 = dy * dy;
                    for (int x = x0; x < x1; x++)
                    {
                        double dx = x - cx;
                        double d2 = (dx * dx) / rx2 + dy2 / ry2;
                        if (d2 > 1.0)
                            continue;

                        byte* p = row + x * 4;
                        byte alpha;
                        if (!softEdge || d2 <= inner2)
                        {
                            alpha = 255;
                        }
                        else
                        {
                            double t = (d2 - inner2) / (1.0 - inner2);
                            if (t < 0.0) t = 0.0;
                            if (t > 1.0) t = 1.0;
                            t = t * t * (3.0 - 2.0 * t);
                            alpha = (byte)Math.Round((1.0 - t) * 255.0);
                        }

                        int scaled = (int)Math.Round(alpha * strength);
                        if (scaled <= p[3])
                            continue;

                        byte outA = (byte)Math.Clamp(scaled, 0, 255);
                        p[0] = outA;
                        p[1] = outA;
                        p[2] = outA;
                        p[3] = outA;
                    }
                }
            }
        }
    }
}
}
