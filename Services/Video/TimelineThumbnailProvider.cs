using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace FaceShield.Services.Video
{
    public sealed class TimelineThumbnailProvider : IDisposable
    {
        private readonly object _sync = new();
        private readonly FfFrameExtractor _extractor;
        private readonly bool _ownsExtractor;
        private readonly int _thumbWidth;
        private readonly int _thumbHeight;
        private readonly int _maxCacheEntries;
        private readonly ConcurrentDictionary<long, WriteableBitmap> _cache = new();
        private readonly ConcurrentDictionary<long, long> _cacheAccess = new();
        private readonly CancellationTokenSource _lifetimeCts = new();
        private readonly object _operationStateSync = new();
        private CancellationTokenSource _operationCts = new();
        private long _cacheAccessClock;
        private int _operationsSuspended;
        private int _disposeStarted;
        private bool _disposed;

        public bool OperationsSuspended =>
            Volatile.Read(ref _operationsSuspended) != 0;

        public TimelineThumbnailProvider(
            string videoPath,
            int thumbWidth = 160,
            int thumbHeight = 90,
            int maxCacheEntries = 256)
            : this(
                new FfFrameExtractor(videoPath, enableHardware: false),
                thumbWidth,
                thumbHeight,
                maxCacheEntries,
                ownsExtractor: true)
        {
        }

        internal TimelineThumbnailProvider(
            FfFrameExtractor extractor,
            int thumbWidth = 160,
            int thumbHeight = 90,
            int maxCacheEntries = 256,
            bool ownsExtractor = false)
        {
            _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
            _ownsExtractor = ownsExtractor;
            _thumbWidth = Math.Max(1, thumbWidth);
            _thumbHeight = Math.Max(1, thumbHeight);
            _maxCacheEntries = Math.Max(16, maxCacheEntries);
        }

        public WriteableBitmap? GetThumbnail(int frameIndex)
            => GetThumbnail(frameIndex, CancellationToken.None);

        public WriteableBitmap? GetThumbnail(int frameIndex, CancellationToken cancellationToken)
        {
            if (frameIndex < 0)
                return null;

            long cacheKey = FrameCacheKey(frameIndex);
            return GetOrCreate(
                cacheKey,
                token => _extractor.GetTimelineThumbnailByFrameIndexScaled(
                    frameIndex,
                    _thumbWidth,
                    _thumbHeight,
                    token),
                cancellationToken);
        }

        public WriteableBitmap? GetThumbnailAtTime(
            double timestampSeconds,
            CancellationToken cancellationToken = default)
        {
            if (!double.IsFinite(timestampSeconds) || timestampSeconds < 0)
                return null;

            long cacheKey = TimeCacheKey(timestampSeconds);
            return GetOrCreate(
                cacheKey,
                token => _extractor.GetTimelineThumbnailAtTimestampScaled(
                    timestampSeconds,
                    _thumbWidth,
                    _thumbHeight,
                    token),
                cancellationToken);
        }

        public WriteableBitmap? GetThumbnailCopy(int frameIndex)
            => GetThumbnailCopy(frameIndex, CancellationToken.None);

        public WriteableBitmap? GetThumbnailCopy(
            int frameIndex,
            CancellationToken cancellationToken)
        {
            if (frameIndex < 0 || cancellationToken.IsCancellationRequested)
                return null;

            long cacheKey = FrameCacheKey(frameIndex);
            using var linked = CreateLinkedTokenSource(cancellationToken);
            lock (_sync)
            {
                if (_disposed ||
                    OperationsSuspended ||
                    linked.Token.IsCancellationRequested)
                {
                    return null;
                }

                WriteableBitmap? cached = GetOrCreateLocked(
                    cacheKey,
                    token => _extractor.GetTimelineThumbnailByFrameIndexScaled(
                        frameIndex,
                        _thumbWidth,
                        _thumbHeight,
                        token),
                    linked.Token);
                return cached == null ? null : CloneBitmap(cached);
            }
        }

        public bool TryGetCachedThumbnail(int frameIndex, out WriteableBitmap? bitmap)
            => TryGetCached(FrameCacheKey(frameIndex), out bitmap);

        public bool TryGetCachedThumbnailAtTime(
            double timestampSeconds,
            out WriteableBitmap? bitmap)
        {
            bitmap = null;
            if (!double.IsFinite(timestampSeconds) || timestampSeconds < 0)
                return false;

            return TryGetCached(TimeCacheKey(timestampSeconds), out bitmap);
        }

        public bool TryGetFrameTimestampSeconds(
            int frameIndex,
            out double timestampSeconds)
            => _extractor.TryGetCachedFrameTimestampSeconds(
                frameIndex,
                out timestampSeconds);

        public bool TryGetFrameIndexAtTimestamp(
            double timestampSeconds,
            out int frameIndex)
            => _extractor.TryGetCachedFrameIndexAtTimestamp(
                timestampSeconds,
                out frameIndex);

        public bool TryGetDecodedTimelineExtentSeconds(
            out double extentSeconds,
            out bool isComplete)
            => _extractor.TryGetDecodedTimelineExtentSeconds(
                out extentSeconds,
                out isComplete);

        public bool TryResolveFrameTimestampSeconds(
            int frameIndex,
            CancellationToken cancellationToken,
            out double timestampSeconds)
        {
            timestampSeconds = double.NaN;
            if (cancellationToken.IsCancellationRequested)
                return false;
            if (_extractor.TryGetCachedFrameTimestampSeconds(
                    frameIndex,
                    out timestampSeconds))
            {
                return true;
            }
            if (_disposed || OperationsSuspended)
                return false;

            using var linked = CreateLinkedTokenSource(cancellationToken);
            lock (_sync)
            {
                if (_disposed ||
                    OperationsSuspended ||
                    linked.Token.IsCancellationRequested)
                {
                    return false;
                }

                return _extractor.TryResolveFrameTimestampSeconds(
                    frameIndex,
                    linked.Token,
                    out timestampSeconds);
            }
        }

        public bool TryResolveFrameIndexAtTimestamp(
            double timestampSeconds,
            CancellationToken cancellationToken,
            out int frameIndex)
        {
            frameIndex = -1;
            if (cancellationToken.IsCancellationRequested)
                return false;
            if (_extractor.TryGetCachedFrameIndexAtTimestamp(
                    timestampSeconds,
                    out frameIndex))
            {
                return true;
            }
            if (_disposed || OperationsSuspended)
                return false;

            using var linked = CreateLinkedTokenSource(cancellationToken);
            lock (_sync)
            {
                if (_disposed ||
                    OperationsSuspended ||
                    linked.Token.IsCancellationRequested)
                {
                    return false;
                }

                return _extractor.TryResolveFrameIndexAtTimestamp(
                    timestampSeconds,
                    linked.Token,
                    out frameIndex);
            }
        }

        public async Task SuspendOperationsAndWaitAsync()
        {
            CancellationTokenSource previous;
            lock (_operationStateSync)
            {
                if (_disposed)
                    return;

                Volatile.Write(ref _operationsSuspended, 1);
                previous = _operationCts;
                _operationCts = new CancellationTokenSource();
            }

            try
            {
                previous.Cancel();
                await Task.Run(() =>
                {
                    lock (_sync)
                    {
                    }
                });
            }
            finally
            {
                previous.Dispose();
            }
        }

        public void ResumeOperations()
        {
            if (_disposed)
                return;

            Volatile.Write(ref _operationsSuspended, 0);
        }

        private WriteableBitmap? GetOrCreate(
            long cacheKey,
            Func<CancellationToken, WriteableBitmap?> factory,
            CancellationToken cancellationToken)
        {
            if (_disposed || cancellationToken.IsCancellationRequested)
                return null;

            if (_cache.TryGetValue(cacheKey, out WriteableBitmap? cached))
            {
                TouchCacheEntry(cacheKey);
                return cached;
            }

            if (OperationsSuspended)
                return null;

            using var linked = CreateLinkedTokenSource(cancellationToken);
            lock (_sync)
            {
                if (_disposed ||
                    OperationsSuspended ||
                    linked.Token.IsCancellationRequested)
                {
                    return null;
                }

                return GetOrCreateLocked(cacheKey, factory, linked.Token);
            }
        }

        private WriteableBitmap? GetOrCreateLocked(
            long cacheKey,
            Func<CancellationToken, WriteableBitmap?> factory,
            CancellationToken cancellationToken)
        {
            if (_cache.TryGetValue(cacheKey, out WriteableBitmap? cached))
            {
                TouchCacheEntry(cacheKey);
                return cached;
            }

            WriteableBitmap? bitmap = factory(cancellationToken);
            if (bitmap == null || cancellationToken.IsCancellationRequested)
            {
                bitmap?.Dispose();
                return null;
            }

            _cache[cacheKey] = bitmap;
            TouchCacheEntry(cacheKey);
            TrimCacheIfNeeded(cacheKey);
            return bitmap;
        }

        private bool TryGetCached(long cacheKey, out WriteableBitmap? bitmap)
        {
            bitmap = null;
            if (_disposed)
                return false;

            if (!_cache.TryGetValue(cacheKey, out bitmap))
                return false;

            TouchCacheEntry(cacheKey);
            return true;
        }

        private CancellationTokenSource CreateLinkedTokenSource(
            CancellationToken cancellationToken)
        {
            lock (_operationStateSync)
            {
                return cancellationToken.CanBeCanceled
                    ? CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken,
                        _lifetimeCts.Token,
                        _operationCts.Token)
                    : CancellationTokenSource.CreateLinkedTokenSource(
                        _lifetimeCts.Token,
                        _operationCts.Token);
            }
        }

        private static long FrameCacheKey(int frameIndex)
            => ((long)Math.Max(0, frameIndex)) << 1;

        private static long TimeCacheKey(double timestampSeconds)
        {
            long milliseconds = Math.Max(
                0,
                (long)Math.Round(timestampSeconds * 1000.0));
            return (milliseconds << 1) | 1L;
        }

        private void TouchCacheEntry(long cacheKey)
        {
            long access = Interlocked.Increment(ref _cacheAccessClock);
            _cacheAccess[cacheKey] = access;
        }

        private void TrimCacheIfNeeded(long protectedCacheKey)
        {
            while (_cache.Count > _maxCacheEntries)
            {
                long evictionKey = -1;
                long oldestAccess = long.MaxValue;

                foreach (var entry in _cacheAccess)
                {
                    if (entry.Key == protectedCacheKey)
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
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
                return;

            _lifetimeCts.Cancel();
            lock (_operationStateSync)
            {
                Volatile.Write(ref _operationsSuspended, 1);
                _operationCts.Cancel();
            }
            lock (_sync)
            {
                _disposed = true;
                if (_ownsExtractor)
                    _extractor.Dispose();

                foreach (var entry in _cache)
                    entry.Value.Dispose();

                _cache.Clear();
                _operationCts.Dispose();
                _cacheAccess.Clear();
            }

            _lifetimeCts.Dispose();
        }
    }
}
