using FFmpeg.AutoGen;
using System;

namespace FaceShield.Services.Video;

internal static unsafe class VideoFrameColorPolicy
{
    internal static void CopyFrameEncodingProperties(
        AVFrame* source,
        AVFrame* destination)
    {
        if (source == null || destination == null || source == destination)
            return;

        ffmpeg.av_frame_side_data_free(
            &destination->side_data,
            &destination->nb_side_data);
        ffmpeg.av_dict_free(&destination->metadata);
        Throw(ffmpeg.av_frame_copy_props(destination, source));
    }

    internal static SwsContext* CreateDynamicSwsContext(string stage)
    {
        SwsContext* context = ffmpeg.sws_alloc_context();
        if (context == null)
        {
            throw new VideoExportIntegrityException(
                $"{stage}용 색상 변환 컨텍스트를 만들 수 없습니다.");
        }

        int optionResult = ffmpeg.av_opt_set_int(
            context,
            "sws_flags",
            (long)SwsFlags.SWS_FAST_BILINEAR,
            0);
        if (optionResult < 0)
        {
            ffmpeg.sws_freeContext(context);
            throw new VideoExportIntegrityException(
                $"{stage}의 색상 변환 옵션을 설정할 수 없습니다: " +
                GetErrorMessage(optionResult));
        }

        return context;
    }

    internal static void ScaleFramePreservingColor(
        SwsContext* context,
        AVFrame* destination,
        AVFrame* source,
        string stage)
    {
        if (context == null || destination == null || source == null)
        {
            throw new VideoExportIntegrityException(
                $"{stage}에 필요한 프레임 또는 색상 변환 컨텍스트가 없습니다.");
        }

        int result = ffmpeg.sws_scale_frame(context, destination, source);
        if (result < 0)
        {
            throw new VideoExportIntegrityException(
                $"{stage} 중 원본 색 공간, 범위 또는 색차 위치를 보존할 수 없습니다: " +
                GetErrorMessage(result));
        }
    }

    internal static void SetBgraColorProperties(AVFrame* source, AVFrame* bgra)
    {
        if (source == null || bgra == null)
            return;

        CopyFrameEncodingProperties(source, bgra);
        bgra->color_range = AVColorRange.AVCOL_RANGE_JPEG;
        bgra->color_primaries = source->color_primaries;
        bgra->color_trc = source->color_trc;
        bgra->colorspace = AVColorSpace.AVCOL_SPC_RGB;
        bgra->chroma_location = AVChromaLocation.AVCHROMA_LOC_UNSPECIFIED;
        bgra->sample_aspect_ratio = source->sample_aspect_ratio;
    }

    private static string GetErrorMessage(int errorCode)
    {
        byte* buffer = stackalloc byte[1024];
        ffmpeg.av_strerror(errorCode, buffer, 1024);
        return System.Text.Encoding.UTF8
            .GetString(new ReadOnlySpan<byte>(buffer, 1024))
            .TrimEnd('\0');
    }

    private static void Throw(int errorCode)
    {
        if (errorCode >= 0)
            return;

        throw new InvalidOperationException(GetErrorMessage(errorCode));
    }
}
