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
                state.VideoPath = videoPath;

            if (enabled && !state.Loaded && !string.IsNullOrWhiteSpace(state.VideoPath))
            {
                state.Segments = ManualMaskTrackStore.Load(state.VideoPath!)
                    .Select(static segment => segment.Clone())
                    .ToList();
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
            foreach (ManualMaskTrackSegment segment in state.Segments)
            {
                if (segment.SourceKeyframe >= frameIndex || segment.EndExclusive <= frameIndex)
                    continue;

                segment.Samples.RemoveAll(sample => sample.FrameIndex >= frameIndex);
                segment.EndExclusive = frameIndex;
                segment.StoppedByFailure = false;
                segment.StopFrame = null;
                segment.StopReason = null;
            }
            PersistLocked(state);
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

        TimelineState state = States.GetValue(provider, static _ => new TimelineState());
        lock (state.Gate)
        {
            state.Segments.RemoveAll(existing =>
                existing.SourceKeyframe == segment.SourceKeyframe);
            state.Segments.Add(segment.Clone());
            state.Segments.Sort(static (a, b) =>
                a.SourceKeyframe.CompareTo(b.SourceKeyframe));
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

        int[] keyframes = GetKeyframeIndices(provider);
        int sourceKeyframe = FindFloorKeyframe(keyframes, frameIndex);
        if (sourceKeyframe < 0)
            return false;

        if (!TryResolveTransform(
                provider,
                sourceKeyframe,
                frameIndex,
                out ManualMaskTrackSample? sample,
                out bool blocked))
        {
            return false;
        }
        if (blocked)
            return false;

        if (!TryCloneSourceMask(provider, sourceKeyframe, out WriteableBitmap sourceMask))
            return false;

        if (sample == null)
        {
            mask = sourceMask;
            return true;
        }

        try
        {
            Rect sourceBounds = GetSegmentSourceBounds(provider, sourceKeyframe, sourceMask);
            mask = CreateTransformedMask(sourceMask, sourceBounds, sample);
            return true;
        }
        finally
        {
            sourceMask.Dispose();
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

    private static ManualMaskTrackSegment[] GetSegmentsSnapshot(FrameMaskProvider provider)
    {
        if (!States.TryGetValue(provider, out TimelineState? state))
            return Array.Empty<ManualMaskTrackSegment>();

        lock (state.Gate)
            return state.Segments.Select(static segment => segment.Clone()).ToArray();
    }

    private static bool TryResolveTransform(
        FrameMaskProvider provider,
        int sourceKeyframe,
        int frameIndex,
        out ManualMaskTrackSample? sample,
        out bool blocked)
    {
        sample = null;
        blocked = false;
        if (frameIndex == sourceKeyframe)
            return true;
        if (!States.TryGetValue(provider, out TimelineState? state))
            return true;

        lock (state.Gate)
        {
            ManualMaskTrackSegment? segment = state.Segments.FirstOrDefault(
                candidate => candidate.SourceKeyframe == sourceKeyframe);
            if (segment == null)
                return true;

            sample = segment.Samples.FirstOrDefault(candidate =>
                candidate.FrameIndex == frameIndex);
            if (sample != null)
                return true;

            blocked = true;
            return true;
        }
    }

    private static Rect GetSegmentSourceBounds(
        FrameMaskProvider provider,
        int sourceKeyframe,
        WriteableBitmap sourceMask)
    {
        if (States.TryGetValue(provider, out TimelineState? state))
        {
            lock (state.Gate)
            {
                ManualMaskTrackSegment? segment = state.Segments.FirstOrDefault(
                    candidate => candidate.SourceKeyframe == sourceKeyframe);
                if (segment != null &&
                    segment.SourceBoundsWidth > 0 &&
                    segment.SourceBoundsHeight > 0)
                {
                    return new Rect(
                        segment.SourceBoundsX,
                        segment.SourceBoundsY,
                        segment.SourceBoundsWidth,
                        segment.SourceBoundsHeight);
                }
            }
        }

        return FindMaskBounds(sourceMask);
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

    private static Rect FindMaskBounds(WriteableBitmap mask)
    {
        using var fb = mask.Lock();
        int minX = fb.Size.Width;
        int minY = fb.Size.Height;
        int maxX = -1;
        int maxY = -1;
        unsafe
        {
            byte* basePtr = (byte*)fb.Address;
            for (int y = 0; y < fb.Size.Height; y++)
            {
                byte* row = basePtr + y * fb.RowBytes;
                for (int x = 0; x < fb.Size.Width; x++)
                {
                    if (row[x * 4 + 3] <= 8)
                        continue;
                    minX = Math.Min(minX, x);
                    minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x);
                    maxY = Math.Max(maxY, y);
                }
            }
        }

        if (maxX < minX || maxY < minY)
            return default;
        return new Rect(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    private static WriteableBitmap CreateTransformedMask(
        WriteableBitmap source,
        Rect sourceBounds,
        ManualMaskTrackSample sample)
    {
        var target = new WriteableBitmap(
            source.PixelSize,
            source.Dpi,
            Avalonia.Platform.PixelFormat.Bgra8888,
            Avalonia.Platform.AlphaFormat.Premul);
        TransformMaskInto(source, target, sourceBounds, sample);
        return target;
    }

    private static void TransformMaskInto(
        WriteableBitmap source,
        WriteableBitmap target,
        Rect sourceBounds,
        ManualMaskTrackSample sample)
    {
        using var sourceBuffer = source.Lock();
        using var targetBuffer = target.Lock();
        if (sourceBuffer.Size != targetBuffer.Size)
            throw new InvalidOperationException("Manual track mask size mismatch.");

        double scale = Math.Clamp(sample.Scale, 0.25, 4.0);
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

        unsafe
        {
            byte* src = (byte*)sourceBuffer.Address;
            byte* dst = (byte*)targetBuffer.Address;
            for (int y = 0; y < targetBuffer.Size.Height; y++)
            {
                new Span<byte>(
                    dst + y * targetBuffer.RowBytes,
                    targetBuffer.Size.Width * 4).Clear();
            }

            for (int y = y0; y < y1; y++)
            {
                byte* dstRow = dst + y * targetBuffer.RowBytes;
                double sourceY = sourceBounds.Y + (y - destinationY) / scale;
                int sy = Math.Clamp(
                    (int)Math.Round(sourceY),
                    0,
                    sourceBuffer.Size.Height - 1);
                byte* srcRow = src + sy * sourceBuffer.RowBytes;
                for (int x = x0; x < x1; x++)
                {
                    double sourceX = sourceBounds.X + (x - destinationX) / scale;
                    int sx = Math.Clamp(
                        (int)Math.Round(sourceX),
                        0,
                        sourceBuffer.Size.Width - 1);
                    byte alpha = srcRow[sx * 4 + 3];
                    if (alpha == 0)
                        continue;
                    byte* pixel = dstRow + x * 4;
                    pixel[0] = alpha;
                    pixel[1] = alpha;
                    pixel[2] = alpha;
                    pixel[3] = alpha;
                }
            }
        }
    }

    private static FrameMaskProvider.FaceMaskData TransformFaceData(
        FrameMaskProvider.FaceMaskData source,
        Rect sourceBounds,
        ManualMaskTrackSample sample)
    {
        double scale = Math.Clamp(sample.Scale, 0.25, 4.0);
        double anchorX = sourceBounds.X;
        double anchorY = sourceBounds.Y;
        var transformed = new Rect[source.Faces.Count];
        for (int i = 0; i < source.Faces.Count; i++)
        {
            Rect face = source.Faces[i];
            transformed[i] = new Rect(
                anchorX + sample.OffsetX + (face.X - anchorX) * scale,
                anchorY + sample.OffsetY + (face.Y - anchorY) * scale,
                face.Width * scale,
                face.Height * scale);
        }

        return new FrameMaskProvider.FaceMaskData(
            source.Size,
            transformed,
            source.MinConfidence,
            source.Confidences);
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
                    out ManualMaskTrackSample? sample,
                    out bool blocked) ||
                blocked ||
                !_keyframeHasCoverage[position])
            {
                return null;
            }

            int sourceKeyframe = _keyframes[position];
            if (sample == null)
                return _snapshot.GetFinalMask(sourceKeyframe);

            if (!_snapshot.TryCloneStoredMask(sourceKeyframe, out WriteableBitmap sourceMask))
            {
                if (!_snapshot.TryGetFaceMaskData(sourceKeyframe, out var faceData))
                    return null;
                Rect bounds = GetSegmentBounds(sourceKeyframe);
                var transformed = TransformFaceData(faceData, bounds, sample);
                return FrameMaskProvider.CreateMaskFromFaceRects(
                    transformed.Size,
                    transformed.Faces);
            }

            try
            {
                return CreateTransformedMask(
                    sourceMask,
                    GetSegmentBounds(sourceKeyframe),
                    sample);
            }
            finally
            {
                sourceMask.Dispose();
            }
        }

        public bool TryGetBorrowedStoredMask(
            int frameIndex,
            out WriteableBitmap mask)
        {
            mask = null!;
            if (!TryResolveFrame(
                    frameIndex,
                    out int position,
                    out ManualMaskTrackSample? sample,
                    out bool blocked) ||
                blocked ||
                !_keyframeHasCoverage[position] ||
                !_keyframeIsStoredMask[position])
            {
                return false;
            }

            int sourceKeyframe = _keyframes[position];
            if (sample == null)
            {
                return _snapshot.TryGetStoredMaskBorrowed(
                    sourceKeyframe,
                    out mask);
            }

            if (!_snapshot.TryGetStoredMaskBorrowed(sourceKeyframe, out WriteableBitmap source))
                return false;

            EnsureScratchMask(source);
            TransformMaskInto(
                source,
                _scratchMask!,
                GetSegmentBounds(sourceKeyframe),
                sample);
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
                    out ManualMaskTrackSample? sample,
                    out bool blocked) ||
                blocked ||
                !_keyframeHasCoverage[position] ||
                _keyframeIsStoredMask[position])
            {
                return false;
            }

            int sourceKeyframe = _keyframes[position];
            if (!_snapshot.TryGetFaceMaskData(sourceKeyframe, out var sourceData))
                return false;

            data = sample == null
                ? sourceData
                : TransformFaceData(
                    sourceData,
                    GetSegmentBounds(sourceKeyframe),
                    sample);
            return true;
        }

        private bool TryResolveFrame(
            int frameIndex,
            out int position,
            out ManualMaskTrackSample? sample,
            out bool blocked)
        {
            position = -1;
            sample = null;
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

            ManualMaskTrackSegment? segment = _segments.FirstOrDefault(
                candidate => candidate.SourceKeyframe == sourceKeyframe);
            if (segment == null)
                return true;

            sample = segment.Samples.FirstOrDefault(candidate =>
                candidate.FrameIndex == frameIndex);
            blocked = sample == null;
            return true;
        }

        private Rect GetSegmentBounds(int sourceKeyframe)
        {
            ManualMaskTrackSegment? segment = _segments.FirstOrDefault(
                candidate => candidate.SourceKeyframe == sourceKeyframe);
            if (segment != null &&
                segment.SourceBoundsWidth > 0 &&
                segment.SourceBoundsHeight > 0)
            {
                return new Rect(
                    segment.SourceBoundsX,
                    segment.SourceBoundsY,
                    segment.SourceBoundsWidth,
                    segment.SourceBoundsHeight);
            }

            if (_snapshot.TryGetStoredMaskBorrowed(sourceKeyframe, out WriteableBitmap stored))
                return FindMaskBounds(stored);

            using WriteableBitmap? mask = _snapshot.GetFinalMask(sourceKeyframe);
            return mask == null ? default : FindMaskBounds(mask);
        }

        private void EnsureScratchMask(WriteableBitmap source)
        {
            if (_scratchMask != null && _scratchMask.PixelSize == source.PixelSize)
                return;

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
        }
    }
}
