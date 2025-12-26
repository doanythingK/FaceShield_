using Avalonia;
using Avalonia.Media.Imaging;
using FFmpeg.AutoGen;
using System;
using System.Reflection.Metadata.Ecma335;

namespace FaceShield.Services.Video
{
    public unsafe sealed class FfFrameExtractor : IDisposable
    {
        private readonly object _sync = new();

        private AVFormatContext* _fmt;
        private AVCodecContext* _dec;
        private SwsContext* _sws;
        private int _videoStreamIndex = -1;

        private AVRational _timeBase;
        private double _fps;

        private bool _disposed;

        public FfFrameExtractor(string videoPath)
        {
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);

            // open input (AVFormatContext**)
            fixed (AVFormatContext** pFmt = &_fmt)
            {
                int r = ffmpeg.avformat_open_input(pFmt, videoPath, null, null);
                if (r < 0) throw new InvalidOperationException("avformat_open_input failed");
            }

            if (ffmpeg.avformat_find_stream_info(_fmt, null) < 0)
                throw new InvalidOperationException("avformat_find_stream_info failed");

            for (int i = 0; i < _fmt->nb_streams; i++)
            {
                if (_fmt->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                {
                    _videoStreamIndex = i;
                    break;
                }
            }
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

            if (ffmpeg.avcodec_parameters_to_context(_dec, stream->codecpar) < 0)
                throw new InvalidOperationException("avcodec_parameters_to_context failed");

            if (ffmpeg.avcodec_open2(_dec, codec, null) < 0)
                throw new InvalidOperationException("avcodec_open2 failed");

            _sws = ffmpeg.sws_getContext(
                _dec->width, _dec->height, _dec->pix_fmt,
                _dec->width, _dec->height, AVPixelFormat.AV_PIX_FMT_BGRA,
                (int)SwsFlags.SWS_BILINEAR,
                null, null, null);

            if (_sws == null)
                throw new InvalidOperationException("sws_getContext failed");
        }

        /// <summary>
        /// 지정 프레임 인덱스의 BGRA WriteableBitmap 반환.
        /// 실패 시 null.
        /// (FFmpeg 세션은 인스턴스 수명 동안 유지)
        /// </summary>
        public WriteableBitmap? GetFrameByIndex(int frameIndex)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(FfFrameExtractor));
            if (frameIndex < 0) return null;

            lock (_sync)
            {
                // frameIndex -> seconds -> PTS
                double tbSec = ffmpeg.av_q2d(_timeBase);
                if (tbSec <= 0) return null;

                double seconds = frameIndex / _fps;
                long targetPts = (long)Math.Floor(seconds / tbSec);

                // seek + flush
                ffmpeg.av_seek_frame(_fmt, _videoStreamIndex, targetPts, ffmpeg.AVSEEK_FLAG_BACKWARD);
                ffmpeg.avcodec_flush_buffers(_dec);

                AVPacket* pkt = ffmpeg.av_packet_alloc();
                AVFrame* src = ffmpeg.av_frame_alloc();
                AVFrame* bgra = ffmpeg.av_frame_alloc();

                if (pkt == null || src == null || bgra == null)
                {
                    if (pkt != null) ffmpeg.av_packet_free(&pkt);
                    if (src != null) ffmpeg.av_frame_free(&src);
                    if (bgra != null) ffmpeg.av_frame_free(&bgra);
                    return null;
                }

                try
                {
                    bgra->format = (int)AVPixelFormat.AV_PIX_FMT_BGRA;
                    bgra->width = _dec->width;
                    bgra->height = _dec->height;

                    if (ffmpeg.av_frame_get_buffer(bgra, 32) < 0)
                        return null;

                    long decodedIndex = (long)Math.Round(seconds * _fps);

                    long currentIndex = -1;

                    while (ffmpeg.av_read_frame(_fmt, pkt) >= 0)
                    {
                        if (pkt->stream_index != _videoStreamIndex)
                        {
                            ffmpeg.av_packet_unref(pkt);
                            continue;
                        }

                        ffmpeg.avcodec_send_packet(_dec, pkt);
                        ffmpeg.av_packet_unref(pkt);

                        while (ffmpeg.avcodec_receive_frame(_dec, src) == 0)
                        {
                            currentIndex++;

                            if (currentIndex < decodedIndex)
                                continue; // ❗ 여기서 프레임 스킵

                            ffmpeg.sws_scale(
                                _sws,
                                src->data, src->linesize,
                                0, src->height,
                                bgra->data, bgra->linesize);

                            return ToBitmap(bgra);
                        }
                    }

                    return null;
                }
                finally
                {
                    ffmpeg.av_packet_free(&pkt);
                    ffmpeg.av_frame_free(&src);
                    ffmpeg.av_frame_free(&bgra);
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            lock (_sync)
            {
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
                    _dec = null;
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
