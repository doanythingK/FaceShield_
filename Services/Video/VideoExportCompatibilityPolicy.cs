using FFmpeg.AutoGen;
using System;

namespace FaceShield.Services.Video;

internal static unsafe class VideoExportCompatibilityPolicy
{
    internal static AVFieldOrder ResolveSourceFieldOrder(
        AVStream* stream,
        AVCodecContext* decoder)
    {
        if (decoder != null &&
            decoder->field_order != AVFieldOrder.AV_FIELD_UNKNOWN)
        {
            return decoder->field_order;
        }

        if (stream != null &&
            stream->codecpar != null &&
            stream->codecpar->field_order != AVFieldOrder.AV_FIELD_UNKNOWN)
        {
            return stream->codecpar->field_order;
        }

        return AVFieldOrder.AV_FIELD_UNKNOWN;
    }

    internal static bool IsInterlacedFieldOrder(AVFieldOrder fieldOrder)
    {
        return fieldOrder is
            AVFieldOrder.AV_FIELD_TT or
            AVFieldOrder.AV_FIELD_BB or
            AVFieldOrder.AV_FIELD_TB or
            AVFieldOrder.AV_FIELD_BT;
    }

    internal static void ThrowInterlacedAutoMosaicUnsupported(
        AVFieldOrder fieldOrder)
    {
        throw new VideoExportIntegrityException(
            "인터레이스 영상은 현재 자동 모자이크 시 필드 순서를 안전하게 보존할 수 없어 " +
            $"내보내기를 중단했습니다(fieldOrder={fieldOrder}). 원본 영상은 변경되지 않았습니다.");
    }

    internal static void ThrowUnsupportedDynamicVideoMetadata(
        string metadataName)
    {
        throw new InvalidOperationException(
            $"{metadataName} 영상 메타데이터는 현재 내보내기에서 원본 그대로 보존할 수 없습니다. " +
            "품질 저하를 막기 위해 내보내기를 중단했습니다.");
    }
}
