using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace FaceShield.Services.Video;

/// <summary>
/// Target-owned explicit masks, correction boundaries and validated motion samples.
/// This does not take ownership of FrameMaskProvider's legacy/global raster.
/// All returned arrays and segments are copies; missing motion samples never
/// turn into a stationary mask. The caller persists edits with Save().
/// </summary>
internal sealed class ManualOverlayTargetWorkspace
{
    private sealed class TargetState
    {
        internal TargetState(Guid id) => Target = new ManualOverlayTarget(id);
        internal ManualOverlayTarget Target { get; }
        // SortedList exposes random-access Keys for a logarithmic floor lookup.
        internal SortedList<int, ManualOverlayStoredKeyframe> Keyframes { get; } = new();
        internal Dictionary<int, string> SourceFingerprints { get; } = new();
        internal Dictionary<int, ManualMaskTrackSegment> Tracks { get; } = new();
    }

    private readonly object _gate = new();
    private readonly string _videoPath;
    private readonly Dictionary<Guid, TargetState> _targets = new();
    private Guid? _selectedTargetId;
    // The last document confirmed on disk, not the current mutable editor.
    // Save() uses this baseline so a stale workspace cannot replace a newer
    // correction created by another workspace or a cooperating process.
    private IReadOnlyList<ManualOverlayStoredTarget> _persistedSnapshot =
        Array.Empty<ManualOverlayStoredTarget>();

    private ManualOverlayTargetWorkspace(string videoPath)
    {
        _videoPath = videoPath;
    }

    internal static ManualOverlayTargetWorkspace Open(string videoPath)
    {
        if (string.IsNullOrWhiteSpace(videoPath))
            throw new ArgumentException("Video path is required.", nameof(videoPath));

        // Existing corrupt or source-mismatched state must not be treated as empty.
        IReadOnlyList<ManualOverlayStoredTarget> stored =
            ManualOverlayWorkspaceStore.LoadForVideo(videoPath);
        var workspace = new ManualOverlayTargetWorkspace(videoPath);
        foreach (ManualOverlayStoredTarget item in stored)
        {
            var state = new TargetState(item.Id);
            foreach (ManualOverlayStoredKeyframe keyframe in item.Keyframes)
            {
                state.Target.SetExplicitKeyframe(keyframe.FrameIndex);
                state.Keyframes.Add(keyframe.FrameIndex, Copy(keyframe));
                state.SourceFingerprints.Add(keyframe.FrameIndex,
                    ManualOverlayTrackValidation.Fingerprint(keyframe));
            }
            foreach (ManualMaskTrackSegment segment in item.Segments ?? Array.Empty<ManualMaskTrackSegment>())
                state.Tracks.Add(segment.SourceKeyframe, segment.Clone());
            workspace._targets.Add(item.Id, state);
        }
        workspace._selectedTargetId = stored.FirstOrDefault()?.Id;
        workspace._persistedSnapshot = workspace.Snapshot();
        return workspace;
    }

    internal Guid? SelectedTargetId
    {
        get { lock (_gate) return _selectedTargetId; }
    }

    internal Guid[] GetTargetIds()
    {
        lock (_gate) return _targets.Keys.OrderBy(static id => id).ToArray();
    }

    internal Guid CreateTarget()
    {
        lock (_gate)
        {
            Guid id = Guid.NewGuid();
            _targets.Add(id, new TargetState(id));
            _selectedTargetId = id;
            return id;
        }
    }

    internal void SelectTarget(Guid targetId)
    {
        lock (_gate)
        {
            RequireTarget(targetId);
            _selectedTargetId = targetId;
        }
    }

    internal void SetExplicitKeyframe(Guid targetId, ManualOverlayStoredKeyframe keyframe)
    {
        ArgumentNullException.ThrowIfNull(keyframe);
        if (keyframe.FrameIndex < 0 || keyframe.Alpha == null ||
            keyframe.Alpha.Length != ManualOverlayStateStore.CheckedPixelCount(keyframe.Width, keyframe.Height))
            throw new InvalidDataException("Invalid manual keyframe.");

        lock (_gate)
        {
            TargetState state = RequireTarget(targetId);
            // A correction to A cannot invalidate anything belonging to B.
            // Preserve the verified prefix of A's preceding interval.
            foreach (ManualMaskTrackSegment previous in state.Tracks.Values)
            {
                if (previous.SourceKeyframe >= keyframe.FrameIndex ||
                    previous.EndExclusive <= keyframe.FrameIndex)
                    continue;
                previous.EndExclusive = keyframe.FrameIndex;
                previous.StoppedByFailure = false;
                previous.StopFrame = null;
                previous.StopReason = null;
                foreach (ManualMaskTrackComponent component in previous.Components)
                    component.Samples.RemoveAll(sample => sample.FrameIndex >= keyframe.FrameIndex);
            }
            state.Tracks.Remove(keyframe.FrameIndex);
            state.Target.SetExplicitKeyframe(keyframe.FrameIndex);
            state.Keyframes[keyframe.FrameIndex] = Copy(keyframe);
            state.SourceFingerprints[keyframe.FrameIndex] =
                ManualOverlayTrackValidation.Fingerprint(keyframe);
        }
    }

    internal bool RemoveExplicitKeyframe(Guid targetId, int frameIndex)
    {
        lock (_gate)
        {
            TargetState state = RequireTarget(targetId);
            if (!state.Target.RemoveExplicitKeyframe(frameIndex)) return false;
            state.Keyframes.Remove(frameIndex);
            state.SourceFingerprints.Remove(frameIndex);
            state.Tracks.Remove(frameIndex);
            // Do not extend the preceding track into the newly vacant interval:
            // those frames have not been verified against this change.
            return true;
        }
    }

    internal int NextBoundaryExclusive(Guid targetId, int sourceFrame, int totalFrames)
    {
        lock (_gate)
            return RequireTarget(targetId).Target.NextBoundaryExclusive(sourceFrame, totalFrames);
    }

    internal string GetSourceFingerprint(Guid targetId, int sourceFrame)
    {
        lock (_gate)
        {
            TargetState state = RequireTarget(targetId);
            if (!state.SourceFingerprints.TryGetValue(sourceFrame, out string? fingerprint))
                throw new InvalidOperationException("Tracking requires this target's explicit source keyframe.");
            return fingerprint;
        }
    }

    /// <summary>
    /// Commit only samples for the requested target. A shorter retry with the
    /// same source fingerprint cannot discard a longer current verified run.
    /// </summary>
    internal bool SetTrackSegment(Guid targetId, ManualMaskTrackSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        lock (_gate)
        {
            TargetState state = RequireTarget(targetId);
            if (!state.Keyframes.TryGetValue(segment.SourceKeyframe, out ManualOverlayStoredKeyframe? source))
                throw new InvalidDataException("The target has no matching explicit source keyframe.");
            ManualOverlayTrackValidation.Validate(segment, source);
            if (segment.EndExclusive > state.Target.NextBoundaryExclusive(segment.SourceKeyframe, 0))
                throw new InvalidDataException("A segment crosses its own target's correction boundary.");

            if (state.Tracks.TryGetValue(segment.SourceKeyframe, out ManualMaskTrackSegment? existing) &&
                string.Equals(existing.SourceMaskFingerprint, segment.SourceMaskFingerprint,
                    StringComparison.Ordinal) &&
                (existing.EndExclusive > segment.EndExclusive ||
                 (existing.EndExclusive == segment.EndExclusive &&
                  !existing.StoppedByFailure && segment.StoppedByFailure)))
                return true;

            state.Tracks[segment.SourceKeyframe] = segment.Clone();
            return false;
        }
    }

    internal bool TryGetTrackSegment(Guid targetId, int sourceFrame, out ManualMaskTrackSegment segment)
    {
        lock (_gate)
        {
            TargetState state = RequireTarget(targetId);
            if (state.Tracks.TryGetValue(sourceFrame, out ManualMaskTrackSegment? stored))
            {
                segment = stored.Clone();
                return true;
            }
            segment = null!;
            return false;
        }
    }

    internal bool TryResolveTargetMask(Guid targetId, int frameIndex,
        out ManualOverlayStoredKeyframe mask)
    {
        lock (_gate)
            return TryResolveLocked(RequireTarget(targetId), frameIndex, out mask);
    }

    /// <summary>
    /// One resolver for preview and export consumers: current-frame Auto plus
    /// each target's exact correction or fully sampled, fingerprint-valid track.
    /// Export enables requireCompleteTargets so missing motion fails closed.
    /// Transform and union each target only once, without per-target BGRA buffers.
    /// Callers own the returned pixel array. No previous-frame mask is held.
    /// </summary>
    internal bool TryComposeFrame(int frameIndex, int width, int height,
        byte[]? automaticMask, int automaticRowBytes, out ManualOverlayMask composite,
        bool requireCompleteTargets = false, CancellationToken cancellationToken = default)
    {
        int pixelCount = ManualOverlayStateStore.CheckedPixelCount(width, height);
        int rowBytes = checked(width * 4);
        if (automaticMask != null && (automaticRowBytes < rowBytes ||
            (long)automaticRowBytes * height > automaticMask.Length))
            throw new ArgumentException("Invalid automatic mask dimensions or row stride.", nameof(automaticMask));

        byte[]? output = automaticMask == null ? null : new byte[checked(pixelCount * 4)];
        if (automaticMask != null)
        {
            for (int y = 0; y < height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int x = 0; x < width; x++)
                {
                    byte alpha = automaticMask[y * automaticRowBytes + x * 4 + 3];
                    int offset = y * rowBytes + x * 4;
                    output![offset] = output[offset + 1] = output[offset + 2] = output[offset + 3] = alpha;
                }
            }
        }

        bool hasManual = false;
        lock (_gate)
        {
            foreach (TargetState target in _targets.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int sourceIndex = FindFloorIndex(target.Keyframes.Keys, frameIndex);
                if (sourceIndex < 0)
                    continue;
                ManualOverlayStoredKeyframe source = target.Keyframes.Values[sourceIndex];
                // Empty correction explicitly terminates only this face until
                // its next keyframe. Do not require samples for its absence.
                if (Array.TrueForAll(source.Alpha, static alpha => alpha == 0))
                {
                    if (frameIndex == source.FrameIndex)
                        hasManual = true;
                    continue;
                }
                if (!TryResolveLocked(target, frameIndex,
                        out ManualOverlayStoredKeyframe mask, cancellationToken))
                {
                    if (requireCompleteTargets)
                        throw new InvalidDataException(
                            $"수동 얼굴 {target.Target.Id}의 {frameIndex} 프레임에 검증된 추적 샘플이 없습니다. " +
                            "같은 얼굴을 보정·재추적하거나 해당 프레임에서 영역을 모두 지워 종료를 지정하세요.");
                    continue;
                }
                if (mask.Width != width || mask.Height != height)
                    throw new InvalidDataException("Manual target mask dimensions differ from video frame.");
                hasManual = true;
                output ??= new byte[checked(pixelCount * 4)];
                for (int y = 0; y < height; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int alphaRow = y * width;
                    int outputRow = y * rowBytes;
                    for (int x = 0; x < width; x++)
                    {
                        byte alpha = mask.Alpha[alphaRow + x];
                        int offset = outputRow + x * 4;
                        if (alpha <= output[offset + 3])
                            continue;
                        output[offset] = output[offset + 1] = output[offset + 2] = output[offset + 3] = alpha;
                    }
                }
            }
        }
        if (automaticMask == null && !hasManual)
        {
            composite = default;
            return false;
        }
        composite = new ManualOverlayMask(output ?? new byte[checked(pixelCount * 4)], rowBytes);
        return true;
    }

    internal IReadOnlyList<ManualOverlayStoredTarget> Snapshot()
    {
        lock (_gate)
            return _targets.Values.OrderBy(static state => state.Target.Id)
                .Select(state => new ManualOverlayStoredTarget(state.Target.Id,
                    state.Keyframes.Values.Select(Copy).ToArray(),
                    state.Tracks.Values.OrderBy(static segment => segment.SourceKeyframe)
                        .Select(static segment => segment.Clone()).ToArray()))
                .ToArray();
    }

    internal void Save()
    {
        // Share the same gate as target correction/tracking committers. A
        // direct Save is never an unconditional last-writer-wins replacement.
        lock (this)
        {
            IReadOnlyList<ManualOverlayStoredTarget> current = Snapshot();
            ManualOverlayWorkspaceStore.CommitIfUnchangedForVideo(
                _videoPath, _persistedSnapshot, current);
            _persistedSnapshot = current;
        }
    }

    // A disk-first committer calls this only after its live publication.
    // The baseline must follow successful commits or a later direct Save()
    // would reject the workspace's own previous correction as a conflict.
    internal void RecordPersistedSnapshot(IReadOnlyList<ManualOverlayStoredTarget> snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (this)
            _persistedSnapshot = snapshot;
    }

    private TargetState RequireTarget(Guid targetId)
        => _targets.TryGetValue(targetId, out TargetState? state)
            ? state : throw new KeyNotFoundException("Unknown manual target ID.");

    private static ManualOverlayStoredKeyframe Copy(ManualOverlayStoredKeyframe source)
        => new(source.FrameIndex, source.Width, source.Height, (byte[])source.Alpha.Clone());

    // SortedList<TKey,TValue>.Keys provides positional access without a scan.
    private static int FindFloorIndex(IList<int> keys, int frameIndex)
    {
        int low = 0, high = keys.Count - 1, floor = -1;
        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            if (keys[middle] <= frameIndex)
            {
                floor = middle;
                low = middle + 1;
            }
            else
                high = middle - 1;
        }
        return floor;
    }

    private static bool TryResolveLocked(TargetState target, int frameIndex,
        out ManualOverlayStoredKeyframe mask, CancellationToken cancellationToken = default)
    {
        mask = null!;
        if (frameIndex < 0) return false;
        int sourceIndex = FindFloorIndex(target.Keyframes.Keys, frameIndex);
        if (sourceIndex < 0) return false;
        ManualOverlayStoredKeyframe source = target.Keyframes.Values[sourceIndex];
        int sourceFrame = source.FrameIndex;
        if (frameIndex == sourceFrame)
        {
            mask = Copy(source);
            return true;
        }
        if (frameIndex >= target.Target.NextBoundaryExclusive(sourceFrame, 0) ||
            !target.Tracks.TryGetValue(sourceFrame, out ManualMaskTrackSegment? segment) ||
            frameIndex >= segment.EndExclusive ||
            !string.Equals(segment.SourceMaskFingerprint,
                target.SourceFingerprints[sourceFrame], StringComparison.Ordinal))
            return false;
        return TryTransform(source, segment, frameIndex, out mask, cancellationToken);
    }

    private static bool TryTransform(ManualOverlayStoredKeyframe source,
        ManualMaskTrackSegment segment, int frameIndex, out ManualOverlayStoredKeyframe mask,
        CancellationToken cancellationToken)
    {
        mask = null!;
        int width = source.Width, height = source.Height;
        if (segment.Components.Count == 0) return false;
        // Validate every component's ordinal before allocating a full-frame mask.
        int sampleIndex = frameIndex - source.FrameIndex - 1;
        foreach (ManualMaskTrackComponent component in segment.Components)
            if ((uint)sampleIndex >= (uint)component.Samples.Count ||
                component.Samples[sampleIndex].FrameIndex != frameIndex)
                return false;

        byte[] output = new byte[ManualOverlayStateStore.CheckedPixelCount(width, height)];
        foreach (ManualMaskTrackComponent component in segment.Components)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ManualMaskTrackSample sample = component.Samples[sampleIndex];
            double scale = sample.Scale;
            double cos = Math.Cos(sample.RotationRadians), sin = Math.Sin(sample.RotationRadians);
            double sx0 = component.SourceBoundsX, sy0 = component.SourceBoundsY;
            double w = component.SourceBoundsWidth, h = component.SourceBoundsHeight;
            double x0 = sx0 + sample.OffsetX, y0 = sy0 + sample.OffsetY;
            double x1 = x0 + scale * cos * w, y1 = y0 + scale * sin * w;
            double x2 = x0 - scale * sin * h, y2 = y0 + scale * cos * h;
            double x3 = x1 - scale * sin * h, y3 = y1 + scale * cos * h;
            int minX = Math.Clamp((int)Math.Floor(Math.Min(Math.Min(x0, x1), Math.Min(x2, x3))), 0, width);
            int minY = Math.Clamp((int)Math.Floor(Math.Min(Math.Min(y0, y1), Math.Min(y2, y3))), 0, height);
            int maxX = Math.Clamp((int)Math.Ceiling(Math.Max(Math.Max(x0, x1), Math.Max(x2, x3))), 0, width);
            int maxY = Math.Clamp((int)Math.Ceiling(Math.Max(Math.Max(y0, y1), Math.Max(y2, y3))), 0, height);
            int sxMin = Math.Clamp((int)Math.Floor(sx0), 0, width - 1);
            int syMin = Math.Clamp((int)Math.Floor(sy0), 0, height - 1);
            int sxMax = Math.Clamp((int)Math.Ceiling(sx0 + w) - 1, 0, width - 1);
            int syMax = Math.Clamp((int)Math.Ceiling(sy0 + h) - 1, 0, height - 1);
            for (int y = minY; y < maxY; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int x = minX; x < maxX; x++)
                {
                    double dx = (x - x0) / scale, dy = (y - y0) / scale;
                    double localX = cos * dx + sin * dy;
                    double localY = -sin * dx + cos * dy;
                    if (localX < 0 || localY < 0 || localX >= w || localY >= h) continue;
                    int sx = Math.Clamp((int)Math.Round(sx0 + localX), sxMin, sxMax);
                    int sy = Math.Clamp((int)Math.Round(sy0 + localY), syMin, syMax);
                    int position = y * width + x;
                    output[position] = Math.Max(output[position], source.Alpha[sy * width + sx]);
                }
            }
        }
        mask = new ManualOverlayStoredKeyframe(frameIndex, width, height, output);
        return true;
    }
}

/// <summary>
/// Strict validation for target-owned tracks; never confuse the global legacy
/// fingerprint of a composite bitmap with a single target's alpha fingerprint.
/// </summary>
internal static class ManualOverlayTrackValidation
{
    internal static string Fingerprint(ManualOverlayStoredKeyframe keyframe)
    {
        byte[] dimensions = System.Text.Encoding.ASCII.GetBytes($"{keyframe.Width}:{keyframe.Height}:");
        byte[] content = new byte[checked(dimensions.Length + keyframe.Alpha.Length)];
        dimensions.CopyTo(content, 0);
        keyframe.Alpha.CopyTo(content, dimensions.Length);
        return Convert.ToHexString(SHA256.HashData(content));
    }

    internal static void Validate(ManualMaskTrackSegment segment, ManualOverlayStoredKeyframe source)
    {
        if (segment == null || source == null || segment.SourceKeyframe != source.FrameIndex ||
            segment.EndExclusive <= source.FrameIndex || segment.Components == null ||
            segment.Components.Count == 0 ||
            !string.Equals(segment.SourceMaskFingerprint, Fingerprint(source), StringComparison.Ordinal))
            throw new InvalidDataException("Invalid target tracking source, fingerprint or interval.");
        if (segment.StoppedByFailure ? segment.StopFrame != segment.EndExclusive : segment.StopFrame.HasValue)
            throw new InvalidDataException("Invalid target tracking failure boundary.");
        var indices = new HashSet<int>();
        foreach (ManualMaskTrackComponent component in segment.Components)
        {
            if (component == null || component.ComponentIndex < 0 || !indices.Add(component.ComponentIndex) ||
                component.Samples == null || !double.IsFinite(component.SourceBoundsX) ||
                !double.IsFinite(component.SourceBoundsY) || !double.IsFinite(component.SourceBoundsWidth) ||
                !double.IsFinite(component.SourceBoundsHeight) || component.SourceBoundsX < 0 ||
                component.SourceBoundsY < 0 || component.SourceBoundsWidth <= 0 ||
                component.SourceBoundsHeight <= 0 ||
                component.SourceBoundsX + component.SourceBoundsWidth > source.Width ||
                component.SourceBoundsY + component.SourceBoundsHeight > source.Height)
                throw new InvalidDataException("Invalid target tracking component bounds.");
            int previous = source.FrameIndex;
            foreach (ManualMaskTrackSample sample in component.Samples)
            {
                if (sample == null || previous == int.MaxValue || sample.FrameIndex != previous + 1 ||
                    sample.FrameIndex >= segment.EndExclusive || !double.IsFinite(sample.OffsetX) ||
                    !double.IsFinite(sample.OffsetY) || !double.IsFinite(sample.Scale) ||
                    sample.Scale < 0.25 || sample.Scale > 4 ||
                    !double.IsFinite(sample.RotationRadians) || Math.Abs(sample.RotationRadians) > Math.PI ||
                    !double.IsFinite(sample.Confidence) || sample.Confidence < 0 || sample.Confidence > 1 ||
                    !double.IsFinite(component.SourceBoundsX + sample.OffsetX) ||
                    !double.IsFinite(component.SourceBoundsY + sample.OffsetY))
                    throw new InvalidDataException("Invalid or missing target tracking sample.");
                previous = sample.FrameIndex;
            }
            if (previous != segment.EndExclusive - 1)
                throw new InvalidDataException("Target tracking has a missing middle or tail sample.");
        }
    }
}
