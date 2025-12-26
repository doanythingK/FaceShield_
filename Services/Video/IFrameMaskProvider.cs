using Avalonia.Media.Imaging;

namespace FaceShield.Services.Video
{
    public interface IFrameMaskProvider
    {
        WriteableBitmap? GetFinalMask(int frameIndex);
    }

}

