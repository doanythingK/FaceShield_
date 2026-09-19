using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System;

namespace FaceShield.Services.Video;

/// <summary>
/// The manual editor must never use an Auto/manual union as its writable mask:
/// an opaque Auto pixel cannot reveal whether the user painted manual coverage
/// underneath it. Only the preview composes the two independent layers.
/// </summary>
internal static class ManualMaskEditorLayer
{
    internal static WriteableBitmap CreateEditableMask(
        FrameMaskProvider provider, int frameIndex, PixelSize size)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (size.Width <= 0 || size.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(size));

        if (provider.TryCloneStoredMask(frameIndex, out WriteableBitmap stored))
        {
            if (stored.PixelSize == size)
                return stored;
            stored.Dispose();
            throw new InvalidOperationException("Stored manual mask dimensions do not match the video frame.");
        }

        if (ManualMaskKeyframeTimeline.TryCloneEffectiveManualMask(
                provider, frameIndex, out WriteableBitmap inherited))
        {
            if (inherited.PixelSize == size)
                return inherited;
            inherited.Dispose();
            throw new InvalidOperationException("Tracked manual mask dimensions do not match the video frame.");
        }

        return new WriteableBitmap(size, new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
    }

    /// <summary>
    /// Returns an independently owned preview-only union. Neither the stored
    /// manual bitmap nor the automatic rectangles are modified. The caller owns
    /// the returned bitmap, including when an exception occurs during copying.
    /// </summary>
    internal static WriteableBitmap ComposeWithAutomatic(
        WriteableBitmap manual, FrameMaskProvider.FaceMaskData automatic)
    {
        ArgumentNullException.ThrowIfNull(manual);
        if (manual.PixelSize != automatic.Size)
            throw new InvalidOperationException("Auto and manual mask dimensions do not match.");

        var composite = new WriteableBitmap(manual.PixelSize, new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
        try
        {
            using var autoMask = FrameMaskProvider.CreateMaskFromFaceRects(
                automatic.Size, automatic.Faces);
            using var source = manual.Lock();
            using var automaticFrame = autoMask.Lock();
            using var destination = composite.Lock();
            unsafe
            {
                byte* manualBase = (byte*)source.Address;
                byte* autoBase = (byte*)automaticFrame.Address;
                byte* outputBase = (byte*)destination.Address;
                for (int y = 0; y < manual.PixelSize.Height; y++)
                {
                    byte* manualRow = manualBase + y * source.RowBytes;
                    byte* autoRow = autoBase + y * automaticFrame.RowBytes;
                    byte* outputRow = outputBase + y * destination.RowBytes;
                    for (int x = 0; x < manual.PixelSize.Width; x++)
                    {
                        int offset = x * 4;
                        byte alpha = Math.Max(manualRow[offset + 3], autoRow[offset + 3]);
                        outputRow[offset] = alpha;
                        outputRow[offset + 1] = alpha;
                        outputRow[offset + 2] = alpha;
                        outputRow[offset + 3] = alpha;
                    }
                }
            }
            return composite;
        }
        catch
        {
            composite.Dispose();
            throw;
        }
    }
}
