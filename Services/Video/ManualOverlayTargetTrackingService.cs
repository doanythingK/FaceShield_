using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace FaceShield.Services.Video;

/// <summary>
/// Tracks one explicitly selected manual target, never a global union or Auto
/// detection. Reports the segment that actually remains in the workspace when
/// a shorter, failed or zero-sample retry is discarded.
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
        {
            // A tracker that cannot produce a usable component/next-frame
            // sample has not replaced anything. Do not misreport its empty
            // attempt when a same-source verified interval remains in memory.
            if (workspace.TryGetTrackSegment(targetId, sourceFrame,
                    out ManualMaskTrackSegment previous) &&
                string.Equals(previous.SourceMaskFingerprint, fingerprint, StringComparison.Ordinal))
                return attempted with
                {
                    Segment = previous,
                    ProcessedFrames = Math.Max(0, previous.EndExclusive - sourceFrame - 1)
                };
            return attempted;
        }

        // Take an independent copy and validate against the current source.
        // Do not mutate the live workspace before the atomic file replacement:
        // a failed disk write must leave its previously verified track intact.
        var snapshot = workspace.Snapshot();
        ManualOverlayStoredTarget current = snapshot.SingleOrDefault(target => target.Id == targetId)
            ?? throw new InvalidDataException("The selected manual target disappeared during tracking.");
        ManualOverlayStoredKeyframe currentSource = current.Keyframes
            .SingleOrDefault(keyframe => keyframe.FrameIndex == sourceFrame)
            ?? throw new InvalidDataException("The tracking source correction disappeared.");
        if (!string.Equals(ManualOverlayTrackValidation.Fingerprint(currentSource),
                fingerprint, StringComparison.Ordinal))
            throw new InvalidDataException("The manual target source changed during tracking.");
        ManualOverlayTrackValidation.Validate(attempted.Segment, currentSource);
        if (attempted.Segment.EndExclusive >
            workspace.NextBoundaryExclusive(targetId, sourceFrame, totalFrames: 0))
            throw new InvalidDataException("Tracking crossed the selected face's correction boundary.");

        ManualMaskTrackSegment? prior = current.Segments?
            .SingleOrDefault(segment => segment.SourceKeyframe == sourceFrame);
        bool retainPrior = prior != null &&
            string.Equals(prior.SourceMaskFingerprint, fingerprint, StringComparison.Ordinal) &&
            (prior.EndExclusive > attempted.Segment.EndExclusive ||
             (prior.EndExclusive == attempted.Segment.EndExclusive &&
              !prior.StoppedByFailure && attempted.Segment.StoppedByFailure));
        ManualMaskTrackSegment effective = retainPrior ? prior! : attempted.Segment;
        var staged = snapshot.Select(target => target.Id != targetId
            ? target
            : new ManualOverlayStoredTarget(target.Id, target.Keyframes,
                (target.Segments ?? Array.Empty<ManualMaskTrackSegment>())
                    .Where(segment => segment.SourceKeyframe != sourceFrame)
                    .Append(effective.Clone())
                    .OrderBy(segment => segment.SourceKeyframe)
                    .ToArray())).ToArray();

        cancellationToken.ThrowIfCancellationRequested();
        ManualOverlayWorkspaceStore.SaveForVideo(videoPath, staged);

        // No cancellation point after successful persistence. UI edit/navigation
        // is gated while tracking runs, so publish the same effective segment
        // immediately. Independent concurrent writers still need an explicit
        // cross-process transaction, which is not provided by this workspace.
        bool retainedOnLive = workspace.SetTrackSegment(targetId, attempted.Segment);
        if (!workspace.TryGetTrackSegment(targetId, sourceFrame,
                out ManualMaskTrackSegment saved) ||
            !string.Equals(saved.SourceMaskFingerprint, fingerprint, StringComparison.Ordinal))
            throw new InvalidDataException("Persisted manual target track changed unexpectedly.");

        if (!retainedOnLive)
            return attempted;
        return attempted with
        {
            Segment = saved,
            ProcessedFrames = Math.Max(0, saved.EndExclusive - sourceFrame - 1)
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
