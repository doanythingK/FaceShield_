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
        internal bool Enabled;
    }

    private static readonly ConditionalWeakTable<FrameMaskProvider, TimelineState> States = new();

    internal static void Configure(FrameMaskProvider provider, bool enabled)
    {
        if (provider == null)
            throw new ArgumentNullException(nameof(provider));

        States.GetValue(provider, static _ => new TimelineState()).Enabled = enabled;
    }

    internal static bool IsEnabled(FrameMaskProvider provider)
        => provider != null &&
           States.TryGetValue(provider, out TimelineState? state) &&
           state.Enabled;

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

        int keyframe = FindFloorKeyframe(
            GetKeyframeIndices(provider),
            frameIndex);
        if (keyframe < 0)
            return false;

        if (provider.TryCloneStoredMask(
                keyframe,
                out mask,
                cancellationToken))
        {
            return true;
        }

        cancellationToken.ThrowIfCancellationRequested();
        WriteableBitmap? faceMask = provider.GetFinalMask(keyframe);
        if (faceMask == null)
            return false;

        mask = faceMask;
        return true;
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
                return new ExportMaskLease(snapshot, snapshot);

            return new ExportMaskLease(
                snapshot,
                new ManualKeyframeExportMaskProvider(snapshot, keyframes));
        }
        catch
        {
            snapshot.Dispose();
            throw;
        }
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

    internal sealed class ExportMaskLease : IDisposable
    {
        private FrameMaskProvider? _snapshot;

        internal ExportMaskLease(
            FrameMaskProvider snapshot,
            IFrameMaskProvider provider)
        {
            _snapshot = snapshot;
            Provider = provider;
        }

        internal IFrameMaskProvider Provider { get; }

        public void Dispose()
        {
            FrameMaskProvider? snapshot = Interlocked.Exchange(ref _snapshot, null);
            snapshot?.Dispose();
        }
    }

    private sealed class ManualKeyframeExportMaskProvider :
        IFrameMaskProvider,
        IExportFrameMaskReadView
    {
        private readonly FrameMaskProvider _snapshot;
        private readonly int[] _keyframes;
        private readonly bool[] _keyframeHasCoverage;
        private readonly bool[] _keyframeIsStoredMask;

        internal ManualKeyframeExportMaskProvider(
            FrameMaskProvider snapshot,
            int[] keyframes)
        {
            _snapshot = snapshot;
            _keyframes = keyframes;
            Array.Sort(_keyframes);
            _keyframeHasCoverage = new bool[_keyframes.Length];
            _keyframeIsStoredMask = new bool[_keyframes.Length];

            for (int i = 0; i < _keyframes.Length; i++)
            {
                int frameIndex = _keyframes[i];
                bool isStored = snapshot.HasStoredMask(frameIndex);
                _keyframeIsStoredMask[i] = isStored;
                if (isStored)
                {
                    _keyframeHasCoverage[i] =
                        snapshot.StoredMaskHasCoverage(frameIndex);
                }
                else
                {
                    _keyframeHasCoverage[i] =
                        snapshot.TryGetFaceMaskData(frameIndex, out var faceData) &&
                        faceData.Faces.Count > 0;
                }
            }
        }

        public WriteableBitmap? GetFinalMask(int frameIndex)
        {
            int position = ResolveKeyframePosition(frameIndex);
            if (position < 0 || !_keyframeHasCoverage[position])
                return null;

            int keyframe = _keyframes[position];
            if (_keyframeIsStoredMask[position])
            {
                return _snapshot.TryCloneStoredMask(
                    keyframe,
                    out WriteableBitmap stored)
                    ? stored
                    : null;
            }

            return _snapshot.GetFinalMask(keyframe);
        }

        public bool TryGetBorrowedStoredMask(
            int frameIndex,
            out WriteableBitmap mask)
        {
            mask = null!;
            int position = ResolveKeyframePosition(frameIndex);
            if (position < 0 ||
                !_keyframeHasCoverage[position] ||
                !_keyframeIsStoredMask[position])
            {
                return false;
            }

            return _snapshot.TryGetStoredMaskBorrowed(
                _keyframes[position],
                out mask);
        }

        public bool TryGetFaceMaskData(
            int frameIndex,
            out FrameMaskProvider.FaceMaskData data)
        {
            data = default;
            int position = ResolveKeyframePosition(frameIndex);
            if (position < 0 ||
                !_keyframeHasCoverage[position] ||
                _keyframeIsStoredMask[position])
            {
                return false;
            }

            return _snapshot.TryGetFaceMaskData(
                _keyframes[position],
                out data);
        }

        private int ResolveKeyframePosition(int frameIndex)
        {
            if (frameIndex < 0 || _keyframes.Length == 0)
                return -1;

            int position = Array.BinarySearch(_keyframes, frameIndex);
            if (position < 0)
                position = ~position - 1;
            return position;
        }

        public void SetMask(int frameIndex, WriteableBitmap mask)
            => throw new NotSupportedException(
                "Export mask snapshots are read-only.");
    }
}
