using Avalonia.Media.Imaging;
using System.Collections.Concurrent;

namespace FaceShield.Services.Video
{
    public sealed class FrameMaskProvider : IFrameMaskProvider
    {
        private readonly ConcurrentDictionary<int, WriteableBitmap> _masks = new();

        public void SetMask(int frameIndex, WriteableBitmap mask)
            => _masks[frameIndex] = mask;

        public WriteableBitmap? GetFinalMask(int frameIndex)
            => _masks.TryGetValue(frameIndex, out var m) ? m : null;
    }
}
