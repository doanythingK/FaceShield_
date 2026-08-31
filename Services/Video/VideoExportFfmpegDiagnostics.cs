using FFmpeg.AutoGen;
using System;
using System.Runtime.InteropServices;

namespace FaceShield.Services.Video;

internal static unsafe class VideoExportFfmpegDiagnostics
{
    internal static string GetEncoderName(AVCodec* encoder)
    {
        if (encoder == null || encoder->name == null)
            return "unknown";

        return Marshal.PtrToStringAnsi((IntPtr)encoder->name) ?? "unknown";
    }

    internal static string GetCodecName(AVCodecID codecId)
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

        return string.IsNullOrWhiteSpace(name)
            ? codecId.ToString()
            : name;
    }

    internal static string GetMediaTypeName(AVMediaType mediaType)
    {
        return mediaType switch
        {
            AVMediaType.AVMEDIA_TYPE_VIDEO => "추가 영상",
            AVMediaType.AVMEDIA_TYPE_SUBTITLE => "자막",
            AVMediaType.AVMEDIA_TYPE_DATA => "데이터",
            AVMediaType.AVMEDIA_TYPE_ATTACHMENT => "첨부",
            _ => "보조"
        };
    }

    internal static string GetOutputFormatName(AVFormatContext* outFmt)
    {
        if (outFmt == null ||
            outFmt->oformat == null ||
            outFmt->oformat->name == null)
        {
            return "unknown";
        }

        return Marshal.PtrToStringAnsi((IntPtr)outFmt->oformat->name) ?? "unknown";
    }

    internal static string GetErrorMessage(int errorCode)
    {
        byte* buffer = stackalloc byte[1024];
        ffmpeg.av_strerror(errorCode, buffer, 1024);
        return System.Text.Encoding.UTF8
            .GetString(new ReadOnlySpan<byte>(buffer, 1024))
            .TrimEnd('\0');
    }

    internal static void ThrowVideoEncoderError(
        int errorCode,
        AVCodecContext* context,
        string operation)
    {
        if (errorCode >= 0)
            return;

        AVCodec* encoder = context == null ? null : context->codec;
        string encoderName = GetEncoderName(encoder);
        bool isHardwareEncoder =
            VideoEncoderSelectionPolicy.IsHardwareEncoder(encoder);
        string detail = GetErrorMessage(errorCode);

        throw new VideoEncoderException(
            $"비디오 인코더({encoderName}) {operation} 실패: {detail}",
            errorCode,
            operation,
            encoderName,
            isHardwareEncoder);
    }

    internal static void Throw(int errorCode)
    {
        if (errorCode >= 0)
            return;

        throw new InvalidOperationException(GetErrorMessage(errorCode));
    }
}
