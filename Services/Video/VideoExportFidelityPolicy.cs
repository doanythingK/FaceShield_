using FFmpeg.AutoGen;
using System;

namespace FaceShield.Services.Video;

internal static unsafe class VideoExportFidelityPolicy
{
    internal static bool IsPixFmtSupported(AVCodec* encoder, AVPixelFormat format)
    {
#pragma warning disable CS0618 // AVCodec.pix_fmts obsolete in ffmpeg headers
        if (encoder == null || encoder->pix_fmts == null)
            return true;

        for (AVPixelFormat* current = encoder->pix_fmts;
             *current != AVPixelFormat.AV_PIX_FMT_NONE;
             current++)
        {
            if (*current == format)
                return true;
        }

        return false;
#pragma warning restore CS0618
    }

    internal static AVPixelFormat ResolveSourcePixelFormat(
        AVStream* stream,
        AVCodecContext* decoder)
    {
        if (decoder != null && decoder->pix_fmt != AVPixelFormat.AV_PIX_FMT_NONE)
            return decoder->pix_fmt;
        if (stream != null && stream->codecpar != null && stream->codecpar->format != -1)
            return (AVPixelFormat)stream->codecpar->format;
        return AVPixelFormat.AV_PIX_FMT_NONE;
    }

    internal static bool IsRgbPixelFormat(AVPixelFormat pixelFormat)
    {
        AVPixFmtDescriptor* descriptor = ffmpeg.av_pix_fmt_desc_get(pixelFormat);
        return descriptor != null &&
               (descriptor->flags & ffmpeg.AV_PIX_FMT_FLAG_RGB) != 0;
    }

    internal static bool CanEncodeCompatibleX264Rgb(
        AVPixelFormat sourcePixelFormat,
        AVColorRange sourceColorRange,
        AVColorSpace sourceColorSpace,
        out string? error)
    {
        error = null;
        AVPixFmtDescriptor* descriptor = ffmpeg.av_pix_fmt_desc_get(sourcePixelFormat);
        if (descriptor == null)
        {
            error =
                $"원본 픽셀 형식({GetPixelFormatName(sourcePixelFormat)}) 정보를 확인할 수 없습니다.";
            return false;
        }
        if ((descriptor->flags & ffmpeg.AV_PIX_FMT_FLAG_RGB) == 0)
        {
            error = $"원본 픽셀 형식({GetPixelFormatName(sourcePixelFormat)})이 RGB가 아닙니다.";
            return false;
        }
        if ((descriptor->flags & ffmpeg.AV_PIX_FMT_FLAG_ALPHA) != 0)
        {
            error =
                $"알파 채널이 있는 RGB 형식({GetPixelFormatName(sourcePixelFormat)})은 현재 RGB H.264 품질 경로에서 지원하지 않습니다.";
            return false;
        }
        if (descriptor->nb_components != 3)
        {
            error =
                $"RGB 색상 성분 수({descriptor->nb_components})는 현재 RGB H.264 품질 경로에서 지원하지 않습니다.";
            return false;
        }
        if (descriptor->log2_chroma_w != 0 || descriptor->log2_chroma_h != 0)
        {
            error =
                $"서브샘플링된 RGB 형식({GetPixelFormatName(sourcePixelFormat)})은 " +
                "현재 libx264rgb RGB 품질 경로에서 지원하지 않습니다.";
            return false;
        }
        for (int component = 0; component < descriptor->nb_components; component++)
        {
            if (descriptor->comp[(uint)component].depth != 8)
            {
                error =
                    $"8비트가 아닌 RGB 형식({GetPixelFormatName(sourcePixelFormat)})은 " +
                    "현재 libx264rgb RGB 품질 경로에서 지원하지 않습니다.";
                return false;
            }
        }
        if (sourceColorRange is not
            AVColorRange.AVCOL_RANGE_UNSPECIFIED and not
            AVColorRange.AVCOL_RANGE_JPEG)
        {
            error =
                $"지원하지 않는 RGB 범위 태그({sourceColorRange})는 " +
                "현재 libx264rgb RGB 품질 경로에서 지원하지 않습니다.";
            return false;
        }
        if (sourceColorSpace is not
            AVColorSpace.AVCOL_SPC_UNSPECIFIED and not
            AVColorSpace.AVCOL_SPC_RGB)
        {
            error =
                $"RGB가 아닌 matrix 태그({sourceColorSpace})가 지정된 RGB 영상은 " +
                "현재 RGB H.264 품질 경로에서 지원하지 않습니다.";
            return false;
        }

        return true;
    }

    internal static int ResolveHighQualityTargetBitrate(
        long sourceBitrate,
        int width,
        int height,
        AVRational framerate,
        AVCodecID codecId)
    {
        int resolutionFloor = EstimateHighQualityBitrate(width, height, framerate);
        int boundedSourceBitrate = ClampBitrate(sourceBitrate);
        if (boundedSourceBitrate > 0)
        {
            int sourceRelativeGuardrail = ClampBitrate(
                Math.Min(
                    (long)resolutionFloor,
                    (long)boundedSourceBitrate * 5L / 4L));
            int targetBitrate = Math.Max(
                boundedSourceBitrate,
                sourceRelativeGuardrail);
            return Math.Max(targetBitrate, 2_000_000);
        }

        return Math.Max(resolutionFloor, 2_000_000);
    }

    internal static int ClampBitrate(long value)
    {
        if (value <= 0)
            return 0;
        if (value > int.MaxValue)
            return int.MaxValue;
        return (int)value;
    }

    internal static AVPixelFormat PickPreferredPixelFormat(
        AVCodec* encoder,
        AVPixelFormat preferred)
    {
#pragma warning disable CS0618 // AVCodec.pix_fmts obsolete in ffmpeg headers
        if (encoder == null || encoder->pix_fmts == null)
        {
            if (preferred != AVPixelFormat.AV_PIX_FMT_NONE)
                return preferred;
            return AVPixelFormat.AV_PIX_FMT_YUV420P;
        }

        AVPixelFormat first = AVPixelFormat.AV_PIX_FMT_NONE;
        for (AVPixelFormat* current = encoder->pix_fmts;
             *current != AVPixelFormat.AV_PIX_FMT_NONE;
             current++)
        {
            if (first == AVPixelFormat.AV_PIX_FMT_NONE)
                first = *current;
            if (*current == preferred)
                return preferred;
        }

        if (preferred != AVPixelFormat.AV_PIX_FMT_NONE)
        {
            int loss = 0;
            AVPixelFormat best = ffmpeg.avcodec_find_best_pix_fmt_of_list(
                encoder->pix_fmts,
                preferred,
                0,
                &loss);
            if (best != AVPixelFormat.AV_PIX_FMT_NONE)
                return best;
        }

        if (first != AVPixelFormat.AV_PIX_FMT_NONE)
            return first;
        return AVPixelFormat.AV_PIX_FMT_YUV420P;
#pragma warning restore CS0618
    }

    internal static string GetPixelFormatName(AVPixelFormat pixelFormat)
    {
        if (pixelFormat == AVPixelFormat.AV_PIX_FMT_NONE)
            return "unknown";

        string? name = ffmpeg.av_get_pix_fmt_name(pixelFormat);
        return string.IsNullOrWhiteSpace(name) ? pixelFormat.ToString() : name;
    }

    internal static int GetPixelFormatBitDepth(AVPixelFormat pixelFormat)
    {
        if (pixelFormat == AVPixelFormat.AV_PIX_FMT_NONE)
            return 0;

        AVPixFmtDescriptor* descriptor = ffmpeg.av_pix_fmt_desc_get(pixelFormat);
        return descriptor == null || descriptor->nb_components == 0
            ? 0
            : descriptor->comp[0].depth;
    }

    internal static string? GetPixelFormatLossReason(
        AVPixelFormat sourcePixelFormat,
        AVPixelFormat outputPixelFormat)
    {
        if (sourcePixelFormat == AVPixelFormat.AV_PIX_FMT_NONE ||
            outputPixelFormat == AVPixelFormat.AV_PIX_FMT_NONE)
        {
            return null;
        }

        AVPixFmtDescriptor* source = ffmpeg.av_pix_fmt_desc_get(sourcePixelFormat);
        AVPixFmtDescriptor* output = ffmpeg.av_pix_fmt_desc_get(outputPixelFormat);
        if (source == null || output == null)
            return null;

        bool sourceIsRgb = (source->flags & ffmpeg.AV_PIX_FMT_FLAG_RGB) != 0;
        bool outputIsRgb = (output->flags & ffmpeg.AV_PIX_FMT_FLAG_RGB) != 0;
        if (sourceIsRgb != outputIsRgb)
        {
            return $"색상 모델 변경 {GetPixelFormatName(sourcePixelFormat)} -> " +
                   GetPixelFormatName(outputPixelFormat);
        }

        bool sourceHasAlpha = (source->flags & ffmpeg.AV_PIX_FMT_FLAG_ALPHA) != 0;
        bool outputHasAlpha = (output->flags & ffmpeg.AV_PIX_FMT_FLAG_ALPHA) != 0;
        if (sourceHasAlpha && !outputHasAlpha)
        {
            return $"알파 채널 유실 {GetPixelFormatName(sourcePixelFormat)} -> " +
                   GetPixelFormatName(outputPixelFormat);
        }

        int comparableComponents = Math.Min(source->nb_components, output->nb_components);
        for (int component = 0; component < comparableComponents; component++)
        {
            int sourceBitDepth = source->comp[(uint)component].depth;
            int outputBitDepth = output->comp[(uint)component].depth;
            if (sourceBitDepth > 0 && outputBitDepth > 0 && outputBitDepth < sourceBitDepth)
            {
                return
                    $"비트 심도 하락 {GetPixelFormatName(sourcePixelFormat)}({sourceBitDepth}) -> " +
                    $"{GetPixelFormatName(outputPixelFormat)}({outputBitDepth})";
            }
        }

        if (output->nb_components < source->nb_components)
        {
            return
                $"색상 성분 감소 {GetPixelFormatName(sourcePixelFormat)}({source->nb_components}) -> " +
                $"{GetPixelFormatName(outputPixelFormat)}({output->nb_components})";
        }

        if (source->nb_components >= 3 &&
            output->nb_components >= 3 &&
            (output->log2_chroma_w > source->log2_chroma_w ||
             output->log2_chroma_h > source->log2_chroma_h))
        {
            return $"색차 해상도 하락 {GetPixelFormatName(sourcePixelFormat)} -> " +
                   GetPixelFormatName(outputPixelFormat);
        }

        return null;
    }

    internal static long ResolveSourceVideoBitrate(
        AVStream* stream,
        AVCodecContext* decoder)
    {
        if (stream != null && stream->codecpar != null && stream->codecpar->bit_rate > 0)
            return stream->codecpar->bit_rate;
        if (decoder != null && decoder->bit_rate > 0)
            return decoder->bit_rate;
        return 0;
    }

    internal static long ResolveTargetVideoBitrateForSummary(
        AVCodec* encoder,
        AVStream* stream,
        AVCodecContext* decoder,
        AVCodecContext* encoderContext)
    {
        if (encoderContext != null && encoderContext->bit_rate > 0)
            return encoderContext->bit_rate;
        if (!VideoEncoderSelectionPolicy.IsHardwareEncoder(encoder))
            return 0;

        long sourceBitrate = ResolveSourceVideoBitrate(stream, decoder);
        return sourceBitrate > 0
            ? ClampBitrate(sourceBitrate)
            : 0;
    }

    private static int EstimateHighQualityBitrate(
        int width,
        int height,
        AVRational framerate)
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
    internal static void ValidateDecodedFrameFormatCompatibility(
        AVFrame* frame,
        AVCodecContext* encoderContext,
        bool x264RgbConfigured)
    {
        if (frame == null || encoderContext == null)
        {
            throw new VideoExportIntegrityException(
                "프레임 픽셀 품질 검증에 필요한 디코더 또는 인코더 정보가 없습니다.");
        }

        if (frame->width != encoderContext->width ||
            frame->height != encoderContext->height)
        {
            throw new VideoExportIntegrityException(
                $"영상 도중 해상도가 변경됐습니다({frame->width}x{frame->height} -> " +
                $"{encoderContext->width}x{encoderContext->height}). " +
                "자동 크기 변환 없이 원본 품질을 보존할 수 없어 내보내기를 중단했습니다.");
        }

        AVPixelFormat framePixelFormat = (AVPixelFormat)frame->format;
        string? pixelFormatLoss = GetPixelFormatLossReason(
            framePixelFormat,
            encoderContext->pix_fmt);
        if (!string.IsNullOrWhiteSpace(pixelFormatLoss))
        {
            throw new VideoExportIntegrityException(
                $"영상 도중 원본 픽셀 형식이 변경되어 품질을 보존할 수 없습니다: {pixelFormatLoss}");
        }

        if (!x264RgbConfigured)
            return;

        if (!CanEncodeCompatibleX264Rgb(
                framePixelFormat,
                frame->color_range,
                frame->colorspace,
                out string? rgbCompatibilityError))
        {
            throw new VideoExportIntegrityException(
                "영상 도중 RGB H.264 픽셀 속성이 변경됐습니다. " +
                rgbCompatibilityError);
        }

        if (frame->color_primaries != AVColorPrimaries.AVCOL_PRI_UNSPECIFIED &&
            frame->color_primaries != encoderContext->color_primaries)
        {
            throw new VideoExportIntegrityException(
                $"영상 도중 RGB color primaries가 변경됐습니다({frame->color_primaries}).");
        }

        if (frame->color_trc !=
                AVColorTransferCharacteristic.AVCOL_TRC_UNSPECIFIED &&
            frame->color_trc != encoderContext->color_trc)
        {
            throw new VideoExportIntegrityException(
                $"영상 도중 RGB transfer characteristic이 변경됐습니다({frame->color_trc}).");
        }
    }

}
