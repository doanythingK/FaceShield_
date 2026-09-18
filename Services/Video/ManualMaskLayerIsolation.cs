using Avalonia.Media.Imaging;
using System;

namespace FaceShield.Services.Video;

/// <summary>
/// Converts the editor's composite mask into the independently owned manual layer.
/// Pixels already covered at equal or greater alpha by Auto are removed from the
/// stored manual bitmap; user additions beyond Auto remain authoritative.
/// </summary>
internal static class ManualMaskLayerIsolation
{
    internal static void StripAutomaticCoverage(
        WriteableBitmap manualComposite,
        FrameMaskProvider.FaceMaskData automatic)
    {
        ArgumentNullException.ThrowIfNull(manualComposite);
        if (manualComposite.PixelSize.Width != automatic.Size.Width ||
            manualComposite.PixelSize.Height != automatic.Size.Height)
        {
            throw new InvalidOperationException(
                "Auto and manual mask dimensions do not match.");
        }

        using WriteableBitmap automaticMask =
            FrameMaskProvider.CreateMaskFromFaceRects(automatic.Size, automatic.Faces);
        using var manualBuffer = manualComposite.Lock();
        using var automaticBuffer = automaticMask.Lock();

        unsafe
        {
            byte* manualBase = (byte*)manualBuffer.Address;
            byte* automaticBase = (byte*)automaticBuffer.Address;
            for (int y = 0; y < manualBuffer.Size.Height; y++)
            {
                byte* manualRow = manualBase + y * manualBuffer.RowBytes;
                byte* automaticRow = automaticBase + y * automaticBuffer.RowBytes;
                for (int x = 0; x < manualBuffer.Size.Width; x++)
                {
                    int p = x * 4;
                    byte manualAlpha = manualRow[p + 3];
                    byte automaticAlpha = automaticRow[p + 3];
                    if (manualAlpha > automaticAlpha)
                        continue;

                    manualRow[p] = 0;
                    manualRow[p + 1] = 0;
                    manualRow[p + 2] = 0;
                    manualRow[p + 3] = 0;
                }
            }
        }
    }
}
