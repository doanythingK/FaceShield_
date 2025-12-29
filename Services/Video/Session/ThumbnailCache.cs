using Avalonia.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FaceShield.Services.Video.Session;

public sealed class ThumbnailCache
{
    private readonly Dictionary<int, WriteableBitmap> _cache;
    private readonly int _step;

    public ThumbnailCache(Dictionary<int, WriteableBitmap> cache, int step)
    {
        _cache = cache;
        _step = step;
    }

    public WriteableBitmap GetNearest(int frameIndex)
    {
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
}
