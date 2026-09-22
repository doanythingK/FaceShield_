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
        CancellationToken cancellationToken,
        Action<int>? onConfirmedEofFrameCount = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (string.IsNullOrWhiteSpace(videoPath))
            throw new ArgumentException("A tracking video path is required.", nameof(videoPath));
        cancellationToken.ThrowIfCancellationRequested();

        // Callers may supply an estimated container frame count. There is no
        // trustworthy-count flag in this legacy signature, so never let this
        // display/progress hint truncate coverage. The next correction of this
        // SAME target is the only finite boundary; otherwise decode to EOF.
        int endExclusive = workspace.NextBoundaryExclusive(targetId, sourceFrame, totalFrames: 0);
        if (!workspace.TryResolveTargetMask(targetId, sourceFrame,
                out ManualOverlayStoredKeyframe source) || source.FrameIndex != sourceFrame)
            throw new InvalidOperationException("The selected target has no explicit source mask.");
        if (Array.TrueForAll(source.Alpha, static value => value == 0))
            throw new InvalidOperationException("Cannot track an explicitly absent manual face.");

        string fingerprint = workspace.GetSourceFingerprint(targetId, sourceFrame);
        using WriteableBitmap sourceBitmap = CreateSourceBitmap(source);
        // Decoding is deliberately outside the commit gate. A correction can
        // run while frames decode; its source/boundary is checked again below.
        ManualMaskTrackResult attempted = ManualMaskTrackingService.TrackForward(
            videoPath, sourceFrame, endExclusive, sourceBitmap, progress, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        attempted.Segment.SourceMaskFingerprint = fingerprint;

        // ReachedBoundary by itself also means a finite correction boundary.
        // Only an unbounded run that reached decoder EOF without error/failure
        // establishes an actual total frame count. Capture the attempted run,
        // not a potentially retained longer prior segment.
        if (endExclusive == int.MaxValue && attempted.ReachedBoundary &&
            !attempted.Segment.StoppedByFailure &&
            attempted.Segment.EndExclusive > sourceFrame &&
            attempted.Segment.EndExclusive != int.MaxValue)
            onConfirmedEofFrameCount?.Invoke(attempted.Segment.EndExclusive);

        // Correction and tracking use the same instance as a commit gate.
        // Hold it from the fresh snapshot through disk replacement and live
        // publication. The store additionally checks for independently saved
        // changes under a per-file lock before overwriting the document.
        lock (workspace)
        {
            var snapshot = workspace.Snapshot();
            ManualOverlayStoredTarget current = snapshot.SingleOrDefault(target => target.Id == targetId)
                ?? throw new InvalidDataException("The selected manual target disappeared during tracking.");
            ManualOverlayStoredKeyframe currentSource = current.Keyframes
                .SingleOrDefault(keyframe => keyframe.FrameIndex == sourceFrame)
                ?? throw new InvalidDataException("The tracking source correction disappeared.");
            if (!string.Equals(ManualOverlayTrackValidation.Fingerprint(currentSource),
                    fingerprint, StringComparison.Ordinal))
                throw new InvalidDataException("The manual target source changed during tracking.");
            if (attempted.Segment.EndExclusive >
                workspace.NextBoundaryExclusive(targetId, sourceFrame, totalFrames: 0))
                throw new InvalidDataException("Tracking crossed the selected face's correction boundary.");

            ManualMaskTrackSegment? prior = current.Segments?
                .SingleOrDefault(segment => segment.SourceKeyframe == sourceFrame);
            if (attempted.ProcessedFrames <= 0 &&
                !(attempted.Segment.StoppedByFailure && attempted.Segment.Components.Count > 0))
            {
                // No usable attempt can overwrite a verified segment. Recheck
                // the source inside the gate before reporting earlier coverage.
                if (prior != null && string.Equals(prior.SourceMaskFingerprint,
                        fingerprint, StringComparison.Ordinal))
                    return attempted with
                    {
                        Segment = prior,
                        ProcessedFrames = Math.Max(0, prior.EndExclusive - sourceFrame - 1)
                    };
                return attempted;
            }

            ManualOverlayTrackValidation.Validate(attempted.Segment, currentSource);
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
            // A conflicting independent writer or failed disk write leaves
            // this live workspace unchanged. Do not cancel after replacement.
            ManualOverlayWorkspaceStore.CommitIfUnchangedForVideo(
                videoPath, snapshot, staged);
            bool retainedOnLive = workspace.SetTrackSegment(targetId, attempted.Segment);
            if (retainedOnLive != retainPrior ||
                !workspace.TryGetTrackSegment(targetId, sourceFrame,
                    out ManualMaskTrackSegment saved) ||
                !string.Equals(saved.SourceMaskFingerprint, fingerprint, StringComparison.Ordinal) ||
                saved.EndExclusive != effective.EndExclusive ||
                saved.StoppedByFailure != effective.StoppedByFailure)
                throw new InvalidDataException("Persisted manual target track changed unexpectedly.");
            workspace.RecordPersistedSnapshot(staged);

            if (!retainedOnLive)
                return attempted;
            return attempted with
            {
                Segment = saved,
                ProcessedFrames = Math.Max(0, saved.EndExclusive - sourceFrame - 1)
            };
        }
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
