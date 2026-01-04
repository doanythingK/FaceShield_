using Avalonia.Media.Imaging;
using FFmpeg.AutoGen;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FaceShield.Services.Video;

public unsafe sealed class VideoExportService
{
    private readonly IFrameMaskProvider _maskProvider;
    private readonly MaskedVideoExporter _masked = new();

    public VideoExportService(IFrameMaskProvider maskProvider)
    {
        _maskProvider = maskProvider;
    }

    public void Export(
        string inputPath,
        string outputPath,
        int blurRadius,
        IProgress<ExportProgress>? progress = null,
        System.Threading.CancellationToken cancellationToken = default)
    {
        ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);

        AVFormatContext* inFmt = null;
        AVFormatContext* outFmt = null;
        AVCodecContext* dec = null;
        AVCodecContext* enc = null;
        AVCodecContext* audioDec = null;
        AVCodecContext* audioEnc = null;
        SwsContext* swsDecToBgra = null;
        SwsContext* swsBgraToEnc = null;
        SwrContext* swr = null;
        AVAudioFifo* audioFifo = null;

        AVPacket* pkt = ffmpeg.av_packet_alloc();
        AVPacket* outPkt = ffmpeg.av_packet_alloc();
        AVPacket* audioPkt = ffmpeg.av_packet_alloc();
        AVFrame* frame = ffmpeg.av_frame_alloc();
        AVFrame* bgra = ffmpeg.av_frame_alloc();
        AVFrame* encFrame = ffmpeg.av_frame_alloc();
        AVFrame* audioFrame = ffmpeg.av_frame_alloc();
        AVFrame* audioConvFrame = ffmpeg.av_frame_alloc();
        AVFrame* audioEncFrame = ffmpeg.av_frame_alloc();

        int videoStreamIndex = -1;
        int audioStreamIndex = -1;
        int frameIndex = 0;
        int totalFrames = 0;
        long swsToBgraMs = 0;
        long maskMs = 0;
        long swsToEncMs = 0;
        long encodeMs = 0;
        var swTotal = Stopwatch.StartNew();

        try
        {
            // ───────── input ─────────
            Throw(ffmpeg.avformat_open_input(&inFmt, inputPath, null, null));
            Throw(ffmpeg.avformat_find_stream_info(inFmt, null));

            for (int i = 0; i < inFmt->nb_streams; i++)
            {
                var stream = inFmt->streams[i];
                if (stream->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                {
                    if (videoStreamIndex < 0)
                        videoStreamIndex = i;
                }
                else if (stream->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                {
                    if (audioStreamIndex < 0)
                        audioStreamIndex = i;
                }
            }
            if (videoStreamIndex < 0)
                throw new InvalidOperationException("Video stream not found.");

            AVStream* inStream = inFmt->streams[videoStreamIndex];
            AVStream* inAudioStream = audioStreamIndex >= 0 ? inFmt->streams[audioStreamIndex] : null;
            totalFrames = (int)inStream->nb_frames;
            if (totalFrames <= 0)
            {
                double fps =
                    inStream->avg_frame_rate.num != 0
                        ? ffmpeg.av_q2d(inStream->avg_frame_rate)
                        : inStream->r_frame_rate.num != 0
                            ? ffmpeg.av_q2d(inStream->r_frame_rate)
                            : 0.0;

                double durationSeconds = inStream->duration != 0
                    ? inStream->duration * ffmpeg.av_q2d(inStream->time_base)
                    : inFmt->duration > 0
                        ? inFmt->duration / (double)ffmpeg.AV_TIME_BASE
                        : 0.0;

                if (fps > 0 && durationSeconds > 0)
                    totalFrames = (int)Math.Round(durationSeconds * fps);
            }

            AVCodec* decoder = ffmpeg.avcodec_find_decoder(inStream->codecpar->codec_id);
            dec = ffmpeg.avcodec_alloc_context3(decoder);
            Throw(ffmpeg.avcodec_parameters_to_context(dec, inStream->codecpar));
            Throw(ffmpeg.avcodec_open2(dec, decoder, null));

            // ───────── output ─────────
            Throw(ffmpeg.avformat_alloc_output_context2(&outFmt, null, null, outputPath));

            AVCodec* encoder;
            AVCodecID inputCodecId = inStream->codecpar->codec_id;
            enc = TryCreateEncoderContext(inputCodecId, inStream, dec, outFmt, out encoder, out var encoderError);

            string? exportNotice = null;
            if (enc == null)
            {
                string inputName = GetCodecName(inputCodecId);
                var fallbackCodecId = AVCodecID.AV_CODEC_ID_H264;
                string fallbackName = GetCodecName(fallbackCodecId);
                string reason = string.IsNullOrWhiteSpace(encoderError)
                    ? "원본 코덱 인코더를 찾을 수 없습니다."
                    : encoderError;
                exportNotice = $"원본 코덱({inputName}) 인코더를 사용할 수 없어 {fallbackName}로 내보냅니다. 사유: {reason}";

                enc = TryCreateEncoderContext(fallbackCodecId, inStream, dec, outFmt, out encoder, out var fallbackError);
                if (enc == null)
                    throw new InvalidOperationException($"대체 인코더 초기화 실패: {fallbackError}");
            }

            AVStream* outAudioStream = null;
            string? audioNotice = null;
            bool audioCopy = false;
            bool audioReencode = false;
            long audioPts = 0;
            if (inAudioStream != null)
            {
                int supported = outFmt->oformat != null
                    ? ffmpeg.avformat_query_codec(outFmt->oformat, inAudioStream->codecpar->codec_id, 0)
                    : 0;
                if (supported > 0)
                {
                    outAudioStream = ffmpeg.avformat_new_stream(outFmt, null);
                    if (outAudioStream == null)
                        audioNotice = "오디오 스트림을 생성하지 못해 소리를 포함하지 않습니다.";
                    else
                    {
                        Throw(ffmpeg.avcodec_parameters_copy(outAudioStream->codecpar, inAudioStream->codecpar));
                        outAudioStream->codecpar->codec_tag = 0;
                        outAudioStream->time_base = inAudioStream->time_base;
                        audioCopy = true;
                    }
                }
                else
                {
                    if (TryInitAudioTranscode(inAudioStream, outFmt, out audioDec, out audioEnc, out outAudioStream, out swr, out audioFifo, out var audioError))
                    {
                        audioReencode = true;
                        string audioCodec = GetCodecName(inAudioStream->codecpar->codec_id);
                        audioNotice = $"오디오 코덱({audioCodec})을 출력 컨테이너가 지원하지 않아 AAC로 변환합니다.";
                    }
                    else
                    {
                        audioNotice = $"오디오 변환 초기화 실패: {audioError}. 소리를 포함하지 않습니다.";
                    }
                }
            }
            else
            {
                audioNotice = "입력 영상에 오디오 스트림이 없습니다.";
            }

            if (progress != null)
            {
                string status = !string.IsNullOrWhiteSpace(exportNotice)
                    ? exportNotice
                    : "내보내기 시작...";
                if (!string.IsNullOrWhiteSpace(audioNotice))
                    status = $"{status} {audioNotice}";
                progress.Report(new ExportProgress(0, totalFrames, status));
            }

            AVStream* outStream = ffmpeg.avformat_new_stream(outFmt, encoder);
            Throw(ffmpeg.avcodec_parameters_from_context(outStream->codecpar, enc));
            outStream->time_base = enc->time_base;

            if ((outFmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
                Throw(ffmpeg.avio_open(&outFmt->pb, outputPath, ffmpeg.AVIO_FLAG_WRITE));

            Throw(ffmpeg.avformat_write_header(outFmt, null));

            // ───────── frames ─────────
            bgra->format = (int)AVPixelFormat.AV_PIX_FMT_BGRA;
            bgra->width = dec->width;
            bgra->height = dec->height;
            Throw(ffmpeg.av_frame_get_buffer(bgra, 32));

            encFrame->format = (int)enc->pix_fmt;
            encFrame->width = enc->width;
            encFrame->height = enc->height;
            Throw(ffmpeg.av_frame_get_buffer(encFrame, 32));

            // ✅ sws context (정정)
            swsDecToBgra = ffmpeg.sws_getContext(
                dec->width, dec->height, dec->pix_fmt,
                dec->width, dec->height, AVPixelFormat.AV_PIX_FMT_BGRA,
                (int)SwsFlags.SWS_BILINEAR,
                null, null, null);

            swsBgraToEnc = ffmpeg.sws_getContext(
                enc->width, enc->height, AVPixelFormat.AV_PIX_FMT_BGRA,
                enc->width, enc->height, enc->pix_fmt,
                (int)SwsFlags.SWS_BILINEAR,
                null, null, null);

            // ───────── main loop ─────────
            while (ffmpeg.av_read_frame(inFmt, pkt) >= 0)
            {
                if (pkt->stream_index == audioStreamIndex)
                {
                    if (audioCopy && outAudioStream != null && inAudioStream != null)
                    {
                        ffmpeg.av_packet_rescale_ts(pkt, inAudioStream->time_base, outAudioStream->time_base);
                        pkt->stream_index = outAudioStream->index;
                        Throw(ffmpeg.av_interleaved_write_frame(outFmt, pkt));
                        ffmpeg.av_packet_unref(pkt);
                        continue;
                    }

                    if (audioReencode && audioDec != null && audioEnc != null && swr != null && audioFifo != null)
                    {
                        int sendErr = ffmpeg.avcodec_send_packet(audioDec, pkt);
                        ffmpeg.av_packet_unref(pkt);
                        if (sendErr < 0)
                            Throw(sendErr);

                        while (ffmpeg.avcodec_receive_frame(audioDec, audioFrame) == 0)
                        {
                            ConvertAndQueueAudioFrame(audioFrame, audioDec, audioEnc, swr, audioFifo, audioConvFrame);
                            DrainAudioFifo(audioFifo, audioEnc, outAudioStream, outFmt, audioPkt, audioEncFrame, ref audioPts, flush: false);
                            ffmpeg.av_frame_unref(audioFrame);
                        }
                        continue;
                    }

                    ffmpeg.av_packet_unref(pkt);
                    continue;
                }

                if (pkt->stream_index != videoStreamIndex)
                {
                    ffmpeg.av_packet_unref(pkt);
                    continue;
                }

                Throw(ffmpeg.avcodec_send_packet(dec, pkt));
                ffmpeg.av_packet_unref(pkt);

                while (ffmpeg.avcodec_receive_frame(dec, frame) == 0)
                {
                    if (cancellationToken.IsCancellationRequested)
                        throw new OperationCanceledException(cancellationToken);

                    var tBgra = Stopwatch.StartNew();
                    Throw(ffmpeg.sws_scale(
                        swsDecToBgra,
                        frame->data,
                        frame->linesize,
                        0,
                        frame->height,
                        bgra->data,
                        bgra->linesize));
                    tBgra.Stop();
                    swsToBgraMs += tBgra.ElapsedMilliseconds;

                    WriteableBitmap? mask = null;
                    bool disposeMask = false;

                    if (_maskProvider is FrameMaskProvider provider)
                    {
                        if (provider.TryGetStoredMask(frameIndex, out var stored))
                        {
                            mask = stored;
                        }
                        else if (provider.TryGetFaceMaskData(frameIndex, out var faces))
                        {
                            mask = FrameMaskProvider.CreateMaskFromFaceRects(faces.Size, faces.Faces);
                            disposeMask = true;
                        }
                    }
                    else
                    {
                        mask = _maskProvider.GetFinalMask(frameIndex);
                    }

                    if (mask != null)
                    {
                        var tMask = Stopwatch.StartNew();
                        _masked.ApplyMaskAndBlur(bgra, mask, blurRadius);
                        tMask.Stop();
                        maskMs += tMask.ElapsedMilliseconds;
                        if (disposeMask)
                            mask.Dispose();
                    }

                    var tEncSws = Stopwatch.StartNew();
                    Throw(ffmpeg.sws_scale(
                        swsBgraToEnc,
                        bgra->data,
                        bgra->linesize,
                        0,
                        bgra->height,
                        encFrame->data,
                        encFrame->linesize));
                    tEncSws.Stop();
                    swsToEncMs += tEncSws.ElapsedMilliseconds;

                    encFrame->pts = frame->pts;

                    var tEncode = Stopwatch.StartNew();
                    Throw(ffmpeg.avcodec_send_frame(enc, encFrame));
                    while (ffmpeg.avcodec_receive_packet(enc, outPkt) == 0)
                    {
                        outPkt->stream_index = outStream->index;
                        Throw(ffmpeg.av_interleaved_write_frame(outFmt, outPkt));
                        ffmpeg.av_packet_unref(outPkt);
                    }
                    tEncode.Stop();
                    encodeMs += tEncode.ElapsedMilliseconds;

                    frameIndex++;
                    if (progress != null && (frameIndex % 15 == 0 || frameIndex == totalFrames))
                        progress.Report(new ExportProgress(frameIndex, totalFrames));
                    if (frameIndex % 60 == 0)
                    {
                        Debug.WriteLine(
                            $"[Export] frames={frameIndex}, swsToBgraMs={swsToBgraMs}, maskMs={maskMs}, swsToEncMs={swsToEncMs}, encodeMs={encodeMs}, totalMs={swTotal.ElapsedMilliseconds}");
                    }
                }
            }

            if (audioReencode && audioDec != null && audioEnc != null && swr != null && audioFifo != null)
            {
                int flushErr = ffmpeg.avcodec_send_packet(audioDec, null);
                if (flushErr < 0)
                    Throw(flushErr);

                while (ffmpeg.avcodec_receive_frame(audioDec, audioFrame) == 0)
                {
                    ConvertAndQueueAudioFrame(audioFrame, audioDec, audioEnc, swr, audioFifo, audioConvFrame);
                    DrainAudioFifo(audioFifo, audioEnc, outAudioStream, outFmt, audioPkt, audioEncFrame, ref audioPts, flush: false);
                    ffmpeg.av_frame_unref(audioFrame);
                }

                DrainAudioFifo(audioFifo, audioEnc, outAudioStream, outFmt, audioPkt, audioEncFrame, ref audioPts, flush: true);

                int sendFinalErr = ffmpeg.avcodec_send_frame(audioEnc, null);
                if (sendFinalErr < 0)
                    Throw(sendFinalErr);
                while (ffmpeg.avcodec_receive_packet(audioEnc, audioPkt) == 0)
                {
                    audioPkt->stream_index = outAudioStream->index;
                    ffmpeg.av_packet_rescale_ts(audioPkt, audioEnc->time_base, outAudioStream->time_base);
                    Throw(ffmpeg.av_interleaved_write_frame(outFmt, audioPkt));
                    ffmpeg.av_packet_unref(audioPkt);
                }
            }

            Throw(ffmpeg.av_write_trailer(outFmt));
            Debug.WriteLine(
                $"[Export] done frames={frameIndex}, swsToBgraMs={swsToBgraMs}, maskMs={maskMs}, swsToEncMs={swsToEncMs}, encodeMs={encodeMs}, totalMs={swTotal.ElapsedMilliseconds}");
        }
        finally
        {
            if (swsDecToBgra != null) ffmpeg.sws_freeContext(swsDecToBgra);
            if (swsBgraToEnc != null) ffmpeg.sws_freeContext(swsBgraToEnc);

            ffmpeg.av_frame_free(&frame);
            ffmpeg.av_frame_free(&bgra);
            ffmpeg.av_frame_free(&encFrame);
            ffmpeg.av_frame_free(&audioFrame);
            ffmpeg.av_frame_free(&audioConvFrame);
            ffmpeg.av_frame_free(&audioEncFrame);
            ffmpeg.av_packet_free(&pkt);
            ffmpeg.av_packet_free(&outPkt);
            ffmpeg.av_packet_free(&audioPkt);

            ffmpeg.avcodec_free_context(&dec);
            ffmpeg.avcodec_free_context(&enc);
            ffmpeg.avcodec_free_context(&audioDec);
            ffmpeg.avcodec_free_context(&audioEnc);
            if (swr != null) ffmpeg.swr_free(&swr);
            if (audioFifo != null) ffmpeg.av_audio_fifo_free(audioFifo);

            if (outFmt != null)
            {
                if ((outFmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
                    ffmpeg.avio_closep(&outFmt->pb);
                ffmpeg.avformat_free_context(outFmt);
            }

            if (inFmt != null)
                ffmpeg.avformat_close_input(&inFmt);
        }
    }

    private static unsafe bool IsPixFmtSupported(AVCodec* encoder, AVPixelFormat fmt)
    {
#pragma warning disable CS0618 // AVCodec.pix_fmts obsolete in ffmpeg headers
        if (encoder == null || encoder->pix_fmts == null)
            return true;

        for (AVPixelFormat* p = encoder->pix_fmts; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
        {
            if (*p == fmt)
                return true;
        }

        return false;
#pragma warning restore CS0618
    }

    private static unsafe AVCodecContext* TryCreateEncoderContext(
        AVCodecID codecId,
        AVStream* inStream,
        AVCodecContext* dec,
        AVFormatContext* outFmt,
        out AVCodec* encoder,
        out string? error)
    {
        encoder = null;
        error = null;

        if (outFmt != null && outFmt->oformat != null)
        {
            int supported = ffmpeg.avformat_query_codec(outFmt->oformat, codecId, 0);
            if (supported <= 0)
            {
                string formatName = GetOutputFormatName(outFmt);
                error = $"출력 컨테이너({formatName})가 코덱({GetCodecName(codecId)})을 지원하지 않습니다.";
                return null;
            }
        }

        encoder = ffmpeg.avcodec_find_encoder(codecId);
        if (encoder == null)
        {
            error = $"인코더를 찾을 수 없습니다(코덱: {GetCodecName(codecId)}). FFmpeg 빌드에 해당 인코더가 포함되어 있지 않을 수 있습니다.";
            return null;
        }

        AVCodecContext* ctx = ffmpeg.avcodec_alloc_context3(encoder);
        if (ctx == null)
        {
            error = "인코더 컨텍스트를 만들 수 없습니다.";
            return null;
        }

        ctx->width = dec->width;
        ctx->height = dec->height;
        ctx->pix_fmt = dec->pix_fmt;
        if (ctx->pix_fmt == AVPixelFormat.AV_PIX_FMT_NONE && inStream->codecpar->format != -1)
            ctx->pix_fmt = (AVPixelFormat)inStream->codecpar->format;
        ctx->time_base = inStream->time_base;
        ctx->framerate = inStream->r_frame_rate.num != 0 ? inStream->r_frame_rate : inStream->avg_frame_rate;

        if (inStream->codecpar->bit_rate > 0)
            ctx->bit_rate = inStream->codecpar->bit_rate;
        if (inStream->codecpar->profile != -99)
            ctx->profile = inStream->codecpar->profile;
        if (inStream->codecpar->level > 0)
            ctx->level = inStream->codecpar->level;

        ctx->sample_aspect_ratio = inStream->sample_aspect_ratio;

        if ((outFmt->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
            ctx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

        if (!IsPixFmtSupported(encoder, ctx->pix_fmt))
            ctx->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;

        int openErr = ffmpeg.avcodec_open2(ctx, encoder, null);
        if (openErr < 0)
        {
            error = $"인코더 초기화 실패: {GetErrorMessage(openErr)}";
            ffmpeg.avcodec_free_context(&ctx);
            return null;
        }

        return ctx;
    }

    private static unsafe bool TryInitAudioTranscode(
        AVStream* inAudioStream,
        AVFormatContext* outFmt,
        out AVCodecContext* audioDec,
        out AVCodecContext* audioEnc,
        out AVStream* outAudioStream,
        out SwrContext* swr,
        out AVAudioFifo* audioFifo,
        out string? error)
    {
        audioDec = null;
        audioEnc = null;
        outAudioStream = null;
        swr = null;
        audioFifo = null;
        error = null;

        AVCodec* decoder = ffmpeg.avcodec_find_decoder(inAudioStream->codecpar->codec_id);
        if (decoder == null)
        {
            error = $"오디오 디코더를 찾을 수 없습니다(코덱: {GetCodecName(inAudioStream->codecpar->codec_id)}).";
            return false;
        }

        audioDec = ffmpeg.avcodec_alloc_context3(decoder);
        if (audioDec == null)
        {
            error = "오디오 디코더 컨텍스트를 만들 수 없습니다.";
            return false;
        }

        int decErr = ffmpeg.avcodec_parameters_to_context(audioDec, inAudioStream->codecpar);
        if (decErr < 0)
        {
            error = $"오디오 디코더 초기화 실패: {GetErrorMessage(decErr)}";
            return false;
        }

        int openDecErr = ffmpeg.avcodec_open2(audioDec, decoder, null);
        if (openDecErr < 0)
        {
            error = $"오디오 디코더 열기 실패: {GetErrorMessage(openDecErr)}";
            return false;
        }

        AVCodec* aacEncoder = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_AAC);
        if (aacEncoder == null)
        {
            error = "AAC 인코더를 찾을 수 없습니다.";
            return false;
        }

        audioEnc = ffmpeg.avcodec_alloc_context3(aacEncoder);
        if (audioEnc == null)
        {
            error = "오디오 인코더 컨텍스트를 만들 수 없습니다.";
            return false;
        }

        int inChannels = audioDec->ch_layout.nb_channels;
        if (inChannels <= 0)
            inChannels = inAudioStream->codecpar->ch_layout.nb_channels;
        if (inChannels <= 0)
            inChannels = 2;

        int inSampleRate = audioDec->sample_rate > 0 ? audioDec->sample_rate : inAudioStream->codecpar->sample_rate;
        if (inSampleRate <= 0)
            inSampleRate = 44100;
        if (audioDec->sample_rate <= 0)
            audioDec->sample_rate = inSampleRate;

        AVChannelLayout inLayout = audioDec->ch_layout;
        if (inLayout.nb_channels <= 0)
            inLayout = inAudioStream->codecpar->ch_layout;
        if (inLayout.nb_channels <= 0)
        {
            AVChannelLayout* inLayoutPtr = stackalloc AVChannelLayout[1];
            *inLayoutPtr = inLayout;
            ffmpeg.av_channel_layout_default(inLayoutPtr, inChannels);
            inLayout = *inLayoutPtr;
        }

        AVChannelLayout outLayout = GetChannelLayout(aacEncoder, inLayout, inChannels);
        if (outLayout.nb_channels <= 0)
        {
            AVChannelLayout* outLayoutDefaultPtr = stackalloc AVChannelLayout[1];
            *outLayoutDefaultPtr = outLayout;
            ffmpeg.av_channel_layout_default(outLayoutDefaultPtr, inChannels);
            outLayout = *outLayoutDefaultPtr;
        }

        audioEnc->ch_layout = outLayout;
        audioEnc->sample_rate = GetSampleRate(aacEncoder, inSampleRate);
        audioEnc->sample_fmt = GetSampleFmt(aacEncoder, audioDec->sample_fmt);
        audioEnc->time_base = new AVRational { num = 1, den = audioEnc->sample_rate };

        if (inAudioStream->codecpar->bit_rate > 0)
            audioEnc->bit_rate = inAudioStream->codecpar->bit_rate;
        else
            audioEnc->bit_rate = 128_000;

        if (outFmt->oformat != null && (outFmt->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
            audioEnc->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

        int openEncErr = ffmpeg.avcodec_open2(audioEnc, aacEncoder, null);
        if (openEncErr < 0)
        {
            error = $"AAC 인코더 열기 실패: {GetErrorMessage(openEncErr)}";
            return false;
        }

        outAudioStream = ffmpeg.avformat_new_stream(outFmt, aacEncoder);
        if (outAudioStream == null)
        {
            error = "오디오 출력 스트림을 생성하지 못했습니다.";
            return false;
        }

        int parErr = ffmpeg.avcodec_parameters_from_context(outAudioStream->codecpar, audioEnc);
        if (parErr < 0)
        {
            error = $"오디오 파라미터 설정 실패: {GetErrorMessage(parErr)}";
            return false;
        }

        outAudioStream->time_base = audioEnc->time_base;

        AVChannelLayout outLayoutCopy = audioEnc->ch_layout;
        AVChannelLayout* outLayoutPtr = stackalloc AVChannelLayout[1];
        AVChannelLayout* inLayoutPtr2 = stackalloc AVChannelLayout[1];
        *outLayoutPtr = outLayoutCopy;
        *inLayoutPtr2 = inLayout;
        SwrContext* swrLocal = null;
        int swrErr = ffmpeg.swr_alloc_set_opts2(
            &swrLocal,
            outLayoutPtr,
            audioEnc->sample_fmt,
            audioEnc->sample_rate,
            inLayoutPtr2,
            audioDec->sample_fmt,
            inSampleRate,
            0,
            null);
        if (swrErr < 0)
        {
            error = $"오디오 리샘플러 초기화 실패: {GetErrorMessage(swrErr)}";
            return false;
        }

        swr = swrLocal;
        swrErr = ffmpeg.swr_init(swr);
        if (swrErr < 0)
        {
            error = $"오디오 리샘플러 열기 실패: {GetErrorMessage(swrErr)}";
            return false;
        }

        int outChannels = audioEnc->ch_layout.nb_channels;
        if (outChannels <= 0)
            outChannels = inChannels;
        audioFifo = ffmpeg.av_audio_fifo_alloc(audioEnc->sample_fmt, outChannels, 1);
        if (audioFifo == null)
        {
            error = "오디오 FIFO 초기화 실패.";
            return false;
        }

        return true;
    }

    private static unsafe AVSampleFormat GetSampleFmt(AVCodec* encoder, AVSampleFormat preferred)
    {
        if (encoder == null || encoder->sample_fmts == null)
            return preferred;

        for (AVSampleFormat* p = encoder->sample_fmts; *p != AVSampleFormat.AV_SAMPLE_FMT_NONE; p++)
        {
            if (*p == preferred)
                return preferred;
        }

        return encoder->sample_fmts[0];
    }

    private static unsafe int GetSampleRate(AVCodec* encoder, int preferred)
    {
        if (encoder == null || encoder->supported_samplerates == null)
            return preferred > 0 ? preferred : 44100;

        int* rates = encoder->supported_samplerates;
        int best = rates[0];
        for (int* p = rates; *p != 0; p++)
        {
            if (*p == preferred)
                return preferred;
            best = *p;
        }

        return best > 0 ? best : (preferred > 0 ? preferred : 44100);
    }

    private static unsafe AVChannelLayout GetChannelLayout(
        AVCodec* encoder,
        AVChannelLayout preferred,
        int preferredChannels)
    {
        if (encoder == null || encoder->ch_layouts == null)
            return preferred;

        AVChannelLayout* layouts = encoder->ch_layouts;
        AVChannelLayout best = layouts[0];
        for (AVChannelLayout* p = layouts; p->nb_channels != 0; p++)
        {
            int channels = p->nb_channels;
            if (channels == preferredChannels)
                return *p;
            best = *p;
        }

        return best;
    }

    private static unsafe void ConvertAndQueueAudioFrame(
        AVFrame* input,
        AVCodecContext* audioDec,
        AVCodecContext* audioEnc,
        SwrContext* swr,
        AVAudioFifo* fifo,
        AVFrame* convFrame)
    {
        long delay = ffmpeg.swr_get_delay(swr, audioDec->sample_rate);
        int outSamples = (int)ffmpeg.av_rescale_rnd(
            delay + input->nb_samples,
            audioEnc->sample_rate,
            audioDec->sample_rate,
            AVRounding.AV_ROUND_UP);

        convFrame->nb_samples = outSamples;
        convFrame->ch_layout = audioEnc->ch_layout;
        convFrame->format = (int)audioEnc->sample_fmt;
        convFrame->sample_rate = audioEnc->sample_rate;
        Throw(ffmpeg.av_frame_get_buffer(convFrame, 0));

        int converted = ffmpeg.swr_convert(
            swr,
            convFrame->extended_data,
            outSamples,
            input->extended_data,
            input->nb_samples);
        if (converted < 0)
            Throw(converted);
        if (converted == 0)
        {
            ffmpeg.av_frame_unref(convFrame);
            return;
        }

        int newSize = ffmpeg.av_audio_fifo_size(fifo) + converted;
        int reallocErr = ffmpeg.av_audio_fifo_realloc(fifo, newSize);
        if (reallocErr < 0)
            Throw(reallocErr);

        int written = ffmpeg.av_audio_fifo_write(fifo, (void**)convFrame->extended_data, converted);
        if (written < 0)
            Throw(written);
        if (written < converted)
            throw new InvalidOperationException("오디오 FIFO에 샘플을 충분히 쓰지 못했습니다.");

        ffmpeg.av_frame_unref(convFrame);
    }

    private static unsafe void DrainAudioFifo(
        AVAudioFifo* fifo,
        AVCodecContext* audioEnc,
        AVStream* outAudioStream,
        AVFormatContext* outFmt,
        AVPacket* outPkt,
        AVFrame* outFrame,
        ref long audioPts,
        bool flush)
    {
        if (fifo == null || audioEnc == null || outAudioStream == null || outFmt == null)
            return;

        int frameSize = audioEnc->frame_size > 0 ? audioEnc->frame_size : 1024;
        bool variable = audioEnc->codec != null
            && (audioEnc->codec->capabilities & ffmpeg.AV_CODEC_CAP_VARIABLE_FRAME_SIZE) != 0;

        while (true)
        {
            int available = ffmpeg.av_audio_fifo_size(fifo);
            if (available <= 0)
                break;

            int nbSamples;
            bool pad = false;
            if (variable)
            {
                nbSamples = Math.Min(available, frameSize);
            }
            else
            {
                if (available < frameSize)
                {
                    if (!flush)
                        break;
                    nbSamples = frameSize;
                    pad = true;
                }
                else
                {
                    nbSamples = frameSize;
                }
            }

            outFrame->nb_samples = nbSamples;
            outFrame->ch_layout = audioEnc->ch_layout;
            outFrame->format = (int)audioEnc->sample_fmt;
            outFrame->sample_rate = audioEnc->sample_rate;
            Throw(ffmpeg.av_frame_get_buffer(outFrame, 0));

            int readSamples = Math.Min(available, nbSamples);
            int read = ffmpeg.av_audio_fifo_read(fifo, (void**)outFrame->extended_data, readSamples);
            if (read < 0)
                Throw(read);
            if (pad && read < nbSamples)
            {
                int silence = nbSamples - read;
                int channels = audioEnc->ch_layout.nb_channels;
                ffmpeg.av_samples_set_silence(outFrame->extended_data, read, silence, channels, audioEnc->sample_fmt);
            }

            outFrame->pts = audioPts;
            audioPts += nbSamples;

            Throw(ffmpeg.avcodec_send_frame(audioEnc, outFrame));
            ffmpeg.av_frame_unref(outFrame);

            while (ffmpeg.avcodec_receive_packet(audioEnc, outPkt) == 0)
            {
                outPkt->stream_index = outAudioStream->index;
                ffmpeg.av_packet_rescale_ts(outPkt, audioEnc->time_base, outAudioStream->time_base);
                Throw(ffmpeg.av_interleaved_write_frame(outFmt, outPkt));
                ffmpeg.av_packet_unref(outPkt);
            }

            if (!flush && !variable && ffmpeg.av_audio_fifo_size(fifo) < frameSize)
                break;
        }
    }

    private static string GetCodecName(AVCodecID codecId)
    {
        string? name = null;
        try
        {
            name = ffmpeg.avcodec_get_name(codecId);
        }
        catch
        {
            // 일부 바인딩은 포인터 시그니처를 사용하므로 예외가 날 수 있음
        }

        if (string.IsNullOrWhiteSpace(name))
            return codecId.ToString();

        return name;
    }

    private static unsafe string GetOutputFormatName(AVFormatContext* outFmt)
    {
        if (outFmt == null || outFmt->oformat == null || outFmt->oformat->name == null)
            return "unknown";

        return Marshal.PtrToStringAnsi((IntPtr)outFmt->oformat->name) ?? "unknown";
    }

    private static string GetErrorMessage(int err)
    {
        byte* buf = stackalloc byte[1024];
        ffmpeg.av_strerror(err, buf, 1024);
        return System.Text.Encoding.UTF8.GetString(new ReadOnlySpan<byte>(buf, 1024)).TrimEnd('\0');
    }

    private static void Throw(int err)
    {
        if (err >= 0) return;

        byte* buf = stackalloc byte[1024];
        ffmpeg.av_strerror(err, buf, 1024);
        throw new InvalidOperationException(
            System.Text.Encoding.UTF8.GetString(new ReadOnlySpan<byte>(buf, 1024)).TrimEnd('\0'));
    }
}
