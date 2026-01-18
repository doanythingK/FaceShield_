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
        private bool _disposed;

        public TimelineThumbnailProvider(string videoPath, int thumbWidth = 160, int thumbHeight = 90)
        {
            _videoPath = videoPath;
            _thumbWidth = thumbWidth;
            _thumbHeight = thumbHeight;

            Open();
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

            for (int i = 0; i < _fmt->nb_streams; i++)
            {
                if (_fmt->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                {
                    _videoStreamIndex = i;
                    break;
                }
            }

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

            int openResult = ffmpeg.avcodec_open2(_dec, codec, null);
            FFmpegErrorHelper.ThrowIfError(openResult, "Failed to open decoder.");

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
            if (frameIndex < 0) return null;

            if (_cache.TryGetValue(frameIndex, out var cached))
                return cached;

            lock (_sync)
            {
                if (_disposed)
                    return null;

                if (_cache.TryGetValue(frameIndex, out cached))
                    return cached;

                var bmp = DecodeFrame(frameIndex);
                if (bmp != null)
                    _cache.TryAdd(frameIndex, bmp);

                return bmp;
            }
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

            ffmpeg.av_seek_frame(_fmt, _videoStreamIndex, targetPts, ffmpeg.AVSEEK_FLAG_BACKWARD);
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

                while (ffmpeg.av_read_frame(_fmt, pkt) >= 0)
                {
                    if (pkt->stream_index != _videoStreamIndex)
                    {
                        ffmpeg.av_packet_unref(pkt);
                        continue;
                    }

                    ffmpeg.avcodec_send_packet(_dec, pkt);
                    ffmpeg.av_packet_unref(pkt);

                    if (ffmpeg.avcodec_receive_frame(_dec, src) == 0)
                    {
                        long pts = src->best_effort_timestamp;
                        if (pts == ffmpeg.AV_NOPTS_VALUE)
                            pts = src->pts;

                        if (pts != ffmpeg.AV_NOPTS_VALUE && pts < targetPts)
                            continue;

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

                        using (var fb = bmp.Lock())
                        {
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
                        }

                        return bmp;
                    }
                }
            }
            finally
            {
                if (pkt != null) ffmpeg.av_packet_free(&pkt);
                if (src != null) ffmpeg.av_frame_free(&src);
                if (dst != null) ffmpeg.av_frame_free(&dst);
            }

            return null;
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
