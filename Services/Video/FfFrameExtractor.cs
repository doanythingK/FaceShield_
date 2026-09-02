using Avalonia;
using Avalonia.Media.Imaging;
using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace FaceShield.Services.Video
{
    public unsafe sealed class FfFrameExtractor : IDisposable
    {
        private string _decodeStatus = "디코딩: 확인 중";
        private string? _decodeError;
        private string? _decodeDiagnostics;
        private bool _hardwareTransferFailed;
        private static readonly object _hwFormatLock = new();
        private static readonly Dictionary<IntPtr, AVPixelFormat> _hwFormatByDecoder = new();
        private static readonly Dictionary<IntPtr, WeakReference<FfFrameExtractor>> _hwOwnerByDecoder = new();
        private static readonly object _timelineCacheLock = new();
        private static readonly Dictionary<FrameTimelineCacheKey, DecodedFrameTimeline> _timelineCache = new();
        private const int MaxTimelineCacheEntries = 8;
        private const int MaxCachedTimelineFramesPerVideo = 500_000;
        private const int MaxCachedTimelineFramesTotal = 1_000_000;

        private readonly record struct FrameTimelineCacheKey(
            string NormalizedPath,
            long FileLength,
            long LastWriteTimeUtcTicks);

        private readonly record struct DecodedFrameTimelineEntry(
            long PresentationTimestamp,
            int TimestampOccurrence)
        {
            public bool HasPresentationTimestamp => PresentationTimestamp != ffmpeg.AV_NOPTS_VALUE;
        }

        private sealed class DecodedFrameTimeline
        {
            public object SyncRoot { get; } = new();
            public List<DecodedFrameTimelineEntry> Entries { get; } = new();
            public bool IsComplete { get; set; }
            public bool IsReliable { get; set; } = true;
            public bool SupportsExactTimestampSeek { get; set; } = true;
            public bool CapacityReached { get; set; }
            public long LastValidPresentationTimestamp { get; set; } = ffmpeg.AV_NOPTS_VALUE;
            public int EntryCountSnapshot;
            public int IsCacheResident = 1;
            public int LiveOwnerCount;
            public long LastAccessTicks { get; set; } = DateTime.UtcNow.Ticks;
        }

        private enum SequentialOrdinalMode
        {
            ResolveIndexedTimestamp,
            DecodeFromBeginning,
            MatchIndexedTimestamp
        }

        public string DecodeStatus
        {
            get
            {
                lock (_sync)
                    return _decodeStatus;
            }
        }

        public string? DecodeError
        {
            get
            {
                lock (_sync)
                    return _decodeError;
            }
        }

        public string? DecodeDiagnostics
        {
            get
            {
                lock (_sync)
                    return _decodeDiagnostics;
            }
        }

        public bool HardwareTransferFailed
        {
            get
            {
                lock (_sync)
                    return _hardwareTransferFailed;
            }
        }

        private void UpdateDecodeStatus(string status, string? error = null)
        {
            lock (_sync)
            {
                _decodeStatus = status;
                _decodeError = error;
                if (!string.IsNullOrWhiteSpace(status) &&
                    status.Contains("HW 프레임 전송 실패", StringComparison.Ordinal))
                {
                    _hardwareTransferFailed = true;
                }
            }
        }

        private void UpdateDecodeDiagnostics(string diagnostics)
        {
            lock (_sync)
                _decodeDiagnostics = diagnostics;
        }

        public readonly struct BgraFrame
        {
            public IntPtr Data { get; }
            public int Stride { get; }
            public int Width { get; }
            public int Height { get; }

            public BgraFrame(IntPtr data, int stride, int width, int height)
            {
                Data = data;
                Stride = stride;
                Width = width;
                Height = height;
            }
        }

        private readonly object _sync = new();

        private AVFormatContext* _fmt;
        private AVCodecContext* _dec;
        private SwsContext* _sws;
        private SwsContext* _swsScaled;
        private int _videoStreamIndex = -1;
        private AVBufferRef* _hwDeviceCtx;
        private AVPixelFormat _hwPixelFormat = AVPixelFormat.AV_PIX_FMT_NONE;
        private AVCodecContext_get_format? _getFormatManaged;
        private bool _hwDeviceInitialized;

        private AVRational _timeBase;
        private double _fps;
        private readonly string _videoPath;
        private readonly DecodedFrameTimeline _decodedFrameTimeline;

        private bool _disposed;
        private bool _sequentialActive;
        private bool _sequentialStarted;
        private int _sequentialIndex;
        private int _sequentialRequestedIndex;
        private SequentialOrdinalMode _sequentialOrdinalMode;
        private double? _pendingSequentialTimestampSeconds;
        private long _sequentialTargetPts;
        private bool _sequentialDrainSent;
        private bool _sequentialReachedEndOfStream;
        private bool _sequentialCancelled;
        private string? _sequentialDecodeError;
        private double _lastDecodedTimestampSeconds = double.NaN;
        private string _lastDecodedTimestampSource = "none";

        private AVFrame* _bgraReusable;
        private int _bgraReusableWidth;
        private int _bgraReusableHeight;
        private AVFrame* _bgraScaledReusable;
        private int _bgraScaledWidth;
        private int _bgraScaledHeight;
        private AVPacket* _sequentialPacketReusable;
        private AVFrame* _sequentialDecodedFrameReusable;

        private AVFormatContext* _ordinalFormat;
        private AVCodecContext* _ordinalDecoder;
        private AVPacket* _ordinalPacket;
        private AVFrame* _ordinalFrame;
        private int _ordinalVideoStreamIndex = -1;
        private int _ordinalNextFrameIndex;
        private bool _ordinalDrainSent;
        private bool _ordinalReachedEndOfStream;
        private bool _ordinalDecoderFailed;
        private int _timelineOwnerReleased;

        public FfFrameExtractor(string videoPath, bool enableHardware = true)
        {
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);

            _videoPath = Path.GetFullPath(videoPath);
            _decodedFrameTimeline = GetOrCreateDecodedFrameTimeline(_videoPath);

            try
            {
                // open input (AVFormatContext**)
                fixed (AVFormatContext** pFmt = &_fmt)
                {
                    int r = ffmpeg.avformat_open_input(pFmt, _videoPath, null, null);
                    FFmpegErrorHelper.ThrowIfError(r, $"Failed to open video: {_videoPath}");
                }

                int streamInfo = ffmpeg.avformat_find_stream_info(_fmt, null);
                FFmpegErrorHelper.ThrowIfError(streamInfo, $"Failed to read stream info: {_videoPath}");

                _videoStreamIndex = FFmpegStreamSelection.FindPrimaryVideoStreamIndex(_fmt);
                if (_videoStreamIndex < 0)
                    throw new InvalidOperationException("video stream not found");

                AVStream* stream = _fmt->streams[_videoStreamIndex];

                _timeBase = stream->time_base;

                double fpsValue =
                    stream->avg_frame_rate.num != 0
                        ? ffmpeg.av_q2d(stream->avg_frame_rate)
                        : stream->r_frame_rate.num != 0
                            ? ffmpeg.av_q2d(stream->r_frame_rate)
                            : 30.0;

                _fps = Math.Max(1.0, fpsValue);

                AVCodec* codec = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
                if (codec == null)
                    throw new InvalidOperationException("decoder not found");

                _dec = ffmpeg.avcodec_alloc_context3(codec);
                if (_dec == null)
                    throw new InvalidOperationException("avcodec_alloc_context3 failed");

                int parResult = ffmpeg.avcodec_parameters_to_context(_dec, stream->codecpar);
                FFmpegErrorHelper.ThrowIfError(parResult, "Failed to apply codec parameters");

                if (enableHardware)
                    TryInitializeHardwareDevice();
                else
                    UpdateDecodeStatus("디코딩: HW 비활성화");

                int openResult = ffmpeg.avcodec_open2(_dec, codec, null);
                FFmpegErrorHelper.ThrowIfError(openResult, "Failed to open decoder");
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private static DecodedFrameTimeline GetOrCreateDecodedFrameTimeline(string videoPath)
        {
            FrameTimelineCacheKey key = CreateFrameTimelineCacheKey(videoPath);
            lock (_timelineCacheLock)
            {
                var staleKeys = new List<FrameTimelineCacheKey>();
                foreach (FrameTimelineCacheKey existingKey in _timelineCache.Keys)
                {
                    if (string.Equals(
                            existingKey.NormalizedPath,
                            key.NormalizedPath,
                            StringComparison.Ordinal) &&
                        existingKey != key)
                    {
                        staleKeys.Add(existingKey);
                    }
                }

                foreach (FrameTimelineCacheKey staleKey in staleKeys)
                {
                    if (_timelineCache.TryGetValue(staleKey, out DecodedFrameTimeline? staleTimeline) &&
                        Volatile.Read(ref staleTimeline.LiveOwnerCount) == 0 &&
                        _timelineCache.Remove(staleKey))
                    {
                        Volatile.Write(ref staleTimeline.IsCacheResident, 0);
                    }
                }

                if (_timelineCache.TryGetValue(key, out DecodedFrameTimeline? existing))
                {
                    existing.LastAccessTicks = DateTime.UtcNow.Ticks;
                    Interlocked.Increment(ref existing.LiveOwnerCount);
                    return existing;
                }

                var created = new DecodedFrameTimeline
                {
                    LiveOwnerCount = 1
                };
                _timelineCache[key] = created;
                TrimDecodedFrameTimelineCache(created);
                return created;
            }
        }

        private static FrameTimelineCacheKey CreateFrameTimelineCacheKey(string videoPath)
        {
            string normalizedPath = Path.GetFullPath(videoPath);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                normalizedPath = normalizedPath.ToUpperInvariant();

            var file = new FileInfo(videoPath);
            file.Refresh();
            return new FrameTimelineCacheKey(
                normalizedPath,
                file.Exists ? file.Length : -1,
                file.Exists ? file.LastWriteTimeUtc.Ticks : 0);
        }

        private static void TrimDecodedFrameTimelineCache(DecodedFrameTimeline activeTimeline)
        {
            lock (_timelineCacheLock)
            {
                int totalFrames = GetResidentTimelineFrameCountLocked();

                while (_timelineCache.Count > MaxTimelineCacheEntries ||
                       totalFrames > MaxCachedTimelineFramesTotal)
                {
                    FrameTimelineCacheKey? evictionKey = null;
                    DecodedFrameTimeline? evictionTimeline = null;
                    foreach (var pair in _timelineCache)
                    {
                        if (ReferenceEquals(pair.Value, activeTimeline) ||
                            Volatile.Read(ref pair.Value.LiveOwnerCount) != 0)
                        {
                            continue;
                        }

                        if (evictionTimeline == null ||
                            pair.Value.LastAccessTicks < evictionTimeline.LastAccessTicks)
                        {
                            evictionKey = pair.Key;
                            evictionTimeline = pair.Value;
                        }
                    }

                    if (!evictionKey.HasValue || evictionTimeline == null)
                        break;

                    _timelineCache.Remove(evictionKey.Value);
                    Volatile.Write(ref evictionTimeline.IsCacheResident, 0);
                    totalFrames -= Math.Max(
                        0,
                        Volatile.Read(ref evictionTimeline.EntryCountSnapshot));
                }
            }
        }

        private static int GetResidentTimelineFrameCountLocked()
        {
            long totalFrames = 0;
            foreach (DecodedFrameTimeline timeline in _timelineCache.Values)
            {
                totalFrames += Math.Max(
                    0,
                    Volatile.Read(ref timeline.EntryCountSnapshot));
                if (totalFrames >= int.MaxValue)
                    return int.MaxValue;
            }

            return (int)totalFrames;
        }

        private static void ReleaseDecodedFrameTimeline(
            DecodedFrameTimeline timeline)
        {
            lock (_timelineCacheLock)
            {
                if (timeline.LiveOwnerCount > 0)
                    timeline.LiveOwnerCount--;

                TrimDecodedFrameTimelineCache(timeline);
            }
        }

        /// <summary>
        /// 가장 최근에 순차 디코딩한 프레임의 표시 시각(초)입니다.
        /// best_effort_timestamp가 없을 때만 평균 FPS 기반 값을 사용합니다.
        /// </summary>
        public double LastDecodedTimestampSeconds
        {
            get
            {
                lock (_sync)
                    return _lastDecodedTimestampSeconds;
            }
        }

        /// <summary>
        /// Returns an already-decoded presentation timestamp for a frame ordinal.
        /// This never grows the shared decoded timeline.
        /// </summary>
        public bool TryGetCachedFrameTimestampSeconds(
            int frameIndex,
            out double timestampSeconds)
        {
            timestampSeconds = double.NaN;
            if (frameIndex < 0)
                return false;

            double timeBaseSeconds = ffmpeg.av_q2d(_timeBase);
            if (!double.IsFinite(timeBaseSeconds) || timeBaseSeconds <= 0)
                return false;

            lock (_decodedFrameTimeline.SyncRoot)
            {
                _decodedFrameTimeline.LastAccessTicks = DateTime.UtcNow.Ticks;
                if (!_decodedFrameTimeline.IsReliable ||
                    _decodedFrameTimeline.Entries.Count == 0 ||
                    frameIndex >= _decodedFrameTimeline.Entries.Count)
                {
                    return false;
                }

                DecodedFrameTimelineEntry origin = _decodedFrameTimeline.Entries[0];
                DecodedFrameTimelineEntry target = _decodedFrameTimeline.Entries[frameIndex];
                if (!origin.HasPresentationTimestamp || !target.HasPresentationTimestamp)
                    return false;

                double seconds =
                    (target.PresentationTimestamp - origin.PresentationTimestamp) *
                    timeBaseSeconds;
                if (!double.IsFinite(seconds))
                    return false;

                timestampSeconds = Math.Max(0, seconds);
                return true;
            }
        }

        /// <summary>
        /// Maps presentation time to an ordinal only when the existing decoded PTS cache
        /// already covers that time. This method never triggers ordinal indexing.
        /// </summary>
        public bool TryGetCachedFrameIndexAtTimestamp(
            double timestampSeconds,
            out int frameIndex)
        {
            frameIndex = -1;
            if (!double.IsFinite(timestampSeconds) || timestampSeconds < 0)
                return false;

            double timeBaseSeconds = ffmpeg.av_q2d(_timeBase);
            if (!double.IsFinite(timeBaseSeconds) || timeBaseSeconds <= 0)
                return false;

            lock (_decodedFrameTimeline.SyncRoot)
            {
                _decodedFrameTimeline.LastAccessTicks = DateTime.UtcNow.Ticks;
                int count = _decodedFrameTimeline.Entries.Count;
                if (!_decodedFrameTimeline.IsReliable ||
                    !_decodedFrameTimeline.SupportsExactTimestampSeek ||
                    count == 0)
                {
                    return false;
                }

                DecodedFrameTimelineEntry origin = _decodedFrameTimeline.Entries[0];
                DecodedFrameTimelineEntry last = _decodedFrameTimeline.Entries[count - 1];
                if (!origin.HasPresentationTimestamp || !last.HasPresentationTimestamp)
                    return false;

                double targetPtsDouble =
                    origin.PresentationTimestamp +
                    timestampSeconds / timeBaseSeconds;
                if (!double.IsFinite(targetPtsDouble) ||
                    targetPtsDouble < long.MinValue ||
                    targetPtsDouble > long.MaxValue)
                {
                    return false;
                }

                long targetPts = (long)Math.Round(targetPtsDouble);
                if (!_decodedFrameTimeline.IsComplete &&
                    targetPts > last.PresentationTimestamp)
                {
                    return false;
                }

                int lo = 0;
                int hi = count - 1;
                while (lo < hi)
                {
                    int mid = lo + (hi - lo) / 2;
                    DecodedFrameTimelineEntry entry = _decodedFrameTimeline.Entries[mid];
                    if (!entry.HasPresentationTimestamp)
                        return false;

                    if (entry.PresentationTimestamp < targetPts)
                        lo = mid + 1;
                    else
                        hi = mid;
                }

                int candidate = lo;
                if (candidate > 0)
                {
                    double currentDistance = Math.Abs(
                        (double)_decodedFrameTimeline.Entries[candidate].PresentationTimestamp -
                        targetPts);
                    double previousDistance = Math.Abs(
                        (double)_decodedFrameTimeline.Entries[candidate - 1].PresentationTimestamp -
                        targetPts);
                    if (previousDistance <= currentDistance)
                        candidate--;
                }

                frameIndex = candidate;
                return true;
            }
        }

        /// <summary>
        /// Resolves an ordinal to its decoded presentation timestamp. Unlike the cached
        /// lookup, this may extend the decoded-ordinal timeline and must run off the UI
        /// thread for uncached distant frames.
        /// </summary>
        public bool TryGetDecodedTimelineExtentSeconds(
            out double extentSeconds,
            out bool isComplete)
        {
            extentSeconds = 0;
            isComplete = false;

            double timeBaseSeconds = ffmpeg.av_q2d(_timeBase);
            if (!double.IsFinite(timeBaseSeconds) || timeBaseSeconds <= 0)
                return false;

            lock (_decodedFrameTimeline.SyncRoot)
            {
                if (!_decodedFrameTimeline.IsReliable ||
                    _decodedFrameTimeline.Entries.Count == 0)
                {
                    return false;
                }

                DecodedFrameTimelineEntry first = _decodedFrameTimeline.Entries[0];
                DecodedFrameTimelineEntry last = _decodedFrameTimeline.Entries[^1];
                if (!first.HasPresentationTimestamp || !last.HasPresentationTimestamp)
                    return false;

                double seconds =
                    (last.PresentationTimestamp - first.PresentationTimestamp) *
                    timeBaseSeconds;
                if (!double.IsFinite(seconds) || seconds < 0)
                    return false;

                extentSeconds = seconds;
                isComplete = _decodedFrameTimeline.IsComplete;
                return true;
            }
        }

        public bool TryResolveFrameTimestampSeconds(
            int frameIndex,
            CancellationToken cancellationToken,
            out double timestampSeconds)
        {
            timestampSeconds = double.NaN;
            if (frameIndex < 0 || cancellationToken.IsCancellationRequested)
                return false;
            if (TryGetCachedFrameTimestampSeconds(frameIndex, out timestampSeconds))
                return true;

            lock (_sync)
            {
                if (_disposed || cancellationToken.IsCancellationRequested)
                    return false;
                if (!EnsureDecodedFrameTimelineThrough(frameIndex, cancellationToken))
                    return false;

                return TryGetCachedFrameTimestampSeconds(
                    frameIndex,
                    out timestampSeconds);
            }
        }

        /// <summary>
        /// Resolves presentation time to the nearest decoded ordinal. It extends the
        /// ordinal PTS index only until the requested timestamp is covered.
        /// </summary>
        public bool TryResolveFrameIndexAtTimestamp(
            double timestampSeconds,
            CancellationToken cancellationToken,
            out int frameIndex)
        {
            frameIndex = -1;
            if (!double.IsFinite(timestampSeconds) ||
                timestampSeconds < 0 ||
                cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            if (TryGetCachedFrameIndexAtTimestamp(timestampSeconds, out frameIndex))
                return true;

            lock (_sync)
            {
                if (_disposed || cancellationToken.IsCancellationRequested)
                    return false;
                if (!EnsureDecodedFrameTimelineThroughTimestamp(
                        timestampSeconds,
                        cancellationToken))
                {
                    return false;
                }

                return TryGetCachedFrameIndexAtTimestamp(
                    timestampSeconds,
                    out frameIndex);
            }
        }

        public string LastDecodedTimestampSource
        {
            get
            {
                lock (_sync)
                    return _lastDecodedTimestampSource;
            }
        }

        public bool SequentialReachedEndOfStream
        {
            get
            {
                lock (_sync)
                    return _sequentialReachedEndOfStream;
            }
        }

        public bool SequentialReadCancelled
        {
            get
            {
                lock (_sync)
                    return _sequentialCancelled;
            }
        }

        public string? SequentialDecodeError
        {
            get
            {
                lock (_sync)
                    return _sequentialDecodeError;
            }
        }

        public PixelSize FrameSize => new(_dec->width, _dec->height);

        /// <summary>
        /// 지정 프레임 인덱스의 BGRA WriteableBitmap 반환.
        /// 실패 시 null.
        /// (FFmpeg 세션은 인스턴스 수명 동안 유지)
        /// </summary>
        public WriteableBitmap? GetFrameByIndex(int frameIndex)
            => GetFrameByIndex(frameIndex, CancellationToken.None);

        public WriteableBitmap? GetFrameByIndex(
            int frameIndex,
            CancellationToken cancellationToken)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(FfFrameExtractor));
            if (frameIndex < 0) return null;

            lock (_sync)
            {
                StartSequentialReadCore(frameIndex, timestampSeconds: null);
                if (!TryGetNextFrame(
                        cancellationToken,
                        requireBitmap: true,
                        out var frame,
                        out int decodedFrameIndex))
                {
                    return null;
                }

                if (decodedFrameIndex == frameIndex)
                    return frame;

                frame?.Dispose();
                SetSequentialDecodeError(
                    $"exact frame ordinal mismatch (requested={frameIndex}, decoded={decodedFrameIndex})");
                return null;
            }
        }

        public WriteableBitmap? GetFrameByIndexScaled(
            int frameIndex,
            int targetWidth,
            int targetHeight,
            CancellationToken cancellationToken)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(FfFrameExtractor));
            if (frameIndex < 0 || targetWidth <= 0 || targetHeight <= 0)
                return null;

            lock (_sync)
            {
                StartSequentialReadCore(frameIndex, timestampSeconds: null);
                if (!TryGetNextFrameRawScaled(
                        cancellationToken,
                        requireBgra: true,
                        targetWidth,
                        targetHeight,
                        useBilinear: true,
                        out BgraFrame raw,
                        out int decodedFrameIndex))
                {
                    return null;
                }

                if (decodedFrameIndex != frameIndex)
                {
                    SetSequentialDecodeError(
                        $"exact scaled frame ordinal mismatch (requested={frameIndex}, decoded={decodedFrameIndex})");
                    return null;
                }

                var bitmap = new WriteableBitmap(
                    new PixelSize(raw.Width, raw.Height),
                    new Vector(96, 96),
                    Avalonia.Platform.PixelFormat.Bgra8888,
                    Avalonia.Platform.AlphaFormat.Premul);

                using var fb = bitmap.Lock();
                byte* src = (byte*)raw.Data;
                int srcStride = raw.Stride;
                if (srcStride < 0)
                    src += (raw.Height - 1) * (-srcStride);

                int copyBytesPerRow = Math.Min(Math.Abs(srcStride), fb.RowBytes);
                byte* dst = (byte*)fb.Address;
                for (int y = 0; y < raw.Height; y++)
                {
                    Buffer.MemoryCopy(
                        src + y * srcStride,
                        dst + y * fb.RowBytes,
                        fb.RowBytes,
                        copyBytesPerRow);
                }

                return bitmap;
            }
        }

        /// <summary>
        /// Frame-ordinal thumbnail lookup. The ordinal is first mapped to its decoded
        /// presentation timestamp, then the image is obtained by timestamp seek.
        /// Dense timeline rendering should use the timestamp-based overload directly
        /// so scrolling does not build the ordinal index for every thumbnail slot.
        /// </summary>
        public WriteableBitmap? GetTimelineThumbnailByFrameIndexScaled(
            int frameIndex,
            int targetWidth,
            int targetHeight,
            CancellationToken cancellationToken)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(FfFrameExtractor));
            if (frameIndex < 0 || targetWidth <= 0 || targetHeight <= 0)
                return null;

            if (!TryResolveFrameTimestampSeconds(
                    frameIndex,
                    cancellationToken,
                    out double timestampSeconds))
            {
                return null;
            }

            return GetTimelineThumbnailAtTimestampScaled(
                timestampSeconds,
                targetWidth,
                targetHeight,
                cancellationToken);
        }

        public WriteableBitmap? GetTimelineThumbnailAtTimestampScaled(
            double timestampSeconds,
            int targetWidth,
            int targetHeight,
            CancellationToken cancellationToken)
        {
            if (!double.IsFinite(timestampSeconds) || timestampSeconds < 0)
                return null;

            lock (_sync)
            {
                if (cancellationToken.IsCancellationRequested)
                    return null;

                double timeBaseSeconds = ffmpeg.av_q2d(_timeBase);
                if (!double.IsFinite(timeBaseSeconds) || timeBaseSeconds <= 0)
                    return null;

                if (!TryGetTimelineOriginPresentationTimestamp(
                        cancellationToken,
                        out long originPts))
                {
                    return null;
                }

                double targetPtsDouble =
                    originPts + timestampSeconds / timeBaseSeconds;
                if (!double.IsFinite(targetPtsDouble) ||
                    targetPtsDouble < long.MinValue ||
                    targetPtsDouble > long.MaxValue)
                {
                    return null;
                }

                long targetPts = (long)Math.Round(targetPtsDouble);
                if (SeekMainDecoder(targetPts) < 0)
                    return null;

                // The timeline provider owns a dedicated extractor. A timestamp seek
                // intentionally abandons any previous sequential-read state.
                _sequentialActive = false;

                if (!EnsureReusableScaledBgraFrame(targetWidth, targetHeight))
                    return null;
                if (ffmpeg.av_frame_make_writable(_bgraScaledReusable) < 0)
                    return null;

                AVPacket* packet = ffmpeg.av_packet_alloc();
                AVFrame* decodedFrame = ffmpeg.av_frame_alloc();
                if (packet == null || decodedFrame == null)
                {
                    if (packet != null) ffmpeg.av_packet_free(&packet);
                    if (decodedFrame != null) ffmpeg.av_frame_free(&decodedFrame);
                    return null;
                }

                int tryAgain = ffmpeg.AVERROR(ffmpeg.EAGAIN);
                bool drainSent = false;
                try
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        ffmpeg.av_frame_unref(decodedFrame);
                        int receiveResult = ffmpeg.avcodec_receive_frame(_dec, decodedFrame);
                        if (receiveResult == 0)
                        {
                            long pts = GetDecodedPresentationTimestamp(decodedFrame);
                            if (pts != ffmpeg.AV_NOPTS_VALUE && pts < targetPts)
                                continue;

                            if (!ConvertDecodedFrameToBgraScaled(
                                    decodedFrame,
                                    _bgraScaledReusable,
                                    targetWidth,
                                    targetHeight,
                                    SwsFlags.SWS_BILINEAR))
                            {
                                return null;
                            }

                            var bitmap = new WriteableBitmap(
                                new PixelSize(targetWidth, targetHeight),
                                new Vector(96, 96),
                                Avalonia.Platform.PixelFormat.Bgra8888,
                                Avalonia.Platform.AlphaFormat.Premul);

                            using var fb = bitmap.Lock();
                            byte* src = _bgraScaledReusable->data[0];
                            int srcStride = _bgraScaledReusable->linesize[0];
                            if (srcStride < 0)
                                src += (targetHeight - 1) * (-srcStride);

                            int copyBytesPerRow = Math.Min(Math.Abs(srcStride), fb.RowBytes);
                            byte* dst = (byte*)fb.Address;
                            for (int y = 0; y < targetHeight; y++)
                            {
                                Buffer.MemoryCopy(
                                    src + y * srcStride,
                                    dst + y * fb.RowBytes,
                                    fb.RowBytes,
                                    copyBytesPerRow);
                            }

                            return bitmap;
                        }

                        if (receiveResult == ffmpeg.AVERROR_EOF)
                            return null;
                        if (receiveResult != tryAgain || drainSent)
                            return null;

                        int readResult;
                        do
                        {
                            if (cancellationToken.IsCancellationRequested)
                                return null;

                            ffmpeg.av_packet_unref(packet);
                            readResult = ffmpeg.av_read_frame(_fmt, packet);
                        }
                        while (readResult >= 0 && packet->stream_index != _videoStreamIndex);

                        if (cancellationToken.IsCancellationRequested)
                        {
                            ffmpeg.av_packet_unref(packet);
                            return null;
                        }

                        if (readResult == ffmpeg.AVERROR_EOF)
                        {
                            ffmpeg.av_packet_unref(packet);
                            if (cancellationToken.IsCancellationRequested)
                                return null;

                            int drainResult = ffmpeg.avcodec_send_packet(_dec, null);
                            drainSent = true;
                            if (drainResult == 0)
                                continue;
                            return null;
                        }

                        if (readResult < 0)
                        {
                            ffmpeg.av_packet_unref(packet);
                            return null;
                        }

                        if (cancellationToken.IsCancellationRequested)
                        {
                            ffmpeg.av_packet_unref(packet);
                            return null;
                        }

                        int sendResult = ffmpeg.avcodec_send_packet(_dec, packet);
                        ffmpeg.av_packet_unref(packet);
                        if (sendResult < 0)
                            return null;
                    }

                    return null;
                }
                finally
                {
                    ffmpeg.av_packet_free(&packet);
                    ffmpeg.av_frame_free(&decodedFrame);
                }
            }
        }

        public void StartSequentialRead(int startFrameIndex)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(FfFrameExtractor));
            if (startFrameIndex < 0) startFrameIndex = 0;

            lock (_sync)
            {
                StartSequentialReadCore(startFrameIndex, timestampSeconds: null);
            }
        }

        /// <summary>
        /// 실제 표시 시각을 기준으로 순차 읽기를 시작합니다. VFR 위험 프레임 재검출에서 사용합니다.
        /// </summary>
        public void StartSequentialReadAtTimestamp(int startFrameIndex, double timestampSeconds)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(FfFrameExtractor));
            if (startFrameIndex < 0) startFrameIndex = 0;
            if (!double.IsFinite(timestampSeconds))
                throw new ArgumentOutOfRangeException(nameof(timestampSeconds));

            lock (_sync)
            {
                StartSequentialReadCore(startFrameIndex, timestampSeconds);
            }
        }

        private void StartSequentialReadCore(int startFrameIndex, double? timestampSeconds)
        {
            ResetSequentialCompletionState();
            _decodeError = null;
            _hardwareTransferFailed = false;
            _lastDecodedTimestampSeconds = double.NaN;
            _lastDecodedTimestampSource = "none";
            _sequentialActive = true;
            _sequentialRequestedIndex = startFrameIndex;
            _sequentialStarted = false;

            if (startFrameIndex == 0)
            {
                PrepareSequentialDecodeFromBeginning();
                return;
            }

            _sequentialOrdinalMode = SequentialOrdinalMode.ResolveIndexedTimestamp;
            _pendingSequentialTimestampSeconds = timestampSeconds;
            _sequentialTargetPts = ffmpeg.AV_NOPTS_VALUE;
            _sequentialIndex = 0;
        }

        public bool TryGetNextFrame(CancellationToken ct, out WriteableBitmap? frame, out int frameIndex)
            => TryGetNextFrame(ct, requireBitmap: true, out frame, out frameIndex);

        public bool TryGetNextFrame(CancellationToken ct, bool requireBitmap, out WriteableBitmap? frame, out int frameIndex)
        {
            frame = null;
            frameIndex = -1;

            if (_disposed) throw new ObjectDisposedException(nameof(FfFrameExtractor));

            lock (_sync)
            {
                if (!_sequentialActive)
                    throw new InvalidOperationException("StartSequentialRead must be called before TryGetNextFrame.");

                AVPacket* pkt = ffmpeg.av_packet_alloc();
                AVFrame* src = ffmpeg.av_frame_alloc();
                AVFrame* bgra = requireBitmap ? ffmpeg.av_frame_alloc() : null;

                if (pkt == null || src == null || (requireBitmap && bgra == null))
                {
                    if (pkt != null) ffmpeg.av_packet_free(&pkt);
                    if (src != null) ffmpeg.av_frame_free(&src);
                    if (bgra != null) ffmpeg.av_frame_free(&bgra);
                    SetSequentialDecodeError("failed to allocate sequential bitmap decode buffers");
                    return false;
                }

                try
                {
                    if (requireBitmap)
                    {
                        bgra->format = (int)AVPixelFormat.AV_PIX_FMT_BGRA;
                        bgra->width = _dec->width;
                        bgra->height = _dec->height;

                        int bufferResult = ffmpeg.av_frame_get_buffer(bgra, 32);
                        if (bufferResult < 0)
                        {
                            SetSequentialDecodeError("av_frame_get_buffer failed", bufferResult);
                            return false;
                        }
                    }

                    if (!TryDecodeNextSequentialFrame(ct, pkt, src, out frameIndex))
                        return false;

                    if (!requireBitmap)
                        return true;

                    var bmp = ConvertDecodedFrameToBitmap(src, bgra);
                    if (bmp != null)
                    {
                        frame = bmp;
                        return true;
                    }

                    SetSequentialDecodeError("decoded frame bitmap conversion failed");
                    return false;
                }
                finally
                {
                    ffmpeg.av_packet_free(&pkt);
                    ffmpeg.av_frame_free(&src);
                    ffmpeg.av_frame_free(&bgra);
                }
            }
        }

        public bool TryGetNextFrameRaw(CancellationToken ct, bool requireBgra, out BgraFrame frame, out int frameIndex)
        {
            frame = default;
            frameIndex = -1;

            if (_disposed) throw new ObjectDisposedException(nameof(FfFrameExtractor));

            lock (_sync)
            {
                if (!_sequentialActive)
                    throw new InvalidOperationException("StartSequentialRead must be called before TryGetNextFrameRaw.");

                if (!EnsureSequentialDecodeResources())
                {
                    SetSequentialDecodeError("failed to allocate sequential raw decode buffers");
                    return false;
                }

                AVPacket* pkt = _sequentialPacketReusable;
                AVFrame* src = _sequentialDecodedFrameReusable;

                try
                {
                    if (requireBgra)
                    {
                        if (!EnsureReusableBgraFrame())
                        {
                            SetSequentialDecodeError("failed to allocate the reusable BGRA frame");
                            return false;
                        }
                        int writableResult = ffmpeg.av_frame_make_writable(_bgraReusable);
                        if (writableResult < 0)
                        {
                            SetSequentialDecodeError("av_frame_make_writable failed", writableResult);
                            return false;
                        }
                    }

                    if (!TryDecodeNextSequentialFrame(ct, pkt, src, out frameIndex))
                        return false;

                    if (!requireBgra)
                        return true;

                    if (ConvertDecodedFrameToBgra(src, _bgraReusable))
                    {
                        frame = new BgraFrame(
                            (IntPtr)_bgraReusable->data[0],
                            _bgraReusable->linesize[0],
                            _bgraReusable->width,
                            _bgraReusable->height);
                        return true;
                    }

                    SetSequentialDecodeError("decoded frame BGRA conversion failed");
                    return false;
                }
                finally
                {
                    ffmpeg.av_packet_unref(pkt);
                    ffmpeg.av_frame_unref(src);
                }
            }
        }

        public bool TryGetNextFrameRawScaled(
            CancellationToken ct,
            bool requireBgra,
            int targetWidth,
            int targetHeight,
            bool useBilinear,
            out BgraFrame frame,
            out int frameIndex)
        {
            frame = default;
            frameIndex = -1;

            if (_disposed) throw new ObjectDisposedException(nameof(FfFrameExtractor));

            lock (_sync)
            {
                if (!_sequentialActive)
                    throw new InvalidOperationException("StartSequentialRead must be called before TryGetNextFrameRawScaled.");

                if (!EnsureSequentialDecodeResources())
                {
                    SetSequentialDecodeError("failed to allocate sequential scaled decode buffers");
                    return false;
                }

                AVPacket* pkt = _sequentialPacketReusable;
                AVFrame* src = _sequentialDecodedFrameReusable;

                try
                {
                    if (requireBgra)
                    {
                        if (!EnsureReusableScaledBgraFrame(targetWidth, targetHeight))
                        {
                            SetSequentialDecodeError("failed to allocate the reusable scaled BGRA frame");
                            return false;
                        }
                        int writableResult = ffmpeg.av_frame_make_writable(_bgraScaledReusable);
                        if (writableResult < 0)
                        {
                            SetSequentialDecodeError("av_frame_make_writable failed", writableResult);
                            return false;
                        }
                    }

                    if (!TryDecodeNextSequentialFrame(ct, pkt, src, out frameIndex))
                        return false;

                    if (!requireBgra)
                        return true;

                    var flags = useBilinear ? SwsFlags.SWS_BILINEAR : SwsFlags.SWS_POINT;
                    if (ConvertDecodedFrameToBgraScaled(src, _bgraScaledReusable, targetWidth, targetHeight, flags))
                    {
                        frame = new BgraFrame(
                            (IntPtr)_bgraScaledReusable->data[0],
                            _bgraScaledReusable->linesize[0],
                            _bgraScaledReusable->width,
                            _bgraScaledReusable->height);
                        return true;
                    }

                    SetSequentialDecodeError("decoded frame scaled BGRA conversion failed");
                    return false;
                }
                finally
                {
                    ffmpeg.av_packet_unref(pkt);
                    ffmpeg.av_frame_unref(src);
                }
            }
        }

        public bool TryGetNextFrameRawToBuffer(
            CancellationToken ct,
            int targetWidth,
            int targetHeight,
            bool useBilinear,
            byte[] buffer,
            out int frameIndex,
            out int stride)
        {
            frameIndex = -1;
            stride = 0;

            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            if (_disposed) throw new ObjectDisposedException(nameof(FfFrameExtractor));

            int dstW = targetWidth > 0 ? targetWidth : _dec->width;
            int dstH = targetHeight > 0 ? targetHeight : _dec->height;
            stride = dstW * 4;
            int bytes = stride * dstH;
            if (buffer.Length < bytes)
                throw new ArgumentException("The destination buffer is too small.", nameof(buffer));

            lock (_sync)
            {
                if (!_sequentialActive)
                    throw new InvalidOperationException("StartSequentialRead must be called before TryGetNextFrameRawToBuffer.");

                if (!EnsureSequentialDecodeResources())
                {
                    SetSequentialDecodeError("failed to allocate sequential buffer decode resources");
                    return false;
                }

                AVPacket* pkt = _sequentialPacketReusable;
                AVFrame* src = _sequentialDecodedFrameReusable;

                try
                {
                    fixed (byte* dst = buffer)
                    {
                        byte_ptrArray4 dstData = default;
                        int_array4 dstLinesize = default;
                        dstData[0] = dst;
                        dstLinesize[0] = stride;

                        bool scaled = dstW != _dec->width || dstH != _dec->height;
                        SwsFlags flags = useBilinear ? SwsFlags.SWS_BILINEAR : SwsFlags.SWS_POINT;

                        if (!TryDecodeNextSequentialFrame(ct, pkt, src, out frameIndex))
                            return false;

                        bool ok = scaled
                            ? ConvertDecodedFrameToBgraToBuffer(
                                src,
                                dstData,
                                dstLinesize,
                                dstW,
                                dstH,
                                flags,
                                ref _swsScaled)
                            : ConvertDecodedFrameToBgraToBuffer(
                                src,
                                dstData,
                                dstLinesize,
                                dstW,
                                dstH,
                                SwsFlags.SWS_BILINEAR,
                                ref _sws);
                        if (ok)
                            return true;
                    }

                    SetSequentialDecodeError("decoded frame buffer conversion failed");
                    return false;
                }
                finally
                {
                    ffmpeg.av_packet_unref(pkt);
                    ffmpeg.av_frame_unref(src);
                }
            }
        }

        private bool TryDecodeNextSequentialFrame(
            CancellationToken ct,
            AVPacket* packet,
            AVFrame* decodedFrame,
            out int frameIndex)
        {
            frameIndex = -1;
            int tryAgain = ffmpeg.AVERROR(ffmpeg.EAGAIN);

            if (_sequentialOrdinalMode == SequentialOrdinalMode.ResolveIndexedTimestamp &&
                !TryResolveSequentialOrdinalPosition(ct))
            {
                return false;
            }

            while (true)
            {
                if (ct.IsCancellationRequested)
                {
                    _sequentialCancelled = true;
                    return false;
                }

                ffmpeg.av_frame_unref(decodedFrame);
                int receiveResult = ffmpeg.avcodec_receive_frame(_dec, decodedFrame);
                if (receiveResult == 0)
                {
                    long pts = GetDecodedPresentationTimestamp(decodedFrame);
                    if (_sequentialOrdinalMode == SequentialOrdinalMode.MatchIndexedTimestamp &&
                        !_sequentialStarted)
                    {
                        if (pts == _sequentialTargetPts)
                        {
                            if (!IsIndexedTimestampStillSafe(
                                    _sequentialRequestedIndex,
                                    _sequentialTargetPts))
                            {
                                if (!TryRestartSequentialDecodeFromBeginning(packet))
                                    return false;
                                continue;
                            }

                            _sequentialStarted = true;
                            frameIndex = _sequentialRequestedIndex;
                            _sequentialIndex = frameIndex + 1;
                            if (!TryRecordDecodedFrameTimelineEntry(frameIndex, pts))
                            {
                                SetSequentialDecodeError(
                                    $"decoded timestamp changed at ordinal {frameIndex}");
                                return false;
                            }
                            CaptureDecodedTimestamp(frameIndex, pts);
                            return true;
                        }

                        if (pts != ffmpeg.AV_NOPTS_VALUE && pts < _sequentialTargetPts)
                            continue;

                        if (!TryRestartSequentialDecodeFromBeginning(packet))
                            return false;
                        continue;
                    }

                    frameIndex = _sequentialIndex++;
                    if (!TryRecordDecodedFrameTimelineEntry(frameIndex, pts))
                    {
                        SetSequentialDecodeError(
                            $"beginning decode did not match cached ordinal {frameIndex}");
                        return false;
                    }
                    if (frameIndex < _sequentialRequestedIndex)
                        continue;

                    _sequentialStarted = true;
                    CaptureDecodedTimestamp(frameIndex, pts);
                    return true;
                }

                if (receiveResult == ffmpeg.AVERROR_EOF)
                {
                    if (_sequentialOrdinalMode == SequentialOrdinalMode.MatchIndexedTimestamp &&
                        !_sequentialStarted &&
                        TryRestartSequentialDecodeFromBeginning(packet))
                    {
                        continue;
                    }

                    _sequentialReachedEndOfStream = true;
                    MarkDecodedFrameTimelineCompleteIfContiguous();
                    return false;
                }

                if (receiveResult != tryAgain)
                {
                    SetSequentialDecodeError("avcodec_receive_frame failed", receiveResult);
                    return false;
                }

                if (_sequentialDrainSent)
                {
                    SetSequentialDecodeError(
                        "decoder returned EAGAIN after the end-of-stream drain packet");
                    return false;
                }

                int readResult;
                do
                {
                    if (ct.IsCancellationRequested)
                    {
                        _sequentialCancelled = true;
                        return false;
                    }

                    ffmpeg.av_packet_unref(packet);
                    readResult = ffmpeg.av_read_frame(_fmt, packet);
                }
                while (readResult >= 0 && packet->stream_index != _videoStreamIndex);

                if (readResult == ffmpeg.AVERROR_EOF)
                {
                    ffmpeg.av_packet_unref(packet);
                    if (ct.IsCancellationRequested)
                    {
                        _sequentialCancelled = true;
                        return false;
                    }

                    int drainResult = ffmpeg.avcodec_send_packet(_dec, null);
                    _sequentialDrainSent = true;
                    if (drainResult == 0)
                        continue;
                    if (drainResult == ffmpeg.AVERROR_EOF)
                    {
                        if (_sequentialOrdinalMode == SequentialOrdinalMode.MatchIndexedTimestamp &&
                            !_sequentialStarted &&
                            TryRestartSequentialDecodeFromBeginning(packet))
                        {
                            continue;
                        }

                        _sequentialReachedEndOfStream = true;
                        MarkDecodedFrameTimelineCompleteIfContiguous();
                        return false;
                    }

                    SetSequentialDecodeError("avcodec_send_packet drain failed", drainResult);
                    return false;
                }

                if (readResult < 0)
                {
                    ffmpeg.av_packet_unref(packet);
                    SetSequentialDecodeError("av_read_frame failed", readResult);
                    return false;
                }

                if (ct.IsCancellationRequested)
                {
                    ffmpeg.av_packet_unref(packet);
                    _sequentialCancelled = true;
                    return false;
                }

                int sendResult = ffmpeg.avcodec_send_packet(_dec, packet);
                ffmpeg.av_packet_unref(packet);
                if (sendResult == 0)
                    continue;

                SetSequentialDecodeError(
                    sendResult == tryAgain
                        ? "decoder rejected an input packet with EAGAIN after output was drained"
                        : "avcodec_send_packet failed",
                    sendResult);
                return false;
            }
        }

        private void PrepareSequentialDecodeFromBeginning()
        {
            if (SeekMainDecoderToBeginning() < 0)
            {
                throw new InvalidOperationException(
                    $"Could not seek to the beginning for exact frame {_sequentialRequestedIndex}.");
            }

            _sequentialOrdinalMode = SequentialOrdinalMode.DecodeFromBeginning;
            _pendingSequentialTimestampSeconds = null;
            _sequentialTargetPts = ffmpeg.AV_NOPTS_VALUE;
            _sequentialIndex = 0;
            _sequentialStarted = false;
        }

        private bool TryRestartSequentialDecodeFromBeginning(AVPacket* packet)
        {
            ffmpeg.av_packet_unref(packet);
            int seekResult = SeekMainDecoderToBeginning();
            if (seekResult < 0)
            {
                SetSequentialDecodeError(
                    $"exact timestamp did not resolve and beginning fallback seek failed",
                    seekResult);
                return false;
            }

            Debug.WriteLine(
                $"[FfFrameExtractor] indexed timestamp did not resolve; decoding ordinal " +
                $"{_sequentialRequestedIndex} from the beginning.");
            _sequentialOrdinalMode = SequentialOrdinalMode.DecodeFromBeginning;
            _pendingSequentialTimestampSeconds = null;
            _sequentialTargetPts = ffmpeg.AV_NOPTS_VALUE;
            _sequentialIndex = 0;
            _sequentialStarted = false;
            _sequentialDrainSent = false;
            _sequentialReachedEndOfStream = false;
            return true;
        }

        private bool TryResolveSequentialOrdinalPosition(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _sequentialCancelled = true;
                return false;
            }

            if (TryGetUnambiguousIndexedTimestamp(
                    _sequentialRequestedIndex,
                    _pendingSequentialTimestampSeconds,
                    cancellationToken,
                    out long indexedTimestamp))
            {
                int seekResult = SeekMainDecoder(indexedTimestamp);
                if (seekResult >= 0)
                {
                    _sequentialOrdinalMode = SequentialOrdinalMode.MatchIndexedTimestamp;
                    _pendingSequentialTimestampSeconds = null;
                    _sequentialTargetPts = indexedTimestamp;
                    _sequentialIndex = _sequentialRequestedIndex;
                    return true;
                }

                Debug.WriteLine(
                    $"[FfFrameExtractor] exact ordinal seek failed; decoding from beginning " +
                    $"(frame={_sequentialRequestedIndex}, pts={indexedTimestamp}, err={seekResult}).");
            }

            if (cancellationToken.IsCancellationRequested)
            {
                _sequentialCancelled = true;
                return false;
            }

            if (IsKnownOutOfRangeOrdinal(_sequentialRequestedIndex))
            {
                _sequentialReachedEndOfStream = true;
                return false;
            }

            try
            {
                PrepareSequentialDecodeFromBeginning();
                return true;
            }
            catch (Exception ex)
            {
                SetSequentialDecodeError(
                    $"could not prepare exact ordinal {_sequentialRequestedIndex}: {ex.Message}");
                return false;
            }
        }

        private bool IsKnownOutOfRangeOrdinal(int frameIndex)
        {
            lock (_decodedFrameTimeline.SyncRoot)
            {
                return _decodedFrameTimeline.IsReliable &&
                    _decodedFrameTimeline.IsComplete &&
                    frameIndex >= _decodedFrameTimeline.Entries.Count;
            }
        }

        private bool IsIndexedTimestampStillSafe(int frameIndex, long timestamp)
        {
            lock (_decodedFrameTimeline.SyncRoot)
            {
                return _decodedFrameTimeline.IsReliable &&
                    _decodedFrameTimeline.SupportsExactTimestampSeek &&
                    frameIndex >= 0 &&
                    frameIndex < _decodedFrameTimeline.Entries.Count &&
                    _decodedFrameTimeline.Entries[frameIndex].PresentationTimestamp == timestamp &&
                    _decodedFrameTimeline.Entries[frameIndex].TimestampOccurrence == 1;
            }
        }

        private int SeekMainDecoderToBeginning()
        {
            long timestamp = GetFirstKnownPresentationTimestamp();
            if (timestamp == ffmpeg.AV_NOPTS_VALUE)
            {
                AVStream* stream = _fmt->streams[_videoStreamIndex];
                timestamp = stream->start_time != ffmpeg.AV_NOPTS_VALUE
                    ? stream->start_time
                    : 0;
            }

            return SeekMainDecoder(timestamp);
        }

        private int SeekMainDecoder(long timestamp)
        {
            int seekResult = ffmpeg.av_seek_frame(
                _fmt,
                _videoStreamIndex,
                timestamp,
                ffmpeg.AVSEEK_FLAG_BACKWARD);
            if (seekResult >= 0)
            {
                ffmpeg.avcodec_flush_buffers(_dec);
                _sequentialDrainSent = false;
            }

            return seekResult;
        }

        private long GetFirstKnownPresentationTimestamp()
        {
            lock (_decodedFrameTimeline.SyncRoot)
            {
                _decodedFrameTimeline.LastAccessTicks = DateTime.UtcNow.Ticks;
                if (_decodedFrameTimeline.IsReliable &&
                    _decodedFrameTimeline.Entries.Count > 0 &&
                    _decodedFrameTimeline.Entries[0].HasPresentationTimestamp)
                {
                    return _decodedFrameTimeline.Entries[0].PresentationTimestamp;
                }
            }

            return ffmpeg.AV_NOPTS_VALUE;
        }

        private bool TryGetUnambiguousIndexedTimestamp(
            int frameIndex,
            double? expectedTimestampSeconds,
            CancellationToken cancellationToken,
            out long presentationTimestamp)
        {
            presentationTimestamp = ffmpeg.AV_NOPTS_VALUE;
            int lookaheadFrameIndex = frameIndex == int.MaxValue
                ? frameIndex
                : frameIndex + 1;
            EnsureDecodedFrameTimelineThrough(lookaheadFrameIndex, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
                return false;

            lock (_decodedFrameTimeline.SyncRoot)
            {
                _decodedFrameTimeline.LastAccessTicks = DateTime.UtcNow.Ticks;
                if (!_decodedFrameTimeline.IsReliable ||
                    !_decodedFrameTimeline.SupportsExactTimestampSeek ||
                    frameIndex >= _decodedFrameTimeline.Entries.Count)
                {
                    return false;
                }

                DecodedFrameTimelineEntry target = _decodedFrameTimeline.Entries[frameIndex];
                if (!target.HasPresentationTimestamp || target.TimestampOccurrence != 1)
                    return false;

                if (frameIndex > 0)
                {
                    DecodedFrameTimelineEntry previous = _decodedFrameTimeline.Entries[frameIndex - 1];
                    if (!previous.HasPresentationTimestamp ||
                        previous.PresentationTimestamp >= target.PresentationTimestamp)
                    {
                        return false;
                    }
                }

                if (frameIndex + 1 < _decodedFrameTimeline.Entries.Count)
                {
                    DecodedFrameTimelineEntry next = _decodedFrameTimeline.Entries[frameIndex + 1];
                    if (!next.HasPresentationTimestamp ||
                        next.PresentationTimestamp <= target.PresentationTimestamp)
                    {
                        return false;
                    }
                }
                else if (!_decodedFrameTimeline.IsComplete)
                {
                    return false;
                }

                if (expectedTimestampSeconds.HasValue)
                {
                    double timeBaseSeconds = ffmpeg.av_q2d(_timeBase);
                    double indexedSeconds = target.PresentationTimestamp * timeBaseSeconds;
                    double tolerance = timeBaseSeconds > 0 && double.IsFinite(timeBaseSeconds)
                        ? Math.Max(timeBaseSeconds, 0.000001)
                        : 0.000001;
                    if (!double.IsFinite(indexedSeconds) ||
                        Math.Abs(indexedSeconds - expectedTimestampSeconds.Value) > tolerance)
                    {
                        Debug.WriteLine(
                            $"[FfFrameExtractor] supplied timestamp did not match decoded ordinal index " +
                            $"(frame={frameIndex}, supplied={expectedTimestampSeconds.Value:0.#########}, " +
                            $"indexed={indexedSeconds:0.#########}).");
                    }
                }

                presentationTimestamp = target.PresentationTimestamp;
                return true;
            }
        }

        private static long GetDecodedPresentationTimestamp(AVFrame* frame)
        {
            long timestamp = frame->best_effort_timestamp;
            return timestamp != ffmpeg.AV_NOPTS_VALUE ? timestamp : frame->pts;
        }

        private bool TryGetTimelineOriginPresentationTimestamp(
            CancellationToken cancellationToken,
            out long originPts)
        {
            originPts = ffmpeg.AV_NOPTS_VALUE;
            if (cancellationToken.IsCancellationRequested)
                return false;

            lock (_decodedFrameTimeline.SyncRoot)
            {
                if (_decodedFrameTimeline.Entries.Count > 0)
                {
                    DecodedFrameTimelineEntry first =
                        _decodedFrameTimeline.Entries[0];
                    if (first.HasPresentationTimestamp)
                    {
                        originPts = first.PresentationTimestamp;
                        return true;
                    }
                }
            }

            if (!EnsureDecodedFrameTimelineThrough(0, cancellationToken))
                return false;

            lock (_decodedFrameTimeline.SyncRoot)
            {
                if (_decodedFrameTimeline.Entries.Count == 0)
                    return false;

                DecodedFrameTimelineEntry first =
                    _decodedFrameTimeline.Entries[0];
                if (!first.HasPresentationTimestamp)
                    return false;

                originPts = first.PresentationTimestamp;
                return true;
            }
        }

        private bool EnsureDecodedFrameTimelineThrough(
            int frameIndex,
            CancellationToken cancellationToken)
        {
            if (frameIndex < 0)
                return true;

            lock (_decodedFrameTimeline.SyncRoot)
            {
                _decodedFrameTimeline.LastAccessTicks = DateTime.UtcNow.Ticks;
                if (!_decodedFrameTimeline.IsReliable)
                    return false;
                if (!_decodedFrameTimeline.SupportsExactTimestampSeek)
                    return false;
                if (_decodedFrameTimeline.Entries.Count > frameIndex)
                    return true;
                if (_decodedFrameTimeline.IsComplete ||
                    _decodedFrameTimeline.CapacityReached ||
                    _ordinalDecoderFailed)
                {
                    return false;
                }
            }

            try
            {
                if (cancellationToken.IsCancellationRequested)
                    return false;
                EnsureOrdinalDecoderInitialized();
                while (!_ordinalReachedEndOfStream)
                {
                    if (cancellationToken.IsCancellationRequested)
                        return false;

                    lock (_decodedFrameTimeline.SyncRoot)
                    {
                        _decodedFrameTimeline.LastAccessTicks = DateTime.UtcNow.Ticks;
                        if (!_decodedFrameTimeline.IsReliable)
                            return false;
                        if (!_decodedFrameTimeline.SupportsExactTimestampSeek)
                            return false;
                        if (_decodedFrameTimeline.Entries.Count > frameIndex)
                            return true;
                        if (_decodedFrameTimeline.IsComplete ||
                            _decodedFrameTimeline.CapacityReached)
                        {
                            return false;
                        }
                    }

                    if (!TryDecodeNextOrdinalIndexFrame(
                            cancellationToken,
                            out long timestamp))
                    {
                        break;
                    }

                    bool accepted;
                    bool capacityReached;
                    lock (_decodedFrameTimeline.SyncRoot)
                    {
                        accepted = TryAddOrValidateTimelineEntry(
                            _decodedFrameTimeline,
                            _ordinalNextFrameIndex,
                            timestamp);
                        capacityReached = _decodedFrameTimeline.CapacityReached;
                    }

                    if (!accepted)
                    {
                        _ordinalDecoderFailed = true;
                        if (capacityReached)
                            DisposeOrdinalDecoder();
                        return false;
                    }

                    _ordinalNextFrameIndex++;
                }

                if (cancellationToken.IsCancellationRequested)
                    return false;

                lock (_decodedFrameTimeline.SyncRoot)
                {
                    if (_ordinalReachedEndOfStream)
                    {
                        _decodedFrameTimeline.IsComplete =
                            _decodedFrameTimeline.IsReliable &&
                            _ordinalNextFrameIndex == _decodedFrameTimeline.Entries.Count;
                    }

                    return _decodedFrameTimeline.IsReliable &&
                        _decodedFrameTimeline.Entries.Count > frameIndex;
                }
            }
            catch (Exception ex)
            {
                _ordinalDecoderFailed = true;
                Debug.WriteLine(
                    $"[FfFrameExtractor] decoded ordinal index failed; using beginning fallback: {ex}");
                DisposeOrdinalDecoder();
                return false;
            }
        }

        private bool EnsureDecodedFrameTimelineThroughTimestamp(
            double timestampSeconds,
            CancellationToken cancellationToken)
        {
            if (!double.IsFinite(timestampSeconds) || timestampSeconds < 0)
                return false;

            double timeBaseSeconds = ffmpeg.av_q2d(_timeBase);
            if (!double.IsFinite(timeBaseSeconds) || timeBaseSeconds <= 0)
                return false;
            if (!EnsureDecodedFrameTimelineThrough(0, cancellationToken))
                return false;

            long targetPts;
            lock (_decodedFrameTimeline.SyncRoot)
            {
                _decodedFrameTimeline.LastAccessTicks = DateTime.UtcNow.Ticks;
                if (!_decodedFrameTimeline.IsReliable ||
                    !_decodedFrameTimeline.SupportsExactTimestampSeek ||
                    _decodedFrameTimeline.Entries.Count == 0)
                {
                    return false;
                }

                DecodedFrameTimelineEntry origin = _decodedFrameTimeline.Entries[0];
                if (!origin.HasPresentationTimestamp)
                    return false;

                double targetPtsDouble =
                    origin.PresentationTimestamp +
                    timestampSeconds / timeBaseSeconds;
                if (!double.IsFinite(targetPtsDouble) ||
                    targetPtsDouble < long.MinValue ||
                    targetPtsDouble > long.MaxValue)
                {
                    return false;
                }

                targetPts = (long)Math.Round(targetPtsDouble);
                DecodedFrameTimelineEntry last = _decodedFrameTimeline.Entries[^1];
                if (last.HasPresentationTimestamp &&
                    last.PresentationTimestamp >= targetPts)
                {
                    return true;
                }

                if (_decodedFrameTimeline.IsComplete ||
                    _decodedFrameTimeline.CapacityReached ||
                    _ordinalDecoderFailed)
                {
                    return _decodedFrameTimeline.IsComplete;
                }
            }

            try
            {
                EnsureOrdinalDecoderInitialized();
                while (!_ordinalReachedEndOfStream)
                {
                    if (cancellationToken.IsCancellationRequested)
                        return false;

                    lock (_decodedFrameTimeline.SyncRoot)
                    {
                        _decodedFrameTimeline.LastAccessTicks = DateTime.UtcNow.Ticks;
                        if (!_decodedFrameTimeline.IsReliable ||
                            !_decodedFrameTimeline.SupportsExactTimestampSeek)
                        {
                            return false;
                        }

                        if (_decodedFrameTimeline.Entries.Count > 0)
                        {
                            DecodedFrameTimelineEntry last =
                                _decodedFrameTimeline.Entries[^1];
                            if (last.HasPresentationTimestamp &&
                                last.PresentationTimestamp >= targetPts)
                            {
                                return true;
                            }
                        }

                        if (_decodedFrameTimeline.IsComplete)
                            return true;
                        if (_decodedFrameTimeline.CapacityReached)
                            return false;
                    }

                    if (!TryDecodeNextOrdinalIndexFrame(
                            cancellationToken,
                            out long timestamp))
                    {
                        break;
                    }

                    bool accepted;
                    bool capacityReached;
                    lock (_decodedFrameTimeline.SyncRoot)
                    {
                        accepted = TryAddOrValidateTimelineEntry(
                            _decodedFrameTimeline,
                            _ordinalNextFrameIndex,
                            timestamp);
                        capacityReached = _decodedFrameTimeline.CapacityReached;
                    }

                    if (!accepted)
                    {
                        _ordinalDecoderFailed = true;
                        if (capacityReached)
                            DisposeOrdinalDecoder();
                        return false;
                    }

                    _ordinalNextFrameIndex++;
                }

                lock (_decodedFrameTimeline.SyncRoot)
                {
                    if (_ordinalReachedEndOfStream)
                    {
                        _decodedFrameTimeline.IsComplete =
                            _decodedFrameTimeline.IsReliable &&
                            _ordinalNextFrameIndex ==
                                _decodedFrameTimeline.Entries.Count;
                    }

                    if (!_decodedFrameTimeline.IsReliable ||
                        !_decodedFrameTimeline.SupportsExactTimestampSeek ||
                        _decodedFrameTimeline.Entries.Count == 0)
                    {
                        return false;
                    }

                    DecodedFrameTimelineEntry last =
                        _decodedFrameTimeline.Entries[^1];
                    return _decodedFrameTimeline.IsComplete ||
                        (last.HasPresentationTimestamp &&
                         last.PresentationTimestamp >= targetPts);
                }
            }
            catch (Exception ex)
            {
                _ordinalDecoderFailed = true;
                Debug.WriteLine(
                    $"[FfFrameExtractor] timestamp ordinal index failed: {ex}");
                DisposeOrdinalDecoder();
                return false;
            }
        }

        private void EnsureOrdinalDecoderInitialized()
        {
            if (_ordinalDecoder != null)
                return;

            fixed (AVFormatContext** format = &_ordinalFormat)
            {
                FFmpegErrorHelper.ThrowIfError(
                    ffmpeg.avformat_open_input(format, _videoPath, null, null),
                    $"Failed to open ordinal index input: {_videoPath}");
            }

            FFmpegErrorHelper.ThrowIfError(
                ffmpeg.avformat_find_stream_info(_ordinalFormat, null),
                $"Failed to read ordinal index stream info: {_videoPath}");

            _ordinalVideoStreamIndex = FFmpegStreamSelection.FindPrimaryVideoStreamIndex(_ordinalFormat);
            if (_ordinalVideoStreamIndex < 0)
                throw new InvalidOperationException("ordinal index video stream not found");

            AVStream* stream = _ordinalFormat->streams[_ordinalVideoStreamIndex];
            AVCodec* decoder = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
            if (decoder == null)
                throw new InvalidOperationException("ordinal index decoder not found");

            _ordinalDecoder = ffmpeg.avcodec_alloc_context3(decoder);
            if (_ordinalDecoder == null)
                throw new InvalidOperationException("ordinal index avcodec_alloc_context3 failed");

            FFmpegErrorHelper.ThrowIfError(
                ffmpeg.avcodec_parameters_to_context(_ordinalDecoder, stream->codecpar),
                "Failed to apply ordinal index codec parameters");
            FFmpegErrorHelper.ThrowIfError(
                ffmpeg.avcodec_open2(_ordinalDecoder, decoder, null),
                "Failed to open ordinal index decoder");

            _ordinalPacket = ffmpeg.av_packet_alloc();
            _ordinalFrame = ffmpeg.av_frame_alloc();
            if (_ordinalPacket == null || _ordinalFrame == null)
                throw new InvalidOperationException("failed to allocate ordinal index decode buffers");
        }

        private bool TryDecodeNextOrdinalIndexFrame(
            CancellationToken cancellationToken,
            out long timestamp)
        {
            timestamp = ffmpeg.AV_NOPTS_VALUE;
            int tryAgain = ffmpeg.AVERROR(ffmpeg.EAGAIN);

            while (true)
            {
                if (cancellationToken.IsCancellationRequested)
                    return false;

                ffmpeg.av_frame_unref(_ordinalFrame);
                int receiveResult = ffmpeg.avcodec_receive_frame(_ordinalDecoder, _ordinalFrame);
                if (receiveResult == 0)
                {
                    timestamp = GetDecodedPresentationTimestamp(_ordinalFrame);
                    return true;
                }

                if (receiveResult == ffmpeg.AVERROR_EOF)
                {
                    _ordinalReachedEndOfStream = true;
                    return false;
                }

                if (receiveResult != tryAgain)
                {
                    throw new InvalidOperationException(
                        $"ordinal index avcodec_receive_frame failed: " +
                        $"{FFmpegErrorHelper.GetErrorMessage(receiveResult)} ({receiveResult})");
                }

                if (_ordinalDrainSent)
                {
                    throw new InvalidOperationException(
                        "ordinal index decoder returned EAGAIN after the drain packet");
                }

                int readResult;
                do
                {
                    if (cancellationToken.IsCancellationRequested)
                        return false;

                    ffmpeg.av_packet_unref(_ordinalPacket);
                    readResult = ffmpeg.av_read_frame(_ordinalFormat, _ordinalPacket);
                }
                while (readResult >= 0 &&
                       _ordinalPacket->stream_index != _ordinalVideoStreamIndex);

                if (readResult == ffmpeg.AVERROR_EOF)
                {
                    ffmpeg.av_packet_unref(_ordinalPacket);
                    if (cancellationToken.IsCancellationRequested)
                        return false;

                    int drainResult = ffmpeg.avcodec_send_packet(_ordinalDecoder, null);
                    _ordinalDrainSent = true;
                    if (drainResult == 0)
                        continue;
                    if (drainResult == ffmpeg.AVERROR_EOF)
                    {
                        _ordinalReachedEndOfStream = true;
                        return false;
                    }

                    throw new InvalidOperationException(
                        $"ordinal index drain failed: " +
                        $"{FFmpegErrorHelper.GetErrorMessage(drainResult)} ({drainResult})");
                }

                if (readResult < 0)
                {
                    ffmpeg.av_packet_unref(_ordinalPacket);
                    throw new InvalidOperationException(
                        $"ordinal index av_read_frame failed: " +
                        $"{FFmpegErrorHelper.GetErrorMessage(readResult)} ({readResult})");
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    ffmpeg.av_packet_unref(_ordinalPacket);
                    return false;
                }

                int sendResult = ffmpeg.avcodec_send_packet(_ordinalDecoder, _ordinalPacket);
                ffmpeg.av_packet_unref(_ordinalPacket);
                if (sendResult == 0)
                    continue;

                throw new InvalidOperationException(
                    $"ordinal index avcodec_send_packet failed: " +
                    $"{FFmpegErrorHelper.GetErrorMessage(sendResult)} ({sendResult})");
            }
        }

        private bool TryRecordDecodedFrameTimelineEntry(int frameIndex, long timestamp)
        {
            lock (_decodedFrameTimeline.SyncRoot)
            {
                _decodedFrameTimeline.LastAccessTicks = DateTime.UtcNow.Ticks;
                if (!_decodedFrameTimeline.IsReliable)
                    return true;

                bool accepted = TryAddOrValidateTimelineEntry(
                    _decodedFrameTimeline,
                    frameIndex,
                    timestamp);
                return accepted || _decodedFrameTimeline.CapacityReached;
            }
        }

        private void MarkDecodedFrameTimelineCompleteIfContiguous()
        {
            if (_sequentialOrdinalMode != SequentialOrdinalMode.DecodeFromBeginning)
                return;

            lock (_decodedFrameTimeline.SyncRoot)
            {
                if (_decodedFrameTimeline.IsReliable &&
                    !_decodedFrameTimeline.CapacityReached &&
                    _decodedFrameTimeline.Entries.Count == _sequentialIndex)
                {
                    _decodedFrameTimeline.IsComplete = true;
                    _decodedFrameTimeline.LastAccessTicks = DateTime.UtcNow.Ticks;
                }
            }
        }

        private static bool TryAddOrValidateTimelineEntry(
            DecodedFrameTimeline timeline,
            int frameIndex,
            long timestamp)
        {
            if (!timeline.IsReliable || frameIndex < 0)
                return false;

            if (frameIndex < timeline.Entries.Count)
            {
                DecodedFrameTimelineEntry existing = timeline.Entries[frameIndex];
                if (existing.PresentationTimestamp != timestamp)
                {
                    timeline.IsReliable = false;
                    Debug.WriteLine(
                        $"[FfFrameExtractor] decoded timeline mismatch at ordinal {frameIndex}: " +
                        $"cached={existing.PresentationTimestamp}/{existing.TimestampOccurrence}, " +
                        $"decoded={timestamp}.");
                    return false;
                }

                return true;
            }

            if (frameIndex != timeline.Entries.Count)
                return false;

            TrimDecodedFrameTimelineCache(timeline);
            lock (_timelineCacheLock)
            {
                if (timeline.Entries.Count >= MaxCachedTimelineFramesPerVideo ||
                    Volatile.Read(ref timeline.IsCacheResident) == 0 ||
                    GetResidentTimelineFrameCountLocked() >=
                        MaxCachedTimelineFramesTotal)
                {
                    timeline.CapacityReached = true;
                    return false;
                }

                if (timestamp != ffmpeg.AV_NOPTS_VALUE)
                {
                    if (timeline.LastValidPresentationTimestamp != ffmpeg.AV_NOPTS_VALUE &&
                        timestamp <= timeline.LastValidPresentationTimestamp)
                    {
                        timeline.SupportsExactTimestampSeek = false;
                        Debug.WriteLine(
                            $"[FfFrameExtractor] decoded timeline PTS is not strictly increasing at " +
                            $"ordinal {frameIndex}: previous={timeline.LastValidPresentationTimestamp}, " +
                            $"current={timestamp}. " +
                            "Timestamp seek is disabled for this source.");
                    }
                    timeline.LastValidPresentationTimestamp = timestamp;
                }

                int occurrence = 0;
                if (timestamp != ffmpeg.AV_NOPTS_VALUE)
                {
                    occurrence = timeline.Entries.Count > 0 &&
                        timeline.Entries[^1].PresentationTimestamp == timestamp
                            ? timeline.Entries[^1].TimestampOccurrence + 1
                            : 1;
                }

                var entry = new DecodedFrameTimelineEntry(timestamp, occurrence);
                timeline.Entries.Add(entry);
                Volatile.Write(
                    ref timeline.EntryCountSnapshot,
                    timeline.Entries.Count);
            }

            if (Volatile.Read(ref timeline.IsCacheResident) != 0 &&
                (timeline.Entries.Count & 4095) == 0)
            {
                TrimDecodedFrameTimelineCache(timeline);
            }
            return true;
        }

        private void ResetSequentialCompletionState()
        {
            _sequentialDrainSent = false;
            _sequentialReachedEndOfStream = false;
            _sequentialCancelled = false;
            _sequentialDecodeError = null;
        }

        private bool EnsureSequentialDecodeResources()
        {
            if (_sequentialPacketReusable == null)
                _sequentialPacketReusable = ffmpeg.av_packet_alloc();
            if (_sequentialDecodedFrameReusable == null)
                _sequentialDecodedFrameReusable = ffmpeg.av_frame_alloc();
            return _sequentialPacketReusable != null && _sequentialDecodedFrameReusable != null;
        }

        private void SetSequentialDecodeError(string message)
        {
            string? detail = _decodeError;
            _sequentialDecodeError = string.IsNullOrWhiteSpace(detail)
                ? message
                : $"{message}: {detail}";
            UpdateDecodeStatus("디코딩: 실패", _sequentialDecodeError);
            Debug.WriteLine($"[FfFrameExtractor] {_sequentialDecodeError}");
        }

        private void SetSequentialDecodeError(string message, int error)
        {
            _sequentialDecodeError =
                $"{message} (ffmpeg: {FFmpegErrorHelper.GetErrorMessage(error)}, code: {error})";
            UpdateDecodeStatus("디코딩: 실패", _sequentialDecodeError);
            Debug.WriteLine($"[FfFrameExtractor] {_sequentialDecodeError}");
        }

        private void CaptureDecodedTimestamp(int frameIndex, long pts)
        {
            double timeBaseSeconds = ffmpeg.av_q2d(_timeBase);
            if (pts != ffmpeg.AV_NOPTS_VALUE && timeBaseSeconds > 0 && double.IsFinite(timeBaseSeconds))
            {
                double timestampSeconds = pts * timeBaseSeconds;
                if (double.IsFinite(timestampSeconds))
                {
                    _lastDecodedTimestampSeconds = timestampSeconds;
                    _lastDecodedTimestampSource = "pts";
                    return;
                }
            }

            _lastDecodedTimestampSeconds = _fps > 0
                ? frameIndex / _fps
                : double.NaN;
            _lastDecodedTimestampSource = double.IsFinite(_lastDecodedTimestampSeconds)
                ? "fps-fallback"
                : "none";
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                lock (_sync)
                {
                DisposeOrdinalDecoder();

                if (_sws != null)
                {
                    ffmpeg.sws_freeContext(_sws);
                    _sws = null;
                }
                if (_swsScaled != null)
                {
                    ffmpeg.sws_freeContext(_swsScaled);
                    _swsScaled = null;
                }

                if (_sequentialPacketReusable != null)
                {
                    fixed (AVPacket** packet = &_sequentialPacketReusable)
                        ffmpeg.av_packet_free(packet);
                }
                if (_sequentialDecodedFrameReusable != null)
                {
                    fixed (AVFrame** decodedFrame = &_sequentialDecodedFrameReusable)
                        ffmpeg.av_frame_free(decodedFrame);
                }

                if (_hwDeviceCtx != null)
                {
                    fixed (AVBufferRef** pHw = &_hwDeviceCtx)
                    {
                        ffmpeg.av_buffer_unref(pHw);
                    }
                    _hwDeviceCtx = null;
                }

                if (_dec != null)
                {
                    UnregisterHardwareFormat(_dec);
                    fixed (AVCodecContext** pDec = &_dec)
                    {
                        ffmpeg.avcodec_free_context(pDec);
                    }
                    _dec = null;
                }

                if (_bgraReusable != null)
                {
                    fixed (AVFrame** pBgra = &_bgraReusable)
                    {
                        ffmpeg.av_frame_free(pBgra);
                    }
                    _bgraReusable = null;
                }
                if (_bgraScaledReusable != null)
                {
                    fixed (AVFrame** pBgra = &_bgraScaledReusable)
                    {
                        ffmpeg.av_frame_free(pBgra);
                    }
                    _bgraScaledReusable = null;
                }

                    if (_fmt != null)
                    {
                        fixed (AVFormatContext** pFmt = &_fmt)
                        {
                            ffmpeg.avformat_close_input(pFmt);
                        }
                        _fmt = null;
                    }
                }
            }
            finally
            {
                if (Interlocked.Exchange(ref _timelineOwnerReleased, 1) == 0)
                    ReleaseDecodedFrameTimeline(_decodedFrameTimeline);
            }
        }

        private void DisposeOrdinalDecoder()
        {
            if (_ordinalPacket != null)
            {
                fixed (AVPacket** packet = &_ordinalPacket)
                    ffmpeg.av_packet_free(packet);
            }

            if (_ordinalFrame != null)
            {
                fixed (AVFrame** frame = &_ordinalFrame)
                    ffmpeg.av_frame_free(frame);
            }

            if (_ordinalDecoder != null)
            {
                fixed (AVCodecContext** decoder = &_ordinalDecoder)
                    ffmpeg.avcodec_free_context(decoder);
            }

            if (_ordinalFormat != null)
            {
                fixed (AVFormatContext** format = &_ordinalFormat)
                    ffmpeg.avformat_close_input(format);
            }

            _ordinalVideoStreamIndex = -1;
        }

        private void TryInitializeHardwareDevice()
        {
            if (_hwDeviceInitialized)
                return;

            _hwDeviceInitialized = true;
            UpdateDecodeStatus("디코딩: HW 미사용");

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                if (TryCreateHwDevice(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA))
                    return;
                TryCreateHwDevice(AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2);
                return;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                TryCreateHwDevice(AVHWDeviceType.AV_HWDEVICE_TYPE_VIDEOTOOLBOX);
            }
        }

        private bool TryCreateHwDevice(AVHWDeviceType deviceType)
        {
            if (deviceType == AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
                return false;

            if (!TryGetHardwarePixelFormat(deviceType, out var hwFmt))
            {
                UpdateDecodeStatus($"디코딩: HW 미지원 ({deviceType})");
                UpdateDecodeDiagnostics(BuildDecoderDiagnostics(deviceType, AVPixelFormat.AV_PIX_FMT_NONE));
                return false;
            }

            AVBufferRef* hwDevice = null;
            int result = ffmpeg.av_hwdevice_ctx_create(&hwDevice, deviceType, null, null, 0);
            if (result < 0 || hwDevice == null)
                return false;

            _hwDeviceCtx = hwDevice;
            _dec->hw_device_ctx = ffmpeg.av_buffer_ref(hwDevice);
            ConfigureHardwareDecoder(deviceType, hwFmt);
            UpdateDecodeStatus($"디코딩: HW 디바이스 초기화됨 ({deviceType})");
            UpdateDecodeDiagnostics(BuildDecoderDiagnostics(deviceType, hwFmt));
            return true;
        }


        private WriteableBitmap? ConvertDecodedFrameToBitmap(AVFrame* src, AVFrame* bgra)
        {
            if (!ConvertDecodedFrameToBgra(src, bgra))
                return null;

            return ToBitmap(bgra);
        }

        private bool ConvertDecodedFrameToBgra(AVFrame* src, AVFrame* bgra)
        {
            return ConvertDecodedFrameToBgraInternal(
                src,
                bgra,
                _dec->width,
                _dec->height,
                SwsFlags.SWS_BILINEAR,
                ref _sws);
        }

        private bool ConvertDecodedFrameToBgraScaled(
            AVFrame* src,
            AVFrame* bgra,
            int dstW,
            int dstH,
            SwsFlags flags)
        {
            return ConvertDecodedFrameToBgraInternal(
                src,
                bgra,
                dstW,
                dstH,
                flags,
                ref _swsScaled);
        }

        private bool ConvertDecodedFrameToBgraInternal(
            AVFrame* src,
            AVFrame* bgra,
            int dstW,
            int dstH,
            SwsFlags flags,
            ref SwsContext* sws)
        {
            AVFrame* swFrame = src;
            AVFrame* temp = null;

            if (_hwDeviceCtx != null && IsHardwareFrame(src))
            {
                if (_dec->sw_pix_fmt == AVPixelFormat.AV_PIX_FMT_NONE)
                {
                    UpdateDecodeStatus("디코딩: HW 프레임 전송 실패", "sw_pix_fmt 미설정");
                    return false;
                }

                temp = ffmpeg.av_frame_alloc();
                if (temp != null)
                {
                    temp->format = (int)_dec->sw_pix_fmt;
                    temp->width = src->width;
                    temp->height = src->height;

                    if (ffmpeg.av_frame_get_buffer(temp, 32) < 0)
                    {
                        UpdateDecodeStatus("디코딩: HW 프레임 전송 실패", "av_frame_get_buffer 실패");
                        ffmpeg.av_frame_free(&temp);
                        temp = null;
                        return false;
                    }

                    if (ffmpeg.av_hwframe_transfer_data(temp, src, 0) == 0)
                    {
                        swFrame = temp;
                        UpdateDecodeStatus("디코딩: HW 프레임 전송 성공");
                    }
                    else
                    {
                        UpdateDecodeStatus("디코딩: HW 프레임 전송 실패", "av_hwframe_transfer_data 실패");
                        ffmpeg.av_frame_free(&temp);
                        temp = null;
                        return false;
                    }
                }
            }
            else if (_hwDeviceCtx != null)
            {
                UpdateDecodeStatus("디코딩: SW 디코더 사용");
            }
            else
            {
                UpdateDecodeStatus("디코딩: SW 디코더 사용");
            }

            AVPixelFormat srcFmt = (AVPixelFormat)swFrame->format;
            int srcW = swFrame->width;
            int srcH = swFrame->height;

            sws = ffmpeg.sws_getCachedContext(
                sws,
                srcW, srcH, srcFmt,
                dstW, dstH, AVPixelFormat.AV_PIX_FMT_BGRA,
                (int)flags,
                null, null, null);

            if (sws == null)
            {
                if (temp != null)
                    ffmpeg.av_frame_free(&temp);
                return false;
            }

            ffmpeg.sws_scale(
                sws,
                swFrame->data, swFrame->linesize,
                0, srcH,
                bgra->data, bgra->linesize);

            if (temp != null)
                ffmpeg.av_frame_free(&temp);

            return true;
        }

        private bool ConvertDecodedFrameToBgraToBuffer(
            AVFrame* src,
            byte_ptrArray4 dstData,
            int_array4 dstLinesize,
            int dstW,
            int dstH,
            SwsFlags flags,
            ref SwsContext* sws)
        {
            AVFrame* swFrame = src;
            AVFrame* temp = null;

            if (_hwDeviceCtx != null && IsHardwareFrame(src))
            {
                if (_dec->sw_pix_fmt == AVPixelFormat.AV_PIX_FMT_NONE)
                {
                    UpdateDecodeStatus("디코딩: HW 프레임 전송 실패", "sw_pix_fmt 미설정");
                    return false;
                }

                temp = ffmpeg.av_frame_alloc();
                if (temp != null)
                {
                    temp->format = (int)_dec->sw_pix_fmt;
                    temp->width = src->width;
                    temp->height = src->height;

                    if (ffmpeg.av_frame_get_buffer(temp, 32) < 0)
                    {
                        UpdateDecodeStatus("디코딩: HW 프레임 전송 실패", "av_frame_get_buffer 실패");
                        ffmpeg.av_frame_free(&temp);
                        temp = null;
                        return false;
                    }

                    if (ffmpeg.av_hwframe_transfer_data(temp, src, 0) == 0)
                    {
                        swFrame = temp;
                        UpdateDecodeStatus("디코딩: HW 프레임 전송 성공");
                    }
                    else
                    {
                        UpdateDecodeStatus("디코딩: HW 프레임 전송 실패", "av_hwframe_transfer_data 실패");
                        ffmpeg.av_frame_free(&temp);
                        temp = null;
                        return false;
                    }
                }
            }
            else if (_hwDeviceCtx != null)
            {
                UpdateDecodeStatus("디코딩: SW 디코더 사용");
            }
            else
            {
                UpdateDecodeStatus("디코딩: SW 디코더 사용");
            }

            AVPixelFormat srcFmt = (AVPixelFormat)swFrame->format;
            int srcW = swFrame->width;
            int srcH = swFrame->height;

            sws = ffmpeg.sws_getCachedContext(
                sws,
                srcW, srcH, srcFmt,
                dstW, dstH, AVPixelFormat.AV_PIX_FMT_BGRA,
                (int)flags,
                null, null, null);

            if (sws == null)
            {
                if (temp != null)
                    ffmpeg.av_frame_free(&temp);
                return false;
            }

            ffmpeg.sws_scale(
                sws,
                swFrame->data, swFrame->linesize,
                0, srcH,
                dstData, dstLinesize);

            if (temp != null)
                ffmpeg.av_frame_free(&temp);

            return true;
        }

        private bool EnsureReusableBgraFrame()
        {
            int w = _dec->width;
            int h = _dec->height;

            if (_bgraReusable != null && _bgraReusableWidth == w && _bgraReusableHeight == h)
                return true;

            if (_bgraReusable != null)
            {
                fixed (AVFrame** pBgra = &_bgraReusable)
                {
                    ffmpeg.av_frame_free(pBgra);
                }
                _bgraReusable = null;
            }

            _bgraReusable = ffmpeg.av_frame_alloc();
            if (_bgraReusable == null)
                return false;

            _bgraReusable->format = (int)AVPixelFormat.AV_PIX_FMT_BGRA;
            _bgraReusable->width = w;
            _bgraReusable->height = h;

            if (ffmpeg.av_frame_get_buffer(_bgraReusable, 32) < 0)
            {
                fixed (AVFrame** pBgra = &_bgraReusable)
                {
                    ffmpeg.av_frame_free(pBgra);
                }
                _bgraReusable = null;
                return false;
            }

            _bgraReusableWidth = w;
            _bgraReusableHeight = h;
            return true;
        }

        private bool EnsureReusableScaledBgraFrame(int w, int h)
        {
            if (_bgraScaledReusable != null && _bgraScaledWidth == w && _bgraScaledHeight == h)
                return true;

            if (_bgraScaledReusable != null)
            {
                fixed (AVFrame** pBgra = &_bgraScaledReusable)
                {
                    ffmpeg.av_frame_free(pBgra);
                }
                _bgraScaledReusable = null;
            }

            _bgraScaledReusable = ffmpeg.av_frame_alloc();
            if (_bgraScaledReusable == null)
                return false;

            _bgraScaledReusable->format = (int)AVPixelFormat.AV_PIX_FMT_BGRA;
            _bgraScaledReusable->width = w;
            _bgraScaledReusable->height = h;

            if (ffmpeg.av_frame_get_buffer(_bgraScaledReusable, 32) < 0)
            {
                fixed (AVFrame** pBgra = &_bgraScaledReusable)
                {
                    ffmpeg.av_frame_free(pBgra);
                }
                _bgraScaledReusable = null;
                return false;
            }

            _bgraScaledWidth = w;
            _bgraScaledHeight = h;
            return true;
        }

        private void ConfigureHardwareDecoder(AVHWDeviceType deviceType, AVPixelFormat hwPixelFormat)
        {
            _hwPixelFormat = hwPixelFormat;
            if (_hwPixelFormat == AVPixelFormat.AV_PIX_FMT_NONE)
                return;

            RegisterHardwareFormat(_dec, _hwPixelFormat);
            _getFormatManaged = GetHardwareFormat;
            _dec->get_format = _getFormatManaged;
            UpdateDecodeStatus($"디코딩: HW 포맷 요청됨 ({_hwPixelFormat})");
            UpdateDecodeDiagnostics(BuildDecoderDiagnostics(deviceType, _hwPixelFormat));
        }

        private bool TryGetHardwarePixelFormat(AVHWDeviceType deviceType, out AVPixelFormat hwPixelFormat)
        {
            hwPixelFormat = AVPixelFormat.AV_PIX_FMT_NONE;

            AVCodec* codec = _dec->codec;
            if (codec == null)
                return false;

            for (int i = 0; ; i++)
            {
                AVCodecHWConfig* config = ffmpeg.avcodec_get_hw_config(codec, i);
                if (config == null)
                    break;

                if (config->device_type != deviceType)
                    continue;

                hwPixelFormat = config->pix_fmt;
                return hwPixelFormat != AVPixelFormat.AV_PIX_FMT_NONE;
            }

            return false;
        }

        private static AVPixelFormat GetHardwareFormat(AVCodecContext* ctx, AVPixelFormat* pixFmts)
        {
            if (pixFmts == null)
                return AVPixelFormat.AV_PIX_FMT_NONE;

            AVPixelFormat target = AVPixelFormat.AV_PIX_FMT_NONE;
            FfFrameExtractor? owner = null;
            lock (_hwFormatLock)
            {
                IntPtr key = (IntPtr)ctx;
                _hwFormatByDecoder.TryGetValue(key, out target);
                if (_hwOwnerByDecoder.TryGetValue(key, out var ownerRef))
                    ownerRef.TryGetTarget(out owner);
            }

            for (AVPixelFormat* p = pixFmts; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
            {
                if (*p == target)
                {
                    owner?.UpdateDecodeStatus(
                        $"디코딩: HW 픽셀 포맷 선택됨 ({target})");
                    owner?.UpdateDecodeDiagnostics(
                        BuildFormatList("get_format", pixFmts, target));
                    return *p;
                }
            }

            owner?.UpdateDecodeStatus("디코딩: HW 픽셀 포맷 미지원");
            owner?.UpdateDecodeDiagnostics(
                BuildFormatList("get_format", pixFmts, target));
            return *pixFmts;
        }

        private static string BuildFormatList(string label, AVPixelFormat* pixFmts, AVPixelFormat target)
        {
            var formats = new List<string>();
            for (AVPixelFormat* p = pixFmts; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
            {
                formats.Add(p->ToString());
            }

            string list = formats.Count == 0 ? "none" : string.Join(", ", formats);
            return $"{label}: target={target}, formats={list}";
        }

        private void RegisterHardwareFormat(AVCodecContext* ctx, AVPixelFormat format)
        {
            if (ctx == null)
                return;

            lock (_hwFormatLock)
            {
                IntPtr key = (IntPtr)ctx;
                _hwFormatByDecoder[key] = format;
                _hwOwnerByDecoder[key] =
                    new WeakReference<FfFrameExtractor>(this);
            }
        }

        private void UnregisterHardwareFormat(AVCodecContext* ctx)
        {
            if (ctx == null)
                return;

            lock (_hwFormatLock)
            {
                IntPtr key = (IntPtr)ctx;
                _hwFormatByDecoder.Remove(key);
                _hwOwnerByDecoder.Remove(key);
            }
        }

        private string BuildDecoderDiagnostics(AVHWDeviceType deviceType, AVPixelFormat selectedFmt)
        {
            string decoderName = _dec != null && _dec->codec != null
                ? Marshal.PtrToStringAnsi((IntPtr)_dec->codec->name) ?? "unknown"
                : "unknown";

            var formats = new List<string>();

            if (_dec != null && _dec->codec != null)
            {
                for (int i = 0; ; i++)
                {
                    AVCodecHWConfig* config = ffmpeg.avcodec_get_hw_config(_dec->codec, i);
                    if (config == null)
                        break;

                    string fmt = config->pix_fmt.ToString();
                    string dev = config->device_type.ToString();
                    formats.Add($"{dev}:{fmt}");
                }
            }

            string list = formats.Count == 0 ? "none" : string.Join(", ", formats);
            return $"decoder={decoderName}, device={deviceType}, selected={selectedFmt}, hw_configs={list}";
        }

        private static bool IsHardwareFrame(AVFrame* frame)
        {
            if (frame == null)
                return false;

            var desc = ffmpeg.av_pix_fmt_desc_get((AVPixelFormat)frame->format);
            if (desc == null)
                return false;

            return (desc->flags & ffmpeg.AV_PIX_FMT_FLAG_HWACCEL) != 0;
        }

        private static WriteableBitmap ToBitmap(AVFrame* bgra)
        {
            int w = bgra->width;
            int h = bgra->height;

            var bmp = new WriteableBitmap(
                new PixelSize(w, h),
                new Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888,
                Avalonia.Platform.AlphaFormat.Premul);

            using var fb = bmp.Lock();

            byte* dst = (byte*)fb.Address;
            byte* src = bgra->data[0];

            int dstStride = fb.RowBytes;
            int srcStride = bgra->linesize[0];
            int copy = Math.Min(srcStride, dstStride);

            for (int y = 0; y < h; y++)
            {
                Buffer.MemoryCopy(
                    src + y * srcStride,
                    dst + y * dstStride,
                    dstStride,
                    copy);
            }

            return bmp;
        }
    }
}
