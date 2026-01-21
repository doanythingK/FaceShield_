using Avalonia;
using Avalonia.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FaceShield.Services.Video.Session;

public sealed class ThumbnailCache : IDisposable
{
    private readonly Dictionary<int, WriteableBitmap> _cache;
    private readonly int _step;
    private bool _disposed;

    public ThumbnailCache(Dictionary<int, WriteableBitmap> cache, int step)
    {
        _cache = cache;
        _step = step;
    }

    public WriteableBitmap GetNearest(int frameIndex)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ThumbnailCache));
        if (_cache.Count == 0)
            throw new InvalidOperationException("Thumbnail cache is empty.");

        // 🔥 핵심: 반올림 기반 매칭으로 잘못된 프레임 표시 방지
        int key = (int)Math.Round(frameIndex / (double)_step) * _step;

        if (_cache.TryGetValue(key, out var bmp))
            return bmp;

        // 🔥 가장 가까운 키 찾기 (보정)
        var nearestKey = _cache.Keys.OrderBy(k => Math.Abs(k - frameIndex)).First();
        return _cache[nearestKey];
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        foreach (var bmp in _cache.Values)
        {
            try { bmp.Dispose(); }
            catch { }
        }
        _cache.Clear();
    }

    internal static WriteableBitmap CloneBitmap(WriteableBitmap src)
    {
        var dst = new WriteableBitmap(
            src.PixelSize,
            src.Dpi,
            Avalonia.Platform.PixelFormat.Bgra8888,
            Avalonia.Platform.AlphaFormat.Premul);

        using var sfb = src.Lock();
        using var dfb = dst.Lock();

        unsafe
        {
            int h = src.PixelSize.Height;
            int copy = Math.Min(sfb.RowBytes, dfb.RowBytes);
            byte* s = (byte*)sfb.Address;
            byte* d = (byte*)dfb.Address;

            for (int y = 0; y < h; y++)
            {
                Buffer.MemoryCopy(
                    s + y * sfb.RowBytes,
                    d + y * dfb.RowBytes,
                    dfb.RowBytes,
                    copy);
            }
        }

        return dst;
    }
}
