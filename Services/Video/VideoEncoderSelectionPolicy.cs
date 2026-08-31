using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace FaceShield.Services.Video;

internal static unsafe class VideoEncoderSelectionPolicy
{
    internal static bool IsHardwareEncoder(AVCodec* encoder)
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

    internal static bool UsesSoftwareConstantQuality(string encoderName)
    {
        return encoderName.Contains("x264", StringComparison.OrdinalIgnoreCase) ||
               encoderName.Contains("x265", StringComparison.OrdinalIgnoreCase) ||
               encoderName.Contains("svtav1", StringComparison.OrdinalIgnoreCase) ||
               encoderName.Contains("aom-av1", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool RequiresSoftwareEncoderForChromaLocation(
        AVChromaLocation chromaLocation)
    {
        return chromaLocation is not
            AVChromaLocation.AVCHROMA_LOC_UNSPECIFIED and not
            AVChromaLocation.AVCHROMA_LOC_LEFT;
    }

    internal static IReadOnlyList<string> GetCandidateNames(
        AVCodecID codecId,
        bool forceSoftwareOnly,
        bool allowTenBitHevcFallback)
    {
        IReadOnlyList<string> primary = GetPreferredEncoderNames(codecId, forceSoftwareOnly);
        if (!allowTenBitHevcFallback || codecId != AVCodecID.AV_CODEC_ID_H264)
            return primary;

        IReadOnlyList<string> hevcFallback = GetPreferredEncoderNames(
            AVCodecID.AV_CODEC_ID_HEVC,
            forceSoftwareOnly);
        var candidates = new List<string>(primary.Count + hevcFallback.Count);

        foreach (string name in primary)
        {
            if (!name.Contains("x264", StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("x265", StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(name);
            }
        }

        foreach (string name in hevcFallback)
        {
            if (!name.Contains("x265", StringComparison.OrdinalIgnoreCase))
                candidates.Add(name);
        }

        foreach (string name in primary)
        {
            if (name.Contains("x264", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("x265", StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(name);
            }
        }

        foreach (string name in hevcFallback)
        {
            if (name.Contains("x265", StringComparison.OrdinalIgnoreCase))
                candidates.Add(name);
        }

        return candidates;
    }

    internal static AVCodec* SelectFallbackEncoder(
        AVCodecID codecId,
        bool forceSoftwareOnly)
    {
        if (forceSoftwareOnly)
        {
            if (codecId == AVCodecID.AV_CODEC_ID_H264)
                return ffmpeg.avcodec_find_encoder_by_name("libx264");
            if (codecId == AVCodecID.AV_CODEC_ID_HEVC)
                return ffmpeg.avcodec_find_encoder_by_name("libx265");
            if (codecId == AVCodecID.AV_CODEC_ID_AV1)
            {
                AVCodec* svtAv1 = ffmpeg.avcodec_find_encoder_by_name("libsvtav1");
                return svtAv1 != null
                    ? svtAv1
                    : ffmpeg.avcodec_find_encoder_by_name("libaom-av1");
            }
        }

        return ffmpeg.avcodec_find_encoder(codecId);
    }

    internal static string AppendError(
        string? existing,
        string encoderName,
        string? detail)
    {
        string part = string.IsNullOrWhiteSpace(detail)
            ? $"{encoderName}: 초기화 실패"
            : $"{encoderName}: {detail}";

        return string.IsNullOrWhiteSpace(existing)
            ? part
            : $"{existing} | {part}";
    }

    private static IReadOnlyList<string> GetPreferredEncoderNames(
        AVCodecID codecId,
        bool forceSoftwareOnly)
    {
        if (forceSoftwareOnly)
        {
            if (codecId == AVCodecID.AV_CODEC_ID_H264)
                return new[] { "libx264" };
            if (codecId == AVCodecID.AV_CODEC_ID_HEVC)
                return new[] { "libx265" };
            if (codecId == AVCodecID.AV_CODEC_ID_AV1)
                return new[] { "libsvtav1", "libaom-av1" };
            return new[] { "libx264", "libx265" };
        }

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

        if (codecId == AVCodecID.AV_CODEC_ID_AV1)
        {
            if (isMac)
                return new[] { "libsvtav1", "libaom-av1" };
            if (isWindows)
            {
                return new[]
                {
                    "av1_nvenc",
                    "av1_qsv",
                    "av1_amf",
                    "libsvtav1",
                    "libaom-av1"
                };
            }

            return new[] { "av1_nvenc", "av1_qsv", "libsvtav1", "libaom-av1" };
        }

        return Array.Empty<string>();
    }
}
