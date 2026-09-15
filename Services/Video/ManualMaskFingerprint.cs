using Avalonia.Media.Imaging;
using System;

namespace FaceShield.Services.Video;

internal static class ManualMaskFingerprint
{
    internal static string Compute(WriteableBitmap mask)
    {
        if (mask == null)
            throw new ArgumentNullException(nameof(mask));

        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offset;
        using var fb = mask.Lock();
        unsafe
        {
            byte* basePtr = (byte*)fb.Address;
            for (int y = 0; y < fb.Size.Height; y++)
            {
                byte* row = basePtr + y * fb.RowBytes;
                for (int x = 0; x < fb.Size.Width; x++)
                {
                    hash ^= row[x * 4 + 3];
                    hash *= prime;
                }
            }
        }

        return $"{fb.Size.Width}x{fb.Size.Height}:{hash:X16}";
    }
}
