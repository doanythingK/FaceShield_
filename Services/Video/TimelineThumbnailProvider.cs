using Avalonia;
using Avalonia.Media.Imaging;
using FFmpeg.AutoGen;
using System;
using System.Collections.Concurrent;

namespace FaceShield.Services.Video
{
    public unsafe sealed class TimelineThumbnailProvider : IDisposable
    {
        private readonly string _videoPath;
        private readonly int _thumbWidth;
        private readonly int _thumbHeight;
        private readonly object _sync = new();

        private AVFormatContext* _fmt;
        private AVCodecContext* _dec;
        private SwsContext* _sws;
        private int _videoStreamIndex = -1;

        // ✅ seek 계산용 메타
        private AVRational _timeBase;
        private double _fps;

        private readonly ConcurrentDictionary<int, WriteableBitmap> _cache = new();
        private readonly ConcurrentDictionary<int, long> _cacheAccess = new();
        private readonly int _maxCacheEntries;
        private long _cacheAccessClock;
        private bool _disposed;

        public TimelineThumbnailProvider(string videoPath, int thumbWidth = 160, int thumbHeight = 90)
        {
            _videoPath = videoPath;
            _thumbWidth = thumbWidth;
            _thumbHeight = thumbHeight;

            try
            {
                Open();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private void Open()
        {
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);

            fixed (AVFormatContext** pFmt = &_fmt)
            {
                int openResult = ffmpeg.avformat_open_input(pFmt, _videoPath, null, null);
                FFmpegErrorHelper.ThrowIfError(openResult, $"Failed to open video: {_videoPath}");

                int streamInfo = ffmpeg.avformat_find_stream_info(_fmt, null);
                FFmpegErrorHelper.ThrowIfError(streamInfo, $"Failed to read stream info: {_videoPath}");
            }

            _videoStreamIndex = FFmpegStreamSelection.FindPrimaryVideoStreamIndex(_fmt);

            if (_videoStreamIndex < 0)
                throw new InvalidOperationException("Video stream not found.");

            AVStream* stream = _fmt->streams[_videoStreamIndex];

            // ✅ time_base / fps 저장 (seek 계산에 사용)
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
                throw new InvalidOperationException("Decoder not found.");

            _dec = ffmpeg.avcodec_alloc_context3(codec);
            if (_dec == null)
                throw new InvalidOperationException("avcodec_alloc_context3 failed.");

            int parResult = ffmpeg.avcodec_parameters_to_context(_dec, stream->codecpar);
            FFmpegErrorHelper.ThrowIfError(parResult, "Failed to apply codec parameters.");

            int decoderOpenResult = ffmpeg.avcodec_open2(_dec, codec, null);
            FFmpegErrorHelper.ThrowIfError(decoderOpenResult, "Failed to open decoder.");

            _sws = ffmpeg.sws_getContext(
                _dec->width,
                _dec->height,
                _dec->pix_fmt,
                _thumbWidth,
                _thumbHeight,
                AVPixelFormat.AV_PIX_FMT_BGRA,
                (int)SwsFlags.SWS_BILINEAR,
                null, null, null);

            if (_sws == null)
                throw new InvalidOperationException("sws_getContext failed.");
        }

        public WriteableBitmap? GetThumbnail(int frameIndex)
        {
            if (frameIndex < 0)
                return null;

            if (_cache.TryGetValue(frameIndex, out var cached))
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

                var bmp = DecodeFrame(frameIndex);
                if (bmp == null)
                    return null;

                _cache[frameIndex] = bmp;
                TouchCacheEntry(frameIndex);
                TrimCacheIfNeeded(frameIndex);
                return bmp;
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
            if (frameIndex < 0)
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

        private WriteableBitmap? DecodeFrame(int frameIndex)
        {
            if (_fmt == null || _dec == null || _videoStreamIndex < 0 || _sws == null)
                return null;

            // ✅ frameIndex -> seconds -> stream time_base 기준 PTS로 변환
            // time_base 초 단위 = av_q2d(time_base)
            double tbSec = ffmpeg.av_q2d(_timeBase);
            if (tbSec <= 0) tbSec = 1.0 / 90000.0; // (확실하지 않음) 매우 드문 방어

            double seconds = frameIndex / _fps;
            long targetPts = (long)Math.Floor(seconds / tbSec);

            int seekResult = ffmpeg.av_seek_frame(
                _fmt,
                _videoStreamIndex,
                targetPts,
                ffmpeg.AVSEEK_FLAG_BACKWARD);
            if (seekResult < 0)
                return null;
            ffmpeg.avcodec_flush_buffers(_dec);

            AVPacket* pkt = ffmpeg.av_packet_alloc();
            AVFrame* src = ffmpeg.av_frame_alloc();
            AVFrame* dst = ffmpeg.av_frame_alloc();

            try
            {
                if (pkt == null || src == null || dst == null)
                    return null;

                dst->format = (int)AVPixelFormat.AV_PIX_FMT_BGRA;
                dst->width = _thumbWidth;
                dst->height = _thumbHeight;

                if (ffmpeg.av_frame_get_buffer(dst, 32) < 0)
                    return null;

                int tryAgain = ffmpeg.AVERROR(ffmpeg.EAGAIN);
                bool drainSent = false;
                while (true)
                {
                    ffmpeg.av_frame_unref(src);
                    int receiveResult = ffmpeg.avcodec_receive_frame(_dec, src);
                    if (receiveResult == 0)
                    {
                        long pts = src->best_effort_timestamp;
                        if (pts == ffmpeg.AV_NOPTS_VALUE)
                            pts = src->pts;

                        if (pts != ffmpeg.AV_NOPTS_VALUE && pts < targetPts)
                            continue;

                        return CreateThumbnail(src, dst);
                    }

                    if (receiveResult == ffmpeg.AVERROR_EOF)
                        return null;
                    if (receiveResult != tryAgain || drainSent)
                        return null;

                    int readResult;
                    do
                    {
                        ffmpeg.av_packet_unref(pkt);
                        readResult = ffmpeg.av_read_frame(_fmt, pkt);
                    }
                    while (readResult >= 0 && pkt->stream_index != _videoStreamIndex);

                    if (readResult == ffmpeg.AVERROR_EOF)
                    {
                        ffmpeg.av_packet_unref(pkt);
                        int drainResult = ffmpeg.avcodec_send_packet(_dec, null);
                        drainSent = true;
                        if (drainResult == 0)
                            continue;
                        return null;
                    }

                    if (readResult < 0)
                    {
                        ffmpeg.av_packet_unref(pkt);
                        return null;
                    }

                    int sendResult = ffmpeg.avcodec_send_packet(_dec, pkt);
                    ffmpeg.av_packet_unref(pkt);
                    if (sendResult < 0)
                        return null;
                }
            }
            finally
            {
                if (pkt != null) ffmpeg.av_packet_free(&pkt);
                if (src != null) ffmpeg.av_frame_free(&src);
                if (dst != null) ffmpeg.av_frame_free(&dst);
            }

        }

        private WriteableBitmap CreateThumbnail(AVFrame* src, AVFrame* dst)
        {
            ffmpeg.sws_scale(
                _sws,
                src->data,
                src->linesize,
                0,
                src->height,
                dst->data,
                dst->linesize);

            var bmp = new WriteableBitmap(
                new PixelSize(_thumbWidth, _thumbHeight),
                new Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888,
                Avalonia.Platform.AlphaFormat.Premul);

            using var fb = bmp.Lock();
            byte* dstPtr = (byte*)fb.Address;
            byte* srcPtr = dst->data[0];
            int srcStride = dst->linesize[0];
            int dstStride = fb.RowBytes;
            int copyBytesPerRow = Math.Min(srcStride, dstStride);

            for (int y = 0; y < _thumbHeight; y++)
            {
                Buffer.MemoryCopy(
                    srcPtr + y * srcStride,
                    dstPtr + y * dstStride,
                    dstStride,
                    copyBytesPerRow);
            }

            return bmp;
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;
                _disposed = true;

                foreach (var kv in _cache)
                    kv.Value.Dispose();
                _cache.Clear();
                _cacheAccess.Clear();

                if (_sws != null)
                {
                    ffmpeg.sws_freeContext(_sws);
                    _sws = null;
                }

                if (_dec != null)
                {
                    fixed (AVCodecContext** pDec = &_dec)
                    {
                        ffmpeg.avcodec_free_context(pDec);
                    }
                }

                if (_fmt != null)
                {
                    fixed (AVFormatContext** pFmt = &_fmt)
                    {
                        ffmpeg.avformat_close_input(pFmt);
                    }
                }
            }
        }
    }
}
