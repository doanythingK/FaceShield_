using System;

namespace FaceShield.Services.Video;

internal static class VideoExportProgressPolicy
{
    private const int ExportSampleWindowSeconds = 30;
    private const int ProgressFrameInterval = 15;

    internal static void ReportVideoProgress(
        IProgress<ExportProgress>? progress,
        int totalFrames,
        ref int lastReportedFrame,
        int currentFrame,
        string? status = null)
    {
        if (progress == null || totalFrames <= 0)
            return;

        int bounded = Math.Clamp(currentFrame, 0, totalFrames);
        if (bounded - lastReportedFrame >= ProgressFrameInterval ||
            bounded >= totalFrames ||
            status != null)
        {
            progress.Report(new ExportProgress(bounded, totalFrames, status));
            lastReportedFrame = bounded;
        }
    }

    internal static int ResolveExportSampleWindowFrames(
        double sourceFps,
        int totalFrames)
    {
        double windowFrames = sourceFps > 0.0
            ? sourceFps * ExportSampleWindowSeconds
            : 30d * ExportSampleWindowSeconds;
        if (double.IsNaN(windowFrames) || double.IsInfinity(windowFrames))
            windowFrames = 30d * ExportSampleWindowSeconds;

        int resolved = (int)Math.Round(windowFrames);
        if (resolved <= 0)
            resolved = 30 * ExportSampleWindowSeconds;

        if (totalFrames > 0)
            resolved = Math.Min(resolved, totalFrames);

        return Math.Max(1, resolved);
    }
}
