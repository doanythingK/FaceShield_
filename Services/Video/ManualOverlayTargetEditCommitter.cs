using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FaceShield.Services.Video;

/// <summary>
/// Publishes an explicit correction only after the complete target document
/// has been atomically replaced. A failed write must not invalidate the live
/// verified track or make an unsaved keyframe appear persisted in the preview.
/// Committers for this workspace lock the same workspace instance; the store
/// separately checks for another committed writer before replacing the file.
/// </summary>
internal static class ManualOverlayTargetEditCommitter
{
    // Undo the complete selected face transaction, including its truncated
    // tracking segments. Repainting old pixels alone cannot restore motion.
    internal static void Restore(
        string videoPath, ManualOverlayTargetWorkspace workspace, Guid targetId,
        ManualOverlayStoredTarget before, ManualOverlayStoredTarget expectedAfter)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(expectedAfter);
        if (before.Id != targetId || expectedAfter.Id != targetId)
            throw new InvalidDataException("Undo snapshot belongs to another manual face.");

        lock (workspace)
        {
            IReadOnlyList<ManualOverlayStoredTarget> current = workspace.Snapshot();
            ManualOverlayStoredTarget live = current.Single(target => target.Id == targetId);
            if (!ManualOverlayWorkspaceStore.SameTargetState(live, expectedAfter))
                throw new IOException(
                    "Manual face changed since this Undo snapshot. Undo the newer edit first.");

            ManualOverlayStoredTarget[] restored = current
                .Select(target => target.Id == targetId ? before : target).ToArray();
            ManualOverlayWorkspaceStore.CommitIfUnchangedForVideo(videoPath, current, restored);
            workspace.RestorePersistedTarget(before, restored);
        }
    }

    internal static void Commit(
        string videoPath,
        ManualOverlayTargetWorkspace workspace,
        Guid targetId,
        ManualOverlayStoredKeyframe correction)
    {
        if (string.IsNullOrWhiteSpace(videoPath))
            throw new ArgumentException("A manual target video path is required.", nameof(videoPath));
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(correction);

        // Tracking and correction both use this instance as their commit gate.
        // Keep snapshot, disk replacement and live publication together.
        lock (workspace)
        {
            IReadOnlyList<ManualOverlayStoredTarget> snapshot = workspace.Snapshot();
            if (!snapshot.Any(target => target.Id == targetId))
                throw new InvalidDataException("Cannot correct an unknown manual face.");

            // Clone each affected segment before truncating its tail. All-zero
            // alpha is a deliberate same-target absence boundary, not deletion.
            ManualOverlayStoredTarget[] staged = snapshot.Select(target =>
            {
                if (target.Id != targetId)
                    return target;

                ManualOverlayStoredKeyframe[] keyframes = target.Keyframes
                    .Where(keyframe => keyframe.FrameIndex != correction.FrameIndex)
                    .Append(correction)
                    .OrderBy(keyframe => keyframe.FrameIndex)
                    .ToArray();
                var tracks = new List<ManualMaskTrackSegment>();
                foreach (ManualMaskTrackSegment original in
                         target.Segments ?? Array.Empty<ManualMaskTrackSegment>())
                {
                    if (original.SourceKeyframe == correction.FrameIndex)
                        continue;
                    ManualMaskTrackSegment segment = original.Clone();
                    if (segment.SourceKeyframe < correction.FrameIndex &&
                        segment.EndExclusive > correction.FrameIndex)
                    {
                        segment.EndExclusive = correction.FrameIndex;
                        segment.StoppedByFailure = false;
                        segment.StopFrame = null;
                        segment.StopReason = null;
                        foreach (ManualMaskTrackComponent component in segment.Components)
                            component.Samples.RemoveAll(sample => sample.FrameIndex >= correction.FrameIndex);
                    }
                    tracks.Add(segment);
                }
                return new ManualOverlayStoredTarget(target.Id, keyframes, tracks);
            }).ToArray();

            // Compare against the on-disk snapshot under a per-file lock.
            // A conflicting writer now fails closed without clearing dirty
            // pixels or changing this workspace's existing verified segments.
            ManualOverlayWorkspaceStore.CommitIfUnchangedForVideo(
                videoPath, snapshot, staged);
            workspace.SetExplicitKeyframe(targetId, correction);
            workspace.RecordPersistedSnapshot(staged);
        }
    }
}
