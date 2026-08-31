using System;

namespace FaceShield.Services.Video;

internal sealed class VideoEncoderException : InvalidOperationException
{
    internal VideoEncoderException(
        string message,
        int errorCode,
        string operation,
        string encoderName,
        bool isHardwareEncoder)
        : base(message)
    {
        ErrorCode = errorCode;
        Operation = operation;
        EncoderName = encoderName;
        IsHardwareEncoder = isHardwareEncoder;
    }

    internal int ErrorCode { get; }
    internal string Operation { get; }
    internal string EncoderName { get; }
    internal bool IsHardwareEncoder { get; }
}

internal static class VideoExportRetryPolicy
{
    internal static bool ShouldRetryWithSafeEncoding(InvalidOperationException exception)
    {
        if (exception is VideoExportIntegrityException)
            return false;

        return exception is VideoEncoderException { IsHardwareEncoder: true } ||
               IsInvalidArgumentError(exception);
    }

    internal static bool ShouldRetryWithH264Fallback(
        InvalidOperationException exception,
        bool staticHdrConfigured)
    {
        return !staticHdrConfigured &&
               ShouldRetryWithSafeEncoding(exception) &&
               IsInvalidArgumentError(exception);
    }

    private static bool IsInvalidArgumentError(Exception exception)
    {
        if (exception is not InvalidOperationException)
            return false;

        return exception.Message.Contains(
            "Invalid argument",
            StringComparison.OrdinalIgnoreCase);
    }
}
