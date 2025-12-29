using Avalonia;
using Avalonia.Media.Imaging;
using FFmpeg.AutoGen;
using System;

namespace FaceShield.Services.Video
{
    public unsafe sealed class VideoFrameReader : IDisposable
    {
        private AVFormatContext* _format;
        private AVCodecContext* _codec;
        private AVFrame* _frame;
        private AVPacket* _packet;
        private SwsContext* _sws;
        private int _videoStreamIndex = -1;

        // BGRA 출력
        private byte* _bgraBuffer;
        private byte_ptrArray4 _bgraData;
        private int_array4 _bgraLinesize;

        public VideoFrameReader(string path)
        {
            ffmpeg.avformat_network_init();

            // AVFormatContext** 시그니처 대응
            fixed (AVFormatContext** pFmt = &_format)
            {
                int r = ffmpeg.avformat_open_input(pFmt, path, null, null);
                if (r < 0) throw new InvalidOperationException("FFmpeg native libraries not loaded. Check DLL placement.");
            }

            if (ffmpeg.avformat_find_stream_info(_format, null) < 0)
                throw new InvalidOperationException("avformat_find_stream_info failed");

            for (int i = 0; i < _format->nb_streams; i++)
            {
                if (_format->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                {
                    _videoStreamIndex = i;
                    break;
                }
            }
            if (_videoStreamIndex < 0)
                throw new InvalidOperationException("No video stream found");

            AVCodecParameters* codecPar = _format->streams[_videoStreamIndex]->codecpar;
            AVCodec* decoder = ffmpeg.avcodec_find_decoder(codecPar->codec_id);
            if (decoder == null)
                throw new InvalidOperationException("Decoder not found");

            _codec = ffmpeg.avcodec_alloc_context3(decoder);
            if (_codec == null)
                throw new InvalidOperationException("avcodec_alloc_context3 failed");

            if (ffmpeg.avcodec_parameters_to_context(_codec, codecPar) < 0)
                throw new InvalidOperationException("avcodec_parameters_to_context failed");

            if (ffmpeg.avcodec_open2(_codec, decoder, null) < 0)
                throw new InvalidOperationException("avcodec_open2 failed");

            _frame = ffmpeg.av_frame_alloc();
            _packet = ffmpeg.av_packet_alloc();
            if (_frame == null || _packet == null)
                throw new InvalidOperationException("av_frame_alloc/av_packet_alloc failed");

            _sws = ffmpeg.sws_getContext(
                _codec->width, _codec->height, _codec->pix_fmt,
                _codec->width, _codec->height, AVPixelFormat.AV_PIX_FMT_BGRA,
                (int)SwsFlags.SWS_BILINEAR,
                null, null, null);

            if (_sws == null)
                throw new InvalidOperationException("sws_getContext failed");

            int bufSize = ffmpeg.av_image_get_buffer_size(
                AVPixelFormat.AV_PIX_FMT_BGRA, _codec->width, _codec->height, 1);

            _bgraBuffer = (byte*)ffmpeg.av_malloc((ulong)bufSize);
            if (_bgraBuffer == null)
                throw new InvalidOperationException("av_malloc failed");

            if (ffmpeg.av_image_fill_arrays(
                    ref _bgraData,
                    ref _bgraLinesize,
                    _bgraBuffer,
                    AVPixelFormat.AV_PIX_FMT_BGRA,
                    _codec->width,
                    _codec->height,
                    1) < 0)
            {
                throw new InvalidOperationException("av_image_fill_arrays failed");
            }
        }

        public WriteableBitmap ReadFrame(int targetFrame)
        {
            int current = 0;

            while (ffmpeg.av_read_frame(_format, _packet) >= 0)
            {
                if (_packet->stream_index != _videoStreamIndex)
                {
                    ffmpeg.av_packet_unref(_packet);
                    continue;
                }

                int send = ffmpeg.avcodec_send_packet(_codec, _packet);
                ffmpeg.av_packet_unref(_packet);
                if (send < 0) continue;

                while (ffmpeg.avcodec_receive_frame(_codec, _frame) == 0)
                {
                    if (current++ == targetFrame)
                    {
                        ffmpeg.sws_scale(
                            _sws,
                            _frame->data,
                            _frame->linesize,
                            0,
                            _codec->height,
                            _bgraData,
                            _bgraLinesize);

                        return CreateBitmapFromBGRA();
                    }
                }
            }

            throw new InvalidOperationException("Frame not found");
        }

        private WriteableBitmap CreateBitmapFromBGRA()
        {
            int w = _codec->width;
            int h = _codec->height;

            var bmp = new WriteableBitmap(
                new PixelSize(w, h),
                new Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888,
                Avalonia.Platform.AlphaFormat.Premul);

            using var fb = bmp.Lock();

            byte* src = _bgraData[0];
            byte* dst = (byte*)fb.Address;

            int srcStride = _bgraLinesize[0];
            int dstStride = w * 4;

            for (int y = 0; y < h; y++)
            {
                Buffer.MemoryCopy(
                    src + y * srcStride,
                    dst + y * dstStride,
                    dstStride,
                    dstStride);
            }

            return bmp;
        }

        public void Dispose()
        {
            if (_sws != null)
            {
                ffmpeg.sws_freeContext(_sws);
                _sws = null;
            }

            if (_packet != null)
            {
                fixed (AVPacket** p = &_packet) ffmpeg.av_packet_free(p);
            }

            if (_frame != null)
            {
                fixed (AVFrame** p = &_frame) ffmpeg.av_frame_free(p);
            }

            if (_codec != null)
            {
                fixed (AVCodecContext** p = &_codec) ffmpeg.avcodec_free_context(p);
            }

            if (_format != null)
            {
                fixed (AVFormatContext** p = &_format) ffmpeg.avformat_close_input(p);
            }

            if (_bgraBuffer != null)
            {
                ffmpeg.av_free(_bgraBuffer);
                _bgraBuffer = null;
            }
        }
    }
}

//public unsafe sealed class VideoFrameReader : IDisposable
//{
//    private AVFormatContext* _format;
//    private AVCodecContext* _codec;
//    private AVFrame* _frame;
//    private AVPacket* _packet;
//    private SwsContext* _sws;

//    private int _videoStreamIndex = -1;

//    // BGRA 출력용
//    private byte* _bgraBuffer;
//    private byte_ptrArray4 _bgraData;
//    private int_array4 _bgraLinesize;

//    public VideoFrameReader(string path)
//    {
//        ffmpeg.avformat_network_init();

//        // --- open input (AVFormatContext**) ---
//        fixed (AVFormatContext** pFmt = &_format)
//        {
//            int r = ffmpeg.avformat_open_input(pFmt, path, null, null);
//            if (r < 0)
//                throw new InvalidOperationException("avformat_open_input failed");
//        }

//        if (ffmpeg.avformat_find_stream_info(_format, null) < 0)
//            throw new InvalidOperationException("avformat_find_stream_info failed");

//        // --- find video stream ---
//        for (int i = 0; i < _format->nb_streams; i++)
//        {
//            if (_format->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
//            {
//                _videoStreamIndex = i;
//                break;
//            }
//        }

//        if (_videoStreamIndex < 0)
//            throw new InvalidOperationException("No video stream found");

//        // --- codec ---
//        AVCodecParameters* codecPar = _format->streams[_videoStreamIndex]->codecpar;
//        AVCodec* decoder = ffmpeg.avcodec_find_decoder(codecPar->codec_id);
//        if (decoder == null)
//            throw new InvalidOperationException("Decoder not found");

//        _codec = ffmpeg.avcodec_alloc_context3(decoder);
//        if (_codec == null)
//            throw new InvalidOperationException("avcodec_alloc_context3 failed");

//        if (ffmpeg.avcodec_parameters_to_context(_codec, codecPar) < 0)
//            throw new InvalidOperationException("avcodec_parameters_to_context failed");

//        if (ffmpeg.avcodec_open2(_codec, decoder, null) < 0)
//            throw new InvalidOperationException("avcodec_open2 failed");

//        _frame = ffmpeg.av_frame_alloc();
//        _packet = ffmpeg.av_packet_alloc();

//        if (_frame == null || _packet == null)
//            throw new InvalidOperationException("av_frame_alloc / av_packet_alloc failed");

//        // --- swscale ---
//        _sws = ffmpeg.sws_getContext(
//            _codec->width,
//            _codec->height,
//            _codec->pix_fmt,
//            _codec->width,
//            _codec->height,
//            AVPixelFormat.AV_PIX_FMT_BGRA,
//            (int)SwsFlags.SWS_BILINEAR,
//            null,
//            null,
//            null);

//        if (_sws == null)
//            throw new InvalidOperationException("sws_getContext failed");

//        // --- BGRA buffer ---
//        int bufSize = ffmpeg.av_image_get_buffer_size(
//            AVPixelFormat.AV_PIX_FMT_BGRA,
//            _codec->width,
//            _codec->height,
//            1);

//        _bgraBuffer = (byte*)ffmpeg.av_malloc((ulong)bufSize);
//        if (_bgraBuffer == null)
//            throw new InvalidOperationException("av_malloc failed");

//        // 중요: AVFrame.data 사용 ❌
//        // av_image_fill_arrays는 byte_ptrArray4 / int_array4만 받는다
//        if (ffmpeg.av_image_fill_arrays(
//                ref _bgraData,
//                ref _bgraLinesize,
//                _bgraBuffer,
//                AVPixelFormat.AV_PIX_FMT_BGRA,
//                _codec->width,
//                _codec->height,
//                1) < 0)
//        {
//            throw new InvalidOperationException("av_image_fill_arrays failed");
//        }
//    }

//    public WriteableBitmap ReadFrame(int targetFrame)
//    {
//        int current = 0;

//        while (ffmpeg.av_read_frame(_format, _packet) >= 0)
//        {
//            if (_packet->stream_index != _videoStreamIndex)
//            {
//                ffmpeg.av_packet_unref(_packet);
//                continue;
//            }

//            int send = ffmpeg.avcodec_send_packet(_codec, _packet);
//            ffmpeg.av_packet_unref(_packet);
//            if (send < 0)
//                continue;

//            while (ffmpeg.avcodec_receive_frame(_codec, _frame) == 0)
//            {
//                if (current++ == targetFrame)
//                {
//                    // YUV → BGRA
//                    ffmpeg.sws_scale(
//                        _sws,
//                        _frame->data,
//                        _frame->linesize,
//                        0,
//                        _codec->height,
//                        _bgraData,
//                        _bgraLinesize);

//                    return CreateBitmapFromBGRA();
//                }
//            }
//        }

//        throw new InvalidOperationException("Frame not found");
//    }

//    private WriteableBitmap CreateBitmapFromBGRA()
//    {
//        int w = _codec->width;
//        int h = _codec->height;

//        var bmp = new WriteableBitmap(
//            new PixelSize(w, h),
//            new Vector(96, 96),
//            Avalonia.Platform.PixelFormat.Bgra8888,
//            Avalonia.Platform.AlphaFormat.Premul);

//        using var fb = bmp.Lock();

//        byte* src = _bgraData[0];
//        byte* dst = (byte*)fb.Address;

//        int srcStride = _bgraLinesize[0];
//        int dstStride = w * 4;

//        for (int y = 0; y < h; y++)
//        {
//            Buffer.MemoryCopy(
//                src + y * srcStride,
//                dst + y * dstStride,
//                dstStride,
//                dstStride);
//        }

//        return bmp;
//    }

//    public void Dispose()
//    {
//        if (_sws != null)
//            ffmpeg.sws_freeContext(_sws);

//        if (_packet != null)
//        {
//            fixed (AVPacket** p = &_packet)
//                ffmpeg.av_packet_free(p);
//        }

//        if (_frame != null)
//        {
//            fixed (AVFrame** p = &_frame)
//                ffmpeg.av_frame_free(p);
//        }

//        if (_codec != null)
//        {
//            fixed (AVCodecContext** p = &_codec)
//                ffmpeg.avcodec_free_context(p);
//        }

//        if (_format != null)
//        {
//            fixed (AVFormatContext** p = &_format)
//                ffmpeg.avformat_close_input(p);
//        }

//        if (_bgraBuffer != null)
//        {
//            ffmpeg.av_free(_bgraBuffer);
//            _bgraBuffer = null;
//        }
//    }
//}
