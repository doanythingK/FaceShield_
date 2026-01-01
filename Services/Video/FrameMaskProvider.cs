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

    public void SetFaceRects(int frameIndex, IReadOnlyList<Rect> faces, PixelSize size)
    {
        if (faces == null || faces.Count == 0 || size.Width <= 0 || size.Height <= 0)
        {
            _faceMasks.TryRemove(frameIndex, out _);
            return;
        }

        _faceMasks[frameIndex] = new FaceMaskData(size, faces.ToArray());
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

    public IReadOnlyCollection<KeyValuePair<int, WriteableBitmap>> GetMaskEntries()
        => _masks.ToArray();

    public void Clear()
    {
        _masks.Clear();
        _faceMasks.Clear();
    }

    public readonly record struct FaceMaskData(PixelSize Size, IReadOnlyList<Rect> Faces);

    public static WriteableBitmap CreateMaskFromFaceRects(PixelSize size, IReadOnlyList<Rect> faces)
    {
        var mask = new WriteableBitmap(
            size,
            new Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888,
            Avalonia.Platform.AlphaFormat.Premul);

        using var fb = mask.Lock();

        unsafe
        {
            byte* basePtr = (byte*)fb.Address;
            int stride = fb.RowBytes;
            int w = size.Width;
            int h = size.Height;

            foreach (var r in faces)
            {
                int x0 = Math.Clamp((int)Math.Floor(r.X), 0, Math.Max(0, w - 1));
                int y0 = Math.Clamp((int)Math.Floor(r.Y), 0, Math.Max(0, h - 1));
                int x1 = Math.Clamp((int)Math.Ceiling(r.X + r.Width), 0, w);
                int y1 = Math.Clamp((int)Math.Ceiling(r.Y + r.Height), 0, h);

                double cx = (x0 + x1 - 1) / 2.0;
                double cy = (y0 + y1 - 1) / 2.0;
                double rx = Math.Max(1.0, (x1 - x0) / 2.0);
                double ry = Math.Max(1.0, (y1 - y0) / 2.0);
                double rx2 = rx * rx;
                double ry2 = ry * ry;

                for (int y = y0; y < y1; y++)
                {
                    byte* row = basePtr + y * stride;
                    double dy = y - cy;
                    double dy2 = dy * dy;
                    for (int x = x0; x < x1; x++)
                    {
                        double dx = x - cx;
                        if ((dx * dx) / rx2 + dy2 / ry2 > 1.0)
                            continue;

                        byte* p = row + x * 4;
                        p[0] = 255;
                        p[1] = 255;
                        p[2] = 255;
                        p[3] = 255;
                    }
                }
            }
        }

        return mask;
    }
}
}
