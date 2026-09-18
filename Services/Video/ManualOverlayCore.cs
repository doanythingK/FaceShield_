using System;
using System.Collections.Generic;

namespace FaceShield.Services.Video;

/// <summary>
/// A manual target owns its own explicit keyframes. Automatic face detections
/// must not be supplied to this type: detections of other faces are not a
/// boundary for a manually selected target.
/// This is a foundation for the layered workflow; it does not replace the
/// existing FrameMaskProvider or connect itself to preview/export.
/// </summary>
internal sealed class ManualOverlayTarget
{
    private readonly SortedSet<int> _keyframes = new();

    internal ManualOverlayTarget(Guid id)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("A manual target needs a stable, nonempty ID.", nameof(id));
        Id = id;
    }

    internal Guid Id { get; }

    internal bool ContainsExplicitKeyframe(int frameIndex) => _keyframes.Contains(frameIndex);

    internal void SetExplicitKeyframe(int frameIndex)
    {
        if (frameIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        _keyframes.Add(frameIndex);
    }

    internal bool RemoveExplicitKeyframe(int frameIndex) => _keyframes.Remove(frameIndex);

    /// <summary>
    /// The next *same-target* manual correction ends this tracking interval.
    /// An unknown total-frame count is deliberately not treated as an EOF.
    /// EndExclusive is never derived from a different manual target or Auto.
    /// </summary>
    internal int NextBoundaryExclusive(int sourceFrame, int totalFrames)
    {
        if (!_keyframes.Contains(sourceFrame))
            throw new InvalidOperationException("Tracking must start at this target's explicit keyframe.");

        foreach (int frame in _keyframes.GetViewBetween(sourceFrame, int.MaxValue))
        {
            if (frame > sourceFrame)
                return frame;
        }

        return totalFrames > sourceFrame ? totalFrames : int.MaxValue;
    }
}

/// <summary>
/// An independently owned BGRA8888 alpha mask (premultiplied or straight:
/// only its alpha channel is read). Callers must keep Pixels stable while
/// Compose is running and must not alias the output with a shifted input.
/// </summary>
internal readonly record struct ManualOverlayMask(byte[] Pixels, int RowBytes);

/// <summary>
/// Union of the *current frame's* Auto mask and individually verified manual
/// masks. Missing manual masks must be omitted, never replaced with a held
/// mask from an earlier frame. This class intentionally does no interpolation,
/// detection, or confidence inference.
/// </summary>
internal static class ManualOverlayMaskComposer
{
    internal static void Compose(
        byte[]? automaticMask,
        int automaticRowBytes,
        IReadOnlyList<ManualOverlayMask> verifiedManualMasks,
        byte[] destination,
        int destinationRowBytes,
        int width,
        int height)
    {
        ArgumentNullException.ThrowIfNull(verifiedManualMasks);
        ArgumentNullException.ThrowIfNull(destination);
        if (width <= 0 || height <= 0 || width > int.MaxValue / 4)
            throw new ArgumentOutOfRangeException(nameof(width));

        int minimumRowBytes = checked(width * 4);
        ValidateLayout(destination, destinationRowBytes, minimumRowBytes, height, nameof(destination));
        if (automaticMask != null)
            ValidateLayout(automaticMask, automaticRowBytes, minimumRowBytes, height, nameof(automaticMask));
        foreach (ManualOverlayMask mask in verifiedManualMasks)
            ValidateLayout(mask.Pixels, mask.RowBytes, minimumRowBytes, height, nameof(verifiedManualMasks));

        for (int y = 0; y < height; y++)
        {
            int targetRow = y * destinationRowBytes;
            int autoRow = y * automaticRowBytes;
            for (int x = 0; x < width; x++)
            {
                int pixel = x * 4;
                byte alpha = automaticMask == null ? (byte)0 : automaticMask[autoRow + pixel + 3];
                foreach (ManualOverlayMask mask in verifiedManualMasks)
                {
                    byte candidate = mask.Pixels[y * mask.RowBytes + pixel + 3];
                    if (candidate > alpha)
                        alpha = candidate;
                }

                int offset = targetRow + pixel;
                destination[offset] = alpha;
                destination[offset + 1] = alpha;
                destination[offset + 2] = alpha;
                destination[offset + 3] = alpha;
            }
        }
    }

    private static void ValidateLayout(
        byte[]? buffer,
        int rowBytes,
        int minimumRowBytes,
        int height,
        string parameter)
    {
        if (buffer == null || rowBytes < minimumRowBytes ||
            (long)rowBytes * height > buffer.Length)
            throw new ArgumentException("Invalid BGRA8888 mask dimensions or row stride.", parameter);
    }
}
