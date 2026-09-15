using Avalonia;
using Avalonia.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

namespace FaceShield.Services.Video;

internal interface IExportFrameMaskReadView
{
    bool TryGetBorrowedStoredMask(int frameIndex, out WriteableBitmap mask);
    bool TryGetFaceMaskData(int frameIndex, out FrameMaskProvider.FaceMaskData data);
}

internal static class ManualMaskKeyframeTimeline
{
    private sealed class TimelineState
    {
        internal readonly object Gate = new();
        internal bool Enabled;
        internal bool Loaded;
        internal string? VideoPath;
        internal List<ManualMaskTrackSegment> Segments = new();
        internal Dictionary<int, bool> SegmentValidity = new();
    }

    private static readonly ConditionalWeakTable<FrameMaskProvider, TimelineState> States = new();

    internal static void Configure(
        FrameMaskProvider provider,
        bool enabled,
        string? videoPath = null)
    {
        if (provider == null)
            throw new ArgumentNullException(nameof(provider));

        TimelineState state = States.GetValue(provider, static _ => new TimelineState());
        lock (state.Gate)
        {
            state.Enabled = enabled;
            if (!string.IsNullOrWhiteSpace(videoPath))
            {
                if (!string.IsNullOrWhiteSpace(state.VideoPath) &&
                    !string.Equals(state.VideoPath, videoPath, StringComparison.Ordinal))
                {
                    state.Loaded = false;
                    state.Segments.Clear();
                    state.SegmentValidity.Clear();
                }
                state.VideoPath = videoPath;
            }

            if (enabled && !state.Loaded && !string.IsNullOrWhiteSpace(state.VideoPath))
            {
                state.Segments = ManualMaskTrackStore.Load(state.VideoPath!)
                    .Select(static segment => segment.Clone())
                    .ToList();
                state.SegmentValidity.Clear();
                state.Loaded = true;
            }
        }
    }

    internal static bool IsEnabled(FrameMaskProvider provider)
        => provider != null &&
           States.TryGetValue(provider, out TimelineState? state) &&
           state.Enabled;

    internal static int GetNextExplicitKeyframe(
        FrameMaskProvider provider,
        int frameIndex,
        int totalFrames)
    {
        int[] keyframes = GetKeyframeIndices(provider);
        int position = Array.BinarySearch(keyframes, frameIndex);
        if (position < 0)
            position = ~position;
        else
            position++;

        if (position >= 0 && position < keyframes.Length)
            return keyframes[position];
        return totalFrames > frameIndex ? totalFrames : int.MaxValue;
    }

    internal static void NotifyExplicitKeyframeChanged(
        FrameMaskProvider provider,
        int frameIndex)
    {
        if (provider == null || frameIndex < 0 ||
            !States.TryGetValue(provider, out TimelineState? state))
        {
            return;
        }

        lock (state.Gate)
        {
            state.Segments.RemoveAll(segment => segment.SourceKeyframe == frameIndex);
            state.SegmentValidity.Remove(frameIndex);
            foreach (ManualMaskTrackSegment segment in state.Segments)
            {
                if (segment.SourceKeyframe >= frameIndex || segment.EndExclusive <= frameIndex)
                    continue;

                foreach (ManualMaskTrackComponent component in segment.Components)
                    component.Samples.RemoveAll(sample => sample.FrameIndex >= frameIndex);
                segment.EndExclusive = frameIndex;
                segment.StoppedByFailure = false;
                segment.StopFrame = null;
                segment.StopReason = null;
            }

            // Workspace bitmap persistence and compact track persistence are separate
            // transactions. Do not eagerly overwrite the disk track here; a stale
            // track is rejected after restart by its source-mask fingerprint.
        }
    }

    internal static void SetTrackSegment(
        FrameMaskProvider provider,
        ManualMaskTrackSegment segment)
    {
        if (provider == null)
            throw new ArgumentNullException(nameof(provider));
        if (segment == null)
            throw new ArgumentNullException(nameof(segment));
        if (string.IsNullOrWhiteSpace(segment.SourceMaskFingerprint))
            throw new InvalidOperationException("Tracked segment is missing its source-mask fingerprint.");
        if (segment.Components.Count == 0)
            throw new InvalidOperationException("Tracked segment has no mask components.");

        TimelineState state = States.GetValue(provider, static _ => new TimelineState());
        lock (state.Gate)
        {
            state.Segments.RemoveAll(existing =>
                existing.SourceKeyframe == segment.SourceKeyframe);
            state.Segments.Add(segment.Clone());
            state.Segments.Sort(static (a, b) =>
                a.SourceKeyframe.CompareTo(b.SourceKeyframe));
            state.SegmentValidity[segment.SourceKeyframe] = true;
            PersistLocked(state);
        }
    }

    internal static bool TryCloneEffectiveKeyframeMask(
        FrameMaskProvider provider,
        int frameIndex,
        out WriteableBitmap mask,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        mask = null!;
        if (!IsEnabled(provider) || frameIndex < 0)
            return false;

        int sourceKeyframe = FindFloorKeyframe(
            GetKeyframeIndices(provider),
            frameIndex);
        if (sourceKeyframe < 0)
            return false;

        if (!TryCloneSourceMask(provider, sourceKeyframe, out WriteableBitmap sourceMask))
            return false;

        try
        {
            if (frameIndex == sourceKeyframe ||
                !TryGetCurrentSegment(provider, sourceKeyframe, out ManualMaskTrackSegment? segment))
            {
                mask = sourceMask;
                sourceMask = null!;
                return true;
            }

            if (!TryCreateTransformedMask(
                    sourceMask,
                    segment!,
                    frameIndex,
                    out WriteableBitmap transformed))
            {
                return false;
            }

            mask = transformed;
            return true;
        }
        finally
        {
            sourceMask?.Dispose();
        }
    }

    internal static ExportMaskLease CreateExportMaskLease(FrameMaskProvider source)
    {
        if (source == null)
            throw new ArgumentNullException(nameof(source));

        FrameMaskProvider snapshot = source.CreateSnapshot();
        try
        {
            int[] keyframes = GetKeyframeIndices(snapshot);
            if (!IsEnabled(source) || keyframes.Length == 0)
                return new ExportMaskLease(snapshot, snapshot, null);

            ManualMaskTrackSegment[] segments = GetSegmentsSnapshot(source);
            var provider = new ManualKeyframeExportMaskProvider(
                snapshot,
                keyframes,
                segments);
            return new ExportMaskLease(snapshot, provider, provider);
        }
        catch
        {
            snapshot.Dispose();
            throw;
        }
    }

    private static bool TryGetCurrentSegment(
        FrameMaskProvider provider,
        int sourceKeyframe,
        out ManualMaskTrackSegment? segment)
    {
        segment = null;
        if (!States.TryGetValue(provider, out TimelineState? state))
            return false;

        lock (state.Gate)
        {
            segment = state.Segments.FirstOrDefault(candidate =>
                candidate.SourceKeyframe == sourceKeyframe);
            if (segment == null)
                return false;
            if (!IsSegmentCurrentLocked(provider, state, segment))
            {
                segment = null;
                return false;
            }
            return true;
        }
    }

    private static ManualMaskTrackSegment[] GetSegmentsSnapshot(FrameMaskProvider provider)
    {
        if (!States.TryGetValue(provider, out TimelineState? state))
            return Array.Empty<ManualMaskTrackSegment>();

        lock (state.Gate)
            return state.Segments.Select(static segment => segment.Clone()).ToArray();
    }

    private static bool IsSegmentCurrentLocked(
        FrameMaskProvider provider,
        TimelineState state,
        ManualMaskTrackSegment segment)
    {
        if (state.SegmentValidity.TryGetValue(segment.SourceKeyframe, out bool cached))
            return cached;

        bool valid = false;
        using WriteableBitmap? sourceMask = provider.GetFinalMask(segment.SourceKeyframe);
        if (sourceMask != null && !string.IsNullOrWhiteSpace(segment.SourceMaskFingerprint))
        {
            valid = string.Equals(
                ManualMaskFingerprint.Compute(sourceMask),
                segment.SourceMaskFingerprint,
                StringComparison.Ordinal);
        }

        state.SegmentValidity[segment.SourceKeyframe] = valid;
        return valid;
    }

    private static bool TryCloneSourceMask(
        FrameMaskProvider provider,
        int keyframe,
        out WriteableBitmap mask)
    {
        if (provider.TryCloneStoredMask(keyframe, out mask))
            return true;

        WriteableBitmap? faceMask = provider.GetFinalMask(keyframe);
        if (faceMask == null)
        {
            mask = null!;
            return false;
        }

        mask = faceMask;
        return true;
    }

    private static int[] GetKeyframeIndices(FrameMaskProvider provider)
    {
        var indices = new HashSet<int>(provider.GetStoredMaskFrameIndices());
        foreach (int frameIndex in provider.GetFaceMaskFrameIndices())
            indices.Add(frameIndex);

        int[] result = indices.ToArray();
        Array.Sort(result);
        return result;
    }

    private static int FindFloorKeyframe(int[] keyframes, int frameIndex)
    {
        int position = Array.BinarySearch(keyframes, frameIndex);
        if (position >= 0)
            return keyframes[position];

        position = ~position - 1;
        return position >= 0 ? keyframes[position] : -1;
    }

    private static bool TryCreateTransformedMask(
        WriteableBitmap source,
        ManualMaskTrackSegment segment,
        int frameIndex,
        out WriteableBitmap target)
    {
        target = new WriteableBitmap(
            source.PixelSize,
            source.Dpi,
            Avalonia.Platform.PixelFormat.Bgra8888,
            Avalonia.Platform.AlphaFormat.Premul);
        try
        {
            if (!TransformComponentsInto(source, target, segment, frameIndex))
            {
                target.Dispose();
                target = null!;
                return false;
            }
            return true;
        }
        catch
        {
            target.Dispose();
            target = null!;
            throw;
        }
    }

    private static bool TransformComponentsInto(
        WriteableBitmap source,
        WriteableBitmap target,
        ManualMaskTrackSegment segment,
        int frameIndex)
    {
        if (segment.Components.Count == 0)
            return false;

        using var sourceBuffer = source.Lock();
        using var targetBuffer = target.Lock();
        if (sourceBuffer.Size.Width != targetBuffer.Size.Width ||
            sourceBuffer.Size.Height != targetBuffer.Size.Height)
        {
            throw new InvalidOperationException("Manual track mask size mismatch.");
        }

        unsafe
        {
            byte* dst = (byte*)targetBuffer.Address;
            for (int y = 0; y < targetBuffer.Size.Height; y++)
            {
                new Span<byte>(
                    dst + y * targetBuffer.RowBytes,
                    targetBuffer.Size.Width * 4).Clear();
            }
        }

        foreach (ManualMaskTrackComponent component in segment.Components)
        {
            ManualMaskTrackSample? sample = component.Samples.FirstOrDefault(candidate =>
                candidate.FrameIndex == frameIndex);
            if (sample == null)
                return false;

            TransformComponentInto(
                sourceBuffer,
                targetBuffer,
                component,
                sample);
        }

        return true;
    }

    private static unsafe void TransformComponentInto(
        Avalonia.Platform.ILockedFramebuffer sourceBuffer,
        Avalonia.Platform.ILockedFramebuffer targetBuffer,
        ManualMaskTrackComponent component,
        ManualMaskTrackSample sample)
    {
        double scale = Math.Clamp(sample.Scale, 0.25, 4.0);
        var sourceBounds = new Rect(
            component.SourceBoundsX,
            component.SourceBoundsY,
            component.SourceBoundsWidth,
            component.SourceBoundsHeight);
        double destinationX = sourceBounds.X + sample.OffsetX;
        double destinationY = sourceBounds.Y + sample.OffsetY;
        int x0 = Math.Clamp((int)Math.Floor(destinationX), 0, targetBuffer.Size.Width);
        int y0 = Math.Clamp((int)Math.Floor(destinationY), 0, targetBuffer.Size.Height);
        int x1 = Math.Clamp(
            (int)Math.Ceiling(destinationX + sourceBounds.Width * scale),
            0,
            targetBuffer.Size.Width);
        int y1 = Math.Clamp(
            (int)Math.Ceiling(destinationY + sourceBounds.Height * scale),
            0,
            targetBuffer.Size.Height);

        byte* src = (byte*)sourceBuffer.Address;
        byte* dst = (byte*)targetBuffer.Address;
        for (int y = y0; y < y1; y++)
        {
            byte* dstRow = dst + y * targetBuffer.RowBytes;
            double sourceY = sourceBounds.Y + (y - destinationY) / scale;
            int sy = Math.Clamp(
                (int)Math.Round(sourceY),
                Math.Clamp((int)Math.Floor(sourceBounds.Y), 0, sourceBuffer.Size.Height - 1),
                Math.Clamp((int)Math.Ceiling(sourceBounds.Bottom) - 1, 0, sourceBuffer.Size.Height - 1));
            byte* srcRow = src + sy * sourceBuffer.RowBytes;
            for (int x = x0; x < x1; x++)
            {
                double sourceX = sourceBounds.X + (x - destinationX) / scale;
                int sx = Math.Clamp(
                    (int)Math.Round(sourceX),
                    Math.Clamp((int)Math.Floor(sourceBounds.X), 0, sourceBuffer.Size.Width - 1),
                    Math.Clamp((int)Math.Ceiling(sourceBounds.Right) - 1, 0, sourceBuffer.Size.Width - 1));
                byte alpha = srcRow[sx * 4 + 3];
                if (alpha == 0)
                    continue;
                byte* pixel = dstRow + x * 4;
                if (alpha <= pixel[3])
                    continue;
                pixel[0] = alpha;
                pixel[1] = alpha;
                pixel[2] = alpha;
                pixel[3] = alpha;
            }
        }
    }

    private static void PersistLocked(TimelineState state)
    {
        if (!state.Enabled || string.IsNullOrWhiteSpace(state.VideoPath))
            return;
        ManualMaskTrackStore.Save(state.VideoPath!, state.Segments);
    }

    internal sealed class ExportMaskLease : IDisposable
    {
        private FrameMaskProvider? _snapshot;
        private IDisposable? _providerLifetime;

        internal ExportMaskLease(
            FrameMaskProvider snapshot,
            IFrameMaskProvider provider,
            IDisposable? providerLifetime)
        {
            _snapshot = snapshot;
            Provider = provider;
            _providerLifetime = providerLifetime;
        }

        internal IFrameMaskProvider Provider { get; }

        public void Dispose()
        {
            Interlocked.Exchange(ref _providerLifetime, null)?.Dispose();
            Interlocked.Exchange(ref _snapshot, null)?.Dispose();
        }
    }

    private sealed class ManualKeyframeExportMaskProvider :
        IFrameMaskProvider,
        IExportFrameMaskReadView,
        IDisposable
    {
        private readonly FrameMaskProvider _snapshot;
        private readonly int[] _keyframes;
        private readonly ManualMaskTrackSegment[] _segments;
        private readonly bool[] _keyframeHasCoverage;
        private readonly bool[] _keyframeIsStoredMask;
        private readonly Dictionary<int, bool> _segmentValidity = new();
        private readonly Dictionary<int, WriteableBitmap> _faceSourceMasks = new();
        private WriteableBitmap? _scratchMask;

        internal ManualKeyframeExportMaskProvider(
            FrameMaskProvider snapshot,
            int[] keyframes,
            ManualMaskTrackSegment[] segments)
        {
            _snapshot = snapshot;
            _keyframes = keyframes;
            _segments = segments;
            Array.Sort(_keyframes);
            _keyframeHasCoverage = new bool[_keyframes.Length];
            _keyframeIsStoredMask = new bool[_keyframes.Length];

            for (int i = 0; i < _keyframes.Length; i++)
            {
                int frameIndex = _keyframes[i];
                bool isStored = snapshot.HasStoredMask(frameIndex);
                _keyframeIsStoredMask[i] = isStored;
                _keyframeHasCoverage[i] = isStored
                    ? snapshot.StoredMaskHasCoverage(frameIndex)
                    : snapshot.TryGetFaceMaskData(frameIndex, out var faceData) &&
                      faceData.Faces.Count > 0;
            }
        }

        public WriteableBitmap? GetFinalMask(int frameIndex)
        {
            if (!TryResolveFrame(
                    frameIndex,
                    out int position,
                    out ManualMaskTrackSegment? segment,
                    out bool tracked,
                    out bool blocked) ||
                blocked ||
                !_keyframeHasCoverage[position])
            {
                return null;
            }

            int sourceKeyframe = _keyframes[position];
            if (!tracked || segment == null)
                return _snapshot.GetFinalMask(sourceKeyframe);

            WriteableBitmap? source = GetSourceRasterMask(sourceKeyframe, position);
            if (source == null)
                return null;
            return TryCreateTransformedMask(source, segment, frameIndex, out WriteableBitmap transformed)
                ? transformed
                : null;
        }

        public bool TryGetBorrowedStoredMask(
            int frameIndex,
            out WriteableBitmap mask)
        {
            mask = null!;
            if (!TryResolveFrame(
                    frameIndex,
                    out int position,
                    out ManualMaskTrackSegment? segment,
                    out bool tracked,
                    out bool blocked) ||
                blocked ||
                !_keyframeHasCoverage[position])
            {
                return false;
            }

            int sourceKeyframe = _keyframes[position];
            if (!tracked || segment == null)
            {
                if (!_keyframeIsStoredMask[position])
                    return false;
                return _snapshot.TryGetStoredMaskBorrowed(sourceKeyframe, out mask);
            }

            WriteableBitmap? source = GetSourceRasterMask(sourceKeyframe, position);
            if (source == null)
                return false;

            EnsureScratchMask(source);
            if (!TransformComponentsInto(source, _scratchMask!, segment, frameIndex))
                return false;
            mask = _scratchMask!;
            return true;
        }

        public bool TryGetFaceMaskData(
            int frameIndex,
            out FrameMaskProvider.FaceMaskData data)
        {
            data = default;
            if (!TryResolveFrame(
                    frameIndex,
                    out int position,
                    out ManualMaskTrackSegment? segment,
                    out bool tracked,
                    out bool blocked) ||
                blocked ||
                tracked ||
                !_keyframeHasCoverage[position] ||
                _keyframeIsStoredMask[position])
            {
                return false;
            }

            return _snapshot.TryGetFaceMaskData(_keyframes[position], out data);
        }

        private bool TryResolveFrame(
            int frameIndex,
            out int position,
            out ManualMaskTrackSegment? segment,
            out bool tracked,
            out bool blocked)
        {
            position = -1;
            segment = null;
            tracked = false;
            blocked = false;
            if (frameIndex < 0 || _keyframes.Length == 0)
                return false;

            position = Array.BinarySearch(_keyframes, frameIndex);
            if (position < 0)
                position = ~position - 1;
            if (position < 0)
                return false;

            int sourceKeyframe = _keyframes[position];
            if (frameIndex == sourceKeyframe)
                return true;

            segment = _segments.FirstOrDefault(candidate =>
                candidate.SourceKeyframe == sourceKeyframe);
            if (segment == null)
                return true;
            if (!IsSegmentCurrent(segment))
            {
                blocked = true;
                return true;
            }

            tracked = true;
            foreach (ManualMaskTrackComponent component in segment.Components)
            {
                if (!component.Samples.Any(sample => sample.FrameIndex == frameIndex))
                {
                    blocked = true;
                    break;
                }
            }
            return true;
        }

        private bool IsSegmentCurrent(ManualMaskTrackSegment segment)
        {
            if (_segmentValidity.TryGetValue(segment.SourceKeyframe, out bool cached))
                return cached;

            bool valid = false;
            using WriteableBitmap? sourceMask = _snapshot.GetFinalMask(segment.SourceKeyframe);
            if (sourceMask != null && !string.IsNullOrWhiteSpace(segment.SourceMaskFingerprint))
            {
                valid = string.Equals(
                    ManualMaskFingerprint.Compute(sourceMask),
                    segment.SourceMaskFingerprint,
                    StringComparison.Ordinal);
            }

            _segmentValidity[segment.SourceKeyframe] = valid;
            return valid;
        }

        private WriteableBitmap? GetSourceRasterMask(int sourceKeyframe, int position)
        {
            if (_keyframeIsStoredMask[position])
            {
                return _snapshot.TryGetStoredMaskBorrowed(sourceKeyframe, out WriteableBitmap stored)
                    ? stored
                    : null;
            }

            if (_faceSourceMasks.TryGetValue(sourceKeyframe, out WriteableBitmap? cached))
                return cached;
            WriteableBitmap? created = _snapshot.GetFinalMask(sourceKeyframe);
            if (created == null)
                return null;
            _faceSourceMasks[sourceKeyframe] = created;
            return created;
        }

        private void EnsureScratchMask(WriteableBitmap source)
        {
            if (_scratchMask != null &&
                _scratchMask.PixelSize.Width == source.PixelSize.Width &&
                _scratchMask.PixelSize.Height == source.PixelSize.Height)
            {
                return;
            }

            _scratchMask?.Dispose();
            _scratchMask = new WriteableBitmap(
                source.PixelSize,
                source.Dpi,
                Avalonia.Platform.PixelFormat.Bgra8888,
                Avalonia.Platform.AlphaFormat.Premul);
        }

        public void SetMask(int frameIndex, WriteableBitmap mask)
            => throw new NotSupportedException(
                "Export mask snapshots are read-only.");

        public void Dispose()
        {
            _scratchMask?.Dispose();
            _scratchMask = null;
            foreach (WriteableBitmap bitmap in _faceSourceMasks.Values)
                bitmap.Dispose();
            _faceSourceMasks.Clear();
        }
    }
}
