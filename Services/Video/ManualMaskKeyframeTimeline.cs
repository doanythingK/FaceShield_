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

    internal static void Configure(FrameMaskProvider provider, bool enabled, string? videoPath = null)
    {
        if (provider == null) throw new ArgumentNullException(nameof(provider));
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
                    .Select(static segment => segment.Clone()).ToList();
                state.SegmentValidity.Clear();
                state.Loaded = true;
            }
        }
    }

    internal static bool IsEnabled(FrameMaskProvider provider) => provider != null &&
        States.TryGetValue(provider, out TimelineState? state) && state.Enabled;

    internal static int GetNextExplicitKeyframe(FrameMaskProvider provider, int frameIndex, int totalFrames)
    {
        int[] stored = provider.GetStoredMaskFrameIndices();
        int[] keyframes = stored.Length > 0 ? stored : GetKeyframeIndices(provider);
        Array.Sort(keyframes);
        int position = Array.BinarySearch(keyframes, frameIndex);
        position = position < 0 ? ~position : position + 1;
        if (position >= 0 && position < keyframes.Length) return keyframes[position];
        return totalFrames > frameIndex ? totalFrames : int.MaxValue;
    }

    internal static void NotifyExplicitKeyframeChanged(FrameMaskProvider provider, int frameIndex)
    {
        if (provider == null || frameIndex < 0 ||
            !States.TryGetValue(provider, out TimelineState? state)) return;
        lock (state.Gate)
        {
            state.Segments.RemoveAll(segment => segment.SourceKeyframe == frameIndex);
            state.SegmentValidity.Remove(frameIndex);
        }
    }

    internal static bool InvalidateSegmentIfSourceChanged(FrameMaskProvider provider, int sourceKeyframe)
    {
        if (provider == null || sourceKeyframe < 0 ||
            !States.TryGetValue(provider, out TimelineState? state)) return false;
        lock (state.Gate)
        {
            ManualMaskTrackSegment? segment = state.Segments.FirstOrDefault(candidate =>
                candidate.SourceKeyframe == sourceKeyframe);
            if (segment == null) return false;
            state.SegmentValidity.Remove(sourceKeyframe);
            return !IsSegmentCurrentLocked(provider, state, segment);
        }
    }

    internal static void SetTrackSegment(FrameMaskProvider provider, ManualMaskTrackSegment segment)
    {
        if (provider == null) throw new ArgumentNullException(nameof(provider));
        if (segment == null) throw new ArgumentNullException(nameof(segment));
        if (string.IsNullOrWhiteSpace(segment.SourceMaskFingerprint))
            throw new InvalidOperationException("Tracked segment is missing its source-mask fingerprint.");
        if (segment.Components.Count == 0)
            throw new InvalidOperationException("Tracked segment has no mask components.");
        TimelineState state = States.GetValue(provider, static _ => new TimelineState());
        lock (state.Gate)
        {
            var next = state.Segments.Where(existing => existing.SourceKeyframe != segment.SourceKeyframe)
                .Select(static existing => existing.Clone()).ToList();
            next.Add(segment.Clone());
            next.Sort(static (a, b) => a.SourceKeyframe.CompareTo(b.SourceKeyframe));
            PersistSegmentsLocked(state, next);
            state.Segments = next;
            state.SegmentValidity[segment.SourceKeyframe] = true;
        }
    }

    internal static bool TryCloneEffectiveKeyframeMask(FrameMaskProvider provider, int frameIndex,
        out WriteableBitmap mask, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        mask = null!;
        if (!IsEnabled(provider) || frameIndex < 0) return false;
        int[] stored = provider.GetStoredMaskFrameIndices();
        bool independentManual = stored.Length > 0;
        int sourceKeyframe = FindFloorKeyframe(
            independentManual ? Sort(stored) : GetKeyframeIndices(provider), frameIndex);
        if (sourceKeyframe < 0)
        {
            if (!independentManual) return false;
            WriteableBitmap? automatic = provider.TryGetFaceMaskData(frameIndex, out var faces)
                ? FrameMaskProvider.CreateMaskFromFaceRects(faces.Size, faces.Faces) : null;
            if (automatic == null) return false;
            mask = automatic;
            return true;
        }
        if (!TryCloneSourceMask(provider, sourceKeyframe, out WriteableBitmap sourceMask))
            return false;
        try
        {
            WriteableBitmap manual;
            if (frameIndex == sourceKeyframe)
            {
                manual = sourceMask;
                sourceMask = null!;
            }
            else
            {
                bool hasSegment = TryResolveCurrentSegment(provider, sourceKeyframe,
                    out ManualMaskTrackSegment? segment, out bool blocked);
                if (blocked || !hasSegment || segment == null ||
                    frameIndex >= segment.EndExclusive || !HasAllSamples(segment, frameIndex) ||
                    !TryCreateTransformedMask(sourceMask, segment, frameIndex, out manual))
                    return false;
            }
            try
            {
                if (independentManual && provider.TryGetFaceMaskData(frameIndex, out var faces))
                    UnionAutoInto(manual, faces);
                mask = manual;
                return true;
            }
            catch
            {
                manual.Dispose();
                throw;
            }
        }
        finally { sourceMask?.Dispose(); }
    }

    /// <summary>
    /// Resolve ONLY the stored manual raster and its validated tracked samples.
    /// Never subtract Auto from a composite: overlapping manual pixels cannot
    /// be recovered from that lossy union. The caller owns the result.
    /// </summary>
    internal static bool TryCloneEffectiveManualMask(FrameMaskProvider provider,
        int frameIndex, out WriteableBitmap mask,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        mask = null!;
        if (!IsEnabled(provider) || frameIndex < 0) return false;
        int[] manualKeyframes = Sort(provider.GetStoredMaskFrameIndices());
        int sourceKeyframe = FindFloorKeyframe(manualKeyframes, frameIndex);
        if (sourceKeyframe < 0 ||
            !provider.TryCloneStoredMask(sourceKeyframe, out WriteableBitmap sourceMask))
            return false;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (frameIndex == sourceKeyframe)
            {
                mask = sourceMask;
                sourceMask = null!;
                return true;
            }

            if (!TryResolveCurrentSegment(provider, sourceKeyframe,
                    out ManualMaskTrackSegment? segment, out bool blocked) ||
                blocked || segment == null || frameIndex >= segment.EndExclusive ||
                !HasAllSamples(segment, frameIndex))
                return false;

            cancellationToken.ThrowIfCancellationRequested();
            return TryCreateTransformedMask(sourceMask, segment, frameIndex, out mask);
        }
        finally
        {
            sourceMask?.Dispose();
        }
    }

    internal static ExportMaskLease CreateExportMaskLease(FrameMaskProvider source)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        FrameMaskProvider snapshot = source.CreateSnapshot();
        try
        {
            int[] stored = Sort(snapshot.GetStoredMaskFrameIndices());
            int[] keyframes = stored.Length > 0 ? stored : GetKeyframeIndices(snapshot);
            if (!IsEnabled(source) || keyframes.Length == 0)
                return new ExportMaskLease(snapshot, snapshot, null);
            ManualMaskTrackSegment[] segments = GetSegmentsSnapshot(source);
            var provider = new ManualKeyframeExportMaskProvider(
                snapshot, keyframes, segments, independentManual: stored.Length > 0);
            return new ExportMaskLease(snapshot, provider, provider);
        }
        catch
        {
            snapshot.Dispose();
            throw;
        }
    }

    private static bool TryResolveCurrentSegment(FrameMaskProvider provider, int sourceKeyframe,
        out ManualMaskTrackSegment? segment, out bool blocked)
    {
        segment = null;
        blocked = false;
        if (!States.TryGetValue(provider, out TimelineState? state)) return false;
        lock (state.Gate)
        {
            segment = state.Segments.FirstOrDefault(candidate => candidate.SourceKeyframe == sourceKeyframe);
            if (segment == null) return false;
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
        lock (state.Gate) return state.Segments.Select(static segment => segment.Clone()).ToArray();
    }

    private static bool IsSegmentCurrentLocked(FrameMaskProvider provider,
        TimelineState state, ManualMaskTrackSegment segment)
    {
        if (state.SegmentValidity.TryGetValue(segment.SourceKeyframe, out bool cached)) return cached;
        bool valid = false;
        WriteableBitmap? sourceMask = null;
        try
        {
            if (TryCloneSourceMask(provider, segment.SourceKeyframe, out WriteableBitmap source))
                sourceMask = source;
            if (sourceMask != null && !string.IsNullOrWhiteSpace(segment.SourceMaskFingerprint))
                valid = string.Equals(ManualMaskFingerprint.Compute(sourceMask),
                    segment.SourceMaskFingerprint, StringComparison.Ordinal);
        }
        finally
        {
            sourceMask?.Dispose();
        }
        state.SegmentValidity[segment.SourceKeyframe] = valid;
        return valid;
    }

    private static bool TryCloneSourceMask(FrameMaskProvider provider, int keyframe, out WriteableBitmap mask)
    {
        if (provider.TryCloneStoredMask(keyframe, out mask)) return true;
        WriteableBitmap? face = provider.GetFinalMask(keyframe);
        if (face == null) { mask = null!; return false; }
        mask = face;
        return true;
    }

    private static int[] Sort(int[] indices)
    {
        Array.Sort(indices);
        return indices;
    }

    private static int[] GetKeyframeIndices(FrameMaskProvider provider)
    {
        var indices = new HashSet<int>(provider.GetStoredMaskFrameIndices());
        foreach (int frame in provider.GetFaceMaskFrameIndices()) indices.Add(frame);
        return Sort(indices.ToArray());
    }

    private static int FindFloorKeyframe(int[] keyframes, int frameIndex)
    {
        int position = Array.BinarySearch(keyframes, frameIndex);
        if (position < 0) position = ~position - 1;
        return position >= 0 ? keyframes[position] : -1;
    }

    private static ManualMaskTrackSample? FindSample(IReadOnlyList<ManualMaskTrackSample> samples, int frameIndex)
    {
        int lo = 0, hi = samples.Count - 1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            ManualMaskTrackSample sample = samples[mid];
            if (sample.FrameIndex == frameIndex) return sample;
            if (sample.FrameIndex < frameIndex) lo = mid + 1;
            else hi = mid - 1;
        }
        return null;
    }

    private static bool HasAllSamples(ManualMaskTrackSegment segment, int frameIndex)
    {
        foreach (ManualMaskTrackComponent component in segment.Components)
            if (FindSample(component.Samples, frameIndex) == null) return false;
        return segment.Components.Count > 0;
    }

    private static bool TryCreateTransformedMask(WriteableBitmap source,
        ManualMaskTrackSegment segment, int frameIndex, out WriteableBitmap target)
    {
        target = new WriteableBitmap(source.PixelSize, source.Dpi,
            Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
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

    private static bool TransformComponentsInto(WriteableBitmap source, WriteableBitmap target,
        ManualMaskTrackSegment segment, int frameIndex)
    {
        if (segment.Components.Count == 0) return false;
        using var src = source.Lock();
        using var dst = target.Lock();
        if (src.Size.Width != dst.Size.Width || src.Size.Height != dst.Size.Height)
            throw new InvalidOperationException("Manual track mask size mismatch.");
        unsafe
        {
            byte* pixels = (byte*)dst.Address;
            for (int y = 0; y < dst.Size.Height; y++)
                new Span<byte>(pixels + y * dst.RowBytes, dst.Size.Width * 4).Clear();
        }
        foreach (ManualMaskTrackComponent component in segment.Components)
        {
            ManualMaskTrackSample? sample = FindSample(component.Samples, frameIndex);
            if (sample == null) return false;
            TransformComponentInto(src, dst, component, sample);
        }
        return true;
    }

    private static unsafe void TransformComponentInto(
        Avalonia.Platform.ILockedFramebuffer sourceBuffer,
        Avalonia.Platform.ILockedFramebuffer targetBuffer,
        ManualMaskTrackComponent component, ManualMaskTrackSample sample)
    {
        double scale = Math.Clamp(sample.Scale, 0.25, 4.0);
        double cos = Math.Cos(sample.RotationRadians), sin = Math.Sin(sample.RotationRadians);
        double sx0 = component.SourceBoundsX, sy0 = component.SourceBoundsY;
        double width = component.SourceBoundsWidth, height = component.SourceBoundsHeight;
        double originX = sx0 + sample.OffsetX, originY = sy0 + sample.OffsetY;
        double x1 = originX + scale * cos * width;
        double y1 = originY + scale * sin * width;
        double x2 = originX - scale * sin * height;
        double y2 = originY + scale * cos * height;
        double x3 = x1 - scale * sin * height;
        double y3 = y1 + scale * cos * height;
        int minX = Math.Clamp((int)Math.Floor(Math.Min(Math.Min(originX, x1), Math.Min(x2, x3))), 0, targetBuffer.Size.Width);
        int minY = Math.Clamp((int)Math.Floor(Math.Min(Math.Min(originY, y1), Math.Min(y2, y3))), 0, targetBuffer.Size.Height);
        int maxX = Math.Clamp((int)Math.Ceiling(Math.Max(Math.Max(originX, x1), Math.Max(x2, x3))), 0, targetBuffer.Size.Width);
        int maxY = Math.Clamp((int)Math.Ceiling(Math.Max(Math.Max(originY, y1), Math.Max(y2, y3))), 0, targetBuffer.Size.Height);
        int sourceMinX = Math.Clamp((int)Math.Floor(sx0), 0, sourceBuffer.Size.Width - 1);
        int sourceMinY = Math.Clamp((int)Math.Floor(sy0), 0, sourceBuffer.Size.Height - 1);
        int sourceMaxX = Math.Clamp((int)Math.Ceiling(sx0 + width) - 1, 0, sourceBuffer.Size.Width - 1);
        int sourceMaxY = Math.Clamp((int)Math.Ceiling(sy0 + height) - 1, 0, sourceBuffer.Size.Height - 1);
        byte* sourcePixels = (byte*)sourceBuffer.Address;
        byte* targetPixels = (byte*)targetBuffer.Address;
        for (int y = minY; y < maxY; y++)
        {
            byte* targetRow = targetPixels + y * targetBuffer.RowBytes;
            for (int x = minX; x < maxX; x++)
            {
                double dx = (x - originX) / scale, dy = (y - originY) / scale;
                double localX = cos * dx + sin * dy;
                double localY = -sin * dx + cos * dy;
                if (localX < 0 || localY < 0 || localX >= width || localY >= height) continue;
                int sx = Math.Clamp((int)Math.Round(sx0 + localX), sourceMinX, sourceMaxX);
                int sy = Math.Clamp((int)Math.Round(sy0 + localY), sourceMinY, sourceMaxY);
                byte alpha = *(sourcePixels + sy * sourceBuffer.RowBytes + sx * 4 + 3);
                if (alpha == 0) continue;
                byte* target = targetRow + x * 4;
                if (alpha <= target[3]) continue;
                target[0] = alpha;
                target[1] = alpha;
                target[2] = alpha;
                target[3] = alpha;
            }
        }
    }

    private static void UnionAutoInto(WriteableBitmap manual, FrameMaskProvider.FaceMaskData data)
    {
        if (manual.PixelSize.Width != data.Size.Width || manual.PixelSize.Height != data.Size.Height)
            throw new InvalidOperationException("Auto and manual mask dimensions do not match.");
        using WriteableBitmap automatic = FrameMaskProvider.CreateMaskFromFaceRects(data.Size, data.Faces);
        using var autoBuffer = automatic.Lock();
        using var manualBuffer = manual.Lock();
        unsafe
        {
            byte* autoPixels = (byte*)autoBuffer.Address;
            byte* manualPixels = (byte*)manualBuffer.Address;
            for (int y = 0; y < manualBuffer.Size.Height; y++)
            {
                byte* autoRow = autoPixels + y * autoBuffer.RowBytes;
                byte* manualRow = manualPixels + y * manualBuffer.RowBytes;
                for (int x = 0; x < manualBuffer.Size.Width; x++)
                {
                    int p = x * 4;
                    byte alpha = Math.Max(manualRow[p + 3], autoRow[p + 3]);
                    if (alpha <= manualRow[p + 3]) continue;
                    manualRow[p] = alpha;
                    manualRow[p + 1] = alpha;
                    manualRow[p + 2] = alpha;
                    manualRow[p + 3] = alpha;
                }
            }
        }
    }

    private static void PersistSegmentsLocked(TimelineState state,
        IReadOnlyCollection<ManualMaskTrackSegment> segments)
    {
        if (!state.Enabled)
            throw new InvalidOperationException("Manual tracking is not enabled for this workspace.");
        if (string.IsNullOrWhiteSpace(state.VideoPath))
            throw new InvalidOperationException("Manual tracking video path is unavailable.");
        ManualMaskTrackStore.Save(state.VideoPath!, segments);
    }

    internal sealed class ExportMaskLease : IDisposable
    {
        private FrameMaskProvider? _snapshot;
        private IDisposable? _providerLifetime;
        internal ExportMaskLease(FrameMaskProvider snapshot, IFrameMaskProvider provider, IDisposable? providerLifetime)
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
        IFrameMaskProvider, IExportFrameMaskReadView, IDisposable
    {
        private readonly FrameMaskProvider _snapshot;
        private readonly int[] _keyframes;
        private readonly ManualMaskTrackSegment[] _segments;
        private readonly bool _independentManual;
        private readonly bool[] _keyframeHasCoverage;
        private readonly bool[] _keyframeIsStoredMask;
        private readonly Dictionary<int, bool> _segmentValidity = new();
        private int _faceSourceMaskKeyframe = -1;
        private WriteableBitmap? _faceSourceMask;
        private WriteableBitmap? _scratchMask;
        private WriteableBitmap? _compositeMask;
        private int _compositeFrame = -1;

        internal ManualKeyframeExportMaskProvider(FrameMaskProvider snapshot,
            int[] keyframes, ManualMaskTrackSegment[] segments, bool independentManual)
        {
            _snapshot = snapshot;
            _keyframes = keyframes;
            _segments = segments;
            _independentManual = independentManual;
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
                    : snapshot.TryGetFaceMaskData(frameIndex, out var faceData) && faceData.Faces.Count > 0;
            }
        }

        public WriteableBitmap? GetFinalMask(int frameIndex)
        {
            if (!TryResolveFrame(frameIndex, out int position,
                    out ManualMaskTrackSegment? segment, out bool tracked, out bool blocked))
            {
                return _independentManual ? _snapshot.GetFinalMask(frameIndex) : null;
            }
            if (blocked || !_keyframeHasCoverage[position])
                return _independentManual ? _snapshot.GetFinalMask(frameIndex) : null;
            int sourceKeyframe = _keyframes[position];
            if (!_independentManual)
            {
                if (!tracked || segment == null) return _snapshot.GetFinalMask(sourceKeyframe);
                WriteableBitmap? legacySource = GetSourceRasterMask(sourceKeyframe, position);
                if (legacySource == null) return null;
                return TryCreateTransformedMask(legacySource, segment, frameIndex,
                    out WriteableBitmap legacyMask) ? legacyMask : throw MissingTracking(frameIndex);
            }

            WriteableBitmap? result;
            if (!tracked || segment == null)
                result = _snapshot.TryCloneStoredMask(sourceKeyframe, out WriteableBitmap stored)
                    ? stored : null;
            else
            {
                WriteableBitmap? source = GetSourceRasterMask(sourceKeyframe, position);
                result = source != null && TryCreateTransformedMask(source, segment,
                    frameIndex, out WriteableBitmap transformed) ? transformed : throw MissingTracking(frameIndex);
            }
            if (result == null) return null;
            try
            {
                if (_snapshot.TryGetFaceMaskData(frameIndex, out var faces))
                    UnionAutoInto(result, faces);
                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        public bool TryGetBorrowedStoredMask(int frameIndex, out WriteableBitmap mask)
        {
            mask = null!;
            if (_independentManual)
            {
                int position = FindFloorKeyframe(_keyframes, frameIndex);
                if (position < 0) return false;
                if (_compositeFrame != frameIndex)
                {
                    _compositeMask?.Dispose();
                    _compositeMask = GetFinalMask(frameIndex);
                    _compositeFrame = frameIndex;
                }
                if (_compositeMask == null) return false;
                mask = _compositeMask;
                return true;
            }

            if (!TryResolveFrame(frameIndex, out int legacyPosition,
                out ManualMaskTrackSegment? segment, out bool tracked, out bool blocked) ||
                blocked || !_keyframeHasCoverage[legacyPosition]) return false;
            int sourceKeyframe = _keyframes[legacyPosition];
            if (!tracked || segment == null)
            {
                if (!_keyframeIsStoredMask[legacyPosition]) return false;
                return _snapshot.TryGetStoredMaskBorrowed(sourceKeyframe, out mask);
            }
            WriteableBitmap? sourceRaster = GetSourceRasterMask(sourceKeyframe, legacyPosition);
            if (sourceRaster == null) return false;
            EnsureScratchMask(sourceRaster);
            if (!TransformComponentsInto(sourceRaster, _scratchMask!, segment, frameIndex))
                throw MissingTracking(frameIndex);
            mask = _scratchMask!;
            return true;
        }

        public bool TryGetFaceMaskData(int frameIndex, out FrameMaskProvider.FaceMaskData data)
        {
            data = default;
            if (_independentManual)
            {
                return FindFloorKeyframe(_keyframes, frameIndex) < 0 &&
                    _snapshot.TryGetFaceMaskData(frameIndex, out data);
            }
            if (!TryResolveFrame(frameIndex, out int position,
                out ManualMaskTrackSegment? segment, out bool tracked, out bool blocked) ||
                blocked || tracked || !_keyframeHasCoverage[position] ||
                _keyframeIsStoredMask[position]) return false;
            return _snapshot.TryGetFaceMaskData(_keyframes[position], out data);
        }

        private static InvalidOperationException MissingTracking(int frameIndex) => new(
            $"수동 블러 {frameIndex} 프레임의 추적 결과가 없습니다. " +
            "고정된 키프레임 마스크로 대신 내보내지 않습니다. 해당 프레임에서 마스크를 다시 지정하고 추적하세요.");

        private bool TryResolveFrame(int frameIndex, out int position,
            out ManualMaskTrackSegment? segment, out bool tracked, out bool blocked)
        {
            position = -1;
            segment = null;
            tracked = false;
            blocked = false;
            if (frameIndex < 0 || _keyframes.Length == 0) return false;
            position = Array.BinarySearch(_keyframes, frameIndex);
            if (position < 0) position = ~position - 1;
            if (position < 0) return false;
            int sourceKeyframe = _keyframes[position];
            if (frameIndex == sourceKeyframe) return true;
            segment = _segments.FirstOrDefault(candidate => candidate.SourceKeyframe == sourceKeyframe);
            if (segment == null || !IsSegmentCurrent(segment) ||
                frameIndex >= segment.EndExclusive || !HasAllSamples(segment, frameIndex))
                throw MissingTracking(frameIndex);
            tracked = true;
            return true;
        }

        private bool IsSegmentCurrent(ManualMaskTrackSegment segment)
        {
            if (_segmentValidity.TryGetValue(segment.SourceKeyframe, out bool cached)) return cached;
            bool valid = false;
            WriteableBitmap? source = null;
            try
            {
                source = _snapshot.TryCloneStoredMask(segment.SourceKeyframe, out WriteableBitmap stored)
                    ? stored
                    : _snapshot.GetFinalMask(segment.SourceKeyframe);
                if (source != null && !string.IsNullOrWhiteSpace(segment.SourceMaskFingerprint))
                    valid = string.Equals(ManualMaskFingerprint.Compute(source),
                        segment.SourceMaskFingerprint, StringComparison.Ordinal);
            }
            finally
            {
                source?.Dispose();
            }
            _segmentValidity[segment.SourceKeyframe] = valid;
            return valid;
        }

        private WriteableBitmap? GetSourceRasterMask(int sourceKeyframe, int position)
        {
            if (_keyframeIsStoredMask[position])
                return _snapshot.TryGetStoredMaskBorrowed(sourceKeyframe, out WriteableBitmap stored)
                    ? stored : null;
            if (_faceSourceMask != null && _faceSourceMaskKeyframe == sourceKeyframe)
                return _faceSourceMask;
            _faceSourceMask?.Dispose();
            _faceSourceMask = null;
            _faceSourceMaskKeyframe = -1;
            WriteableBitmap? created = _snapshot.GetFinalMask(sourceKeyframe);
            if (created == null) return null;
            _faceSourceMask = created;
            _faceSourceMaskKeyframe = sourceKeyframe;
            return created;
        }

        private void EnsureScratchMask(WriteableBitmap source)
        {
            if (_scratchMask != null &&
                _scratchMask.PixelSize.Width == source.PixelSize.Width &&
                _scratchMask.PixelSize.Height == source.PixelSize.Height) return;
            _scratchMask?.Dispose();
            _scratchMask = new WriteableBitmap(source.PixelSize, source.Dpi,
                Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
        }

        public void SetMask(int frameIndex, WriteableBitmap mask) =>
            throw new NotSupportedException("Export mask snapshots are read-only.");

        public void Dispose()
        {
            _scratchMask?.Dispose();
            _scratchMask = null;
            _faceSourceMask?.Dispose();
            _faceSourceMask = null;
            _faceSourceMaskKeyframe = -1;
            _compositeMask?.Dispose();
            _compositeMask = null;
            _compositeFrame = -1;
        }
    }
}