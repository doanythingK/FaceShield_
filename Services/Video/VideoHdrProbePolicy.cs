using FFmpeg.AutoGen;
using System;
using System.Threading;

namespace FaceShield.Services.Video;

internal static unsafe class VideoHdrProbePolicy
{
    private const int MaxProbeFrames = 16;

    internal static VideoHdrMetadata? ProbeVideoHdrMetadata(
        string inputPath,
        CancellationToken cancellationToken = default)
    {
        AVFormatContext* format = null;
        AVCodecContext* decoderContext = null;
        AVPacket* packet = ffmpeg.av_packet_alloc();
        AVFrame* decodedFrame = ffmpeg.av_frame_alloc();
        if (packet == null || decodedFrame == null)
        {
            ffmpeg.av_packet_free(&packet);
            ffmpeg.av_frame_free(&decodedFrame);
            throw new InvalidOperationException(
                "HDR 메타데이터 확인용 프레임을 할당할 수 없습니다.");
        }

        using var ioInterrupt = new VideoIoInterruptGuard();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            format = ffmpeg.avformat_alloc_context();
            if (format == null)
                throw new InvalidOperationException("HDR 메타데이터 확인용 입력 컨텍스트를 만들 수 없습니다.");
            ioInterrupt.Configure(format);

            int openResult;
            using (ioInterrupt.Begin(cancellationToken))
                openResult = ffmpeg.avformat_open_input(&format, inputPath, null, null);
            cancellationToken.ThrowIfCancellationRequested();
            VideoExportFfmpegDiagnostics.Throw(openResult);

            int streamInfoResult;
            using (ioInterrupt.Begin(cancellationToken))
                streamInfoResult = ffmpeg.avformat_find_stream_info(format, null);
            cancellationToken.ThrowIfCancellationRequested();
            VideoExportFfmpegDiagnostics.Throw(streamInfoResult);

            int videoStreamIndex =
                FFmpegStreamSelection.FindPrimaryVideoStreamIndex(format);
            if (videoStreamIndex < 0)
                return null;

            AVCodecParameters* parameters =
                format->streams[videoStreamIndex]->codecpar;
            AVCodec* decoder =
                ffmpeg.avcodec_find_decoder(parameters->codec_id);
            if (decoder == null)
            {
                throw new InvalidOperationException(
                    "HDR 메타데이터 확인용 비디오 디코더를 찾을 수 없습니다.");
            }

            decoderContext = ffmpeg.avcodec_alloc_context3(decoder);
            if (decoderContext == null)
            {
                throw new InvalidOperationException(
                    "HDR 메타데이터 확인용 디코더를 만들 수 없습니다.");
            }

            VideoExportFfmpegDiagnostics.Throw(
                ffmpeg.avcodec_parameters_to_context(
                    decoderContext,
                    parameters));
            VideoHdrMetadataPolicy.ConfigureDecoderSideDataExport(
                decoderContext,
                parameters->codec_id);
            VideoExportFfmpegDiagnostics.Throw(
                ffmpeg.avcodec_open2(decoderContext, decoder, null));

            int decodedFrames = 0;
            bool reachedProbeLimit = false;
            VideoHdrMetadata? accumulatedMetadata = null;

            while (!reachedProbeLimit)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int readResult;
                using (ioInterrupt.Begin(cancellationToken))
                    readResult = ffmpeg.av_read_frame(format, packet);
                cancellationToken.ThrowIfCancellationRequested();
                if (readResult < 0)
                    break;

                if (packet->stream_index != videoStreamIndex)
                {
                    ffmpeg.av_packet_unref(packet);
                    continue;
                }

                int sendError =
                    ffmpeg.avcodec_send_packet(decoderContext, packet);
                ffmpeg.av_packet_unref(packet);
                if (sendError < 0)
                    continue;

                while (ffmpeg.avcodec_receive_frame(
                           decoderContext,
                           decodedFrame) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    decodedFrames++;
                    MergeFrameMetadata(decodedFrame, ref accumulatedMetadata);
                    ffmpeg.av_frame_unref(decodedFrame);

                    if (decodedFrames >= MaxProbeFrames)
                    {
                        reachedProbeLimit = true;
                        break;
                    }
                }
            }

            if (!reachedProbeLimit)
            {
                _ = ffmpeg.avcodec_send_packet(decoderContext, null);
                while (ffmpeg.avcodec_receive_frame(
                           decoderContext,
                           decodedFrame) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    decodedFrames++;
                    MergeFrameMetadata(decodedFrame, ref accumulatedMetadata);
                    ffmpeg.av_frame_unref(decodedFrame);

                    if (decodedFrames >= MaxProbeFrames)
                        break;
                }
            }

            return accumulatedMetadata;
        }
        finally
        {
            ffmpeg.av_frame_free(&decodedFrame);
            ffmpeg.av_packet_free(&packet);
            ffmpeg.avcodec_free_context(&decoderContext);
            if (format != null)
                ffmpeg.avformat_close_input(&format);
        }
    }

    private static void MergeFrameMetadata(
        AVFrame* decodedFrame,
        ref VideoHdrMetadata? accumulatedMetadata)
    {
        string? unsupportedMetadata =
            FFmpegHdrMetadataGuard.FindUnsupportedMetadata(decodedFrame);
        if (unsupportedMetadata != null)
        {
            VideoExportCompatibilityPolicy
                .ThrowUnsupportedDynamicVideoMetadata(unsupportedMetadata);
        }

        accumulatedMetadata =
            VideoHdrMetadataPolicy.MergeVideoHdrMetadata(
                accumulatedMetadata,
                VideoHdrMetadataPolicy.ReadVideoHdrMetadata(decodedFrame));
    }
}
