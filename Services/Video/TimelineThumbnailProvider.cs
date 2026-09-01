using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System;
using System.Collections.Concurrent;
using System.Threading;

namespace FaceShield.Services.Video
{
    public sealed class TimelineThumbnailProvider : IDisposable
    {
        private readonly object _sync = new();
        private readonly FfFrameExtractor _extractor;
        private readonly int _thumbWidth;
        private readonly int _thumbHeight;
        private readonly int _maxCacheEntries;
        private readonly ConcurrentDictionary<int, WriteableBitmap> _cache = new();
        private readonly ConcurrentDictionary<int, long> _cacheAccess = new();
        private long _cacheAccessClock;
        private bool _disposed;

        public TimelineThumbnailProvider(
            string videoPath,
            int thumbWidth = 160,
            int thumbHeight = 90,
            int maxCacheEntries = 256)
        {
            if (string.IsNullOrWhiteSpace(videoPath))
                throw new ArgumentException("Video path is required.", nameof(videoPath));

            _thumbWidth = Math.Max(1, thumbWidth);
            _thumbHeight = Math.Max(1, thumbHeight);
            _maxCacheEntries = Math.Max(16, maxCacheEntries);
            _extractor = new FfFrameExtractor(videoPath, enableHardware: false);
        }

        public WriteableBitmap? GetThumbnail(int frameIndex)
        {
            if (frameIndex < 0)
                return null;

            if (_cache.TryGetValue(frameIndex, out WriteableBitmap? cached))
            {
                TouchCacheEntry(frameIndex);
                return cached;
            }

            lock (_sync)
            {
                if (_disposed)
                    return null;

                if (_cache.TryGetValue(frameIndex, out cached))
                {
                    TouchCacheEntry(frameIndex);
                    return cached;
                }

                WriteableBitmap? bitmap = _extractor.GetTimelineThumbnailByFrameIndexScaled(
                    frameIndex,
                    _thumbWidth,
                    _thumbHeight,
                    CancellationToken.None);
                if (bitmap == null)
                    return null;

                _cache[frameIndex] = bitmap;
                TouchCacheEntry(frameIndex);
                TrimCacheIfNeeded(frameIndex);
                return bitmap;
            }
        }

        public WriteableBitmap? GetThumbnailCopy(int frameIndex)
        {
            if (frameIndex < 0)
                return null;

            lock (_sync)
            {
                if (_disposed)
                    return null;

                WriteableBitmap? cached = GetThumbnail(frameIndex);
                return cached == null ? null : CloneBitmap(cached);
            }
        }

        public bool TryGetCachedThumbnail(int frameIndex, out WriteableBitmap? bitmap)
        {
            bitmap = null;
            if (frameIndex < 0 || _disposed)
                return false;

            if (!_cache.TryGetValue(frameIndex, out bitmap))
                return false;

            TouchCacheEntry(frameIndex);
            return true;
        }

        private void TouchCacheEntry(int frameIndex)
        {
            long access = Interlocked.Increment(ref _cacheAccessClock);
            _cacheAccess[frameIndex] = access;
        }

        private void TrimCacheIfNeeded(int protectedFrameIndex)
        {
            while (_cache.Count > _maxCacheEntries)
            {
                int evictionKey = -1;
                long oldestAccess = long.MaxValue;

                foreach (var entry in _cacheAccess)
                {
                    if (entry.Key == protectedFrameIndex)
                        continue;

                    if (entry.Value < oldestAccess)
                    {
                        oldestAccess = entry.Value;
                        evictionKey = entry.Key;
                    }
                }

                if (evictionKey < 0)
                    break;

                _cacheAccess.TryRemove(evictionKey, out _);
                if (_cache.TryRemove(evictionKey, out WriteableBitmap? evicted))
                    DisposeOnUiThread(evicted);
            }
        }

        private static WriteableBitmap CloneBitmap(WriteableBitmap source)
        {
            var copy = new WriteableBitmap(
                source.PixelSize,
                source.Dpi,
                Avalonia.Platform.PixelFormat.Bgra8888,
                Avalonia.Platform.AlphaFormat.Premul);

            using var sourceBuffer = source.Lock();
            using var copyBuffer = copy.Lock();
            int rows = Math.Min(sourceBuffer.Size.Height, copyBuffer.Size.Height);
            int bytesPerRow = Math.Min(sourceBuffer.RowBytes, copyBuffer.RowBytes);

            unsafe
            {
                byte* src = (byte*)sourceBuffer.Address;
                byte* dst = (byte*)copyBuffer.Address;
                for (int y = 0; y < rows; y++)
                {
                    Buffer.MemoryCopy(
                        src + y * sourceBuffer.RowBytes,
                        dst + y * copyBuffer.RowBytes,
                        copyBuffer.RowBytes,
                        bytesPerRow);
                }
            }

            return copy;
        }

        private static void DisposeOnUiThread(WriteableBitmap bitmap)
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                bitmap.Dispose();
                return;
            }

            Dispatcher.UIThread.Post(bitmap.Dispose);
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;

                _disposed = true;
                _extractor.Dispose();

                foreach (var entry in _cache)
                    entry.Value.Dispose();

                _cache.Clear();
                _cacheAccess.Clear();
            }
        }
    }
}
