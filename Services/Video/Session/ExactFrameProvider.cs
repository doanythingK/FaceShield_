// FILE: Services/Video/Session/ExactFrameProvider.cs
using Avalonia.Media.Imaging;
using System.Threading;
using System.Threading.Tasks;

namespace FaceShield.Services.Video.Session;

public sealed class ExactFrameProvider : IDisposable
{
    private readonly FfFrameExtractor _extractor;
    private bool _disposed;

    public ExactFrameProvider(FfFrameExtractor extractor)
    {
        _extractor = extractor;
    }

    public Task<WriteableBitmap?> GetExactAsync(int frameIndex, CancellationToken ct)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ExactFrameProvider));
        return Task.Run(() => _extractor.GetFrameByIndex(frameIndex), ct);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _extractor.Dispose();
    }
}
