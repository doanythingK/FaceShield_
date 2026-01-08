using Avalonia;
using Avalonia.Media.Imaging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace FaceShield.Services.Video
{
public sealed class FrameMaskProvider : IFrameMaskProvider
{
    private readonly ConcurrentDictionary<int, WriteableBitmap> _masks = new();
    private readonly ConcurrentDictionary<int, FaceMaskData> _faceMasks = new();

    public void SetMask(int frameIndex, WriteableBitmap mask)
    {
        _masks[frameIndex] = mask;
        _faceMasks.TryRemove(frameIndex, out _);
    }

    public void SetFaceRects(int frameIndex, IReadOnlyList<Rect> faces, PixelSize size, float? minConfidence = null)
    {
        if (faces == null || faces.Count == 0 || size.Width <= 0 || size.Height <= 0)
        {
            _faceMasks.TryRemove(frameIndex, out _);
            return;
        }

        _faceMasks[frameIndex] = new FaceMaskData(size, faces.ToArray(), minConfidence);
    }

    public void SetFaceRects(int frameIndex, Rect[] faces, PixelSize size, float? minConfidence = null)
    {
        if (faces == null || faces.Length == 0 || size.Width <= 0 || size.Height <= 0)
        {
            _faceMasks.TryRemove(frameIndex, out _);
            return;
        }

        _faceMasks[frameIndex] = new FaceMaskData(size, faces, minConfidence);
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

    public IReadOnlyCollection<KeyValuePair<int, WriteableBitmap>> GetMaskEntries()
        => _masks.ToArray();

    public void Clear()
    {
        _masks.Clear();
        _faceMasks.Clear();
    }

    public readonly record struct FaceMaskData(PixelSize Size, IReadOnlyList<Rect> Faces, float? MinConfidence);

    public static WriteableBitmap CreateMaskFromFaceRects(PixelSize size, IReadOnlyList<Rect> faces)
    {
        var mask = new WriteableBitmap(
            size,
            new Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888,
            Avalonia.Platform.AlphaFormat.Premul);

        const double SoftEdgeRatio = 0.35;
        const double R1 = 0.01;
        const double R2 = 0.03;
        const double R3 = 0.05;

        using var fb = mask.Lock();

        unsafe
        {
            byte* basePtr = (byte*)fb.Address;
            int stride = fb.RowBytes;
            int w = size.Width;
            int h = size.Height;

            foreach (var r in faces)
            {
                double padX = Math.Max(6.0, r.Width * 0.15);
                double padY = Math.Max(6.0, r.Height * 0.25);
                double rx0 = r.X - padX;
                double ry0 = r.Y - padY;
                double rx1 = r.X + r.Width + padX;
                double ry1 = r.Y + r.Height + padY;

                int x0 = Math.Clamp((int)Math.Floor(rx0), 0, Math.Max(0, w - 1));
                int y0 = Math.Clamp((int)Math.Floor(ry0), 0, Math.Max(0, h - 1));
                int x1 = Math.Clamp((int)Math.Ceiling(rx1), 0, w);
                int y1 = Math.Clamp((int)Math.Ceiling(ry1), 0, h);

                double strength = 1.0;

                double cx = (x0 + x1 - 1) / 2.0;
                double cy = (y0 + y1 - 1) / 2.0;
                double rx = Math.Max(1.0, (x1 - x0) / 2.0);
                double ry = Math.Max(1.0, (y1 - y0) / 2.0);
                double rx2 = rx * rx;
                double ry2 = ry * ry;
                double inner = 1.0 - SoftEdgeRatio;
                if (inner < 0.0)
                    inner = 0.0;
                double inner2 = inner * inner;
                bool softEdge = SoftEdgeRatio > 0.0 && inner2 < 0.999;

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

        return mask;
    }
}
}
