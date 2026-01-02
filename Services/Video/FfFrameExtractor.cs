using Avalonia;
using Avalonia.Media.Imaging;
using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.InteropServices;
using System.Threading;

namespace FaceShield.Services.Video
{
    public unsafe sealed class FfFrameExtractor : IDisposable
    {
        private static readonly object _decodeStatusLock = new();
        private static string _lastDecodeStatus = "디코딩: 확인 중";
        private static string? _lastDecodeError;
        private static string? _lastDecodeDiagnostics;
        private static readonly object _hwFormatLock = new();
        private static readonly Dictionary<IntPtr, AVPixelFormat> _hwFormatByDecoder = new();

        public static string GetLastDecodeStatus()
        {
            lock (_decodeStatusLock)
            {
                return _lastDecodeStatus;
            }
        }

        public static string? GetLastDecodeError()
        {
            lock (_decodeStatusLock)
            {
                return _lastDecodeError;
            }
        }

        public static string? GetLastDecodeDiagnostics()
        {
            lock (_decodeStatusLock)
            {
                return _lastDecodeDiagnostics;
            }
        }

        private static void UpdateDecodeStatus(string status, string? error = null)
        {
            lock (_decodeStatusLock)
            {
                _lastDecodeStatus = status;
                _lastDecodeError = error;
            }
        }

        private static void UpdateDecodeDiagnostics(string diagnostics)
        {
            lock (_decodeStatusLock)
            {
                _lastDecodeDiagnostics = diagnostics;
            }
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
        private int _videoStreamIndex = -1;
        private AVBufferRef* _hwDeviceCtx;
        private AVPixelFormat _hwPixelFormat = AVPixelFormat.AV_PIX_FMT_NONE;
        private AVCodecContext_get_format? _getFormatManaged;
        private bool _hwDeviceInitialized;

        private AVRational _timeBase;
        private double _fps;

        private bool _disposed;
        private bool _sequentialActive;
        private bool _sequentialStarted;
        private int _sequentialIndex;
        private long _sequentialTargetPts;

        private AVFrame* _bgraReusable;
        private int _bgraReusableWidth;
        private int _bgraReusableHeight;

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

            TryInitializeHardwareDevice();

            if (ffmpeg.avcodec_open2(_dec, codec, null) < 0)
                throw new InvalidOperationException("avcodec_open2 failed");
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
                            long pts = src->best_effort_timestamp;
                            if (pts == ffmpeg.AV_NOPTS_VALUE)
                                pts = src->pts;

                            if (pts != ffmpeg.AV_NOPTS_VALUE && pts < targetPts)
                                continue;

                            var bmp = ConvertDecodedFrameToBitmap(src, bgra);
                            if (bmp != null)
                                return bmp;
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

        public void StartSequentialRead(int startFrameIndex)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(FfFrameExtractor));
            if (startFrameIndex < 0) startFrameIndex = 0;

            lock (_sync)
            {
                double tbSec = ffmpeg.av_q2d(_timeBase);
                if (tbSec <= 0)
                    throw new InvalidOperationException("time_base is invalid");

                double seconds = startFrameIndex / _fps;
                _sequentialTargetPts = (long)Math.Floor(seconds / tbSec);

                ffmpeg.av_seek_frame(_fmt, _videoStreamIndex, _sequentialTargetPts, ffmpeg.AVSEEK_FLAG_BACKWARD);
                ffmpeg.avcodec_flush_buffers(_dec);

                _sequentialIndex = startFrameIndex;
                _sequentialActive = true;
                _sequentialStarted = false;
            }
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
                    return false;
                }

                try
                {
                    if (requireBitmap)
                    {
                        bgra->format = (int)AVPixelFormat.AV_PIX_FMT_BGRA;
                        bgra->width = _dec->width;
                        bgra->height = _dec->height;

                        if (ffmpeg.av_frame_get_buffer(bgra, 32) < 0)
                            return false;
                    }

                    while (!ct.IsCancellationRequested && ffmpeg.av_read_frame(_fmt, pkt) >= 0)
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
                            long pts = src->best_effort_timestamp;
                            if (pts == ffmpeg.AV_NOPTS_VALUE)
                                pts = src->pts;

                            if (!_sequentialStarted && pts != ffmpeg.AV_NOPTS_VALUE && pts < _sequentialTargetPts)
                                continue;

                            _sequentialStarted = true;

                            frameIndex = _sequentialIndex++;
                            if (!requireBitmap)
                                return true;

                            var bmp = ConvertDecodedFrameToBitmap(src, bgra);
                            if (bmp != null)
                            {
                                frame = bmp;
                                return true;
                            }
                        }
                    }

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

                AVPacket* pkt = ffmpeg.av_packet_alloc();
                AVFrame* src = ffmpeg.av_frame_alloc();

                if (pkt == null || src == null)
                {
                    if (pkt != null) ffmpeg.av_packet_free(&pkt);
                    if (src != null) ffmpeg.av_frame_free(&src);
                    return false;
                }

                try
                {
                    if (requireBgra)
                    {
                        if (!EnsureReusableBgraFrame())
                            return false;
                        if (ffmpeg.av_frame_make_writable(_bgraReusable) < 0)
                            return false;
                    }

                    while (!ct.IsCancellationRequested && ffmpeg.av_read_frame(_fmt, pkt) >= 0)
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
                            long pts = src->best_effort_timestamp;
                            if (pts == ffmpeg.AV_NOPTS_VALUE)
                                pts = src->pts;

                            if (!_sequentialStarted && pts != ffmpeg.AV_NOPTS_VALUE && pts < _sequentialTargetPts)
                                continue;

                            _sequentialStarted = true;
                            frameIndex = _sequentialIndex++;

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
                        }
                    }

                    return false;
                }
                finally
                {
                    ffmpeg.av_packet_free(&pkt);
                    ffmpeg.av_frame_free(&src);
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

            _sws = ffmpeg.sws_getCachedContext(
                _sws,
                srcW, srcH, srcFmt,
                _dec->width, _dec->height, AVPixelFormat.AV_PIX_FMT_BGRA,
                (int)SwsFlags.SWS_BILINEAR,
                null, null, null);

            if (_sws == null)
            {
                if (temp != null)
                    ffmpeg.av_frame_free(&temp);
                return false;
            }

            ffmpeg.sws_scale(
                _sws,
                swFrame->data, swFrame->linesize,
                0, srcH,
                bgra->data, bgra->linesize);

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
            lock (_hwFormatLock)
            {
                _hwFormatByDecoder.TryGetValue((IntPtr)ctx, out target);
            }

            for (AVPixelFormat* p = pixFmts; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
            {
                if (*p == target)
                {
                    UpdateDecodeStatus($"디코딩: HW 픽셀 포맷 선택됨 ({target})");
                    UpdateDecodeDiagnostics(BuildFormatList("get_format", pixFmts, target));
                    return *p;
                }
            }

            UpdateDecodeStatus("디코딩: HW 픽셀 포맷 미지원");
            UpdateDecodeDiagnostics(BuildFormatList("get_format", pixFmts, target));
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

        private static void RegisterHardwareFormat(AVCodecContext* ctx, AVPixelFormat format)
        {
            if (ctx == null)
                return;

            lock (_hwFormatLock)
            {
                _hwFormatByDecoder[(IntPtr)ctx] = format;
            }
        }

        private static void UnregisterHardwareFormat(AVCodecContext* ctx)
        {
            if (ctx == null)
                return;

            lock (_hwFormatLock)
            {
                _hwFormatByDecoder.Remove((IntPtr)ctx);
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
