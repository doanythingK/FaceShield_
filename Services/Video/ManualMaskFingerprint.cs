using Avalonia.Media.Imaging;
using System;
using System.Threading;

namespace FaceShield.Services.Video;

internal static class ManualMaskFingerprint
{
    internal static string Compute(
        WriteableBitmap mask,
        CancellationToken cancellationToken = default)
    {
        if (mask == null)
            throw new ArgumentNullException(nameof(mask));

        cancellationToken.ThrowIfCancellationRequested();
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offset;
        using var fb = mask.Lock();
        unsafe
        {
            byte* basePtr = (byte*)fb.Address;
            for (int y = 0; y < fb.Size.Height; y++)
            {
                if ((y & 15) == 0)
                    cancellationToken.ThrowIfCancellationRequested();

                byte* row = basePtr + y * fb.RowBytes;
                for (int x = 0; x < fb.Size.Width; x++)
                {
                    hash ^= row[x * 4 + 3];
                    hash *= prime;
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return $"{fb.Size.Width}x{fb.Size.Height}:{hash:X16}";
    }
}
