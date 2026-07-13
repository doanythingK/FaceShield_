// FILE: Services/Video/Session/ExactFrameProvider.cs
using Avalonia.Media.Imaging;
using System;
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
        try
        {
            await _decodeGate.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        try
        {
            if (ct.IsCancellationRequested)
                return null;

            var frame = await Task.Run(() => _extractor.GetFrameByIndex(frameIndex, ct));
            if (ct.IsCancellationRequested)
            {
                frame?.Dispose();
                return null;
            }

            return frame;
        }
        finally
        {
            _decodeGate.Release();
        }
    }
}
