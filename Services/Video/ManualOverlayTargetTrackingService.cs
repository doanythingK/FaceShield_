using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace FaceShield.Services.Video;

/// <summary>
/// Target-aware entry point to the existing video motion tracker. The tracked
/// source is one target's explicitly confirmed mask, never a global union or
/// an unrelated Auto detection. The target workspace owns the saved samples.
/// This service is not yet invoked by the existing GUI tracking command.
/// </summary>
internal static class ManualOverlayTargetTrackingService
{
    internal static ManualMaskTrackResult TrackForward(
        string videoPath,
        ManualOverlayTargetWorkspace workspace,
        Guid targetId,
        int sourceFrame,
        int totalFrames,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (string.IsNullOrWhiteSpace(videoPath))
            throw new ArgumentException("A tracking video path is required.", nameof(videoPath));
        cancellationToken.ThrowIfCancellationRequested();

        // NextBoundaryExclusive refuses an inherited result or a keyframe from
        // a different target; the same target's next correction is the only
        // manual upper bound. Zero/unknown totalFrames means decoder EOF.
        int endExclusive = workspace.NextBoundaryExclusive(targetId, sourceFrame, totalFrames);
        if (!workspace.TryResolveTargetMask(targetId, sourceFrame,
                out ManualOverlayStoredKeyframe source) || source.FrameIndex != sourceFrame)
            throw new InvalidOperationException("The selected target has no explicit source mask.");

        string fingerprint = workspace.GetSourceFingerprint(targetId, sourceFrame);
        using WriteableBitmap sourceBitmap = CreateSourceBitmap(source);
        ManualMaskTrackResult result = ManualMaskTrackingService.TrackForward(
            videoPath, sourceFrame, endExclusive, sourceBitmap, progress, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        // A target fingerprint identifies the original alpha and dimensions;
        // it must not reuse the global bitmap/Auto-composite fingerprint.
        result.Segment.SourceMaskFingerprint = fingerprint;
        if (result.ProcessedFrames > 0 ||
            (result.Segment.StoppedByFailure && result.Segment.Components.Count > 0))
        {
            // The workspace rejects corrections inserted while decoding by
            // checking the exact source fingerprint and same-target boundary.
            workspace.SetTrackSegment(targetId, result.Segment);
            cancellationToken.ThrowIfCancellationRequested();
            workspace.Save();
        }
        return result;
    }

    private static WriteableBitmap CreateSourceBitmap(ManualOverlayStoredKeyframe source)
    {
        int rowBytes = checked(source.Width * 4);
        ManualOverlayMask alpha = source.ToBgra(rowBytes);
        var bitmap = new WriteableBitmap(new PixelSize(source.Width, source.Height),
            new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        try
        {
            using var frame = bitmap.Lock();
            for (int y = 0; y < source.Height; y++)
                Marshal.Copy(alpha.Pixels, checked(y * rowBytes),
                    IntPtr.Add(frame.Address, checked(y * frame.RowBytes)), rowBytes);
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }
}
