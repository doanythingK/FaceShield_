using Avalonia.Media.Imaging;
using FFmpeg.AutoGen;
using System;
using System.Diagnostics;

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
        SwsContext* swsDecToBgra = null;
        SwsContext* swsBgraToEnc = null;

        AVPacket* pkt = ffmpeg.av_packet_alloc();
        AVPacket* outPkt = ffmpeg.av_packet_alloc();
        AVFrame* frame = ffmpeg.av_frame_alloc();
        AVFrame* bgra = ffmpeg.av_frame_alloc();
        AVFrame* encFrame = ffmpeg.av_frame_alloc();

        int videoStreamIndex = -1;
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
                if (inFmt->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                {
                    videoStreamIndex = i;
                    break;
                }
            }
            if (videoStreamIndex < 0)
                throw new InvalidOperationException("Video stream not found.");

            AVStream* inStream = inFmt->streams[videoStreamIndex];
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

            AVCodec* encoder = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_H264);
            enc = ffmpeg.avcodec_alloc_context3(encoder);

            enc->width = dec->width;
            enc->height = dec->height;
            enc->pix_fmt = dec->pix_fmt;
            enc->time_base = inStream->time_base;
            enc->framerate = inStream->r_frame_rate;

            if ((outFmt->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
                enc->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

            if (!IsPixFmtSupported(encoder, enc->pix_fmt))
                enc->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;

            Throw(ffmpeg.avcodec_open2(enc, encoder, null));

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
            ffmpeg.av_packet_free(&pkt);
            ffmpeg.av_packet_free(&outPkt);

            ffmpeg.avcodec_free_context(&dec);
            ffmpeg.avcodec_free_context(&enc);

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
        if (encoder == null || encoder->pix_fmts == null)
            return true;

        for (AVPixelFormat* p = encoder->pix_fmts; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
        {
            if (*p == fmt)
                return true;
        }

        return false;
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
