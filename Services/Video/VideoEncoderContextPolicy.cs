using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FaceShield.Services.Video;

internal static unsafe class VideoEncoderContextPolicy
{
    internal static AVCodecContext* TryCreateEncoderContext(
        AVCodecID codecId,
        AVStream* inStream,
        AVCodecContext* dec,
        AVFormatContext* outFmt,
        out AVCodec* encoder,
        out EncoderQualityConfiguration qualityConfiguration,
        out string? error,
        bool forceSoftwareEncoder,
        bool forceSafeEncoding,
        VideoHdrMetadata? hdrMetadata)
    {
        encoder = null;
        qualityConfiguration = EncoderQualityConfiguration.Unconfigured;
        error = null;

        if (outFmt != null && outFmt->oformat != null)
        {
            int supported =
                ffmpeg.avformat_query_codec(outFmt->oformat, codecId, 0);
            if (supported <= 0)
            {
                string formatName =
                    VideoExportFfmpegDiagnostics.GetOutputFormatName(outFmt);
                error =
                    $"출력 컨테이너({formatName})가 코덱(" +
                    $"{VideoExportFfmpegDiagnostics.GetCodecName(codecId)})을 지원하지 않습니다.";
                return null;
            }
        }

        AVPixelFormat sourcePixelFormat =
            VideoExportFidelityPolicy.ResolveSourcePixelFormat(inStream, dec);
        bool requiresLosslessRgbH264 =
            codecId == AVCodecID.AV_CODEC_ID_H264 &&
            VideoExportFidelityPolicy.IsRgbPixelFormat(sourcePixelFormat);

        if (requiresLosslessRgbH264 &&
            !VideoExportFidelityPolicy.CanEncodeLosslessX264Rgb(
                sourcePixelFormat,
                dec->color_range,
                dec->colorspace,
                out string? rgbCompatibilityError))
        {
            throw new VideoExportIntegrityException(
                "RGB H.264 원본을 품질 저하 없이 내보낼 수 없습니다. " +
                rgbCompatibilityError);
        }

        bool allowTenBitHevcFallback =
            codecId == AVCodecID.AV_CODEC_ID_H264 &&
            !requiresLosslessRgbH264 &&
            VideoExportFidelityPolicy.GetPixelFormatBitDepth(dec->pix_fmt) > 8;

        var attemptedNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<string> candidateNames =
            requiresLosslessRgbH264
                ? new[] { "libx264rgb" }
                : VideoEncoderSelectionPolicy.GetCandidateNames(
                    codecId,
                    forceSoftwareEncoder,
                    allowTenBitHevcFallback);

        foreach (string candidateName in candidateNames)
        {
            if (string.IsNullOrWhiteSpace(candidateName) ||
                !attemptedNames.Add(candidateName))
            {
                continue;
            }

            AVCodec* candidate =
                ffmpeg.avcodec_find_encoder_by_name(candidateName);
            if (candidate == null ||
                (candidate->id != codecId &&
                 !(allowTenBitHevcFallback &&
                   candidate->id == AVCodecID.AV_CODEC_ID_HEVC)))
            {
                continue;
            }

            if (hdrMetadata?.HasStaticMetadata == true &&
                VideoEncoderSelectionPolicy.IsHardwareEncoder(candidate))
            {
                error = VideoEncoderSelectionPolicy.AppendError(
                    error,
                    candidateName,
                    "HDR mastering/CLL 메타데이터 전달이 검증되지 않은 하드웨어 인코더입니다.");
                continue;
            }

            AVChromaLocation sourceChromaLocation =
                VideoHdrMetadataPolicy.ResolveSourceChromaLocation(
                    inStream,
                    dec);
            if (VideoEncoderSelectionPolicy
                    .RequiresSoftwareEncoderForChromaLocation(
                        sourceChromaLocation) &&
                VideoEncoderSelectionPolicy.IsHardwareEncoder(candidate))
            {
                error = VideoEncoderSelectionPolicy.AppendError(
                    error,
                    candidateName,
                    $"원본 chroma 위치({sourceChromaLocation}) 보존이 검증되지 않은 하드웨어 인코더입니다.");
                continue;
            }

            if (outFmt != null &&
                outFmt->oformat != null &&
                ffmpeg.avformat_query_codec(
                    outFmt->oformat,
                    candidate->id,
                    0) <= 0)
            {
                error = VideoEncoderSelectionPolicy.AppendError(
                    error,
                    candidateName,
                    $"출력 컨테이너({VideoExportFfmpegDiagnostics.GetOutputFormatName(outFmt)})가 " +
                    $"코덱({VideoExportFfmpegDiagnostics.GetCodecName(candidate->id)})을 지원하지 않습니다.");
                continue;
            }

            AVCodecContext* ctx = TryOpenEncoderContext(
                candidate,
                inStream,
                dec,
                outFmt,
                out EncoderQualityConfiguration candidateQualityConfiguration,
                out string? openError,
                forceSafeEncoding,
                hdrMetadata);
            if (ctx != null)
            {
                encoder = candidate;
                qualityConfiguration = candidateQualityConfiguration;
                return ctx;
            }

            Debug.WriteLine(
                $"[ExportEncoderCandidate] name={candidateName}, " +
                $"codec={VideoExportFfmpegDiagnostics.GetCodecName(candidate->id)}, " +
                $"opened=false, error={openError ?? "unknown"}");
            error = VideoEncoderSelectionPolicy.AppendError(
                error,
                candidateName,
                openError);
        }

        if (requiresLosslessRgbH264)
        {
            throw new VideoExportIntegrityException(
                "RGB H.264 원본은 검증된 무손실 libx264rgb 경로로만 내보낼 수 있습니다. " +
                $"인코더 초기화 실패: {error ?? "libx264rgb를 찾을 수 없습니다."}");
        }

        AVCodec* fallback =
            VideoEncoderSelectionPolicy.SelectFallbackEncoder(
                codecId,
                forceSoftwareEncoder ||
                hdrMetadata?.HasStaticMetadata == true);
        if (fallback == null)
        {
            error = VideoEncoderSelectionPolicy.AppendError(
                error,
                VideoExportFfmpegDiagnostics.GetCodecName(codecId),
                $"인코더를 찾을 수 없습니다(코덱: " +
                $"{VideoExportFfmpegDiagnostics.GetCodecName(codecId)}). " +
                "FFmpeg 빌드에 해당 인코더가 포함되어 있지 않을 수 있습니다.");
            return null;
        }

        string fallbackName =
            fallback->name != null
                ? Marshal.PtrToStringAnsi((IntPtr)fallback->name) ??
                  VideoExportFfmpegDiagnostics.GetCodecName(codecId)
                : VideoExportFfmpegDiagnostics.GetCodecName(codecId);

        if (attemptedNames.Add(fallbackName))
        {
            AVCodecContext* ctx = TryOpenEncoderContext(
                fallback,
                inStream,
                dec,
                outFmt,
                out EncoderQualityConfiguration fallbackQualityConfiguration,
                out string? fallbackError,
                forceSafeEncoding,
                hdrMetadata);
            if (ctx != null)
            {
                encoder = fallback;
                qualityConfiguration = fallbackQualityConfiguration;
                return ctx;
            }

            error = VideoEncoderSelectionPolicy.AppendError(
                error,
                fallbackName,
                fallbackError);
        }

        return null;
    }

    private static AVCodecContext* TryOpenEncoderContext(
        AVCodec* encoder,
        AVStream* inStream,
        AVCodecContext* dec,
        AVFormatContext* outFmt,
        out EncoderQualityConfiguration qualityConfiguration,
        out string? error,
        bool forceSafeEncoding,
        VideoHdrMetadata? hdrMetadata)
    {
        qualityConfiguration = EncoderQualityConfiguration.Unconfigured;
        error = null;

        AVCodecContext* ctx = ffmpeg.avcodec_alloc_context3(encoder);
        if (ctx == null)
        {
            error = "인코더 컨텍스트를 만들 수 없습니다.";
            return null;
        }

        string encoderName =
            VideoExportFfmpegDiagnostics.GetEncoderName(encoder);
        bool isLosslessX264Rgb = string.Equals(
            encoderName,
            "libx264rgb",
            StringComparison.OrdinalIgnoreCase);
        AVPixelFormat sourcePixelFormat =
            VideoExportFidelityPolicy.ResolveSourcePixelFormat(inStream, dec);

        ctx->width = dec->width;
        ctx->height = dec->height;
        ctx->pix_fmt = sourcePixelFormat;

        if (isLosslessX264Rgb)
        {
            if (!VideoExportFidelityPolicy.CanEncodeLosslessX264Rgb(
                    sourcePixelFormat,
                    dec->color_range,
                    dec->colorspace,
                    out string? rgbCompatibilityError))
            {
                error = rgbCompatibilityError;
                ffmpeg.avcodec_free_context(&ctx);
                return null;
            }

            if (!VideoExportFidelityPolicy.IsPixFmtSupported(
                    encoder,
                    AVPixelFormat.AV_PIX_FMT_BGR24))
            {
                error =
                    "libx264rgb가 검증된 BGR24 입력 형식을 지원하지 않습니다.";
                ffmpeg.avcodec_free_context(&ctx);
                return null;
            }

            ctx->pix_fmt = AVPixelFormat.AV_PIX_FMT_BGR24;
        }

        ctx->time_base = inStream->time_base;
        ctx->framerate =
            inStream->r_frame_rate.num != 0
                ? inStream->r_frame_rate
                : inStream->avg_frame_rate;

        if (ctx->framerate.num == 0 || ctx->framerate.den == 0)
            ctx->framerate = new AVRational { num = 30, den = 1 };

        if ((ctx->time_base.num <= 0 || ctx->time_base.den <= 0) &&
            ctx->framerate.num > 0 &&
            ctx->framerate.den > 0)
        {
            AVRational stableTimeBase = ffmpeg.av_inv_q(ctx->framerate);
            if (stableTimeBase.num > 0 && stableTimeBase.den > 0)
                ctx->time_base = stableTimeBase;
        }

        if (ctx->time_base.num <= 0 || ctx->time_base.den <= 0)
            ctx->time_base = inStream->time_base;

        long sourceBitrate =
            VideoExportFidelityPolicy.ResolveSourceVideoBitrate(
                inStream,
                dec);
        int targetBitrate =
            VideoExportFidelityPolicy.ResolveHighQualityTargetBitrate(
                sourceBitrate,
                ctx->width,
                ctx->height,
                ctx->framerate,
                encoder->id);

        bool usesSoftwareConstantQuality =
            VideoEncoderSelectionPolicy.UsesSoftwareConstantQuality(
                encoderName);
        if (usesSoftwareConstantQuality)
        {
            ctx->bit_rate = 0;
            ctx->rc_max_rate = 0;
            ctx->rc_buffer_size = 0;
        }
        else
        {
            ctx->bit_rate = targetBitrate;
            ctx->rc_max_rate =
                VideoExportFidelityPolicy.ClampBitrate(
                    (long)targetBitrate * 2L);
            ctx->rc_buffer_size =
                VideoExportFidelityPolicy.ClampBitrate(
                    (long)targetBitrate * 4L);
        }

        if (encoder->id == inStream->codecpar->codec_id)
        {
            if (inStream->codecpar->profile != -99)
                ctx->profile = inStream->codecpar->profile;
            if (!isLosslessX264Rgb &&
                inStream->codecpar->level > 0)
            {
                ctx->level = inStream->codecpar->level;
            }
        }

        ctx->sample_aspect_ratio = inStream->sample_aspect_ratio;
        ctx->color_range = dec->color_range;
        ctx->color_primaries = dec->color_primaries;
        ctx->color_trc = dec->color_trc;
        ctx->colorspace = dec->colorspace;
        ctx->chroma_sample_location =
            VideoHdrMetadataPolicy.ResolveSourceChromaLocation(
                inStream,
                dec);

        if (isLosslessX264Rgb)
        {
            ctx->color_range = AVColorRange.AVCOL_RANGE_JPEG;
            ctx->colorspace = AVColorSpace.AVCOL_SPC_RGB;
            ctx->chroma_sample_location =
                AVChromaLocation.AVCHROMA_LOC_UNSPECIFIED;
        }

        ctx->flags |= ffmpeg.AV_CODEC_FLAG_FRAME_DURATION;

        if ((outFmt->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
            ctx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

        if (!VideoExportFidelityPolicy.IsPixFmtSupported(
                encoder,
                ctx->pix_fmt))
        {
            ctx->pix_fmt =
                VideoExportFidelityPolicy.PickPreferredPixelFormat(
                    encoder,
                    ctx->pix_fmt);
        }

        string? pixelFormatLoss =
            VideoExportFidelityPolicy.GetPixelFormatLossReason(
                sourcePixelFormat,
                ctx->pix_fmt);
        if (!string.IsNullOrWhiteSpace(pixelFormatLoss))
        {
            error =
                $"원본 픽셀 품질을 보존할 수 없습니다: {pixelFormatLoss}";
            ffmpeg.avcodec_free_context(&ctx);
            return null;
        }

        if (forceSafeEncoding)
        {
            ctx->max_b_frames = 0;
            ctx->gop_size = 60;
        }

        ctx->thread_count =
            VideoEncoderSelectionPolicy.IsHardwareEncoder(encoder)
                ? 0
                : Math.Max(1, Environment.ProcessorCount - 2);

        if (hdrMetadata?.HasStaticMetadata == true)
        {
            string hdrEncoderName =
                VideoExportFfmpegDiagnostics.GetEncoderName(encoder);
            bool isX265 = hdrEncoderName.Contains(
                "x265",
                StringComparison.OrdinalIgnoreCase);
            bool isSvtAv1 =
                encoder->id == AVCodecID.AV_CODEC_ID_AV1 &&
                hdrEncoderName.Contains(
                    "svtav1",
                    StringComparison.OrdinalIgnoreCase);

            if (!isX265 && !isSvtAv1)
            {
                error =
                    $"인코더({hdrEncoderName})가 HDR mastering/CLL 메타데이터 보존을 지원하지 않습니다.";
                ffmpeg.avcodec_free_context(&ctx);
                return null;
            }

            if (isX265 && !hdrMetadata.CanConfigureX265)
            {
                error =
                    "불완전한 HDR mastering/CLL 메타데이터를 x265에 정확히 전달할 수 없습니다.";
                ffmpeg.avcodec_free_context(&ctx);
                return null;
            }

            if (isSvtAv1 &&
                !VideoHdrMetadataPolicy.CanConfigureSvtAv1StaticHdr(
                    hdrMetadata,
                    out string? hdrCompatibilityError))
            {
                error = hdrCompatibilityError;
                ffmpeg.avcodec_free_context(&ctx);
                return null;
            }

            if (isSvtAv1 &&
                !VideoHdrMetadataPolicy.TryConfigureEncoderStaticHdrMetadata(
                    ctx,
                    hdrMetadata,
                    out string? hdrError))
            {
                error = hdrError;
                ffmpeg.avcodec_free_context(&ctx);
                return null;
            }
        }

        qualityConfiguration = VideoEncoderQualityPolicy.Apply(
            ctx,
            encoder,
            forceSafeEncoding,
            hdrMetadata?.HasStaticMetadata == true,
            hdrMetadata?.ToX265Params());

        if (!qualityConfiguration.RequiredOptionsApplied)
        {
            error =
                $"필수 품질 옵션을 적용하지 못했습니다: " +
                $"{qualityConfiguration.FailedOptions}";
            ffmpeg.avcodec_free_context(&ctx);
            return null;
        }

        int openErr = ffmpeg.avcodec_open2(ctx, encoder, null);
        if (openErr < 0)
        {
            error = VideoExportFfmpegDiagnostics.GetErrorMessage(openErr);
            ffmpeg.avcodec_free_context(&ctx);
            return null;
        }

        return ctx;
    }
}
