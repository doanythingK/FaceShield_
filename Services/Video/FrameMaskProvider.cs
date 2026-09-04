using Avalonia;
using Avalonia.Media.Imaging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace FaceShield.Services.Video
{
    public sealed class FrameMaskProvider : IFrameMaskProvider, IDisposable
{
    private readonly object _stateGate = new();
    private readonly ConcurrentDictionary<int, WriteableBitmap> _masks = new();
    private readonly ConcurrentDictionary<int, FaceMaskData> _faceMasks = new();
    private long _version;
    private readonly bool _allowsBorrowedBitmapReads;

    public FrameMaskProvider()
    {
    }

    private FrameMaskProvider(bool allowsBorrowedBitmapReads)
    {
        _allowsBorrowedBitmapReads = allowsBorrowedBitmapReads;
    }

    internal sealed class SparseFaceMaskWorkingCopy
    {
        private readonly HashSet<int> _storedMaskFrames;
        private readonly Dictionary<int, FaceMaskData> _faceMasks;

        internal SparseFaceMaskWorkingCopy(
            long sourceVersion,
            HashSet<int> storedMaskFrames,
            Dictionary<int, FaceMaskData> faceMasks)
        {
            SourceVersion = sourceVersion;
            _storedMaskFrames = storedMaskFrames;
            _faceMasks = faceMasks;
        }

        internal long SourceVersion { get; }

        internal bool HasEntry(int frameIndex)
            => _storedMaskFrames.Contains(frameIndex) || _faceMasks.ContainsKey(frameIndex);

        internal void SetFaceRects(
            int frameIndex,
            IReadOnlyList<Rect> faces,
            PixelSize size,
            float? minConfidence = null,
            IReadOnlyList<float>? confidences = null)
        {
            if (_storedMaskFrames.Contains(frameIndex))
                return;

            Rect[] faceArray = faces == null ? Array.Empty<Rect>() : faces.ToArray();
            if (faceArray.Length == 0 || size.Width <= 0 || size.Height <= 0)
            {
                _faceMasks.Remove(frameIndex);
                return;
            }

            _faceMasks[frameIndex] = new FaceMaskData(
                size,
                faceArray,
                minConfidence,
                NormalizeConfidences(faceArray.Length, minConfidence, confidences));
        }

        internal Dictionary<int, FaceMaskData> ExportFaceMaskSnapshot(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = new Dictionary<int, FaceMaskData>(_faceMasks.Count);
            foreach (var entry in _faceMasks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                snapshot[entry.Key] = CloneFaceMaskData(entry.Value);
            }

            return snapshot;
        }
    }


    /// <summary>
    /// Stores the supplied bitmap and takes ownership of it.
    /// After this call succeeds, the caller must not mutate or dispose the bitmap.
    /// </summary>
    public void SetMask(int frameIndex, WriteableBitmap mask)
    {
        if (mask == null)
            throw new ArgumentNullException(nameof(mask));

        lock (_stateGate)
        {
            if (_masks.TryRemove(frameIndex, out var previous) &&
                !ReferenceEquals(previous, mask))
            {
                previous.Dispose();
            }

            _masks[frameIndex] = mask;
            _faceMasks.TryRemove(frameIndex, out _);
            _version++;
        }
    }

    public void SetFaceRects(
        int frameIndex,
        IReadOnlyList<Rect> faces,
        PixelSize size,
        float? minConfidence = null,
        IReadOnlyList<float>? confidences = null)
    {
        lock (_stateGate)
        {
            SetFaceRectsLocked(
                frameIndex,
                faces == null ? Array.Empty<Rect>() : faces.ToArray(),
                size,
                minConfidence,
                confidences);
        }
    }

    public void SetFaceRects(
        int frameIndex,
        Rect[] faces,
        PixelSize size,
        float? minConfidence = null,
        IReadOnlyList<float>? confidences = null)
    {
        lock (_stateGate)
        {
            SetFaceRectsLocked(
                frameIndex,
                faces == null ? Array.Empty<Rect>() : faces.ToArray(),
                size,
                minConfidence,
                confidences);
        }
    }

    private void SetFaceRectsLocked(
        int frameIndex,
        Rect[] faces,
        PixelSize size,
        float? minConfidence,
        IReadOnlyList<float>? confidences)
    {
        RemoveStoredMaskLocked(frameIndex);

        if (faces.Length == 0 || size.Width <= 0 || size.Height <= 0)
        {
            _faceMasks.TryRemove(frameIndex, out _);
            _version++;
            return;
        }

        var confArray = NormalizeConfidences(faces.Length, minConfidence, confidences);
        _faceMasks[frameIndex] = new FaceMaskData(size, faces, minConfidence, confArray);
        _version++;
    }

    public WriteableBitmap? GetFinalMask(int frameIndex)
    {
        lock (_stateGate)
        {
            if (_masks.TryGetValue(frameIndex, out var mask))
                return CloneBitmap(mask, CancellationToken.None);

            if (_faceMasks.TryGetValue(frameIndex, out var faces))
                return CreateMaskFromFaceRects(faces.Size, faces.Faces);

            return null;
        }
    }

    public bool HasEntry(int frameIndex)
    {
        lock (_stateGate)
            return _masks.ContainsKey(frameIndex) || _faceMasks.ContainsKey(frameIndex);
    }

    /// <summary>
    /// Returns a provider-owned bitmap reference for read-stable internal paths only.
    /// The caller must not dispose it and must ensure the provider is not mutated while it is in use.
    /// </summary>
    internal bool TryGetStoredMaskBorrowed(int frameIndex, out WriteableBitmap mask)
    {
        if (!_allowsBorrowedBitmapReads)
        {
            throw new InvalidOperationException(
                "Borrowed stored-mask reads are only allowed on detached provider snapshots.");
        }

        lock (_stateGate)
            return _masks.TryGetValue(frameIndex, out mask!);
    }

    internal bool HasStoredMask(int frameIndex)
    {
        lock (_stateGate)
            return _masks.ContainsKey(frameIndex);
    }

    public bool TryCloneStoredMask(
        int frameIndex,
        out WriteableBitmap mask,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_stateGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_masks.TryGetValue(frameIndex, out var stored))
            {
                mask = null!;
                return false;
            }

            mask = CloneBitmap(stored, cancellationToken);
            return true;
        }
    }

    public bool TryGetFaceMaskData(int frameIndex, out FaceMaskData data)
    {
        lock (_stateGate)
            return _faceMasks.TryGetValue(frameIndex, out data);
    }

    public void RemoveFaceMask(int frameIndex)
    {
        lock (_stateGate)
        {
            if (_faceMasks.TryRemove(frameIndex, out _))
                _version++;
        }
    }

    public int RemoveFaceMasksFrom(int startFrameIndex)
    {
        lock (_stateGate)
        {
            int removed = 0;
            foreach (int frameIndex in _faceMasks.Keys.ToArray())
            {
                if (frameIndex < startFrameIndex)
                    continue;

                if (_faceMasks.TryRemove(frameIndex, out _))
                    removed++;
            }

            if (removed > 0)
                _version++;
            return removed;
        }
    }

    public int RemoveFaceMasksRange(int startFrameIndex, int endExclusive)
    {
        lock (_stateGate)
        {
            int start = Math.Max(0, startFrameIndex);
            int end = Math.Max(start, endExclusive);
            int removedFaceMasks = 0;
            int removedStoredMasks = 0;
            for (int frameIndex = start; frameIndex < end; frameIndex++)
            {
                if (_faceMasks.TryRemove(frameIndex, out _))
                    removedFaceMasks++;
                if (_masks.TryRemove(frameIndex, out var storedMask))
                {
                    storedMask.Dispose();
                    removedStoredMasks++;
                }
            }

            int removed = removedFaceMasks + removedStoredMasks;
            if (removed > 0)
                _version++;
            return removed;
        }
    }

    public void ClearFaceMasks()
    {
        lock (_stateGate)
        {
            if (_faceMasks.IsEmpty)
                return;

            _faceMasks.Clear();
            _version++;
        }
    }

    public bool HasAnyMaskEntries()
    {
        lock (_stateGate)
            return !_masks.IsEmpty || !_faceMasks.IsEmpty;
    }

    public int[] GetStoredMaskFrameIndices()
    {
        lock (_stateGate)
            return _masks.Keys.ToArray();
    }

    public int[] GetFaceMaskFrameIndices()
    {
        lock (_stateGate)
            return _faceMasks.Keys.ToArray();
    }

    public IReadOnlyCollection<KeyValuePair<int, FaceMaskData>> GetFaceMaskEntries()
    {
        lock (_stateGate)
            return _faceMasks.ToArray();
    }

    public IReadOnlyCollection<KeyValuePair<int, WriteableBitmap>> GetStoredMaskSnapshot(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_stateGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = new List<KeyValuePair<int, WriteableBitmap>>(_masks.Count);
            try
            {
                foreach (var entry in _masks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    snapshot.Add(new KeyValuePair<int, WriteableBitmap>(
                        entry.Key,
                        CloneBitmap(entry.Value, cancellationToken)));
                }

                return snapshot;
            }
            catch
            {
                foreach (var entry in snapshot)
                    entry.Value.Dispose();
                throw;
            }
        }
    }

    internal SparseFaceMaskWorkingCopy CreateSparseFaceMaskWorkingCopy(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_stateGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long sourceVersion = _version;
            var storedMaskFrames = new HashSet<int>(_masks.Keys);
            var faceMasks = new Dictionary<int, FaceMaskData>(_faceMasks.Count);
            foreach (var entry in _faceMasks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                faceMasks[entry.Key] = CloneFaceMaskData(entry.Value);
            }

            return new SparseFaceMaskWorkingCopy(
                sourceVersion,
                storedMaskFrames,
                faceMasks);
        }
    }

    public FrameMaskProvider CreateSnapshot()
        => CreateSnapshot(out _, CancellationToken.None);

    public FrameMaskProvider CreateSnapshot(out long sourceVersion)
        => CreateSnapshot(out sourceVersion, CancellationToken.None);

    public FrameMaskProvider CreateSnapshot(
        out long sourceVersion,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_stateGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sourceVersion = _version;
            var snapshot = new FrameMaskProvider(allowsBorrowedBitmapReads: true);
            try
            {
                foreach (var entry in _masks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    snapshot._masks[entry.Key] = CloneBitmap(
                        entry.Value,
                        cancellationToken);
                }

                foreach (var entry in _faceMasks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    snapshot._faceMasks[entry.Key] = CloneFaceMaskData(entry.Value);
                }

                return snapshot;
            }
            catch
            {
                snapshot.Dispose();
                throw;
            }
        }
    }

    internal void CommitFaceMasksFrom(
        SparseFaceMaskWorkingCopy source,
        CancellationToken cancellationToken = default)
    {
        if (source == null)
            throw new ArgumentNullException(nameof(source));

        var committedFaces = source.ExportFaceMaskSnapshot(cancellationToken);

        lock (_stateGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_version != source.SourceVersion)
            {
                throw new InvalidOperationException(
                    "Mask provider changed while sparse Auto materialization was staged.");
            }

            _faceMasks.Clear();
            foreach (var entry in committedFaces)
            {
                // Stored/manual bitmap entries remain authoritative.
                if (_masks.ContainsKey(entry.Key))
                    continue;

                _faceMasks[entry.Key] = entry.Value;
            }

            // Once commit mutation starts, finish atomically rather than observing cancellation mid-commit.
            _version++;
        }
    }

    public void CommitFaceMasksFrom(
        FrameMaskProvider source,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        if (source == null)
            throw new ArgumentNullException(nameof(source));
        if (ReferenceEquals(this, source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        var committedFaces = source.ExportFaceMaskSnapshot(cancellationToken);

        lock (_stateGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_version != expectedVersion)
            {
                throw new InvalidOperationException(
                    "Mask provider changed while Auto post-processing was staged.");
            }

            _faceMasks.Clear();
            foreach (var entry in committedFaces)
            {
                // A manually stored bitmap remains authoritative if an unexpected
                // working-copy mutation attempted to replace it with face rectangles.
                if (_masks.ContainsKey(entry.Key))
                    continue;

                _faceMasks[entry.Key] = entry.Value;
            }

            _version++;
        }
    }

    private Dictionary<int, FaceMaskData> ExportFaceMaskSnapshot(
        CancellationToken cancellationToken)
    {
        lock (_stateGate)
        {
            var snapshot = new Dictionary<int, FaceMaskData>(_faceMasks.Count);
            foreach (var entry in _faceMasks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                snapshot[entry.Key] = CloneFaceMaskData(entry.Value);
            }

            return snapshot;
        }
    }

    public void Clear()
    {
        lock (_stateGate)
        {
            foreach (var entry in _masks.ToArray())
            {
                if (_masks.TryRemove(entry.Key, out var mask))
                    mask.Dispose();
            }

            _faceMasks.Clear();
            _version++;
        }
    }

    public void Dispose()
    {
        Clear();
    }

    private static WriteableBitmap CloneBitmap(
        WriteableBitmap source,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var copy = new WriteableBitmap(
            source.PixelSize,
            source.Dpi,
            Avalonia.Platform.PixelFormat.Bgra8888,
            Avalonia.Platform.AlphaFormat.Premul);

        try
        {
            using var sourceBuffer = source.Lock();
            using var copyBuffer = copy.Lock();
            int rows = Math.Min(sourceBuffer.Size.Height, copyBuffer.Size.Height);
            int bytesPerRow = Math.Min(sourceBuffer.RowBytes, copyBuffer.RowBytes);

            unsafe
            {
                byte* src = (byte*)sourceBuffer.Address;
                byte* dst = (byte*)copyBuffer.Address;
                for (int y = 0; y < rows; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Buffer.MemoryCopy(
                        src + y * sourceBuffer.RowBytes,
                        dst + y * copyBuffer.RowBytes,
                        copyBuffer.RowBytes,
                        bytesPerRow);
                }
            }

            return copy;
        }
        catch
        {
            copy.Dispose();
            throw;
        }
    }

    private void RemoveStoredMaskLocked(int frameIndex)
    {
        if (_masks.TryRemove(frameIndex, out var mask))
            mask.Dispose();
    }

    private static FaceMaskData CloneFaceMaskData(FaceMaskData data)
        => new(
            data.Size,
            data.Faces.ToArray(),
            data.MinConfidence,
            data.Confidences.ToArray());

    public readonly record struct FaceMaskData
    {
        public FaceMaskData(
            PixelSize size,
            IReadOnlyList<Rect> faces,
            float? minConfidence,
            IReadOnlyList<float> confidences)
        {
            Size = size;
            Faces = Array.AsReadOnly(
                faces == null ? Array.Empty<Rect>() : faces.ToArray());
            MinConfidence = minConfidence;
            Confidences = Array.AsReadOnly(
                confidences == null ? Array.Empty<float>() : confidences.ToArray());
        }

        public PixelSize Size { get; }
        public IReadOnlyList<Rect> Faces { get; }
        public float? MinConfidence { get; }
        public IReadOnlyList<float> Confidences { get; }
    }

    private static IReadOnlyList<float> NormalizeConfidences(
        int faceCount,
        float? minConfidence,
        IReadOnlyList<float>? confidences)
    {
        if (faceCount <= 0)
            return Array.Empty<float>();

        if (confidences != null && confidences.Count == faceCount)
            return confidences.ToArray();

        float fill = minConfidence ?? 1.0f;
        var fallback = new float[faceCount];
        for (int i = 0; i < faceCount; i++)
            fallback[i] = fill;
        return fallback;
    }

    public static WriteableBitmap CreateMaskFromFaceRects(PixelSize size, IReadOnlyList<Rect> faces)
    {
        var mask = new WriteableBitmap(
            size,
            new Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888,
            Avalonia.Platform.AlphaFormat.Premul);

        RenderMaskFromFaceRects(mask, size, faces);
        return mask;
    }

    public static void RenderMaskFromFaceRects(WriteableBitmap mask, PixelSize size, IReadOnlyList<Rect> faces)
    {
        if (mask == null)
            throw new ArgumentNullException(nameof(mask));
        if (size.Width <= 0 || size.Height <= 0)
            return;

        if (mask.PixelSize.Width != size.Width || mask.PixelSize.Height != size.Height)
            throw new ArgumentException("Mask bitmap size does not match target size.", nameof(mask));

        using var fb = mask.Lock();

        unsafe
        {
            byte* basePtr = (byte*)fb.Address;
            int stride = fb.RowBytes;
            int w = size.Width;
            int h = size.Height;

            for (int y = 0; y < h; y++)
            {
                var row = new Span<byte>(basePtr + y * stride, w * 4);
                row.Clear();
            }

            foreach (var r in faces)
            {
                Rect padded = FaceBlurGeometry.GetPaddedRect(r, w, h);
                int x0 = Math.Clamp((int)Math.Floor(padded.X), 0, Math.Max(0, w - 1));
                int y0 = Math.Clamp((int)Math.Floor(padded.Y), 0, Math.Max(0, h - 1));
                int x1 = Math.Clamp((int)Math.Ceiling(padded.Right), 0, w);
                int y1 = Math.Clamp((int)Math.Ceiling(padded.Bottom), 0, h);

                double strength = 1.0;

                double cx = (x0 + x1 - 1) / 2.0;
                double cy = (y0 + y1 - 1) / 2.0;
                double rx = Math.Max(1.0, (x1 - x0) / 2.0);
                double ry = Math.Max(1.0, (y1 - y0) / 2.0);
                double rx2 = rx * rx;
                double ry2 = ry * ry;
                double inner = 1.0 - FaceBlurGeometry.SoftEdgeRatio;
                if (inner < 0.0)
                    inner = 0.0;
                double inner2 = inner * inner;
                bool softEdge = FaceBlurGeometry.SoftEdgeRatio > 0.0 && inner2 < 0.999;

                for (int y = y0; y < y1; y++)
                {
                    byte* row = basePtr + y * stride;
                    double dy = y - cy;
                    double dy2 = dy * dy;
                    for (int x = x0; x < x1; x++)
                    {
                        double dx = x - cx;
                        double d2 = (dx * dx) / rx2 + dy2 / ry2;
                        if (d2 > 1.0)
                            continue;

                        byte* p = row + x * 4;
                        byte alpha;
                        if (!softEdge || d2 <= inner2)
                        {
                            alpha = 255;
                        }
                        else
                        {
                            double t = (d2 - inner2) / (1.0 - inner2);
                            if (t < 0.0) t = 0.0;
                            if (t > 1.0) t = 1.0;
                            t = t * t * (3.0 - 2.0 * t);
                            alpha = (byte)Math.Round((1.0 - t) * 255.0);
                        }

                        int scaled = (int)Math.Round(alpha * strength);
                        if (scaled <= p[3])
                            continue;

                        byte outA = (byte)Math.Clamp(scaled, 0, 255);
                        p[0] = outA;
                        p[1] = outA;
                        p[2] = outA;
                        p[3] = outA;
                    }
                }
            }
        }
    }
}
}
