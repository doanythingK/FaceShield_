using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace FaceShield.Services.Video;

/// <summary>
/// Tracks one explicitly selected manual target, never a global union or Auto
/// detection. Reports the segment that actually remains in the workspace when
/// a shorter or failed retry is discarded.
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

        int endExclusive = workspace.NextBoundaryExclusive(targetId, sourceFrame, totalFrames);
        if (!workspace.TryResolveTargetMask(targetId, sourceFrame,
                out ManualOverlayStoredKeyframe source) || source.FrameIndex != sourceFrame)
            throw new InvalidOperationException("The selected target has no explicit source mask.");
        if (Array.TrueForAll(source.Alpha, static value => value == 0))
            throw new InvalidOperationException("Cannot track an explicitly absent manual face.");

        string fingerprint = workspace.GetSourceFingerprint(targetId, sourceFrame);
        using WriteableBitmap sourceBitmap = CreateSourceBitmap(source);
        ManualMaskTrackResult attempted = ManualMaskTrackingService.TrackForward(
            videoPath, sourceFrame, endExclusive, sourceBitmap, progress, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        attempted.Segment.SourceMaskFingerprint = fingerprint;
        if (attempted.ProcessedFrames <= 0 &&
            !(attempted.Segment.StoppedByFailure && attempted.Segment.Components.Count > 0))
            return attempted;

        // The workspace rechecks the individual target's source fingerprint
        // and correction boundary after decoding. It may retain a longer,
        // already persisted run rather than accepting a shorter retry.
        bool retainedPrevious = workspace.SetTrackSegment(targetId, attempted.Segment);
        cancellationToken.ThrowIfCancellationRequested();
        workspace.Save();
        if (!retainedPrevious)
            return attempted;

        if (!workspace.TryGetTrackSegment(targetId, sourceFrame,
                out ManualMaskTrackSegment effective) ||
            !string.Equals(effective.SourceMaskFingerprint, fingerprint, StringComparison.Ordinal))
            throw new InvalidDataException("Retained target tracking state changed unexpectedly.");

        // Do not show the rejected attempt's shorter frame count or failure
        // as though it were the currently saved interval.
        return attempted with
        {
            Segment = effective,
            ProcessedFrames = Math.Max(0, effective.EndExclusive - sourceFrame - 1)
        };
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
