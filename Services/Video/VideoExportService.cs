using Avalonia;
using Avalonia.Media.Imaging;
using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
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
        SwsContext* swsDecToEnc = null;
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
        double sourceFps = 0.0;
        WriteableBitmap? reusableFaceMask = null;
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
            sourceFps =
                inStream->avg_frame_rate.num != 0
                    ? ffmpeg.av_q2d(inStream->avg_frame_rate)
                    : inStream->r_frame_rate.num != 0
                        ? ffmpeg.av_q2d(inStream->r_frame_rate)
                        : 0.0;
            if (totalFrames <= 0)
            {
                double durationSeconds = inStream->duration != 0
                    ? inStream->duration * ffmpeg.av_q2d(inStream->time_base)
                    : inFmt->duration > 0
                        ? inFmt->duration / (double)ffmpeg.AV_TIME_BASE
                        : 0.0;

                if (sourceFps > 0 && durationSeconds > 0)
                    totalFrames = (int)Math.Round(durationSeconds * sourceFps);
            }

            HashSet<int>? blurFrameSet = null;
            List<(int Start, int EndExclusive)>? blurRanges = null;
            int blurRangeCursor = 0;
            (int Start, int EndExclusive)? hybridEncodeWindow = null;
            bool hybridCopyAttempted = false;
            if (_maskProvider is FrameMaskProvider frameMaskProvider)
            {
                blurFrameSet = BuildBlurFrameSet(frameMaskProvider, totalFrames);
                if (blurFrameSet.Count == 0)
                {
                    progress?.Report(new ExportProgress(0, totalFrames, "블러 대상이 없어 원본 스트림을 고속 복사합니다..."));
                    ExportByRemuxCopy(
                        inFmt,
                        outputPath,
                        videoStreamIndex,
                        totalFrames,
                        sourceFps,
                        progress,
                        cancellationToken);
                    return;
                }

                blurRanges = BuildBlurFrameRanges(blurFrameSet);
                if (blurRanges.Count > 0 && sourceFps > 0.0 && totalFrames > 0)
                {
                    var keyframes = CollectKeyframeFrameIndices(inputPath, sourceFps, totalFrames);
                    if (keyframes.Count > 0)
                        blurRanges = AlignRangesToKeyframes(blurRanges, keyframes, totalFrames);

                    int encodeStart = blurRanges[0].Start;
                    int encodeEnd = blurRanges[blurRanges.Count - 1].EndExclusive;
                    if (encodeStart > 0 || encodeEnd < totalFrames)
                    {
                        hybridEncodeWindow = (encodeStart, encodeEnd);
                        hybridCopyAttempted = true;
                    }
                }
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

            bool useHybridCopyWindow =
                hybridEncodeWindow.HasValue &&
                enc != null &&
                enc->codec_id == inStream->codecpar->codec_id;

            AVStream* outStream = ffmpeg.avformat_new_stream(outFmt, useHybridCopyWindow ? null : encoder);
            if (useHybridCopyWindow)
            {
                Throw(ffmpeg.avcodec_parameters_copy(outStream->codecpar, inStream->codecpar));
                outStream->codecpar->codec_tag = 0;
                outStream->time_base = inStream->time_base;
                if (progress != null && hybridEncodeWindow.HasValue)
                {
                    var window = hybridEncodeWindow.Value;
                    progress.Report(new ExportProgress(
                        Math.Max(0, window.Start),
                        totalFrames,
                        $"구간 고속 내보내기: [{window.Start}..{window.EndExclusive}) 프레임만 인코딩합니다."));
                }
            }
            else
            {
                Throw(ffmpeg.avcodec_parameters_from_context(outStream->codecpar, enc));
                outStream->time_base = enc->time_base;
            }

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
                (int)SwsFlags.SWS_FAST_BILINEAR,
                null, null, null);

            swsBgraToEnc = ffmpeg.sws_getContext(
                enc->width, enc->height, AVPixelFormat.AV_PIX_FMT_BGRA,
                enc->width, enc->height, enc->pix_fmt,
                (int)SwsFlags.SWS_FAST_BILINEAR,
                null, null, null);

            // ───────── main loop ─────────
            int encodeWindowStart = hybridEncodeWindow?.Start ?? 0;
            int encodeWindowEnd = hybridEncodeWindow?.EndExclusive ?? int.MaxValue;
            if (encodeWindowEnd <= encodeWindowStart)
                useHybridCopyWindow = false;
            if (hybridCopyAttempted && !useHybridCopyWindow && progress != null)
                progress.Report(new ExportProgress(0, totalFrames, "구간 복사 조건을 만족하지 않아 일반 내보내기로 진행합니다..."));

            int packetFrameFallback = 0;
            int lastResolvedFrameIndex = -1;
            int lastReportedFrame = -1;
            bool videoFlushed = false;

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

                int packetFrameIndex = ResolveFrameIndexFromPacket(pkt, inStream->time_base, sourceFps, packetFrameFallback, totalFrames);
                if (packetFrameIndex < packetFrameFallback)
                    packetFrameIndex = packetFrameFallback;
                packetFrameFallback = packetFrameIndex + 1;

                bool packetInEncodeWindow =
                    !useHybridCopyWindow ||
                    (packetFrameIndex >= encodeWindowStart && packetFrameIndex < encodeWindowEnd);

                if (!packetInEncodeWindow)
                {
                    if (useHybridCopyWindow && packetFrameIndex >= encodeWindowEnd)
                    {
                        FlushVideoPipeline(
                            ref videoFlushed,
                            dec,
                            enc,
                            frame,
                            bgra,
                            encFrame,
                            swsDecToBgra,
                            swsBgraToEnc,
                            ref swsDecToEnc,
                            inStream,
                            outPkt,
                            outStream,
                            outFmt,
                            blurRadius,
                            blurRanges,
                            ref blurRangeCursor,
                            sourceFps,
                            totalFrames,
                            ref frameIndex,
                            ref lastResolvedFrameIndex,
                            ref swsToBgraMs,
                            ref maskMs,
                            ref swsToEncMs,
                            ref encodeMs,
                            ref reusableFaceMask,
                            progress,
                            ref lastReportedFrame,
                            swTotal,
                            cancellationToken);
                    }

                    ffmpeg.av_packet_rescale_ts(pkt, inStream->time_base, outStream->time_base);
                    pkt->stream_index = outStream->index;
                    pkt->pos = -1;
                    Throw(ffmpeg.av_interleaved_write_frame(outFmt, pkt));
                    ReportVideoProgress(progress, totalFrames, ref lastReportedFrame, packetFrameIndex);
                    ffmpeg.av_packet_unref(pkt);
                    continue;
                }

                Throw(ffmpeg.avcodec_send_packet(dec, pkt));
                ffmpeg.av_packet_unref(pkt);
                while (ffmpeg.avcodec_receive_frame(dec, frame) == 0)
                {
                    ProcessDecodedVideoFrame(
                        frame,
                        bgra,
                        encFrame,
                        dec,
                        enc,
                        swsDecToBgra,
                        swsBgraToEnc,
                        ref swsDecToEnc,
                        inStream,
                        outPkt,
                        outStream,
                        outFmt,
                        blurRadius,
                        blurRanges,
                        ref blurRangeCursor,
                        sourceFps,
                        totalFrames,
                        ref frameIndex,
                        ref lastResolvedFrameIndex,
                        ref swsToBgraMs,
                        ref maskMs,
                        ref swsToEncMs,
                        ref encodeMs,
                        ref reusableFaceMask,
                        progress,
                        ref lastReportedFrame,
                        swTotal,
                        cancellationToken);
                    ffmpeg.av_frame_unref(frame);
                }
            }

            FlushVideoPipeline(
                ref videoFlushed,
                dec,
                enc,
                frame,
                bgra,
                encFrame,
                swsDecToBgra,
                swsBgraToEnc,
                ref swsDecToEnc,
                inStream,
                outPkt,
                outStream,
                outFmt,
                blurRadius,
                blurRanges,
                ref blurRangeCursor,
                sourceFps,
                totalFrames,
                ref frameIndex,
                ref lastResolvedFrameIndex,
                ref swsToBgraMs,
                ref maskMs,
                ref swsToEncMs,
                ref encodeMs,
                ref reusableFaceMask,
                progress,
                ref lastReportedFrame,
                swTotal,
                cancellationToken);

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
            if (swsDecToEnc != null) ffmpeg.sws_freeContext(swsDecToEnc);

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
            reusableFaceMask?.Dispose();

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

    private static void ReportVideoProgress(
        IProgress<ExportProgress>? progress,
        int totalFrames,
        ref int lastReportedFrame,
        int currentFrame,
        string? status = null)
    {
        if (progress == null || totalFrames <= 0)
            return;

        int bounded = Math.Clamp(currentFrame, 0, totalFrames);
        if (bounded - lastReportedFrame >= 15 || bounded >= totalFrames || status != null)
        {
            progress.Report(new ExportProgress(bounded, totalFrames, status));
            lastReportedFrame = bounded;
        }
    }

    private static unsafe void DrainEncoderPackets(
        AVCodecContext* enc,
        AVPacket* outPkt,
        AVStream* outStream,
        AVFormatContext* outFmt)
    {
        while (ffmpeg.avcodec_receive_packet(enc, outPkt) == 0)
        {
            outPkt->stream_index = outStream->index;
            Throw(ffmpeg.av_interleaved_write_frame(outFmt, outPkt));
            ffmpeg.av_packet_unref(outPkt);
        }
    }

    private unsafe void ProcessDecodedVideoFrame(
        AVFrame* frame,
        AVFrame* bgra,
        AVFrame* encFrame,
        AVCodecContext* dec,
        AVCodecContext* enc,
        SwsContext* swsDecToBgra,
        SwsContext* swsBgraToEnc,
        ref SwsContext* swsDecToEnc,
        AVStream* inStream,
        AVPacket* outPkt,
        AVStream* outStream,
        AVFormatContext* outFmt,
        int blurRadius,
        List<(int Start, int EndExclusive)>? blurRanges,
        ref int blurRangeCursor,
        double sourceFps,
        int totalFrames,
        ref int frameIndex,
        ref int lastResolvedFrameIndex,
        ref long swsToBgraMs,
        ref long maskMs,
        ref long swsToEncMs,
        ref long encodeMs,
        ref WriteableBitmap? reusableFaceMask,
        IProgress<ExportProgress>? progress,
        ref int lastReportedFrame,
        Stopwatch swTotal,
        System.Threading.CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            throw new OperationCanceledException(cancellationToken);

        int fallbackIndex = Math.Max(frameIndex, lastResolvedFrameIndex + 1);
        int resolvedFrameIndex = ResolveFrameIndexFromFrame(frame, inStream->time_base, sourceFps, fallbackIndex, totalFrames);
        if (resolvedFrameIndex < fallbackIndex)
            resolvedFrameIndex = fallbackIndex;
        lastResolvedFrameIndex = resolvedFrameIndex;
        frameIndex = resolvedFrameIndex + 1;

        WriteableBitmap? mask = null;
        IReadOnlyList<Rect>? faceRects = null;
        bool mightHaveMask = blurRanges == null || IsFrameInBlurRanges(resolvedFrameIndex, blurRanges, ref blurRangeCursor);

        if (mightHaveMask && _maskProvider is FrameMaskProvider provider)
        {
            if (provider.TryGetStoredMask(resolvedFrameIndex, out var stored))
            {
                mask = stored;
            }
            else if (provider.TryGetFaceMaskData(resolvedFrameIndex, out var faces))
            {
                if (reusableFaceMask == null ||
                    reusableFaceMask.PixelSize.Width != faces.Size.Width ||
                    reusableFaceMask.PixelSize.Height != faces.Size.Height)
                {
                    reusableFaceMask?.Dispose();
                    reusableFaceMask = new WriteableBitmap(
                        faces.Size,
                        new Vector(96, 96),
                        Avalonia.Platform.PixelFormat.Bgra8888,
                        Avalonia.Platform.AlphaFormat.Premul);
                }

                FrameMaskProvider.RenderMaskFromFaceRects(reusableFaceMask, faces.Size, faces.Faces);
                mask = reusableFaceMask;
                faceRects = faces.Faces;
            }
        }
        else if (mightHaveMask)
        {
            mask = _maskProvider.GetFinalMask(resolvedFrameIndex);
        }

        if (mask != null)
        {
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

            var tMask = Stopwatch.StartNew();
            _masked.ApplyMaskAndBlur(bgra, mask, blurRadius, faceRects);
            tMask.Stop();
            maskMs += tMask.ElapsedMilliseconds;

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
            DrainEncoderPackets(enc, outPkt, outStream, outFmt);
            tEncode.Stop();
            encodeMs += tEncode.ElapsedMilliseconds;
        }
        else
        {
            bool direct = frame->format == (int)enc->pix_fmt
                && frame->width == enc->width
                && frame->height == enc->height;

            if (!direct)
            {
                if (swsDecToEnc == null)
                {
                    swsDecToEnc = ffmpeg.sws_getContext(
                        dec->width, dec->height, dec->pix_fmt,
                        enc->width, enc->height, enc->pix_fmt,
                        (int)SwsFlags.SWS_FAST_BILINEAR,
                        null, null, null);
                }

                var tEncSws = Stopwatch.StartNew();
                Throw(ffmpeg.sws_scale(
                    swsDecToEnc,
                    frame->data,
                    frame->linesize,
                    0,
                    frame->height,
                    encFrame->data,
                    encFrame->linesize));
                tEncSws.Stop();
                swsToEncMs += tEncSws.ElapsedMilliseconds;

                encFrame->pts = frame->pts;

                var tEncode = Stopwatch.StartNew();
                Throw(ffmpeg.avcodec_send_frame(enc, encFrame));
                DrainEncoderPackets(enc, outPkt, outStream, outFmt);
                tEncode.Stop();
                encodeMs += tEncode.ElapsedMilliseconds;
            }
            else
            {
                var tEncode = Stopwatch.StartNew();
                Throw(ffmpeg.avcodec_send_frame(enc, frame));
                DrainEncoderPackets(enc, outPkt, outStream, outFmt);
                tEncode.Stop();
                encodeMs += tEncode.ElapsedMilliseconds;
            }
        }

        ReportVideoProgress(progress, totalFrames, ref lastReportedFrame, resolvedFrameIndex);
        if (resolvedFrameIndex % 60 == 0)
        {
            Debug.WriteLine(
                $"[Export] frames={resolvedFrameIndex}, swsToBgraMs={swsToBgraMs}, maskMs={maskMs}, swsToEncMs={swsToEncMs}, encodeMs={encodeMs}, totalMs={swTotal.ElapsedMilliseconds}");
        }
    }

    private unsafe void FlushVideoPipeline(
        ref bool videoFlushed,
        AVCodecContext* dec,
        AVCodecContext* enc,
        AVFrame* frame,
        AVFrame* bgra,
        AVFrame* encFrame,
        SwsContext* swsDecToBgra,
        SwsContext* swsBgraToEnc,
        ref SwsContext* swsDecToEnc,
        AVStream* inStream,
        AVPacket* outPkt,
        AVStream* outStream,
        AVFormatContext* outFmt,
        int blurRadius,
        List<(int Start, int EndExclusive)>? blurRanges,
        ref int blurRangeCursor,
        double sourceFps,
        int totalFrames,
        ref int frameIndex,
        ref int lastResolvedFrameIndex,
        ref long swsToBgraMs,
        ref long maskMs,
        ref long swsToEncMs,
        ref long encodeMs,
        ref WriteableBitmap? reusableFaceMask,
        IProgress<ExportProgress>? progress,
        ref int lastReportedFrame,
        Stopwatch swTotal,
        System.Threading.CancellationToken cancellationToken)
    {
        if (videoFlushed)
            return;

        int sendErr = ffmpeg.avcodec_send_packet(dec, null);
        if (sendErr < 0 && sendErr != ffmpeg.AVERROR_EOF)
            Throw(sendErr);

        while (ffmpeg.avcodec_receive_frame(dec, frame) == 0)
        {
            ProcessDecodedVideoFrame(
                frame,
                bgra,
                encFrame,
                dec,
                enc,
                swsDecToBgra,
                swsBgraToEnc,
                ref swsDecToEnc,
                inStream,
                outPkt,
                outStream,
                outFmt,
                blurRadius,
                blurRanges,
                ref blurRangeCursor,
                sourceFps,
                totalFrames,
                ref frameIndex,
                ref lastResolvedFrameIndex,
                ref swsToBgraMs,
                ref maskMs,
                ref swsToEncMs,
                ref encodeMs,
                ref reusableFaceMask,
                progress,
                ref lastReportedFrame,
                swTotal,
                cancellationToken);
            ffmpeg.av_frame_unref(frame);
        }

        int encErr = ffmpeg.avcodec_send_frame(enc, null);
        if (encErr < 0 && encErr != ffmpeg.AVERROR_EOF)
            Throw(encErr);
        DrainEncoderPackets(enc, outPkt, outStream, outFmt);
        videoFlushed = true;
    }

    private static HashSet<int> BuildBlurFrameSet(FrameMaskProvider provider, int totalFrames)
    {
        var result = new HashSet<int>();
        if (provider == null || !provider.HasAnyMaskEntries())
            return result;

        foreach (int index in provider.GetStoredMaskFrameIndices())
        {
            if (index < 0)
                continue;
            if (totalFrames > 0 && index >= totalFrames)
                continue;
            result.Add(index);
        }

        foreach (int index in provider.GetFaceMaskFrameIndices())
        {
            if (index < 0)
                continue;
            if (totalFrames > 0 && index >= totalFrames)
                continue;
            result.Add(index);
        }

        return result;
    }

    private static List<(int Start, int EndExclusive)> BuildBlurFrameRanges(HashSet<int> blurFrameSet)
    {
        var ranges = new List<(int Start, int EndExclusive)>();
        if (blurFrameSet == null || blurFrameSet.Count == 0)
            return ranges;

        int[] sorted = new int[blurFrameSet.Count];
        blurFrameSet.CopyTo(sorted);
        Array.Sort(sorted);

        int start = sorted[0];
        int end = start + 1;
        for (int i = 1; i < sorted.Length; i++)
        {
            int current = sorted[i];
            if (current == end)
            {
                end++;
                continue;
            }

            ranges.Add((start, end));
            start = current;
            end = current + 1;
        }

        ranges.Add((start, end));
        return ranges;
    }

    private static bool IsFrameInBlurRanges(int frameIndex, List<(int Start, int EndExclusive)> ranges, ref int cursor)
    {
        while (cursor < ranges.Count && frameIndex >= ranges[cursor].EndExclusive)
            cursor++;

        if (cursor >= ranges.Count)
            return false;

        var current = ranges[cursor];
        return frameIndex >= current.Start && frameIndex < current.EndExclusive;
    }

    private static List<(int Start, int EndExclusive)> AlignRangesToKeyframes(
        List<(int Start, int EndExclusive)> ranges,
        List<int> keyframes,
        int totalFrames)
    {
        if (ranges == null || ranges.Count == 0 || keyframes == null || keyframes.Count == 0)
            return ranges ?? new List<(int Start, int EndExclusive)>();

        keyframes.Sort();
        if (keyframes[0] > 0)
            keyframes.Insert(0, 0);

        var aligned = new List<(int Start, int EndExclusive)>(ranges.Count);
        foreach (var (start, endExclusive) in ranges)
        {
            int safeStart = Math.Max(0, start);
            int safeEnd = Math.Max(safeStart + 1, endExclusive);
            int alignedStart = FindFloorKeyframe(keyframes, safeStart);
            int alignedEnd = FindNextKeyframe(keyframes, safeEnd - 1);
            if (alignedEnd <= alignedStart)
                alignedEnd = safeEnd;
            if (totalFrames > 0)
                alignedEnd = Math.Min(alignedEnd, totalFrames);
            aligned.Add((alignedStart, alignedEnd));
        }

        if (aligned.Count <= 1)
            return aligned;

        aligned.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<(int Start, int EndExclusive)>(aligned.Count);
        int curStart = aligned[0].Start;
        int curEnd = aligned[0].EndExclusive;
        for (int i = 1; i < aligned.Count; i++)
        {
            var next = aligned[i];
            if (next.Start <= curEnd)
            {
                curEnd = Math.Max(curEnd, next.EndExclusive);
                continue;
            }

            merged.Add((curStart, curEnd));
            curStart = next.Start;
            curEnd = next.EndExclusive;
        }
        merged.Add((curStart, curEnd));
        return merged;
    }

    private static int FindFloorKeyframe(List<int> keyframes, int value)
    {
        int lo = 0;
        int hi = keyframes.Count - 1;
        int result = keyframes[0];
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            int current = keyframes[mid];
            if (current <= value)
            {
                result = current;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return result;
    }

    private static int FindNextKeyframe(List<int> keyframes, int value)
    {
        int lo = 0;
        int hi = keyframes.Count - 1;
        int result = keyframes[hi];
        bool found = false;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            int current = keyframes[mid];
            if (current > value)
            {
                result = current;
                found = true;
                hi = mid - 1;
            }
            else
            {
                lo = mid + 1;
            }
        }

        return found ? result : Math.Max(value + 1, keyframes[keyframes.Count - 1] + 1);
    }

    private static unsafe List<int> CollectKeyframeFrameIndices(string inputPath, double sourceFps, int totalFrames)
    {
        var keyframes = new List<int>();
        if (string.IsNullOrWhiteSpace(inputPath) || sourceFps <= 0.0)
            return keyframes;

        AVFormatContext* inFmt = null;
        AVPacket* pkt = ffmpeg.av_packet_alloc();
        if (pkt == null)
            return keyframes;

        try
        {
            if (ffmpeg.avformat_open_input(&inFmt, inputPath, null, null) < 0)
                return keyframes;
            if (ffmpeg.avformat_find_stream_info(inFmt, null) < 0)
                return keyframes;

            int videoStreamIndex = -1;
            for (int i = 0; i < inFmt->nb_streams; i++)
            {
                if (inFmt->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                {
                    videoStreamIndex = i;
                    break;
                }
            }
            if (videoStreamIndex < 0)
                return keyframes;

            AVStream* stream = inFmt->streams[videoStreamIndex];
            int fallback = 0;
            while (ffmpeg.av_read_frame(inFmt, pkt) >= 0)
            {
                if (pkt->stream_index != videoStreamIndex)
                {
                    ffmpeg.av_packet_unref(pkt);
                    continue;
                }

                int frameIndex = ResolveFrameIndexFromPacket(pkt, stream->time_base, sourceFps, fallback, totalFrames);
                if (frameIndex < fallback)
                    frameIndex = fallback;
                fallback = frameIndex + 1;

                if ((pkt->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0)
                    keyframes.Add(frameIndex);

                ffmpeg.av_packet_unref(pkt);
            }
        }
        catch
        {
            // 키프레임 스캔 실패 시 일반 경로로 폴백
        }
        finally
        {
            ffmpeg.av_packet_free(&pkt);
            if (inFmt != null)
                ffmpeg.avformat_close_input(&inFmt);
        }

        if (keyframes.Count == 0)
            return keyframes;

        keyframes.Sort();
        var distinct = new List<int>(keyframes.Count);
        int prev = int.MinValue;
        foreach (int index in keyframes)
        {
            if (index == prev)
                continue;
            distinct.Add(index);
            prev = index;
        }

        return distinct;
    }

    private static int ResolveFrameIndexFromPacket(
        AVPacket* packet,
        AVRational timeBase,
        double sourceFps,
        int fallback,
        int totalFrames)
    {
        if (packet == null || sourceFps <= 0.0)
            return fallback;

        long ts = packet->pts != ffmpeg.AV_NOPTS_VALUE ? packet->pts : packet->dts;
        if (ts == ffmpeg.AV_NOPTS_VALUE)
            return fallback;

        double seconds = ts * ffmpeg.av_q2d(timeBase);
        if (double.IsNaN(seconds) || double.IsInfinity(seconds))
            return fallback;

        int index = (int)Math.Round(seconds * sourceFps);
        if (index < 0)
            index = 0;
        if (totalFrames > 0 && index > totalFrames)
            index = totalFrames;
        return index;
    }

    private static int ResolveFrameIndexFromFrame(
        AVFrame* frame,
        AVRational timeBase,
        double sourceFps,
        int fallback,
        int totalFrames)
    {
        if (frame == null || sourceFps <= 0.0)
            return fallback;

        long ts = frame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE
            ? frame->best_effort_timestamp
            : frame->pts;
        if (ts == ffmpeg.AV_NOPTS_VALUE)
            return fallback;

        double seconds = ts * ffmpeg.av_q2d(timeBase);
        if (double.IsNaN(seconds) || double.IsInfinity(seconds))
            return fallback;

        int index = (int)Math.Round(seconds * sourceFps);
        if (index < 0)
            index = 0;
        if (totalFrames > 0 && index > totalFrames)
            index = totalFrames;
        return index;
    }

    private static unsafe void ExportByRemuxCopy(
        AVFormatContext* inFmt,
        string outputPath,
        int videoStreamIndex,
        int totalFrames,
        double sourceFps,
        IProgress<ExportProgress>? progress,
        System.Threading.CancellationToken cancellationToken)
    {
        if (inFmt == null)
            throw new ArgumentNullException(nameof(inFmt));

        AVFormatContext* outFmt = null;
        AVPacket* pkt = ffmpeg.av_packet_alloc();
        if (pkt == null)
            throw new InvalidOperationException("패킷 버퍼를 할당하지 못했습니다.");

        try
        {
            Throw(ffmpeg.avformat_alloc_output_context2(&outFmt, null, null, outputPath));

            int streamCount = (int)inFmt->nb_streams;
            var streamMap = new int[streamCount];
            for (int i = 0; i < streamCount; i++)
            {
                streamMap[i] = -1;
                AVStream* inStream = inFmt->streams[i];
                AVStream* outStream = ffmpeg.avformat_new_stream(outFmt, null);
                if (outStream == null)
                    throw new InvalidOperationException("출력 스트림을 생성하지 못했습니다.");

                Throw(ffmpeg.avcodec_parameters_copy(outStream->codecpar, inStream->codecpar));
                outStream->codecpar->codec_tag = 0;
                outStream->time_base = inStream->time_base;
                streamMap[i] = outStream->index;
            }

            if ((outFmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
                Throw(ffmpeg.avio_open(&outFmt->pb, outputPath, ffmpeg.AVIO_FLAG_WRITE));

            Throw(ffmpeg.avformat_write_header(outFmt, null));

            int lastReportedFrame = -1;
            while (ffmpeg.av_read_frame(inFmt, pkt) >= 0)
            {
                if (cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException(cancellationToken);

                int inIndex = pkt->stream_index;
                if (inIndex < 0 || inIndex >= streamMap.Length || streamMap[inIndex] < 0)
                {
                    ffmpeg.av_packet_unref(pkt);
                    continue;
                }

                AVStream* inStream = inFmt->streams[inIndex];
                AVStream* outStream = outFmt->streams[streamMap[inIndex]];
                ffmpeg.av_packet_rescale_ts(pkt, inStream->time_base, outStream->time_base);
                pkt->stream_index = outStream->index;
                pkt->pos = -1;
                Throw(ffmpeg.av_interleaved_write_frame(outFmt, pkt));

                if (progress != null && inIndex == videoStreamIndex && totalFrames > 0 && sourceFps > 0.0)
                {
                    long ts = pkt->pts != ffmpeg.AV_NOPTS_VALUE ? pkt->pts : pkt->dts;
                    if (ts != ffmpeg.AV_NOPTS_VALUE)
                    {
                        double seconds = ts * ffmpeg.av_q2d(outStream->time_base);
                        int currentFrame = (int)Math.Round(seconds * sourceFps);
                        if (currentFrame < 0) currentFrame = 0;
                        if (currentFrame > totalFrames) currentFrame = totalFrames;

                        if (currentFrame - lastReportedFrame >= 15 || currentFrame >= totalFrames)
                        {
                            progress.Report(new ExportProgress(currentFrame, totalFrames, "원본 스트림 복사 중..."));
                            lastReportedFrame = currentFrame;
                        }
                    }
                }

                ffmpeg.av_packet_unref(pkt);
            }

            Throw(ffmpeg.av_write_trailer(outFmt));
            progress?.Report(new ExportProgress(totalFrames, totalFrames, "블러 대상이 없어 무손실 고속 복사로 완료했습니다."));
        }
        finally
        {
            ffmpeg.av_packet_free(&pkt);

            if (outFmt != null)
            {
                if ((outFmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
                    ffmpeg.avio_closep(&outFmt->pb);
                ffmpeg.avformat_free_context(outFmt);
            }
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

        var attemptedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidateName in GetPreferredEncoderNames(codecId))
        {
            if (string.IsNullOrWhiteSpace(candidateName) || !attemptedNames.Add(candidateName))
                continue;

            AVCodec* candidate = ffmpeg.avcodec_find_encoder_by_name(candidateName);
            if (candidate == null || candidate->id != codecId)
                continue;

            var ctx = TryOpenEncoderContext(candidate, inStream, dec, outFmt, out var openError);
            if (ctx != null)
            {
                encoder = candidate;
                return ctx;
            }

            error = AppendEncoderError(error, candidateName, openError);
        }

        AVCodec* fallback = ffmpeg.avcodec_find_encoder(codecId);
        if (fallback == null)
        {
            error = AppendEncoderError(
                error,
                GetCodecName(codecId),
                $"인코더를 찾을 수 없습니다(코덱: {GetCodecName(codecId)}). FFmpeg 빌드에 해당 인코더가 포함되어 있지 않을 수 있습니다.");
            return null;
        }

        string fallbackName = fallback->name != null
            ? (Marshal.PtrToStringAnsi((IntPtr)fallback->name) ?? GetCodecName(codecId))
            : GetCodecName(codecId);
        if (attemptedNames.Add(fallbackName))
        {
            var ctx = TryOpenEncoderContext(fallback, inStream, dec, outFmt, out var fallbackError);
            if (ctx != null)
            {
                encoder = fallback;
                return ctx;
            }

            error = AppendEncoderError(error, fallbackName, fallbackError);
        }

        return null;
    }

    private static unsafe AVCodecContext* TryOpenEncoderContext(
        AVCodec* encoder,
        AVStream* inStream,
        AVCodecContext* dec,
        AVFormatContext* outFmt,
        out string? error)
    {
        error = null;
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
        if (ctx->framerate.num == 0 || ctx->framerate.den == 0)
            ctx->framerate = new AVRational { num = 30, den = 1 };

        int sourceBitrate = 0;
        if (inStream->codecpar->bit_rate > 0)
            sourceBitrate = ClampBitrate(inStream->codecpar->bit_rate);
        else if (dec->bit_rate > 0)
            sourceBitrate = ClampBitrate(dec->bit_rate);

        int targetBitrate = sourceBitrate > 0
            ? ClampBitrate((long)sourceBitrate * 11L / 10L)
            : EstimateHighQualityBitrate(ctx->width, ctx->height, ctx->framerate);
        targetBitrate = Math.Max(targetBitrate, 2_000_000);
        ctx->bit_rate = targetBitrate;
        ctx->rc_max_rate = targetBitrate;
        ctx->rc_buffer_size = ClampBitrate((long)targetBitrate * 2L);

        if (inStream->codecpar->profile != -99)
            ctx->profile = inStream->codecpar->profile;
        if (inStream->codecpar->level > 0)
            ctx->level = inStream->codecpar->level;

        ctx->sample_aspect_ratio = inStream->sample_aspect_ratio;
        ctx->color_range = dec->color_range;
        ctx->color_primaries = dec->color_primaries;
        ctx->color_trc = dec->color_trc;
        ctx->colorspace = dec->colorspace;
        ctx->chroma_sample_location = dec->chroma_sample_location;

        if ((outFmt->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
            ctx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

        if (!IsPixFmtSupported(encoder, ctx->pix_fmt))
            ctx->pix_fmt = PickPreferredPixelFormat(encoder, ctx->pix_fmt);

        ctx->thread_count = IsHardwareEncoder(encoder)
            ? 0
            : Math.Max(1, Environment.ProcessorCount - 2);
        ApplyHighQualityEncoderOptions(ctx, encoder);

        int openErr = ffmpeg.avcodec_open2(ctx, encoder, null);
        if (openErr < 0)
        {
            error = GetErrorMessage(openErr);
            ffmpeg.avcodec_free_context(&ctx);
            return null;
        }

        return ctx;
    }

    private static bool IsHardwareEncoder(AVCodec* encoder)
    {
        if (encoder == null || encoder->name == null)
            return false;

        string name = Marshal.PtrToStringAnsi((IntPtr)encoder->name) ?? string.Empty;
        if (name.Length == 0)
            return false;

        return name.Contains("videotoolbox", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("nvenc", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("qsv", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("amf", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("vaapi", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("v4l2m2m", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> GetPreferredEncoderNames(AVCodecID codecId)
    {
        bool isMac = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        if (codecId == AVCodecID.AV_CODEC_ID_H264)
        {
            if (isMac)
                return new[] { "h264_videotoolbox", "libx264" };
            if (isWindows)
                return new[] { "h264_nvenc", "h264_qsv", "h264_amf", "libx264" };
            return new[] { "h264_nvenc", "h264_vaapi", "libx264" };
        }

        if (codecId == AVCodecID.AV_CODEC_ID_HEVC)
        {
            if (isMac)
                return new[] { "hevc_videotoolbox", "libx265" };
            if (isWindows)
                return new[] { "hevc_nvenc", "hevc_qsv", "hevc_amf", "libx265" };
            return new[] { "hevc_nvenc", "hevc_vaapi", "libx265" };
        }

        return Array.Empty<string>();
    }

    private static string AppendEncoderError(string? existing, string encoderName, string? detail)
    {
        string part = string.IsNullOrWhiteSpace(detail)
            ? $"{encoderName}: 초기화 실패"
            : $"{encoderName}: {detail}";

        if (string.IsNullOrWhiteSpace(existing))
            return part;
        return $"{existing} | {part}";
    }

    private static int EstimateHighQualityBitrate(int width, int height, AVRational framerate)
    {
        long baseFloor;
        long pixels = Math.Max(1L, (long)width * height);
        if (pixels <= 1280L * 720L)
            baseFloor = 6_000_000;
        else if (pixels <= 1920L * 1080L)
            baseFloor = 10_000_000;
        else if (pixels <= 2560L * 1440L)
            baseFloor = 16_000_000;
        else if (pixels <= 3840L * 2160L)
            baseFloor = 28_000_000;
        else
            baseFloor = 40_000_000;

        double fps = framerate.den != 0 ? ffmpeg.av_q2d(framerate) : 0.0;
        if (fps <= 0.0)
            fps = 30.0;
        if (fps > 30.0)
            baseFloor = (long)Math.Round(baseFloor * (fps / 30.0));

        return ClampBitrate(baseFloor);
    }

    private static int ClampBitrate(long value)
    {
        if (value <= 0)
            return 0;
        if (value > int.MaxValue)
            return int.MaxValue;
        return (int)value;
    }

    private static unsafe AVPixelFormat PickPreferredPixelFormat(AVCodec* encoder, AVPixelFormat preferred)
    {
#pragma warning disable CS0618 // AVCodec.pix_fmts obsolete in ffmpeg headers
        if (encoder == null || encoder->pix_fmts == null)
        {
            if (preferred != AVPixelFormat.AV_PIX_FMT_NONE)
                return preferred;
            return AVPixelFormat.AV_PIX_FMT_YUV420P;
        }

        AVPixelFormat first = AVPixelFormat.AV_PIX_FMT_NONE;
        bool hasYuv420 = false;
        for (AVPixelFormat* p = encoder->pix_fmts; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
        {
            if (first == AVPixelFormat.AV_PIX_FMT_NONE)
                first = *p;
            if (*p == preferred)
                return preferred;
            if (*p == AVPixelFormat.AV_PIX_FMT_YUV420P)
                hasYuv420 = true;
        }

        if (hasYuv420)
            return AVPixelFormat.AV_PIX_FMT_YUV420P;

        if (first != AVPixelFormat.AV_PIX_FMT_NONE)
            return first;
        return AVPixelFormat.AV_PIX_FMT_YUV420P;
#pragma warning restore CS0618
    }

    private static unsafe void ApplyHighQualityEncoderOptions(AVCodecContext* ctx, AVCodec* encoder)
    {
        if (ctx == null || encoder == null || encoder->name == null)
            return;

        string name = Marshal.PtrToStringAnsi((IntPtr)encoder->name) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
            return;

        bool isX264 = name.Contains("x264", StringComparison.OrdinalIgnoreCase);
        bool isX265 = name.Contains("x265", StringComparison.OrdinalIgnoreCase);
        bool isH264Family = name.Contains("h264", StringComparison.OrdinalIgnoreCase);
        bool isHevcFamily = name.Contains("hevc", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("h265", StringComparison.OrdinalIgnoreCase);

        if (isX264)
        {
            TrySetEncoderOption(ctx, "preset", "faster");
            TrySetEncoderOption(ctx, "crf", "14");
            return;
        }

        if (isX265)
        {
            TrySetEncoderOption(ctx, "preset", "fast");
            TrySetEncoderOption(ctx, "crf", "16");
            return;
        }

        if (isH264Family || isHevcFamily)
        {
            TrySetEncoderOption(ctx, "qmin", "10");
            TrySetEncoderOption(ctx, "qmax", "28");
        }

        if (name.Contains("videotoolbox", StringComparison.OrdinalIgnoreCase))
            TrySetEncoderOption(ctx, "realtime", "true");
    }

    private static unsafe void TrySetEncoderOption(AVCodecContext* ctx, string key, string value)
    {
        if (ctx == null || ctx->priv_data == null)
            return;

        ffmpeg.av_opt_set(ctx->priv_data, key, value, 0);
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
