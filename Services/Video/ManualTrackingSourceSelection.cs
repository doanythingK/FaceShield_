using Avalonia.Media.Imaging;
using System;

namespace FaceShield.Services.Video;

/// <summary>
/// Selects the manual-only layer when continuing an existing manual track.
/// An Auto-only detection is never substituted for a missing manual sample.
/// Ownership of a successful result transfers to the caller.
/// </summary>
internal static class ManualTrackingSourceSelection
{
    internal static bool TryCloneContinuation(
        FrameMaskProvider provider,
        int frameIndex,
        out WriteableBitmap source)
    {
        ArgumentNullException.ThrowIfNull(provider);
        source = null!;
        // No manual keyframe means the caller must use the legacy source path.
        if (provider.GetStoredMaskFrameIndices().Length == 0)
            return false;

        // An explicit manual correction is authoritative, even when Auto is
        // present on the exact same frame.
        if (provider.TryCloneStoredMask(frameIndex, out source))
            return true;

        // Only a validated, fully sampled inherited track may become a new
        // source. Do not fall back to provider.GetFinalMask(frameIndex): on an
        // Auto-only frame that method contains *only* the unrelated Auto face.
        if (!ManualMaskKeyframeTimeline.TryCloneEffectiveKeyframeMask(
                provider, frameIndex, out WriteableBitmap inherited))
            return false;

        try
        {
            if (provider.TryGetFaceMaskData(frameIndex, out var automatic))
                ManualMaskLayerIsolation.StripAutomaticCoverage(inherited, automatic);

            if (!HasCoverage(inherited))
                return false;

            source = inherited;
            inherited = null!;
            return true;
        }
        finally
        {
            inherited?.Dispose();
        }
    }

    private static bool HasCoverage(WriteableBitmap mask)
    {
        using var buffer = mask.Lock();
        unsafe
        {
            byte* pixels = (byte*)buffer.Address;
            for (int y = 0; y < buffer.Size.Height; y++)
            {
                byte* row = pixels + y * buffer.RowBytes;
                for (int x = 0; x < buffer.Size.Width; x++)
                    if (row[x * 4 + 3] != 0)
                        return true;
            }
        }
        return false;
    }
}
