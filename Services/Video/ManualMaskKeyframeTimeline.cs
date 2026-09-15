using Avalonia.Media.Imaging;
using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace FaceShield.Services.Video;

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

        States.GetOrCreateValue(provider).Enabled = enabled;
    }

    internal static bool IsEnabled(FrameMaskProvider provider)
        => provider != null &&
           States.TryGetValue(provider, out TimelineState? state) &&
           state.Enabled;

    internal static bool TryCloneEffectiveStoredMask(
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
            provider.GetStoredMaskFrameIndices(),
            frameIndex);
        if (keyframe < 0)
            return false;

        return provider.TryCloneStoredMask(
            keyframe,
            out mask,
            cancellationToken);
    }

    internal static ExportMaskLease CreateExportMaskLease(FrameMaskProvider source)
    {
        if (source == null)
            throw new ArgumentNullException(nameof(source));

        FrameMaskProvider snapshot = source.CreateSnapshot();
        try
        {
            int[] storedKeyframes = snapshot.GetStoredMaskFrameIndices();
            if (!IsEnabled(source) || storedKeyframes.Length == 0)
                return new ExportMaskLease(snapshot, snapshot);

            return new ExportMaskLease(
                snapshot,
                new ManualKeyframeExportMaskProvider(snapshot, storedKeyframes));
        }
        catch
        {
            snapshot.Dispose();
            throw;
        }
    }

    private static int FindFloorKeyframe(int[] keyframes, int frameIndex)
    {
        int result = -1;
        for (int i = 0; i < keyframes.Length; i++)
        {
            int candidate = keyframes[i];
            if (candidate <= frameIndex && candidate > result)
                result = candidate;
        }

        return result;
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

    private sealed class ManualKeyframeExportMaskProvider : IFrameMaskProvider
    {
        private readonly FrameMaskProvider _snapshot;
        private readonly int[] _keyframes;
        private readonly bool[] _keyframeHasCoverage;

        internal ManualKeyframeExportMaskProvider(
            FrameMaskProvider snapshot,
            int[] keyframes)
        {
            _snapshot = snapshot;
            _keyframes = keyframes;
            Array.Sort(_keyframes);
            _keyframeHasCoverage = new bool[_keyframes.Length];
            for (int i = 0; i < _keyframes.Length; i++)
                _keyframeHasCoverage[i] = snapshot.StoredMaskHasCoverage(_keyframes[i]);
        }

        public WriteableBitmap? GetFinalMask(int frameIndex)
        {
            if (frameIndex < 0)
                return null;

            if (_snapshot.HasStoredMask(frameIndex))
            {
                int exactPosition = Array.BinarySearch(_keyframes, frameIndex);
                if (exactPosition < 0 || !_keyframeHasCoverage[exactPosition])
                    return null;

                return _snapshot.TryCloneStoredMask(frameIndex, out WriteableBitmap exact)
                    ? exact
                    : null;
            }

            if (_snapshot.TryGetFaceMaskData(frameIndex, out _))
                return _snapshot.GetFinalMask(frameIndex);

            int position = Array.BinarySearch(_keyframes, frameIndex);
            if (position < 0)
                position = ~position - 1;
            if (position < 0 || !_keyframeHasCoverage[position])
                return null;

            return _snapshot.TryCloneStoredMask(
                _keyframes[position],
                out WriteableBitmap inherited)
                ? inherited
                : null;
        }

        public void SetMask(int frameIndex, WriteableBitmap mask)
            => throw new NotSupportedException(
                "Export mask snapshots are read-only.");
    }
}
