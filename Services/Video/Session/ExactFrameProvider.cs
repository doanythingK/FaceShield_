// FILE: Services/Video/Session/ExactFrameProvider.cs
using Avalonia.Media.Imaging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FaceShield.Services.Video.Session;

public sealed class ExactFrameProvider : IDisposable
{
    private readonly FfFrameExtractor _extractor;
    private readonly bool _ownsExtractor;
    private readonly SemaphoreSlim _decodeGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();
    private int _disposeStarted;
    private bool _disposed;

    public ExactFrameProvider(FfFrameExtractor extractor, bool ownsExtractor = true)
    {
        _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
        _ownsExtractor = ownsExtractor;
    }

    public async Task<WriteableBitmap?> GetExactAsync(int frameIndex, CancellationToken ct)
    {
        if (_disposed)
            return null;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            ct,
            _lifetimeCts.Token);
        CancellationToken token = linked.Token;

        try
        {
            await _decodeGate.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }

        try
        {
            if (_disposed || token.IsCancellationRequested)
                return null;

            var frame = await Task.Run(
                () => _extractor.GetFrameByIndex(frameIndex, token),
                token).ConfigureAwait(false);
            if (token.IsCancellationRequested)
            {
                frame?.Dispose();
                return null;
            }

            return frame;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            _decodeGate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            return;

        _disposed = true;
        _lifetimeCts.Cancel();
        _decodeGate.Wait();
        try
        {
            if (_ownsExtractor)
                _extractor.Dispose();
        }
        finally
        {
            _decodeGate.Release();
            _decodeGate.Dispose();
            _lifetimeCts.Dispose();
        }
    }
}
