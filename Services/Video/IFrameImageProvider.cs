using Avalonia.Media.Imaging;
using System.Threading;
using System.Threading.Tasks;

namespace FaceShield.Services.Video
{
    public interface IFrameImageProvider
    {
        //Task<Bitmap?> GetFrameAsync(
        //    string videoPath,
        //    int frameIndex,
        //    CancellationToken ct);
        Task<WriteableBitmap> GetFrameAsync(string videoPath, int frameIndex, CancellationToken ct);
    }
}
