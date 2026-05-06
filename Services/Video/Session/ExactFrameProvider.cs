// FILE: Services/Video/Session/ExactFrameProvider.cs
using Avalonia.Media.Imaging;
using System.Threading;
using System.Threading.Tasks;

namespace FaceShield.Services.Video.Session;

public sealed class ExactFrameProvider
{
    private readonly FfFrameExtractor _extractor;
    private readonly SemaphoreSlim _decodeGate = new(1, 1);

    public ExactFrameProvider(FfFrameExtractor extractor)
    {
        _extractor = extractor;
    }

    public async Task<WriteableBitmap?> GetExactAsync(int frameIndex, CancellationToken ct)
    {
        await _decodeGate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                return _extractor.GetFrameByIndex(frameIndex);
            }, ct);
        }
        finally
        {
            _decodeGate.Release();
        }
    }
}
