using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace FaceShield.Services.Video;

internal sealed record VideoHdrMetadata(
    string? MasterDisplay,
    string? MaxContentLightLevel,
    byte[]? MasteringDisplayPayload,
    byte[]? ContentLightPayload)
{
    internal bool HasStaticMetadata =>
        MasteringDisplayPayload != null || ContentLightPayload != null;

    internal bool CanConfigureX265 =>
        (MasteringDisplayPayload == null || !string.IsNullOrWhiteSpace(MasterDisplay)) &&
        (ContentLightPayload == null || !string.IsNullOrWhiteSpace(MaxContentLightLevel));

    internal string ToX265Params()
    {
        var values = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(MasterDisplay))
            values.Add($"master-display={MasterDisplay}");
        if (!string.IsNullOrWhiteSpace(MaxContentLightLevel))
            values.Add($"max-cll={MaxContentLightLevel}");
        return string.Join(':', values);
    }
}

internal static unsafe class VideoHdrMetadataPolicy
{
    internal static VideoHdrMetadata? ReadVideoHdrMetadata(AVFrame* frame)
    {
        if (frame == null)
            return null;

        byte[]? masteringDisplayPayload = null;
        AVFrameSideData* masteringSideData = ffmpeg.av_frame_get_side_data(
            frame,
            AVFrameSideDataType.AV_FRAME_DATA_MASTERING_DISPLAY_METADATA);
        if (masteringSideData != null)
        {
            masteringDisplayPayload = CopyNativeSideDataPayload(
                masteringSideData->data,
                masteringSideData->size,
                sizeof(AVMasteringDisplayMetadata),
                "mastering display");
        }

        byte[]? contentLightPayload = null;
        AVFrameSideData* contentLightSideData = ffmpeg.av_frame_get_side_data(
            frame,
            AVFrameSideDataType.AV_FRAME_DATA_CONTENT_LIGHT_LEVEL);
        if (contentLightSideData != null)
        {
            contentLightPayload = CopyNativeSideDataPayload(
                contentLightSideData->data,
                contentLightSideData->size,
                sizeof(AVContentLightMetadata),
                "content light");
        }

        return CreateVideoHdrMetadata(masteringDisplayPayload, contentLightPayload);
    }

    internal static VideoHdrMetadata? MergeVideoHdrMetadata(
        VideoHdrMetadata? accumulated,
        VideoHdrMetadata? current)
    {
        if (current == null)
            return accumulated;
        if (accumulated == null)
            return current;

        byte[]? masteringDisplayPayload = MergeStaticHdrPayload(
            "mastering display",
            AVFrameSideDataType.AV_FRAME_DATA_MASTERING_DISPLAY_METADATA,
            accumulated.MasteringDisplayPayload,
            current.MasteringDisplayPayload);
        byte[]? contentLightPayload = MergeStaticHdrPayload(
            "content light",
            AVFrameSideDataType.AV_FRAME_DATA_CONTENT_LIGHT_LEVEL,
            accumulated.ContentLightPayload,
            current.ContentLightPayload);

        return new VideoHdrMetadata(
            accumulated.MasterDisplay ?? current.MasterDisplay,
            accumulated.MaxContentLightLevel ?? current.MaxContentLightLevel,
            masteringDisplayPayload,
            contentLightPayload);
    }

    internal static void ConfigureDecoderSideDataExport(
        AVCodecContext* decoderContext,
        AVCodecID codecId)
    {
        if (decoderContext != null && codecId == AVCodecID.AV_CODEC_ID_AV1)
            decoderContext->export_side_data |= ffmpeg.AV_CODEC_EXPORT_DATA_FILM_GRAIN;
    }

    internal static bool TryConfigureEncoderStaticHdrMetadata(
        AVCodecContext* encoderContext,
        VideoHdrMetadata hdrMetadata,
        out string? error)
    {
        error = null;
        if (!TryAddEncoderStaticHdrPayload(
                encoderContext,
                AVFrameSideDataType.AV_FRAME_DATA_MASTERING_DISPLAY_METADATA,
                hdrMetadata.MasteringDisplayPayload,
                "mastering display",
                out error))
        {
            return false;
        }

        return TryAddEncoderStaticHdrPayload(
            encoderContext,
            AVFrameSideDataType.AV_FRAME_DATA_CONTENT_LIGHT_LEVEL,
            hdrMetadata.ContentLightPayload,
            "content light",
            out error);
    }

    internal static void ValidateFrameStaticHdrPayload(
        AVFrame* frame,
        AVFrameSideDataType type,
        byte[]? configuredPayload,
        string metadataName)
    {
        AVFrameSideData* sideData = ffmpeg.av_frame_get_side_data(frame, type);
        if (sideData == null)
            return;
        if (configuredPayload == null ||
            sideData->data == null ||
            sideData->size < (ulong)configuredPayload.Length)
        {
            throw new InvalidOperationException(
                $"정적 HDR {metadataName} 메타데이터를 인코더 초기화 전에 정확히 구성하지 못했습니다. " +
                "품질 저하를 막기 위해 내보내기를 중단했습니다.");
        }

        var actualPayload = new ReadOnlySpan<byte>(sideData->data, configuredPayload.Length);
        if (!AreStaticHdrPayloadsEquivalent(type, configuredPayload, actualPayload))
        {
            throw new InvalidOperationException(
                $"정적 HDR {metadataName} 메타데이터가 탐색 구간 이후 변경됐습니다. " +
                "원본 표현 손실을 막기 위해 내보내기를 중단했습니다.");
        }
    }

    internal static bool CanConfigureSvtAv1StaticHdr(
        VideoHdrMetadata hdrMetadata,
        out string? error)
    {
        error = null;
        if (hdrMetadata.MasteringDisplayPayload != null)
        {
            fixed (byte* payload = hdrMetadata.MasteringDisplayPayload)
            {
                AVMasteringDisplayMetadata* mastering =
                    (AVMasteringDisplayMetadata*)payload;
                if (mastering->has_primaries == 0 || mastering->has_luminance == 0)
                {
                    error =
                        "SVT-AV1은 부분 mastering display 메타데이터를 원본 형태로 보존할 수 없습니다.";
                    return false;
                }

                for (uint color = 0; color < 3; color++)
                {
                    for (uint coordinate = 0; coordinate < 2; coordinate++)
                    {
                        if (!IsExactlyRepresentableHdrRational(
                                mastering->display_primaries[color][coordinate],
                                1L << 16,
                                ushort.MaxValue))
                        {
                            error =
                                "SVT-AV1 Q16 범위에서 정확히 표현할 수 없는 mastering display 색좌표입니다.";
                            return false;
                        }
                    }
                }

                for (uint coordinate = 0; coordinate < 2; coordinate++)
                {
                    if (!IsExactlyRepresentableHdrRational(
                            mastering->white_point[coordinate],
                            1L << 16,
                            ushort.MaxValue))
                    {
                        error =
                            "SVT-AV1 Q16 범위에서 정확히 표현할 수 없는 mastering display 백색점입니다.";
                        return false;
                    }
                }

                if (!IsExactlyRepresentableHdrRational(
                        mastering->max_luminance,
                        1L << 8,
                        uint.MaxValue) ||
                    mastering->max_luminance.num <= 0 ||
                    !IsExactlyRepresentableHdrRational(
                        mastering->min_luminance,
                        1L << 14,
                        uint.MaxValue))
                {
                    error =
                        "SVT-AV1 고정폭 범위에서 정확히 표현할 수 없는 mastering display 휘도입니다.";
                    return false;
                }
            }
        }

        if (hdrMetadata.ContentLightPayload != null)
        {
            fixed (byte* payload = hdrMetadata.ContentLightPayload)
            {
                AVContentLightMetadata* contentLight =
                    (AVContentLightMetadata*)payload;
                if (contentLight->MaxCLL == 0 ||
                    contentLight->MaxCLL > ushort.MaxValue ||
                    contentLight->MaxFALL > ushort.MaxValue)
                {
                    error =
                        "SVT-AV1 16비트 범위에서 정확히 표현할 수 없는 content light 메타데이터입니다.";
                    return false;
                }
            }
        }

        return true;
    }

    internal static AVChromaLocation ResolveSourceChromaLocation(
        AVStream* stream,
        AVCodecContext* decoder)
    {
        if (decoder != null &&
            decoder->chroma_sample_location != AVChromaLocation.AVCHROMA_LOC_UNSPECIFIED)
        {
            return decoder->chroma_sample_location;
        }

        if (stream != null &&
            stream->codecpar != null &&
            stream->codecpar->chroma_location != AVChromaLocation.AVCHROMA_LOC_UNSPECIFIED)
        {
            return stream->codecpar->chroma_location;
        }

        return AVChromaLocation.AVCHROMA_LOC_UNSPECIFIED;
    }

    private static VideoHdrMetadata? CreateVideoHdrMetadata(
        byte[]? masteringDisplayPayload,
        byte[]? contentLightPayload)
    {
        string? masterDisplay = null;
        if (masteringDisplayPayload != null)
        {
            fixed (byte* payload = masteringDisplayPayload)
            {
                AVMasteringDisplayMetadata* mastering =
                    (AVMasteringDisplayMetadata*)payload;
                if (mastering->has_primaries == 0 && mastering->has_luminance == 0)
                {
                    masteringDisplayPayload = null;
                }
                else
                {
                    if (mastering->has_primaries != 0)
                    {
                        for (uint color = 0; color < 3; color++)
                        {
                            for (uint coordinate = 0; coordinate < 2; coordinate++)
                            {
                                _ = ScaleHdrRational(
                                    mastering->display_primaries[color][coordinate],
                                    50_000);
                            }
                        }

                        for (uint coordinate = 0; coordinate < 2; coordinate++)
                            _ = ScaleHdrRational(mastering->white_point[coordinate], 50_000);
                    }

                    if (mastering->has_luminance != 0)
                    {
                        _ = ScaleHdrRational(mastering->max_luminance, 10_000);
                        _ = ScaleHdrRational(mastering->min_luminance, 10_000);
                    }

                    if (mastering->has_primaries != 0 &&
                        mastering->has_luminance != 0)
                    {
                        long redX = ScaleHdrRational(
                            mastering->display_primaries[0][0], 50_000);
                        long redY = ScaleHdrRational(
                            mastering->display_primaries[0][1], 50_000);
                        long greenX = ScaleHdrRational(
                            mastering->display_primaries[1][0], 50_000);
                        long greenY = ScaleHdrRational(
                            mastering->display_primaries[1][1], 50_000);
                        long blueX = ScaleHdrRational(
                            mastering->display_primaries[2][0], 50_000);
                        long blueY = ScaleHdrRational(
                            mastering->display_primaries[2][1], 50_000);
                        long whiteX = ScaleHdrRational(mastering->white_point[0], 50_000);
                        long whiteY = ScaleHdrRational(mastering->white_point[1], 50_000);
                        long maxLuminance = ScaleHdrRational(
                            mastering->max_luminance, 10_000);
                        long minLuminance = ScaleHdrRational(
                            mastering->min_luminance, 10_000);

                        masterDisplay =
                            $"G({greenX},{greenY})B({blueX},{blueY})R({redX},{redY})" +
                            $"WP({whiteX},{whiteY})L({maxLuminance},{minLuminance})";
                    }
                }
            }
        }

        string? maxContentLightLevel = null;
        if (contentLightPayload != null)
        {
            fixed (byte* payload = contentLightPayload)
            {
                AVContentLightMetadata* contentLight =
                    (AVContentLightMetadata*)payload;
                maxContentLightLevel = $"{contentLight->MaxCLL},{contentLight->MaxFALL}";
            }
        }

        return masteringDisplayPayload == null && contentLightPayload == null
            ? null
            : new VideoHdrMetadata(
                masterDisplay,
                maxContentLightLevel,
                masteringDisplayPayload,
                contentLightPayload);
    }

    private static byte[]? MergeStaticHdrPayload(
        string metadataName,
        AVFrameSideDataType type,
        byte[]? accumulated,
        byte[]? current)
    {
        if (current == null)
            return accumulated;
        if (accumulated == null)
            return current;

        if (!AreStaticHdrPayloadsEquivalent(type, accumulated, current))
        {
            throw new InvalidOperationException(
                $"정적 HDR {metadataName} 메타데이터가 프레임 사이에서 변경됩니다. " +
                "원본 표현 손실을 막기 위해 내보내기를 중단했습니다.");
        }

        return accumulated;
    }

    private static bool AreStaticHdrPayloadsEquivalent(
        AVFrameSideDataType type,
        ReadOnlySpan<byte> left,
        ReadOnlySpan<byte> right)
    {
        if (type == AVFrameSideDataType.AV_FRAME_DATA_CONTENT_LIGHT_LEVEL)
        {
            if (left.Length < sizeof(AVContentLightMetadata) ||
                right.Length < sizeof(AVContentLightMetadata))
            {
                return false;
            }

            fixed (byte* leftPayload = left)
            fixed (byte* rightPayload = right)
            {
                AVContentLightMetadata* leftMetadata =
                    (AVContentLightMetadata*)leftPayload;
                AVContentLightMetadata* rightMetadata =
                    (AVContentLightMetadata*)rightPayload;
                return leftMetadata->MaxCLL == rightMetadata->MaxCLL &&
                       leftMetadata->MaxFALL == rightMetadata->MaxFALL;
            }
        }

        if (type != AVFrameSideDataType.AV_FRAME_DATA_MASTERING_DISPLAY_METADATA ||
            left.Length < sizeof(AVMasteringDisplayMetadata) ||
            right.Length < sizeof(AVMasteringDisplayMetadata))
        {
            return false;
        }

        fixed (byte* leftPayload = left)
        fixed (byte* rightPayload = right)
        {
            AVMasteringDisplayMetadata* leftMetadata =
                (AVMasteringDisplayMetadata*)leftPayload;
            AVMasteringDisplayMetadata* rightMetadata =
                (AVMasteringDisplayMetadata*)rightPayload;

            if (leftMetadata->has_primaries != rightMetadata->has_primaries ||
                leftMetadata->has_luminance != rightMetadata->has_luminance)
            {
                return false;
            }

            if (leftMetadata->has_primaries != 0)
            {
                for (int color = 0; color < 3; color++)
                {
                    for (int coordinate = 0; coordinate < 2; coordinate++)
                    {
                        if (!AreHdrRationalsEquivalent(
                                leftMetadata->display_primaries[(uint)color][(uint)coordinate],
                                rightMetadata->display_primaries[(uint)color][(uint)coordinate]))
                        {
                            return false;
                        }
                    }
                }

                for (int coordinate = 0; coordinate < 2; coordinate++)
                {
                    if (!AreHdrRationalsEquivalent(
                            leftMetadata->white_point[(uint)coordinate],
                            rightMetadata->white_point[(uint)coordinate]))
                    {
                        return false;
                    }
                }
            }

            return leftMetadata->has_luminance == 0 ||
                   (AreHdrRationalsEquivalent(
                        leftMetadata->max_luminance,
                        rightMetadata->max_luminance) &&
                    AreHdrRationalsEquivalent(
                        leftMetadata->min_luminance,
                        rightMetadata->min_luminance));
        }
    }

    private static bool AreHdrRationalsEquivalent(AVRational left, AVRational right)
    {
        return left.den > 0 &&
               right.den > 0 &&
               left.num >= 0 &&
               right.num >= 0 &&
               (long)left.num * right.den == (long)right.num * left.den;
    }

    private static byte[] CopyNativeSideDataPayload(
        byte* data,
        ulong size,
        int expectedSize,
        string metadataName)
    {
        if (data == null || expectedSize <= 0 || size < (ulong)expectedSize)
        {
            throw new InvalidOperationException(
                $"정적 HDR {metadataName} 메타데이터 payload가 올바르지 않습니다.");
        }

        var payload = new byte[expectedSize];
        Marshal.Copy((IntPtr)data, payload, 0, payload.Length);
        return payload;
    }

    private static long ScaleHdrRational(AVRational value, long scale)
    {
        if (value.den <= 0 || value.num < 0)
        {
            throw new InvalidOperationException(
                "정적 HDR 메타데이터에 유효하지 않은 좌표 또는 휘도 값이 있습니다. " +
                "원본 표현 손실을 막기 위해 내보내기를 중단했습니다.");
        }

        return (long)Math.Round(
            value.num * (double)scale / value.den,
            MidpointRounding.AwayFromZero);
    }

    private static bool TryAddEncoderStaticHdrPayload(
        AVCodecContext* encoderContext,
        AVFrameSideDataType type,
        byte[]? payload,
        string metadataName,
        out string? error)
    {
        error = null;
        if (payload == null)
            return true;

        if (encoderContext == null || payload.Length == 0)
        {
            error = $"HDR {metadataName} 메타데이터 payload가 비어 있습니다.";
            return false;
        }

        ffmpeg.av_frame_side_data_remove(
            &encoderContext->decoded_side_data,
            &encoderContext->nb_decoded_side_data,
            type);
        AVFrameSideData* sideData = ffmpeg.av_frame_side_data_new(
            &encoderContext->decoded_side_data,
            &encoderContext->nb_decoded_side_data,
            type,
            (ulong)payload.Length,
            0);
        if (sideData == null || sideData->data == null)
        {
            error = $"HDR {metadataName} 메타데이터를 AV1 인코더에 전달할 수 없습니다.";
            return false;
        }

        Marshal.Copy(payload, 0, (IntPtr)sideData->data, payload.Length);
        return true;
    }

    private static bool IsExactlyRepresentableHdrRational(
        AVRational value,
        long scale,
        ulong maxEncodedValue)
    {
        if (value.den <= 0 || value.num < 0 || scale <= 0)
            return false;

        long scaledNumerator = (long)value.num * scale;
        if (scaledNumerator % value.den != 0)
            return false;

        long encodedValue = scaledNumerator / value.den;
        return encodedValue >= 0 && (ulong)encodedValue <= maxEncodedValue;
    }
}
