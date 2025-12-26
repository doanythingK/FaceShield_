// FILE: Services/Video/Session/ExactFrameProvider.cs
using Avalonia.Media.Imaging;
using System.Threading;
using System.Threading.Tasks;

namespace FaceShield.Services.Video.Session;

public sealed class ExactFrameProvider
{
    private readonly FfFrameExtractor _extractor;

    public ExactFrameProvider(FfFrameExtractor extractor)
    {
        _extractor = extractor;
    }

    public Task<WriteableBitmap?> GetExactAsync(int frameIndex, CancellationToken ct)
    {
        return Task.Run(() => _extractor.GetFrameByIndex(frameIndex), ct);
    }
}
