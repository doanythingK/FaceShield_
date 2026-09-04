using System;
using System.Diagnostics;

namespace FaceShield.Services.Video;

internal readonly record struct VideoExportAttemptConfiguration(
    string ExportMode,
    bool ForceSoftwareEncoder,
    bool AllowHybridCopy,
    bool ForceSafeEncoding,
    bool ForceAudioTranscode,
    bool ForceH264Fallback);

internal static class VideoExportAttemptCoordinator
{
    internal static int Execute(
        bool allowHybridCopy,
        Func<bool> isStaticHdrConfigured,
        Action<VideoExportAttemptConfiguration> executeAttempt)
    {
        if (isStaticHdrConfigured == null)
            throw new ArgumentNullException(nameof(isStaticHdrConfigured));
        if (executeAttempt == null)
            throw new ArgumentNullException(nameof(executeAttempt));

        int attemptCount = 0;
        try
        {
            attemptCount++;
            executeAttempt(new VideoExportAttemptConfiguration(
                ExportMode: "primary",
                ForceSoftwareEncoder: false,
                AllowHybridCopy: allowHybridCopy && VideoHybridCopyPolicy.EnableHybridCopyWindow,
                ForceSafeEncoding: false,
                ForceAudioTranscode: false,
                ForceH264Fallback: false));
        }
        catch (InvalidOperationException ex) when (
            VideoExportRetryPolicy.ShouldRetryWithSafeEncoding(ex))
        {
            Debug.WriteLine($"[Export] mode=fallback-safe로 재시도: {ex.Message}");
            try
            {
                attemptCount++;
                executeAttempt(new VideoExportAttemptConfiguration(
                    ExportMode: "fallback-safe",
                    ForceSoftwareEncoder: true,
                    AllowHybridCopy: false,
                    ForceSafeEncoding: true,
                    ForceAudioTranscode: false,
                    ForceH264Fallback: false));
            }
            catch (InvalidOperationException nestedEx) when (
                VideoExportRetryPolicy.ShouldRetryWithH264Fallback(
                    nestedEx,
                    isStaticHdrConfigured()))
            {
                Debug.WriteLine(
                    $"[Export] mode=fallback-h264로 재시도: 안전 모드에서도 실패. {nestedEx.Message}");
                attemptCount++;
                executeAttempt(new VideoExportAttemptConfiguration(
                    ExportMode: "fallback-h264",
                    ForceSoftwareEncoder: true,
                    AllowHybridCopy: false,
                    ForceSafeEncoding: true,
                    ForceAudioTranscode: false,
                    ForceH264Fallback: true));
            }
        }

        return attemptCount;
    }
}
