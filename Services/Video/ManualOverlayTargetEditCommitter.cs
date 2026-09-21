using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FaceShield.Services.Video;

/// <summary>
/// Publishes an explicit correction only after the complete target document
/// has been atomically replaced. A failed write must not invalidate the live
/// verified track or make an unsaved keyframe appear persisted in the preview.
/// The caller must serialize edits/tracking for this workspace while committing.
/// </summary>
internal static class ManualOverlayTargetEditCommitter
{
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

        IReadOnlyList<ManualOverlayStoredTarget> snapshot = workspace.Snapshot();
        if (!snapshot.Any(target => target.Id == targetId))
            throw new InvalidDataException("Cannot correct an unknown manual face.");

        // The staged document is derived from the live snapshot, not a fresh
        // disk load: it cannot inadvertently discard another face's in-memory
        // corrections. Clone each affected segment before truncating its tail.
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

        // SaveForVideo validates all keyframe dimensions, fingerprints, sample
        // continuity and same-face boundaries before replacing the document.
        // If it throws, the original workspace and dirty editor are untouched.
        ManualOverlayWorkspaceStore.SaveForVideo(videoPath, staged);
        workspace.SetExplicitKeyframe(targetId, correction);
    }
}
