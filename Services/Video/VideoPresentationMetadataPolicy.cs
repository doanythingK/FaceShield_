using FFmpeg.AutoGen;
using System;

namespace FaceShield.Services.Video;

internal static unsafe class VideoPresentationMetadataPolicy
{
    internal static void EnsureContainerStructureSupported(AVFormatContext* format)
    {
        string? unsupportedStructureKey =
            FFmpegContainerStructureGuard.FindUnsupportedStructure(format);
        if (unsupportedStructureKey == null)
            return;

        string unsupportedStructure = unsupportedStructureKey == "programs"
            ? "프로그램 단위 스트림 구성"
            : "IAMF 등 스트림 그룹 구성";

        throw new InvalidOperationException(
            $"{unsupportedStructure}은 현재 내보내기에서 원본 그대로 보존할 수 없습니다. " +
            "스트림 구성 유실을 막기 위해 내보내기를 중단했습니다.");
    }

    internal static void CopyStreamPresentationMetadata(AVStream* source, AVStream* output)
    {
        if (source == null || output == null)
            return;

        output->avg_frame_rate = source->avg_frame_rate;
        output->r_frame_rate = source->r_frame_rate;
        output->sample_aspect_ratio = source->sample_aspect_ratio;
        output->disposition = source->disposition;
        Throw(ffmpeg.av_dict_copy(&output->metadata, source->metadata, 0));
        CopyCodecPresentationSideData(source->codecpar, output->codecpar);
    }

    internal static void CopyFormatPresentationMetadata(
        AVFormatContext* source,
        AVFormatContext* output)
    {
        if (source == null || output == null)
            return;

        Throw(ffmpeg.av_dict_copy(&output->metadata, source->metadata, 0));
        int chapterCount = checked((int)source->nb_chapters);
        if (chapterCount <= 0)
            return;
        if (source->chapters == null)
            throw new InvalidOperationException("원본 챕터 목록 정보가 없습니다.");

        output->chapters = (AVChapter**)ffmpeg.av_calloc(
            (ulong)chapterCount,
            (ulong)sizeof(AVChapter*));
        if (output->chapters == null)
            throw new InvalidOperationException("챕터 목록 메모리를 할당할 수 없습니다.");

        for (int i = 0; i < chapterCount; i++)
        {
            AVChapter* sourceChapter = source->chapters[i];
            if (sourceChapter == null)
                throw new InvalidOperationException($"원본 챕터 {i + 1} 정보가 없습니다.");

            AVChapter* outputChapter =
                (AVChapter*)ffmpeg.av_mallocz((ulong)sizeof(AVChapter));
            if (outputChapter == null)
                throw new InvalidOperationException($"챕터 {i + 1} 메모리를 할당할 수 없습니다.");

            outputChapter->id = sourceChapter->id;
            outputChapter->time_base = sourceChapter->time_base;
            outputChapter->start = sourceChapter->start;
            outputChapter->end = sourceChapter->end;
            output->chapters[i] = outputChapter;
            output->nb_chapters++;
            Throw(ffmpeg.av_dict_copy(
                &outputChapter->metadata,
                sourceChapter->metadata,
                0));
        }
    }

    private static void CopyCodecPresentationSideData(
        AVCodecParameters* source,
        AVCodecParameters* output)
    {
        if (source == null ||
            output == null ||
            source->coded_side_data == null ||
            source->nb_coded_side_data <= 0)
        {
            return;
        }

        for (int i = 0; i < source->nb_coded_side_data; i++)
        {
            AVPacketSideData* sourceSideData = source->coded_side_data + i;
            if (sourceSideData == null ||
                sourceSideData->data == null ||
                sourceSideData->size == 0 ||
                !IsPresentationSideData(sourceSideData->type))
            {
                continue;
            }

            ffmpeg.av_packet_side_data_remove(
                output->coded_side_data,
                &output->nb_coded_side_data,
                sourceSideData->type);

            ulong sourceSize = (ulong)sourceSideData->size;
            AVPacketSideData* outputSideData = ffmpeg.av_packet_side_data_new(
                &output->coded_side_data,
                &output->nb_coded_side_data,
                sourceSideData->type,
                sourceSize,
                0);
            if (outputSideData == null || outputSideData->data == null)
            {
                throw new InvalidOperationException(
                    $"스트림 부가정보({sourceSideData->type})를 복사할 수 없습니다.");
            }

            Buffer.MemoryCopy(
                sourceSideData->data,
                outputSideData->data,
                checked((long)sourceSize),
                checked((long)sourceSize));
        }
    }

    private static bool IsPresentationSideData(AVPacketSideDataType type)
    {
        return type is
            AVPacketSideDataType.AV_PKT_DATA_DISPLAYMATRIX or
            AVPacketSideDataType.AV_PKT_DATA_STEREO3D or
            AVPacketSideDataType.AV_PKT_DATA_MASTERING_DISPLAY_METADATA or
            AVPacketSideDataType.AV_PKT_DATA_SPHERICAL or
            AVPacketSideDataType.AV_PKT_DATA_CONTENT_LIGHT_LEVEL or
            AVPacketSideDataType.AV_PKT_DATA_ICC_PROFILE or
            AVPacketSideDataType.AV_PKT_DATA_DYNAMIC_HDR10_PLUS or
            AVPacketSideDataType.AV_PKT_DATA_AMBIENT_VIEWING_ENVIRONMENT;
    }

    private static void Throw(int errorCode)
    {
        if (errorCode >= 0)
            return;

        byte* buffer = stackalloc byte[1024];
        ffmpeg.av_strerror(errorCode, buffer, 1024);
        throw new InvalidOperationException(
            System.Text.Encoding.UTF8
                .GetString(new ReadOnlySpan<byte>(buffer, 1024))
                .TrimEnd('\0'));
    }
}
