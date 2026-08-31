// FILE: Services/Video/Session/ExactFrameProvider.cs
using Avalonia.Media.Imaging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FaceShield.Services.Video.Session;

public sealed class ExactFrameProvider : IDisposable
{
    private readonly FfFrameExtractor _extractor;
    private readonly SemaphoreSlim _decodeGate = new(1, 1);
    private bool _disposed;

    public ExactFrameProvider(FfFrameExtractor extractor)
    {
        _extractor = extractor;
    }

    public async Task<WriteableBitmap?> GetExactAsync(int frameIndex, CancellationToken ct)
    {
        if (_disposed)
            return null;

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
            if (_disposed || ct.IsCancellationRequested)
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

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _decodeGate.Wait();
        try
        {
            _extractor.Dispose();
        }
        finally
        {
            _decodeGate.Release();
            _decodeGate.Dispose();
        }
    }
}
